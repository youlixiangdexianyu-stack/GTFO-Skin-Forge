using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace ForgeSkin.Backend;

// A prefab's transform tree with renderers, read from the game. Local TRS in Unity space.
public sealed class Node
{
    public string Name = "";
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public List<Node> Children = new();
    public Node? Parent;
    public MeshRef? Mesh;
    public List<MaterialRef> Materials = new();
    public bool Active = true;
    public string? Part; // catalog part id on part roots

    public void ResetRoot(string part)
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
        Part = part;
    }

    public void Attach(Node parent)
    {
        Parent = parent;
        parent.Children.Add(this);
    }

    public Matrix4x4 Local => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
    public Matrix4x4 World => Parent == null ? Local : Local * Parent.World;

    public IEnumerable<Node> All() => Children.SelectMany(c => c.All()).Prepend(this);
    public Node? Find(string name) => All().FirstOrDefault(n => n.Name == name);
}

public sealed record MeshRef(AssetsFileInstance File, long PathId, string Name);
public sealed record MaterialRef(AssetsFileInstance File, long PathId, string Name);

public sealed class MeshData
{
    public string Name = "";
    public Vector3[] Positions = [];
    public Vector3[]? Normals;
    public Vector4[]? Tangents;
    public Vector2[]? Uv0;
    public Vector4[]? Colors; // vertex colours; GTFO/BlendHS reads its layer masks from them
    public List<int[]> SubMeshes = new(); // triangle indices per submesh, absolute vertex indices
}

public sealed class MaterialData
{
    public string Name = "";
    public string Shader = "";
    public Dictionary<string, TexRef> Textures = new();
    public Dictionary<string, float> Floats = new();
    public Dictionary<string, float[]> Colors = new();
    public List<string> Keywords = new();
    public int RenderQueue = -1; // m_CustomRenderQueue; -1 = the shader's own queue
}

public sealed record TexRef(AssetsFileInstance File, long PathId, float[] Scale, float[] Offset);

public static class Unity
{
    public static Node LoadPrefab(GameAssets g, GameAssets.AssetRef prefab) => LoadGameObject(g, g.Load(prefab.File), prefab.PathId, null);

    static Node LoadGameObject(GameAssets g, AssetsFileInstance file, long pathId, Node? parent)
    {
        var go = g.Field(file, pathId);
        var node = new Node { Name = go["m_Name"].AsString, Parent = parent, Active = go["m_IsActive"].AsBool };
        foreach (var comp in go["m_Component.Array"].Children)
        {
            var at = g.Resolve(file, comp["component"]);
            var info = at.File.file.GetAssetInfo(at.PathId);
            switch ((AssetClassID)info.TypeId)
            {
                case AssetClassID.Transform:
                    var t = g.Field(at);
                    node.Position = V3(t["m_LocalPosition"]);
                    node.Rotation = new Quaternion(t["m_LocalRotation.x"].AsFloat, t["m_LocalRotation.y"].AsFloat, t["m_LocalRotation.z"].AsFloat, t["m_LocalRotation.w"].AsFloat);
                    node.Scale = V3(t["m_LocalScale"]);
                    foreach (var child in t["m_Children.Array"].Children)
                    {
                        var ct = g.Resolve(at.File, child);
                        var cgo = g.Resolve(ct.File, g.Field(ct)["m_GameObject"]);
                        node.Children.Add(LoadGameObject(g, cgo.File, cgo.PathId, node));
                    }
                    break;
                case AssetClassID.MeshFilter:
                    node.Mesh ??= MeshRefOf(g, at.File, g.Field(at)["m_Mesh"]);
                    break;
                case AssetClassID.SkinnedMeshRenderer:
                    var smr = g.Field(at);
                    node.Mesh = MeshRefOf(g, at.File, smr["m_Mesh"]);
                    node.Materials = MaterialsOf(g, at.File, smr);
                    break;
                case AssetClassID.MeshRenderer:
                    var mr = g.Field(at);
                    if (mr["m_Enabled"].AsBool) node.Materials = MaterialsOf(g, at.File, mr);
                    break;
            }
        }
        return node;
    }

