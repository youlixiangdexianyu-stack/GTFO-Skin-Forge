using System;

namespace ForgeUI;

/// <summary>What a <see cref="HudShape"/> is: a filled rectangle (centre, width, height), a slanted cell (a parallelogram whose top
/// edge is shifted right by a quarter of its width) or a line (from X, Y to W, H, thickness T).</summary>
public enum HudShapeKind : byte { Rect, Cell, Line }

/// <summary>One piece of a drawn widget, in the element's units; drawn by <see cref="HudCanvas"/> in game and by any offline renderer
/// the same way.</summary>
public struct HudShape
{
    public HudShapeKind Kind;
    public float X, Y, W, H, T;
    public float R, G, B, A;
}

/// <summary>An RGBA colour for Core drawing code (no Unity types here).</summary>
public readonly record struct HudColour(float R, float G, float B, float A)
{
    public HudColour Faded(float alpha) => new(R, G, B, A * alpha);

    /// <summary>Mixed towards <paramref name="other"/> by <paramref name="t"/> (0 this colour, 1 the other).</summary>
    public HudColour Mix(HudColour other, float t) => new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t, A + (other.A - A) * t);
}

/// <summary>
/// The look of a progress bar every mod shares: slanted end caps, a dark track with a tick every tenth, a fill with a bright leading
/// edge and a highlight sweeping along it while it runs; on completion the whole bar flashes white for <see cref="FlashSeconds"/>, then
/// folds away over <see cref="FoldSeconds"/>. <see cref="Build"/> writes the shapes; it allocates nothing.
/// </summary>
internal static class ProgressBar
{
    public const float FlashSeconds = 0.15f, FoldSeconds = 0.25f, SweepSeconds = 1.1f;
    /// <summary>Shapes one bar writes at most.</summary>
    public const int MaxShapes = 20;
    private const int Ticks = 10;

    public static readonly HudColour Track = new(0.039f, 0.047f, 0.051f, 0.62f);
    public static readonly HudColour Tick = new(0.039f, 0.047f, 0.051f, 0.55f);
    public static readonly HudColour White = new(0.957f, 0.965f, 0.957f, 1f);

    /// <summary>Whether a bar <paramref name="doneAge"/> seconds after completion (negative: still running) still draws anything.</summary>
    public static bool Visible(float doneAge) => doneAge < FlashSeconds + FoldSeconds;

    /// <summary>Writes a bar from <paramref name="left"/> to <paramref name="left"/> + <paramref name="width"/>, centred on
    /// <paramref name="y"/>, filled to <paramref name="fraction"/> in <paramref name="colour"/>. <paramref name="doneAge"/> is the time since
    /// it completed (negative while running); <paramref name="now"/> drives the sweep. Returns the shapes written.</summary>
    public static int Build(Span<HudShape> into, float left, float y, float width, float height, float fraction, HudColour colour, float doneAge, float now)
    {
        if (!(width > 0f) || !(height > 0f) || !Visible(doneAge) || into.Length < MaxShapes) return 0;
        var n = 0;
        var done = doneAge >= 0f;
        var f = done ? 1f : Math.Clamp(fraction, 0f, 1f);
        var alpha = 1f;
        if (done && doneAge > FlashSeconds)
        {
            var fold = Math.Clamp((doneAge - FlashSeconds) / FoldSeconds, 0f, 1f);
            height *= 1f - Ease.Smoothstep(fold);
            alpha = 1f - fold;
            if (height < 0.5f) return 0;
        }
        var cap = Math.Min(height * 0.9f, width * 0.2f);
        var inner = width - 2f * cap;
        var innerLeft = left + cap;
        var flash = done && doneAge <= FlashSeconds ? 1f - doneAge / FlashSeconds : 0f;
        var fill = colour.Mix(White, flash * 0.85f).Faded(alpha);

        // Track, then the fill over it.
        Rect(into, ref n, innerLeft + inner / 2f, y, inner, height, Track.Faded(alpha));
        if (f > 0f) Rect(into, ref n, innerLeft + inner * f / 2f, y, inner * f, height, fill);
        // A highlight running along the fill while it is still filling.
        // Short bars (inside a key box) keep only the fill, the edge and the caps.
        var full = inner >= 60f;
        if (full && !done && f > 0.08f)
        {
            var phase = Fract(now / SweepSeconds);
            var w = Math.Min(inner * f, Math.Max(height * 1.5f, inner * 0.12f));
            var x = innerLeft + (inner * f - w) * phase + w / 2f;
            Rect(into, ref n, x, y, w, height * 0.5f, White.Faded(0.22f * alpha));
        }
        // Ticks every tenth, over the fill.
        for (var i = 1; full && i < Ticks; i++)
        {
            var x = innerLeft + inner * i / Ticks;
            Line(into, ref n, x, y - height * 0.32f, x, y + height * 0.32f, Math.Max(1f, height * 0.12f), Tick.Faded(alpha));
        }
        // The leading edge, brighter and a little taller than the bar.
        if (f > 0f && f < 1f)
        {
            var x = innerLeft + inner * f;
            Line(into, ref n, x, y - height * 0.7f, x, y + height * 0.7f, Math.Max(1.5f, height * 0.18f), White.Faded(0.9f * alpha));
        }
        // Slanted caps: the left one lit once anything is filled, the right one once complete.
        Cell(into, ref n, left + cap / 2f, y, cap, height, f > 0f ? fill : Track.Faded(alpha));
        Cell(into, ref n, left + width - cap / 2f, y, cap, height, f >= 1f ? fill : Track.Faded(alpha));
        return n;
    }


    private static float Fract(float v) => v - MathF.Floor(v);

    private static void Rect(Span<HudShape> into, ref int n, float x, float y, float w, float h, HudColour c)
        => into[n++] = new HudShape { Kind = HudShapeKind.Rect, X = x, Y = y, W = w, H = h, R = c.R, G = c.G, B = c.B, A = c.A };

    private static void Cell(Span<HudShape> into, ref int n, float x, float y, float w, float h, HudColour c)
        => into[n++] = new HudShape { Kind = HudShapeKind.Cell, X = x, Y = y, W = w, H = h, R = c.R, G = c.G, B = c.B, A = c.A };

    private static void Line(Span<HudShape> into, ref int n, float x0, float y0, float x1, float y1, float t, HudColour c)
        => into[n++] = new HudShape { Kind = HudShapeKind.Line, X = x0, Y = y0, W = x1, H = y1, T = t, R = c.R, G = c.G, B = c.B, A = c.A };
}
