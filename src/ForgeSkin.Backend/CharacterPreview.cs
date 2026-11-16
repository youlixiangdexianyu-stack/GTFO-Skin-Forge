using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeSkin.Format;
using ForgeVisual;

namespace ForgeSkin.Backend;

// The 人物模型 tab's three backend steps, all on ForgeCore's own linked code so the editor holds no second copy:
// springs.simulate moves one spring-bone chain set by ForgeCore's chain physics along the anchor bone's path the 3D view sends (as
// charm.simulate does for a charm), character.check holds a bone match's rest pose to ForgeCore's rig check (RigMap), and
// character.links gives the motion preview the game's pose-copy links for a bone match (HumanRig, PoseSolver).
public static class CharacterPreview
{
    public sealed record Node(int Parent, double[] At);
    public sealed record Frame(double Time, double[] M, double[] Body, List<double[]>? Capsules);

    public const int MaxFrames = 1200, MaxNodes = 512;

    /// <summary>The author values each preset starts from, for the editor's sliders (the format's own preset rule, SpringChain.Spec).</summary>
    public static object Presets() => Enum.GetValues<SpringKind>().ToDictionary(k => JsonNamingPolicy.CamelCase.ConvertName(k.ToString()), k =>
    {
        var s = new SpringChain { Kind = k }.Spec();
        return new { stiffness = s.Stiffness, damping = s.Damping, weight = s.Weight, gravity = s.Gravity, radius = s.Radius };
    });

