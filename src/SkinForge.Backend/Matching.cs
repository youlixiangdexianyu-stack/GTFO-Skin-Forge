using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkinForge.Format;

namespace SkinForge.Backend;

public sealed record ChannelMatch(string? File, double Confidence, string Reason, bool NeedsConfirmation);
public sealed record PartMatch(string PartId, string Material, Dictionary<TextureChannel, ChannelMatch> Channels);
public sealed record MatchResult(List<PartMatch> Parts, List<string> Unmatched, List<string> Warnings);

// Channel and part recognition for a folder of user textures (port of the old tool's matching.py): file-name rules for
// channel and material, image statistics for the channel, and structural correlation with the game's own texture for
// the part. Each (material, channel) slot takes the best remaining proposal; below 0.82 the app asks for confirmation.
public static class Matching
{
    public const double ConfirmBelow = 0.82;
    static readonly string[] Extensions = [".png", ".tga", ".tif", ".tiff", ".webp", ".jpg", ".jpeg"];

    static readonly (TextureChannel Channel, string[] Patterns)[] ChannelPatterns =
    [
        (TextureChannel.Albedo, [@"albedo", @"base[\s_-]*colou?r", @"diffuse", @"(?:^|[\s_-])diff(?:use)?(?:$|[\s_-])", @"(?:^|[\s_-])color(?:$|[\s_-])", @"colou?r", "基础色", "底色", "漫反射"]),
        (TextureChannel.Normal, [@"normal", @"(?:^|[\s_-])nrm(?:$|[\s_-])", @"(?:^|[\s_-])n(?:$|[\s_-])", "法线"]),
        (TextureChannel.MetallicSmoothness, [@"metallic", @"metal(?:ness)?", @"smoothness", @"(?:^|[\s_-])gloss(?:iness)?(?:$|[\s_-])", @"roughness", @"metal(?:lic)?[\s_-]*(?:smooth|gloss)", "金属", "光滑", "粗糙"]),
        (TextureChannel.Emissive, [@"emissive", @"emission", @"(?:^|[\s_-])glow(?:$|[\s_-])", @"(?:^|[\s_-])e(?:$|[\s_-])", "自发光", "发光"]),
    ];

    static readonly Dictionary<string, string[]> RoleTokens = new()
    {
        ["front"] = ["front", "body", "slide", "barrel", "枪身", "前部", "套筒", "枪管"],
        ["receiver"] = ["receiver", "machinegun", "smg", "rifle", "frame", "机匣", "框架"],
        ["stock"] = ["stock", "grip", "shootgun", "shotgun", "枪托", "握把"],
        ["mag"] = ["mag", "magazine", "弹匣"],
        ["sight"] = ["sight", "optic", "scope", "iron", "瞄具", "准星", "瞄"],
        ["lens"] = ["glass", "lens", "镜片", "镜"],
        ["flashlight"] = ["flashlight", "gunlight", "light", "peq", "枪灯", "手电"],
        ["ammo"] = ["ammo", "cartridge", "casing", "bullet", "shell", "子弹", "弹壳", "弹药"],
    };

