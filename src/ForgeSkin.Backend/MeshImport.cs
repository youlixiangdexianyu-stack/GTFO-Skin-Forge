using System.Numerics;
using System.Security.Cryptography;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// GLB attachment -> .forgeskinmesh (Unity space) plus a GLB written back from it, so the editor shows exactly what the game gets.
// A .forgeskinmesh (a skin's model whose preview is gone) is kept as it is and only gets the GLB.
public static class MeshImport
{
    public static object Import(string glbPath)
    {
        var bytes = File.Exists(glbPath) ? File.ReadAllBytes(glbPath) : throw new BackendException("not-found", $"{glbPath} not found");
        var own = ForgeSkinMesh.IsMesh(bytes);
        var mesh = own ? FromForgeSkinMesh(ForgeSkinMesh.Read(bytes)) : GlbReader.Read(glbPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var dir = Path.Combine(Paths.Cache, "meshes");
        Directory.CreateDirectory(dir);
        var name = $"{Preview.Safe(Path.GetFileNameWithoutExtension(glbPath))}-{hash}";
        var forgeskinmesh = own ? glbPath : Path.Combine(dir, name + ".forgeskinmesh");
        if (!own) File.WriteAllBytes(forgeskinmesh, ToForgeSkinMesh(mesh).Write());

        var glb = new GlbWriter();
        int m = glb.Mesh(mesh, ["attachment"]);
        var preview = Path.Combine(dir, name + ".glb");
        glb.Save(preview, glb.Node(mesh.Name, Vector3.Zero, Quaternion.Identity, Vector3.One, m, new()));

        var min = mesh.Positions.Aggregate(Vector3.Min);
        var max = mesh.Positions.Aggregate(Vector3.Max);
        return new { forgeskinmesh, glb = preview, bounds = new { min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } } };
    }

    public static ForgeSkinMesh ToForgeSkinMesh(MeshData m) => new()
    {
        Positions = m.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Normals = m.Normals?.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Uv0 = m.Uv0?.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
        Indices = m.SubMeshes.SelectMany(s => s).Select(i => (uint)i).ToArray(),
    };

    static MeshData FromForgeSkinMesh(ForgeSkinMesh m) => new()
    {
        Positions = Enumerable.Range(0, m.Positions.Length / 3).Select(i => new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2])).ToArray(),
        Normals = m.Normals == null ? null : Enumerable.Range(0, m.Normals.Length / 3).Select(i => new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2])).ToArray(),
        Uv0 = m.Uv0 == null ? null : Enumerable.Range(0, m.Uv0.Length / 2).Select(i => new Vector2(m.Uv0[i * 2], m.Uv0[i * 2 + 1])).ToArray(),
        SubMeshes = [m.Indices.Select(i => (int)i).ToArray()],
    };
}