    /// <summary>Node world positions per frame (base64 little-endian float32, three per node in the order of `nodes`): the chain set
    /// `chain` (the document's own entry, its kind and author values) with its bones at rest as `nodes` in the anchor's frame, the
    /// strands' node indices (root down) for a side-linked set.</summary>
    public static object Simulate(SpringChain chain, List<Node> nodes, List<int[]>? strands, List<Frame> frames)
    {
        if (frames.Count is 0 or > MaxFrames) throw new BackendException("bad-request", $"frames: 1 to {MaxFrames}");
        if (frames.Any(f => f.M is not { Length: 16 } || f.Body is not { Length: 3 } || f.Capsules?.Any(c => c is not { Length: 7 }) == true))
            throw new BackendException("bad-request", "frames: m has 16 numbers, body 3, a capsule 7");
        if (nodes.Count is < 2 or > MaxNodes || nodes[0].Parent != -1) throw new BackendException("bad-request", $"nodes: a root first, 2 to {MaxNodes}");
        var spec = chain.Spec();
        var shape = new ChainShape();
        foreach (var n in nodes)
        {
            if (n.At is not { Length: 3 }) throw new BackendException("bad-request", "a node is at three numbers");
            if (n.Parent < 0) { shape.AddRoot(n.At[0], n.At[1], n.At[2]); continue; }
            if (n.Parent >= shape.Count) throw new BackendException("bad-request", "a node's parent comes before it");
            var p = nodes[n.Parent].At;
            double dx = n.At[0] - p[0], dy = n.At[1] - p[1], dz = n.At[2] - p[2];
            double length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), ChainState.MinPiece);
            if (dx == 0 && dy == 0 && dz == 0) dy = -1; // two bones on one point: hang the piece straight down
            shape.Add(n.Parent, length, dx, dy, dz);
        }
        if (chain.LinkSide && strands is { Count: > 1 }) shape.LinkSide(strands, chain.Loop);
        var state = new ChainState(shape, spec);
        var bytes = new byte[state.Count * 3 * 4];
        var floats = new float[state.Count * 3];
        var output = new List<string>(frames.Count);
        foreach (var f in frames)
        {
            var caps = f.Capsules ?? [];
            state.Capsules = caps.Count;
            for (var i = 0; i < state.Capsules; i++)
            {
                var c = caps[i];
                state.SetCapsule(i, c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
            }
            state.Update(f.Time, f.M, f.Body);
            for (var i = 0; i < floats.Length; i++) floats[i] = (float)state.Nodes[i];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            output.Add(Convert.ToBase64String(bytes));
        }
        return new { frames = output };
    }


    /// <summary>The game's pose copy for a bone match (canonical joint to model bone), as the motion preview plays it: per canonical
    /// joint the link ForgeCore's HumanRig gives (the joint its frame points at or comes from, -1 for none) and whether the copy leaves
    /// it out (PoseSolver.Skipped: an optional joint the model lacks, a hand with only some of its fingers, every finger when the
    /// playing body has none), and the limbs that reach (HumanRig.ArmReach: upper, lower, end, anchor).</summary>
    public static object Links(Dictionary<string, string> bones, bool fingers)
    {
        var names = new string?[ClipRig.Count];
        foreach (var (joint, bone) in bones)
        {
            var j = Array.IndexOf(ClipRig.Names, joint);
            if (j < 0) throw new BackendException("bad-request", $"no canonical joint {joint}");
            if (j < ClipRig.BodyCount || fingers) names[j] = bone;
        }
        PoseLink[] links;
        try { links = HumanRig.Links(names); }
        catch (ArgumentException e) { throw new BackendException("bad-request", e.Message); }
        var skip = PoseSolver.Skipped(links, links.Select(l => l.Optional && l.Model.StartsWith('\0')).ToArray());
        return new
        {
            links = links.Select((l, j) => new { joint = ClipRig.Names[j], toward = l.Toward, from = l.From, skip = skip[j] }),
            reach = HumanRig.ArmReach.Select(r => new[] { r.Upper, r.Lower, r.End, r.Anchor }),
        };
    }

    sealed class BoneNode(string name)
    {
        public string Name { get; } = name;
        public BoneNode? Parent { get; set; }
    }

    /// <summary>The first-person arms the game wears for this character (Characters.Arms in the plugin): the model's mesh cut by
    /// ForgeCore's own rule (HumanRig.ArmWear's bones, MeshCut.KeptBones with descendants, then MeshCut's vertex and triangle cut), the
    /// materials in `hide` left out. Only the kept vertices come back, in Unity's axes as the mesh file holds them: positions and normals
    /// (three float32 each), uvs (two), four bone indices (into `bones`, uint16) and weights (float32) per vertex, base64 little-endian;
    /// per submesh its triangles into those vertices (base64 uint32) and its material.</summary>
    public static object Arms(string descriptorPath, string meshPath, Dictionary<string, string> joints, List<int>? hide)
    {
        var descriptor = ImportedModelDescriptor.Parse(File.ReadAllText(descriptorPath), out var failure) ?? throw new BackendException("bad-mesh", failure.Detail);
        if (descriptor.Bones is not { } skeleton) throw new BackendException("bad-mesh", "the model has no skeleton");
        var mesh = ImportedMesh.Read(File.ReadAllBytes(meshPath), descriptor, out failure) ?? throw new BackendException("bad-mesh", failure.Detail);
        var names = new string?[ClipRig.Count];
        foreach (var (joint, bone) in joints)
        {
            var j = Array.IndexOf(ClipRig.Names, joint);
            if (j < 0) throw new BackendException("bad-request", $"no canonical joint {joint}");
            names[j] = bone;
        }
        WearBone[] wear;
        try { wear = HumanRig.ArmWear(names); }
        catch (ArgumentException e) { throw new BackendException("bad-request", e.Message); }
        var nodes = skeleton.Select(b => new BoneNode(b.Name)).ToArray();
        for (var i = 0; i < nodes.Length; i++) nodes[i].Parent = skeleton[i].Parent < 0 ? null : nodes[skeleton[i].Parent];
        var listed = new Dictionary<BoneNode, int>();
        for (var i = 0; i < wear.Length; i++)
            if (nodes.FirstOrDefault(n => n.Name == wear[i].Model) is { } node) listed.TryAdd(node, i);
        var worn = wear.Select(w => w.Worn).ToArray();
        var kept = MeshCut.KeptBones(nodes, n => n, n => n.Parent, n => listed.TryGetValue(n, out var i) ? i : -1, worn, worn.Select(w => !w).ToArray(), descendants: true);
        var count = descriptor.Vertices;
        var bi = mesh.BoneIndices!;
        var bw = mesh.BoneWeights!;
        var weights = new Weights4[count];
        for (var v = 0; v < count; v++)
            weights[v] = new Weights4 { I0 = bi[v * 4], I1 = bi[v * 4 + 1], I2 = bi[v * 4 + 2], I3 = bi[v * 4 + 3], W0 = bw[v * 4], W1 = bw[v * 4 + 1], W2 = bw[v * 4 + 2], W3 = bw[v * 4 + 3] };
        var keep = MeshCut.KeepVertices(weights, kept);
        // Kept vertices renumbered in order.
        var renumber = new int[count];
        var n = 0;
        for (var v = 0; v < count; v++) renumber[v] = keep[v] ? n++ : -1;
        var positions = new float[n * 3];
        var normals = new float[n * 3];
        var uvs = new float[n * 2];
        var index = new ushort[n * 4];
        var weight = new float[n * 4];
        for (var v = 0; v < count; v++)
        {
            var k = renumber[v];
            if (k < 0) continue;
            Array.Copy(mesh.Positions, v * 3, positions, k * 3, 3);
            Array.Copy(mesh.Normals, v * 3, normals, k * 3, 3);
            Array.Copy(mesh.Uvs, v * 2, uvs, k * 2, 2);
            var w = weights[v];
            (index[k * 4], index[k * 4 + 1], index[k * 4 + 2], index[k * 4 + 3]) = ((ushort)w.I0, (ushort)w.I1, (ushort)w.I2, (ushort)w.I3);
            (weight[k * 4], weight[k * 4 + 1], weight[k * 4 + 2], weight[k * 4 + 3]) = (w.W0, w.W1, w.W2, w.W3);
        }
        var submeshes = descriptor.Submeshes.Select(s =>
        {
            var cut = hide?.Contains(s.Material) == true ? [] : MeshCut.KeepTriangles(mesh.Indices[s.IndexStart..(s.IndexStart + s.IndexCount)], keep);
            return new { material = s.Material, triangles = Base64(cut.Select(i => (uint)renumber[i]).ToArray()) };
        }).ToList();
        return new
        {
            vertices = n,
            bones = skeleton.Select(b => b.Name),
            positions = Base64(positions),
            normals = Base64(normals),
            uvs = Base64(uvs),
            index = Base64(index),
            weight = Base64(weight),
            submeshes,
        };
    }

    static string Base64<T>(T[] values) where T : unmanaged => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));

    public sealed record Issue(string Kind, string[] Joints, double? Degrees, string Text);

    /// <summary>A bone match held to the canonical rig: the required joints it lacks, what ForgeCore's rig check finds wrong with its
    /// rest pose (`joints`: canonical name to the bone's rest position in the game's axes, root space), and which hands keep their
    /// fingers (a hand with only some finger joints plays with none).</summary>
    public static object Check(Dictionary<string, double[]> joints)
    {
        var present = new bool[ClipRig.Count];
        var rest = new Vector3[ClipRig.Count];
        foreach (var (name, at) in joints)
        {
            var j = Array.IndexOf(ClipRig.Names, name);
            if (j < 0) throw new BackendException("bad-request", $"no canonical joint {name}");
            if (at is not { Length: 3 } || !at.All(double.IsFinite)) throw new BackendException("bad-request", $"{name}: three finite numbers");
            present[j] = true;
            rest[j] = new Vector3((float)at[0], (float)at[1], (float)at[2]);
        }
        var missing = CharacterRig.Required.Where(n => !present[Array.IndexOf(ClipRig.Names, n)]).ToArray();
        var backwards = RigMap.FacesBackwards(present, rest);
        if (backwards) RigMap.TurnAround(present, rest, new Quaternion[ClipRig.Count]);
        var issues = Sentences(RigMap.Check(present, rest, RigShape.Body));
        var bones = new object?[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++) bones[j] = present[j] ? new object() : null;
        RigMap.DropBrokenFingers(present, bones);
        bool Hand(int first) => Enumerable.Range(first, 15).All(j => present[j]);
        return new { required = CharacterRig.Required, missing, issues, backwards, fingers = new { left = Hand(ClipRig.BodyCount), right = Hand(ClipRig.BodyCount + 15) } };
    }

    // RigMap.Check speaks in sentences for the game log; the editor words them itself, so each known sentence becomes a kind with its
    // joints. A sentence of another form (a later check) still reaches the author as it is.
    static readonly (string Kind, Regex Form)[] Forms =
    [
        ("samePoint", new Regex(@"^(\w+) and (\w+) rest on one point\.$")),
        ("standing", new Regex(@"^(\w+) rests (\d+) degrees from where it stands on the rig")),
        ("sides", new Regex(@"^(\w+) is on the body's right")),
    ];

    static List<Issue> Sentences(string text)
    {
        var list = new List<Issue>();
        foreach (var raw in Regex.Split(text.Trim(), @"(?<=\.)\s+(?=[a-z])"))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            var known = Forms.Select(f => (f.Kind, Match: f.Form.Match(s))).FirstOrDefault(x => x.Match.Success);
            if (known.Match == null) { list.Add(new Issue("other", [], null, s)); continue; }
            var g = known.Match.Groups;
            list.Add(known.Kind switch
            {
                "samePoint" => new Issue("samePoint", [g[1].Value, g[2].Value], null, s),
                "standing" => new Issue("standing", [g[1].Value], double.Parse(g[2].Value), s),
                _ => new Issue("sides", [g[1].Value], null, s),
            });
        }
        return list;
    }
}
