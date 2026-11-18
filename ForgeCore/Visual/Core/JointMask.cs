using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>Humanoid parts a <see cref="JointMask"/> selects, each a set of <see cref="ClipRig"/> joints.</summary>
public enum BodyPart
{
    LeftHand, RightHand, LeftFingers, RightFingers, LeftForearm, RightForearm, LeftArm, RightArm, HeadNeck, Torso, Pelvis, LeftLeg, RightLeg,
}

/// <summary>
/// Which bones a clip drives, each with a weight from 0 to 1: humanoid parts (<see cref="Add(BodyPart, float)"/>) and bone paths
/// (<see cref="AddPath"/>, optionally with the whole subtree under the bone). A joint takes the highest weight any entry gives it; a
/// lower weight at the edge (a forearm at 0.5 under a hand at 1) blends the clip with the game's own pose so the joint does not kink.
/// Bones outside the mask are not written at all, and the game's animator and IK keep them. Build masks while loading; a body
/// resolves one into per-joint weights when a clip starts, so playing looks nothing up.
/// </summary>
public sealed class JointMask
{
    private readonly List<(int[]? Joints, string? Path, bool Subtree, float Weight)> _entries = new();

    /// <summary>Every joint at full weight: the clip drives the whole body, hips included.</summary>
    public static JointMask FullBody => new JointMask().Add(BodyPart.Pelvis).Add(BodyPart.Torso).Add(BodyPart.HeadNeck)
        .Add(BodyPart.LeftArm).Add(BodyPart.RightArm).Add(BodyPart.LeftLeg).Add(BodyPart.RightLeg);

    /// <summary>The spine up, the arms and the head; the spine's root at 0.5 and its middle at 0.8 so the waist bends smoothly.</summary>
    public static JointMask UpperBody => new JointMask().AddJoints(new[] { 7 }, 0.5f).AddJoints(new[] { 8 }, 0.8f).AddJoints(new[] { 9 }, 1f)
        .Add(BodyPart.HeadNeck).Add(BodyPart.LeftArm).Add(BodyPart.RightArm);

    /// <summary>The hips and both legs.</summary>
    public static JointMask LowerBody => new JointMask().Add(BodyPart.Pelvis).Add(BodyPart.LeftLeg).Add(BodyPart.RightLeg);

    private static int[] Fingers(int first)
    {
        var joints = new int[15];
        for (var i = 0; i < joints.Length; i++) joints[i] = first + i;
        return joints;
    }

    private static int[] Arm(int first, int fingers)
    {
        var joints = new int[19];
        for (var i = 0; i < 4; i++) joints[i] = first + i;
        Array.Copy(Fingers(fingers), 0, joints, 4, 15);
        return joints;
    }

    /// <summary>The <see cref="ClipRig"/> joints of <paramref name="part"/>; an arm goes down to its finger tips.</summary>
    public static int[] Joints(BodyPart part) => part switch
    {
        BodyPart.LeftHand => new[] { 13 },
        BodyPart.RightHand => new[] { 17 },
        BodyPart.LeftFingers => Fingers(20),
        BodyPart.RightFingers => Fingers(35),
        BodyPart.LeftForearm => new[] { 12 },
        BodyPart.RightForearm => new[] { 16 },
        BodyPart.LeftArm => Arm(10, 20),
        BodyPart.RightArm => Arm(14, 35),
        BodyPart.HeadNeck => new[] { 18, 19 },
        BodyPart.Torso => new[] { 7, 8, 9 },
        BodyPart.Pelvis => new[] { 0 },
        BodyPart.LeftLeg => new[] { 1, 2, 3 },
        BodyPart.RightLeg => new[] { 4, 5, 6 },
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    public JointMask Add(BodyPart part, float weight = 1f) => AddJoints(Joints(part), weight);

    /// <summary>The joint whose bone is at <paramref name="path"/> (slash-separated names under the body's root) and, with
    /// <paramref name="subtree"/>, every joint whose bone lies under it.</summary>
    public JointMask AddPath(string path, bool subtree = true, float weight = 1f)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A mask path names a bone.", nameof(path));
        _entries.Add((null, path, subtree, Check(weight)));
        return this;
    }

    /// <summary>The canonical joints <paramref name="joints"/> (indices of <see cref="ClipRig.Names"/>) at <paramref name="weight"/>: a mask rebuilt from the joints a
    /// track covers, as a replay does.</summary>
    public JointMask AddJoints(int[] joints, float weight)
    {
        _entries.Add((joints, null, false, Check(weight)));
        return this;
    }

    private static float Check(float weight) => weight is >= 0f and <= 1f ? weight : throw new ArgumentOutOfRangeException(nameof(weight), "A mask weight is from 0 to 1.");

    /// <summary>Per-joint weights into <paramref name="into"/> (<see cref="ClipRig.Count"/> long). <paramref name="underPath"/> answers
    /// whether a joint's bone is the bone at a path (or, for a subtree, under it); the body asks its own transforms.</summary>
    public void Resolve(float[] into, Func<int, string, bool, bool> underPath)
    {
        if (into.Length != ClipRig.Count) throw new ArgumentException("One weight per clip joint.", nameof(into));
        Array.Clear(into);
        foreach (var (joints, path, subtree, weight) in _entries)
        {
            if (joints != null) { foreach (var j in joints) into[j] = Math.Max(into[j], weight); continue; }
            for (var j = 0; j < ClipRig.Count; j++) if (underPath(j, path!, subtree)) into[j] = Math.Max(into[j], weight);
        }
    }
}
