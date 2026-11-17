using System.Text.Json;
using ForgeAudio;
using ForgeSkin.Format;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

enum SkinState { Unloaded, Loading, Ready }

// One installed pack. At a reload only its manifest is read and checked; its textures, meshes and sounds are read the first
// time a gear wants the skin (Request) and freed once no gear has used it for a while (Unload). A worker thread reads the
// zip and the meshes; the textures are uploaded (precompressed by the editor) or decoded on the main thread through
// ForgeCore's image queue, a few milliseconds a frame. The zip is open only while it is read, so the backend can overwrite it while the game runs.
sealed class Skin
{
    public readonly SkinDocument Doc;
    public readonly DateTime Stamp;
    public readonly string File;
    public SkinState State { get; private set; }
    public bool Retired; // no longer installed (removed or rebuilt): freed as soon as no gear shows it
    public float LastUsed;
    public int ReadFailures; // reads that failed with an I/O error (a locked file): retried a few times, then the pack is rejected
    readonly Dictionary<string, Texture2D> _textures = new();
    readonly Dictionary<string, Mesh> _meshes = new();
    readonly Dictionary<string, string> _cues = new(StringComparer.OrdinalIgnoreCase); // sound file -> cue of _sounds
    AudioLibrary? _sounds;
    Task<Contents>? _reading;
    readonly List<ForgeCore.ImageJob> _jobs = new();
    int _decoding, _loads;
    readonly System.Diagnostics.Stopwatch _watch = new();
    // The coloured copies by owner (0: the local player, else a teammate's SNet lookup): the copy and the colour version it was made at.
    readonly Dictionary<ulong, (int Version, SkinDocument Doc)> _painted = new();
    // The skin number a teammate's look names it by, and the Thunderstore mod ("author-name") its pack came in ("" otherwise).
    public readonly uint Number;
    public string Pack = "";
    Task<ulong>? _hashing;

    Skin(SkinDocument doc, string file, DateTime stamp) { Doc = doc; File = file; Stamp = stamp; Number = Look.SkinNumber(doc.Id); }

    // The pack file's FNV-1a 64, which a look names the pack's content by: read on a worker the first time it is asked for (only for a
    // skin the local player wears or a teammate's look names), then kept with this skin (a pack changed on disk is a new Skin). Null
    // until it is known; Skins.Hashed says when one arrived. A read that failed (the file locked) is tried again on the next ask.
    public ulong? ContentHash()
    {
        if (_hashing is { IsFaulted: true }) _hashing = null;
        _hashing ??= Task.Run(() =>
        {
            var hash = ForgeCore.Fnv.Offset64;
            var buffer = new byte[1 << 16];
            using (var stream = System.IO.File.OpenRead(File))
                for (int n; (n = stream.Read(buffer, 0, buffer.Length)) > 0;)
                    for (int i = 0; i < n; i++) hash = ForgeCore.Fnv.Mix64(hash, buffer[i]);
            Skins.NoteHashed();
            return hash;
        });
        return _hashing.IsCompletedSuccessfully ? _hashing.Result : null;
    }

    public Texture2D Texture(string path, bool linear) => _textures[(linear ? "L:" : "S:") + path];
    public Mesh Mesh(string path) => _meshes[path];
    public AudioCue Sound(string path) => _sounds!.Cue(_cues[path]);

    public static bool Linear(TextureChannel channel) => Dds.Linear(channel);

    // The manifest only, checked as the pack format requires (every referenced file present and of its kind).
    public static Skin Read(string file, DateTime stamp)
    {
        using var pack = SkinPack.Open(file);
        return new Skin(pack.Manifest, file, stamp);
    }

    // What the worker read out of the zip.
    sealed class Contents
    {
        // Packed: the texture the editor compressed when it built the pack (Bytes is then not read).
        public readonly List<(string Path, TextureChannel Channel, bool Compress, byte[]? Bytes, Dds? Packed)> Textures = new();
        public readonly Dictionary<string, ForgeSkinMesh> Meshes = new();
        public readonly Dictionary<string, byte[]> Sounds = new(StringComparer.OrdinalIgnoreCase);
        // A character skin's model, its files checked against its descriptor and its skeleton against the bone map.
        public ImportedModelDescriptor? Model;
        public byte[] Mesh = Array.Empty<byte>();
        public readonly List<byte[]> ModelTextures = new();
    }

    // A character skin's model once built (ImportedModels format 2), and the pose links of its bone map.
    public ImportedModel? Character { get; private set; }
    public PoseLink[]? Links { get; private set; }
    ImportedModels? _models;
    Contents? _model; // read, waiting for the game's gear shader to build it

    // The pack changed on disk since its manifest was read: the reload reads it again.
    public sealed class StaleException : IOException
    {
        public StaleException(string file) : base($"{Path.GetFileName(file)} changed since it was read") { }
    }

    public void Request(float now)
    {
        LastUsed = now;
        if (State != SkinState.Unloaded) return;
        State = SkinState.Loading;
        _watch.Restart();
        var (file, stamp, doc) = (File, Stamp, Doc);
        _reading = Task.Run(() => ReadContents(file, stamp, doc));
        ForgeSkinPlugin.Info($"loading skin {Doc.Id} from {Path.GetFileName(File)}");
    }

