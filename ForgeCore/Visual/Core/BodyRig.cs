using System;
using System.Collections.Generic;
using System.Numerics;

namespace ForgeVisual;

/// <summary>A humanoid body bone, numbered as Unity's <c>HumanBodyBones</c> (so a value casts straight to it). The 30 finger joints
/// (24 to 53) are listed for the pose copy (<see cref="PoseFingers"/>); eyes and jaw are not: no body mechanism of this library uses them.</summary>
public enum BodyBone
{
    Hips = 0, LeftUpperLeg = 1, RightUpperLeg = 2, LeftLowerLeg = 3, RightLowerLeg = 4, LeftFoot = 5, RightFoot = 6,
    Spine = 7, Chest = 8, Neck = 9, Head = 10, LeftShoulder = 11, RightShoulder = 12, LeftUpperArm = 13, RightUpperArm = 14,
    LeftLowerArm = 15, RightLowerArm = 16, LeftHand = 17, RightHand = 18, LeftToes = 19, RightToes = 20, UpperChest = 54,
    LeftThumbProximal = 24, LeftThumbIntermediate = 25, LeftThumbDistal = 26,
    LeftIndexProximal = 27, LeftIndexIntermediate = 28, LeftIndexDistal = 29,
    LeftMiddleProximal = 30, LeftMiddleIntermediate = 31, LeftMiddleDistal = 32,
    LeftRingProximal = 33, LeftRingIntermediate = 34, LeftRingDistal = 35,
    LeftLittleProximal = 36, LeftLittleIntermediate = 37, LeftLittleDistal = 38,
    RightThumbProximal = 39, RightThumbIntermediate = 40, RightThumbDistal = 41,
    RightIndexProximal = 42, RightIndexIntermediate = 43, RightIndexDistal = 44,
    RightMiddleProximal = 45, RightMiddleIntermediate = 46, RightMiddleDistal = 47,
    RightRingProximal = 48, RightRingIntermediate = 49, RightRingDistal = 50,
    RightLittleProximal = 51, RightLittleIntermediate = 52, RightLittleDistal = 53,
}

/// <summary>Which length of the two skeletons sets a worn bone's scale (game over model): the shoulder span (upper arm to upper arm),
/// or one limb's two segments (upper leg to lower leg to foot, upper arm to lower arm to hand). A limb whose three bones are not all
/// listed and on the game's body falls back to the shoulder span.</summary>
public enum BodyMeasure { ShoulderSpan, LeftLeg, RightLeg, LeftArm, RightArm }

/// <summary>One bone of a model worn on the player's first-person body (<c>Models.WearFirstPerson</c>): the model's bone
/// <see cref="Model"/> sits on the game's body bone <see cref="Body"/>. A bone that is not <see cref="Worn"/> only lines the two
/// skeletons up (it is neither bound nor drawn); <see cref="Scale"/> says which length sizes it.</summary>
public readonly struct WearBone
{
    public WearBone(string model, BodyBone body, bool worn = true, BodyMeasure scale = BodyMeasure.ShoulderSpan)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Body = body;
        Worn = worn;
        Scale = scale;
    }

    public string Model { get; }
    public BodyBone Body { get; }
    public bool Worn { get; }
    public BodyMeasure Scale { get; }
}

/// <summary>
/// One model bone of a pose copy (<c>Models.ReadPoseTarget</c>, <c>Models.CopyPose</c>) and the body bone it copies. Each bone's frame
/// is its direction, toward the joint of link <see cref="Toward"/> (or of the body bone <see cref="TowardBody"/> when the body has it,
/// else <see cref="TowardBodyAlt"/>) or along from link <see cref="From"/>'s, with the body's front as its up; a bone with neither
/// takes the body's own frame. Both skeletons are read that way at rest, so different rest poses (arms out, arms down) give one and
/// the same pose. <see cref="BodyAlt"/> stands in for <see cref="Body"/> on a body without it (an optional bone such as the upper
/// chest). Link 0 is the root: its position is copied too (height and sway scaled by hip height over hip height). An
/// <see cref="Optional"/> link is skipped, not an error, when the model or the body lacks its bone; so is every optional link that points
/// at a skipped one (a finger joint after a missing joint). A link that is not optional may not point at an optional one.
/// </summary>
public readonly struct PoseLink
{
    public PoseLink(string model, BodyBone body, int toward = -1, int from = -1, BodyBone? bodyAlt = null, BodyBone? towardBody = null, BodyBone? towardBodyAlt = null, bool optional = false)
    {
        Optional = optional;
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Body = body;
        Toward = toward;
        From = from;
        BodyAlt = bodyAlt;
        TowardBody = towardBody;
        TowardBodyAlt = towardBodyAlt;
    }

    public string Model { get; }
    public BodyBone Body { get; }
    public BodyBone? BodyAlt { get; }
    public int Toward { get; }
    public int From { get; }
    public BodyBone? TowardBody { get; }
    public BodyBone? TowardBodyAlt { get; }
    public bool Optional { get; }
}

