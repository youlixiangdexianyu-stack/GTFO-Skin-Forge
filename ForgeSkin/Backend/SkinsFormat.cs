using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// The skin group format of the Skins library (food-Skins on Thunderstore): a folder with manifest.skingroup.json (name,
// author, guid, filterGroups that pick gear by OfflineGearPersistentID) and *.mattarget.json files, one per game material,
// each setting shader properties (Texture2D from a file beside it, Float, Color, Vector). Import turns a group into a
// ForgeSkin project; export writes a built pack as a group. Inside the editor a skin is always a ForgeSkin project.
public static class SkinsFormat
{
    public const string Manifest = "manifest.skingroup.json";
    const string MatTarget = ".mattarget.json";

    static readonly Dictionary<string, TextureChannel> Slots = new()
    {
        ["_MainTex"] = TextureChannel.Albedo,
        ["_BumpMap"] = TextureChannel.Normal,
        ["_MetallicGlossMap"] = TextureChannel.MetallicSmoothness,
        ["_EmissiveMap"] = TextureChannel.Emissive,
    };

    public static bool IsGroup(string path) => Path.GetFileName(path).Equals(Manifest, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A skin group as a project for its first gear the catalog knows (none: a skin of those materials on every gear).
    /// Texture macros, shader swaps, texture-name variants and mesh targets have no ForgeSkin counterpart and are left out.
    /// </summary>
    public static SkinDocument Import(string manifestPath, List<CatalogItem> catalog)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var group = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var gearIds = (group["filterGroups"]?.AsArray() ?? [])
            .SelectMany(g => g?["filters"]?.AsArray() ?? [])
            .Where(f => f?["invert"]?.GetValue<bool>() != true && FilterType(f!["type"]) == 0 && uint.TryParse((string?)f["value"], out _))
            .Select(f => uint.Parse((string)f!["value"]!));
        var item = gearIds.Select(id => catalog.FirstOrDefault(c => c.GearId == id)).FirstOrDefault(c => c != null);
        var doc = Projects.New(item?.Id, catalog);
        doc.Name = Text(group["name"]) is { Length: > 0 } name ? name : doc.Name;
        doc.Author = Text(group["author"]);

        foreach (var file in Directory.EnumerateFiles(dir, "*" + MatTarget, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var target = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var material = (string?)target["material_name"];
            if (string.IsNullOrEmpty(material) || doc.Materials.Any(m => m.Material == material)) continue;
            var o = new MaterialOverride { Material = material, Part = item?.Parts.FirstOrDefault(p => p.Materials.Contains(material))?.Id };
            var baseDir = Path.GetDirectoryName(file)!;
            foreach (var (property, entry) in target["properties"]?.AsObject() ?? [])
            {
                var data = entry?["data"];
                if (data == null) continue;
                switch ((string?)entry!["type"])
                {
                    case "Texture2D" when Slots.TryGetValue(property, out var channel) && (string?)data["src"] is { Length: > 0 } src:
                        var path = Path.GetFullPath(Path.Combine(baseDir, src));
                        if (File.Exists(path)) o.Textures[channel] = path; // else a macro (GREY, RGBA(...)) or a missing file
                        break;
                    case "Float" when (float?)data["value"] is { } v:
                        if (property == "_Metallic") o.Params.Metallic = v;
                        else if (property is "_Glossiness" or "_Smoothness") o.Params.Smoothness = v;
                        else if (property == "_EnableEmissive") o.Params.EmissiveEnabled = v > 0.5f;
                        break;
                    case "Color":
                        float[] c = [(float?)data["r"] ?? 0, (float?)data["g"] ?? 0, (float?)data["b"] ?? 0, (float?)data["a"] ?? 1];
                        if (property == "_Color") o.Params.Tint = c;
                        else if (property is "_EmissiveColor" or "_EmissionColor") o.Params.EmissiveColor = c[..3];
                        break;
                }
            }
            if (o.Textures.Count > 0 || JsonSerializer.Serialize(o.Params) != JsonSerializer.Serialize(new MaterialParams())) doc.Materials.Add(o);
        }
        return doc;
    }

    /// <summary>
    /// Writes the pack at `packPath` as a skin group under `folder` in `zip`. Notes what the Skins format cannot carry in
    /// `dropped`. Only gear built from parts can be targeted (Skins matches on the offline gear id).
    /// </summary>
    public static void Write(ZipArchive zip, string packPath, string folder, List<CatalogItem> catalog, List<string> dropped)
    {
        using var pack = SkinPack.Open(packPath);
        var doc = pack.Manifest;
        var gearIds = doc.Target.GearId is { } id ? [id]
            : doc.Target.ItemId != null ? throw new BackendException("bad-request", $"{doc.Name}: Skins only skins guns, melee weapons and tools")
            : catalog.Where(c => c.GearId != null && c.Parts.Any(p => p.Materials.Any(m => doc.Materials.Any(o => o.Material == m)))).Select(c => c.GearId!.Value).ToList();
        if (gearIds.Count == 0) throw new BackendException("bad-request", $"{doc.Name}: no gear uses this skin's materials");

        var group = new JsonObject
        {
            ["name"] = doc.Name,
            ["author"] = string.IsNullOrWhiteSpace(doc.Author) ? "ForgeSkin" : doc.Author,
            ["guid"] = "forgeskin." + doc.Id,
            ["filterGroups"] = new JsonArray(gearIds.Select(g => (JsonNode)new JsonObject
            {
                ["filters"] = new JsonArray(new JsonObject { ["type"] = "OfflineGearPersistentID", ["value"] = g.ToString() }),
            }).ToArray()),
        };
        Json(zip, $"{folder}/{Manifest}", group);

        var written = new HashSet<string>();
        foreach (var m in doc.Materials)
        {
            if (!written.Add(m.Material))
            {
                dropped.Add($"{doc.Name}: {m.Material} ({m.Part}) — Skins sets a material once for the whole gear");
                continue;
            }
            var properties = new JsonObject();
            foreach (var (channel, entry) in m.Textures)
            {
                using (var s = zip.CreateEntry($"{folder}/{entry}").Open()) s.Write(pack.ReadBytes(entry));
                properties[Slots.First(x => x.Value == channel).Key] = Property("Texture2D", new JsonObject { ["src"] = entry });
            }
            var p = m.Params;
            if (p.EmissiveEnabled is bool on) properties["_EnableEmissive"] = Property("Float", new JsonObject { ["value"] = on ? 1 : 0 });
            if (p.EmissiveColor != null || p.EmissiveIntensity != null)
            {
                var c = p.EmissiveColor ?? [1, 1, 1];
                float k = p.EmissiveIntensity ?? 1;
                properties["_EmissiveColor"] = Property("Color", new JsonObject { ["r"] = c[0] * k, ["g"] = c[1] * k, ["b"] = c[2] * k, ["a"] = 1 });
            }
            if (p.Metallic is float metal) properties["_Metallic"] = Property("Float", new JsonObject { ["value"] = metal });
            if (p.Smoothness is float smooth) properties["_Glossiness"] = Property("Float", new JsonObject { ["value"] = smooth });
            if (p.Tint is { } t) properties["_Color"] = Property("Color", new JsonObject { ["r"] = t[0], ["g"] = t[1], ["b"] = t[2], ["a"] = t[3] });
            if (m.Layers.Count > 0) dropped.Add($"{doc.Name}: {m.Material} — {m.Layers.Count} dynamic layers (Skins textures are still)");
            if (properties.Count > 0) Json(zip, $"{folder}/{Preview.Safe(m.Material)}{MatTarget}", new JsonObject { ["material_name"] = m.Material, ["properties"] = properties });
        }
        if (doc.Attachments.Count > 0) dropped.Add($"{doc.Name}: {doc.Attachments.Count} attachments");
        if (doc.Parts.Count > 0) dropped.Add($"{doc.Name}: {doc.Parts.Count} part layout changes");
        if (doc.Sounds.Count > 0) dropped.Add($"{doc.Name}: {doc.Sounds.Count} sounds");
        if (doc.PlayerColors?.Count > 0) dropped.Add($"{doc.Name}: {doc.PlayerColors.Count} player colours (their defaults are kept)");
    }

    static JsonObject Property(string type, JsonObject data) => new() { ["type"] = type, ["data"] = data };

    static void Json(ZipArchive zip, string entry, JsonNode node)
    {
        using var s = zip.CreateEntry(entry).Open();
        JsonSerializer.Serialize(s, node, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // A filter type as an enum name or its number (OfflineGearPersistentID = 0, RundownName = 1).
    static int FilterType(JsonNode? type) => type?.GetValueKind() == JsonValueKind.Number ? type.GetValue<int>()
        : (string?)type == "OfflineGearPersistentID" ? 0 : 1;

    // A LocalizedString: plain text or a map of language to text (English first).
    static string Text(JsonNode? node) => node is JsonObject map
        ? (string?)(map["English"] ?? map.Select(x => x.Value).FirstOrDefault()) ?? ""
        : (string?)node ?? "";
}
