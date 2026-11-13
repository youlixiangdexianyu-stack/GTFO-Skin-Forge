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
    SkinDocument? _painted;
    int _paintedVersion = -1;

    Skin(SkinDocument doc, string file, DateTime stamp) { Doc = doc; File = file; Stamp = stamp; }

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
    }

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
            _decoding = c.Textures.Count;
            int load = ++_loads;
            foreach (var (path, channel, compress, bytes, packed) in c.Textures)
            {
                var (p, ch, comp) = (path, channel, compress);
                Action<Texture2D?, string> done = (texture, failure) => Decoded(load, p, ch, comp, texture, failure);
                _jobs.Add(packed != null
                    ? ForgeCore.Images.QueueRaw(packed.Data, Dds.HeaderSize, packed.Width, packed.Height, packed.Alpha ? TextureFormat.DXT5 : TextureFormat.DXT1, true, !packed.Srgb, done)
                    : ForgeCore.Images.Queue(bytes!, done, Linear(channel), mipChain: compress, normalMap: channel == TextureChannel.Normal && compress, compress: compress));
            }
        }
        if (_failure is { } failed) { _failure = null; return Abort(failed, out finished); }
        if (_decoding > 0) return null;
        _jobs.Clear();
        State = SkinState.Ready;
        finished = true;
        ForgeSkinPlugin.Info($"loaded skin {Doc.Id}: {_textures.Count} textures, {_meshes.Count} meshes, {_cues.Count} sounds in {_watch.ElapsedMilliseconds} ms");
        return null;
    }

    Exception? _failure;

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

    // The document as the player coloured it (`picks`: colour id -> linear RGB): a painted copy, or the pack's own when
    // the player changed nothing. One copy per colour change (`version`), not one per gear and apply.
    public SkinDocument Painted(IReadOnlyDictionary<string, float[]>? picks, int version)
    {
        if (picks == null || picks.Count == 0 || Doc.PlayerColors is not { Count: > 0 }) return Doc;
        if (_painted != null && _paintedVersion == version) return _painted;
        var copy = SkinJson.Read(SkinJson.Write(Doc));
        foreach (var c in copy.PlayerColors!)
            if (picks.TryGetValue(c.Id, out var rgb)) c.Paint(copy, rgb);
        (_painted, _paintedVersion) = (copy, version);
        return copy;
    }

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
        _textures.Clear();
        _meshes.Clear();
        _cues.Clear();
        _sounds = null;
        State = SkinState.Unloaded;
    }
}

// Installed packs, their manifests read on the first tick (each skin's files when a gear first wears it). Three sources:
// - the app's own skins folder: every pack in it, selection from its active.json;
// - skin mods: a mod folder directly under BepInEx/plugins with a `forgeskin.forge` marker lists its packs, one path per
//   line relative to that folder (blank lines and `#` comments skipped). Only the marker is looked for, one level deep;
// - code: other plugins call `SkinPacks.Register` with a pack path from their Load.
// A mod's or registered skin is active for its target unless active.json picks a skin for that target; among them the
// first by path wins; the player's choice in the in-game picker (ForgeSkin.choices.json) goes before both. In development mode a watcher marks the plugins folder dirty and everything is reloaded;
// otherwise changes wait for the next launch, except a pack that changed before its skin was first worn: it is read again then.
public static class SkinPacks
{
    public const string Marker = "forgeskin.forge";

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
    // The player's colours: skin id -> player colour id -> linear RGB; kept for skins not loaded.
    static readonly Dictionary<string, Dictionary<string, float[]>> Colours = new();
    static string _choicesFile = "", _coloursFile = "";
    static bool _picked;

    public static IReadOnlyDictionary<string, float[]>? ColoursOf(string skinId) => Colours.GetValueOrDefault(skinId);
    static int _colourVersion; // counts colour changes: a painted document is made once per change

    // The skin's document as the player coloured it.
    public static SkinDocument Painted(Skin skin) => skin.Painted(ColoursOf(skin.Doc.Id), _colourVersion);

