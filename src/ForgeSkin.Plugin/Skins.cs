using System.Text.Json;
using ForgeSkin.Format;
using UnityEngine;
using UnityEngine.Rendering;
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
    readonly Dictionary<string, AudioClip> _sounds = new();

    Skin(SkinDocument doc, DateTime stamp) { Doc = doc; Stamp = stamp; }

    public Texture2D Texture(string path, bool linear) => _textures[(linear ? "L:" : "S:") + path];
    public Mesh Mesh(string path) => _meshes[path];
    public AudioClip Sound(string path) => _sounds[path];

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
                foreach (var l in m.Layers) skin.Decode(pack, l.Atlas!.Path, l.Channel, false, false);
            }
            foreach (var a in skin.Doc.Attachments)
            {
                if (a.Mesh != null && !skin._meshes.ContainsKey(a.Mesh)) skin._meshes[a.Mesh] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(a.Mesh)), a.Mesh);
                if (a.Material != null) foreach (var (channel, path) in a.Material.Textures) skin.Decode(pack, path, channel, true, true);
                foreach (var g in (a.Screen?.GlyphMeshes ?? Array.Empty<string?>()).Append(a.Screen?.TextMesh))
                    if (g != null && !skin._meshes.ContainsKey(g)) skin._meshes[g] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(g)), g);
            }
            foreach (var t in skin.Doc.Parts)
                if (t.Mesh != null && !skin._meshes.ContainsKey(t.Mesh)) skin._meshes[t.Mesh] = ToMesh(ForgeSkinMesh.Read(pack.ReadBytes(t.Mesh)), t.Mesh);
            foreach (var s in skin.Doc.Sounds)
                if (!skin._sounds.ContainsKey(s.File)) skin._sounds[s.File] = SoundOut.Decode(pack.ReadBytes(s.File), s.File);
            return skin;
        }
        catch
        {
            skin.Destroy();
            throw;
        }
    }

    // A texture set straight on a material (`compress`) is compressed to DXT on load, a quarter of the
    // VRAM of raw RGBA. Layer atlases stay raw: layers copy frames out of them with CopyTexture, which
    // needs the format of the uncompressed render target. Normal maps are stored the way
    // the game's own are (DXT5nm: x in alpha, y in green), which the gear shaders read.
    void Decode(SkinPack pack, string path, TextureChannel channel, bool mips, bool compress)
    {
        bool linear = Linear(channel);
        var key = (linear ? "L:" : "S:") + path;
        if (_textures.ContainsKey(key)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ForgeSkinPlugin.Info($"decoding {path} ({channel}, compress {compress})");
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mips, linear) { name = "ForgeSkin " + path, hideFlags = HideFlags.DontUnloadUnusedAsset };
        _textures[key] = texture;
        if (!ImageConversion.LoadImage(texture, pack.ReadBytes(path), !compress))
            throw new SkinFormatException("bad-image", $"{path} is not a PNG or JPG");
        if (!compress) return;
        if (texture.width % 4 != 0 || texture.height % 4 != 0)
        {
            ForgeSkinPlugin.Fail($"{path}: {texture.width}x{texture.height} is not a multiple of 4, left uncompressed");
            texture.Apply(false, true);
            return;
        }
        if (channel == TextureChannel.Normal)
        {
            // LoadImage gives RGB24 for an image without alpha; the swizzled copy needs an alpha channel.
            var pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = new Color32(255, p.g, 255, p.r);
            }
            var nm = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, true, true) { name = texture.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            nm.SetPixels32(pixels);
            nm.Apply(true);
            Object.Destroy(texture);
            _textures[key] = texture = nm;
        }
        ForgeSkinPlugin.Info($"{path}: {texture.width}x{texture.height} {texture.format} loaded in {watch.ElapsedMilliseconds} ms, compressing");
        texture.Compress(true);
        texture.Apply(false, true); // drop the CPU copy
        ForgeSkinPlugin.Info($"{path}: compressed to {texture.format} in {watch.ElapsedMilliseconds} ms");
    }

    static Mesh ToMesh(ForgeSkinMesh m, string name)
    {
        int vc = m.Positions.Length / 3;
        var mesh = new Mesh { name = "ForgeSkin " + name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (vc > 65535) mesh.indexFormat = IndexFormat.UInt32;
        var positions = new Vector3[vc];
        for (int i = 0; i < vc; i++) positions[i] = new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
        mesh.vertices = positions;
        if (m.Normals is { } n)
        {
            var normals = new Vector3[vc];
            for (int i = 0; i < vc; i++) normals[i] = new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]);
            mesh.normals = normals;
        }
        if (m.Uv0 is { } uv)
        {
            var uvs = new Vector2[vc];
            for (int i = 0; i < vc; i++) uvs[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
            mesh.uv = uvs;
        }
        if (m.Tangents is { } t)
        {
            var tangents = new Vector4[vc];
            for (int i = 0; i < vc; i++) tangents[i] = new Vector4(t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]);
            mesh.tangents = tangents;
        }
        var triangles = new int[m.Indices.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = (int)m.Indices[i];
        mesh.triangles = triangles;
        if (m.Normals == null) mesh.RecalculateNormals();
        if (m.Tangents == null && m.Uv0 != null) mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return mesh;
    }

    public void Destroy()
    {
        foreach (var t in _textures.Values) Object.Destroy(t);
        foreach (var m in _meshes.Values) Object.Destroy(m);
        foreach (var c in _sounds.Values) Object.Destroy(c);
    }
}