    // Worker thread: no Unity call.
    static Contents ReadContents(string file, DateTime stamp, SkinDocument doc)
    {
        if (System.IO.File.GetLastWriteTimeUtc(file) != stamp) throw new StaleException(file);
        using var pack = SkinPack.Open(file);
        if (System.IO.File.GetLastWriteTimeUtc(file) != stamp) throw new StaleException(file);
        var c = new Contents();
        var seen = new HashSet<string>();
        // A texture set straight on a material (`compress`) is compressed to DXT, a quarter of the VRAM of raw RGBA; a
        // normal map is stored the way the game's own are on its gear shader (DXT5nm). Layer atlases stay raw: layers copy
        // frames out of them with CopyTexture, which needs the format of the uncompressed render target. The editor names those
        // already compressed (Dds), uploaded as they are; a PNG or JPEG (a pack made by hand) is decoded.
        void Texture(string path, TextureChannel channel, bool compress)
        {
            if (!seen.Add((Linear(channel) ? "L:" : "S:") + path)) return;
            var bytes = pack.ReadBytes(path);
            var packed = compress && Dds.IsDds(bytes) ? Dds.Read(bytes, channel, path) : null;
            c.Textures.Add((path, channel, compress, packed == null ? bytes : null, packed));
        }
        void Mesh(string? path)
        {
            if (path != null && !c.Meshes.ContainsKey(path)) c.Meshes[path] = ForgeSkinMesh.Read(pack.ReadBytes(path));
        }
        foreach (var m in doc.Materials)
        {
            foreach (var (channel, path) in m.Textures) Texture(path, channel, true);
            foreach (var stage in m.Wear ?? []) foreach (var (channel, path) in stage.Textures) Texture(path, channel, true);
            foreach (var l in m.Layers) Texture(l.Atlas!.Path, l.Channel, false);
        }
        foreach (var a in doc.Attachments)
        {
            Mesh(a.Mesh);
            if (a.Material != null) foreach (var (channel, path) in a.Material.Textures) Texture(path, channel, true);
            foreach (var g in (a.Screen?.GlyphMeshes ?? Array.Empty<string?>()).Append(a.Screen?.TextMesh).Append(a.Screen?.BorderMesh)) Mesh(g);
        }
        foreach (var t in doc.Parts) Mesh(t.Mesh);
        foreach (var s in doc.Sounds)
            if (!c.Sounds.ContainsKey(s.File)) c.Sounds[s.File] = pack.ReadBytes(s.File);
        if (doc.Character is { } person)
        {
            var model = ImportedModelDescriptor.Parse(System.Text.Encoding.UTF8.GetString(pack.ReadBytes(person.Model.Descriptor)), out var failure)
                ?? throw new SkinFormatException("bad-character-model", $"{person.Model.Descriptor}: {failure}");
            c.Mesh = pack.ReadBytes(person.Model.Mesh);
            foreach (var t in person.Model.Textures) c.ModelTextures.Add(pack.ReadBytes(t));
            if (CharacterRig.Matches(model, c.Mesh, c.ModelTextures) is { } bad) throw new SkinFormatException("bad-character-model", bad);
            if (CharacterRig.Check(person, model) is { Length: > 0 } wrong) throw new SkinFormatException("bad-character-bones", wrong);
            c.Model = model;
        }
        return c;
    }

    // Main thread, every frame while loading. Null while not done; the exception that failed the load; or none when ready.
    public Exception? Pump(out bool finished)
    {
        finished = false;
        if (State != SkinState.Loading) return null;
        if (_reading != null)
        {
            if (!_reading.IsCompleted) return null;
            var reading = _reading;
            _reading = null;
            if (reading.IsFaulted) return Abort(reading.Exception!.InnerException!, out finished);
            var c = reading.Result;
            try
            {
                foreach (var (path, m) in c.Meshes) _meshes[path] = ToMesh(m, path);
                if (Doc.Sounds.Count > 0) _sounds = SkinSounds.Load(Doc.Sounds, file => c.Sounds[file], _cues);
            }
            catch (Exception e) { return Abort(e, out finished); }
            if (c.Model != null) _model = c;
            _decoding = c.Textures.Count;
            int load = ++_loads;
            foreach (var (path, channel, compress, bytes, packed) in c.Textures)
            {
                var (p, ch, comp) = (path, channel, compress);
                Action<Texture2D?, string> done = (texture, failure) => Decoded(load, p, ch, comp, texture, failure);
                _jobs.Add(packed != null
                    ? ForgeCore.Images.QueueRaw(packed.Data, Dds.HeaderSize, packed.Width, packed.Height, packed.Alpha ? TextureFormat.DXT5 : TextureFormat.DXT1, true, !packed.Srgb, done)
                    : ForgeCore.Images.Queue(bytes!, done, Linear(channel), mipChain: compress, normalMap: channel == TextureChannel.Normal && compress, compress: compress, maxSide: Dds.MaxSide));
            }
        }
        if (_failure is { } failed) { _failure = null; return Abort(failed, out finished); }
        if (_decoding > 0) return null;
        if (_model != null)
        {
            if (BuildCharacter() is { } bad) return Abort(bad, out finished);
            if (_model != null) return null;
        }
        _jobs.Clear();
        State = SkinState.Ready;
        finished = true;
        ForgeSkinPlugin.Info($"loaded skin {Doc.Id}: {(Doc.Character != null ? "its character model, " : "")}{_textures.Count} gear textures, {_meshes.Count} gear meshes, {_cues.Count} sounds in {_watch.ElapsedMilliseconds} ms");
        return null;
    }

    Exception? _failure;

