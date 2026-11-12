using System.Globalization;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

public sealed record ProjectEntry(string Path, string Id, string Name, DateTime Modified);

// Editor projects (.forgeskinproj = SkinDocument with paths relative to the file) and the old tool's project formats.
// In the protocol every file reference is absolute.
public static class Projects
{
    static readonly JsonObject Legacy = JsonNode.Parse(typeof(Projects).Assembly.GetManifestResourceStream("naming.json")!)!["legacyMaterials"]!.AsObject();

    public static SkinDocument New(string? itemId, List<CatalogItem> catalog)
    {
        var doc = new SkinDocument { Id = "skin-" + Guid.NewGuid().ToString("N")[..12], Name = "New skin" };
        if (itemId != null)
        {
            var item = Catalog.Item(catalog, itemId);
            doc.Name = item.Name;
            doc.Target = new SkinTarget { Weapon = item.Id, GearId = item.GearId, ItemId = item.ItemId };
        }
        return doc;
    }

    public static SkinDocument Load(string path, List<CatalogItem> catalog)
    {
        if (!File.Exists(path)) throw new BackendException("not-found", $"{path} not found");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new SkinFormatException("schema", $"{path} is empty");
        SkinDocument doc;
        if (json.ContainsKey("assignments")) doc = FromGtfoSkin(json, catalog);
        else if (json.ContainsKey("layers") && json.ContainsKey("item_id")) doc = FromGtfDyn(json, catalog);
        else if (json.ContainsKey("anchor_part_id")) doc = FromDly(json, catalog);
        else doc = SkinJson.Read(json.ToJsonString());
        MapPaths(doc, p => Path.GetFullPath(Path.Combine(dir, p)));
        return doc;
    }

