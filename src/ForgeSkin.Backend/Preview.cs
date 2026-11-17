using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET.Texture;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// A transform of a part (its root and every sub-transform, not descending into other parts mounted on it): Unity
// name, child path from the part root as Transform.Find takes it (null for the root, as PartTransform.Path), the GLB node
// that is it, its parent's GLB node, vanilla local pose in Unity space (rotation as quaternion x, y, z, w) and the
// renderer nodes at or under it.
public sealed record PreviewTransform(string Name, string? Path, string Node, string? Parent, float[] Position, float[] Rotation, float[] Scale, List<string> Meshes);
public sealed record PreviewPart(string Id, List<string> Nodes, List<PreviewTransform> Transforms, PreviewAmmo? Ammo);
// How an ammo part is shown (Catalog.AmmoLayout): the GLB node moved by a world offset (Unity space) and the nodes hidden.
public sealed record PreviewAmmo(string Node, float[] Offset, List<string> Hidden);
// Damaged: some game textures could not be read (a game file rewritten outside the game). Modified: some are not the game's
// own (another mod rewrote the game files; GameAssets.IsVanilla). Neither kind of preview is cached.
public sealed record PreviewResult(string Glb, List<PreviewPart> Parts, Dictionary<string, Dictionary<TextureChannel, string>> Textures,
    Dictionary<string, PreviewMaterial> Params, bool Damaged = false, bool Modified = false);

// How the game draws a material. Kind: opaque; cutout (alpha test); glass (alpha-blended lens: Tint alpha is its opacity,
// albedo if any is lens dirt); reticle (holographic sight surface: invisible except its reticle layers, drawn additively as
// texture x colour). Colours are the material's values (Tint = _Color RGBA), except its glow (_EmissiveColor or
// _EmissionColor RGB), which is in a skin's units (MaterialParams.GlowFromGame), as the editor previews and edits it, and a
// reticle layer's colour, which is a skin's Reticle colour and intensity (ColourSpace.FromGlow of _ReticuleColor<L>, alpha
// dropped). Reticles: every layer the material has, every value set; the pictures are in PreviewResult.Textures.
public sealed record PreviewMaterial(string Shader, int RenderQueue, string Kind, bool? EmissiveEnabled, float[]? EmissiveColor,
    float? EmissiveIntensity, float? Metallic, float? Smoothness, float[]? Tint, Dictionary<TextureChannel, Reticle>? Reticles);

// Assembled item -> GLB (full transform tree, Unity -> glTF) + game textures as PNG + material parameters, cached per game
// build when the item is first asked for. Textures go to the build's shared store (PreviewCache.Textures) named by content,
// so items that share a texture share its file.
public static class Preview
{
    public static readonly Dictionary<string, TextureChannel> ChannelProps = Enum.GetValues<TextureChannel>().ToDictionary(c => c.Property());

    public static PreviewResult Weapon(GameAssets g, string id, long requestId)
    {
        var item = Catalog.Item(Catalog.Get(g), id);
        var dir = PreviewCache.Dir(g, id);
        var cached = Path.Combine(dir, "preview.json");
        if (File.Exists(cached))
            return MapPaths(JsonSerializer.Deserialize<PreviewResult>(File.ReadAllText(cached), Program.Json)!, p => Path.GetFullPath(Path.Combine(dir, p)));

        var clock = System.Diagnostics.Stopwatch.StartNew();
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
            if (Assembly.IsPartRoot(n)) (extras ??= new())["partRoot"] = n.Part;
            if (extras != null) extras["transform"] = n.Name;
            int index = glb.Node(n.Name, n.Position, n.Rotation, n.Scale, mesh, children, extras);
            nodeName[n] = glb.NodeName(index);
            if (part != null) (parts.TryGetValue(part, out var l) ? l : parts[part] = new()).Add(nodeName[n]);
            return index;
        }
        int rootIndex = Emit(root);
        Directory.CreateDirectory(dir);
        var glbPath = Path.Combine(dir, id + ".glb");
        // A file being shown is not rewritten.
        if (!File.Exists(glbPath))
        {
            using var file = new AtomicFile(glbPath);
            glb.Save(file.Temp, rootIndex);
            file.Commit();
        }

