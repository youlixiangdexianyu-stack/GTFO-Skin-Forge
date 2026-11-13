using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// One thing an import or export left out or changed: what kind (the editor words it, `skinsNote.<kind>`), the skin, and
// what it concerns (a material, file, filter value or gear name) with a count where one applies.
public sealed record SkinsNote(string Kind, string Skin, string? What = null, int N = 0);

// What `skins.import` made: one project per gear. NeedsTarget: the group names no gear the catalog knows (no gear id
// filter, only rundown conditions, or unknown ids), so nothing was written; the editor asks which gear to put it on.
public sealed record SkinsImported(string Path, string Weapon, string Id, string Name);
public sealed record SkinsImport(List<SkinsImported> Skins, List<SkinsNote> Notes, bool NeedsTarget);

// How the game draws one material of a gear, as the Skins library sees it: its shader and the name of the texture in each
// texture slot (Skins' match_groups compare these names).
public sealed record GameMaterial(string Shader, Dictionary<string, string> Textures);

// The skin group format of the Skins library (food-Skins on Thunderstore, source git.takina.io/gtfo/skins): a folder
// with manifest.skingroup.json (name, author, desc, guid, filterGroups: OR of AND-ed filters on the offline gear id, the
// item id or the rundown) and *.mattarget.json files, one per game material, each setting shader properties (Texture2D
// from a file beside it or a texture macro, Float, Color, Vector), an optional shader swap and texture-name variant
// conditions; Skins uses the first target whose material name and variant match. Import turns a group into one ForgeSkin
// project per gear; export writes built packs as one group (a gun pack) or one group per skin. Inside the editor a skin is
// always a ForgeSkin project.
public static class SkinsFormat
{
    public const string Manifest = "manifest.skingroup.json";
    const string MatTarget = ".mattarget.json";
    const string GearShader = "Cell/Player/CustomGearShader";
    static readonly (string Extension, string Kind)[] OtherTargets = [(".meshtarget.json", "meshTarget"), (".transformtarget.json", "transformTarget"), (".fpstarget.json", "fpsTarget")];

    public static bool IsGroup(string path) => Path.GetFileName(path).Equals(Manifest, StringComparison.OrdinalIgnoreCase);

    static readonly ConcurrentDictionary<(string Build, string Item), Dictionary<string, GameMaterial>> MaterialCache = new();

    /// <summary>Every material of a gear with its shader and texture names, first renderer of each name; cached per game build.</summary>
    public static Dictionary<string, GameMaterial> Materials(GameAssets g, CatalogItem item) => MaterialCache.GetOrAdd((g.BuildId, item.Id), _ =>
    {
        var result = new Dictionary<string, GameMaterial>();
        foreach (var (node, _) in Catalog.Renderers(item.Build(g)))
            foreach (var m in node.Materials)
            {
                if (m.PathId == 0 || result.ContainsKey(m.Name)) continue;
                var data = Unity.ReadMaterial(g, m);
                result[m.Name] = new GameMaterial(data.Shader, data.Textures.ToDictionary(t => t.Key, t => g.Field(t.Value.File, t.Value.PathId)["m_Name"].AsString));
            }
        return result;
    });

    // ---------------------------------------------------------------- import

    // One parsed *.mattarget.json: its material, variant conditions (OR of AND-ed slot = texture name) and what it sets.
    sealed record Target(string File, string Material, List<List<(string Slot, string Value)>> Variants, string? Shader,
        Dictionary<TextureChannel, string> Files, Dictionary<TextureChannel, string> Macros, MaterialParams Params);

    /// <summary>
    /// The group at `manifestPath` as one project per gear it names, saved as `root/&lt;gear&gt;/&lt;skin id&gt;.forgeskinproj` (the
    /// editor's autosave folders); generated macro textures go in `&lt;skin id&gt;.textures` beside it. `weapon` puts a group
    /// that names no gear on that gear. Names are read in `lang` ("zh" or "en"), else the first one given.
    /// </summary>
    public static SkinsImport Import(string manifestPath, string root, string? weapon, string lang, List<CatalogItem> catalog,
        Func<CatalogItem, Dictionary<string, GameMaterial>> materials)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        JsonObject group;
        try { group = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SkinFormatException("schema", $"{manifestPath}: {e.Message}"); }
        var name = Text(group["name"], lang) is { Length: > 0 } n ? n : Path.GetFileName(dir);
        var notes = new List<SkinsNote>();
        if (Text(group["desc"], lang) is { Length: > 0 }) notes.Add(new("desc", name));

