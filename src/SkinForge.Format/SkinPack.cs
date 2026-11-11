using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkinForge.Format;

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

// A .gtfskin file: a zip with manifest.json at the root and every referenced file beside it.
public sealed class SkinPack : IDisposable
{
    public const string Extension = ".gtfskin";
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

        void File(string? path, string where, bool image = false, bool audio = false)
        {
            if (path == null) return;
            if (path.Contains("..") || Path.IsPathRooted(path))
                throw new SkinFormatException("bad-path", $"{where}: illegal path {path}");
            var bytes = head(path) ?? throw new SkinFormatException("missing-file", $"{where}: {path} missing");
            if (image && !IsImage(bytes))
                throw new SkinFormatException("bad-image", $"{where}: {path} is not a PNG or JPEG");
            if (audio && !IsOgg(bytes))
                throw new SkinFormatException("bad-audio", $"{where}: {path} is not an Ogg Vorbis file");
        }
        static void Vec(float[]? v, int n, string where)
        {
            if (v != null && (v.Length != n || v.Any(x => float.IsNaN(x) || float.IsInfinity(x))))
                throw new SkinFormatException("bad-vector", $"{where}: expected {n} finite numbers");
        }

        foreach (var m in doc.Materials)
        {
            if (string.IsNullOrWhiteSpace(m.Material)) throw new SkinFormatException("no-material", "Material override without a material name");
            foreach (var t in m.Textures.Values) File(t, m.Material, true);
            Vec(m.Params.EmissiveColor, 3, m.Material);
            Vec(m.Params.Tint, 4, m.Material);
            foreach (var l in m.Layers)
            {
                if (l.Play == PlayMode.LoopFor && !(l.PlaySeconds > 0))
                    throw new SkinFormatException("bad-trigger", $"{m.Material}/{l.Id}: loopFor needs playSeconds > 0");
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
        }
        foreach (var s in doc.Sounds)
        {
            if (s.Trigger == Trigger.Always || !(s.Volume >= 0))
                throw new SkinFormatException("bad-trigger", $"{s.Id}: a sound needs an event trigger and a volume >= 0");
            File(s.File, s.Id, audio: true);
        }
        foreach (var a in doc.Attachments)
        {
            if (string.IsNullOrWhiteSpace(a.Part)) throw new SkinFormatException("no-anchor", $"{a.Id}: no anchor part");
            if (a.Mesh == null && a.Screen == null) throw new SkinFormatException("empty-attachment", $"{a.Id}: neither mesh nor screen");
            Vec(a.Position ?? Array.Empty<float>(), 3, a.Id);
            Vec(a.Rotation ?? Array.Empty<float>(), 3, a.Id);
            Vec(a.Scale ?? Array.Empty<float>(), 3, a.Id);
            File(a.Mesh, a.Id);
            foreach (var e in a.Effects)
            {
                if (e.Trigger == Trigger.Always || !(e.Seconds > 0))
                    throw new SkinFormatException("bad-trigger", $"{a.Id}: an effect needs an event trigger and seconds > 0");
                Vec(e.Color ?? Array.Empty<float>(), 3, a.Id);
            }
            if (a.Material != null) foreach (var t in a.Material.Textures.Values) File(t, a.Id, true);
            if (a.Screen != null)
            {
                if (a.Screen.Digits is < 1 or > 6) throw new SkinFormatException("bad-screen", $"{a.Id}: digits 1-6");
                if (a.Screen.Source != ScreenSource.Static && a.Screen.GlyphAtlas == null)
                    throw new SkinFormatException("no-glyphs", $"{a.Id}: glyph atlas not baked");
                File(a.Screen.GlyphAtlas, a.Id, true);
            }
        }
    }
}

// .sfmesh: little-endian. "SFM1", u32 vertexCount, u32 indexCount, u32 flags (1 normals, 2 uv0, 4 tangents),
// then float3 positions, [float3 normals], [float2 uv0], [float4 tangents], u32 indices (triangles).
// Unity space (left-handed, Y up, metres).
public sealed class SfMesh
{
    public const uint Magic = 0x314D4653; // "SFM1"
    public float[] Positions = Array.Empty<float>();
    public float[]? Normals;
    public float[]? Uv0;
    public float[]? Tangents;
    public uint[] Indices = Array.Empty<uint>();

    public static SfMesh Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt32() != Magic) throw new SkinFormatException("bad-mesh", "Not an sfmesh");
        int vc = r.ReadInt32(), ic = r.ReadInt32();
        uint flags = r.ReadUInt32();
        if (vc <= 0 || ic <= 0 || ic % 3 != 0) throw new SkinFormatException("bad-mesh", "Bad vertex or index count");
        float[] F(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }
        var m = new SfMesh { Positions = F(vc * 3) };
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
