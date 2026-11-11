using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinForge.Backend;

// Minimal glTF 2.0 binary writer/reader for our own meshes. Unity -> glTF negates Z (positions, normals, tangent.z,
// rotations), flips triangle winding and V. Materials carry only a name: the app applies textures from preview.weapon.
public sealed class GlbWriter
{
    readonly MemoryStream _bin = new();
    readonly JsonArray _views = new(), _accessors = new(), _meshes = new(), _materials = new(), _nodes = new();
    readonly Dictionary<string, int> _materialIndex = new();
    readonly HashSet<string> _names = new();

    int View(byte[] data, int? target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = data.Length };
        if (target != null) view["target"] = target;
        _bin.Write(data);
        _views.Add(view);
        return _views.Count - 1;
    }

    int Accessor(float[] data, int components, string type, bool minMax = false)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var acc = new JsonObject { ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = data.Length / components, ["type"] = type };
        if (minMax)
        {
            var min = new JsonArray(); var max = new JsonArray();
            for (int c = 0; c < components; c++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = c; i < data.Length; i += components) { lo = Math.Min(lo, data[i]); hi = Math.Max(hi, data[i]); }
                min.Add(lo); max.Add(hi);
            }
            acc["min"] = min; acc["max"] = max;
        }
        _accessors.Add(acc);
        return _accessors.Count - 1;
    }

    int Indices(int[] idx)
    {
        var bytes = new byte[idx.Length * 4];
        Buffer.BlockCopy(idx, 0, bytes, 0, bytes.Length);
        _accessors.Add(new JsonObject { ["bufferView"] = View(bytes, 34963), ["componentType"] = 5125, ["count"] = idx.Length, ["type"] = "SCALAR" });
        return _accessors.Count - 1;
    }

    int Material(string name)
    {
        if (_materialIndex.TryGetValue(name, out var i)) return i;
        _materials.Add(new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.6 } });
        return _materialIndex[name] = _materials.Count - 1;
    }

    // Mesh in Unity space; one primitive per submesh with the given material names.
    public int Mesh(MeshData m, IReadOnlyList<string> materials)
    {
        var attrs = new JsonObject { ["POSITION"] = Accessor(m.Positions.SelectMany(p => new[] { p.X, p.Y, -p.Z }).ToArray(), 3, "VEC3", true) };
        if (m.Normals != null) attrs["NORMAL"] = Accessor(m.Normals.SelectMany(p => Normalize(new Vector3(p.X, p.Y, -p.Z))).ToArray(), 3, "VEC3");
        if (m.Tangents != null) attrs["TANGENT"] = Accessor(m.Tangents.SelectMany(t => Normalize(new Vector3(t.X, t.Y, -t.Z)).Append(t.W < 0 ? 1f : -1f)).ToArray(), 4, "VEC4");
        if (m.Uv0 != null) attrs["TEXCOORD_0"] = Accessor(m.Uv0.SelectMany(u => new[] { u.X, 1 - u.Y }).ToArray(), 2, "VEC2");
        var prims = new JsonArray();
        for (int s = 0; s < m.SubMeshes.Count; s++)
        {
            var idx = (int[])m.SubMeshes[s].Clone();
            for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            var prim = new JsonObject { ["attributes"] = attrs.DeepClone(), ["indices"] = Indices(idx) };
            if (s < materials.Count && materials[s] != "") prim["material"] = Material(materials[s]);
            prims.Add(prim);
        }
        _meshes.Add(new JsonObject { ["name"] = m.Name, ["primitives"] = prims });
        return _meshes.Count - 1;
    }

    static float[] Normalize(Vector3 v)
    {
        v = v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitY;
        return [v.X, v.Y, v.Z];
    }

    // Unique node name (duplicates get " (n)"); extras keep the Unity transform name and part.
    public int Node(string name, Vector3 pos, Quaternion rot, Vector3 scale, int? mesh, List<int> children, JsonObject? extras = null)
    {
        var unique = name;
        for (int k = 2; !_names.Add(unique); k++) unique = $"{name} ({k})";
        var node = new JsonObject
        {
            ["name"] = unique,
            ["translation"] = new JsonArray(pos.X, pos.Y, -pos.Z),
            ["rotation"] = new JsonArray(-rot.X, -rot.Y, rot.Z, rot.W),
            ["scale"] = new JsonArray(scale.X, scale.Y, scale.Z),
        };
        if (mesh != null) node["mesh"] = mesh;
        if (children.Count > 0) node["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray());
        if (extras != null) node["extras"] = extras;
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    public string NodeName(int index) => (string)_nodes[index]!["name"]!;

    public void Save(string path, int root)
    {
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "SkinForge" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(root) }),
            ["nodes"] = _nodes, ["meshes"] = _meshes, ["materials"] = _materials,
            ["accessors"] = _accessors, ["bufferViews"] = _views,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length }),
        };
        if (_materials.Count == 0) json.Remove("materials");
        if (_meshes.Count == 0) { json.Remove("meshes"); json.Remove("accessors"); json.Remove("bufferViews"); json.Remove("buffers"); }
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        int jsonLen = (text.Length + 3) & ~3, binLen = (int)((_bin.Length + 3) & ~3);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonLen + (binLen > 0 ? 8 + binLen : 0)));
        w.Write((uint)jsonLen); w.Write(0x4E4F534Au); w.Write(text); for (int i = text.Length; i < jsonLen; i++) w.Write((byte)' ');
        if (binLen > 0)
        {
            w.Write((uint)binLen); w.Write(0x004E4942u); w.Write(_bin.ToArray()); for (long i = _bin.Length; i < binLen; i++) w.Write((byte)0);
        }
    }
}

