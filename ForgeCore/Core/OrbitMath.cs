using System;

namespace ForgeCore;

/// <summary>The arithmetic of <c>CameraLease.Orbit</c>.</summary>
internal static class OrbitMath
{
    /// <summary>How far the arm reaches: where the sweep hit, never past its own length.</summary>
    public static float Pulled(float hitDistance, float length, float radius) => Math.Clamp(hitDistance, Math.Min(radius, length), length);

    /// <summary>How far out the view is after <paramref name="dt"/> seconds, moving out or back over <paramref name="seconds"/>.</summary>
    public static float Blend(float at, bool outward, float dt, float seconds)
    {
        var step = seconds > 0f ? dt / seconds : 1f;
        return Math.Clamp(at + (outward ? step : -step), 0f, 1f);
    }
}
