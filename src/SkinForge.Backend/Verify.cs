using System.Numerics;
using System.Text.Json;

namespace SkinForge.Backend;

public sealed record ReadbackNode(string Part, string? Path, float[] Position, float[] Rotation, float[] Scale);
public sealed record Readback(uint GearId, string? Gear, List<ReadbackNode> Nodes);
public sealed record VerifyOffender(string Part, string? Path, double PositionMm, double RotationDeg, double Scale, string? Problem);
// Misses: every node beyond tolerance or missing, worst first. Ignored: game subtrees under a "(Clone)" that is not one of
// the gear's parts (runtime effects such as muzzle flashes), listed by their root path.
public sealed record VerifyResult(string File, uint GearId, string? Weapon, bool Pass, int Compared, List<VerifyOffender> Misses, List<string> Ignored);

// assembly.verify (PLAN §3.4 readback): the plugin's vanilla gear transforms, relative to the gear root, against this
// backend's assembly of the same weapon. Fails beyond 1 mm, 0.1 degrees or 0.001 scale, or when a node is missing.
public static class Verify
{
    const double MaxMm = 1, MaxDeg = 0.1, MaxScale = 0.001;

    public static List<VerifyResult> Profile(GameAssets g, string profile)
    {
        var dir = Path.Combine(profile, "BepInEx", "SkinForge", "readback");
        if (!Directory.Exists(dir)) throw new BackendException("not-found", $"No readback folder {dir}; set Readback = true in SkinForge.cfg and equip the gear");
        var catalog = Catalog.Get(g);
        return Directory.GetFiles(dir, "*.json").Order().Select(file =>
        {
            var rb = JsonSerializer.Deserialize<Readback>(File.ReadAllText(file), Program.Json)!;
            var item = catalog.FirstOrDefault(i => i.GearId == rb.GearId);
            if (item == null)
                return new VerifyResult(file, rb.GearId, null, false, 0, [new VerifyOffender("", null, 0, 0, 0, "gear not in the catalog")], []);
            return Compare(file, rb, item.Id, item.Build(g));
        }).ToList();
    }

    public static VerifyResult Compare(string file, Readback rb, string weapon, Node holder)
    {
        var ours = new Dictionary<(string, string), Node>();
        foreach (var (part, path, node) in Assembly.PartNodes(holder, activeOnly: false)) ours.TryAdd((part, path ?? ""), node);
        var partByName = holder.All().Where(n => n.Part != null).GroupBy(n => n.Name).ToDictionary(x => x.Key, x => x.First().Part!);
        var rows = new List<(double Score, VerifyOffender Row)>();
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var read in rb.Nodes)
        {
            // The game parents mounted parts under the aligns of other parts as "<prefab>(Clone)": re-key the node to the
            // innermost such part (its path below the clone); clones that are no part of ours are runtime effects.
            var (part, path) = (read.Part, read.Path);
            var segments = (read.Path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            int clone = Array.FindLastIndex(segments, s => s.EndsWith("(Clone)"));
            if (clone >= 0)
            {
                if (!partByName.TryGetValue(segments[clone][..^"(Clone)".Length], out var owner))
                {
                    ignored.Add($"{read.Part}/{string.Join('/', segments.Take(clone + 1))}");
                    continue;
                }
                (part, path) = (owner, clone == segments.Length - 1 ? null : string.Join('/', segments.Skip(clone + 1)));
            }
            var n = read with { Part = part, Path = path };
            if (!ours.TryGetValue((n.Part, n.Path ?? ""), out var node))
            {
                rows.Add((double.MaxValue, new VerifyOffender(n.Part, n.Path, 0, 0, 0, "not in the backend assembly")));
                continue;
            }
            Matrix4x4.Decompose(node.World, out var scale, out var rotation, out var position);
            double mm = Vector3.Distance(position, new Vector3(n.Position[0], n.Position[1], n.Position[2])) * 1000;
            var q = new Quaternion(n.Rotation[0], n.Rotation[1], n.Rotation[2], n.Rotation[3]);
            double deg = 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(q), Quaternion.Normalize(rotation))))) * 180 / Math.PI;
            double ds = (scale - new Vector3(n.Scale[0], n.Scale[1], n.Scale[2])).Length();
            rows.Add((Math.Max(mm / MaxMm, Math.Max(deg / MaxDeg, ds / MaxScale)), new VerifyOffender(n.Part, n.Path, Math.Round(mm, 3), Math.Round(deg, 3), Math.Round(ds, 5), null)));
        }
        return new VerifyResult(file, rb.GearId, weapon, rows.All(r => r.Score <= 1), rows.Count,
            rows.Where(r => r.Score > 1).OrderByDescending(r => r.Score).Select(r => r.Row).ToList(), ignored.ToList());
    }
}