        var textures = new Dictionary<string, Dictionary<TextureChannel, string>>();
        var parameters = new Dictionary<string, PreviewMaterial>();
        var written = new Dictionary<(string, long), string>();
        var store = PreviewCache.Textures(g);
        // Textures are read here (the game reader is single-threaded) and decoded and written as PNG in parallel below.
        var pending = new List<((TextureFile, byte[]) Loaded, TextureChannel Channel, string Png)>();
        int done = 0;
        bool damaged = false, modified = false;
        // A texture a rewritten game file no longer holds is left out (the material falls back as if it had none) and logged.
        bool Read(Action read, string what)
        {
            try { read(); return true; }
            catch (BackendException e) when (e.Code == GameAssets.Damaged)
            {
                damaged = true;
                Log.Write($"preview {id}: {what} unreadable: {e.Message}");
                return false;
            }
        }
        foreach (var (name, m) in materials)
        {
            Program.Progress(requestId, done++, materials.Count, name);
            var data = Unity.ReadMaterial(g, m);
            var channels = new Dictionary<TextureChannel, string>();
            string? Export(TexRef t, TextureChannel channel, string suffix)
            {
                if (!written.TryGetValue((t.File.path, t.PathId), out var png))
                {
                    (TextureFile Tex, byte[] Pixels) loaded = default;
                    if (!Read(() => loaded = Unity.LoadTexture(g, t), $"{name} {suffix} texture")) return null;
                    var (key, texName) = (TextureKey(loaded), loaded.Tex!.m_Name);
                    if (g.IsVanilla(key) == false)
                    {
                        modified = true;
                        Log.Write($"preview {id}: {name} {suffix} texture {texName} is not the game's own");
                    }
                    png = Path.Combine(store, $"{Safe(texName)}_{key}_{channel}.png");
                    written[(t.File.path, t.PathId)] = png;
                    if (!File.Exists(png) && !pending.Exists(p => p.Png == png)) pending.Add((loaded, channel, png));
                }
                return png;
            }
            var info = Params(data);
            parameters[name] = info;
            foreach (var (prop, channel) in ChannelProps)
            {
                if (data.Textures.TryGetValue(prop, out var t) && Export(t, channel, channel.ToString()) is { } png) channels[channel] = png;
            }
            if (!channels.ContainsKey(TextureChannel.Albedo) && info.Kind is "opaque" or "cutout")
            {
                // The item's own bake (a file being shown is not rewritten).
                var albedo = Path.Combine(dir, "tex", $"{Safe(name)}_{TextureChannel.Albedo}.png");
                var ms = Path.Combine(dir, "tex", $"{Safe(name)}_{TextureChannel.MetallicSmoothness}.png");
                if (data.Shader == BlendHS.Shader && data.Textures.ContainsKey("_AOCurveTex")
                    && ((File.Exists(albedo) && File.Exists(ms)) || Read(() => BlendHS.Bake(g, data, uses[name], albedo, ms), $"{name} AO curve texture")))
                    (channels[TextureChannel.Albedo], channels[TextureChannel.MetallicSmoothness]) = (albedo, ms);
                else
                {
                    // No albedo texture assigned: Unity samples its default white (tint and emission come from params).
                    var white = channels[TextureChannel.Albedo] = Path.Combine(store, "white.png");
                    if (!File.Exists(white))
                    {
                        using var bmp = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                        bmp.Erase(SKColors.White);
                        Images.SavePng(bmp, white);
                    }
                }
            }
            textures[name] = channels;
        }
        Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            p => SaveTexture(p.Loaded, p.Channel, p.Png));
        Log.Write($"preview {id}: {written.Count} textures in {clock.ElapsedMilliseconds} ms");

        var transforms = new Dictionary<string, List<PreviewTransform>>();
        foreach (var (part, path, n) in Assembly.PartNodes(root, activeOnly: true))
        {
            var under = new List<string>();
            void Collect(Node c) { if (!c.Active || (c != n && c.Part != null && c.Part != part)) return; if (partOf.ContainsKey(c)) under.Add(nodeName[c]); c.Children.ForEach(Collect); }
            Collect(n);
            (transforms.TryGetValue(part, out var list) ? list : transforms[part] = new()).Add(new PreviewTransform(n.Name, path, nodeName[n],
                path == null ? null : nodeName[n.Parent!],
                [n.Position.X, n.Position.Y, n.Position.Z], [n.Rotation.X, n.Rotation.Y, n.Rotation.Z, n.Rotation.W], [n.Scale.X, n.Scale.Y, n.Scale.Z], under));
        }
        var ammo = Catalog.AmmoLayout(root, n => meshes[(n.Mesh!.File.path, n.Mesh.PathId)].Data).ToDictionary(a => a.Part,
            a => new PreviewAmmo(nodeName[a.Shown], [a.Offset.X, a.Offset.Y, a.Offset.Z], a.Hidden.Select(h => nodeName[h]).ToList()));
        var order = Assembly.Slots.Select(s => s.Id).Concat(["ammo_speedloader", "ammo_bullet", "ammo_shell"]).ToList();
        var result = new PreviewResult(glbPath, parts.Keys.Union(transforms.Keys).OrderBy(p => order.IndexOf(p) is var i && i < 0 ? 99 : i)
            .Select(p => new PreviewPart(p, parts.GetValueOrDefault(p) ?? new(), transforms.GetValueOrDefault(p) ?? new(), ammo.GetValueOrDefault(p))).ToList(), textures, parameters, damaged, modified);
        // Stored relative to the cache folder, so a moved data dir (portable GTFO_UserData) keeps its cache. A damaged or
        // modified one is rebuilt next time, so restored game files (Steam's file check keeps the build id) show again.
        if (!damaged && !modified) AtomicFile.WriteText(cached, JsonSerializer.Serialize(MapPaths(result, p => Path.GetRelativePath(dir, p)), Program.Json));
        return result;
    }

    static PreviewResult MapPaths(PreviewResult r, Func<string, string> map) => r with
    {
        Glb = map(r.Glb),
        Textures = r.Textures.ToDictionary(m => m.Key, m => m.Value.ToDictionary(c => c.Key, c => map(c.Value))),
    };

    public static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static PreviewMaterial Params(MaterialData d)
    {
        float? F(string k) => d.Floats.TryGetValue(k, out var v) ? v : null;
        float[]? C(string k, int n) => d.Colors.TryGetValue(k, out var v) ? v[..n] : null;
        bool Has(string k) => d.Keywords.Contains(k);
        // Standard-style blend setup: _Mode 1 cutout, 2 fade, 3 transparent; a custom queue overrides the shader's.
        var kind = d.Shader.Contains("HolographicSight") ? "reticle"
            : d.Shader.Contains("Glass") || Has("_ALPHABLEND_ON") || Has("_ALPHAPREMULTIPLY_ON") || F("_Mode") >= 2 || d.RenderQueue >= 2500 ? "glass"
            : Has("_ALPHATEST_ON") || F("_Mode") == 1 || d.RenderQueue >= 2450 ? "cutout"
            : "opaque";
        int queue = d.RenderQueue >= 0 ? d.RenderQueue : kind switch { "opaque" => 2000, "cutout" => 2450, _ => 3000 };
        var glowName = d.Colors.ContainsKey("_EmissiveColor") ? "_EmissiveColor" : "_EmissionColor";
        var (glow, intensity) = C(glowName, 3) is { } g ? MaterialParams.GlowFromGame(g[0], g[1], g[2], d.Hdr.Contains(glowName)) : default;
        Dictionary<TextureChannel, Reticle>? reticles = null;
        if (kind == "reticle")
            foreach (var channel in TextureChannels.Reticles)
            {
                // A layer the material holds a picture or a colour for.
                var l = channel.Layer();
                if (!d.Textures.ContainsKey(channel.Property()) && C("_ReticuleColor" + l, 3) == null) continue;
                var (colour, gain) = C("_ReticuleColor" + l, 3) is { } c ? ForgeVisual.ColourSpace.FromGlow(c[0], c[1], c[2], d.Hdr.Contains("_ReticuleColor" + l)) : (new float[] { 1, 1, 1 }, 1f);
                (reticles ??= new())[channel] = new Reticle
                {
                    Color = colour,
                    Intensity = gain,
                    Scale = F("_ReticuleScale" + l) ?? 1,
                    Offset = F("_ReticuleOffset" + l) ?? 0,
                    Visible = !(F("_Fade" + l) <= 0),
                };
            }
        return new PreviewMaterial(d.Shader, queue, kind, F("_EnableEmissive") is { } e ? e > 0.5f : null,
            glow, glow != null ? intensity : null, F("_Metallic"), F("_Glossiness"), C("_Color", 4), reticles);
    }

    // A game texture's identity by content: size, format and encoded data (the first 16 hex digits of their SHA-256). It
    // names the stored PNG and is what the list of the game's own textures holds.
    public static string TextureKey((TextureFile Tex, byte[] Pixels) t)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes($"{t.Tex.m_Width}x{t.Tex.m_Height}:{t.Tex.m_TextureFormat}:"));
        hash.AppendData(t.Pixels);
        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    // The keys of every texture a preview of any catalog item can read (`--write-vanilla-textures`, run by the release script).
    public static SortedSet<string> TextureKeys(GameAssets g)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string, long)>();
        foreach (var item in Catalog.Get(g))
            foreach (var (node, _) in Catalog.Renderers(item.Build(g)))
                foreach (var m in node.Materials.Where(m => m.PathId != 0))
                    foreach (var (prop, t) in Unity.ReadMaterial(g, m).Textures)
                        if ((ChannelProps.ContainsKey(prop) || prop.StartsWith("_Reticule")) && seen.Add((t.File.path, t.PathId)))
                            keys.Add(TextureKey(Unity.LoadTexture(g, t)));
        return keys;
    }

    // Writes a game texture as PNG; Unity DXT5nm normal maps (x in A, y in G) become ordinary RGB normal maps.
    static void SaveTexture((TextureFile, byte[]) loaded, TextureChannel channel, string path)
    {
        var (w, h, rgba, _) = Unity.DecodeTexture(loaded);
        if (channel == TextureChannel.Normal && IsDxtNm(rgba))
            for (int i = 0; i < rgba.Length; i += 4)
            {
                float x = rgba[i + 3] / 127.5f - 1, y = rgba[i + 1] / 127.5f - 1;
                float z = MathF.Sqrt(Math.Max(0, 1 - x * x - y * y));
                rgba[i] = rgba[i + 3];
                rgba[i + 2] = (byte)Math.Round((z + 1) * 127.5f);
                rgba[i + 3] = 255;
            }
        using var bmp = Images.FromRgba(w, h, rgba);
        Images.SavePng(bmp, path);
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

    public static SKBitmap Load(string path)
    {
        var decoded = SKBitmap.Decode(path) ?? throw new SkinFormatException("bad-image", $"{path} is not a readable image");
        if (decoded.Copy(SKColorType.Rgba8888) is not { } copy) return decoded;
        decoded.Dispose();
        return copy;
    }

    // Straight (not premultiplied) RGBA rows from the top, as the game's decoder reads the file.
    public static SKBitmap Decode(byte[] image, string what)
    {
        using var codec = SKCodec.Create(new MemoryStream(image)) ?? throw new SkinFormatException("bad-image", $"{what}: not a readable image");
        return SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    }

    public static (int Width, int Height) Size(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new SkinFormatException("bad-image", $"{path} is not a readable image");
        return (codec.Info.Width, codec.Info.Height);
    }

    // Whole or not at all: a file that exists is complete (the preview store reuses any file it finds).
    public static void SavePng(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var file = new AtomicFile(path);
        using (var f = File.Create(file.Temp)) data.SaveTo(f);
        file.Commit();
    }

    public static SKBitmap Resize(SKBitmap src, int w, int h) =>
        src.Width == w && src.Height == h ? src.Copy() : src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
}