    public static List<string> ListImages(string folder)
    {
        if (!Directory.Exists(folder)) throw new BackendException("not-found", $"Folder {folder} not found");
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Fold(string s) => s.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
    static string Normalized(string s) => Regex.Replace(Fold(s), @"[^a-z0-9一-鿿]+", "");
    static string[] Tokens(string s) => Regex.Matches(Fold(s), @"[a-z]+|\d+|[一-鿿]+").Select(m => m.Value).ToArray();

    static bool ContainsTokens(string[] hay, string[] needle) =>
        needle.Length > 0 && needle.Length <= hay.Length &&
        Enumerable.Range(0, hay.Length - needle.Length + 1).Any(i => hay.Skip(i).Take(needle.Length).SequenceEqual(needle));

    public static (TextureChannel? Channel, double Confidence) FilenameChannel(string file)
    {
        var stem = Fold(Path.GetFileNameWithoutExtension(file));
        var hits = new List<(TextureChannel, double)>();
        foreach (var (channel, patterns) in ChannelPatterns)
            foreach (var p in patterns)
                if (Regex.IsMatch(stem, p, RegexOptions.IgnoreCase)) { hits.Add((channel, p.Length > 3 ? 0.99 : 0.90)); break; }
        if (hits.Count == 0) return (null, 0);
        hits.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (hits.Count > 1 && Math.Abs(hits[0].Item2 - hits[1].Item2) < 0.03) return (null, 0.45);
        return hits[0];
    }

    sealed record Stats(double R, double G, double B, double RStd, double GStd, double BStd, double Black);

    static (TextureChannel, double) VisualChannel(Stats s)
    {
        if (s.Black >= 0.72) return (TextureChannel.Emissive, Math.Min(0.95, 0.62 + s.Black * 0.4));
        if (s.B > 0.48 && s.G > 0.30 && s.B >= s.R - 0.02 && s.RStd < 0.22 && s.GStd < 0.22) return (TextureChannel.Normal, 0.72);
        if (s.B > 0.78 && s.BStd < 0.12 && s.G > 0.34) return (TextureChannel.MetallicSmoothness, 0.72);
        return (TextureChannel.Albedo, 0.56);
    }

    static string FilenameBase(string file) => Regex.Replace(Path.GetFileNameWithoutExtension(file).Normalize(NormalizationForm.FormKC),
        @"[_\s-]+(?:base[\s_-]*colou?r|albedo|metallic(?:_raw)?|metallic[\s_-]*smoothness|metallicgloss|normal(?:_raw)?|emissive|emission|glow)(?:[_\s-]+(?:s?rgb|linear|raw))?$",
        "", RegexOptions.IgnoreCase);

    static double PartScore(string file, CatalogItem item, CatalogPart part, string material,
        Dictionary<string, string> knownMaterials, Dictionary<string, string> knownTokens)
    {
        var baseName = FilenameBase(file);
        string b = Normalized(baseName), hay = Normalized(Path.GetFileNameWithoutExtension(file)), key = Normalized(material);
        var baseTokens = Tokens(baseName);
        if (knownMaterials.TryGetValue(b, out var km) && Normalized(km) != key) return 0;
        if (knownTokens.TryGetValue(string.Join(' ', baseTokens), out var kt) && Normalized(kt) != key) return 0;
        double score = 0;
        if (key != "" && b == key) score = 0.995;
        else if (key != "" && b.StartsWith(key)) score = 0.90;
        else if (ContainsTokens(baseTokens, Tokens(material))) score = 0.90;
        else if (key != "" && hay.Contains(key)) score = 0.72;
        var partKey = Normalized(part.Id);
        if (partKey.Length >= 3 && hay.Contains(partKey)) score = Math.Max(score, 0.52);
        if (RoleTokens.TryGetValue(part.Role, out var tokens) && tokens.Any(t => hay.Contains(Normalized(t)))) score = Math.Max(score, 0.36);
        var itemKey = Normalized(item.Name);
        if (itemKey != "" && hay.Contains(itemKey)) score = Math.Max(score, 0.52);
        return score;
    }

    // Grey structure, edges and alpha at low resolution, each standardised (old tool's _feature_from_image).
    static float[] Feature(SKBitmap src)
    {
        using var rgb = Images.Resize(src, 128, 128);
        var gray = new float[128 * 128];
        var alpha = new float[128 * 128];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                var c = rgb.GetPixel(x, y);
                gray[y * 128 + x] = (c.Red * 0.2126f + c.Green * 0.7152f + c.Blue * 0.0722f) / 255f;
                alpha[y * 128 + x] = c.Alpha / 255f;
            }
        gray = Blur(gray, 128, 1.2f);
        var small = Standardize(Sample(gray, 128, 48));
        var coarse = Sample(gray, 128, 64);
        var edge = new float[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float gx = (coarse[y * 64 + Math.Min(x + 1, 63)] - coarse[y * 64 + Math.Max(x - 1, 0)]) / (x == 0 || x == 63 ? 1 : 2);
                float gy = (coarse[Math.Min(y + 1, 63) * 64 + x] - coarse[Math.Max(y - 1, 0) * 64 + x]) / (y == 0 || y == 63 ? 1 : 2);
                edge[y * 64 + x] = Math.Clamp(MathF.Sqrt(gx * gx + gy * gy) * 8f, 0, 1);
            }
        return [.. small, .. Standardize(Sample(edge, 64, 24)), .. Standardize(Sample(alpha, 128, 24))];
    }