// Installed packs, loaded on the first tick. Three sources:
// - the app's own skins folder: every pack in it, selection from its active.json;
// - skin mods: a mod folder directly under BepInEx/plugins with a `forgeskin.forge` marker lists its packs, one path per
//   line relative to that folder (blank lines and `#` comments skipped). Only the marker is looked for, one level deep;
// - code: other plugins call `SkinPacks.Register` with a pack path from their Load.
// A mod's or registered skin is active for its target unless active.json picks a skin for that target; among them the
// first by path wins. In development mode a watcher marks the plugins folder dirty and everything is reloaded;
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

    static readonly Dictionary<string, Skin> ByFile = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> Rejected = new(StringComparer.OrdinalIgnoreCase);
    static string _dir = "", _plugins = "";
    internal static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher? _watcher;
    static int _events, _seen, _busyRetries;
    static float _due; // 0 = load on the first tick; < 0 = nothing pending

    public static void Watch(string dir, string plugins, bool hotReload)
    {
        (_dir, _plugins) = (dir, plugins);
        Directory.CreateDirectory(dir);
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
        catch (Exception e) { _busyRetries = 0; ForgeSkinPlugin.Fail($"skins folder: {e.Message}; the skins loaded before stay"); }
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
                    ForgeSkinPlugin.Fail($"{Path.GetFileName(file)}: {code}: {e.Message}; skin not loaded");
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
            if (!ById.TryAdd(skin.Doc.Id, skin)) ForgeSkinPlugin.Fail($"two packs share id {skin.Doc.Id}; one is ignored");

        Targets.Clear();
        foreach (var (key, id) in targets) Targets[key] = id;
        Globals.Clear();
        Globals.AddRange(globals);
        var own = Path.GetFullPath(_dir) + Path.DirectorySeparatorChar;
        foreach (var (file, skin) in ByFile.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFullPath(file).StartsWith(own, StringComparison.OrdinalIgnoreCase) || skin.Doc.Target.Key() is not { } key) continue;
            if (Targets.TryAdd(key, skin.Doc.Id)) ForgeSkinPlugin.Info($"{key}: {skin.Doc.Id} from {Path.GetFileName(Path.GetDirectoryName(file))} is active");
            else if (Targets[key] != skin.Doc.Id) ForgeSkinPlugin.Info($"{key}: {skin.Doc.Id} from {Path.GetFileName(Path.GetDirectoryName(file))} not used, {Targets[key]} is active");
        }
        ForgeSkinPlugin.Info($"active: {Targets.Count} target skins, {Globals.Count} global skins");
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
                if (!pack.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase)) ForgeSkinPlugin.Fail($"{Path.GetFileName(mod)}/{SkinPacks.Marker}: {line} is not a {SkinPack.Extension} pack");
                else if (File.Exists(pack)) files.Add(pack);
                else ForgeSkinPlugin.Fail($"{Path.GetFileName(mod)}/{SkinPacks.Marker}: {line} not found");
            }
        }
        lock (Registered)
            foreach (var pack in Registered)
                if (File.Exists(pack)) files.Add(pack);
                else ForgeSkinPlugin.Fail($"registered pack {pack} not found");
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
                else ForgeSkinPlugin.Fail($"active.json: bad target entry {entry.Name}");
        if (json.RootElement.TryGetProperty("global", out var global))
            foreach (var id in global.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String) globals.Add(id.GetString()!);
    }
}