/// <summary>The 30 finger links of a pose copy (<see cref="PoseLink"/>), every joint optional: a model or body without a joint skips it
/// and the joints after it on that finger. Each joint copies the body's turn (not an IK) with its direction taken from the joint before.</summary>
public static class PoseFingers
{
    /// <summary>Finger joints per hand and in all.</summary>
    public const int PerHand = 15, Count = 30;

    /// <summary>The 30 links in Unity's order (left hand thumb to little, then right). They go at link indices <paramref name="first"/>
    /// to <paramref name="first"/> + 29 of the caller's list, after the hand links <paramref name="leftHand"/> and <paramref name="rightHand"/>
    /// (a finger's first joint points from its hand). <paramref name="modelName"/> gives the model's bone of a joint; null names it as the
    /// joint is (<c>LeftIndexProximal</c>).</summary>
    public static PoseLink[] Links(int first, int leftHand, int rightHand, Func<BodyBone, string>? modelName = null)
    {
        var links = new PoseLink[Count];
        for (var i = 0; i < Count; i++)
        {
            var bone = (BodyBone)(24 + i);
            var hand = i < PerHand ? leftHand : rightHand;
            links[i] = new PoseLink(modelName != null ? modelName(bone) : bone.ToString(), bone, from: i % 3 == 0 ? hand : first + i - 1, optional: true);
        }
        return links;
    }
}

/// <summary>
/// The pose copy of a skeleton mapped onto the canonical humanoid (<see cref="ClipRig"/>), for a model with its own humanoid skeleton
/// that follows a player's body (a character model, a stand-in): link <c>j</c> is canonical joint <c>j</c>, so a caller finds the model
/// bone of a joint as <c>PoseTarget.Bone(j)</c>. Only the hips need a bone: they are the copy's root, whose place and height the model
/// takes from the body. Any other joint may have none: its link is left out and that part of the model keeps its rest pose on the bone
/// above it. A finger joint is copied when its hand and the joints before it on that finger have bones.
/// </summary>
public static class HumanRig
{
    // Per body joint: the joints down its chain its frame may point at, nearest first. The optional joints (upper chest, shoulders)
    // are no one's direction: a link that is not optional may not point at one.
    private static readonly int[][] Down =
    {
        new[] { 7, 8, 18, 19 }, new[] { 2, 3 }, new[] { 3 }, Array.Empty<int>(), new[] { 5, 6 }, new[] { 6 }, Array.Empty<int>(),
        new[] { 8, 18, 19 }, new[] { 18, 19 }, new[] { 18, 19 }, new[] { 11, 12, 13 }, new[] { 12, 13 }, new[] { 13 }, Array.Empty<int>(),
        new[] { 15, 16, 17 }, new[] { 16, 17 }, new[] { 17 }, Array.Empty<int>(), new[] { 19 }, Array.Empty<int>(),
    };

    // ClipRig joint -> humanoid body bone, the fingers after (both in Unity's thumb-to-little order).
    private static readonly BodyBone[] Body =
    {
        BodyBone.Hips, BodyBone.LeftUpperLeg, BodyBone.LeftLowerLeg, BodyBone.LeftFoot, BodyBone.RightUpperLeg, BodyBone.RightLowerLeg, BodyBone.RightFoot,
        BodyBone.Spine, BodyBone.Chest, BodyBone.UpperChest, BodyBone.LeftShoulder, BodyBone.LeftUpperArm, BodyBone.LeftLowerArm, BodyBone.LeftHand,
        BodyBone.RightShoulder, BodyBone.RightUpperArm, BodyBone.RightLowerArm, BodyBone.RightHand, BodyBone.Neck, BodyBone.Head,
    };

    // Whether the body may lack a joint the model has (the upper chest, the shoulders); a joint the model has no bone for is always optional.
    private static readonly bool[] Optional = { false, false, false, false, false, false, false, false, false, true, true, false, false, false, true, false, false, false, false, false };

    /// <summary>Both arms bend as the body's do, each hand placed from its own shoulder at the arm's own proportions. A copy leaves out
    /// the reach of an arm whose upper arm, forearm or hand has no bone (<c>PoseCopy</c>).</summary>
    public static readonly PoseReach[] ArmReach = { new(11, 12, 13), new(15, 16, 17) };

