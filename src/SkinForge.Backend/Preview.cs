using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using SkinForge.Format;

namespace SkinForge.Backend;

// A transform of a part (its root and every sub-transform, not descending into other parts mounted on it): Unity
// name, the GLB node that is it, its parent's GLB node, vanilla local pose in Unity space (rotation as quaternion
// x, y, z, w) and the renderer nodes at or under it.
public sealed record PreviewTransform(string Name, string Node, string? Parent, float[] Position, float[] Rotation, float[] Scale, List<string> Meshes);
public sealed record PreviewPart(string Id, List<string> Nodes, List<PreviewTransform> Transforms);
public sealed record PreviewResult(string Glb, List<PreviewPart> Parts, Dictionary<string, Dictionary<TextureChannel, string>> Textures,
    Dictionary<string, MaterialParams> Params);

// Assembled item -> GLB (full transform tree, Unity -> glTF) + game textures as PNG + material parameters, cached per game build.
public static class Preview
{
    public static readonly Dictionary<string, TextureChannel> ChannelProps = new()
    {
        ["_MainTex"] = TextureChannel.Albedo,
        ["_BumpMap"] = TextureChannel.Normal,
        ["_MetallicGlossMap"] = TextureChannel.MetallicSmoothness,
        ["_EmissiveMap"] = TextureChannel.Emissive,
    };

    public static PreviewResult Weapon(GameAssets g, string id, long requestId)
    {
        var item = Catalog.Item(Catalog.Get(g), id);
        var dir = PreviewCache.Dir(g, id);
        var cached = Path.Combine(dir, "preview.json");
        if (File.Exists(cached)) return JsonSerializer.Deserialize<PreviewResult>(File.ReadAllText(cached), Program.Json)!;

        var root = item.Build(g);
        var partOf = Catalog.Renderers(root).ToDictionary(r => r.Node, r => r.Part);
        var glb = new GlbWriter();
        var parts = new Dictionary<string, List<string>>();
        var materials = new Dictionary<string, MaterialRef>();
        var meshes = new Dictionary<(string, long), (int Index, MeshData Data)>();
        var uses = new Dictionary<string, List<(MeshData, int)>>();
        var nodeName = new Dictionary<Node, string>();

        int Emit(Node n)
        {
            var children = n.Children.Where(c => c.Active).Select(Emit).ToList();
            int? mesh = null;
            JsonObject? extras = null;
            if (partOf.TryGetValue(n, out var part))
            {
                var key = (n.Mesh!.File.path, n.Mesh.PathId);
                if (!meshes.TryGetValue(key, out var mi))
                {
                    var data = Unity.ReadMesh(g, n.Mesh);
                    meshes[key] = mi = (glb.Mesh(data, n.Materials.Select(m => m.Name).ToList()), data);
                }
                mesh = mi.Index;
                for (int s = 0; s < n.Materials.Count; s++)
                {
                    var m = n.Materials[s];
                    if (m.PathId == 0) continue;
                    materials.TryAdd(m.Name, m);
                    (uses.TryGetValue(m.Name, out var u) ? u : uses[m.Name] = new()).Add((mi.Data, s));
                }
                extras = new JsonObject { ["part"] = part };
            }
            if (n.Part != null) (extras ??= new())["partRoot"] = n.Part;
            if (extras != null) extras["transform"] = n.Name;
            int index = glb.Node(n.Name, n.Position, n.Rotation, n.Scale, mesh, children, extras);
            nodeName[n] = glb.NodeName(index);
            if (part != null) (parts.TryGetValue(part, out var l) ? l : parts[part] = new()).Add(nodeName[n]);
            return index;
        }
        int rootIndex = Emit(root);
        Directory.CreateDirectory(dir);
        var glbPath = Path.Combine(dir, id + ".glb");
        glb.Save(glbPath, rootIndex);

        var textures = new Dictionary<string, Dictionary<TextureChannel, string>>();
        var parameters = new Dictionary<string, MaterialParams>();
        var written = new Dictionary<(string, long), string>();
        int done = 0;
        foreach (var (name, m) in materials)
        {
            Program.Progress(requestId, done++, materials.Count, name);
            var data = Unity.ReadMaterial(g, m);
            parameters[name] = Params(data);
            var channels = new Dictionary<TextureChannel, string>();
            foreach (var (prop, channel) in ChannelProps)
            {
                if (!data.Textures.TryGetValue(prop, out var t)) continue;
                if (!written.TryGetValue((t.File.path, t.PathId), out var png))
                {
                    png = Path.Combine(dir, "tex", $"{Safe(name)}_{channel}.png");
                    SaveTexture(g, t, channel, png);
                    written[(t.File.path, t.PathId)] = png;
                }
                channels[channel] = png;
            }
            if (!channels.ContainsKey(TextureChannel.Albedo))
            {
                var albedo = channels[TextureChannel.Albedo] = Path.Combine(dir, "tex", $"{Safe(name)}_{TextureChannel.Albedo}.png");
                if (data.Shader == BlendHS.Shader && data.Textures.ContainsKey("_AOCurveTex"))
                    BlendHS.Bake(g, data, uses[name], albedo, channels[TextureChannel.MetallicSmoothness] = Path.Combine(dir, "tex", $"{Safe(name)}_{TextureChannel.MetallicSmoothness}.png"));
                else
                {
                    // No albedo texture assigned: Unity samples its default white (tint and emission come from params).
                    using var white = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    white.Erase(SKColors.White);
                    Images.SavePng(white, albedo);
                }
            }
            textures[name] = channels;
        }

        var transforms = new Dictionary<string, List<PreviewTransform>>();
        foreach (var partRoot in root.All().Where(n => n.Part != null && n.Active))
        {
            var list = transforms[partRoot.Part!] = new();
            void Walk(Node n)
            {
                if (!n.Active || (n != partRoot && n.Part != null)) return;
                var under = new List<string>();
                void Collect(Node c) { if (!c.Active || (c != n && c.Part != null)) return; if (partOf.ContainsKey(c)) under.Add(nodeName[c]); c.Children.ForEach(Collect); }
                Collect(n);
                list.Add(new PreviewTransform(n.Name, nodeName[n], n == partRoot ? null : nodeName[n.Parent!],
                    [n.Position.X, n.Position.Y, n.Position.Z], [n.Rotation.X, n.Rotation.Y, n.Rotation.Z, n.Rotation.W], [n.Scale.X, n.Scale.Y, n.Scale.Z], under));
                n.Children.ForEach(Walk);
            }
            Walk(partRoot);
        }
        var order = Assembly.Slots.Select(s => s.Id).Concat(["ammo_speedloader", "ammo_bullet", "ammo_shell"]).ToList();
        var result = new PreviewResult(glbPath, parts.Keys.Union(transforms.Keys).OrderBy(p => order.IndexOf(p) is var i && i < 0 ? 99 : i)
            .Select(p => new PreviewPart(p, parts.GetValueOrDefault(p) ?? new(), transforms.GetValueOrDefault(p) ?? new())).ToList(), textures, parameters);
        File.WriteAllText(cached, JsonSerializer.Serialize(result, Program.Json));
        return result;
    }

