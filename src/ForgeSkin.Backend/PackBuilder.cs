using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// Project -> .forgeskin: textures resized by size mode (aspect kept), layer frames packed into atlases, glyph strips traced
// into meshes, meshes copied, manifest validated by opening the written pack.
public static class PackBuilder
{
    // Frames: how many of the folder's PNGs the last bake wrote (an earlier, longer bake leaves more behind).
    sealed record FrameRect(int X, int Y, int W, int H, int TextureWidth, int TextureHeight, int? Frames);

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
        using var temp = new AtomicFile(outPath);
        int total = doc.Materials.Sum(m => m.Textures.Count + m.Layers.Count) + doc.Attachments.Count, done = 0;
        // Video memory the skin takes in game, as the plugin loads it: textures DXT5 with mips (a byte a pixel, a third more
        // for the mips), layer atlases and their regions' bases RGBA32 without mips, one RGBA32 render target per
        // material channel that plays layers.
        long memory = 0;
        static long Dxt(int w, int h) => (long)w * h * 4 / 3;
        static long Rgba(int w, int h) => (long)w * h * 4;

        using (var zip = ZipFile.Open(temp.Temp, ZipArchiveMode.Create))
        {
            void Png(string name, SKBitmap bmp)
            {
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                data.SaveTo(s);
            }
            // A resized texture or a layer atlas, encoded once per set of inputs: an apply that changed one layer
            // re-encodes that layer only (resizing, packing and PNG encoding are nearly all of a build's time).
            void CachedPng(string name, string key, Func<SKBitmap> make)
            {
                var file = PngCache(key, make);
                zip.CreateEntryFromFile(file, name, CompressionLevel.NoCompression);
            }
            void Copy(string name, string file)
            {
                if (!File.Exists(file)) throw new SkinFormatException("missing-file", $"{file} missing");
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
            // A .forgeskinmesh is copied; a GLB is converted.
            void Mesh(string name, string file)
            {
                if (file.EndsWith(".forgeskinmesh", StringComparison.OrdinalIgnoreCase)) Copy(name, file);
                else
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(MeshImport.ToForgeSkinMesh(GlbReader.Read(file)).Write());
                }
            }

            foreach (var m in doc.Materials)
            {
                (m.Finish, m.Livery) = (null, null); // the editor baked the livery into this material's textures
                var sizes = new Dictionary<TextureChannel, (int W, int H)>();
                // A material's textures, and each wear stage's (the plugin loads them all), at the target size.
                void Textures(Dictionary<TextureChannel, string> textures, string stage)
                {
                    foreach (var channel in textures.Keys.ToList())
                    {
                        Program.Progress(requestId, done++, total, $"{m.Material} {stage}{channel}");
                        var file = Abs(textures[channel]);
                        var (sw, sh) = Images.Size(file);
                        var size = TargetSize(sizeMode, sw, sh, BaselineSize(baseline, m.Material, channel));
                        var name = $"textures/{Preview.Safe(m.Material)}_{stage}{channel}.png";
                        CachedPng(name, $"texture|{Stamp(file)}|{size}", () =>
                        {
                            using var src = Images.Load(file);
                            return Images.Resize(src, size.W, size.H);
                        });
                        textures[channel] = name;
                        sizes[channel] = size;
                        memory += Dxt(size.W, size.H);
                    }
                }
                Textures(m.Textures, "");
                for (int i = 0; i < (m.Wear?.Count ?? 0); i++) Textures(m.Wear![i].Textures, $"wear{i}_");
                foreach (var channel in m.Layers.Select(l => l.Channel).Distinct())
                    if ((sizes.TryGetValue(channel, out var ts) ? ts : BaselineSize(baseline, m.Material, channel)) is { } target)
                        memory += Rgba(target.W, target.H);
                foreach (var layer in m.Layers)
                {
                    Program.Progress(requestId, done++, total, $"{m.Material} {layer.Name}");
                    var frameDir = Path.Combine(projectDir, ".frames", layer.Id);
                    var frames = Directory.Exists(frameDir) ? NaturalSort(Directory.GetFiles(frameDir, "*.png")) : [];
                    var rectFile = Path.Combine(frameDir, "rect.json");
                    if (frames.Count == 0 || !File.Exists(rectFile))
                        throw new SkinFormatException("no-atlas", $"{m.Material}/{layer.Name}: no rendered frames in {frameDir}");
                    var rect = JsonSerializer.Deserialize<FrameRect>(File.ReadAllText(rectFile), Program.Json)!;
                    if (rect.Frames is int count) frames = frames.Take(count).ToList();
                    var target = sizes.TryGetValue(layer.Channel, out var s) ? s : BaselineSize(baseline, m.Material, layer.Channel) ?? (rect.TextureWidth, rect.TextureHeight);
                    double sx = (double)target.W / rect.TextureWidth, sy = (double)target.H / rect.TextureHeight;
                    int x = (int)Math.Round(rect.X * sx), y = (int)Math.Round(rect.Y * sy);
                    int w = Math.Max(1, (int)Math.Round(rect.W * sx)), h = Math.Max(1, (int)Math.Round(rect.H * sy));
                    w = Math.Min(w, target.W - x); h = Math.Min(h, target.H - y);
                    if (w <= 0 || h <= 0)
                        throw new SkinFormatException("bad-atlas", $"{m.Material}/{layer.Name}: layer rect lies outside the {target.W}x{target.H} texture");
                    int columns = Math.Max(1, Math.Min(frames.Count, 4096 / w));
                    int rows = (frames.Count + columns - 1) / columns;
                    if (rows * h > 4096) warnings.Add($"{layer.Name}: atlas is {columns * w}x{rows * h}, larger than 4096");
                    var name = $"layers/{Preview.Safe(layer.Id)}.png";
                    CachedPng(name, $"atlas|{w}x{h}x{columns}x{rows}|{layer.Channel == TextureChannel.Normal}|{string.Join("|", frames.Select(Stamp))}", () =>
                    {
                        var atlas = new SKBitmap(new SKImageInfo(columns * w, rows * h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
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
                        return atlas;
                    });
                    memory += Rgba(columns * w, rows * h) + Rgba(w, h);
                    layer.Atlas = new LayerAtlas { Path = name, FrameCount = frames.Count, Columns = columns, FrameWidth = w, FrameHeight = h, TargetRect = [x, y, w, h] };
                    layer.Sources = new();
                    if (layer.Text != null) layer.Text.Font = null;
                }
            }

            foreach (var t in doc.Parts.Where(t => t.Mesh != null))
            {
                var name = $"meshes/part_{Preview.Safe(t.Part + "_" + (t.Path ?? ""))}.forgeskinmesh";
                Mesh(name, Abs(t.Mesh!));
                t.Mesh = name;
            }

            foreach (var a in doc.Attachments)
            {
                Program.Progress(requestId, done++, total, a.Name);
                if (a.Mesh != null)
                {
                    var name = $"meshes/{Preview.Safe(a.Id)}.forgeskinmesh";
                    Mesh(name, Abs(a.Mesh));
                    a.Mesh = name;
                }
                if (a.Material != null)
                    foreach (var channel in a.Material.Textures.Keys.ToList())
                    {
                        var name = $"textures/{Preview.Safe(a.Id)}_{channel}.png";
                        using var src = Images.Load(Abs(a.Material.Textures[channel]));
                        Png(name, src);
                        memory += Dxt(src.Width, src.Height);
                        a.Material.Textures[channel] = name;
                    }
                if (a.Screen != null)
                {
                    a.Screen.Font = null;
                    a.Screen.GlyphMeshes = null; // Validate reports no-glyphs for live screens without a strip
                    a.Screen.TextMesh = a.Screen.BorderMesh = null;
                    // A traced image (static text, a styled border) as one mesh entry; null where it has no pixels.
                    string? Traced(string png, string entry, out float aspect)
                    {
                        aspect = 0;
                        if (!File.Exists(png)) return null;
                        using var bmp = Images.Load(png);
                        var (mesh, traced) = GlyphMesh.Text(bmp);
                        if (mesh == null) return null;
                        aspect = traced;
                        using var s = zip.CreateEntry(entry).Open();
                        s.Write(mesh.Write());
                        return entry;
                    }
                    var frames = Path.Combine(projectDir, ".frames", a.Id);
                    if (a.Screen.Source == ScreenSource.Static)
                    {
                        a.Screen.TextMesh = Traced(frames + "-text.png", $"glyphs/{Preview.Safe(a.Id)}_text.forgeskinmesh", out var aspect);
                        a.Screen.TextAspect = aspect;
                    }
                    if (a.Screen.BorderStyle != BorderStyle.Solid)
                        a.Screen.BorderMesh = Traced(frames + "-border.png", $"glyphs/{Preview.Safe(a.Id)}_border.forgeskinmesh", out _);
                    var strip = Path.Combine(projectDir, ".frames", a.Id + "-glyphs.png");
                    if (a.Screen.Source != ScreenSource.Static && File.Exists(strip))
                    {
                        using var bmp = Images.Load(strip);
                        var (glyphs, aspect) = GlyphMesh.Build(bmp);
                        a.Screen.GlyphAspect = aspect;
                        a.Screen.GlyphMeshes = glyphs.Select((g, i) =>
                        {
                            if (g == null) return null;
                            var name = $"glyphs/{Preview.Safe(a.Id)}_{i}.forgeskinmesh";
                            using var s = zip.CreateEntry(name).Open();
                            s.Write(g.Write());
                            return name;
                        }).ToArray();
                    }
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

        using (SkinPack.Open(temp.Temp)) { }
        temp.Commit();
        Program.Progress(requestId, total, total, "done");
        return new { pack = Path.GetFullPath(outPath), warnings, memory };
    }

    // A file by path, length and write time: a re-baked frame or a replaced texture is a new input.
    static string Stamp(string file)
    {
        var f = new FileInfo(file);
        if (!f.Exists) throw new SkinFormatException("missing-file", $"{file} missing");
        return $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
    }

    static readonly TimeSpan CacheAge = TimeSpan.FromDays(14);

    // <cache>/pack/<hash of key>.png, made by `make` when missing; entries unused for CacheAge are removed.
    static string PngCache(string key, Func<SKBitmap> make)
    {
        var dir = Path.Combine(Paths.Cache, "pack");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".png");
        if (File.Exists(file))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            return file;
        }
        foreach (var old in Directory.EnumerateFiles(dir, "*.png"))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > CacheAge) File.Delete(old);
        using (var bmp = make())
        using (var temp = new AtomicFile(file))
        {
            Images.SavePng(bmp, temp.Temp);
            temp.Commit();
        }
        return file;
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