    static float[] Sample(float[] src, int n, int m)
    {
        var dst = new float[m * m];
        float scale = (float)n / m;
        for (int y = 0; y < m; y++)
            for (int x = 0; x < m; x++)
            {
                float sx = Math.Clamp((x + 0.5f) * scale - 0.5f, 0, n - 1), sy = Math.Clamp((y + 0.5f) * scale - 0.5f, 0, n - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, n - 1), y1 = Math.Min(y0 + 1, n - 1);
                float fx = sx - x0, fy = sy - y0;
                dst[y * m + x] = (src[y0 * n + x0] * (1 - fx) + src[y0 * n + x1] * fx) * (1 - fy) + (src[y1 * n + x0] * (1 - fx) + src[y1 * n + x1] * fx) * fy;
            }
        return dst;
    }

    static float[] Blur(float[] src, int n, float sigma)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => MathF.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        float sum = k.Sum();
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * src[y * n + Math.Clamp(x + i, 0, n - 1)];
                tmp[y * n + x] = a;
            }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * tmp[Math.Clamp(y + i, 0, n - 1) * n + x];
                dst[y * n + x] = a;
            }
        return dst;
    }

    static float[] Standardize(float[] v)
    {
        float mean = v.Average(), std = MathF.Sqrt(v.Select(x => (x - mean) * (x - mean)).Average());
        return v.Select(x => (x - mean) / (std + 1e-6f)).ToArray();
    }

    static double Correlation(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { double x = a[i] - ma, y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        var r = sab / Math.Sqrt(saa * sbb);
        return double.IsNaN(r) ? 0 : Math.Clamp(r, 0, 1);
    }

    static Stats Statistics(SKBitmap src)
    {
        using var s = Images.Resize(src, 192, 192);
        var px = s.Pixels;
        double[] Ch(Func<SKColor, byte> f) => px.Select(c => f(c) / 255.0).ToArray();
        var r = Ch(c => c.Red); var g = Ch(c => c.Green); var b = Ch(c => c.Blue);
        static double Std(double[] v) { var m = v.Average(); return Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average()); }
        double black = Enumerable.Range(0, px.Length).Count(i => r[i] * 0.2126 + g[i] * 0.7152 + b[i] * 0.0722 < 0.025) / (double)px.Length;
        return new Stats(r.Average(), g.Average(), b.Average(), Std(r), Std(g), Std(b), black);
    }

    sealed record Proposal(double Score, string File, string PartId, string Material, TextureChannel Channel, double PartConfidence, double ChannelConfidence, string Reason);

    // `baseline` gives the game's texture per material and channel (preview.weapon output); the slots are its keys.
    public static MatchResult Match(CatalogItem item, List<CatalogItem> catalog, Dictionary<string, Dictionary<TextureChannel, string>> baseline,
        List<string> files)
    {
        var warnings = new List<string>();
        var known = new Dictionary<string, string>();
        var knownTokens = new Dictionary<string, string>();
        foreach (var m in catalog.SelectMany(i => i.Parts).SelectMany(p => p.Materials))
        {
            known.TryAdd(Normalized(m), m);
            knownTokens.TryAdd(string.Join(' ', Tokens(m)), m);
        }

        // Slots: every channel of every material (procedural game materials such as GTFO/BlendHS have no texture to compare
        // against but can still take the user's).
        var slots = item.Parts.SelectMany(p => p.Materials.Select(m => (Part: p, Material: m))).DistinctBy(s => s.Material)
            .Select(s => (s.Part, s.Material, Channels: Enum.GetValues<TextureChannel>().ToList()))
            .ToList();
        var features = new Dictionary<string, float[]>();
        float[]? BaselineFeature(string material, TextureChannel c)
        {
            if (!baseline.TryGetValue(material, out var t) || !t.TryGetValue(c, out var png)) return null;
            if (!features.TryGetValue(png, out var f)) { using var bmp = Images.Load(png); features[png] = f = Feature(bmp); }
            return f;
        }

        var proposals = new List<Proposal>();
        foreach (var file in files)
        {
            try
            {
                using var bmp = Images.Load(file);
                var stats = Statistics(bmp);
                var (named, namedConf) = FilenameChannel(file);
                var (visual, visualConf) = VisualChannel(stats);
                TextureChannel guess = named ?? visual;
                double guessConf = named != null ? Math.Max(namedConf, named == visual ? visualConf : 0) : visualConf;
                var feature = Feature(bmp);
                if (bmp.Width != bmp.Height) warnings.Add($"{Path.GetFileName(file)}: non-square {bmp.Width}x{bmp.Height}");
                if (bmp.Width > 4096 || bmp.Height > 4096) warnings.Add($"{Path.GetFileName(file)}: very large {bmp.Width}x{bmp.Height}");

                foreach (var (part, material, channels) in slots)
                {
                    double nameScore = PartScore(file, item, part, material, known, knownTokens);
                    if (named != null && nameScore < 0.85) continue;
                    var candidates = guessConf >= 0.50 ? channels.Where(c => c == guess).ToList() : channels;
                    foreach (var channel in candidates)
                    {
                        var basef = BaselineFeature(material, channel);
                        double structural = basef != null ? Correlation(feature, basef) : 0;
                        double channelMatch = named != null ? (channel == named ? Math.Max(namedConf, guessConf) : 0.08) : Math.Max(0.45, structural);
                        double partScore = Math.Max(nameScore, structural);
                        double total = partScore * 0.68 + channelMatch * 0.32;
                        if (partScore < 0.075 || total < 0.27) continue;
                        var reason = $"{(nameScore >= structural ? $"name {nameScore:0.00}" : $"looks like the game texture {structural:0.00}")}; " +
                                     $"{(named != null ? "channel from file name" : $"channel from image ({visual})")}";
                        proposals.Add(new Proposal(total, file, part.Id, material, channel, partScore, channelMatch, reason));
                    }
                }
            }
            catch (Exception e) when (e is SkinFormatException or IOException)
            {
                warnings.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        var assignedFiles = new HashSet<string>();
        var assigned = new Dictionary<(string, TextureChannel), Proposal>();
        foreach (var p in proposals.OrderByDescending(p => p.Score))
            if (!assignedFiles.Contains(p.File) && assigned.TryAdd((p.Material, p.Channel), p))
                assignedFiles.Add(p.File);

        var parts = slots.Select(s => new PartMatch(s.Part.Id, s.Material, s.Channels.ToDictionary(c => c, c =>
            assigned.TryGetValue((s.Material, c), out var p)
                ? new ChannelMatch(p.File, Math.Min(p.PartConfidence, p.ChannelConfidence), p.Reason, Math.Min(p.PartConfidence, p.ChannelConfidence) < ConfirmBelow)
                : new ChannelMatch(null, 0, "missing", false)))).ToList();
        var unmatched = files.Where(f => !assignedFiles.Contains(f)).ToList();
        if (unmatched.Count > 0) warnings.Add($"{unmatched.Count} images not recognised");
        return new MatchResult(parts, unmatched, warnings);
    }
}
