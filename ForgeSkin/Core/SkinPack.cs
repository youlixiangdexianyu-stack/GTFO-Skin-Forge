using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeSkin.Format;

public static class SkinJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static SkinDocument Read(string json) =>
        JsonSerializer.Deserialize<SkinDocument>(json, Options) ?? throw new SkinFormatException("empty", "Document is empty");

    public static string Write(SkinDocument doc) => JsonSerializer.Serialize(doc, Options);
}

public sealed class SkinFormatException : Exception
{
    public string Code { get; }
    public SkinFormatException(string code, string message) : base(message) => Code = code;
}

// A .forgeskin file: a zip with manifest.json at the root and every referenced file beside it.
public sealed class SkinPack : IDisposable
{
    public const string Extension = ".forgeskin";
    public const string ManifestName = "manifest.json";

    readonly ZipArchive _zip;
    public SkinDocument Manifest { get; }
    public string FilePath { get; }

    SkinPack(string path, ZipArchive zip, SkinDocument manifest)
    {
        FilePath = path;
        _zip = zip;
        Manifest = manifest;
    }

    public static SkinPack Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var entry = zip.GetEntry(ManifestName) ?? throw new SkinFormatException("no-manifest", $"{path}: manifest.json missing");
            using var reader = new StreamReader(entry.Open());
            var doc = SkinJson.Read(reader.ReadToEnd());
            Validate(doc, name => Head(zip.GetEntry(name)));
            return new SkinPack(path, zip, doc);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    static byte[]? Head(ZipArchiveEntry? entry)
    {
        if (entry == null) return null;
        using var stream = entry.Open();
        var head = new byte[8];
        int n = 0, read;
        while (n < head.Length && (read = stream.Read(head, n, head.Length - n)) > 0) n += read;
        return head.Take(n).ToArray();
    }