    // The character model from what the worker read, once per load: the PNGs decode in this frame (ForgeCore ImportedModels). Without the
    // game's gear shader yet (before the first gear is built) it waits (_model stays) and is tried again next frame. The failure, or null.
    Exception? BuildCharacter()
    {
        // Its PNGs decode over the frames before (ForgeCore's image queue and frame budget), not all in the frame that builds it.
        if (!(_models ??= new ImportedModels(keep: true)).Prepare(_model!.Model!, _model.ModelTextures)) return null;
        var decoded = _watch.ElapsedMilliseconds;
        var build = System.Diagnostics.Stopwatch.StartNew();
        var result = _models.Create(_model.Model!, _model.Mesh, _model.ModelTextures);
        if (result.Failure.Code == ImportedModelCodes.ShaderUnavailable) return null;
        ForgeSkinPlugin.Info($"character model of {Doc.Id}: pictures decoded {decoded} ms after the load began, built in {build.ElapsedMilliseconds} ms in one frame");
        if (result.Model is not { Skinned: true } built)
            return new SkinFormatException(result.Failure.Code == ImportedModelCodes.CutoutUnsupported ? "unsupported-character-material" : "bad-character-model", result.Failure.ToString());
        try { Links = CharacterRig.Links(Doc.Character!, _model.Model!); }
        catch (ArgumentException e) { return new SkinFormatException("bad-character-bones", e.Message); }
        // Never silent: the joints this model has no bone for, once per load.
        if (HumanRig.Unposed(Links) is { Length: > 0 } unposed)
            ForgeSkinPlugin.Fail("skin-character-part-skipped", $"character {Doc.Id} has no bone for {string.Join(", ", unposed)}: those parts keep their rest pose and move with the bone above them");
        Character = built;
        _model = null;
        return null;
    }

    void Decoded(int load, string path, TextureChannel channel, bool compress, Texture2D? texture, string failure)
    {
        if (load != _loads || State != SkinState.Loading)
        {
            if (texture != null) Object.Destroy(texture);
            return;
        }
        _decoding--;
        if (texture == null) { _failure ??= new SkinFormatException("bad-image", $"{path}: {failure}"); return; }
        texture.name = "ForgeSkin " + path;
        _textures[(Linear(channel) ? "L:" : "S:") + path] = ForgeCore.Assets.Keep(texture);
        if (compress && (texture.width % 4 != 0 || texture.height % 4 != 0))
            ForgeSkinPlugin.Warn($"{path}: {texture.width}x{texture.height} is not a multiple of 4, left uncompressed");
        ForgeSkinPlugin.Info($"{path} ({channel}): {texture.width}x{texture.height} {texture.format}");
    }

    Exception Abort(Exception e, out bool finished)
    {
        finished = true;
        Unload();
        return e;
    }

    // A .forgeskinmesh (SFM1) read on the worker, built by ForgeCore's one mesh entry.
    static Mesh ToMesh(ForgeSkinMesh m, string name)
    {
        var mesh = ForgeCore.Assets.Keep(MeshArrays.Build("ForgeSkin " + name, m.Positions, m.Normals, m.Uv0, m.Tangents, Array.ConvertAll(m.Indices, i => (int)i)));
        mesh.UploadMeshData(true);
        return mesh;
    }

    // The document as `owner` coloured it (`picks`: colour id -> sRGB): a painted copy, or the pack's own when they changed nothing.
    // One copy per owner and colour change (`version`), not one per gear and apply.
    public SkinDocument Painted(ulong owner, IReadOnlyDictionary<string, float[]>? picks, int version)
    {
        if (picks == null || Doc.PlayerColors is not { Count: > 0 } || !Doc.PlayerColors.Exists(c => picks.ContainsKey(c.Id))) return Doc;
        if (_painted.TryGetValue(owner, out var made) && made.Version == version) return made.Doc;
        var copy = SkinJson.Read(SkinJson.Write(Doc));
        foreach (var c in copy.PlayerColors!)
            if (picks.TryGetValue(c.Id, out var rgb)) c.Paint(copy, rgb);
        _painted[owner] = (version, copy);
        return copy;
    }

    // A teammate who left: their coloured copy goes.
    public void Forget(ulong owner) => _painted.Remove(owner);

    // Frees what the skin loaded and drops a load in progress (a worker still reading is left to finish and ignored).
    public void Unload()
    {
        foreach (var job in _jobs) job.Cancel();
        _jobs.Clear();
        _reading = null;
        _loads++;
        _decoding = 0;
        _failure = null;
        foreach (var t in _textures.Values) Object.Destroy(t);
        foreach (var m in _meshes.Values) Object.Destroy(m);
        _sounds?.Dispose();
        _models?.Dispose(); // the character model and every instance of it
        _models = null;
        _model = null;
        Character = null;
        Links = null;
        _textures.Clear();
        _meshes.Clear();
        _cues.Clear();
        _sounds = null;
        State = SkinState.Unloaded;
    }
}

// Installed packs, their manifests read on the first tick (each skin's files when a gear first wears it). Three sources:
// - the skins folder BepInEx/ForgeSkin/skins (SkinFolders.Skins, where the editor installs): every pack in it, selection from its active.json;
// - skin mods: a mod folder directly under BepInEx/plugins with a `forgeskin.forge` marker lists its packs, one path per
//   line relative to that folder (blank lines and `#` comments skipped). Only the marker is looked for, one level deep;
// - code: other plugins call `SkinPacks.Register` with a pack path from their Load.
// A mod's or registered skin is active for its target unless active.json picks a skin for that target; among them the
// first by path wins; the player's choice in the in-game picker (ForgeSkin.choices.json) goes before both. A change in the skins folder reads the packs it names again,
// in a running game; a skin mod changed in the plugins folder is read in development mode, else at the next launch.
public static class SkinPacks
{
    public const string Marker = SkinFolders.ModMarker;

    /// <summary>Adds a skin pack file to ForgeSkin; call from your plugin's Load. Its manifest is read with the others on the first frame, its files when a gear first wears it.</summary>
    public static void Register(string pack) { lock (Skins.Registered) Skins.Registered.Add(Path.GetFullPath(pack)); }
}

static class Skins
{
    public static readonly Dictionary<string, Skin> ById = new();
    public static readonly Dictionary<string, string> Targets = new(); // SkinTarget.Key() -> skin id
    public static readonly List<string> Globals = new();
    public static readonly Dictionary<string, List<Skin>> Candidates = new(); // SkinTarget.Key() -> every loaded skin for it, by path