        var named = Gears(group, name, catalog, notes);
        var gears = weapon != null ? [Catalog.Item(catalog, weapon)] : named;
        if (gears.Count == 0) return new([], notes, true);

        var targets = new List<Target>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (file.EndsWith(MatTarget, StringComparison.OrdinalIgnoreCase)) targets.Add(ReadTarget(file, rel, name, notes));
            else if (OtherTargets.FirstOrDefault(o => file.EndsWith(o.Extension, StringComparison.OrdinalIgnoreCase)) is { Kind: { } kind }) notes.Add(new(kind, name, rel));
        }

        var docs = new Dictionary<CatalogItem, SkinDocument>();
        foreach (var t in targets.Where(t => t.Material != ""))
        {
            var on = gears.Where(g => materials(g).TryGetValue(t.Material, out var gm)
                && (t.Variants.Count == 0 || t.Variants.Any(v => v.All(c => gm.Textures.TryGetValue(c.Slot, out var tex) && tex == c.Value)))).ToList();
            if (on.Count == 0) { notes.Add(new("noGear", name, $"{t.File} ({t.Material})")); continue; }
            foreach (var gear in on)
            {
                if (!docs.TryGetValue(gear, out var doc))
                {
                    docs[gear] = doc = Projects.New(gear.Id, catalog);
                    (doc.Name, doc.Author) = (name, Text(group["author"], lang));
                }
                // Skins uses the first target that matches; a later one for the same material never shows.
                if (doc.Materials.Any(m => m.Material == t.Material)) { notes.Add(new("conflict", name, $"{t.File} ({t.Material}, {gear.Name})")); continue; }
                var shader = materials(gear)[t.Material].Shader;
                // BlendHS samples no albedo; Skins swaps it to the gear shader for a texture, which the plugin does by itself.
                if (t.Shader != null && t.Shader != shader && !(t.Shader == GearShader && shader == BlendHS.Shader && (t.Files.ContainsKey(TextureChannel.Albedo) || t.Macros.ContainsKey(TextureChannel.Albedo))))
                    notes.Add(new("shader", name, $"{t.Material}: {t.Shader}"));
                var o = new MaterialOverride
                {
                    Material = t.Material,
                    Part = gear.Parts.FirstOrDefault(p => p.Materials.Contains(t.Material))?.Id,
                    Params = JsonSerializer.Deserialize<MaterialParams>(JsonSerializer.Serialize(t.Params))!,
                };
                foreach (var (channel, file) in t.Files) o.Textures[channel] = file;
                var textures = Path.Combine(root, gear.Id, doc.Id + ".textures");
                foreach (var (channel, macro) in t.Macros)
                {
                    using var bmp = Macro(macro)!;
                    var png = Path.Combine(textures, $"{Preview.Safe(t.Material)}_{channel}.png");
                    Images.SavePng(bmp, png);
                    o.Textures[channel] = png;
                }
                doc.Materials.Add(o);
            }
        }
        foreach (var gear in gears.Where(g => !docs.ContainsKey(g))) notes.Add(new("emptyGear", name, gear.Name));

        var skins = new List<SkinsImported>();
        foreach (var (gear, doc) in docs)
        {
            var path = Path.Combine(root, gear.Id, doc.Id + ".forgeskinproj");
            Projects.Save(doc, path);
            skins.Add(new(path, gear.Id, doc.Id, doc.Name));
        }
        return new(skins, notes, false);
    }

    // The gears a group names: per filter group (filters AND-ed), its one gear or item id; rundown conditions and inverted
    // ids are noted and left out, as is a group whose ids contradict each other. The legacy flat `filters` is one group.
    static List<CatalogItem> Gears(JsonObject group, string name, List<CatalogItem> catalog, List<SkinsNote> notes)
    {
        var filterGroups = (group["filterGroups"]?.AsArray() ?? []).Select(g => g?["filters"]?.AsArray() ?? []).ToList();
        if (group["filters"] is JsonArray { Count: > 0 } legacy) filterGroups.Add(legacy);
        var gears = new List<CatalogItem>();
        foreach (var filters in filterGroups)
        {
            var ids = new List<(int Type, string Value)>();
            foreach (var f in filters.OfType<JsonObject>())
            {
                var value = f["value"] is JsonValue v ? v.ToString() : "";
                bool invert = f["invert"]?.GetValueKind() == JsonValueKind.True;
                switch (FilterType(f["type"]))
                {
                    case 0 or 2 when invert: notes.Add(new("inverted", name, value)); break;
                    case int t and (0 or 2): ids.Add((t, value)); break;
                    case 1: notes.Add(new("rundown", name, value)); break;
                    default: notes.Add(new("filterType", name, f["type"]?.ToJsonString())); break;
                }
            }
            if (ids.Distinct().Count() > 1) { notes.Add(new("contradiction", name, string.Join(" + ", ids.Select(i => i.Value)))); continue; }
            if (ids.Count == 0) continue;
            var (type, id) = ids[0];
            var item = uint.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? catalog.FirstOrDefault(c => type == 0 ? c.GearId == number : c.ItemId == number) : null;
            if (item == null) notes.Add(new("unknownGear", name, id));
            else if (!gears.Contains(item)) gears.Add(item);
        }
        return gears;
    }

    // Skins' SkinGroupFilterType as its enum name or number (OfflineGearPersistentID 0, RundownName 1, ItemPersistentID 2);
    // a filter without a type is the default, an offline gear id. -1: not a type Skins knows.
    static int FilterType(JsonNode? type)
    {
        string[] names = ["OfflineGearPersistentID", "RundownName", "ItemPersistentID"];
        if (type == null) return 0;
        if (type.GetValueKind() == JsonValueKind.Number) return type.GetValue<int>() is var i and >= 0 and <= 2 ? i : -1;
        var s = type.GetValueKind() == JsonValueKind.String ? type.GetValue<string>() : "";
        if (int.TryParse(s, out var k)) return k is >= 0 and <= 2 ? k : -1;
        return Array.FindIndex(names, x => x.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    static Target ReadTarget(string file, string rel, string skin, List<SkinsNote> notes)
    {
        JsonObject j;
        try { j = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SkinFormatException("schema", $"{file}: {e.Message}"); }
        var material = (string?)j["material_name"] ?? "";
        if (material == "") notes.Add(new("noMaterial", skin, rel));
        var variants = (j["match_groups"] ?? j["matchGroups"])?.AsArray().OfType<JsonObject>()
            .Select(g => (g["conditions"]?.AsArray() ?? []).OfType<JsonObject>().Select(c => ((string?)c["slot"] ?? "", (string?)c["value"] ?? "")).ToList()).ToList() ?? new();
        if (j["texture_names"] is JsonObject { Count: > 0 } legacy) variants.Add(legacy.Select(x => (x.Key, (string?)x.Value ?? "")).ToList());

        var t = new Target(rel, material, variants, (string?)j["shader"], new(), new(), new());
        var baseDir = Path.GetDirectoryName(file)!;
        foreach (var (property, entry) in j["properties"]?.AsObject() ?? [])
        {
            var type = (string?)entry?["type"];
            string what = $"{material}.{property}";
            if (entry?["data"] is not JsonObject data) { notes.Add(new("property", skin, $"{what} ({type})")); continue; }
            switch (type)
            {
                case "Texture2D" when Preview.ChannelProps.TryGetValue(property, out var channel) && (string?)data["src"] is { Length: > 0 } src:
                    if (data["options"] != null) notes.Add(new("textureOptions", skin, what));
                    if (MacroPattern.IsMatch(src))
                    {
                        using var probe = Macro(src);
                        if (probe == null) notes.Add(new("badMacro", skin, $"{what}: {src}"));
                        else t.Macros[channel] = src;
                    }
                    else if (Path.GetFullPath(Path.Combine(baseDir, src)) is var path && File.Exists(path)) t.Files[channel] = path;
                    else notes.Add(new("missingFile", skin, $"{what}: {src}"));
                    break;
                case "Float" when data["value"] is JsonValue fv && fv.TryGetValue<float>(out var v) && property is "_Metallic" or "_Glossiness" or "_Smoothness" or "_EnableEmissive":
                    if (property == "_Metallic") t.Params.Metallic = v;
                    else if (property == "_EnableEmissive") t.Params.EmissiveEnabled = v > 0.5f;
                    else t.Params.Smoothness = v;
                    break;
                case "Color" when property is "_Color" or "_EmissiveColor" or "_EmissionColor":
                    float C(string k, float d) => data[k] is JsonValue x && x.TryGetValue<float>(out var f) ? f : d;
                    float[] c = [C("r", 0), C("g", 0), C("b", 0), C("a", 1)];
                    if (property == "_Color") t.Params.Tint = c;
                    else (t.Params.EmissiveColor, t.Params.EmissiveIntensity) = MaterialParams.GlowFromGame(c[0], c[1], c[2]);
                    break;
                case "Vector": notes.Add(new("vector", skin, what)); break;
                default: notes.Add(new("property", skin, $"{what} ({type})")); break;
            }
        }
        return t;
    }

    // ---------------------------------------------------------------- texture macros (Skins Textures/TextureMacro.cs)

    // "@NAME", "@NAME(a,b,...)" or either with "[w,h]"; names ignore case, the size defaults to 4x4.
    static readonly Regex MacroPattern = new(@"@(\w+)(?:\(([^)]*)\))?(?:\[(\d+),\s*(\d+)\])?", RegexOptions.IgnoreCase);

    /// <summary>The texture a Skins macro stands for, as an image (top row first); null for an unknown name or too few arguments.</summary>
    public static SKBitmap? Macro(string src)
    {
        var m = MacroPattern.Match(src.Trim());
        if (!m.Success) return null;
        var args = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int w = 4, h = 4;
        if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var pw) && int.TryParse(m.Groups[4].Value, out var ph)) (w, h) = (pw, ph);
        (w, h) = (Math.Clamp(w, 1, 8192), Math.Clamp(h, 1, 8192));
        float? F(int i) => i < args.Length && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        float[] Colour01(int i) => [F(i) ?? 0, F(i + 1) ?? 0, F(i + 2) ?? 0, F(i + 3) ?? 1];
        float[] Solid(float r, float g, float b, float a) => [r, g, b, a];
        Func<int, int, float[]>? pixel = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "WHITE" => (_, _) => Solid(1, 1, 1, 1),
            "BLACK" => (_, _) => Solid(0, 0, 0, 1),
            "TRANSPARENT" => (_, _) => Solid(0, 0, 0, 0),
            "NORMAL" => (_, _) => Solid(0.5f, 0.5f, 1, 1),
            "GREY" => (F(0) is { } grey) ? (_, _) => Solid(grey / 255, grey / 255, grey / 255, 1) : (_, _) => Solid(0.5f, 0.5f, 0.5f, 1),
            "RGBA" when args.Length >= 4 => (_, _) => Colour01(0),
            "RGBA32" when args.Length >= 4 => (_, _) => [(F(0) ?? 0) / 255, (F(1) ?? 0) / 255, (F(2) ?? 0) / 255, (F(3) ?? 255) / 255],
            "HSL" when args.Length >= 3 => (_, _) => Hsl((F(0) ?? 0) / 360, (F(1) ?? 0) / 100, (F(2) ?? 0) / 100, F(3) ?? 1),
            "METALLIC_SMOOTHNESS" => (_, _) => Solid(F(0) ?? 0, F(1) ?? 0, 0, 1),
            "CHECKER" => Checker(args.Length >= 4 ? Colour01(0) : [1, 1, 1, 1], args.Length >= 8 ? Colour01(4) : [0, 0, 0, 1],
                (args.Length >= 9 && int.TryParse(args[8], out var cells)) ? cells : 2, w, h),
            "GRADIENT_H" => Gradient(args.Length >= 4 ? Colour01(0) : [0, 0, 0, 1], args.Length >= 8 ? Colour01(4) : [1, 1, 1, 1], w, true),
            "GRADIENT_V" => Gradient(args.Length >= 4 ? Colour01(0) : [0, 0, 0, 1], args.Length >= 8 ? Colour01(4) : [1, 1, 1, 1], h, false),
            _ => null,
        };
        if (pixel == null) return null;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        static byte B(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        // Unity's pixel rows start at the bottom; the image's at the top.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = pixel(x, y);
                bmp.SetPixel(x, h - 1 - y, new SKColor(B(c[0]), B(c[1]), B(c[2]), B(c[3])));
            }
        return bmp;
    }

    static Func<int, int, float[]> Checker(float[] a, float[] b, int cells, int w, int h) =>
        (x, y) => (x * cells / w + y * cells / h) % 2 == 0 ? a : b;

    static Func<int, int, float[]> Gradient(float[] a, float[] b, int size, bool horizontal) => (x, y) =>
    {
        float t = (horizontal ? x : y) / (float)Math.Max(1, size - 1);
        return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t, a[3] + (b[3] - a[3]) * t];
    };

    static float[] Hsl(float h, float s, float l, float a)
    {
        if (s == 0) return [l, l, l, a];
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static float Hue(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
        }
        return [Hue(p, q, h + 1f / 3), Hue(p, q, h), Hue(p, q, h - 1f / 3), a];
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes the packs as one skin group under `folder` in `zip`: every gun or tool a pack is for joins the group's
    /// filters, and each material target names the textures of its gear's material wherever another gear of the group has
    /// a material of that name, so Skins tells them apart. Item skins (Skins 0.12.0 filters gear only) are skipped; what
    /// the format cannot carry is noted in `dropped`. False when no pack could go in.
    /// </summary>
    public static bool Write(ZipArchive zip, string folder, string name, string description, string guid, IReadOnlyList<string> packPaths,
        List<CatalogItem> catalog, Func<CatalogItem, Dictionary<string, GameMaterial>> materials, List<SkinsNote> dropped)
    {
        // One entry per gear skin and material: the gear (null for a skin of every gear with these materials) and the
        // override the plugin would use (the last one of that material in the skin).
        var entries = new List<(CatalogItem? Gear, SkinPack Pack, MaterialOverride O)>();
        var gears = new List<CatalogItem>();
        var packs = new List<SkinPack>();
        string? author = null;
        try
        {
            foreach (var path in packPaths)
            {
                var pack = SkinPack.Open(path);
                packs.Add(pack);
                var doc = pack.Manifest;
                List<CatalogItem> on;
                CatalogItem? gear = null;
                if (doc.Target.ItemId != null) { dropped.Add(new("itemSkin", doc.Name)); continue; }
                if (doc.Target.GearId is { } id)
                {
                    gear = catalog.FirstOrDefault(c => c.GearId == id) ?? throw new BackendException("not-found", $"{doc.Name}: gear {id} is not in the game");
                    if (packs.Take(packs.Count - 1).Any(p => p.Manifest.Target.GearId == id)) throw new BackendException("bad-request", $"{doc.Name}: a gun pack takes one skin per gear, and {gear.Name} has two");
                    on = [gear];
                }
                else
                {
                    on = catalog.Where(c => c.GearId != null && c.Parts.Any(p => p.Materials.Any(m => doc.Materials.Any(o => o.Material == m)))).ToList();
                    if (on.Count == 0) { dropped.Add(new("unusedMaterials", doc.Name)); continue; }
                    dropped.Add(new("global", doc.Name, null, on.Count));
                }
                gears.AddRange(on.Where(g => !gears.Contains(g)));
                author ??= string.IsNullOrWhiteSpace(doc.Author) ? null : doc.Author;
                foreach (var group in doc.Materials.GroupBy(m => m.Material))
                {
                    // Like the plugin, the gear uses only the last override of a material.
                    if (group.Count() > 1) dropped.Add(new("sameMaterial", doc.Name, group.Key, group.Count() - 1));
                    entries.Add((gear, pack, group.Last()));
                }
                Notes(doc, dropped);
            }
            if (gears.Count == 0) return false;

            var group2 = new JsonObject
            {
                ["name"] = name,
                ["author"] = author ?? "ForgeSkin",
                ["guid"] = guid,
                ["filterGroups"] = new JsonArray(gears.Select(g => (JsonNode)new JsonObject
                {
                    ["filters"] = new JsonArray(new JsonObject { ["type"] = "OfflineGearPersistentID", ["value"] = g.GearId!.Value.ToString(CultureInfo.InvariantCulture) }),
                }).ToArray()),
            };
            if (description != "") group2["desc"] = description;
            Json(zip, $"{folder}/{Manifest}", group2);

            var overridden = entries.Where(e => e.Gear != null).Select(e => (e.Gear!, e.O.Material)).ToHashSet();
            var written = new HashSet<string>();
            var files = new HashSet<string>();
            int index = 0;
            // Targets that name textures first, so Skins (first match wins) reaches them before a target for every variant.
            foreach (var (gear, pack, o) in entries.OrderBy(e => e.Gear == null))
            {
                var doc = pack.Manifest;
                var game = gear != null ? materials(gear).GetValueOrDefault(o.Material) : gears.Select(g => materials(g).GetValueOrDefault(o.Material)).FirstOrDefault(m => m != null);
                var sig = gear != null ? game?.Textures ?? new() : new Dictionary<string, string>();
                var others = gears.Where(g => g != gear && materials(g).ContainsKey(o.Material)).ToList();
                bool conditions = gear != null && others.Count > 0 && sig.Count > 0;
                var key = o.Material + "|" + (conditions ? string.Join("|", sig.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}")) : "*");
                if (!written.Add(key)) { dropped.Add(new("conflict", doc.Name, $"{o.Material} ({gear?.Name})")); continue; }
                // Gears of the group whose material of this name the target cannot tell apart, and that set none of their own.
                if (gear != null)
                {
                    var shared = others.Where(g => !overridden.Contains((g, o.Material)) && (!conditions || Same(materials(g)[o.Material].Textures, sig))).Select(g => g.Name).ToList();
                    if (shared.Count > 0) dropped.Add(new("sharedMaterial", doc.Name, $"{o.Material}: {string.Join(", ", shared)}", shared.Count));
                }

                var properties = new JsonObject();
                foreach (var (channel, entry) in o.Textures)
                {
                    // Skins reads pictures: a precompressed texture goes in decoded back to a PNG.
                    var file = $"{Preview.Safe(doc.Id)}/{(entry.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(entry, ".png") : entry)}";
                    if (files.Add(file)) using (var s = zip.CreateEntry($"{folder}/{file}").Open()) s.Write(Precompress.Png(pack.ReadBytes(entry), channel, entry));
                    properties[Preview.ChannelProps.First(x => x.Value == channel).Key] = Property("Texture2D", new JsonObject { ["src"] = file });
                }
                var p = o.Params;
                bool emissive = o.Textures.ContainsKey(TextureChannel.Emissive);
                if (p.EmissiveEnabled != null || emissive) properties["_EnableEmissive"] = Property("Float", new JsonObject { ["value"] = (p.EmissiveEnabled ?? true) ? 1 : 0 });
                if (p.EmissiveColor != null || p.EmissiveIntensity != null)
                {
                    var c = p.EmissiveColor ?? [1, 1, 1];
                    float k = (p.EmissiveIntensity ?? 1) * MaterialParams.EmissionUnit; // the game's units
                    properties["_EmissiveColor"] = Property("Color", new JsonObject { ["r"] = c[0] * k, ["g"] = c[1] * k, ["b"] = c[2] * k, ["a"] = 1 });
                }
                if (p.Metallic is float metal) properties["_Metallic"] = Property("Float", new JsonObject { ["value"] = metal });
                if (p.Smoothness is float smooth) properties["_Glossiness"] = Property("Float", new JsonObject { ["value"] = smooth });
                if (p.Tint is { } t) properties["_Color"] = Property("Color", new JsonObject { ["r"] = t[0], ["g"] = t[1], ["b"] = t[2], ["a"] = t[3] });
                var target = new JsonObject { ["material_name"] = o.Material };
                // BlendHS samples no albedo: as the plugin does, draw the picture with the gear shader, the slots the skin
                // leaves empty filled as the plugin fills them (flat normal, no metal, no glow, white tint).
                if (game?.Shader == BlendHS.Shader && o.Textures.ContainsKey(TextureChannel.Albedo))
                {
                    target["shader"] = GearShader;
                    properties["_BumpMap"] ??= Property("Texture2D", new JsonObject { ["src"] = "@NORMAL" });
                    properties["_MetallicGlossMap"] ??= Property("Texture2D", new JsonObject { ["src"] = "@BLACK" });
                    properties["_EnableEmissive"] ??= Property("Float", new JsonObject { ["value"] = 0 });
                    properties["_Color"] ??= Property("Color", new JsonObject { ["r"] = 1, ["g"] = 1, ["b"] = 1, ["a"] = 1 });
                }
                if (conditions)
                    target["match_groups"] = new JsonArray(new JsonObject
                    {
                        ["conditions"] = new JsonArray(sig.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject { ["slot"] = s.Key, ["value"] = s.Value }).ToArray()),
                    });
                target["properties"] = properties;
                if (o.Layers.Count > 0) dropped.Add(new("layers", doc.Name, o.Material, o.Layers.Count));
                if (o.Wear is { Count: > 0 }) dropped.Add(new("wear", doc.Name, o.Material, o.Wear.Count));
                if (properties.Count > 0) Json(zip, $"{folder}/{index++:D3}_{Preview.Safe(o.Material)}{MatTarget}", target);
            }
            return true;
        }
        finally
        {
            foreach (var pack in packs) pack.Dispose();
        }
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var v) && v == x.Value);

    // What a skin carries beyond still textures and material values.
    static void Notes(SkinDocument doc, List<SkinsNote> dropped)
    {
        if (doc.Attachments.Count > 0) dropped.Add(new("attachments", doc.Name, null, doc.Attachments.Count));
        if (doc.Parts.Count > 0) dropped.Add(new("parts", doc.Name, null, doc.Parts.Count));
        if (doc.Sounds.Count > 0) dropped.Add(new("sounds", doc.Name, null, doc.Sounds.Count));
        if (doc.PlayerColors?.Count > 0) dropped.Add(new("playerColors", doc.Name, null, doc.PlayerColors.Count));
        if (doc.CodeEffects?.Count > 0) dropped.Add(new("codeEffects", doc.Name, null, doc.CodeEffects.Count));
    }

    static JsonObject Property(string type, JsonObject data) => new() { ["type"] = type, ["data"] = data };

    static void Json(ZipArchive zip, string entry, JsonNode node)
    {
        using var s = zip.CreateEntry(entry).Open();
        JsonSerializer.Serialize(s, node, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // A LocalizedString: plain text, or a map of BCP 47 language tags ("en", "zh-Hans") to text; the editor's language
    // ("zh" takes any Chinese tag, Simplified first), else the first entry, as Skins falls back.
    static string Text(JsonNode? node, string lang)
    {
        if (node is not JsonObject map) return node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
        var texts = map.Where(x => x.Value is JsonValue s && s.GetValueKind() == JsonValueKind.String).Select(x => (Tag: x.Key, Text: x.Value!.GetValue<string>())).ToList();
        string[] wanted = lang == "zh" ? ["zh-Hans", "zh-CN", "zh", "zh-Hant"] : [lang];
        foreach (var w in wanted)
            if (texts.FirstOrDefault(x => x.Tag.Equals(w, StringComparison.OrdinalIgnoreCase) || x.Tag.StartsWith(w + "-", StringComparison.OrdinalIgnoreCase)) is { Text: { } text }) return text;
        return texts.FirstOrDefault().Text ?? "";
    }
}
