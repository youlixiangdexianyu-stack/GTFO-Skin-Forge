using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace SkinForge.Backend;

// Assembled item -> OBJ (one file per part, or one merged file with continuous indices), MTL with the game material
// names and a manifest, in a timestamped folder. OBJ is right-handed: X is negated from Unity and winding flipped.
public static class ObjExport
{
    public static object Export(GameAssets g, string itemId, List<string>? parts, string mode, string outDir)
    {
        if (mode is not ("separate" or "merged")) throw new BackendException("bad-request", "mode: separate or merged");
        var item = Catalog.Item(Catalog.Get(g), itemId);
        var selected = parts ?? item.Parts.Select(p => p.Id).ToList();
        var unknown = selected.Except(item.Parts.Select(p => p.Id)).ToList();
        if (selected.Count == 0 || unknown.Count > 0) throw new BackendException("bad-request", $"Unknown or no parts: {string.Join(", ", unknown)}");

        var root = item.Build(g);
        var meshCache = new Dictionary<(string, long), MeshData>();
        MeshData MeshOf(Node n) => meshCache.TryGetValue((n.Mesh!.File.path, n.Mesh.PathId), out var m) ? m : meshCache[(n.Mesh.File.path, n.Mesh.PathId)] = Unity.ReadMesh(g, n.Mesh);
        // Ammo as the editor shows it: one piece per ammo part, in a row below the gun; the other rounds are left out.
        var ammo = Catalog.AmmoLayout(root, MeshOf);
        Vector3? Offset(Node n)
        {
            for (Node? p = n; p != null; p = p.Parent)
            {
                if (ammo.Any(a => a.Hidden.Contains(p))) return null;
                if (ammo.FirstOrDefault(a => a.Shown == p) is { } a) return a.Offset;
            }
            return Vector3.Zero;
        }
        var renderers = Catalog.Renderers(root).Where(r => selected.Contains(r.Part) && Offset(r.Node) != null).ToList();
        var display = Preview.Safe(item.NameZh != null ? $"{item.NameZh} · {item.Name}" : item.Name);
        var stamp = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-{display}.OBJ";
        var dir = Path.Combine(outDir, stamp);
        for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(outDir, $"{stamp}-{n}");
        Directory.CreateDirectory(dir);
        var mtl = $"{display}_materials.mtl";
        var files = new List<string>();
        var materials = new SortedSet<string>(StringComparer.Ordinal);
        var I = CultureInfo.InvariantCulture;

        // Appends one part; returns the new (v, vt, vn) counts.
        (int, int, int) Part(StringBuilder sb, string partId, (int V, int T, int N) at)
        {
            sb.Append("o ").Append(partId).Append('\n');
            foreach (var (node, part) in renderers.Where(r => r.Part == partId))
            {
                var mesh = MeshOf(node);
                var world = node.World * Matrix4x4.CreateTranslation(Offset(node)!.Value);
                Matrix4x4.Invert(world, out var inv);
                var normalMatrix = Matrix4x4.Transpose(inv);
                sb.Append("g ").Append(node.Name).Append('\n');
                foreach (var p in mesh.Positions) { var w = Vector3.Transform(p, world); sb.Append(I, $"v {-w.X:0.#########} {w.Y:0.#########} {w.Z:0.#########}\n"); }
                if (mesh.Uv0 != null) foreach (var t in mesh.Uv0) sb.Append(I, $"vt {t.X:0.#########} {t.Y:0.#########}\n");
                if (mesh.Normals != null) foreach (var nrm in mesh.Normals) { var w = Vector3.Normalize(Vector3.TransformNormal(nrm, normalMatrix)); sb.Append(I, $"vn {-w.X:0.######} {w.Y:0.######} {w.Z:0.######}\n"); }
                bool mirrored = world.GetDeterminant() < 0;
                for (int s = 0; s < mesh.SubMeshes.Count; s++)
                {
                    var material = s < node.Materials.Count ? node.Materials[s].Name : node.Materials[^1].Name;
                    materials.Add(material);
                    sb.Append("usemtl ").Append(material).Append('\n');
                    var idx = mesh.SubMeshes[s];
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        sb.Append('f');
                        foreach (var v in mirrored ? new[] { idx[i], idx[i + 1], idx[i + 2] } : [idx[i], idx[i + 2], idx[i + 1]])
                        {
                            sb.Append(' ').Append(at.V + v + 1);
                            if (mesh.Uv0 != null || mesh.Normals != null) sb.Append('/').Append(mesh.Uv0 != null ? (at.T + v + 1).ToString(I) : "");
                            if (mesh.Normals != null) sb.Append('/').Append(at.N + v + 1);
                        }
                        sb.Append('\n');
                    }
                }
                at = (at.V + mesh.Positions.Length, at.T + (mesh.Uv0?.Length ?? 0), at.N + (mesh.Normals?.Length ?? 0));
            }
            return at;
        }

        string Header() => $"# GTFO Skin Forge OBJ, GTFO weapon space in metres\nmtllib {mtl}\n";
        var exported = selected.Where(p => renderers.Any(r => r.Part == p)).ToList();
        if (mode == "merged")
        {
            var sb = new StringBuilder(Header());
            var at = (0, 0, 0);
            foreach (var p in exported) at = Part(sb, p, at);
            files.Add(Write(Path.Combine(dir, $"{display}_assembled.obj"), sb.ToString()));
        }
        else
            foreach (var p in exported)
            {
                var sb = new StringBuilder(Header());
                Part(sb, p, (0, 0, 0));
                files.Add(Write(Path.Combine(dir, $"{Preview.Safe(p)}.obj"), sb.ToString()));
            }
        Write(Path.Combine(dir, mtl), "# GTFO Skin Forge materials\n" + string.Concat(materials.Select(m =>
            $"newmtl {m}\nKa 1.000000 1.000000 1.000000\nKd 1.000000 1.000000 1.000000\nKs 0.000000 0.000000 0.000000\nd 1.000000\nillum 2\n\n")));
        var manifest = Write(Path.Combine(dir, "obj-export-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            application = "GTFO Skin Forge",
            createdAt = DateTimeOffset.UtcNow,
            item = item.Id,
            name = display,
            mode,
            units = "metres",
            parts = exported.Select(p => new { id = p, materials = item.Parts.First(x => x.Id == p).Materials }),
            files = files.Select(Path.GetFileName),
        }, Program.Json));
        return new { folder = dir, files, mtl = Path.Combine(dir, mtl), manifest };
    }

    static string Write(string path, string text)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
