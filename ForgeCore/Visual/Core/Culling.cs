using System;

namespace ForgeVisual;

/// <summary>Whether an effect is worth drawing this frame: far from the eye or outside the view it is paused and hidden, and it comes back once
/// it is in range again. Plain numbers, no Unity: the caller hands in the eye-relative offset of the effect's bounding sphere and the eye's
/// forward direction.</summary>
internal static class CullMath
{
    /// <summary>Metres of the beyond-range band an effect that is hidden has to come back through (the distance limit shrinks to this share), so one
    /// standing on the edge does not flicker.</summary>
    internal const float ReturnShare = 0.9f;

    /// <summary>The cone is widened by this factor so a quick turn finds the effects at the screen edge already drawn.</summary>
    internal const float ViewPadding = 1.2f;

    /// <summary>Tangent of the half angle of a cone that holds the whole view: the frustum's diagonal, padded.
    /// <paramref name="tanHalfFov"/> is the vertical half field of view's tangent.</summary>
    internal static float TanCone(float tanHalfFov, float aspect)
    {
        var horizontal = tanHalfFov * Math.Max(aspect, 0.1f);
        return (float)Math.Sqrt(tanHalfFov * tanHalfFov + horizontal * horizontal) * ViewPadding;
    }

    /// <summary>True when a sphere of <paramref name="radius"/> at (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>) from the eye is not
    /// worth drawing: beyond <paramref name="limit"/> metres (its surface, 0 or less: no limit; <paramref name="wasHidden"/> asks for
    /// <see cref="ReturnShare"/> of it to come back) or outside the cone of <paramref name="tanCone"/> round the forward direction (<paramref name="fx"/>,
    /// <paramref name="fy"/>, <paramref name="fz"/>, a unit vector). An eye inside the sphere always sees it.</summary>
    internal static bool Hidden(bool wasHidden, float dx, float dy, float dz, float radius, float limit, float fx, float fy, float fz, float tanCone)
    {
        var distSq = dx * dx + dy * dy + dz * dz;
        if (distSq <= radius * radius) return false;
        if (limit > 0f)
        {
            var reach = (wasHidden ? limit * ReturnShare : limit) + radius;
            if (distSq > reach * reach) return true;
        }
        var ahead = dx * fx + dy * fy + dz * fz;
        if (ahead < -radius) return true;
        var lateralSq = distSq - ahead * ahead;
        var lateral = lateralSq > 0f ? (float)Math.Sqrt(lateralSq) : 0f;
        // a sphere meets the cone while its centre is within radius / cos(half angle) = radius * sqrt(1 + tan^2) of it
        var margin = radius * (float)Math.Sqrt(1f + tanCone * tanCone);
        return lateral - margin > Math.Max(ahead, 0f) * tanCone;
    }
}