    static MeshRef? MeshRefOf(GameAssets g, AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var at = g.Resolve(file, pptr);
        return at.PathId == 0 ? null : new MeshRef(at.File, at.PathId, g.Field(at)["m_Name"].AsString);
    }

    static List<MaterialRef> MaterialsOf(GameAssets g, AssetsFileInstance file, AssetTypeValueField renderer) =>
        renderer["m_Materials.Array"].Children.Select(m => g.Resolve(file, m))
            .Select(at => at.PathId == 0 ? new MaterialRef(at.File, 0, "") : new MaterialRef(at.File, at.PathId, g.Field(at)["m_Name"].AsString))
            .ToList();

    static Vector3 V3(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);

    public static MaterialData ReadMaterial(GameAssets g, MaterialRef m)
    {
        var f = g.Field(m.File, m.PathId);
        var data = new MaterialData { Name = f["m_Name"].AsString };
        var shader = g.Resolve(m.File, f["m_Shader"]);
        if (shader.PathId != 0) data.Shader = g.Field(shader)["m_ParsedForm.m_Name"].AsString;
        data.RenderQueue = f["m_CustomRenderQueue"].AsInt;
        data.Keywords = f["m_ShaderKeywords"].AsString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var props = f["m_SavedProperties"];
        foreach (var e in props["m_TexEnvs.Array"].Children)
        {
            var tex = g.Resolve(m.File, e["second.m_Texture"]);
            if (tex.PathId == 0) continue;
            data.Textures[e["first"].AsString] = new TexRef(tex.File, tex.PathId,
                [e["second.m_Scale.x"].AsFloat, e["second.m_Scale.y"].AsFloat], [e["second.m_Offset.x"].AsFloat, e["second.m_Offset.y"].AsFloat]);
        }
        foreach (var e in props["m_Floats.Array"].Children) data.Floats[e["first"].AsString] = e["second"].AsFloat;
        foreach (var e in props["m_Colors.Array"].Children)
            data.Colors[e["first"].AsString] = [e["second.r"].AsFloat, e["second.g"].AsFloat, e["second.b"].AsFloat, e["second.a"].AsFloat];
        return data;
    }

    // Decoded RGBA8, top row first.
    public static (int Width, int Height, byte[] Rgba, string Name) ReadTexture(GameAssets g, TexRef t) => DecodeTexture(LoadTexture(g, t));

    // The texture's header and encoded pixels: reads the game files, so it needs the game reader.
    public static (TextureFile Tex, byte[] Pixels) LoadTexture(GameAssets g, TexRef t)
    {
        var tex = TextureFile.ReadTextureFile(g.Field(t.File, t.PathId));
        return (tex, tex.FillPictureData(t.File));
    }

