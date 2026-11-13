using System;
using System.Numerics;

namespace ForgeVisual;

/// <summary>What part of the canonical rig a skeleton has: a whole body, or only the arms (first-person arms).</summary>
public enum RigShape
{
    Body, Arms,
}

/// <summary>
/// The declaration that puts a skeleton on the canonical rig: which bone is which <see cref="ClipRig"/> joint. Humanoid bodies with an
/// avatar need none (<c>Models.BindJoints(PlayerAgent)</c>); any other skeleton names its bones here, by path under its root or by bone name.
/// Nothing else is declared: the rest pose and the proportions come from the body's own bind poses when it is bound
/// (<see cref="JointRetarget"/>), and the fit is checked there, so a wrong map fails with one error naming the joints and not with a broken pose.
/// </summary>
public sealed class RigMap
{
    private readonly string?[] _paths = new string?[ClipRig.Count];

    /// <summary>The bone of <paramref name="joint"/> (a name in <see cref="ClipRig.Names"/>): a slash-separated path under the root, or a bone name anywhere under it.</summary>
    public RigMap Bone(string joint, string path)
    {
        var index = Array.IndexOf(ClipRig.Names, joint);
        if (index < 0) throw new ArgumentException("ClipRig has no joint " + joint + ".", nameof(joint));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A bone needs a path.", nameof(path));
        _paths[index] = path;
        return this;
    }

    internal string? PathOf(int joint) => _paths[joint];

    private static readonly int[] BodyRequired = { 0, 1, 2, 4, 5, 7, 11, 12, 15, 16, 19 };
    private static readonly int[] ArmsRequired = { 11, 12, 15, 16 };

    /// <summary>The joints a clip cannot play without.</summary>
    public static int[] Required(RigShape shape) => shape == RigShape.Arms ? ArmsRequired : BodyRequired;

    // Joints a standing body's bones must point about the way the canonical rig's do: a skeleton that does not is mapped wrong or lying on
    // its side. The arms are free: arms down, out or between are all fine.
    private static readonly (int From, int To)[] Standing = { (0, 7), (1, 2), (2, 3), (4, 5), (5, 6) };
    private const float StandingLimitDegrees = 60f, ShortestBone = 0.005f;

    /// <summary>What is wrong with a skeleton's rest pose against the canonical rig, one sentence each, empty when it fits (game axes,
    /// root space). Called once at bind.</summary>
    internal static string Check(bool[] present, Vector3[] rest, RigShape shape)
    {
        var wrong = "";
        for (var j = 0; j < ClipRig.Count; j++)
            foreach (var to in JointRetarget.Toward[j])
            {
                if (!present[j] || !present[to]) continue;
                if ((rest[to] - rest[j]).Length() < ShortestBone) wrong += " " + ClipRig.Names[j] + " and " + ClipRig.Names[to] + " rest on one point.";
                break;
            }
        if (shape != RigShape.Body) return wrong;
        foreach (var (from, to) in Standing)
        {
            if (!present[from] || !present[to]) continue;
            var cosine = Vector3.Dot(Vector3.Normalize(rest[to] - rest[from]), ClipRig.RestDirection(from, to));
            var degrees = MathF.Acos(Math.Clamp(cosine, -1f, 1f)) * 180f / MathF.PI;
            if (degrees > StandingLimitDegrees)
                wrong += " " + ClipRig.Names[from] + " rests " + degrees.ToString("0") + " degrees from where it stands on the rig: the map is wrong or the root is not upright.";
        }
        // Left and right: the game's +X is a body's right.
        if (present[1] && present[4] && rest[1].X > rest[4].X) wrong += " leftUpperLeg is on the body's right: the sides are swapped or the root faces backwards.";
        if (present[11] && present[15] && rest[11].X > rest[15].X) wrong += " leftUpperArm is on the body's right: the sides are swapped or the root faces backwards.";
        return wrong;
    }

    /// <summary>The whole body rests turned half round against its root: its left leg, and its left arm when it has both, on the root's
    /// right (the game's Birther family). Such a body is read turned round (<see cref="TurnAround"/>) and plays facing where its root faces;
    /// one side swapped alone is a wrong map and stays a <see cref="Check"/> error.</summary>
    internal static bool FacesBackwards(bool[] present, Vector3[] rest) =>
        present[1] && present[4] && rest[1].X > rest[4].X && (!(present[11] && present[15]) || rest[11].X > rest[15].X);

    /// <summary>Turns a rest pose half round about the root's up axis, in place.</summary>
    internal static void TurnAround(bool[] present, Vector3[] rest, Quaternion[] rotation)
    {
        var half = new Quaternion(0f, 1f, 0f, 0f);
        for (var j = 0; j < rest.Length; j++)
        {
            if (!present[j]) continue;
            rest[j] = new Vector3(-rest[j].X, rest[j].Y, -rest[j].Z);
            rotation[j] = half * rotation[j];
        }
    }

    /// <summary>A hand with only some of its finger joints plays with none of them: a clip's fingers are all or nothing.</summary>
    internal static void DropBrokenFingers<T>(bool[] present, T?[] bones) where T : class
    {
        foreach (var first in new[] { ClipRig.BodyCount, ClipRig.BodyCount + 15 })
        {
            var have = 0;
            for (var j = first; j < first + 15; j++) if (present[j]) have++;
            if (have is 0 or 15) continue;
            for (var j = first; j < first + 15; j++) { present[j] = false; bones[j] = null; }
        }
    }
}
