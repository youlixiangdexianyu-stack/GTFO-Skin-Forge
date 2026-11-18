using System.Numerics;
using System.Text.Json.Nodes;
using AssetsTools.NET.Extra;

namespace ForgeSkin.Backend;

// One joint of a hand: its bone in the game's first-person rig, its parent's index in the list (-1 for the hand itself, which is first)
// and its rest pose under that parent in Unity space (rotation x, y, z, w).
public sealed record HandJoint(string Bone, int Parent, float[] Position, float[] Rotation);
// One hand as the editor draws it: the hand bone and its 15 finger joints (thumb, index, middle, ring, little; three each, in the
// order of hand-shapes.json), and the glove's skin in the hand bone's space at rest (Unity space): positions and normals
// (three numbers a vertex), triangles, and four joint indices and weights a vertex. Skin that follows the forearm follows the hand.
public sealed record HandModel(List<HandJoint> Joints, float[] Positions, float[] Normals, int[] Indices, int[] SkinIndex, float[] SkinWeight);
// Both hands, and per hand ("left", "right") each of the game's grip animations as the local rotations (x, y, z, w, Unity space) of
// that hand's 15 finger joints: hand-shapes.json, measured once in game on the first-person arms.
public sealed record HandsResult(HandModel Left, HandModel Right, JsonNode Shapes);

// The game's own first-person hands, read from the player's game files: the gloves of the first-person body every character wears its
// clothes on (FPSBody_genericRigForClothes), whose finger bones the grip animations move.
public static class Hands
{
    const string File = "sharedassets1.assets", Glove = "Gloves001";
    static readonly string[] Fingers = ["Thumb", "Index", "Middle", "Ring", "Pinky"];
    static readonly JsonNode Shapes = JsonNode.Parse(typeof(Hands).Assembly.GetManifestResourceStream("hand-shapes.json")!)!;

    public static HandsResult Get(GameAssets g)
    {
        var file = g.Load(File);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.SkinnedMeshRenderer))
        {
            var renderer = g.Field(file, info.PathId);
            if (g.Field(g.Resolve(file, renderer["m_GameObject"]))["m_Name"].AsString != Glove) continue;
            var bones = renderer["m_Bones.Array"].Children.Select(b => g.Resolve(file, b))
                .Select(t => t.PathId == 0 ? "" : g.Field(g.Resolve(t.File, g.Field(t)["m_GameObject"]))["m_Name"].AsString).ToList();
            if (!bones.Contains("LeftIndex1") || !bones.Contains("RightIndex1")) continue;
            var at = g.Resolve(file, renderer["m_Mesh"]);
            var mesh = Unity.ReadMesh(g, new MeshRef(at.File, at.PathId, Glove));
            if (mesh.BoneWeights == null || mesh.BindPoses == null || mesh.BindPoses.Length != bones.Count)
                throw new BackendException("bad-mesh", $"{Glove}: the game's hand mesh has no skin that fits its bones");
            return new HandsResult(Hand(mesh, bones, "Left"), Hand(mesh, bones, "Right"), Shapes);
        }
        throw new BackendException("bad-mesh", $"the game's first-person hands ({Glove}) are not in {File}");
    }

    static HandModel Hand(MeshData mesh, List<string> bones, string side)
    {
        var names = new List<string> { side + "Hand" };
        foreach (var finger in Fingers) for (var k = 1; k <= 3; k++) names.Add(side + finger + k);
        var bone = names.Select(n => bones.IndexOf(n)).ToArray();
        if (bone.Any(b => b < 0)) throw new BackendException("bad-mesh", $"{Glove}: no bone {names[Array.FindIndex(bone, b => b < 0)]}");
        var jointOf = new Dictionary<int, int>();
        for (var j = 0; j < bone.Length; j++) jointOf[bone[j]] = j;
        var binds = mesh.BindPoses!;

        var joints = new List<HandJoint> { new(names[0], -1, [0, 0, 0], [0, 0, 0, 1]) };
        for (var j = 1; j < names.Count; j++)
        {
            var parent = (j - 1) % 3 == 0 ? 0 : j - 1;
            // A point in the joint's space, through mesh space, into its parent's space.
            Matrix4x4.Invert(binds[bone[j]], out var fromJoint);
            Matrix4x4.Decompose(fromJoint * binds[bone[parent]], out _, out var turn, out var place);
            joints.Add(new HandJoint(names[j], parent, [place.X, place.Y, place.Z], [turn.X, turn.Y, turn.Z, turn.W]));
        }

        // The vertices this side's bones carry more than the other side's.
        var weights = mesh.BoneWeights!;
        var indices = mesh.BoneIndices!;
        var mine = bones.Select(b => b.StartsWith(side, StringComparison.Ordinal)).ToArray();
        var other = bones.Select(b => b.StartsWith(side == "Left" ? "Right" : "Left", StringComparison.Ordinal)).ToArray();
        var remap = new int[mesh.Positions.Length];
        List<float> positions = [], normals = [], skinWeight = [];
        List<int> skinIndex = [];
        var toHand = binds[bone[0]];
        for (var v = 0; v < remap.Length; v++)
        {
            float own = 0, theirs = 0;
            var perJoint = new float[names.Count];
            for (var k = 0; k < 4; k++)
            {
                var (b, w) = (indices[v * 4 + k], weights[v * 4 + k]);
                if (w <= 0 || b < 0 || b >= bones.Count) continue;
                if (mine[b]) own += w; else if (other[b]) theirs += w;
                perJoint[jointOf.GetValueOrDefault(b, 0)] += w;
            }
            remap[v] = -1;
            if (own <= theirs) continue;
            remap[v] = positions.Count / 3;
            var p = Vector3.Transform(mesh.Positions[v], toHand);
            var n = mesh.Normals != null ? Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[v], toHand)) : Vector3.UnitY;
            positions.AddRange([p.X, p.Y, p.Z]);
            normals.AddRange([n.X, n.Y, n.Z]);
            var top = Enumerable.Range(0, perJoint.Length).OrderByDescending(j => perJoint[j]).Take(4).ToArray();
            var sum = top.Sum(j => perJoint[j]);
            foreach (var j in top) { skinIndex.Add(j); skinWeight.Add(perJoint[j] / sum); }
        }
        var triangles = new List<int>();
        foreach (var sub in mesh.SubMeshes)
            for (var i = 0; i + 2 < sub.Length; i += 3)
                if (remap[sub[i]] >= 0 && remap[sub[i + 1]] >= 0 && remap[sub[i + 2]] >= 0)
                    triangles.AddRange([remap[sub[i]], remap[sub[i + 1]], remap[sub[i + 2]]]);
        return new HandModel(joints, positions.ToArray(), normals.ToArray(), triangles.ToArray(), skinIndex.ToArray(), skinWeight.ToArray());
    }
}
