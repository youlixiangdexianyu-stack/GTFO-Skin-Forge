using System;

namespace ForgeUI;

/// <summary>Screen geometry in HUD units (x right, y up, origin at the middle of the screen).</summary>
public static class ScreenMath
{
    /// <summary>Where a block of <paramref name="size"/> starts (its left or bottom) so that it grows in direction
    /// <paramref name="flow"/> from the anchor: -1 ends at the anchor, +1 starts at it, 0 is centred on it.</summary>
    public static float Span(int flow, float size) => flow < 0 ? -size : flow > 0 ? 0f : -size / 2f;

    /// <summary>A viewport point (0..1 from the bottom left) in HUD units from the middle of a screen of this half size.</summary>
    public static (float X, float Y) ViewportToHud(float viewportX, float viewportY, float halfWidth, float halfHeight) =>
        ((viewportX - 0.5f) * 2f * halfWidth, (viewportY - 0.5f) * 2f * halfHeight);

    /// <summary>Where a world marker is drawn. <paramref name="x"/>, <paramref name="y"/> is the target's HUD position from the
    /// middle of the screen; <paramref name="behind"/> is true when the target is behind the camera (its projection is mirrored). On
    /// screen (inside the screen shrunk by <paramref name="inset"/>) the marker stays on the target and the result is true. Otherwise
    /// it is moved onto the inset rectangle's edge, in the direction of the target from the middle of the screen (reversed when
    /// behind), and false is returned; <paramref name="angle"/> is that direction in degrees (0 = right, 90 = up), the way an
    /// arrow should point.</summary>
    public static bool EdgeMarker(float x, float y, bool behind, float halfWidth, float halfHeight, float inset,
        out float markerX, out float markerY, out float angle)
    {
        var limitX = Math.Max(1f, halfWidth - inset);
        var limitY = Math.Max(1f, halfHeight - inset);
        if (!behind && Math.Abs(x) <= limitX && Math.Abs(y) <= limitY)
        {
            markerX = x; markerY = y; angle = 90f;
            return true;
        }
        var dx = behind ? -x : x;
        var dy = behind ? -y : y;
        if (Math.Abs(dx) < 1e-3f && Math.Abs(dy) < 1e-3f) { dx = 0f; dy = behind ? -1f : 1f; }
        var k = 1f / Math.Max(Math.Abs(dx) / limitX, Math.Abs(dy) / limitY);
        markerX = dx * k;
        markerY = dy * k;
        angle = MathF.Atan2(dy, dx) * 57.29578f;
        return false;
    }
}