    /// <summary><see cref="Links(IReadOnlyList{string?}, IReadOnlyList{Vector3}?)"/> without rest positions.</summary>
    public static PoseLink[] Links(IReadOnlyList<string?> bones) => Links(bones, null);

    /// <summary>The links for <paramref name="bones"/> (the model bone of each <see cref="ClipRig"/> joint, null for none). The hips must
    /// have one (throws <see cref="ArgumentException"/>): the model's root follows the body's hips. A joint without a bone is left out of the
    /// copy, with the finger joints after it on its finger and the fingers of a hand without one; <see cref="Unposed"/> names them. Each
    /// bone's frame points at the nearest joint down its chain that has a bone (a thigh at the foot when there is no shin), a hand's and a
    /// finger joint's from the nearest above; with none it takes the body bone's turn from rest. <paramref name="rest"/>, when given, is
    /// each bone's rest position in the model's space: a joint resting on the very point of the bone asked about gives no direction, and
    /// the next one is taken.</summary>
    public static PoseLink[] Links(IReadOnlyList<string?> bones, IReadOnlyList<Vector3>? rest)
    {
        if (bones.Count != ClipRig.Count) throw new ArgumentException("one bone per ClipRig joint, " + ClipRig.Count + " in all", nameof(bones));
        if (rest != null && rest.Count != ClipRig.Count) throw new ArgumentException("one rest position per ClipRig joint, " + ClipRig.Count + " in all", nameof(rest));
        if (bones[0] == null) throw new ArgumentException("the joint hip has no bone: the model's root follows the body's hips, so one bone must stand for them", nameof(bones));
        var has = new bool[ClipRig.Count];
        for (var j = 0; j < ClipRig.BodyCount; j++) has[j] = bones[j] != null;
        // A finger joint needs its hand and the joints before it on its finger (parents come first in ClipRig).
        for (var j = ClipRig.BodyCount; j < ClipRig.Count; j++) has[j] = bones[j] != null && has[ClipRig.Parent[j]];
        // A joint without a bone gets a name no skeleton has, so its optional link is skipped and the names stay unique.
        string Name(int j) => has[j] ? bones[j]! : "\0" + ClipRig.Names[j];
        bool Points(int j, int at) => has[at] && (rest == null || (rest[at] - rest[j]).Length() >= ClipRig.ShortestBone);
        var links = new PoseLink[ClipRig.Count];
        for (var j = 0; j < ClipRig.BodyCount; j++)
        {
            var optional = Optional[j] || !has[j];
            int toward = -1, from = -1;
            foreach (var at in Down[j]) if (Points(j, at)) { toward = at; break; }
            // A hand points on from its forearm, else from its upper arm.
            if (j is 13 or 17) from = Points(j, j - 1) ? j - 1 : Points(j, j - 2) ? j - 2 : -1;
            links[j] = new PoseLink(Name(j), Body[j], toward, from, towardBody: j is 8 or 9 && toward == 18 ? BodyBone.Neck : null, optional: optional);
        }
        for (var j = ClipRig.BodyCount; j < ClipRig.Count; j++)
        {
            // From the joint before it on its finger, else the one before that, the hand last.
            var from = -1;
            for (var up = ClipRig.Parent[j]; from < 0; up = ClipRig.Parent[up])
            {
                if (Points(j, up)) from = up;
                if (up < ClipRig.BodyCount) break;
            }
            links[j] = new PoseLink(Name(j), (BodyBone)((int)BodyBone.LeftThumbProximal + j - ClipRig.BodyCount), from: from, optional: true);
        }
        return links;
    }

    /// <summary>The joints <paramref name="links"/> (from <see cref="Links(IReadOnlyList{string?}, IReadOnlyList{Vector3}?)"/>) leave out
    /// of the copy, by <see cref="ClipRig"/> name: those parts of the model keep their rest pose on the bone above them.</summary>
    public static string[] Unposed(IReadOnlyList<PoseLink> links)
    {
        var names = new List<string>();
        for (var j = 0; j < links.Count && j < ClipRig.Count; j++) if (links[j].Model[0] == '\0') names.Add(ClipRig.Names[j]);
        return names.ToArray();
    }

    /// <summary><see cref="ArmWear(IReadOnlyList{string?}, IReadOnlyList{Vector3}?)"/> without rest positions.</summary>
    public static WearBone[] ArmWear(IReadOnlyList<string?> bones) => ArmWear(bones, null);