    // What a skin looks like, as text that changes when its pack is rebuilt or the player changes its colours (pictures are redrawn on a change).
    public static string LookOf(string id) =>
        (ById.TryGetValue(id, out var skin) ? $"{id}@{skin.Stamp.Ticks}" : id)
        + string.Concat((ColoursOf(id) ?? new Dictionary<string, float[]>()).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"|{c.Key}={string.Join(",", c.Value)}"));

    // The global skins' looks together.
    public static string GlobalsLook() => string.Join(",", Globals.Select(LookOf));

    // The colour the player gave one of a skin's player colours (null: back to the skin's own), kept in
    // ForgeSkin.colours.json and re-applied to live gear on the next tick like a pick.
    public static void Colour(string skinId, string colourId, float[]? rgb)
    {
        if (!Colours.TryGetValue(skinId, out var picks)) Colours[skinId] = picks = new();
        if (rgb == null) picks.Remove(colourId);
        else picks[colourId] = rgb;
        if (picks.Count == 0) Colours.Remove(skinId);
        _colourVersion++;
        _picked = true;
        ForgeSkinPlugin.Info($"colour {skinId}/{colourId}: {(rgb == null ? "default" : string.Join(", ", rgb))}");
        Save(_coloursFile, Colours, "skin-colours-write-failed");
    }

    // Counts the player's code-effect changes: live effects read their values again when it moves (no re-apply, no reload).
    public static int TweakVersion { get; private set; }
    static float _tweaksDue = -1; // when the changed tweaks are written to ForgeSkin.colours.json; < 0: nothing waiting

    // A player change of a code effect (CodeEffects.TweakKey -> one number or sRGB RGBA; null: back to the skin's own), kept with the
    // skin's player colours. A slider sends one per step while dragged, so the file is written half a second after the last.
    public static void Tweak(string skinId, string key, float[]? value)
    {
        if (!Colours.TryGetValue(skinId, out var picks)) Colours[skinId] = picks = new();
        if (value == null) picks.Remove(key);
        else picks[key] = value;
        if (picks.Count == 0) Colours.Remove(skinId);
        TweakVersion++;
        _tweaksDue = Time.unscaledTime + 0.5f;
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
    // The packs found at the last reload that could not be loaded: file name and why (the picker lists them).
    public static readonly List<(string File, string Reason)> Failed = new();
    static string _dir = "", _plugins = "";
    internal static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher? _watcher;
    static int _events, _seen, _busyRetries;
    static float _due; // 0 = load on the first tick; < 0 = nothing pending

    public static void Watch(string dir, string plugins, string choices, string colours, bool hotReload)
    {
        (_dir, _plugins, _choicesFile, _coloursFile) = (dir, plugins, choices, colours);
        Directory.CreateDirectory(dir);
        Load<Dictionary<string, string>>(choices, read => { foreach (var (key, id) in read) Choices[key] = id; }, "skin-choices-unreadable");
        Load<Dictionary<string, Dictionary<string, float[]>>>(colours, read =>
        {
            foreach (var (skin, picks) in read)
                // Player colours are linear RGB (3); code-effect changes one number (1) or sRGB RGBA (4).
                Colours[skin] = picks.Where(p => p.Value is { Length: 1 or 3 or 4 } && p.Value.All(float.IsFinite)).ToDictionary(p => p.Key, p => p.Value);
        }, "skin-colours-unreadable");
        if (!hotReload) return;
        _watcher = new FileSystemWatcher(plugins) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        // Only skin inputs count: the plugin's own log (and other mods' files) live in the same tree, and counting them
        // would reload after every log line, which logs again.
        FileSystemEventHandler touched = (_, e) => { if (SkinInput(e.Name)) Interlocked.Increment(ref _events); };
        _watcher.Changed += touched;
        _watcher.Created += touched;
        _watcher.Deleted += touched;
        _watcher.Renamed += (_, e) => { if (SkinInput(e.Name) || SkinInput(e.OldName)) Interlocked.Increment(ref _events); };
        _watcher.EnableRaisingEvents = true;
    }

    static bool SkinInput(string? name)
    {
        var file = Path.GetFileName(name ?? "");
        return file.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase) || file.EndsWith(SkinPack.PickExtension, StringComparison.OrdinalIgnoreCase)
            || file.Equals("active.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals(SkinPacks.Marker, StringComparison.OrdinalIgnoreCase);
    }

    // Main thread. True when the skins were reloaded and live gear must be re-applied.
    public static bool Poll(float now)
    {
        if (_tweaksDue >= 0 && now >= _tweaksDue)
        {
            _tweaksDue = -1;
            ForgeSkinPlugin.Info("code effect changes saved");
            Save(_coloursFile, Colours, "skin-colours-write-failed");
        }
        if (_picked) { _picked = false; return true; }
        int events = Volatile.Read(ref _events);
        if (events != _seen) { _seen = events; _due = now + 0.3f; return false; }
        if (_due < 0 || now < _due) return false;
        _due = -1;
        try
        {
            Reload();
            _busyRetries = 0;
            return true;
        }
        // The backend is still writing a pack or active.json (a half-written file reads as bad JSON).
        catch (Exception e) when (e is IOException or JsonException && ++_busyRetries < 10) { _due = now + 0.5f; }
        catch (Exception e) { _busyRetries = 0; ForgeSkinPlugin.Fail("skin-folder-reload-failed", $"skins folder: {e.Message}; the skins loaded before stay", e); }
        return false;
    }

    // Builds the new state completely and only then replaces the old one; a failure leaves the old state as it was.
    static void Reload()
    {
        // Texture quality 1 (half) or 2 (quarter) shows every mipmapped skin texture at that fraction of its width and height.
        ForgeSkinPlugin.Info($"reloading skins; texture quality limit {QualitySettings.masterTextureLimit}");
        var byFile = new Dictionary<string, Skin>(StringComparer.OrdinalIgnoreCase);
        var read = new List<Skin>();
        var targets = new Dictionary<string, string>();
        var globals = new List<string>();
        foreach (var file in Discover())
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            ByFile.TryGetValue(file, out var old);
            // A pack whose new version is rejected keeps its old one.
            if (old != null && old.Stamp == stamp || Rejected.TryGetValue(file, out var bad) && bad.Stamp == stamp)
            {
                if (old != null) byFile[file] = old;
                continue;
            }
            try { byFile[file] = Skin.Read(file, stamp); }
            catch (Exception e) when (e is not IOException)
            {
                Reject(file, stamp, e);
                if (old != null) byFile[file] = old;
                continue;
            }
            read.Add(byFile[file]);
            Rejected.Remove(file);
        }
        ReadActive(targets, globals);
        var picks = ReadPicks();

        // A replaced or removed skin is freed once no gear shows it any more (Release).
        foreach (var (file, old) in ByFile)
        {
            if (byFile.TryGetValue(file, out var kept) && kept == old) continue;
            old.Retired = true;
            if (kept == null) ForgeSkinPlugin.Info($"removed {Path.GetFileName(file)}");
        }
        ByFile.Clear();
        foreach (var (file, skin) in byFile) ByFile[file] = skin;
        foreach (var (file, skin) in byFile)
            if (read.Contains(skin))
                ForgeSkinPlugin.Info($"found {Path.GetFileName(file)}: id {skin.Doc.Id}, {skin.Doc.Materials.Count} materials, {skin.Doc.Attachments.Count} attachments, {skin.Doc.Sounds.Count} sounds (loaded when first worn)");
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
        // way): said, so it does not look ignored.
        foreach (var (key, id) in targets)
            if (before.GetValueOrDefault(key) != id && ById.ContainsKey(id) && Targets.GetValueOrDefault(key) != id && Choices.TryGetValue(key, out var pick))
                ForgeSkinPlugin.Warn($"{key}: active.json now picks {id}, but the in-game picker's choice ({(pick == "" ? "original look" : pick)}) goes first; choose {id} in the picker to wear it");
    }

    // The editor's one-shot picks (`<ticks>.pick`: {"target", "skin"}) in the skins folder, oldest first. The editor writes one
    // whole (a `.tmp` renamed), so one that cannot be understood, not JSON included, is reported and deleted rather than failing
    // every reload; a file still locked throws IOException and the reload is tried again.
    static List<(string File, string Target, string Skin)> ReadPicks()
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
                ForgeSkinPlugin.Fail("skin-active-entry-invalid", $"{Path.GetFileName(file)}: not a target and skin; ignored");
                File.Delete(file);
            }
        }
        return picks;
    }

    static Dictionary<string, string> _activeTargets = new(); // active.json's targets
    static List<string> _activeGlobals = new(); // active.json's global skins

    static void Reject(string file, DateTime stamp, Exception e)
    {
        var code = e is SkinFormatException f ? f.Code : e.GetType().Name;
        Rejected[file] = (stamp, $"{code}: {e.Message}");
        ForgeSkinPlugin.Fail("skin-pack-rejected", $"{Path.GetFileName(file)}: {code}: {e.Message}; skin not loaded", e is SkinFormatException ? null : e);
    }

    // The lookups built from ByFile, the rejected packs and active.json.
    static void Index()
    {
        var targets = new Dictionary<string, string>(_activeTargets);
        ById.Clear();
        foreach (var skin in ByFile.Values)
            if (!ById.TryAdd(skin.Doc.Id, skin)) ForgeSkinPlugin.Fail("skin-pack-duplicate-id", $"two packs share id {skin.Doc.Id}; one is ignored");

        Failed.Clear();
        foreach (var (file, bad) in Rejected)
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) == bad.Stamp) Failed.Add((Path.GetFileName(file), bad.Reason));
        _base = targets;
        Globals.Clear();
        Globals.AddRange(_activeGlobals);
        Candidates.Clear();
        var own = Path.GetFullPath(_dir) + Path.DirectorySeparatorChar;
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
    // keeps its own look instead of failing every apply.
    static void Resolve()
    {
        Targets.Clear();
        foreach (var (key, id) in _base) if (ById.ContainsKey(id)) Targets[key] = id;
        foreach (var (key, id) in Choices)
            if (id == "") Targets.Remove(key);
            else if (ById.TryGetValue(id, out var skin) && skin.Doc.Target.Key() == key) Targets[key] = id;
    }

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
    public static List<Skin> Wanted(string key)
    {
        var wanted = new List<Skin>();
        foreach (var id in Globals)
            if (ById.TryGetValue(id, out var global)) wanted.Add(global);
        if (Targets.TryGetValue(key, out var own) && ById.TryGetValue(own, out var skin)) wanted.Add(skin);
        return wanted;
    }

    // True when all of them are loaded; starts loading the others.
    public static bool Ready(List<Skin> wanted, float now)
    {
        bool ready = true;
        foreach (var skin in wanted)
        {
            if (skin.State == SkinState.Ready) { skin.LastUsed = now; continue; }
            ready = false;
            if (skin.State == SkinState.Unloaded) Resident.Add(skin);
            skin.Request(now);
        }
        return ready;
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
                if (_due < 0) _due = now + 0.5f;
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

    static string[] Discover()
    {
        var files = new HashSet<string>(Directory.GetFiles(_dir, "*" + SkinPack.Extension), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Directory.GetDirectories(_plugins))
        {
            var marker = Path.Combine(mod, SkinPacks.Marker);
            if (!File.Exists(marker)) continue;
            foreach (var raw in File.ReadAllLines(marker))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var pack = Path.GetFullPath(Path.Combine(mod, line));
                if (!pack.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase)) ForgeSkinPlugin.Fail("skin-marker-entry-invalid", $"{Path.GetFileName(mod)}/{SkinPacks.Marker}: {line} is not a {SkinPack.Extension} pack");
                else if (File.Exists(pack)) files.Add(pack);
                else ForgeSkinPlugin.Fail("skin-marker-entry-invalid", $"{Path.GetFileName(mod)}/{SkinPacks.Marker}: {line} not found");
            }
        }
        lock (Registered)
            foreach (var pack in Registered)
                if (File.Exists(pack)) files.Add(pack);
                else ForgeSkinPlugin.Fail("skin-registered-pack-missing", $"registered pack {pack} not found");
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
