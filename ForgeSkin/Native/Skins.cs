using System.Text.Json;
using ForgeAudio;
using ForgeSkin.Format;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// One installed pack, decoded once: every texture and mesh it references lives on the GPU; the zip is closed again
// so the backend can overwrite it while the game runs.
sealed class Skin
{
    public readonly SkinDocument Doc;
    public readonly DateTime Stamp;
    readonly Dictionary<string, Texture2D> _textures = new();
    readonly Dictionary<string, Mesh> _meshes = new();
    readonly Dictionary<string, string> _cues = new(StringComparer.OrdinalIgnoreCase); // sound file -> cue of _sounds
    AudioLibrary? _sounds;

    Skin(SkinDocument doc, DateTime stamp) { Doc = doc; Stamp = stamp; }

    public Texture2D Texture(string path, bool linear) => _textures[(linear ? "L:" : "S:") + path];
    public Mesh Mesh(string path) => _meshes[path];
    public AudioCue Sound(string path) => _sounds!.Cue(_cues[path]);

    public static bool Linear(TextureChannel channel) => channel is TextureChannel.Normal or TextureChannel.MetallicSmoothness;

    public static Skin Load(string file, DateTime stamp)
    {
        using var pack = SkinPack.Open(file);
        var skin = new Skin(pack.Manifest, stamp);
        try
        {
            foreach (var m in skin.Doc.Materials)
            {
                foreach (var (channel, path) in m.Textures) skin.Decode(pack, path, channel, true, true);
                foreach (var stage in m.Wear ?? []) foreach (var (channel, path) in stage.Textures) skin.Decode(pack, path, channel, true, true);
                foreach (var l in m.Layers) skin.Decode(pack, l.Atlas!.Path, l.Channel, false, false);
            }
            foreach (var a in skin.Doc.Attachments)
            {
                if (a.Mesh != null && !skin._meshes.ContainsKey(a.Mesh)) skin._meshes[a.Mesh] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(a.Mesh)), a.Mesh);
                if (a.Material != null) foreach (var (channel, path) in a.Material.Textures) skin.Decode(pack, path, channel, true, true);
                foreach (var g in (a.Screen?.GlyphMeshes ?? Array.Empty<string?>()).Append(a.Screen?.TextMesh).Append(a.Screen?.BorderMesh))
                    if (g != null && !skin._meshes.ContainsKey(g)) skin._meshes[g] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(g)), g);
            }
            foreach (var t in skin.Doc.Parts)
                if (t.Mesh != null && !skin._meshes.ContainsKey(t.Mesh)) skin._meshes[t.Mesh] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(t.Mesh)), t.Mesh);
            if (skin.Doc.Sounds.Count > 0) skin._sounds = SkinSounds.Load(pack, skin.Doc.Sounds, skin._cues);
            return skin;
        }
        catch
        {
            skin.Destroy();
            throw;
        }
    }

    // ForgeCore's decoder. A texture set straight on a material (`compress`) is compressed to DXT on load, a quarter of the
    // VRAM of raw RGBA; a normal map is stored the way the game's own are on its gear shader (DXT5nm). Layer atlases stay
    // raw: layers copy frames out of them with CopyTexture, which needs the format of the uncompressed render target.
    void Decode(SkinPack pack, string path, TextureChannel channel, bool mips, bool compress)
    {
        bool linear = Linear(channel);
        var key = (linear ? "L:" : "S:") + path;
        if (_textures.ContainsKey(key)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var texture = ImageTextures.Decode(pack.ReadBytes(path), out var failure, linear, channel == TextureChannel.Normal && compress, mips, compress)
            ?? throw new SkinFormatException("bad-image", $"{path}: {failure}");
        texture.name = "ForgeSkin " + path;
        _textures[key] = ForgeCore.Assets.Keep(texture);
        if (compress && (texture.width % 4 != 0 || texture.height % 4 != 0))
            ForgeSkinPlugin.Warn($"{path}: {texture.width}x{texture.height} is not a multiple of 4, left uncompressed");
        ForgeSkinPlugin.Info($"{path} ({channel}): {texture.width}x{texture.height} {texture.format} in {watch.ElapsedMilliseconds} ms");
    }

    // A .forgeskinmesh (SFM1) read here, built by ForgeCore's one mesh entry.
    static Mesh ToMesh(ForgeSkinMesh m, string name)
    {
        var mesh = ForgeCore.Assets.Keep(MeshArrays.Build("ForgeSkin " + name, m.Positions, m.Normals, m.Uv0, m.Tangents, Array.ConvertAll(m.Indices, i => (int)i)));
        mesh.UploadMeshData(true);
        return mesh;
    }

    // The document as the player coloured it (`picks`: colour id -> linear RGB): a painted copy, or the pack's own when
    // the player changed nothing.
    public SkinDocument Painted(IReadOnlyDictionary<string, float[]>? picks)
    {
        if (picks == null || picks.Count == 0 || Doc.PlayerColors is not { Count: > 0 }) return Doc;
        var copy = SkinJson.Read(SkinJson.Write(Doc));
        foreach (var c in copy.PlayerColors!)
            if (picks.TryGetValue(c.Id, out var rgb)) c.Paint(copy, rgb);
        return copy;
    }

    public void Destroy()
    {
        foreach (var t in _textures.Values) Object.Destroy(t);
        foreach (var m in _meshes.Values) Object.Destroy(m);
        _sounds?.Dispose();
    }
}