    // Decoding is pure CPU work on the loaded bytes and may run on any thread.
    public static (int Width, int Height, byte[] Rgba, string Name) DecodeTexture((TextureFile Tex, byte[] Pixels) loaded)
    {
        var (tex, pixels) = loaded;
        var raw = tex.DecodeTextureRaw(pixels, false);
        int w = tex.m_Width, h = tex.m_Height, row = w * 4;
        var rgba = new byte[row * h];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * row, rgba, y * row, row);
        return (w, h, rgba, tex.m_Name);
    }

    // Unity 2019.4 Mesh: vertex channels across up to 4 streams (each stream 16-byte aligned), 16/32-bit index buffer.
    public static MeshData ReadMesh(GameAssets g, MeshRef m)
    {
        var f = g.Field(m.File, m.PathId);
        var mesh = new MeshData { Name = f["m_Name"].AsString };
        var vd = f["m_VertexData"];
        int count = (int)vd["m_VertexCount"].AsUInt;
        byte[] data = vd["m_DataSize"].AsByteArray;
        var stream = f["m_StreamData"];
        if (data.Length == 0 && stream["size"].AsUInt > 0)
        {
            var path = Path.Combine(g.DataDir, Path.GetFileName(stream["path"].AsString));
            using var fs = File.OpenRead(path);
            fs.Position = (long)stream["offset"].AsULong;
            data = new byte[stream["size"].AsUInt];
            fs.ReadExactly(data);
        }
        if (f["m_CompressedMesh.m_Vertices.m_NumItems"].AsUInt > 0)
            throw new BackendException("bad-mesh", $"{mesh.Name}: compressed meshes are not supported");

        var channels = vd["m_Channels.Array"].Children
            .Select(c => (Stream: c["stream"].AsByte, Offset: c["offset"].AsByte, Format: c["format"].AsByte, Dim: c["dimension"].AsByte & 0xF))
            .ToArray();
        int streams = channels.Where(c => c.Dim > 0).Select(c => c.Stream + 1).DefaultIfEmpty(0).Max();
        var stride = new int[streams];
        var start = new int[streams];
        foreach (var c in channels.Where(c => c.Dim > 0))
            stride[c.Stream] = Math.Max(stride[c.Stream], c.Offset + FormatSize(c.Format) * c.Dim);
        for (int s = 0, pos = 0; s < streams; s++)
        {
            start[s] = pos;
            pos += stride[s] * count;
            pos = (pos + 15) & ~15;
        }

        float[]? Channel(int index, out int dim)
        {
            dim = 0;
            if (index >= channels.Length || channels[index].Dim == 0) return null;
            var c = channels[index];
            dim = c.Dim;
            var result = new float[count * dim];
            int size = FormatSize(c.Format);
            for (int v = 0; v < count; v++)
                for (int d = 0; d < dim; d++)
                    result[v * dim + d] = ReadComponent(data, start[c.Stream] + v * stride[c.Stream] + c.Offset + d * size, c.Format);
            return result;
        }

        var pos3 = Channel(0, out int pd)!;
        mesh.Positions = Enumerable.Range(0, count).Select(i => new Vector3(pos3[i * pd], pos3[i * pd + 1], pos3[i * pd + 2])).ToArray();
        if (Channel(1, out int nd) is { } n) mesh.Normals = Enumerable.Range(0, count).Select(i => new Vector3(n[i * nd], n[i * nd + 1], n[i * nd + 2])).ToArray();
        if (Channel(2, out int td) is { } t && td == 4) mesh.Tangents = Enumerable.Range(0, count).Select(i => new Vector4(t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3])).ToArray();
        if (Channel(3, out int cd) is { } col && cd == 4) mesh.Colors = Enumerable.Range(0, count).Select(i => new Vector4(col[i * 4], col[i * 4 + 1], col[i * 4 + 2], col[i * 4 + 3])).ToArray();
        if (Channel(4, out int ud) is { } u) mesh.Uv0 = Enumerable.Range(0, count).Select(i => new Vector2(u[i * ud], u[i * ud + 1])).ToArray();

        bool wide = f["m_IndexFormat"].AsInt == 1;
        var ib = f["m_IndexBuffer.Array"].AsByteArray;
        foreach (var sm in f["m_SubMeshes.Array"].Children)
        {
            if (sm["topology"].AsInt != 0) continue; // triangles only
            int first = (int)sm["firstByte"].AsUInt / (wide ? 4 : 2), n2 = (int)sm["indexCount"].AsUInt, baseVertex = (int)sm["baseVertex"].AsUInt;
            var idx = new int[n2];
            for (int i = 0; i < n2; i++)
                idx[i] = baseVertex + (wide ? (int)BitConverter.ToUInt32(ib, (first + i) * 4) : BitConverter.ToUInt16(ib, (first + i) * 2));
            mesh.SubMeshes.Add(idx);
        }
        return mesh;
    }

    // Unity 2019 VertexFormat.
    static int FormatSize(int format) => format switch
    {
        0 or 10 or 11 => 4,
        1 or 4 or 5 or 8 or 9 => 2,
        _ => 1,
    };

    static float ReadComponent(byte[] d, int at, int format) => format switch
    {
        0 => BitConverter.ToSingle(d, at),
        1 => (float)BitConverter.ToHalf(d, at),
        2 => d[at] / 255f,
        3 => Math.Max((sbyte)d[at] / 127f, -1f),
        4 => BitConverter.ToUInt16(d, at) / 65535f,
        5 => Math.Max(BitConverter.ToInt16(d, at) / 32767f, -1f),
        6 => d[at],
        7 => (sbyte)d[at],
        8 => BitConverter.ToUInt16(d, at),
        9 => BitConverter.ToInt16(d, at),
        10 => BitConverter.ToUInt32(d, at),
        _ => BitConverter.ToInt32(d, at),
    };
}