    public static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static MaterialParams Params(MaterialData d)
    {
        float? F(string k) => d.Floats.TryGetValue(k, out var v) ? v : null;
        float[]? C(string k, int n) => d.Colors.TryGetValue(k, out var v) ? v[..n] : null;
        return new MaterialParams
        {
            EmissiveEnabled = F("_EnableEmissive") is { } e ? e > 0.5f : null,
            EmissiveColor = C("_EmissiveColor", 3),
            Metallic = F("_Metallic"),
            Smoothness = F("_Glossiness"),
            Tint = C("_Color", 4),
        };
    }

    // Writes a game texture as PNG; Unity DXT5nm normal maps (x in A, y in G) become ordinary RGB normal maps.
    public static (int Width, int Height) SaveTexture(GameAssets g, TexRef t, TextureChannel channel, string path)
    {
        var (w, h, rgba, _) = Unity.ReadTexture(g, t);
        if (channel == TextureChannel.Normal && IsDxtNm(rgba))
            for (int i = 0; i < rgba.Length; i += 4)
            {
                float x = rgba[i + 3] / 127.5f - 1, y = rgba[i + 1] / 127.5f - 1;
                float z = MathF.Sqrt(Math.Max(0, 1 - x * x - y * y));
                rgba[i] = rgba[i + 3];
                rgba[i + 2] = (byte)Math.Round((z + 1) * 127.5f);
                rgba[i + 3] = 255;
            }
        Images.SavePng(Images.FromRgba(w, h, rgba), path);
        return (w, h);
    }

    // DXT5nm keeps R at 1 (x lives in A); an ordinary normal map has varied R.
    static bool IsDxtNm(byte[] rgba)
    {
        int ones = 0, n = 0;
        for (int i = 0; i < rgba.Length; i += 4 * 97, n++) if (rgba[i] > 250) ones++;
        return ones > n * 0.95;
    }
}

// SkiaSharp helpers shared by preview, matching and pack building.
public static class Images
{
    public static SKBitmap FromRgba(int w, int h, byte[] rgba)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        return bmp;
    }

    public static SKBitmap Load(string path) =>
        SKBitmap.Decode(path) is { } b ? b.Copy(SKColorType.Rgba8888) ?? b : throw new SkinFormatException("bad-image", $"{path} is not a readable image");

    public static (int Width, int Height) Size(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new SkinFormatException("bad-image", $"{path} is not a readable image");
        return (codec.Info.Width, codec.Info.Height);
    }

    public static void SavePng(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    public static SKBitmap Resize(SKBitmap src, int w, int h) =>
        src.Width == w && src.Height == h ? src.Copy() : src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
}