    public static bool IsImage(byte[] head) =>
        (head.Length >= 8 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') ||
        (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF);

    // The container only; the plugin checks the Vorbis stream when it decodes.
    public static bool IsOgg(byte[] head) => head.Length >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S';

    public byte[] ReadBytes(string name)
    {
        var entry = _zip.GetEntry(name) ?? throw new SkinFormatException("missing-file", $"{FilePath}: {name} missing");
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose() => _zip.Dispose();

    // Rejects a document the plugin cannot apply. `head` returns the first 8 bytes of a referenced file, or null when it is missing.
    public static void Validate(SkinDocument doc, Func<string, byte[]?> head)
    {
        if (doc.Schema != SkinDocument.CurrentSchema)
            throw new SkinFormatException("schema", $"Unsupported schema {doc.Schema}");
        if (string.IsNullOrWhiteSpace(doc.Id))
            throw new SkinFormatException("no-id", "Skin id is empty");
        if (doc.Target.Weapon != null && (doc.Target.GearId == null) == (doc.Target.ItemId == null))
            throw new SkinFormatException("no-gear", $"Weapon {doc.Target.Weapon} needs exactly one of a gear id and an item id");

        void File(string? path, string where, bool image = false, bool audio = false, bool mesh = false)
        {
            if (path == null) return;
            if (path.Contains("..") || Path.IsPathRooted(path))
                throw new SkinFormatException("bad-path", $"{where}: illegal path {path}");
            var bytes = head(path) ?? throw new SkinFormatException("missing-file", $"{where}: {path} missing");
            if (image && !IsImage(bytes))
                throw new SkinFormatException("bad-image", $"{where}: {path} is not a PNG or JPEG");
            if (audio && !IsOgg(bytes))
                throw new SkinFormatException("bad-audio", $"{where}: {path} is not an Ogg Vorbis file");
            if (mesh && !ForgeSkinMesh.IsMesh(bytes))
                throw new SkinFormatException("bad-mesh", $"{where}: {path} is not a .forgeskinmesh");
        }
        static void Vec(float[]? v, int n, string where)
        {
            if (v != null && (v.Length != n || v.Any(x => float.IsNaN(x) || float.IsInfinity(x))))
                throw new SkinFormatException("bad-vector", $"{where}: expected {n} finite numbers");
        }
        static void Params(MaterialParams p, string where)
        {
            Vec(p.EmissiveColor, 3, where);
            Vec(p.Tint, 4, where);
        }
        static bool Fraction(float f) => f is >= 0 and <= 1;

        var names = new HashSet<string>();
        foreach (var m in doc.Materials)
        {
            if (string.IsNullOrWhiteSpace(m.Material)) throw new SkinFormatException("no-material", "Material override without a material name");
            // The plugin matches renderers by material name, so two overrides of one material would silently shadow each other.
            if (!names.Add(m.Material)) throw new SkinFormatException("duplicate-material", $"{m.Material}: more than one override");
            foreach (var t in m.Textures.Values.Concat(m.Wear?.SelectMany(w => w.Textures.Values) ?? [])) File(t, m.Material, true);
            Params(m.Params, m.Material);
            foreach (var l in m.Layers)
            {
                if (l.Play == PlayMode.LoopFor && !(l.PlaySeconds > 0))
                    throw new SkinFormatException("bad-trigger", $"{m.Material}/{l.Id}: loopFor needs playSeconds > 0");
                if (!Fraction(l.LowAmmoFraction))
                    throw new SkinFormatException("bad-trigger", $"{m.Material}/{l.Id}: lowAmmoFraction 0 to 1");
                if (l.Atlas == null) throw new SkinFormatException("no-atlas", $"{m.Material}/{l.Id}: layer not baked");
                File(l.Atlas.Path, $"{m.Material}/{l.Id}", true);
                var a = l.Atlas;
                if (a.FrameCount < 1 || a.Columns < 1 || a.TargetRect.Length != 4 || a.FrameWidth < 1 || a.FrameHeight < 1
                    || a.TargetRect[2] != a.FrameWidth || a.TargetRect[3] != a.FrameHeight)
                    throw new SkinFormatException("bad-atlas", $"{m.Material}/{l.Id}: bad atlas layout");
            }
        }
        foreach (var t in doc.Parts)
        {
            var where = $"{t.Part}/{t.Path}";
            if (string.IsNullOrWhiteSpace(t.Part)) throw new SkinFormatException("no-anchor", "Part layout without a part id");
            Vec(t.Position, 3, where);
            Vec(t.Rotation, 3, where);
            Vec(t.Scale, 3, where);
            File(t.Mesh, where, mesh: true);
        }
        foreach (var s in doc.Sounds)
        {
            if (s.Trigger == Trigger.Always || !Fraction(s.Volume) || !Fraction(s.LowAmmoFraction))
                throw new SkinFormatException("bad-trigger", $"{s.Id}: a sound needs an event trigger, a volume 0 to 1 and a lowAmmoFraction 0 to 1");
            File(s.File, s.Id, audio: true);
        }
        foreach (var a in doc.Attachments)
        {
            if (string.IsNullOrWhiteSpace(a.Part)) throw new SkinFormatException("no-anchor", $"{a.Id}: no anchor part");
            if (a.Mesh == null && a.Screen == null) throw new SkinFormatException("empty-attachment", $"{a.Id}: neither mesh nor screen");
            Vec(a.Position ?? Array.Empty<float>(), 3, a.Id);
            Vec(a.Rotation ?? Array.Empty<float>(), 3, a.Id);
            Vec(a.Scale ?? Array.Empty<float>(), 3, a.Id);
            File(a.Mesh, a.Id, mesh: true);
            foreach (var e in a.Effects)
            {
                if (e.Trigger == Trigger.Always || !(e.Seconds > 0))
                    throw new SkinFormatException("bad-trigger", $"{a.Id}: an effect needs an event trigger and seconds > 0");
                Vec(e.Color ?? Array.Empty<float>(), 3, a.Id);
            }
            if (a.Hologram != null && !Fraction(a.Hologram.Flicker))
                throw new SkinFormatException("bad-vector", $"{a.Id}: hologram flicker 0 to 1");
            if (a.Swing != null && !Fraction(a.Swing.Looseness))
                throw new SkinFormatException("bad-vector", $"{a.Id}: swing looseness 0 to 1");
            if (a.Material != null)
            {
                foreach (var t in a.Material.Textures.Values) File(t, a.Id, true);
                Vec(a.Material.Color, 4, a.Id);
                Params(a.Material.Params, a.Id);
            }
            if (a.Screen != null)
            {
                if (a.Screen.Digits is < 0 or > 6) throw new SkinFormatException("bad-screen", $"{a.Id}: digits 0 (automatic) to 6");
                if (a.Screen.Style == ScreenStyle.Bar && !a.Screen.Source.Bounded())
                    throw new SkinFormatException("bad-screen", $"{a.Id}: a bar needs a source with a maximum, not {a.Screen.Source}");
                if (!Fraction(a.Screen.LowAmmoGradientStart) || !Fraction(a.Screen.LowAmmoFraction))
                    throw new SkinFormatException("bad-screen", $"{a.Id}: low-ammo fraction and gradient start 0 to 1");
                Vec(a.Screen.Size, 2, a.Id);
                Vec(a.Screen.PanelColor, 4, a.Id);
                Vec(a.Screen.BorderColor, 4, a.Id);
                Vec(a.Screen.TextColor, 4, a.Id);
                Vec(a.Screen.LowAmmoColor, 4, a.Id);
                if (a.Screen.Source != ScreenSource.Static && a.Screen.GlyphMeshes == null)
                    throw new SkinFormatException("no-glyphs", $"{a.Id}: glyph meshes not baked");
                if (a.Screen.GlyphMeshes is { } glyphs)
                {
                    // 12: packs baked before the strip had '%'.
                    if (glyphs.Length != 12 && glyphs.Length != Screen.Glyphs.Length || !(a.Screen.GlyphAspect > 0))
                        throw new SkinFormatException("no-glyphs", $"{a.Id}: needs {Screen.Glyphs.Length} glyph meshes and their aspect");
                    foreach (var g in glyphs) File(g, a.Id, mesh: true);
                }
                File(a.Screen.TextMesh, a.Id, mesh: true);
                File(a.Screen.BorderMesh, a.Id, mesh: true);
            }
        }

        // Player colours: unique ids, each slot a colour this document has, no slot driven by two of them.
        var colorIds = new HashSet<string>();
        var slots = new HashSet<(ColorSlotKind, string, int)>();
        foreach (var c in doc.PlayerColors ?? [])
        {
            void Bad(string why) => throw new SkinFormatException("bad-player-color", $"player colour {c.Id}: {why}");
            if (string.IsNullOrWhiteSpace(c.Id) || !colorIds.Add(c.Id)) Bad("empty or repeated id");
            if (c.Slots.Count == 0) Bad("drives no colour");
            Vec(c.Default, 3, $"player colour {c.Id}");
            foreach (var p in c.Palette ?? []) Vec(p, 3, $"player colour {c.Id}");
            foreach (var s in c.Slots)
            {
                var m = doc.Materials.Find(x => x.Material == s.Target);
                var a = doc.Attachments.Find(x => x.Id == s.Target);
                var exists = s.Kind switch
                {
                    ColorSlotKind.Tint or ColorSlotKind.Emissive => m != null,
                    ColorSlotKind.AttachmentColor => a?.Mesh != null,
                    ColorSlotKind.ScreenText or ColorSlotKind.ScreenPanel or ColorSlotKind.ScreenBorder => a?.Screen != null,
                    ColorSlotKind.ScreenLowAmmo => a?.Screen?.LowAmmoColor != null,
                    ColorSlotKind.EffectColor => a != null && s.Effect >= 0 && s.Effect < a.Effects.Count && a.Effects[s.Effect].Action == EffectAction.Flash,
                    _ => false,
                };
                if (!exists) Bad($"{s.Kind} of {s.Target} is not in this skin");
                if (!slots.Add((s.Kind, s.Target, s.Kind == ColorSlotKind.EffectColor ? s.Effect : 0))) Bad($"{s.Kind} of {s.Target} is driven twice");
            }
        }
    }
}

// .forgeskinmesh: little-endian. "SFM1", u32 vertexCount, u32 indexCount, u32 flags (1 normals, 2 uv0, 4 tangents),
// then float3 positions, [float3 normals], [float2 uv0], [float4 tangents], u32 indices (triangles).
// Unity space (left-handed, Y up, metres).
public sealed class ForgeSkinMesh
{
    public const uint Magic = 0x314D4653; // "SFM1"
    public float[] Positions = Array.Empty<float>();
    public float[]? Normals;
    public float[]? Uv0;
    public float[]? Tangents;
    public uint[] Indices = Array.Empty<uint>();

    public static bool IsMesh(byte[] head) => head.Length >= 4 && BitConverter.ToUInt32(head, 0) == Magic;

    public static ForgeSkinMesh Read(byte[] data)
    {
        if (data.Length < 16 || !IsMesh(data)) throw new SkinFormatException("bad-mesh", "Not a .forgeskinmesh");
        using var r = new BinaryReader(new MemoryStream(data));
        r.ReadUInt32();
        int vc = r.ReadInt32(), ic = r.ReadInt32();
        uint flags = r.ReadUInt32();
        if (vc <= 0 || ic <= 0 || ic % 3 != 0) throw new SkinFormatException("bad-mesh", "Bad vertex or index count");
        // The counts must fit the data before anything is allocated from them.
        long floats = 3 + ((flags & 1) != 0 ? 3 : 0) + ((flags & 2) != 0 ? 2 : 0) + ((flags & 4) != 0 ? 4 : 0);
        if (16 + (floats * vc + ic) * 4L > data.Length) throw new SkinFormatException("bad-mesh", "Vertex or index count larger than the data");
        float[] F(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }
        var m = new ForgeSkinMesh { Positions = F(vc * 3) };
        if ((flags & 1) != 0) m.Normals = F(vc * 3);
        if ((flags & 2) != 0) m.Uv0 = F(vc * 2);
        if ((flags & 4) != 0) m.Tangents = F(vc * 4);
        m.Indices = new uint[ic];
        for (int i = 0; i < ic; i++)
        {
            m.Indices[i] = r.ReadUInt32();
            if (m.Indices[i] >= vc) throw new SkinFormatException("bad-mesh", "Index out of range");
        }
        return m;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int vc = Positions.Length / 3;
        w.Write(Magic); w.Write(vc); w.Write(Indices.Length);
        w.Write((Normals != null ? 1u : 0) | (Uv0 != null ? 2u : 0) | (Tangents != null ? 4u : 0));
        foreach (var arr in new[] { Positions, Normals, Uv0, Tangents })
            if (arr != null) foreach (var f in arr) w.Write(f);
        foreach (var i in Indices) w.Write(i);
        w.Flush();
        return ms.ToArray();
    }
}