    /// <summary>The bones that wear the arms of a model on this rig on the first-person body (<c>Models.WearFirstPerson</c> with
    /// <c>Descendants</c>): the hips line the skeletons up, each upper arm, forearm, hand and finger joint that has a bone (as
    /// <see cref="Links(IReadOnlyList{string?}, IReadOnlyList{Vector3}?)"/> says) is worn on the game's and sized by its own arm. Empty when
    /// either upper arm has no bone: the two skeletons are lined up by both upper arms and the hips, so such a model wears no first-person arms.</summary>
    public static WearBone[] ArmWear(IReadOnlyList<string?> bones, IReadOnlyList<Vector3>? rest)
    {
        var links = Links(bones, rest);
        if (links[11].Model[0] == '\0' || links[15].Model[0] == '\0') return Array.Empty<WearBone>();
        var wear = new List<WearBone> { new(links[0].Model, BodyBone.Hips, worn: false) };
        foreach (var j in new[] { 11, 12, 13, 15, 16, 17 })
            if (links[j].Model[0] != '\0') wear.Add(new WearBone(links[j].Model, Body[j], scale: j < 14 ? BodyMeasure.LeftArm : BodyMeasure.RightArm));
        for (var k = 0; k < PoseFingers.Count; k++)
        {
            var j = ClipRig.BodyCount + k;
            if (links[j].Model[0] != '\0') wear.Add(new WearBone(links[j].Model, links[j].Body, scale: k < PoseFingers.PerHand ? BodyMeasure.LeftArm : BodyMeasure.RightArm));
        }
        return wear.ToArray();
    }
}

/// <summary>
/// A limb of a pose copy whose end must land where the body's does (<c>Models.CopyPose</c>): links <see cref="Upper"/>,
/// <see cref="Lower"/> and <see cref="End"/> (an upper arm, its forearm, its hand; the lower a descendant of the upper, the end of the
/// lower). Copying turns alone puts a model limb of other proportions somewhere else; with a reach, after the turns the two upper
/// bones bend (in the body limb's own bend plane) so the end joint sits where the body's does seen from the limb's own upper joint,
/// scaled by the limb's length over the body limb's (both at rest): the limb keeps the body's bend whatever its proportions, and the
/// end keeps its copied turn. A body holding an item in its hand socket (a player's <c>PlayerSyncModelData.ItemAlign</c>) has the
/// item carried to the model's hand, and the other hand on the item where the body's is (<c>PoseCopy</c>).
/// </summary>
public readonly struct PoseReach
{
    public PoseReach(int upper, int lower, int end)
    {
        Upper = upper;
        Lower = lower;
        End = end;
    }

    public int Upper { get; }
    public int Lower { get; }
    public int End { get; }
}

/// <summary>A bone's rest pose (from a skinned mesh's bind pose): position, rotation and uniform scale in its skeleton's frame.</summary>
internal readonly struct RestPose
{
    internal RestPose(Vector3 position, Quaternion rotation, float scale = 1f)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    internal Vector3 Position { get; }
    internal Quaternion Rotation { get; }
    internal float Scale { get; }
}

/// <summary>A vertex's four bone weights, as a skinned mesh stores them.</summary>
internal struct Weights4
{
    internal int I0 { get; set; }
    internal int I1 { get; set; }
    internal int I2 { get; set; }
    internal int I3 { get; set; }
    internal float W0 { get; set; }
    internal float W1 { get; set; }
    internal float W2 { get; set; }
    internal float W3 { get; set; }
}

/// <summary>The rotation helpers with Unity's semantics (left-handed frames, <c>LookRotation</c>, <c>FromToRotation</c>) on
/// System.Numerics types, so the body solvers run offline.</summary>
internal static class BodyMath
{
    /// <summary>The rotation turning +Z onto <paramref name="forward"/> and +Y toward <paramref name="up"/> (Unity's LookRotation).</summary>
    internal static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 1e-12f) return Quaternion.Identity;
        var f = Vector3.Normalize(forward);
        var r = Vector3.Cross(up, f);
        if (r.LengthSquared() < 1e-12f) r = Vector3.Cross(Math.Abs(f.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, f);
        r = Vector3.Normalize(r);
        var u = Vector3.Cross(f, r);
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0f, u.X, u.Y, u.Z, 0f, f.X, f.Y, f.Z, 0f, 0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>The shortest rotation turning <paramref name="from"/> onto <paramref name="to"/> (Unity's FromToRotation).</summary>
    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Quaternion.Identity;
        var a = Vector3.Normalize(from);
        var b = Vector3.Normalize(to);
        var d = Vector3.Dot(a, b);
        if (d >= 1f - 1e-6f) return Quaternion.Identity;
        if (d <= -1f + 1e-6f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + d));
    }

    internal static Vector3 Rotate(Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    /// <summary>A skeleton's frame from its shoulders and hips: right from the left to the right upper arm, up from the hips to
    /// between them.</summary>
    internal static Quaternion SkeletonFrame(Vector3 leftArm, Vector3 rightArm, Vector3 hips)
    {
        var right = Vector3.Normalize(rightArm - leftArm);
        var up = (leftArm + rightArm) * 0.5f - hips;
        var forward = Vector3.Normalize(Vector3.Cross(right, up));
        up = Vector3.Cross(forward, right);
        return LookRotation(forward, up);
    }

    /// <summary>A bone's frame in its body's rest frame: forward along the bone, up toward the body's front (its top when the bone
    /// points forward); the body's own frame for a bone with no direction.</summary>
    internal static Quaternion BoneFrame(Vector3 along)
    {
        if (along.LengthSquared() < 1e-8f) return Quaternion.Identity;
        along = Vector3.Normalize(along);
        return LookRotation(along, Math.Abs(Vector3.Dot(along, Vector3.UnitZ)) > 0.8f ? Vector3.UnitY : Vector3.UnitZ);
    }
}