    static Dictionary<string, string> _base = new(); // Targets before the player's choices: active.json, then the first by path
    static readonly Dictionary<string, string> Choices = new(); // the picker's, "" = vanilla; kept for skins not loaded
    // The player's colours: skin id -> player colour id -> sRGB; kept for skins not loaded.
    static readonly Dictionary<string, Dictionary<string, float[]>> Colours = new();
    static string _choicesFile = "", _coloursFile = "";
    static bool _picked;

    public static IReadOnlyDictionary<string, float[]>? ColoursOf(string skinId) => Colours.GetValueOrDefault(skinId);
    static int _colourVersion; // counts colour changes: a painted document is made once per change

    // The skin's document as the player coloured it.
    public static SkinDocument Painted(Skin skin) => skin.Painted(0, ColoursOf(skin.Doc.Id), _colourVersion);

    // Installed skins by the number a teammate's look names them by (Look.SkinNumber).
    public static readonly Dictionary<uint, Skin> ByNumber = new();

    // Counts every change of what the local player wears or how (a pick, a colour, a code-effect change, a reload, skins on or off):
    // their look is written again when it moves.
    public static int Version { get; private set; }

    static int _hashedFlag;
    internal static void NoteHashed() => Interlocked.Exchange(ref _hashedFlag, 1);

    // True once since a pack's content hash arrived (Skin.ContentHash).
    public static bool Hashed() => Interlocked.Exchange(ref _hashedFlag, 0) == 1;

