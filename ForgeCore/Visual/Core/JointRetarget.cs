using System;
using System.Numerics;

namespace ForgeVisual;

/// <summary>
/// Lines a humanoid body's rest pose up with the canonical rig (<see cref="ClipRig"/>), once per body, so a clip's turns can be laid over
/// it: each bone first swings from its own rest direction (toward its child) onto the canonical one, keeping its own twist, and an end
/// bone (foot, head) takes its parent's swing. A body posed as <c>facing * turn * Aligned</c> then has every segment pointing along
/// the clip's turn of the canonical rest direction, whatever its rest pose (arms down, out, or between) and its bones' local axes.
/// A hand with fingers lines up by its palm (wrist to middle knuckle, pinky to index knuckle), twist included: its fingers are lined up
/// with the canonical fingers, so a palm left at its own twist would turn every finger away from the hand they hang on. A hand without
/// fingers lines up by the aim its bone gives.
/// </summary>
internal static class JointRetarget
{
    // Joints a joint points at, nearest first: the first one the body has is the direction the bone is lined up with. Ends point at
    // nothing. A body without the upper chest (joint 9) points the spine at the next joint up.
    internal static readonly int[][] Toward =
    {
        new[] { 7 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>(), new[] { 5 }, new[] { 6 }, Array.Empty<int>(),
        new[] { 8, 9, 18 }, new[] { 9, 18 }, new[] { 18 },
        new[] { 11 }, new[] { 12 }, new[] { 13 }, Array.Empty<int>(),
        new[] { 15 }, new[] { 16 }, new[] { 17 }, Array.Empty<int>(),
        new[] { 19 }, Array.Empty<int>(),
        // Thumb to pinky, left then right: each segment points at the next, the last takes the one before's swing.
        new[] { 21 }, new[] { 22 }, Array.Empty<int>(), new[] { 24 }, new[] { 25 }, Array.Empty<int>(), new[] { 27 }, new[] { 28 }, Array.Empty<int>(), new[] { 30 }, new[] { 31 }, Array.Empty<int>(), new[] { 33 }, new[] { 34 }, Array.Empty<int>(),
        new[] { 36 }, new[] { 37 }, Array.Empty<int>(), new[] { 39 }, new[] { 40 }, Array.Empty<int>(), new[] { 42 }, new[] { 43 }, Array.Empty<int>(), new[] { 45 }, new[] { 46 }, Array.Empty<int>(), new[] { 48 }, new[] { 49 }, Array.Empty<int>(),
    };

    /// <summary>For every present joint, the rotation that takes a clip's turn to a world rotation of the body's bone
    /// (<c>turn * Aligned[joint]</c>, in the body root's space); the rest (<paramref name="present"/> false) are left as the identity.</summary>
    internal static Quaternion[] Aligned(bool[] present, Vector3[] restPosition, Quaternion[] restRotation)
    {
        var aligned = new Quaternion[ClipRig.Count];
        var swing = new Quaternion[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++)
        {
            swing[j] = Quaternion.Identity;
            aligned[j] = Quaternion.Identity;
            if (!present[j]) continue;
            if (j is 13 or 17 && Palm(j, present) is var (middle, index, pinky) && middle >= 0)
                swing[j] = Quaternion.Normalize(PalmFrame(ClipRig.RestPoint(middle) - ClipRig.RestPoint(j), ClipRig.RestPoint(index) - ClipRig.RestPoint(pinky))
                    * Quaternion.Inverse(PalmFrame(restPosition[middle] - restPosition[j], restPosition[index] - restPosition[pinky])));
            else if (j is 13 or 17) swing[j] = BareHand(j, swing[ClipRig.Parent[j]], restPosition, restRotation);
            else if (Toward[j].Length == 0) { if (ClipRig.Parent[j] >= 0) swing[j] = swing[ClipRig.Parent[j]]; }
            else
                foreach (var to in Toward[j])
                {
                    if (!present[to]) continue;
                    swing[j] = BodyMath.FromTo(restPosition[to] - restPosition[j], ClipRig.RestDirection(j, to));
                    break;
                }
            aligned[j] = swing[j] * restRotation[j];
        }
        return aligned;
    }

    // A hand without fingers has nothing to point at, so its aim is read from its bone: the axis that runs along the forearm in the forearm's
    // bone runs along the hand in the hand's, which keeps a wrist the body was modelled with bent. Beyond MaxWristBend the two bones' axes
    // do not share a convention and the forearm's direction is the better guess. The hand then swings from there onto the canonical palm,
    // on top of the forearm's swing, so it keeps its twist against the forearm.
    private static Quaternion BareHand(int hand, Quaternion forearmSwing, Vector3[] restPosition, Quaternion[] restRotation)
    {
        var forearm = ClipRig.Parent[hand];
        var along = Vector3.Normalize(restPosition[hand] - restPosition[forearm]);
        var aim = Vector3.Transform(Vector3.Transform(along, Quaternion.Inverse(restRotation[forearm])), restRotation[hand]);
        if (Vector3.Dot(aim, along) < MathF.Cos(MaxWristBend)) aim = along;
        var middle = (hand == 13 ? 20 : 35) + 6;
        return Quaternion.Normalize(BodyMath.FromTo(Vector3.Transform(aim, forearmSwing), ClipRig.RestDirection(hand, middle)) * forearmSwing);
    }

    private const float MaxWristBend = 60f * MathF.PI / 180f;

    // The middle, index and pinky knuckles of a hand joint, or -1s when the body lacks one.
    private static (int Middle, int Index, int Pinky) Palm(int hand, bool[] present)
    {
        var first = hand == 13 ? 20 : 35;
        var (index, middle, pinky) = (first + 3, first + 6, first + 12);
        return present[index] && present[middle] && present[pinky] ? (middle, index, pinky) : (-1, -1, -1);
    }

    private static Quaternion PalmFrame(Vector3 aim, Vector3 knuckles)
    {
        var y = Vector3.Normalize(aim);
        var x = Vector3.Normalize(knuckles - y * Vector3.Dot(knuckles, y));
        var z = Vector3.Cross(x, y);
        return Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));
    }
}
