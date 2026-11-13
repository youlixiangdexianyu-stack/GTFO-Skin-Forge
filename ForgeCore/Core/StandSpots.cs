using System;

namespace ForgeCore;

/// <summary>The order in which <c>Level.FindStandSpot</c> tries places around a body: straight behind first, then the back diagonals,
/// the sides, further behind, and the front last (or the mirror of that when the front is preferred). Pure geometry on the ground plane.</summary>
internal static class StandSpots
{
    // Body-space offsets (x right, z forward) in units of the clearance, in the order they are tried.
    private static readonly float[] Tries =
    {
        0f, -1f, 1f,
        -0.71f, -0.71f, 1f, 0.71f, -0.71f, 1f,
        -1f, 0f, 1f, 1f, 0f, 1f,
        0f, -1f, 1.5f,
        0f, 1f, 1.2f,
    };

    /// <summary>How many places are tried.</summary>
    public static int Count => Tries.Length / 3;

    /// <summary>Place number <paramref name="index"/> around a body facing <paramref name="yawDegrees"/> (Unity's yaw, clockwise seen
    /// from above): the world-space offset on x and z for a clearance of <paramref name="clearance"/> metres.</summary>
    public static void Offset(int index, float yawDegrees, float clearance, bool preferBehind, out float x, out float z)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        var scale = Tries[index * 3 + 2] * clearance;
        var local = Tries[index * 3] * scale;
        var ahead = Tries[index * 3 + 1] * scale * (preferBehind ? 1f : -1f);
        var radians = yawDegrees * (MathF.PI / 180f);
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        x = local * cos + ahead * sin;
        z = -local * sin + ahead * cos;
    }
}
