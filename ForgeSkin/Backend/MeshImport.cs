using System.Numerics;
using System.Security.Cryptography;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// GLB attachment -> .forgeskinmesh (Unity space) plus a GLB written back from it, so the editor shows exactly what the game gets.
public static class MeshImport
{
    public static object Import(string glbPath)
    {
        var mesh = GlbReader.Read(glbPath);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(glbPath)))[..16].ToLowerInvariant();
        var dir = Path.Combine(Paths.Cache, "meshes");
        Directory.CreateDirectory(dir);
        var name = $"{Preview.Safe(Path.GetFileNameWithoutExtension(glbPath))}-{hash}";
        var forgeskinmesh = Path.Combine(dir, name + ".forgeskinmesh");
        File.WriteAllBytes(forgeskinmesh, ToForgeSkinMesh(mesh).Write());

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
}
