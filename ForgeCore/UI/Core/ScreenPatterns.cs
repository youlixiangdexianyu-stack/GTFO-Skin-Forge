using System;

namespace ForgeUI;

/// <summary>
/// The placeholder pictures of the screen overlays, drawn in code until the delivered art files are in the kit's <c>icons</c> folder:
/// each is an alpha in 0..1 at a texel (u, v in 0..1, v up). Deterministic, so every machine draws the same picture.
/// </summary>
internal static class ScreenPatterns
{
    /// <summary>The hit-edge flash at pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="w"/> x <paramref name="h"/> texture
    /// drawn at one texel per screen pixel, y = 0 at the screen edge: a bright line along the edge with 45° chamfered ends, and under it a
    /// glow fading inwards with armour scratches and faint scanlines.</summary>
    public static float EdgeFlash(int x, int y, int w, int h)
    {
        var line = Math.Max(2f, h * 0.05f);
        var chamfer = h * 0.3f;
        var inset = Math.Min(y, chamfer);
        if (x < inset || x > w - 1 - inset) return 0f;
        if (y < line) return 1f;
        var depth = Ease.Clamp01((y - line) / Math.Max(1f, h - line));
        var glow = (1f - depth) * (1f - depth);
        var u = x / (float)w;
        var v = y / (float)h;
        var pattern = Scratches(u, v);
        // Faint scanlines every third screen row across the glow.
        var scan = y % 3 == 0 ? 0.7f : 1f;
        return glow * (0.35f + 0.65f * pattern) * scan;
    }


    /// <summary>Short diagonal scratches (the armour look).</summary>
    public static float Scratches(float u, float v)
    {
        var a = 0f;
        for (var i = 0; i < 3; i++)
        {
            var t = (u * 0.8f + v * 0.6f) * (14f + i * 9f) + i * 1.7f;
            var lane = MathF.Floor(t);
            var h = Hash(lane * 7.1f + i * 13.3f);
            var along = Hash(lane * 3.7f + i) * 1.4f - 0.2f;
            var position = u * 0.6f - v * 0.8f;
            var inside = MathF.Abs(position - along) < 0.12f + 0.1f * h ? 1f : 0f;
            var line = 1f - Ease.Clamp01(MathF.Abs(t - lane - 0.5f) / (0.06f + 0.06f * h));
            a = MathF.Max(a, line * inside * (0.5f + 0.5f * h));
        }
        return a;
    }

    /// <summary>The keep-clear mask for strong overlays: 0 in the middle 40 % of the screen (the crosshair and line of sight), rising to 1
    /// at 70 % of the way to the edge; measured on the screen's own proportions, so it is an ellipse over any aspect.</summary>
    public static float CentreClear(float u, float v)
    {
        var x = (u - 0.5f) * 2f;
        var y = (v - 0.5f) * 2f;
        return Ease.Smoothstep((MathF.Sqrt(x * x + y * y) - 0.4f) / 0.3f);
    }

    /// <summary>The mask of crack layer <paramref name="layer"/> (0 nearest the impact, 2 farthest) of a crack picture whose impact
    /// point is its bottom right corner: three bands by distance from that corner, softly joined, together the whole picture.</summary>
    public static float CrackLayer(float u, float v, int layer)
    {
        var dx = 1f - u;
        var r = MathF.Sqrt(dx * dx + v * v) / 1.41421356f;
        const float soft = 0.04f;
        var inner = layer == 0 ? -1f : layer == 1 ? 0.38f : 0.68f;
        var outer = layer == 0 ? 0.38f : layer == 1 ? 0.68f : 2f;
        return Ease.Clamp01((r - inner) / soft + 0.5f) * Ease.Clamp01((outer - r) / soft + 0.5f);
    }

    /// <summary>Scanline interference at pixel row <paramref name="row"/> of a screen-sized texture: thin bright lines of uneven strength
    /// every few rows, strong toward the edges and nothing in the middle (<see cref="CentreClear"/>).</summary>
    public static float Interference(float u, float v, int row)
    {
        var band = row / 3;
        var line = row % 3 == 0 ? 0.35f + 0.65f * Hash(band) : 0f;
        var tear = Hash(band / 7 + 500) > 0.8f ? 1f : 0.5f;
        return line * tear * CentreClear(u, v);
    }

    /// <summary>A soft glow across a band, <paramref name="v"/> 0..1 from one edge to the other: 1 in the middle third, fading smoothly
    /// to 0 at both edges.</summary>
    public static float GlowBand(float v)
    {
        var d = Ease.Clamp01((MathF.Abs(v - 0.5f) * 2f - 0.33f) / 0.67f);
        return (1f - d) * (1f - d);
    }

    /// <summary>A soft glow round a ring filling the unit square: brightest on the circle of radius 0.78 (of the half side), fading to 0
    /// at 0.56 and at the edge.</summary>
    public static float GlowRing(float u, float v)
    {
        var x = (u - 0.5f) * 2f;
        var y = (v - 0.5f) * 2f;
        var d = Ease.Clamp01(MathF.Abs(MathF.Sqrt(x * x + y * y) - 0.78f) / 0.22f);
        return (1f - d) * (1f - d);
    }

    /// <summary>A soft round blob filling the unit square (behind a number).</summary>
    public static float GlowBlob(float u, float v)
    {
        var x = MathF.Abs(u - 0.5f) * 2f;
        var y = MathF.Abs(v - 0.5f) * 2f;
        var d = Ease.Clamp01(MathF.Max(x, y) * 0.6f + MathF.Sqrt(x * x + y * y) * 0.4f);
        return (1f - d) * (1f - d);
    }

    private static float Hash(float n)
    {
        var s = MathF.Sin(n * 12.9898f + 78.233f) * 43758.547f;
        return s - MathF.Floor(s);
    }
}