/// <summary>Matches a model's skeleton to the game's first-person skeleton through both rest poses: both are put in one frame
/// (shoulders and hips), and every worn bone gets the fixed rotation that turns it from its rest pose onto its game bone's (its rest
/// direction swung onto the game bone's) and the scale its <see cref="BodyMeasure"/> gives.</summary>
internal static class WearSolver
{
    /// <summary>For each bone, its one listed child (-1 when it has none or more than one), from <paramref name="parents"/>: each
    /// bone's nearest listed ancestor in the model's hierarchy (-1 for none; the native side walks the transforms).</summary>
    internal static int[] Children(int[] parents)
    {
        var child = new int[parents.Length];
        for (var i = 0; i < child.Length; i++) child[i] = -1;
        for (var i = 0; i < parents.Length; i++)
        {
            var p = parents[i];
            if (p < 0) continue;
            // A bone with more than one listed child takes no direction from a child.
            child[p] = child[p] == -1 ? i : -2;
        }
        for (var i = 0; i < child.Length; i++) if (child[i] == -2) child[i] = -1;
        return child;
    }

    /// <summary>
    /// The binding of every worn bone that is on the game's body (<paramref name="game"/>[i] not null): <paramref name="offset"/> is its
    /// rotation relative to its game bone, <paramref name="scale"/> its uniform scale from model units to body units. Null when it worked;
    /// otherwise why not (the left and right upper arms and the hips must be listed and on the game's body).
    /// </summary>
    internal static string? Solve(IReadOnlyList<WearBone> bones, int[] parents, RestPose[] model, RestPose?[] game, out Quaternion[] offset, out float[] scale)
    {
        var n = bones.Count;
        offset = new Quaternion[n];
        scale = new float[n];
        for (var i = 0; i < n; i++) { offset[i] = Quaternion.Identity; scale[i] = 1f; }
        var l = IndexOf(bones, BodyBone.LeftUpperArm);
        var r = IndexOf(bones, BodyBone.RightUpperArm);
        var h = IndexOf(bones, BodyBone.Hips);
        if (l < 0 || r < 0 || h < 0 || game[l] == null || game[r] == null || game[h] == null)
            return "the bones must list LeftUpperArm, RightUpperArm and Hips, each on the game's body, to match the skeletons";
        var modelFrame = BodyMath.SkeletonFrame(model[l].Position, model[r].Position, model[h].Position);
        var gameFrame = BodyMath.SkeletonFrame(game[l]!.Value.Position, game[r]!.Value.Position, game[h]!.Value.Position);
        var align = gameFrame * Quaternion.Inverse(modelFrame);
        var span = (game[r]!.Value.Position - game[l]!.Value.Position).Length() / Math.Max(1e-4f, (model[r].Position - model[l].Position).Length());
        var child = Children(parents);
        for (var i = 0; i < n; i++)
        {
            if (game[i] is not { } g || !bones[i].Worn) continue;
            scale[i] = Measure(bones, model, game, bones[i].Scale, span);
            // The bone's rest direction: toward its listed child, or from its parent for a leaf.
            Vector3 modelAlong = Vector3.Zero, gameAlong = Vector3.Zero;
            if (child[i] >= 0 && game[child[i]] is { } c)
            {
                modelAlong = model[child[i]].Position - model[i].Position;
                gameAlong = c.Position - g.Position;
            }
            else if (parents[i] >= 0 && game[parents[i]] is { } p)
            {
                modelAlong = model[i].Position - model[parents[i]].Position;
                gameAlong = g.Position - p.Position;
            }
            var rotation = align * model[i].Rotation;
            if (modelAlong.LengthSquared() > 1e-8f && gameAlong.LengthSquared() > 1e-8f)
                rotation = BodyMath.FromTo(BodyMath.Rotate(align, modelAlong), gameAlong) * rotation;
            offset[i] = Quaternion.Inverse(g.Rotation) * rotation;
        }
        return null;
    }

