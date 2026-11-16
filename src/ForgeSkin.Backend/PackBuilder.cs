using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// Project -> .forgeskin: textures scaled to their material's TextureSize (aspect kept), layer frames packed into atlases,
// glyph strips traced into meshes, meshes copied, manifest validated by opening the written pack.
public static class PackBuilder
{
    // Frames: how many of the folder's PNGs the last bake wrote (an earlier, longer bake leaves more behind).
    sealed record FrameRect(int X, int Y, int W, int H, int TextureWidth, int TextureHeight, int? Frames);

    // Milliseconds and count per step of one build, written to backend.log, so a slow apply shows which step it waits on.
    sealed class Laps
    {
        readonly Dictionary<string, (double Ms, int N)> _steps = new();

        public T Time<T>(string step, Func<T> run)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return run(); }
            finally
            {
                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                lock (_steps)
                {
                    var (sum, n) = _steps.GetValueOrDefault(step);
                    _steps[step] = (sum + ms, n + 1);
                }
            }
        }

        public void Time(string step, Action run) => Time(step, () => { run(); return 0; });

        public override string ToString() => string.Join(", ", _steps.Select(s => $"{s.Key} {s.Value.Ms:0} ms x{s.Value.N}"));
    }

    // `baseline`: the game's texture per material and channel (preview.weapon), for layer targets on game textures.
    public static object Build(SkinDocument project, string projectPath, string outPath, List<CatalogItem> catalog,
        Dictionary<string, Dictionary<TextureChannel, string>> baseline, long requestId)
    {
        using var cacheUse = new CacheUse();
        var laps = new Laps();
        var doc = SkinJson.Read(SkinJson.Write(project));
        // A character's credit (the line its model's licence asks for, read from the file by the editor) goes in the pack as CREDITS.txt.
        var credit = doc.Editor?.GetValueOrDefault("credit") is JsonElement { ValueKind: JsonValueKind.String } line ? line.GetString() : null;
        doc.Editor = null;
        var warnings = new List<string>();
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        if (doc.Target.Weapon is { } weapon && doc.Character == null)
        {
            var item = Catalog.Item(catalog, weapon);
            (doc.Target.GearId, doc.Target.ItemId) = (item.GearId, item.ItemId);
            if (item.GearId == null && item.ItemId == null) doc.Target.Weapon = null; // attachment, lens or ammo skin: applies wherever its materials are used
        }
        if (string.IsNullOrWhiteSpace(doc.Id)) doc.Id = "skin-" + Guid.NewGuid().ToString("N")[..12];
        // A pack's player colours hold their defaults, so the plugin shows the skin as built until the player picks.
        foreach (var c in doc.PlayerColors ?? []) c.Paint(doc, c.Default);

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

        foreach (var m in doc.Materials)
            if (m.TextureSize is < 4 or > Dds.MaxSide)
                throw new BackendException("bad-request", $"{m.Material}: textureSize {m.TextureSize} is outside 4–{Dds.MaxSide}");
        // Every texture is resized and compressed on several threads first (that is most of a first build's time); the zip
        // below then takes them from the cache in its own order.
        var jobs = doc.Materials.SelectMany(m => new[] { m.Textures }.Concat((m.Wear ?? []).Select(w => w.Textures))
                .SelectMany(t => t.Select(c => MaterialTexture(c.Value, m.TextureSize, c.Key))))
            .Concat(doc.Attachments.Where(a => a.Material != null).SelectMany(a => a.Material!.Textures.Select(c => AttachmentTexture(c.Value, c.Key))))
            .DistinctBy(j => (j.Key, j.Channel)).ToList();
        var made = new System.Collections.Concurrent.ConcurrentDictionary<(string, TextureChannel), (string Png, string Dds)>();
        laps.Time("textures", () => Parallel.ForEach(jobs, Workers, j => made[(j.Key, j.Channel)] = CachedTexture(j)));

        using (var zip = ZipFile.Open(temp.Temp, ZipArchiveMode.Create))
        {
            // A resized texture or a layer atlas, encoded once per set of inputs: an apply that changed one layer
            // re-encodes that layer only (resizing, packing and PNG encoding are nearly all of a build's time).
            // Every entry name passes the loader's path rule here, so a name the game would refuse (a layer id such as CON) fails the build.
            string Name(string name)
            {
                SkinPack.RequirePath(name, name);
                return name;
            }
            void CachedPng(string name, string key, Func<SKBitmap> make)
            {
                var png = laps.Time("atlas", () => PngCache(key, make));
                laps.Time("zip", () => zip.CreateEntryFromFile(png, Name(name), CompressionLevel.NoCompression));
            }
            // A texture set straight on a material goes in compressed (Core Dds, `<stem>.dds`), so the plugin uploads it
            // instead of decoding and compressing a PNG in the game; compressed once per PNG content, channel kind and
            // encoder version. A size the plugin would leave uncompressed goes in as the PNG (`<stem>.png`). The entry name.
            // The textures were made before the zip was opened.
            string Texture(string stem, TextureJob job)
            {
                var (png, dds) = made[(job.Key, job.Channel)];
                if (new FileInfo(dds).Length == 0)
                {
                    laps.Time("zip", () => zip.CreateEntryFromFile(png, Name(stem + ".png"), CompressionLevel.NoCompression));
                    return stem + ".png";
                }
                laps.Time("zip", () => zip.CreateEntryFromFile(dds, Name(stem + ".dds"), CompressionLevel.Optimal));
                return stem + ".dds";
            }
            void Copy(string name, string file)
            {
                if (!File.Exists(file)) throw new SkinFormatException("missing-file", $"{file} missing");
                // Pictures and sounds are compressed already: deflating them again costs time and saves nothing.
                var level = Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".ogg" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                laps.Time("copy", () => zip.CreateEntryFromFile(file, Name(name), level));
            }
            // A .forgeskinmesh is copied; a GLB is converted.
            void Mesh(string name, string file)
            {
                if (file.EndsWith(".forgeskinmesh", StringComparison.OrdinalIgnoreCase)) Copy(name, file);
                else
                {
                    using var s = zip.CreateEntry(Name(name)).Open();
                    s.Write(MeshImport.ToForgeSkinMesh(GlbReader.Read(file)).Write());
                }
            }

            if (doc.Picture != null)
            {
                Copy("picture.png", doc.Picture);
                doc.Picture = "picture.png";
            }
            // A character's model (ImportedModels format 2, converted by the editor) goes in as it is: character/model.json, .forgemesh, t<i>.png.
            if (doc.Character is { } person)
            {
                void Model(CharacterModelFiles? m, string stem, string textures)
                {
                    if (m == null) return;
                    Copy($"character/{stem}.json", m.Descriptor);
                    Copy($"character/{stem}.forgemesh", m.Mesh);
                    (m.Descriptor, m.Mesh) = ($"character/{stem}.json", $"character/{stem}.forgemesh");
                    m.Textures = m.Textures.Select((file, i) =>
                    {
                        Copy($"character/{textures}{i}.png", file);
                        return $"character/{textures}{i}.png";
                    }).ToList();
                }
                Model(person.Model, "model", "t");
                Model(person.Arms?.Model, "arms", "arms_t");
                if (person.Picture != null)
                {
                    Copy("character/picture.png", person.Picture);
                    person.Picture = "character/picture.png";
                }
                if (!string.IsNullOrWhiteSpace(credit))
                    using (var s = new StreamWriter(zip.CreateEntry(CreditsName).Open())) s.Write(credit.Trim() + "\n");
            }

            foreach (var m in doc.Materials)
            {
                (m.Finish, m.Livery) = (null, null); // the editor baked the livery into this material's textures
                var longest = m.TextureSize;
                m.TextureSize = null;
                var sizes = new Dictionary<TextureChannel, (int W, int H)>();
                // A material's textures, and each wear stage's (the plugin loads them all), at the target size.
                void Textures(Dictionary<TextureChannel, string> textures, string stage)
                {
                    foreach (var channel in textures.Keys.ToList())
                    {
                        Program.Progress(requestId, done++, total, $"{m.Material} {stage}{channel}");
                        var job = MaterialTexture(textures[channel], longest, channel);
                        textures[channel] = Texture($"textures/{Preview.Safe(m.Material)}_{stage}{channel}", job);
                        sizes[channel] = job.Size;
                        memory += Dxt(job.Size.W, job.Size.H);
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
                    var (columns, fw, fh) = AtlasLayout(frames.Count, w, h);
                    int rows = (frames.Count + columns - 1) / columns;
                    if (fw < w) warnings.Add($"{layer.Name}: {frames.Count} frames of {w}x{h} scaled to {fw}x{fh} to fit a {MaxAtlas}x{MaxAtlas} atlas");
                    var name = $"layers/{Preview.Safe(layer.Id)}.png";
                    CachedPng(name, $"atlas|{fw}x{fh}x{columns}x{rows}|{layer.Channel == TextureChannel.Normal}|{string.Join("|", frames.Select(Stamp))}", () =>
                    {
                        // Frames decode and scale on several threads (nearly all of an atlas's time), then are drawn in order.
                        var scaled = new SKBitmap[frames.Count];
                        Parallel.For(0, frames.Count, Workers, i =>
                        {
                            using var frame = Images.Load(frames[i]);
                            scaled[i] = Images.Resize(frame, fw, fh);
                        });
                        var atlas = new SKBitmap(new SKImageInfo(columns * fw, rows * fh, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                        using (var canvas = new SKCanvas(atlas))
                        {
                            canvas.Clear(SKColors.Transparent);
                            for (int i = 0; i < frames.Count; i++)
                            {
                                canvas.DrawBitmap(scaled[i], i % columns * fw, i / columns * fh);
                                scaled[i].Dispose();
                            }
                        }
                        if (layer.Channel == TextureChannel.Normal) OpaqueAlpha(atlas);
                        return atlas;
                    });
                    memory += Rgba(columns * fw, rows * fh) + Rgba(w, h);
                    layer.Atlas = new LayerAtlas { Path = name, FrameCount = frames.Count, Columns = columns, FrameWidth = fw, FrameHeight = fh, TargetRect = [x, y, w, h] };
                    layer.Sources = new();
                    if (layer.Text != null) layer.Text.Font = null;
                }
            }

            foreach (var t in doc.Parts.Where(t => t.Mesh != null))
            {
                var name = $"meshes/part_{Preview.Safe(t.Part + "_" + (t.Path ?? ""))}.forgeskinmesh";
                Mesh(name, t.Mesh!);
                t.Mesh = name;
            }

            foreach (var a in doc.Attachments)
            {
                Program.Progress(requestId, done++, total, a.Name);
                if (a.Mesh != null)
                {
                    var name = $"meshes/{Preview.Safe(a.Id)}.forgeskinmesh";
                    Mesh(name, a.Mesh);
                    a.Mesh = name;
                }
                if (a.Material != null)
                    foreach (var channel in a.Material.Textures.Keys.ToList())
                    {
                        var job = AttachmentTexture(a.Material.Textures[channel], channel);
                        a.Material.Textures[channel] = Texture($"textures/{Preview.Safe(a.Id)}_{channel}", job);
                        memory += Dxt(job.Size.W, job.Size.H);
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
                        var (mesh, traced) = laps.Time("glyphs", () =>
                        {
                            using var bmp = Images.Load(png);
                            return GlyphMesh.Text(bmp);
                        });
                        if (mesh == null) return null;
                        aspect = traced;
                        using var s = zip.CreateEntry(Name(entry)).Open();
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
                        var (glyphs, aspect) = laps.Time("glyphs", () =>
                        {
                            using var bmp = Images.Load(strip);
                            return GlyphMesh.Build(bmp);
                        });
                        a.Screen.GlyphAspect = aspect;
                        a.Screen.GlyphMeshes = glyphs.Select((g, i) =>
                        {
                            if (g == null) return null;
                            var name = $"glyphs/{Preview.Safe(a.Id)}_{i}.forgeskinmesh";
                            using var s = zip.CreateEntry(Name(name)).Open();
                            s.Write(g.Write());
                            return name;
                        }).ToArray();
                    }
                }
            }

            foreach (var sound in doc.Sounds)
            {
                var name = $"sounds/{Preview.Safe(sound.Id)}.ogg";
                Copy(name, sound.File);
                sound.File = name;
            }

            using (var s = new StreamWriter(zip.CreateEntry(SkinPack.ManifestName).Open()))
                s.Write(SkinJson.Write(doc));
        }

        laps.Time("check", () =>
        {
            using var built = SkinPack.Open(temp.Temp);
            CheckSounds(built);
            CheckCharacter(built);
        });
        laps.Time("commit", () => temp.Commit());
        Log.Write($"pack.build {doc.Id} steps: {laps}", echo: false);
        Program.Progress(requestId, total, total, "done");
        return new { pack = Path.GetFullPath(outPath), warnings, memory };
    }

    /// <summary>The pack entry holding the credit a character model's licence asks for; a skin mod's README repeats it.</summary>
    public const string CreditsName = "CREDITS.txt";

    // A character pack read back as the plugin reads it: the descriptor parses, the mesh and textures are the ones it names, and the bone
    // map, springs, capsules and materials fit its skeleton (CharacterRig.Check). Anything else is refused before it reaches a game.
    public static void CheckCharacter(SkinPack pack)
    {
        if (pack.Manifest.Character is not { } person) return;
        foreach (var m in new[] { person.Model, person.Arms?.Model })
        {
            if (m == null) continue;
            var model = ForgeVisual.ImportedModelDescriptor.Parse(Encoding.UTF8.GetString(pack.ReadBytes(m.Descriptor)), out var failure)
                ?? throw new SkinFormatException("bad-character-model", $"{m.Descriptor}: {failure}");
            if (CharacterRig.Matches(model, pack.ReadBytes(m.Mesh), m.Textures.Select(pack.ReadBytes).ToList()) is { } wrong)
                throw new SkinFormatException("bad-character-model", $"{m.Descriptor}: {wrong}");
            if (m == person.Model && CharacterRig.Check(person, model) is { Length: > 0 } misfit)
                throw new SkinFormatException("bad-character-rig", misfit);
        }
    }

    // Every sound of a pack through the audio kit's own container check (ForgeCore AssetValidation, linked): a pack the game
    // would refuse when it loads the sounds (bad-audio) is refused here, naming the sound.
    public static void CheckSounds(SkinPack pack)
    {
        foreach (var file in pack.Manifest.Sounds.Select(s => s.File).Distinct())
            if (ForgeAudio.AssetValidation.Ogg(pack.ReadBytes(file), SkinPack.MaxSoundSeconds) is { Success: false } bad)
                throw new SkinFormatException("bad-audio", $"{Path.GetFileName(file)}: {bad.Error.Detail}");
    }

    // A file by path, length and write time: a re-baked frame or a replaced texture is a new input.
    static string Stamp(string file)
    {
        var f = new FileInfo(file);
        if (!f.Exists) throw new SkinFormatException("missing-file", $"{file} missing");
        return $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
    }

    // The pack cache stays under CacheLimit: the least recently used files (write time, touched on every use) are deleted
    // first, but never one a running build has used (written or touched since the oldest running build started) or one
    // still open. Pruned at the backend's start and after each build.
    const long CacheLimit = 200L << 20;
    static readonly List<DateTime> Running = new();

    sealed class CacheUse : IDisposable
    {
        readonly DateTime _start = DateTime.UtcNow;
        public CacheUse() { lock (Running) Running.Add(_start); }
        public void Dispose()
        {
            lock (Running) Running.Remove(_start);
            PruneCache();
        }
    }

    public static void PruneCache()
    {
        var dir = Path.Combine(Paths.Cache, "pack");
        if (!Directory.Exists(dir)) return;
        lock (Running)
        {
            var keep = Running.Count > 0 ? Running.Min() : DateTime.MaxValue;
            var files = new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files.AsEnumerable().Reverse())
            {
                if (total <= CacheLimit) break;
                if (f.LastWriteTimeUtc >= keep) continue;
                var length = f.Length; // FileInfo reads it again after Delete
                try { f.Delete(); total -= length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // open: kept for the next prune
            }
        }
    }

    // Threads a build decodes, scales and compresses on: each holds a few full-size images, so not every core.
    static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };

    // A texture set straight on a material or an attachment: its cache key (the file and the size it goes in at) and how
    // the image at that size is made.
    sealed record TextureJob(string Key, Func<SKBitmap> Make, TextureChannel Channel, (int W, int H) Size, string File);

    static TextureJob MaterialTexture(string file, int? longest, TextureChannel channel)
    {
        var (sw, sh) = Images.Size(file);
        var size = TargetSize(longest, sw, sh);
        return new($"texture|{Stamp(file)}|{size}", () =>
        {
            using var src = Images.Load(file);
            return Images.Resize(src, size.W, size.H);
        }, channel, size, file);
    }

    static TextureJob AttachmentTexture(string file, TextureChannel channel) =>
        new($"attachment|{Stamp(file)}", () => Images.Load(file), channel, Images.Size(file), file);

    // A texture's resized PNG and its Core Dds (compressed once per PNG content, channel kind and encoder version; an empty
    // file where the plugin leaves the PNG uncompressed), made when not cached.
    static (string Png, string Dds) CachedTexture(TextureJob job)
    {
        var png = PngCache(job.Key, job.Make);
        var bytes = File.ReadAllBytes(png);
        var kind = job.Channel == TextureChannel.Normal ? "normal" : Dds.Linear(job.Channel) ? "linear" : "srgb";
        var dds = Cache($"{Convert.ToHexString(SHA256.HashData(bytes))}|{kind}|{Precompress.Version}", ".dds",
            file => File.WriteAllBytes(file, Precompress.Encode(bytes, job.Channel, job.File) ?? []));
        return (png, dds);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Making = new();

    static string PngCache(string key, Func<SKBitmap> make) => Cache(key, ".png", path =>
    {
        using var bmp = make();
        Images.SavePng(bmp, path);
    });

    // <cache>/pack/<hash of key><extension>, written by `write` when missing.
    static string Cache(string key, string extension, Action<string> write)
    {
        var dir = Path.Combine(Paths.Cache, "pack");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + extension);
        // One thread makes a file; another that needs it meanwhile waits and takes it (the temp file has one name).
        lock (Making.GetOrAdd(file, _ => new object()))
        {
            lock (Running) // not pruned between the check and the touch
                if (File.Exists(file))
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                    return file;
                }
            using (var temp = new AtomicFile(file))
            {
                write(temp.Temp);
                temp.Commit();
            }
            return file;
        }
    }

    public static (int W, int H)? BaselineSize(Dictionary<string, Dictionary<TextureChannel, string>> baseline, string material, TextureChannel channel) =>
        baseline.TryGetValue(material, out var t) && t.TryGetValue(channel, out var png) ? Images.Size(png) : null;

    // A w x h texture with `longest` as its longest side, aspect kept; null keeps its size.
    public static (int W, int H) TargetSize(int? longest, int w, int h)
    {
        if (longest is not { } l) return (w, h);
        double scale = (double)l / Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    const int MaxAtlas = 4096;

    // Columns and frame size of a layer atlas: `count` frames of the w x h region at full size where they fit a MaxAtlas
    // square, else smaller by the largest one scale for both sides any column count allows (most columns among equals).
    public static (int Columns, int W, int H) AtlasLayout(int count, int w, int h)
    {
        var best = (Columns: 1, W: 1, H: 1);
        double bestScale = -1;
        for (int c = count; c >= 1; c--)
        {
            int rows = (count + c - 1) / c;
            double k = Math.Min(1, Math.Min((double)MaxAtlas / (c * w), (double)MaxAtlas / (rows * h)));
            if (k > bestScale) (bestScale, best) = (k, (c, Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k))));
        }
        return best;
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