// Reads every mesh primitive of a GLB into one Unity-space mesh (node transforms applied).
public static class GlbReader
{
    public static MeshData Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67) throw new BackendException("bad-mesh", $"{path} is not a binary glTF (.glb)");
        int jsonLen = BitConverter.ToInt32(data, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(data, 20, jsonLen))!;
        int binStart = 20 + jsonLen + 8;
        if (json["extensionsRequired"] is JsonArray req && req.Count > 0)
            throw new BackendException("bad-mesh", $"{path} needs {string.Join(", ", req)}; export without compression");

        float[] Floats(int accessor, out int comps)
        {
            var acc = json["accessors"]![accessor]!;
            var view = json["bufferViews"]![(int)acc["bufferView"]!]!;
            comps = (string)acc["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, var t => throw new BackendException("bad-mesh", $"accessor type {t}") };
            int count = (int)acc["count"]!, type = (int)acc["componentType"]!;
            bool norm = (bool?)acc["normalized"] ?? false;
            int size = type switch { 5126 or 5125 => 4, 5123 or 5122 => 2, _ => 1 };
            int stride = (int?)view["byteStride"] ?? size * comps;
            int start = binStart + ((int?)view["byteOffset"] ?? 0) + ((int?)acc["byteOffset"] ?? 0);
            var result = new float[count * comps];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int at = start + i * stride + c * size;
                    result[i * comps + c] = type switch
                    {
                        5126 => BitConverter.ToSingle(data, at),
                        5125 => BitConverter.ToUInt32(data, at),
                        5123 => norm ? BitConverter.ToUInt16(data, at) / 65535f : BitConverter.ToUInt16(data, at),
                        5122 => norm ? Math.Max(BitConverter.ToInt16(data, at) / 32767f, -1) : BitConverter.ToInt16(data, at),
                        5121 => norm ? data[at] / 255f : data[at],
                        _ => norm ? Math.Max((sbyte)data[at] / 127f, -1) : (sbyte)data[at],
                    };
                }
            return result;
        }

        var mesh = new MeshData { Name = Path.GetFileNameWithoutExtension(path) };
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        bool hasNormals = true, hasUv = true;

        void Visit(int nodeIndex, Matrix4x4 parent)
        {
            var node = json["nodes"]![nodeIndex]!;
            var local = node["matrix"] is JsonArray m
                ? new Matrix4x4((float)m[0]!, (float)m[1]!, (float)m[2]!, (float)m[3]!, (float)m[4]!, (float)m[5]!, (float)m[6]!, (float)m[7]!,
                    (float)m[8]!, (float)m[9]!, (float)m[10]!, (float)m[11]!, (float)m[12]!, (float)m[13]!, (float)m[14]!, (float)m[15]!)
                : Matrix4x4.CreateScale(V3(node["scale"], 1)) * Matrix4x4.CreateFromQuaternion(Q(node["rotation"])) * Matrix4x4.CreateTranslation(V3(node["translation"], 0));
            var world = local * parent;
            if (node["mesh"] is { } meshIndex)
                foreach (var prim in json["meshes"]![(int)meshIndex]!["primitives"]!.AsArray())
                {
                    if (((int?)prim!["mode"] ?? 4) != 4) continue;
                    var a = prim["attributes"]!;
                    var p = Floats((int)a["POSITION"]!, out _);
                    int baseVertex = pos.Count, count = p.Length / 3;
                    Matrix4x4.Invert(world, out var inv);
                    var normalMatrix = Matrix4x4.Transpose(inv);
                    for (int i = 0; i < count; i++) pos.Add(Vector3.Transform(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), world));
                    if (a["NORMAL"] is { } na)
                    {
                        var n = Floats((int)na, out _);
                        for (int i = 0; i < count; i++) nrm.Add(Vector3.Normalize(Vector3.TransformNormal(new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]), normalMatrix)));
                    }
                    else hasNormals = false;
                    if (a["TEXCOORD_0"] is { } ta)
                    {
                        var t = Floats((int)ta, out _);
                        for (int i = 0; i < count; i++) uv.Add(new Vector2(t[i * 2], t[i * 2 + 1]));
                    }
                    else hasUv = false;
                    var ind = prim["indices"] is { } ia ? Floats((int)ia, out _).Select(f => (int)f).ToArray() : Enumerable.Range(0, count).ToArray();
                    bool mirrored = world.GetDeterminant() < 0;
                    for (int i = 0; i + 2 < ind.Length; i += 3)
                    {
                        idx.Add(baseVertex + ind[i]);
                        idx.Add(baseVertex + ind[mirrored ? i + 2 : i + 1]);
                        idx.Add(baseVertex + ind[mirrored ? i + 1 : i + 2]);
                    }
                }
            foreach (var c in node["children"]?.AsArray() ?? []) Visit((int)c!, world);
        }

        var scene = json["scenes"]![(int?)json["scene"] ?? 0]!;
        foreach (var n in scene["nodes"]!.AsArray()) Visit((int)n!, Matrix4x4.Identity);
        if (pos.Count == 0 || idx.Count == 0) throw new BackendException("bad-mesh", $"{path} has no triangle meshes");

        // glTF -> Unity: negate Z, flip winding and V.
        mesh.Positions = pos.Select(p => new Vector3(p.X, p.Y, -p.Z)).ToArray();
        if (hasNormals) mesh.Normals = nrm.Select(n => new Vector3(n.X, n.Y, -n.Z)).ToArray();
        if (hasUv) mesh.Uv0 = uv.Select(t => new Vector2(t.X, 1 - t.Y)).ToArray();
        var tri = idx.ToArray();
        for (int i = 0; i < tri.Length; i += 3) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]);
        mesh.SubMeshes.Add(tri);
        return mesh;
    }

    static Vector3 V3(JsonNode? a, float d) => a is JsonArray v ? new((float)v[0]!, (float)v[1]!, (float)v[2]!) : new(d);
    static Quaternion Q(JsonNode? a) => a is JsonArray v ? new((float)v[0]!, (float)v[1]!, (float)v[2]!, (float)v[3]!) : Quaternion.Identity;
}
