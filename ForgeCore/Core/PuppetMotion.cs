using System;

namespace ForgeCore;

/// <summary>The arithmetic behind driving a puppet's walk animation: a world velocity on the ground plane becomes the forward and right
/// components in the puppet's own frame, which is what the game's locomotion animators take. Pure, no game types.</summary>
public static class PuppetMotion
{
    /// <summary>Ground-plane velocity between two positions <paramref name="seconds"/> apart; zero when the time is not positive.</summary>
    public static (float X, float Z) Velocity(float fromX, float fromZ, float toX, float toZ, float seconds)
        => seconds > 0f ? ((toX - fromX) / seconds, (toZ - fromZ) / seconds) : (0f, 0f);

    /// <summary>Splits the world velocity (<paramref name="vx"/>, <paramref name="vz"/>) along the facing direction (<paramref name="fx"/>,
    /// <paramref name="fz"/>, need not be unit length): <paramref name="forward"/> is the speed along it, <paramref name="right"/> the speed to its
    /// right (Unity's left-handed ground plane). A facing of zero length gives zero for both.</summary>
    public static void Local(float vx, float vz, float fx, float fz, out float forward, out float right)
    {
        var length = MathF.Sqrt(fx * fx + fz * fz);
        if (length < 1e-6f) { forward = right = 0f; return; }
        fx /= length; fz /= length;
        forward = vx * fx + vz * fz;
        right = vx * fz - vz * fx;
    }
}