    // The projects in a folder (one weapon's skins), newest first. A file that does not parse is listed under its file
    // name, so loading it reports the damage instead of the skin silently disappearing.
    public static List<ProjectEntry> List(string dir) => !Directory.Exists(dir) ? new() : Directory.GetFiles(dir, "*.forgeskinproj").Select(file =>
    {
        var name = Path.GetFileNameWithoutExtension(file);
        JsonObject? json = null;
        try { json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch (System.Text.Json.JsonException) { }
        return new ProjectEntry(file, (string?)json?["id"] ?? name, (string?)json?["name"] ?? name, File.GetLastWriteTimeUtc(file));
    }).OrderByDescending(e => e.Modified).ToList();

    public static object Delete(string path)
    {
        if (!path.EndsWith(".forgeskinproj", StringComparison.OrdinalIgnoreCase)) throw new BackendException("bad-request", $"{path} is not a project file");
        File.Delete(path);
        return new { removed = path };
    }

    public static object Save(SkinDocument doc, string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var copy = SkinJson.Read(SkinJson.Write(doc));
        MapPaths(copy, p => Path.IsPathRooted(p) && string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(dir), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(dir, p).Replace('\\', '/') : p);
        var temp = path + ".tmp";
        File.WriteAllText(temp, SkinJson.Write(copy));
        File.Move(temp, path, true);
        return new { path };
    }

    // Every file reference in a document (texture, layer source, font, mesh, glyph strip, atlas, sound). A texture slot
    // without a file is dropped (the game texture stays).
    public static void MapPaths(SkinDocument doc, Func<string, string> map)
    {
        static void Dict(Dictionary<TextureChannel, string> d, Func<string, string> map)
        {
            foreach (var k in d.Keys.ToList())
                if (string.IsNullOrEmpty(d[k])) d.Remove(k);
                else d[k] = map(d[k]);
        }
        foreach (var m in doc.Materials)
        {
            Dict(m.Textures, map);
            foreach (var l in m.Layers)
            {
                if (l.Kind != LayerKind.Text) l.Sources = l.Sources.Select(map).ToList();
                if (l.Text?.Font is { } font) l.Text.Font = map(font);
                if (l.Atlas != null) l.Atlas.Path = map(l.Atlas.Path);
            }
        }
        foreach (var a in doc.Attachments)
        {
            if (a.Mesh != null) a.Mesh = map(a.Mesh);
            if (a.Material != null) Dict(a.Material.Textures, map);
            if (a.Screen?.Font is { } font) a.Screen.Font = map(font);
            if (a.Screen?.GlyphAtlas is { } glyphs) a.Screen.GlyphAtlas = map(glyphs);
        }
        foreach (var s in doc.Sounds.Where(s => !string.IsNullOrEmpty(s.File))) s.File = map(s.File);
    }

    static SkinDocument Start(string? itemId, string? name, List<CatalogItem> catalog)
    {
        var doc = New(catalog.Any(i => i.Id == itemId) ? itemId : null, catalog);
        if (!string.IsNullOrWhiteSpace(name)) doc.Name = name;
        return doc;
    }

    // Material of an old-tool part: the old catalog's material name, else the new part's first material.
    static string? LegacyMaterial(string itemId, string partId, List<CatalogItem> catalog) =>
        (string?)Legacy[itemId]?[partId]
        ?? catalog.FirstOrDefault(i => i.Id == itemId)?.Parts.FirstOrDefault(p => p.Id == partId)?.Materials.FirstOrDefault();

    static MaterialOverride Override(SkinDocument doc, string material, string partId)
    {
        var m = doc.Materials.FirstOrDefault(x => x.Material == material && x.Part == partId);
        if (m == null) doc.Materials.Add(m = new MaterialOverride { Material = material, Part = partId });
        return m;
    }

    static TextureChannel Channel(string s) => Enum.Parse<TextureChannel>(s, true);

    // Old .gtfoskin: assignments[part][Channel] = path, emissive[part] = {enabled, color, intensity, mask_path}.
    static SkinDocument FromGtfoSkin(JsonObject j, List<CatalogItem> catalog)
    {
        var itemId = (string?)j["item_id"] ?? "";
        var doc = Start(itemId, (string?)j["name"], catalog);
        var disabled = j["disabled_parts"]?.AsArray().Select(x => (string)x!).ToHashSet() ?? new();
        foreach (var (part, slots) in j["assignments"]!.AsObject())
        {
            if (disabled.Contains(part) || LegacyMaterial(itemId, part, catalog) is not { } material) continue;
            var m = Override(doc, material, part);
            foreach (var (channel, file) in slots!.AsObject())
                if (!string.IsNullOrEmpty((string?)file)) m.Textures[Channel(channel)] = (string)file!;
        }
        foreach (var (part, e) in j["emissive"]?.AsObject() ?? new JsonObject())
        {
            if (disabled.Contains(part) || LegacyMaterial(itemId, part, catalog) is not { } material) continue;
            var m = Override(doc, material, part);
            m.Params.EmissiveEnabled = (bool?)e!["enabled"] ?? false;
            if (e["color"] is JsonArray c) m.Params.EmissiveColor = c.Take(3).Select(x => (float)x!).ToArray();
            m.Params.EmissiveIntensity = (float?)e["intensity"] ?? 1;
            if (m.Params.EmissiveEnabled == true && !m.Textures.ContainsKey(TextureChannel.Emissive) && (string?)e["mask_path"] is { Length: > 0 } mask)
                m.Textures[TextureChannel.Emissive] = mask;
        }
        doc.Editor = new() { ["sizeMode"] = (string?)j["resolution_mode"] == "source" ? "original" : (string?)j["resolution_mode"] ?? "keep" };
        if ((string?)doc.Editor["sizeMode"] == "user") doc.Editor["sizeMode"] = "keep";
        return doc;
    }

    // Old .gtfdyn: layers with part_id, channel, media, crop edges, UV top-left + size, rotation, scale, offset, text.
    static SkinDocument FromGtfDyn(JsonObject j, List<CatalogItem> catalog)
    {
        var itemId = (string?)j["item_id"] ?? "auto_pistol";
        var doc = Start(itemId, (string?)j["name"], catalog);
        foreach (var l in j["layers"]!.AsArray().Select(x => x!.AsObject()))
        {
            var part = (string?)l["part_id"] ?? "front";
            if (LegacyMaterial(itemId, part, catalog) is not { } material) continue;
            float F(string k, float d) => (float?)l[k] ?? d;
            var kind = Enum.TryParse<LayerKind>((string?)l["kind"], true, out var k2) ? k2 : LayerKind.Image;
            float w = F("uv_width", 0.24f) * F("scale_x", 1), h = F("uv_height", 0.18f) * F("scale_y", 1);
            var layer = new DynamicLayer
            {
                Id = (string?)l["id"] ?? Guid.NewGuid().ToString("N")[..12],
                Name = (string?)l["name"] ?? "",
                Kind = kind,
                Channel = Enum.TryParse<TextureChannel>((string?)l["channel"], true, out var ch) ? ch : TextureChannel.Albedo,
                Visible = (bool?)l["visible"] ?? true,
                Sources = kind == LayerKind.Video ? [(string)l["source_video"]!] : l["media_paths"]?.AsArray().Select(x => (string)x!).ToList() ?? new(),
                Fps = F("fps", 10),
                Play = (bool?)l["loop"] ?? true ? PlayMode.Loop : PlayMode.Once,
                Opacity = F("opacity", 1),
                Crop = [F("crop_left", 0), F("crop_top", 0), F("crop_right", 1) - F("crop_left", 0), F("crop_bottom", 1) - F("crop_top", 0)],
                UvCenter = [F("uv_x", 0.38f) + F("uv_width", 0.24f) / 2 + F("offset_x", 0), F("uv_y", 0.18f) + F("uv_height", 0.18f) / 2 + F("offset_y", 0)],
                UvSize = [w, h],
                Rotation = F("uv_rotation", 0),
            };
            if (kind == LayerKind.Text)
                layer.Text = new TextStyle
                {
                    Content = (string?)l["text"] ?? "",
                    Font = (string?)l["font_path"] is { Length: > 0 } f ? f : null,
                    SizePx = F("font_size", 96),
                    Color = Hex((string?)l["text_color"] ?? "#FFFFFF"),
                };
            Override(doc, material, part).Layers.Add(layer);
        }
        return doc;
    }

    // Old DLY ammo display preset (.gtfdly): one screen attachment in game weapon space (v1 presets were 0.8x Forge space).
    static SkinDocument FromDly(JsonObject j, List<CatalogItem> catalog)
    {
        var itemId = (string?)j["target_item_id"] ?? "auto_pistol";
        var doc = Start(itemId, "Ammo display", catalog);
        float scale = ((int?)j["schema_version"] ?? 1) < 2 ? 1.25f : 1f;
        float F(string k, float d) => (float?)j[k] ?? d;
        bool absolute = (bool?)j["use_absolute_anchor"] ?? false;
        var pos = absolute ? new[] { F("anchor_local_x", 0), F("anchor_local_y", 0), F("anchor_local_z", 0) } : [F("offset_x", 0), F("offset_y", 0), F("offset_z", 0)];
        float s = F("panel_scale", 1);
        var panel = Hex((string?)j["panel_color"] ?? "#05090AF2");
        panel[3] *= F("panel_opacity", 0.95f);
        var border = Hex((string?)j["border_color"] ?? "#3EDAF5");
        border[3] *= F("border_opacity", 1);
        int clip = (int?)j["preview_clip"] ?? 60;
        doc.Attachments.Add(new Attachment
        {
            Id = "ammo-display",
            Name = "Ammo display",
            Part = (string?)j["anchor_part_id"] ?? "receiver",
            Transform = (string?)j["anchor_name"],
            Position = pos.Select(v => v * scale).ToArray(),
            Rotation = [F("rotation_x", 0), F("rotation_y", 0), F("rotation_z", 0)],
            Scale = [s, s, s],
            FaceCamera = (bool?)j["face_player"] ?? true,
            Screen = new Screen
            {
                Source = ScreenSource.AmmoClip,
                Digits = Math.Clamp(clip.ToString(CultureInfo.InvariantCulture).Length, 2, 6),
                Size = [F("panel_width", 0.03f), F("panel_height", 0.0125f)],
                SurfaceGap = F("surface_clearance", 0.0015f) * scale,
                PanelColor = panel,
                BorderColor = border,
                TextColor = Hex((string?)j["text_color"] ?? "#B9FBFF"),
            },
        });
        return doc;
    }

    // "#RRGGBB" or "#RRGGBBAA" -> RGBA 0..1.
    static float[] Hex(string hex)
    {
        hex = hex.TrimStart('#');
        var c = new float[] { 1, 1, 1, 1 };
        for (int i = 0; i < 4 && i * 2 + 2 <= hex.Length; i++) c[i] = int.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber) / 255f;
        return c;
    }
}