    /// <summary>The scale <paramref name="measure"/> gives: the game's length over the model's.</summary>
    internal static float Measure(IReadOnlyList<WearBone> bones, RestPose[] model, RestPose?[] game, BodyMeasure measure, float span)
        => measure switch
        {
            BodyMeasure.LeftLeg => Chain(bones, model, game, span, BodyBone.LeftUpperLeg, BodyBone.LeftLowerLeg, BodyBone.LeftFoot),
            BodyMeasure.RightLeg => Chain(bones, model, game, span, BodyBone.RightUpperLeg, BodyBone.RightLowerLeg, BodyBone.RightFoot),
            BodyMeasure.LeftArm => Chain(bones, model, game, span, BodyBone.LeftUpperArm, BodyBone.LeftLowerArm, BodyBone.LeftHand),
            BodyMeasure.RightArm => Chain(bones, model, game, span, BodyBone.RightUpperArm, BodyBone.RightLowerArm, BodyBone.RightHand),
            _ => span,
        };

    private static float Chain(IReadOnlyList<WearBone> bones, RestPose[] model, RestPose?[] game, float fallback, BodyBone a, BodyBone b, BodyBone c)
    {
        int ia = IndexOf(bones, a), ib = IndexOf(bones, b), ic = IndexOf(bones, c);
        if (ia < 0 || ib < 0 || ic < 0 || game[ia] is not { } ga || game[ib] is not { } gb || game[ic] is not { } gc) return fallback;
        var modelLength = (model[ib].Position - model[ia].Position).Length() + (model[ic].Position - model[ib].Position).Length();
        var gameLength = (gb.Position - ga.Position).Length() + (gc.Position - gb.Position).Length();
        return modelLength > 1e-4f ? gameLength / modelLength : fallback;
    }

    /// <summary>The local scale of a model bone parented on its game bone: its <paramref name="scale"/> (model to body units) times the
    /// body's world scale and the bone's own rest scale, over the game bone's world scale.</summary>
    internal static float LocalScale(float scale, float bodyScale, float modelRestScale, float gameBoneScale)
        => scale * bodyScale * modelRestScale / (Math.Abs(gameBoneScale) > 1e-6f ? gameBoneScale : 1f);

    internal static int IndexOf(IReadOnlyList<WearBone> bones, BodyBone body)
    {
        for (var i = 0; i < bones.Count; i++) if (bones[i].Body == body) return i;
        return -1;
    }
}