// Installed packs, loaded on the first tick. Three sources:
// - the app's own skins folder: every pack in it, selection from its active.json;
// - skin mods: a mod folder directly under BepInEx/plugins with a `forgeskin.forge` marker lists its packs, one path per
//   line relative to that folder (blank lines and `#` comments skipped). Only the marker is looked for, one level deep;
// - code: other plugins call `SkinPacks.Register` with a pack path from their Load.
// A mod's or registered skin is active for its target unless active.json picks a skin for that target; among them the
// first by path wins; the player's choice in the in-game picker (ForgeSkin.choices.json) goes before both. In development mode a watcher marks the plugins folder dirty and everything is reloaded;
// otherwise changes wait for the next launch.
public static class SkinPacks
{
    public const string Marker = "forgeskin.forge";

    /// <summary>Adds a skin pack file to ForgeSkin; call from your plugin's Load. Loaded with the others on the first frame.</summary>
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

    // The colour the player gave one of a skin's player colours (null: back to the skin's own), kept in
    // ForgeSkin.colours.json and re-applied to live gear on the next tick like a pick.
    public static void Colour(string skinId, string colourId, float[]? rgb)
    {
        if (!Colours.TryGetValue(skinId, out var picks)) Colours[skinId] = picks = new();
        if (rgb == null) picks.Remove(colourId);
        else picks[colourId] = rgb;
        if (picks.Count == 0) Colours.Remove(skinId);
        _picked = true;
        ForgeSkinPlugin.Info($"colour {skinId}/{colourId}: {(rgb == null ? "default" : string.Join(", ", rgb))}");
        Save(_coloursFile, Colours, "skin-colours-write-failed");
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
    static readonly Dictionary<string, DateTime> Rejected = new(StringComparer.OrdinalIgnoreCase);
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
                Colours[skin] = picks.Where(p => p.Value is { Length: 3 } && p.Value.All(float.IsFinite)).ToDictionary(p => p.Key, p => p.Value);
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
        return file.EndsWith(".forgeskin", StringComparison.OrdinalIgnoreCase) || file.Equals("active.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals(SkinPacks.Marker, StringComparison.OrdinalIgnoreCase);
    }

    // Main thread. True when the skins were reloaded and live gear must be re-applied.
    public static bool Poll(float now)
    {
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
        ForgeSkinPlugin.Info("reloading skins");
        var byFile = new Dictionary<string, Skin>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<Skin>();
        var targets = new Dictionary<string, string>();
        var globals = new List<string>();
        try
        {
            foreach (var file in Discover())
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                ByFile.TryGetValue(file, out var old);
                // A pack whose new version is rejected keeps its old one.
                if (old != null && old.Stamp == stamp || Rejected.TryGetValue(file, out var bad) && bad == stamp)
                {
                    if (old != null) byFile[file] = old;
                    continue;
                }
                try { byFile[file] = Skin.Load(file, stamp); }
                catch (Exception e) when (e is not IOException)
                {
                    Rejected[file] = stamp;
                    var code = e is SkinFormatException f ? f.Code : e.GetType().Name;
                    ForgeSkinPlugin.Fail("skin-pack-rejected", $"{Path.GetFileName(file)}: {code}: {e.Message}; skin not loaded", e is SkinFormatException ? null : e);
                    if (old != null) byFile[file] = old;
                    continue;
                }
                loaded.Add(byFile[file]);
                Rejected.Remove(file);
            }
            ReadActive(targets, globals);
        }
        catch
        {
            foreach (var skin in loaded) skin.Destroy();
            throw;
        }

        foreach (var (file, old) in ByFile)
        {
            if (byFile.TryGetValue(file, out var kept) && kept == old) continue;
            old.Destroy();
            if (kept == null) ForgeSkinPlugin.Info($"removed {Path.GetFileName(file)}");
        }
        ByFile.Clear();
        foreach (var (file, skin) in byFile) ByFile[file] = skin;
        foreach (var (file, skin) in byFile)
            if (loaded.Contains(skin))
                ForgeSkinPlugin.Info($"loaded {Path.GetFileName(file)}: id {skin.Doc.Id}, {skin.Doc.Materials.Count} materials, {skin.Doc.Attachments.Count} attachments, {skin.Doc.Sounds.Count} sounds");

        ById.Clear();
        foreach (var skin in ByFile.Values)
            if (!ById.TryAdd(skin.Doc.Id, skin)) ForgeSkinPlugin.Fail("skin-pack-duplicate-id", $"two packs share id {skin.Doc.Id}; one is ignored");

        _base = targets;
        Globals.Clear();
        Globals.AddRange(globals);
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

    // Targets = _base with the player's choices on top; a choice whose skin is not loaded for that target is ignored.
    static void Resolve()
    {
        Targets.Clear();
        foreach (var (key, id) in _base) Targets[key] = id;
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
