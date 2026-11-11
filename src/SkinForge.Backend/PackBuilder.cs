using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkinForge.Format;

namespace SkinForge.Backend;

// Project -> .gtfskin: textures resized by size mode (aspect kept), layer frames packed into atlases, glyph strips and
// meshes copied, manifest validated by opening the written pack.
public static class PackBuilder
{
    sealed record FrameRect(int X, int Y, int W, int H, int TextureWidth, int TextureHeight);

    // `baseline`: the game's texture per material and channel (preview.weapon), for "original" size and layer targets.
    public static object Build(SkinDocument project, string projectPath, string outPath, string sizeMode, List<CatalogItem> catalog,
        Dictionary<string, Dictionary<TextureChannel, string>> baseline, long requestId)
    {
        var doc = SkinJson.Read(SkinJson.Write(project));
        doc.Editor = null;
        var warnings = new List<string>();
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string Abs(string p) => Path.GetFullPath(Path.Combine(projectDir, p));

        if (doc.Target.Weapon is { } weapon)
        {
            var item = Catalog.Item(catalog, weapon);
            (doc.Target.GearId, doc.Target.ItemId) = (item.GearId, item.ItemId);
            if (item.GearId == null && item.ItemId == null) doc.Target.Weapon = null; // attachment, lens or ammo skin: applies wherever its materials are used
        }
        if (string.IsNullOrWhiteSpace(doc.Id)) doc.Id = "skin-" + Guid.NewGuid().ToString("N")[..12];

        if (!outPath.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase)) outPath = Path.Combine(outPath, Preview.Safe(doc.Id) + SkinPack.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var temp = outPath + ".tmp";
        int total = doc.Materials.Sum(m => m.Textures.Count + m.Layers.Count) + doc.Attachments.Count, done = 0;

        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            void Png(string name, SKBitmap bmp)
            {
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                data.SaveTo(s);
            }
            void Copy(string name, string file)
            {
                if (!File.Exists(file)) throw new SkinFormatException("missing-file", $"{file} missing");
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }

            foreach (var m in doc.Materials)
            {
                var sizes = new Dictionary<TextureChannel, (int W, int H)>();
                foreach (var channel in m.Textures.Keys.ToList())
                {
                    Program.Progress(requestId, done++, total, $"{m.Material} {channel}");
                    using var src = Images.Load(Abs(m.Textures[channel]));
                    var size = TargetSize(sizeMode, src.Width, src.Height, BaselineSize(baseline, m.Material, channel));
                    using var bmp = Images.Resize(src, size.W, size.H);
                    var name = $"textures/{Preview.Safe(m.Material)}_{channel}.png";
                    Png(name, bmp);
                    m.Textures[channel] = name;
                    sizes[channel] = size;
                }
                foreach (var layer in m.Layers)
                {
                    Program.Progress(requestId, done++, total, $"{m.Material} {layer.Name}");
                    var frameDir = Path.Combine(projectDir, ".frames", layer.Id);
                    var frames = Directory.Exists(frameDir) ? NaturalSort(Directory.GetFiles(frameDir, "*.png")) : [];
                    var rectFile = Path.Combine(frameDir, "rect.json");
                    if (frames.Count == 0 || !File.Exists(rectFile))
                        throw new SkinFormatException("no-atlas", $"{m.Material}/{layer.Name}: no rendered frames in {frameDir}");
                    var rect = JsonSerializer.Deserialize<FrameRect>(File.ReadAllText(rectFile), Program.Json)!;
                    var target = sizes.TryGetValue(layer.Channel, out var s) ? s : BaselineSize(baseline, m.Material, layer.Channel) ?? (rect.TextureWidth, rect.TextureHeight);
                    double sx = (double)target.W / rect.TextureWidth, sy = (double)target.H / rect.TextureHeight;
                    int x = (int)Math.Round(rect.X * sx), y = (int)Math.Round(rect.Y * sy);
                    int w = Math.Max(1, (int)Math.Round(rect.W * sx)), h = Math.Max(1, (int)Math.Round(rect.H * sy));
                    w = Math.Min(w, target.W - x); h = Math.Min(h, target.H - y);
                    int columns = Math.Max(1, Math.Min(frames.Count, 4096 / w));
                    int rows = (frames.Count + columns - 1) / columns;
                    if (rows * h > 4096) warnings.Add($"{layer.Name}: atlas is {columns * w}x{rows * h}, larger than 4096");
                    using var atlas = new SKBitmap(new SKImageInfo(columns * w, rows * h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                    using (var canvas = new SKCanvas(atlas))
                    {
                        canvas.Clear(SKColors.Transparent);
                        for (int i = 0; i < frames.Count; i++)
                        {
                            using var frame = Images.Load(frames[i]);
                            using var scaled = Images.Resize(frame, w, h);
                            canvas.DrawBitmap(scaled, i % columns * w, i / columns * h);
                        }
                    }
                    if (layer.Channel == TextureChannel.Normal) OpaqueAlpha(atlas);
                    var name = $"layers/{Preview.Safe(layer.Id)}.png";
                    Png(name, atlas);
                    layer.Atlas = new LayerAtlas { Path = name, FrameCount = frames.Count, Columns = columns, FrameWidth = w, FrameHeight = h, TargetRect = [x, y, w, h] };
                    layer.Sources = new();
                    if (layer.Text != null) layer.Text.Font = null;
                }
            }

            foreach (var a in doc.Attachments)
            {
                Program.Progress(requestId, done++, total, a.Name);
                if (a.Mesh != null)
                {
                    var src = Abs(a.Mesh);
                    var name = $"meshes/{Preview.Safe(a.Id)}.sfmesh";
                    if (src.EndsWith(".sfmesh", StringComparison.OrdinalIgnoreCase)) Copy(name, src);
                    else
                    {
                        using var s = zip.CreateEntry(name).Open();
                        s.Write(MeshImport.ToSfMesh(GlbReader.Read(src)).Write());
                    }
                    a.Mesh = name;
                }
                if (a.Material != null)
                    foreach (var channel in a.Material.Textures.Keys.ToList())
                    {
                        var name = $"textures/{Preview.Safe(a.Id)}_{channel}.png";
                        using var src = Images.Load(Abs(a.Material.Textures[channel]));
                        Png(name, src);
                        a.Material.Textures[channel] = name;
                    }
                if (a.Screen != null)
                {
                    a.Screen.Font = null;
                    var glyphs = a.Screen.GlyphAtlas != null ? Abs(a.Screen.GlyphAtlas) : Path.Combine(projectDir, ".frames", a.Id + "-glyphs.png");
                    if (File.Exists(glyphs))
                    {
                        var name = $"glyphs/{Preview.Safe(a.Id)}.png";
                        Copy(name, glyphs);
                        a.Screen.GlyphAtlas = name;
                    }
                    else a.Screen.GlyphAtlas = null; // Validate reports no-glyphs for live screens
                }
            }

            foreach (var sound in doc.Sounds)
            {
                var name = $"sounds/{Preview.Safe(sound.Id)}.ogg";
                Copy(name, Abs(sound.File));
                sound.File = name;
            }

            using (var s = new StreamWriter(zip.CreateEntry(SkinPack.ManifestName).Open()))
                s.Write(SkinJson.Write(doc));
        }

        try
        {
            using (SkinPack.Open(temp)) { }
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        File.Move(temp, outPath, true);
        Program.Progress(requestId, total, total, "done");
        return new { pack = Path.GetFullPath(outPath), warnings };
    }

    static (int W, int H)? BaselineSize(Dictionary<string, Dictionary<TextureChannel, string>> baseline, string material, TextureChannel channel) =>
        baseline.TryGetValue(material, out var t) && t.TryGetValue(channel, out var png) ? Images.Size(png) : null;

    // keep: the user's size; original: longest side of the game texture; 1k/2k/4k: that longest side. Aspect always kept.
    public static (int W, int H) TargetSize(string mode, int w, int h, (int W, int H)? original)
    {
        int? longest = mode switch
        {
            "keep" => null,
            "original" => original is { } o ? Math.Max(o.W, o.H) : null,
            "1k" => 1024,
            "2k" => 2048,
            "4k" => 4096,
            _ => throw new BackendException("bad-request", $"sizeMode {mode}: keep, original, 1k, 2k or 4k"),
        };
        if (longest is not { } l) return (w, h);
        double scale = (double)l / Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    static void OpaqueAlpha(SKBitmap bmp)
    {
        var px = bmp.Pixels;
        for (int i = 0; i < px.Length; i++) px[i] = px[i].WithAlpha(255);
        bmp.Pixels = px;
    }

    public static List<string> NaturalSort(IEnumerable<string> files) =>
        files.OrderBy(f => Regex.Replace(Path.GetFileName(f).ToLowerInvariant(), @"\d+", d => d.Value.PadLeft(12, '0')), StringComparer.Ordinal).ToList();
}
