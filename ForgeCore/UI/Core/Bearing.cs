using System;

namespace ForgeUI;

/// <summary>Horizontal directions relative to where the player looks, without Unity: an azimuth in degrees, 0 straight ahead, 90 to
/// the right, 180 (or −180) behind, −90 to the left. Used by <see cref="HitEdge"/> and <see cref="HudBearingArc"/>.</summary>
internal static class Bearing
{
    /// <summary>The azimuth of a point <paramref name="toX"/>, <paramref name="toZ"/> away (world x and z) from the viewer, whose
    /// horizontal facing is <paramref name="forwardX"/>, <paramref name="forwardZ"/>; in −180..180. NaN when either is zero length.</summary>
    public static float Relative(float forwardX, float forwardZ, float toX, float toZ)
    {
        if (forwardX * forwardX + forwardZ * forwardZ < 1e-12f || toX * toX + toZ * toZ < 1e-12f) return float.NaN;
        var facing = MathF.Atan2(forwardX, forwardZ);
        var target = MathF.Atan2(toX, toZ);
        return Wrap((target - facing) * 180f / MathF.PI);
    }

    /// <summary><paramref name="degrees"/> brought into −180..180.</summary>
    public static float Wrap(float degrees)
    {
        if (!float.IsFinite(degrees)) return degrees;
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        else if (degrees <= -180f) degrees += 360f;
        return degrees;
    }

    /// <summary>The smallest angle between two azimuths, 0..180.</summary>
    /// <summary>Half the horizontal field of view, in degrees, of a camera with <paramref name="verticalFov"/> degrees vertically on a
    /// screen <paramref name="aspect"/> wide per unit of height.</summary>
    public static float HalfHorizontalFov(float verticalFov, float aspect) =>
        MathF.Atan(MathF.Tan(Math.Clamp(verticalFov, 1f, 179f) * 0.5f * MathF.PI / 180f) * Math.Max(0.1f, aspect)) * 180f / MathF.PI;

    /// <summary>Whether a contact at <paramref name="azimuth"/> (0 straight ahead) is inside the view <paramref name="halfFov"/>
    /// degrees either side of ahead.</summary>
    public static bool InView(float azimuth, float halfFov) => Between(azimuth, 0f) <= halfFov;
    public static float Between(float a, float b) => MathF.Abs(Wrap(a - b));

    /// <summary>Where an azimuth meets the edge of a screen <paramref name="aspect"/> wide and 1 high, seen from above with ahead at the
    /// top: (<c>U</c>, <c>V</c>) in the unit square (0, 0 bottom left), and <c>Normal</c>, the screen angle in degrees pointing from the
    /// edge into the screen (90 up from the bottom edge, 270 down from the top edge, 0 right from the left edge, 180 left from the right).</summary>
    public static (float U, float V, float Normal) EdgePoint(float azimuth, float aspect)
    {
        aspect = Math.Max(0.1f, aspect);
        var a = azimuth * MathF.PI / 180f;
        // Direction on the screen: ahead is up, right is right.
        var dx = MathF.Sin(a);
        var dy = MathF.Cos(a);
        var hw = aspect / 2f;
        var tx = MathF.Abs(dx) < 1e-6f ? float.MaxValue : hw / MathF.Abs(dx);
        var ty = MathF.Abs(dy) < 1e-6f ? float.MaxValue : 0.5f / MathF.Abs(dy);
        float x, y, normal;
        if (tx < ty)
        {
            x = MathF.Sign(dx) * hw;
            y = dy * tx;
            normal = dx > 0f ? 180f : 0f;
        }
        else
        {
            x = dx * ty;
            y = MathF.Sign(dy) * 0.5f;
            normal = dy > 0f ? 270f : 90f;
        }
        return (x / aspect + 0.5f, y + 0.5f, normal);
    }

    /// <summary>Where an azimuth goes on an arc of <paramref name="radius"/> around the centre covering <paramref name="span"/> degrees
    /// centred straight ahead (up): (x, y) from the centre, and whether it was inside the span (outside ones are pressed to the nearer end).</summary>
    public static (float X, float Y, bool Inside) ArcPoint(float azimuth, float radius, float span)
    {
        span = Math.Clamp(span, 0f, 360f);
        var half = span / 2f;
        var a = Wrap(azimuth);
        var inside = span >= 360f || MathF.Abs(a) <= half;
        if (!inside) a = MathF.Sign(a) * half;
        var r = a * MathF.PI / 180f;
        return (MathF.Sin(r) * radius, MathF.Cos(r) * radius, inside);
    }

    /// <summary>The silhouette layer for a hit from <paramref name="azimuth"/>: 0 front, 1 back, 2 left, 3 right.</summary>
    public static int Quadrant(float azimuth)
    {
        var a = Wrap(azimuth);
        if (float.IsNaN(a)) return 0;
        var abs = MathF.Abs(a);
        if (abs <= 45f) return 0;
        if (abs >= 135f) return 1;
        return a < 0f ? 2 : 3;
    }
}