/// <summary>The fixed corrections of a pose copy: each model bone's frame read from the model's rest (<see cref="Unframe"/>) and,
/// once per body, from the body's rest (<see cref="Correct"/>), so that every frame the model bone takes
/// <c>turn × body bone × correction</c>.</summary>
internal static class PoseSolver
{
    /// <summary>Null when <paramref name="links"/> can be copied; otherwise why not (empty, a direction to itself or out of range, both
    /// a toward and a from).</summary>
    internal static string? Validate(IReadOnlyList<PoseLink> links)
    {
        if (links == null || links.Count == 0) return "no links";
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (string.IsNullOrEmpty(link.Model)) return "link " + i + " names no model bone";
            if (link.Toward >= 0 && link.From >= 0) return "link " + i + " (" + link.Model + ") has both a toward and a from";
            if (link.Toward == i || link.From == i) return "link " + i + " (" + link.Model + ") points at itself";
            if (link.Toward >= links.Count || link.From >= links.Count || link.Toward < -1 || link.From < -1)
                return "link " + i + " (" + link.Model + ") points outside the " + links.Count + " links";
            for (var j = 0; j < i; j++)
                if (links[j].Model == link.Model) return "model bone " + link.Model + " is listed twice";
        }
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (!link.Optional && ((link.Toward >= 0 && links[link.Toward].Optional) || (link.From >= 0 && links[link.From].Optional)))
                return "link " + i + " (" + link.Model + ") is not optional but points at an optional link";
        }
        return null;
    }

    /// <summary>Which links a pose copy leaves out: <paramref name="absent"/>[i] (the model or the body lacks link i's bone, optional links
    /// only) and every optional link that points at a left-out one, followed through the chain.</summary>
    internal static bool[] Skipped(IReadOnlyList<PoseLink> links, bool[] absent)
    {
        var skip = (bool[])absent.Clone();
        for (var again = true; again;)
        {
            again = false;
            for (var i = 0; i < skip.Length; i++)
            {
                var link = links[i];
                if (skip[i] || !link.Optional || !((link.Toward >= 0 && skip[link.Toward]) || (link.From >= 0 && skip[link.From]))) continue;
                skip[i] = true;
                again = true;
            }
        }
        return skip;
    }

    /// <summary>Per link: inverse(frame) × rest rotation, from the model's rest positions and rotations (in its root's space).</summary>
    internal static Quaternion[] Unframe(IReadOnlyList<PoseLink> links, Vector3[] position, Quaternion[] rotation)
    {
        var unframe = new Quaternion[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var along = link.Toward >= 0 ? position[link.Toward] - position[i] : link.From >= 0 ? position[i] - position[link.From] : Vector3.Zero;
            unframe[i] = Quaternion.Inverse(BodyMath.BoneFrame(along)) * rotation[i];
        }
        return unframe;
    }

    /// <summary>Per link: the correction from the body bone to the model bone. <paramref name="toward"/>[i], when set, is the body's own
    /// toward bone's rest position (<see cref="PoseLink.TowardBody"/>) and replaces the toward link's.</summary>
    internal static Quaternion[] Correct(IReadOnlyList<PoseLink> links, Vector3[] position, Quaternion[] rotation, Vector3?[] toward, Quaternion[] unframe)
    {
        var correction = new Quaternion[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var along = Vector3.Zero;
            if (link.Toward >= 0) along = (toward[i] ?? position[link.Toward]) - position[i];
            else if (link.From >= 0) along = position[i] - position[link.From];
            correction[i] = Quaternion.Inverse(rotation[i]) * BodyMath.BoneFrame(along) * unframe[i];
        }
        return correction;
    }

    /// <summary>The model's size against the body's (root height over root height). Null when it worked; otherwise why not (the body's
    /// root below 0.1 m). A model whose root rests on its floor or under it has no height to scale by: the ratio is 0, so its root keeps
    /// its rest height and takes none of the body's rise and sway.</summary>
    internal static string? Ratio(float modelRootHeight, float bodyRootHeight, out float ratio)
    {
        ratio = 1f;
        if (bodyRootHeight < 0.1f) return "the root (hips) stands at " + bodyRootHeight.ToString("0.###") + " m on the body";
        ratio = Math.Max(0f, modelRootHeight) / bodyRootHeight;
        return null;
    }

    /// <summary>The reaches of <paramref name="reaches"/> a copy can bend: those whose three links are all copied (<paramref name="skip"/>).</summary>
    internal static List<PoseReach> Reachable(IReadOnlyList<PoseReach> reaches, bool[] skip)
    {
        var kept = new List<PoseReach>(reaches.Count);
        foreach (var r in reaches) if (!skip[r.Upper] && !skip[r.Lower] && !skip[r.End]) kept.Add(r);
        return kept;
    }

    /// <summary>A limb's length on the model over its length on the body (upper joint to middle joint to end, each at rest in its own
    /// skeleton's units); <paramref name="fallback"/> when either limb has no length.</summary>
    internal static float LimbScale(Vector3 modelUpper, Vector3 modelLower, Vector3 modelEnd, Vector3 bodyUpper, Vector3 bodyLower, Vector3 bodyEnd, float fallback)
    {
        var body = (bodyLower - bodyUpper).Length() + (bodyEnd - bodyLower).Length();
        var model = (modelLower - modelUpper).Length() + (modelEnd - modelLower).Length();
        return body < 1e-4f || model < 1e-4f ? fallback : model / body;
    }

    /// <summary>Null when <paramref name="reaches"/> fit <paramref name="count"/> links; otherwise why not (a link out of range, or
    /// the upper, lower and end not three different links).</summary>
    internal static string? ValidateReach(IReadOnlyList<PoseReach> reaches, int count)
    {
        for (var i = 0; i < reaches.Count; i++)
        {
            var r = reaches[i];
            if (r.Upper < 0 || r.Lower < 0 || r.End < 0 || r.Upper >= count || r.Lower >= count || r.End >= count)
                return "reach " + i + " points outside the " + count + " links";
            if (r.Upper == r.Lower || r.Lower == r.End || r.Upper == r.End) return "reach " + i + " needs three different links";
        }
        return null;
    }

    /// <summary>Where the middle joint of a two-bone limb goes so its end lands on <paramref name="target"/>: from
    /// <paramref name="root"/>, bones <paramref name="upper"/> and <paramref name="lower"/> long, bending toward
    /// <paramref name="bend"/> (any vector off the root-to-target line; ignored along it). Out of reach (or closer than the bones
    /// fold) the end stops on the line toward the target. <paramref name="end"/> is where the end then actually lands.</summary>
    internal static Vector3 Elbow(Vector3 root, Vector3 target, float upper, float lower, Vector3 bend, out Vector3 end)
    {
        var to = target - root;
        var distance = to.Length();
        var dir = distance > 1e-5f ? to / distance : Vector3.UnitY;
        var d = Math.Clamp(distance, Math.Abs(upper - lower) + 1e-4f, upper + lower - 1e-4f);
        end = root + dir * d;
        var side = bend - dir * Vector3.Dot(bend, dir);
        if (side.LengthSquared() < 1e-10f) side = Vector3.Cross(dir, Math.Abs(dir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX);
        var along = (upper * upper + d * d - lower * lower) / (2f * d);
        var height = MathF.Sqrt(Math.Max(0f, upper * upper - along * along));
        return root + dir * along + Vector3.Normalize(side) * height;
    }

    /// <summary>Where the model's root goes in its own space: its rest plus the body root's offset from its rest, scaled.</summary>
    internal static Vector3 Root(Vector3 modelRootRest, Vector3 bodyRoot, Vector3 bodyRootRest, float ratio)
        => modelRootRest + (bodyRoot - bodyRootRest) * ratio;
}

