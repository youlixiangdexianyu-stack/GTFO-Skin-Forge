using System;

namespace ForgeUI;

/// <summary>
/// Alpha of the nine-sliced shape textures <c>HudCanvas</c> draws plates, frames, slanted segments and glows with. Each is a
/// square of <see cref="Size"/> texels whose borders hold the shaped part, so the shape keeps its corner or end at any size; the
/// arguments are texel centres (x right, y up).
/// </summary>
internal static class ShapePatterns
{
    /// <summary>Texture side of the chamfer and skew shapes; their border is <see cref="Border"/> texels (one unit when drawn).</summary>
    public const int Size = 64, Border = 16;

    /// <summary>The frame line's thickness in texels: drawn, a frame's line is <c>cut × Line / Border</c> thick.</summary>
    public const float Line = 2f;

    /// <summary>Texture side and border of the glow shapes; the shape's own edge sits <see cref="GlowEdge"/> texels in from the
    /// texture's edge (one unit when drawn), so the glow reaches one unit out and the line glow one unit each way.</summary>
    public const int GlowSize = 64, GlowBorder = 28, GlowEdge = 14;

    private const float Sqrt2 = 1.41421356f;

    /// <summary>Signed distance (texels, positive inside) to a rectangle with its top left and bottom right corners cut at 45°.</summary>
    public static float ChamferDistance(float x, float y)
    {
        var d = Math.Min(Math.Min(x, Size - x), Math.Min(y, Size - y));
        d = Math.Min(d, (x + (Size - y) - Border) / Sqrt2);
        d = Math.Min(d, ((Size - x) + y - Border) / Sqrt2);
        return d;
    }

    /// <summary>A filled chamfered plate.</summary>
    public static float ChamferFill(float x, float y) => Cover(ChamferDistance(x, y));

    /// <summary>A chamfered outline <see cref="Line"/> texels thick, inside the shape's edge.</summary>
    public static float ChamferLine(float x, float y)
    {
        var d = ChamferDistance(x, y);
        return Math.Clamp(Cover(d) - Cover(d - Line), 0f, 1f);
    }

    /// <summary>A parallelogram leaning right across the whole square: its left end runs from the bottom left corner up to
    /// (<see cref="Border"/>, top), its right end from (<see cref="Size"/> − <see cref="Border"/>, bottom) to the top right corner.
    /// Sliced with left and right borders only, the lean is one unit across the full height.</summary>
    public static float SkewFill(float x, float y)
    {
        var lean = y / Size * Border;
        var left = x - lean;
        var right = Size - Border + lean - x;
        return Cover(Math.Min(left, right));
    }

    /// <summary>Distance (texels, positive outside) from the glow textures' inner rectangle, the drawn shape's edge.</summary>
    private static float GlowOutside(float x, float y)
    {
        var dx = Math.Max(GlowEdge - x, x - (GlowSize - GlowEdge));
        var dy = Math.Max(GlowEdge - y, y - (GlowSize - GlowEdge));
        if (dx <= 0f && dy <= 0f) return Math.Max(dx, dy);
        return MathF.Sqrt(Math.Max(dx, 0f) * Math.Max(dx, 0f) + Math.Max(dy, 0f) * Math.Max(dy, 0f));
    }

    /// <summary>A filled soft box: solid inside the shape's edge, falling off over one unit outside it.</summary>
    public static float GlowFill(float x, float y)
    {
        var d = GlowOutside(x, y);
        return d <= 0f ? 1f : Falloff(d);
    }

    /// <summary>A soft ring on the shape's edge, falling off over one unit both ways (for outlines, whose inside stays clear).</summary>
    public static float GlowRing(float x, float y) => Falloff(Math.Abs(GlowOutside(x, y)));

    private static float Falloff(float d)
    {
        var t = d / GlowEdge;
        return t >= 1f ? 0f : MathF.Exp(-4f * t * t) * (1f - t);
    }

    /// <summary>Texture size of the infinity sign: <see cref="InfinityWidth"/> x <see cref="InfinityHeight"/> texels.</summary>
    public const int InfinityWidth = 96, InfinityHeight = 48;

    /// <summary>The infinity sign, a lemniscate (x² + y²)² = a²(x² − y²) whose loops span 0.84 of the texture's width, its line a ninth of
    /// the height thick: alpha at a texel centre. Drawn in code so it does not depend on the game's font having the glyph.</summary>
    public static float Infinity(float x, float y)
    {
        var a = InfinityWidth * 0.42f;
        var px = x - InfinityWidth / 2f;
        var py = y - InfinityHeight / 2f;
        var r2 = px * px + py * py;
        var f = r2 * r2 - a * a * (px * px - py * py);
        var gx = 4f * px * r2 - 2f * a * a * px;
        var gy = 4f * py * r2 + 2f * a * a * py;
        var gradient = MathF.Sqrt(gx * gx + gy * gy);
        // The curve crosses itself at the centre, where its gradient vanishes: there the distance is the distance to the centre.
        var distance = gradient < 1e-3f ? MathF.Sqrt(r2) : Math.Abs(f) / gradient;
        return Cover(InfinityHeight / 18f - distance);
    }

    private static float Cover(float d) => Math.Clamp(d + 0.5f, 0f, 1f);
}
