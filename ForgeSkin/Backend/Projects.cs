using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;
using SkiaSharp;

namespace ForgeSkin.Backend;

// `Edited`: when the author last changed the work (the editor keeps it in editor.editedAt; a save without a change keeps it).
// `Adopted`: made from someone else's installed pack (editor.adopted, Adopt): its id is that pack's, so deleting the work
// leaves the pack installed.
public sealed record ProjectEntry(string Path, string Id, string Name, DateTime Modified, string? Edited = null, bool Adopted = false);

// Editor projects (.forgeskinproj = SkinDocument with paths relative to the file) and the old tool's project formats.
// In the protocol every file reference is absolute (Absolute); Load and Save convert.
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
        // A damaged file is the file's fault (schema), not the backend's.
        SkinDocument doc;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new SkinFormatException("schema", $"{path} is empty");
            if (SkinsFormat.IsGroup(path)) throw new BackendException("bad-request", $"{path} is a Skins skin group: import it (skins.import)");
            if (json.ContainsKey("assignments")) doc = FromGtfoSkin(json, catalog);
            else if (json.ContainsKey("layers") && json.ContainsKey("item_id")) doc = FromGtfDyn(json, catalog);
            else if (json.ContainsKey("anchor_part_id")) doc = FromDly(json, catalog);
            else doc = SkinJson.Read(json.ToJsonString());
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new SkinFormatException("schema", $"{path}: {e.Message}");
        }
        MapPaths(doc, p => Moved(Path.GetFullPath(Path.Combine(dir, p))));
        return doc;
    }

    static bool Under(string p, string root) =>
        Path.GetFullPath(p).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // A file that is gone but was in a former data folder is taken from the same place in the current one when it is
    // there (the data folder moved); anything else stays as named, and a missing file is reported as before. The next
    // save writes the new place.
    static string Moved(string path)
    {
        if (File.Exists(path)) return path;
        foreach (var former in Paths.FormerData.Where(f => Under(path, f)))
        {
            var now = Path.Combine(Paths.Data, Path.GetRelativePath(former, path));
            if (File.Exists(now)) return now;
        }
        return path;
    }

    // The projects in a folder (one weapon's skins), newest first. A file that does not parse is listed under its file
    // name, so loading it reports the damage instead of the skin silently disappearing.
    public static List<ProjectEntry> List(string dir) => !Directory.Exists(dir) ? new() : Directory.GetFiles(dir, "*.forgeskinproj").Select(file =>
    {
        var name = Path.GetFileNameWithoutExtension(file);
        JsonObject? json = null;
        try { json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch (System.Text.Json.JsonException) { json = null; }
        var editor = json?["editor"] as JsonObject;
        return new ProjectEntry(file, (string?)json?["id"] ?? name, (string?)json?["name"] ?? name, File.GetLastWriteTimeUtc(file), editor?["editedAt"]?.GetValue<string>(),
            editor?["adopted"] is JsonValue v && v.TryGetValue<bool>(out var adopted) && adopted);
    }).OrderByDescending(e => e.Modified).ToList();

    // Every item's skins under the autosave root (one folder per item id); items without skins are left out.
    public static Dictionary<string, List<ProjectEntry>> ListAll(string root) => !Directory.Exists(root) ? new()
        : Directory.GetDirectories(root).Select(d => (Item: Path.GetFileName(d), Skins: List(d))).Where(x => x.Skins.Count > 0).ToDictionary(x => x.Item, x => x.Skins);

    public static object Delete(string path)
    {
        if (!path.EndsWith(".forgeskinproj", StringComparison.OrdinalIgnoreCase)) throw new BackendException("bad-request", $"{path} is not a project file");
        // To the Recycle Bin, so a deleted work can be put back; its snapshots, the textures an import generated for it and a
        // character's converted model go with it. All or nothing: everything is first checked to be free (a folder another
        // program holds open cannot be moved, and a half-deleted work would lose its project yet keep its files), then the
        // folders go before the project, so a failure still leaves the work listed.
        var folders = new[] { ".snapshots", ".textures", ".character" }.Select(e => Path.ChangeExtension(path, e)).Where(Directory.Exists).ToList();
        // A file just written is often read for a moment by another program (a virus scan, the thumbnail cache): asked again a few
        // times before the delete is refused.
        string? held = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            held = folders.FirstOrDefault(InUse) ?? (File.Exists(path) && FileInUse(path) ? path : null);
            if (held == null) break;
            Thread.Sleep(200);
        }
        if (held != null) throw new BackendException("work-in-use", $"{held} is open in another program");
        foreach (var folder in folders) RecycleBin.Send(folder);
        RecycleBin.Send(path);
        return new { removed = path };
    }

    // A copy of the work saved at `from`, to be saved at `to`: the files the work keeps beside it (an import's textures, a
    // character's converted model) are copied into the new work's own folders and `doc` points at them, so deleting one of
    // the two works leaves the other whole.
    public static SkinDocument Copy(SkinDocument doc, string from, string to)
    {
        foreach (var ext in new[] { ".textures", ".character" })
        {
            var source = Path.GetFullPath(Path.ChangeExtension(from, ext));
            if (!Directory.Exists(source)) continue;
            var target = Path.GetFullPath(Path.ChangeExtension(to, ext));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, true);
            }
            MapPaths(doc, p => Under(p, source) ? Path.Combine(target, Path.GetRelativePath(source, Path.GetFullPath(p))) : p);
        }
        return doc;
    }

    // An installed pack made into one of the author's works (「搬到我的作品」): its manifest saved as the project `<dir>/<id>.forgeskinproj`
    // and its files unpacked into `<id>.textures/` beside it, a precompressed texture decoded back to PNG (the editor reads only
    // pictures). `lost` names what a pack no longer holds, which the work comes back without: "editor" (the editor's own notes:
    // livery settings, texture sizes, the scheme shown), "compressed" (textures decoded from the game's compressed copy),
    // "frames" (a dynamic layer comes back as its baked frames, not its source video, GIF or text: RestoreLayer), "layers" (a
    // dynamic layer the pack holds no frames for, left out so the work still applies), "meshes" (an attachment keeps only its game mesh, not the GLB),
    // "screens" (a screen's font, so it shows in the default one), "missing" (a file the manifest names that is not in the pack). A character
    // pack comes back with its converted model (the editor draws it from those files), its bone match, springs, colliders, hidden
    // materials and height; "source": the model file it was made from is not in a pack, so the model itself cannot be imported again.
    // `picture`: the pack's card picture as unpacked, null without one. A name among `taken` (the author's works of that gear) gets " 2",
    // " 3", ... as a new work's does. `baseline`: the game texture size of a weapon's material channel, for a layer over a texture the
    // pack does not replace.
    public static object Adopt(string pack, string dir, IReadOnlyCollection<string> taken, Func<string?, string, TextureChannel, (int W, int H)?> baseline)
    {
        using var p = SkinPack.Open(pack);
        var doc = SkinJson.Read(SkinJson.Write(p.Manifest));
        var name = doc.Name;
        for (var n = 2; taken.Contains(doc.Name); n++) doc.Name = $"{name} {n}";
        var path = Path.GetFullPath(Path.Combine(dir, Preview.Safe(doc.Id) + ".forgeskinproj"));
        if (File.Exists(path)) throw new BackendException("bad-request", $"{path} already exists");
        var files = Path.ChangeExtension(path, ".textures");
        var lost = new SortedSet<string> { doc.Character != null ? "source" : "editor" };
        doc.Editor = new() { ["adopted"] = true };
        if (doc.Attachments.Any(a => a.Mesh != null)) lost.Add("meshes");
        // A screen's font is not in a pack; the editor draws its glyphs again (default font) and the build traces them anew.
        foreach (var s in doc.Attachments.Select(a => a.Screen).OfType<Screen>())
        {
            (s.GlyphMeshes, s.TextMesh, s.BorderMesh) = (null, null, null);
            lost.Add("screens");
        }
        // A hologram's scanlines and outline are code effects the build adds from its settings (hologram.ts, ids `<attachment>~scan`
        // and `~edge`); the pack holds them already made, and kept they would be added twice on the next build.
        var holograms = doc.Attachments.Where(a => a.Hologram != null).SelectMany(a => new[] { a.Id + "~scan", a.Id + "~edge" }).ToHashSet();
        doc.CodeEffects?.RemoveAll(e => holograms.Contains(e.Id));
        Unpack(p, doc, files, lost);
        foreach (var m in doc.Materials)
            foreach (var l in m.Layers.ToList())
                if (RestoreLayer(l, m.Textures.TryGetValue(l.Channel, out var own) && File.Exists(own) ? Images.Size(own) : baseline(doc.Target.Weapon, m.Material, l.Channel), path))
                    lost.Add("frames");
                else
                {
                    m.Layers.Remove(l);
                    lost.Add("layers");
                }
        Save(doc, path);
        return new { path, weapon = doc.Target.Weapon, name = doc.Name, lost, picture = doc.Character?.Picture };
    }

    // Unpacks every file `doc` (read from `p`'s manifest) names into `files`, keeping the pack's folders, and points `doc` at the
    // copies; a precompressed texture is decoded back to PNG ("compressed" in `lost`), a file the pack lacks is "missing".
    internal static void Unpack(SkinPack p, SkinDocument doc, string files, ISet<string> lost)
    {
        var channels = new Dictionary<string, TextureChannel>();
        foreach (var d in doc.Materials.SelectMany(m => (m.Wear ?? []).Select(w => w.Textures).Prepend(m.Textures)).Concat(doc.Attachments.Where(a => a.Material != null).Select(a => a.Material!.Textures)))
            foreach (var (ch, file) in d) channels.TryAdd(file, ch);
        var done = new Dictionary<string, string>();
        MapPaths(doc, name =>
        {
            if (done.TryGetValue(name, out var to)) return to;
            to = Path.Combine(files, name.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes;
            try { bytes = p.ReadBytes(name); }
            catch (SkinFormatException) { lost.Add("missing"); return done[name] = to; }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Dds.IsDds(bytes))
            {
                to = Path.ChangeExtension(to, ".png");
                bytes = Precompress.Png(bytes, channels.GetValueOrDefault(name, TextureChannel.Albedo), name);
                lost.Add("compressed");
            }
            File.WriteAllBytes(to, bytes);
            return done[name] = to;
        });
    }
    // A packed layer made editable again: its atlas cut into frames (final pixels of the target region, base texture included)
    // that become the layer's sources, placed to cover exactly that region unrotated and uncropped, so the editor's bake
    // gives back the same pixels; the frames also go to `.frames/<id>` (the bake contract in frames.ts) so the work builds
    // before the editor bakes it. `texture`: the target texture's size (the pack's own texture, else the game's). False when
    // the atlas or that size is missing: the layer cannot come back.
    static bool RestoreLayer(DynamicLayer l, (int W, int H)? texture, string projectPath)
    {
        if (l.Atlas is not { } a || !File.Exists(a.Path) || texture is not var (tw, th) || a.TargetRect is not [var x, var y, var w, var h]) return false;
        var sources = Path.Combine(Path.GetDirectoryName(a.Path)!, Preview.Safe(l.Id));
        var baked = Path.Combine(Path.GetDirectoryName(projectPath)!, ".frames", l.Id);
        using (var atlas = Images.Load(a.Path))
            for (int i = 0; i < a.FrameCount; i++)
            {
                using var frame = new SKBitmap();
                if (!atlas.ExtractSubset(frame, SKRectI.Create(i % a.Columns * a.FrameWidth, i / a.Columns * a.FrameHeight, a.FrameWidth, a.FrameHeight))) return false;
                var file = Path.Combine(sources, $"{i:0000}.png");
                Images.SavePng(frame, file);
                l.Sources.Add(file);
                using var full = Images.Resize(frame, w, h);
                Images.SavePng(full, Path.Combine(baked, $"{i:0000}.png"));
            }
        File.WriteAllText(Path.Combine(baked, "rect.json"), JsonSerializer.Serialize(new { x, y, w, h, textureWidth = tw, textureHeight = th, frames = a.FrameCount }, Program.Json));
        File.Delete(a.Path);
        l.Kind = a.FrameCount > 1 ? LayerKind.Sequence : LayerKind.Image;
        (l.Opacity, l.Crop, l.Rotation, l.Text, l.Atlas) = (1, [0, 0, 1, 1], 0, null, null);
        l.UvCenter = [(x + w / 2f) / tw, 1 - (y + h / 2f) / th];
        l.UvSize = [(float)w / tw, (float)h / th];
        return true;
    }

    // A folder some program holds open (a watcher, a file opened in it) cannot be renamed; moving it away and back tells.
    static bool InUse(string folder)
    {
        var probe = folder + ".deleting";
        try
        {
            Directory.Move(folder, probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        Directory.Move(probe, folder);
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(FileInUse);
    }

    static bool FileInUse(string file)
    {
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // locked, or read-only (the Recycle Bin would refuse it too)
        }
    }

    // The files a document names, in document order, each once.
    public static List<string> Files(SkinDocument doc)
    {
        var files = new List<string>();
        MapPaths(doc, p =>
        {
            if (!files.Contains(p)) files.Add(p);
            return p;
        });
        return files;
    }

    // The files a document names that are not on disk, in document order.
    public static object Missing(SkinDocument doc) => new { files = Files(doc).Where(p => !File.Exists(p)).ToList() };

    // A document in the protocol names every file by its absolute path: only the project file on disk holds relative ones,
    // and Load and Save own that conversion. A relative one here is a project file's text that reached the editor without
    // Load; resolving it against any folder would be a guess, so it is refused.
    public static SkinDocument Absolute(SkinDocument doc) =>
        Files(doc).FirstOrDefault(p => !Path.IsPathFullyQualified(p)) is { } relative
            ? throw new BackendException("bad-request", $"{relative}: a file the skin names must be an absolute path (project.load gives a project file's paths absolute)")
            : doc;

    public static object Save(SkinDocument doc, string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var copy = SkinJson.Read(SkinJson.Write(doc));
        // Relative only what moves with the project (under its folder or the data folder); a file elsewhere stays absolute,
        // so a project file copied to another folder still finds it.
        MapPaths(copy, p => Under(p, dir) || Under(p, Paths.Data) ? Path.GetRelativePath(dir, p).Replace('\\', '/') : p);
        AtomicFile.WriteText(path, SkinJson.Write(copy));
        return new { path };
    }

    // A font is a family name (bundled or installed) or a font file.
    static bool FontFile(string font) => Path.GetExtension(font).ToLowerInvariant() is ".ttf" or ".otf" or ".woff" or ".woff2";

    // Every file reference in a document (texture, layer source, decal image, font file, mesh, part mesh, atlas, sound). A texture
    // slot without a file is dropped (the game texture stays).
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
            foreach (var w in m.Wear ?? []) Dict(w.Textures, map);
            foreach (var l in m.Layers)
            {
                if (l.Kind != LayerKind.Text) l.Sources = l.Sources.Select(map).ToList();
                if (l.Text?.Font is { } font && FontFile(font)) l.Text.Font = map(font);
                if (l.Atlas != null) l.Atlas.Path = map(l.Atlas.Path);
            }
            foreach (var l in m.Livery ?? [])
            {
                if (l.Image != null) l.Image = map(l.Image);
                if (l.Text?.Font is { } font && FontFile(font)) l.Text.Font = map(font);
            }
        }
        foreach (var a in doc.Attachments)
        {
            if (a.Mesh != null) a.Mesh = map(a.Mesh);
            if (a.Material != null) Dict(a.Material.Textures, map);
            if (a.Screen?.Font is { } font && FontFile(font)) a.Screen.Font = map(font);
        }
        foreach (var t in doc.Parts.Where(t => t.Mesh != null)) t.Mesh = map(t.Mesh!);
        foreach (var s in doc.Sounds.Where(s => !string.IsNullOrEmpty(s.File))) s.File = map(s.File);
        foreach (var m in new[] { doc.Character?.Model, doc.Character?.Arms?.Model })
        {
            if (m == null) continue;
            if (m.Descriptor.Length > 0) m.Descriptor = map(m.Descriptor);
            if (m.Mesh.Length > 0) m.Mesh = map(m.Mesh);
            m.Textures = m.Textures.Select(map).ToList();
        }
        if (doc.Character?.Picture is { Length: > 0 } picture) doc.Character.Picture = map(picture);
        if (doc.Picture is { Length: > 0 } own) doc.Picture = map(own);
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