/// <summary>Where a held item's copy goes: the game holds the item at <c>source</c> near the body's hand; the copy keeps that offset,
/// turned by the body-to-model turn and scaled, from the model's hand.</summary>
internal static class HeldPlacement
{
    internal static Vector3 Position(Quaternion turn, Vector3 source, Vector3 bodyHand, Vector3 modelHand, float scale)
        => modelHand + BodyMath.Rotate(turn, source - bodyHand) * scale;
}

/// <summary>Cutting a skinned mesh down to the triangles skinned to a set of bones.</summary>
internal static class MeshCut
{
    /// <summary>Which skin bones a first-person wear keeps (<c>Models.WearFirstPerson</c>): each bone stands for its
    /// <paramref name="owner"/>; it is kept when the owner is a worn bone of the wear (<paramref name="listed"/> gives its index, -1 for a
    /// bone the wear does not list), or with <paramref name="descendants"/> when the first listed bone above it is worn; a listed bone
    /// that only lines the skeletons up (<paramref name="alignOnly"/>) stops the walk. The game and the editor's preview both cut by it.</summary>
    internal static bool[] KeptBones<T>(IReadOnlyList<T?> skin, Func<T, T> owner, Func<T, T?> parent, Func<T, int> listed, bool[] worn, bool[] alignOnly, bool descendants) where T : class
    {
        var kept = new bool[skin.Count];
        for (var b = 0; b < skin.Count; b++)
        {
            if (skin[b] is not { } bone) continue;
            for (T? t = owner(bone); t != null; t = descendants ? parent(t) : null)
            {
                var i = listed(t);
                if (i < 0) continue;
                if (worn[i]) { kept[b] = true; break; }
                if (alignOnly[i]) break;
            }
        }
        return kept;
    }

    /// <summary>Which vertices are skinned at least half to the kept bones (<paramref name="kept"/>, by bone index); each kept vertex's
    /// weights renormalised over the kept bones (written to <paramref name="weights"/>; other vertices are left as they were).</summary>
    internal static bool[] KeepVertices(Weights4[] weights, bool[] kept)
    {
        var keep = new bool[weights.Length];
        for (var v = 0; v < weights.Length; v++)
        {
            var w = weights[v];
            float K(int index, float weight) => index >= 0 && index < kept.Length && kept[index] ? weight : 0f;
            var w0 = K(w.I0, w.W0);
            var w1 = K(w.I1, w.W1);
            var w2 = K(w.I2, w.W2);
            var w3 = K(w.I3, w.W3);
            var sum = w0 + w1 + w2 + w3;
            if (sum < 0.5f) continue;
            keep[v] = true;
            w.W0 = w0 / sum; w.W1 = w1 / sum; w.W2 = w2 / sum; w.W3 = w3 / sum;
            weights[v] = w;
        }
        return keep;
    }

    /// <summary>The triangles of <paramref name="triangles"/> whose three corners are all kept.</summary>
    internal static int[] KeepTriangles(int[] triangles, bool[] keep)
    {
        var cut = new List<int>(triangles.Length / 4);
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (!keep[a] || !keep[b] || !keep[c]) continue;
            cut.Add(a); cut.Add(b); cut.Add(c);
        }
        return cut.ToArray();
    }
}
