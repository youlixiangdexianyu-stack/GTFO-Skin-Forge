using System;
using System.Collections.Generic;
using System.Numerics;

namespace ForgeVisual;

/// <summary>A humanoid body bone, numbered as Unity's <c>HumanBodyBones</c> (so a value casts straight to it). Fingers, eyes and jaw
/// are not listed: no body mechanism of this library uses them.</summary>
public enum BodyBone
{
    Hips = 0, LeftUpperLeg = 1, RightUpperLeg = 2, LeftLowerLeg = 3, RightLowerLeg = 4, LeftFoot = 5, RightFoot = 6,
    Spine = 7, Chest = 8, Neck = 9, Head = 10, LeftShoulder = 11, RightShoulder = 12, LeftUpperArm = 13, RightUpperArm = 14,
    LeftLowerArm = 15, RightLowerArm = 16, LeftHand = 17, RightHand = 18, LeftToes = 19, RightToes = 20, UpperChest = 54,
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
/// chest). Link 0 is the root: its position is copied too (height and sway scaled by hip height over hip height).
/// </summary>
public readonly struct PoseLink
{
    public PoseLink(string model, BodyBone body, int toward = -1, int from = -1, BodyBone? bodyAlt = null, BodyBone? towardBody = null, BodyBone? towardBodyAlt = null)
    {
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
}

/// <summary>
/// A limb of a pose copy whose end must land where the body's does (<c>Models.CopyPose</c>): links <see cref="Upper"/>,
/// <see cref="Lower"/> and <see cref="End"/> (an upper arm, its forearm, its hand; the lower a descendant of the upper, the end of the
/// lower). Copying turns alone puts a model limb of other proportions somewhere else; with a reach, after the turns the two upper
/// bones bend (in the body limb's own bend plane) so the end joint sits at the body's end joint carried over from link
/// <see cref="Anchor"/> (the body's offset from its anchor bone, turned and scaled, from the model's anchor bone), and the end keeps
/// its copied turn. A hand on a grip stays on the grip. Out of reach, the limb stretches toward it.
/// </summary>
public readonly struct PoseReach
{
    public PoseReach(int upper, int lower, int end, int anchor)
    {
        Upper = upper;
        Lower = lower;
        End = end;
        Anchor = anchor;
    }

    public int Upper { get; }
    public int Lower { get; }
    public int End { get; }
    public int Anchor { get; }
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
        return null;
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

    /// <summary>The model's size against the body's (root height over root height). Null when it worked; otherwise why not (a root
    /// below 0.1 m).</summary>
    internal static string? Ratio(float modelRootHeight, float bodyRootHeight, out float ratio)
    {
        ratio = 1f;
        if (bodyRootHeight < 0.1f || modelRootHeight < 0.1f) return "the root (hips) stands at " + bodyRootHeight.ToString("0.###") + " m on the body and " + modelRootHeight.ToString("0.###") + " m on the model";
        ratio = modelRootHeight / bodyRootHeight;
        return null;
    }

    /// <summary>Null when <paramref name="reaches"/> fit <paramref name="count"/> links; otherwise why not (a link out of range, or
    /// the upper, lower and end not three different links).</summary>
    internal static string? ValidateReach(IReadOnlyList<PoseReach> reaches, int count)
    {
        for (var i = 0; i < reaches.Count; i++)
        {
            var r = reaches[i];
            if (r.Upper < 0 || r.Lower < 0 || r.End < 0 || r.Anchor < 0 || r.Upper >= count || r.Lower >= count || r.End >= count || r.Anchor >= count)
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