    // The Thunderstore mod a pack came in: the mod folder directly under BepInEx/plugins that holds it; "" for the skins folder's.
    static string PackOf(string file)
    {
        var plugins = Path.GetFullPath(_plugins) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) return "";
        var rest = full[plugins.Length..];
        int cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return cut > 0 ? rest[..cut] : "";
    }

    // What a skin looks like, as text that changes when its pack is rebuilt or the player changes its colours (pictures are redrawn on a change).
    public static string LookOf(string id) =>
        (ById.TryGetValue(id, out var skin) ? $"{id}@{skin.Stamp.Ticks}" : id)
        + string.Concat((ColoursOf(id) ?? new Dictionary<string, float[]>()).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"|{c.Key}={string.Join(",", c.Value)}"));

    // The global skins' looks together.
    public static string GlobalsLook() => string.Join(",", Globals.Select(LookOf));

    // ForgeSkin.colours.json is written half a second after the last colour or code-effect change (a slider sends one per step
    // while dragged, swatches are clicked through); < 0: nothing waiting.
    static float _coloursDue = -1;

    // The colour the player gave one of a skin's player colours (null: back to the skin's own), kept in
    // ForgeSkin.colours.json and re-applied to live gear on the next tick like a pick.
    public static void Colour(string skinId, string colourId, float[]? rgb)
    {
        if (!Colours.TryGetValue(skinId, out var picks)) Colours[skinId] = picks = new();
        if (rgb == null) picks.Remove(colourId);
        else picks[colourId] = rgb;
        if (picks.Count == 0) Colours.Remove(skinId);
        _colourVersion++;
        Version++;
        _picked = true;
        ForgeSkinPlugin.Info($"colour {skinId}/{colourId}: {(rgb == null ? "default" : string.Join(", ", rgb))}");
        _coloursDue = Time.unscaledTime + 0.5f;
    }

    // Counts the player's code-effect changes: live effects read their values again when it moves (no re-apply, no reload).
    public static int TweakVersion { get; private set; }

    // A player change of a code effect (CodeEffects.TweakKey -> one number or sRGB RGBA; null: back to the skin's own), kept with the
    // skin's player colours.
    public static void Tweak(string skinId, string key, float[]? value)
    {
        if (!Colours.TryGetValue(skinId, out var picks)) Colours[skinId] = picks = new();
        if (value == null) picks.Remove(key);
        else picks[key] = value;
        if (picks.Count == 0) Colours.Remove(skinId);
        TweakVersion++;
        Version++;
        _coloursDue = Time.unscaledTime + 0.5f;
    }

    static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase); // files kept as they are: never written over

    static void Save<T>(string file, T value, string code)
    {
        if (Unreadable.Contains(file))
        {
            ForgeSkinPlugin.Fail(code, $"{Path.GetFileName(file)} not written: it could not be read at launch and is kept as it is; the choice lasts until the game closes");
            return;
        }
        try { ForgeCore.Store.WriteText(file, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ForgeSkinPlugin.Fail(code, $"{Path.GetFileName(file)} not written: {e.Message}; the choice lasts until the game closes", e); }
    }

    static void Load<T>(string file, Action<T> read, string code)
    {
        try { if (File.Exists(file)) read(JsonSerializer.Deserialize<T>(File.ReadAllText(file))!); }
        catch (Exception e)
        {
            Unreadable.Add(file);
            ForgeSkinPlugin.Fail(code, $"{Path.GetFileName(file)}: {e.Message}; starts empty and the file is not written over", e);
        }
    }

    static readonly Dictionary<string, Skin> ByFile = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, (DateTime Stamp, string Reason)> Rejected = new(StringComparer.OrdinalIgnoreCase);
    // The packs found at the last reload that could not be loaded: file name and the lang key of why (the picker lists them).
    public static readonly List<(string File, string Reason)> Failed = new();
    static string _dir = "", _plugins = "";
    internal static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher? _skinsWatcher, _pluginsWatcher;
    static int _events, _seen, _busyRetries;
    static float _due; // 0 = load on the first tick; < 0 = nothing pending
    // What the next scan reads: every pack (_full: the first one, a change in the plugins tree, a watcher that overflowed) or only
    // the skins-folder packs the watcher named since the last scan (_changed, full paths); the others keep what was read before.
    static bool _full = true;
    static readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    static Task<Scanned>? _scan;

    public static void Watch(string dir, string plugins, string choices, string colours, bool hotReload)
    {
        (_dir, _plugins, _choicesFile, _coloursFile) = (dir, plugins, choices, colours);
        Directory.CreateDirectory(dir);
        Load<Dictionary<string, string>>(choices, read => { foreach (var (key, id) in read) Choices[key] = id; }, "skin-choices-unreadable");
        Load<Dictionary<string, Dictionary<string, float[]>>>(colours, read =>
        {
            foreach (var (skin, picks) in read)
                // Player colours are sRGB (3); code-effect changes one number (1) or sRGB RGBA (4).
                Colours[skin] = picks.Where(p => p.Value is { Length: 1 or 3 or 4 } && p.Value.All(float.IsFinite)).ToDictionary(p => p.Key, p => p.Value);
        }, "skin-colours-unreadable");
        // The skins folder (BepInEx/ForgeSkin/skins) is watched always: only the editor writes there, and what it installs or takes
        // out shows up in a running game. A change reads just the packs it names, on a worker.
        _skinsWatcher = Watcher(_dir, false);
        HotReload(hotReload);
    }

    // Development: the plugins folder (skin mods' markers and packs) is watched too, a change there reading everything again.
    // Turned on while the game runs, everything is read again at once.
    public static void HotReload(bool on)
    {
        if (on == (_pluginsWatcher != null)) return;
        _pluginsWatcher?.Dispose();
        _pluginsWatcher = null;
        if (!on) return;
        Rescan();
        _pluginsWatcher = Watcher(_plugins, true);
    }

    // Everything is read again at the next scan.
    static void Rescan()
    {
        lock (_changed) _full = true;
        Interlocked.Increment(ref _events);
    }

    // `file` (a pack) is read again at the next scan.
    static void Touched(string file)
    {
        lock (_changed) _changed.Add(Path.GetFullPath(file));
        Interlocked.Increment(ref _events);
    }

    static FileSystemWatcher Watcher(string folder, bool plugins)
    {
        var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = plugins, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        // Only skin inputs count: other mods' files live in the plugins tree, and counting the plugin's own log would reload
        // after every log line, which logs again. In the skins folder a pack is named for the scan; active.json and the
        // one-shot picks are small and read at every scan.
        void Seen(string? name, string path)
        {
            if (!SkinInput(name)) return;
            if (plugins) Rescan();
            else if (path.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase)) Touched(path);
            else Interlocked.Increment(ref _events);
        }
        FileSystemEventHandler touched = (_, e) => Seen(e.Name, e.FullPath);
        watcher.Changed += touched;
        watcher.Created += touched;
        watcher.Deleted += touched;
        watcher.Renamed += (_, e) => { Seen(e.OldName, e.OldFullPath); Seen(e.Name, e.FullPath); };
        watcher.Error += (_, _) => Rescan(); // events were lost (the buffer overflowed): read everything
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    static bool SkinInput(string? name)
    {
        var file = Path.GetFileName(name ?? "");
        return file.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase) || file.EndsWith(SkinPack.PickExtension, StringComparison.OrdinalIgnoreCase)
            || file.Equals("active.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals(SkinPacks.Marker, StringComparison.OrdinalIgnoreCase);
    }

    // Writes the waiting colour and code-effect changes now: when the half second is up, and when the game quits inside it.
    public static void SaveColours()
    {
        if (_coloursDue < 0) return;
        _coloursDue = -1;
        ForgeSkinPlugin.Info("colour and code effect changes saved");
        Save(_coloursFile, Colours, "skin-colours-write-failed");
    }

    // Main thread. True when the skins were reloaded and live gear must be re-applied.
    public static bool Poll(float now)
    {
        if (_coloursDue >= 0 && now >= _coloursDue) SaveColours();
        if (_picked) { _picked = false; return true; }
        if (_scan != null)
        {
            if (!_scan.IsCompleted) return false;
            var scan = _scan;
            _scan = null;
            if (scan.IsCompletedSuccessfully)
            {
                _busyRetries = 0;
                Apply(scan.Result);
                return true;
            }
            var e = scan.Exception!.GetBaseException();
            // What it was to read is read again: the backend is still writing a pack or active.json (a half-written file reads as bad JSON).
            lock (_changed)
            {
                _full |= scan.AsyncState is true;
                if (scan.AsyncState is HashSet<string> retry) _changed.UnionWith(retry);
            }
            if (e is IOException or JsonException && ++_busyRetries < 10) _due = now + 0.5f;
            else { _busyRetries = 0; ForgeSkinPlugin.Fail("skin-folder-reload-failed", $"skins folder: {e.Message}; the skins loaded before stay", e); }
            return false;
        }
        int events = Volatile.Read(ref _events);
        if (events != _seen) { _seen = events; _due = now + 0.3f; return false; }
        if (_due < 0 || now < _due) return false;
        _due = -1;
        // Texture quality 1 (half) or 2 (quarter) shows every mipmapped skin texture at that fraction of its width and height.
        ForgeSkinPlugin.Info($"reloading skins; texture quality limit {QualitySettings.masterTextureLimit}");
        bool full;
        HashSet<string> named;
        lock (_changed)
        {
            (full, named) = (_full, new HashSet<string>(_changed, StringComparer.OrdinalIgnoreCase));
            _full = false;
            _changed.Clear();
        }
        var known = new Dictionary<string, Skin>(ByFile, StringComparer.OrdinalIgnoreCase);
        var rejected = Rejected.ToDictionary(kv => kv.Key, kv => kv.Value.Stamp, StringComparer.OrdinalIgnoreCase);
        // The state names what to read again if the scan fails: everything, or the named packs.
        _scan = Task.Factory.StartNew(state => Scan(full, named, known, rejected), full ? true : named, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        return false;
    }

    // What a scan found: every pack and its skin (the one read before where the file is unchanged), the skins it read, the packs it
    // could not, active.json, the one-shot picks, and the errors to report on the main thread.
    sealed class Scanned
    {
        public readonly Dictionary<string, Skin> ByFile = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Skin> Read = new();
        public readonly List<(string File, DateTime Stamp, Exception Error)> Bad = new();
        public readonly Dictionary<string, string> Targets = new();
        public readonly List<string> Globals = new();
        public List<(string File, string Target, string Skin)> Picks = new();
        public readonly List<(string Code, string Message)> Fails = new();
    }

    // Worker thread, no Unity call: reads the packs to read (all with `full`, else those named) and keeps the others as they were.
    // An I/O error (a pack still being written) fails the whole scan, which is tried again; the old state stays meanwhile.
    static Scanned Scan(bool full, HashSet<string> named, Dictionary<string, Skin> known, Dictionary<string, DateTime> rejected)
    {
        var s = new Scanned();
        IEnumerable<string> files;
        if (full) files = Discover(s.Fails);
        else
        {
            var set = new HashSet<string>(known.Keys, StringComparer.OrdinalIgnoreCase);
            set.UnionWith(rejected.Keys);
            foreach (var file in named)
                if (File.Exists(file)) set.Add(file);
                else set.Remove(file);
            files = set;
        }
        foreach (var file in files)
        {
            known.TryGetValue(file, out var old);
            if (!full && !named.Contains(file))
            {
                if (old != null) s.ByFile[file] = old;
                continue;
            }
            var stamp = File.GetLastWriteTimeUtc(file);
            // A pack whose new version is rejected keeps its old one.
            if (old != null && old.Stamp == stamp || rejected.TryGetValue(file, out var bad) && bad == stamp)
            {
                if (old != null) s.ByFile[file] = old;
                continue;
            }
            try { (s.ByFile[file] = Skin.Read(file, stamp)).Pack = PackOf(file); }
            catch (Exception e) when (e is not IOException)
            {
                s.Bad.Add((file, stamp, e));
                if (old != null) s.ByFile[file] = old;
                continue;
            }
            s.Read.Add(s.ByFile[file]);
        }
        ReadActive(s.Targets, s.Globals);
        s.Picks = ReadPicks(s.Fails);
        return s;
    }

    // Main thread: replaces the old state with what the scan found, in one step.
    static void Apply(Scanned scan)
    {
        var (byFile, read, targets, globals, picks) = (scan.ByFile, scan.Read, scan.Targets, scan.Globals, scan.Picks);
        foreach (var (code, message) in scan.Fails) ForgeSkinPlugin.Fail(code, message);
        foreach (var skin in read) Rejected.Remove(skin.File);
        foreach (var file in Rejected.Keys.Where(f => !File.Exists(f)).ToList()) Rejected.Remove(file);
        foreach (var (file, stamp, e) in scan.Bad) Reject(file, stamp, e);

        // A replaced or removed skin is freed once no gear shows it any more (Release).
        foreach (var (file, old) in ByFile)
        {
            if (byFile.TryGetValue(file, out var kept) && kept == old) continue;
            old.Retired = true;
            if (kept == null) ForgeSkinPlugin.Info($"removed {Path.GetFileName(file)}");
        }
        ByFile.Clear();
        foreach (var (file, skin) in byFile) ByFile[file] = skin;
        foreach (var skin in read)
            ForgeSkinPlugin.Note($"found {Path.GetFileName(skin.File)}: id {skin.Doc.Id}, {skin.Doc.Materials.Count} materials, {skin.Doc.Attachments.Count} attachments, {skin.Doc.Sounds.Count} sounds (loaded when first worn)");
        var before = _activeTargets;
        (_activeTargets, _activeGlobals) = (targets, globals);
        Index();
        // The editor's 应用到游戏 goes before the player's choice for that one target, oldest first; later picks in the picker
        // work as before. Each one-shot file is deleted once its choice is saved.
        if (picks.Count > 0)
        {
            foreach (var (_, key, id) in picks)
            {
                Choices[key] = id;
                ForgeSkinPlugin.Info($"picked {key}: {id} (applied from the editor)");
            }
            Resolve();
            Save(_choicesFile, Choices, "skin-choices-write-failed");
            foreach (var (file, _, _) in picks)
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ForgeSkinPlugin.Warn($"{Path.GetFileName(file)} not deleted: {e.Message}"); }
        }
        // A skin active.json newly names that the player's pick keeps off (a pack put in by hand, active.json changed another
        // way): said, so it does not look ignored. Not at the first read after launch: nothing changed then, the pick simply stands.
        var first = !_readOnce;
        _readOnce = true;
        foreach (var (key, id) in targets)
            if (Enabled && !first && before.GetValueOrDefault(key) != id && ById.ContainsKey(id) && Targets.GetValueOrDefault(key) != id && Choices.TryGetValue(key, out var pick))
                ForgeSkinPlugin.Warn($"{key}: active.json now picks {id}, but the in-game picker's choice ({(pick == "" ? "original look" : pick)}) goes first; choose {id} in the picker to wear it");
    }

    // The editor's one-shot picks (`<ticks>.pick`: {"target", "skin"}) in the skins folder, oldest first. The editor writes one
    // whole (a `.tmp` renamed), so one that cannot be understood, not JSON included, is reported and deleted rather than failing
    // every reload; a file still locked throws IOException and the reload is tried again.
    static List<(string File, string Target, string Skin)> ReadPicks(List<(string Code, string Message)> fails)
    {
        var picks = new List<(string, string, string)>();
        foreach (var file in Directory.GetFiles(_dir, "*" + SkinPack.PickExtension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(file);
            JsonDocument? json = null;
            try { json = JsonDocument.Parse(text); }
            catch (JsonException) { json = null; } // not JSON: reported and deleted below
            using var _ = json;
            if (json != null && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String
                && json.RootElement.TryGetProperty("skin", out var skin) && skin.ValueKind == JsonValueKind.String)
                picks.Add((file, target.GetString()!, skin.GetString()!));
            else
            {
                fails.Add(("skin-active-entry-invalid", $"{Path.GetFileName(file)}: not a target and skin; ignored"));
                File.Delete(file);
            }
        }
        return picks;
    }

    static bool _readOnce; // the skins folder was read once since launch
    static Dictionary<string, string> _activeTargets = new(); // active.json's targets
    static List<string> _activeGlobals = new(); // active.json's global skins

    static void Reject(string file, DateTime stamp, Exception e)
    {
        var code = e is SkinFormatException f ? f.Code : e.GetType().Name;
        // The picker shows a plain reason (a lang key); the code and the exception's own text go only to the log.
        Rejected[file] = (stamp, code switch
        {
            "schema" => "picker.failedVersion",
            "bad-character-bones" => "picker.failedBones",
            "unsupported-character-material" => "picker.failedUnsupported",
            _ => e is IOException ? "picker.failedLocked" : "picker.failedDamaged",
        });
        ForgeSkinPlugin.Fail("skin-pack-rejected", $"{Path.GetFileName(file)}: {code}: {e.Message}; skin not loaded", e is SkinFormatException ? null : e);
    }

    // The lookups built from ByFile, the rejected packs and active.json.
    static void Index()
    {
        var targets = new Dictionary<string, string>(_activeTargets);
        ById.Clear();
        ByNumber.Clear();
        var own = Path.GetFullPath(_dir) + Path.DirectorySeparatorChar;
        // Two packs with one id: the newer file wins (a pack's write time is when it was built: the editor's 应用 builds and writes it
        // then, a mod's packs carry when their author built them or when the mod manager unpacked them), then the skins folder's
        // over a mod's, then the first
        // by path, so the same one wins at every reload.
        string Where(string file) => Path.GetFullPath(file).StartsWith(own, StringComparison.OrdinalIgnoreCase)
            ? $"the skins folder's {Path.GetFileName(file)}" : $"plugins/{Path.GetRelativePath(_plugins, file).Replace('\\', '/')}";
        foreach (var (file, skin) in ByFile.OrderByDescending(kv => kv.Value.Stamp).ThenBy(kv => Path.GetFullPath(kv.Key).StartsWith(own, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (ById.TryGetValue(skin.Doc.Id, out var kept))
                ForgeSkinPlugin.Fail("skin-pack-duplicate-id", $"two packs share id {skin.Doc.Id}; {Where(kept.File)} ({kept.Stamp:yyyy-MM-dd HH:mm} UTC) is used, {Where(file)} ({skin.Stamp:yyyy-MM-dd HH:mm} UTC) is ignored");
            else
            {
                ById[skin.Doc.Id] = skin;
                ByNumber.TryAdd(skin.Number, skin);
            }

        Failed.Clear();
        foreach (var (file, bad) in Rejected)
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) == bad.Stamp) Failed.Add((Path.GetFileName(file), bad.Reason));
        _base = targets;
        Candidates.Clear();
        foreach (var (file, skin) in ByFile.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (skin.Doc.Target.Key() is not { } key) continue;
            if (ById[skin.Doc.Id] == skin)
            {
                if (!Candidates.TryGetValue(key, out var list)) Candidates[key] = list = new();
                list.Add(skin);
            }
            if (Path.GetFullPath(file).StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue;
            if (_base.TryAdd(key, skin.Doc.Id)) ForgeSkinPlugin.Info($"{key}: {skin.Doc.Id} from {Path.GetFileName(Path.GetDirectoryName(file))} is active");
            else if (_base[key] != skin.Doc.Id) ForgeSkinPlugin.Info($"{key}: {skin.Doc.Id} from {Path.GetFileName(Path.GetDirectoryName(file))} not used, {_base[key]} is active");
        }
        Resolve();
        ForgeSkinPlugin.Info($"active: {Targets.Count} target skins, {Globals.Count} global skins");
    }

    // Targets = _base with the player's choices on top; a choice whose skin is not loaded for that target is ignored, and so
    // is an active.json entry whose skin is not loaded (its pack was rejected, which is reported once, or removed): the gear
    // keeps its own look instead of failing every apply. With skins off (Enabled) nothing is worn; picks and colours stay.
    static void Resolve()
    {
        Version++;
        Targets.Clear();
        Globals.Clear();
        if (!Enabled) return;
        // A character skin is never a global one (it has no gear to go on).
        Globals.AddRange(_activeGlobals.Where(id => !(ById.TryGetValue(id, out var g) && g.Doc.Character != null)));
        foreach (var (key, id) in _base) if (ById.ContainsKey(id)) Targets[key] = id;
        foreach (var (key, id) in Choices)
            if (id == "") Targets.Remove(key);
            else if (ById.TryGetValue(id, out var skin) && skin.Doc.Target.Key() == key) Targets[key] = id;
    }

    // Skins on or off (Enabled in ForgeSkin.cfg): off, every gear goes back to its original look on the next tick.
    public static bool Enabled = true;

    public static void Enable(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;
        Resolve();
        _picked = true;
        ForgeSkinPlugin.Info($"skins {(on ? "on" : "off")}");
    }

    // Live gear puts its skins on again on the next tick (a drawing setting changed).
    public static void Reapply() => _picked = true;

    // The picker's choice ("" = vanilla): kept in ForgeSkin.choices.json and re-applied to live gear on the next tick,
    // the same way as a hot reload, without reading the packs again.
    public static void Choose(string key, string id)
    {
        Choices[key] = id;
        Resolve();
        _picked = true;
        ForgeSkinPlugin.Info($"picked {key}: {(id == "" ? "vanilla" : id)}");
        Save(_choicesFile, Choices, "skin-choices-write-failed");
    }

    // ---- loading on demand ----

    // A skin no gear has shown or waited for this long is freed; switching back within it costs no second decode.
    const float KeepUnused = 30f;
    static readonly List<Skin> Resident = new(); // loading or loaded
    static bool _arrived;

    // The skins a gear of this target wears now: the global ones and its own.
    public static List<Skin> Wanted(string key) => Wanted(Own(key));

    // The skins a gear wears in `weapon` (null: no skin of its own): the global ones and that one.
    public static List<Skin> Wanted(Skin? weapon)
    {
        var wanted = new List<Skin>();
        foreach (var id in Globals)
            if (ById.TryGetValue(id, out var global)) wanted.Add(global);
        if (weapon != null) wanted.Add(weapon);
        return wanted;
    }

    // The skin of its own a gear of this target wears (null: none).
    public static Skin? Own(string key) => Targets.TryGetValue(key, out var own) ? ById.GetValueOrDefault(own) : null;

    // True when all of them are loaded; starts loading the others.
    public static bool Ready(List<Skin> wanted, float now)
    {
        bool ready = true;
        foreach (var skin in wanted) ready &= Loaded(skin, now);
        return ready;
    }

    // Ready for Wanted(weapon) without making its list: for waits checked every frame.
    public static bool Ready(Skin? weapon, float now)
    {
        bool ready = true;
        foreach (var id in Globals)
            if (ById.TryGetValue(id, out var global)) ready &= Loaded(global, now);
        return weapon == null ? ready : Loaded(weapon, now) & ready;
    }

    // True when the skin is loaded; asks for it otherwise.
    static bool Loaded(Skin skin, float now)
    {
        if (skin.State == SkinState.Ready) { skin.LastUsed = now; return true; }
        if (skin.State == SkinState.Unloaded) Resident.Add(skin);
        skin.Request(now);
        return false;
    }

    // True once since a skin finished loading or failed to: gear waiting for one tries again.
    public static bool Arrived()
    {
        var arrived = _arrived;
        _arrived = false;
        return arrived;
    }

    // Main thread, every frame: finishes loads. A pack that changed on disk since its manifest was read is read again
    // (a reload); one whose files cannot be decoded is rejected like a bad manifest and its gear re-applied.
    public static void Pump(float now)
    {
        for (int i = Resident.Count - 1; i >= 0; i--)
        {
            var skin = Resident[i];
            if (skin.State != SkinState.Loading) continue;
            var failure = skin.Pump(out var finished);
            if (!finished) continue;
            if (failure == null) { _arrived = true; continue; }
            Resident.RemoveAt(i);
            // The gear waiting for it asks again after that reload.
            if (failure is Skin.StaleException || failure is IOException && ++skin.ReadFailures < 5)
            {
                ForgeSkinPlugin.Info($"skin {skin.Doc.Id}: {failure.Message}; reading the skins again");
                Touched(skin.File);
                continue;
            }
            _arrived = true;
            // No longer installed: whatever still holds it (a picker card waiting for its picture) lets go instead of loading it again.
            skin.Retired = true;
            Reject(skin.File, skin.Stamp, failure);
            if (ByFile.TryGetValue(skin.File, out var installed) && installed == skin)
            {
                ByFile.Remove(skin.File);
                Index();
                _picked = true;
            }
        }
    }

    // Frees the skins no gear in `used` shows or waits for: retired ones at once, the others after KeepUnused.
    public static void Release(float now, HashSet<Skin> used)
    {
        for (int i = Resident.Count - 1; i >= 0; i--)
        {
            var skin = Resident[i];
            if (used.Contains(skin)) { skin.LastUsed = now; continue; }
            if (!skin.Retired && now - skin.LastUsed < KeepUnused) continue;
            skin.Unload();
            Resident.RemoveAt(i);
            ForgeSkinPlugin.Info($"freed skin {skin.Doc.Id}{(skin.Retired ? " (replaced or removed)" : $", unused for {KeepUnused} s")}");
        }
    }

    // Worker thread: every pack file (skins folder, skin mods' markers, registered packs); what cannot be found goes to `fails`.
    static string[] Discover(List<(string Code, string Message)> fails)
    {
        var files = new HashSet<string>(Directory.GetFiles(_dir, "*" + SkinPack.Extension), StringComparer.OrdinalIgnoreCase);
        foreach (var (_, pack, problem) in SkinFolders.ModPacks(_plugins))
            if (problem != null) fails.Add(("skin-marker-entry-invalid", problem));
            else files.Add(pack);
        lock (Registered)
            foreach (var pack in Registered)
                if (File.Exists(pack)) files.Add(pack);
                else fails.Add(("skin-registered-pack-missing", $"registered pack {pack} not found"));
        return files.ToArray();
    }

    static void ReadActive(Dictionary<string, string> targets, List<string> globals)
    {
        var active = Path.Combine(_dir, "active.json");
        if (!File.Exists(active)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(active));
        if (json.RootElement.TryGetProperty("targets", out var entries))
            foreach (var entry in entries.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String) targets[entry.Name] = entry.Value.GetString()!;
                else ForgeSkinPlugin.Fail("skin-active-entry-invalid", $"active.json: bad target entry {entry.Name}");
        if (json.RootElement.TryGetProperty("global", out var global))
            foreach (var id in global.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String) globals.Add(id.GetString()!);
    }
}
