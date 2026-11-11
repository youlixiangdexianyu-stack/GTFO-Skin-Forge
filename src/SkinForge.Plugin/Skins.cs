using System.Text.Json;
using SkinForge.Format;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SkinForge.Plugin;

// One installed pack, decoded once: every texture and mesh it references lives on the GPU; the zip is closed again
// so the backend can overwrite it while the game runs.
sealed class Skin
{
    public readonly SkinDocument Doc;
    public readonly DateTime Stamp;
    readonly Dictionary<string, Texture2D> _textures = new();
    readonly Dictionary<string, Mesh> _meshes = new();

    Skin(SkinDocument doc, DateTime stamp) { Doc = doc; Stamp = stamp; }

    public Texture2D Texture(string path, bool linear) => _textures[(linear ? "L:" : "S:") + path];
    public Mesh Mesh(string path) => _meshes[path];

    public static bool Linear(TextureChannel channel) => channel is TextureChannel.Normal or TextureChannel.MetallicSmoothness;

    public static Skin Load(string file, DateTime stamp)
    {
        using var pack = SkinPack.Open(file);
        var skin = new Skin(pack.Manifest, stamp);
        try
        {
            foreach (var m in skin.Doc.Materials)
            {
                foreach (var (channel, path) in m.Textures) skin.Decode(pack, path, Linear(channel), true);
                foreach (var l in m.Layers) skin.Decode(pack, l.Atlas!.Path, Linear(l.Channel), false);
            }
            foreach (var a in skin.Doc.Attachments)
            {
                if (a.Mesh != null && !skin._meshes.ContainsKey(a.Mesh)) skin._meshes[a.Mesh] = ToMesh(SfMesh.Read(pack.ReadBytes(a.Mesh)), a.Mesh);
                if (a.Material != null) foreach (var (channel, path) in a.Material.Textures) skin.Decode(pack, path, Linear(channel), true);
                if (a.Screen?.GlyphAtlas is { } glyphs) skin.Decode(pack, glyphs, false, true);
            }
            return skin;
        }
        catch
        {
            skin.Destroy();
            throw;
        }
    }

    void Decode(SkinPack pack, string path, bool linear, bool mips)
    {
        var key = (linear ? "L:" : "S:") + path;
        if (_textures.ContainsKey(key)) return;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mips, linear) { name = "SkinForge " + path, hideFlags = HideFlags.DontUnloadUnusedAsset };
        _textures[key] = texture;
        if (!ImageConversion.LoadImage(texture, pack.ReadBytes(path), true))
            throw new SkinFormatException("bad-image", $"{path} is not a PNG or JPG");
    }

    static Mesh ToMesh(SfMesh m, string name)
    {
        int vc = m.Positions.Length / 3;
        var mesh = new Mesh { name = "SkinForge " + name, hideFlags = HideFlags.DontUnloadUnusedAsset };
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
    }
}

// The skins folder: packs by file, the active selection from active.json, and the watcher that marks it dirty.
static class Skins
{
    public static readonly Dictionary<string, Skin> ById = new();
    public static readonly Dictionary<uint, string> Weapons = new();
    public static readonly List<string> Globals = new();

    static readonly Dictionary<string, Skin> ByFile = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> Rejected = new(StringComparer.OrdinalIgnoreCase);
    static string _dir = "";
    static FileSystemWatcher? _watcher;
    static int _events, _seen, _busyRetries;
    static float _due; // 0 = load on the first tick; < 0 = nothing pending

    public static void Watch(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler touched = (_, _) => Interlocked.Increment(ref _events);
        _watcher.Changed += touched;
        _watcher.Created += touched;
        _watcher.Deleted += touched;
        _watcher.Renamed += (_, _) => Interlocked.Increment(ref _events);
        _watcher.EnableRaisingEvents = true;
    }

    // Main thread. True when the skins were reloaded and live gear must be re-applied.
    public static bool Poll(float now)
    {
        int events = Volatile.Read(ref _events);
        if (events != _seen) { _seen = events; _due = now + 0.3f; return false; }
        if (_due < 0 || now < _due) return false;
        _due = -1;
        try { Reload(); }
        catch (IOException) when (++_busyRetries < 10) { _due = now + 0.5f; } // the backend is still writing
        catch (Exception e) { _busyRetries = 0; SkinForgePlugin.Fail($"skins folder: {e.Message}"); }
        return true;
    }

    static void Reload()
    {
        var files = Directory.GetFiles(_dir, "*" + SkinPack.Extension);
        foreach (var gone in ByFile.Keys.Where(f => !files.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            ByFile[gone].Destroy();
            ByFile.Remove(gone);
            SkinForgePlugin.Info($"removed {Path.GetFileName(gone)}");
        }
        foreach (var file in files)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (ByFile.TryGetValue(file, out var old) && old.Stamp == stamp) continue;
            if (Rejected.TryGetValue(file, out var bad) && bad == stamp) continue;
            Skin skin;
            try { skin = Skin.Load(file, stamp); }
            catch (IOException) { throw; }
            catch (Exception e)
            {
                Rejected[file] = stamp;
                var code = e is SkinFormatException f ? f.Code : e.GetType().Name;
                SkinForgePlugin.Fail($"{Path.GetFileName(file)}: {code}: {e.Message}; skin not loaded");
                continue;
            }
            old?.Destroy();
            ByFile[file] = skin;
            Rejected.Remove(file);
            SkinForgePlugin.Info($"loaded {Path.GetFileName(file)}: id {skin.Doc.Id}, {skin.Doc.Materials.Count} materials, {skin.Doc.Attachments.Count} attachments");
        }
        _busyRetries = 0;

        ById.Clear();
        foreach (var skin in ByFile.Values)
            if (!ById.TryAdd(skin.Doc.Id, skin)) SkinForgePlugin.Fail($"two packs share id {skin.Doc.Id}; one is ignored");

        Weapons.Clear();
        Globals.Clear();
        var active = Path.Combine(_dir, "active.json");
        if (!File.Exists(active)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(active));
        if (json.RootElement.TryGetProperty("weapons", out var weapons))
            foreach (var entry in weapons.EnumerateObject())
                if (uint.TryParse(entry.Name, out var archetype) && entry.Value.ValueKind == JsonValueKind.String) Weapons[archetype] = entry.Value.GetString()!;
                else SkinForgePlugin.Fail($"active.json: bad weapon entry {entry.Name}");
        if (json.RootElement.TryGetProperty("global", out var global))
            foreach (var id in global.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String) Globals.Add(id.GetString()!);
        SkinForgePlugin.Info($"active: {Weapons.Count} weapon skins, {Globals.Count} global skins");
    }
}
