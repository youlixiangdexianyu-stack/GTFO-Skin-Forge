using System;

namespace ForgeUI;

/// <summary>Easing curves and smoothing for HUD animation. Every function maps 0..1 to 0..1 (<see cref="OutBack"/> overshoots a
/// little before settling) and clamps its input, so a timer can be passed in as it is.</summary>
public static class Ease
{
    public static float Clamp01(float t) => float.IsNaN(t) ? 0f : t < 0f ? 0f : t > 1f ? 1f : t;

    public static float Lerp(float from, float to, float t) => from + (to - from) * Clamp01(t);

    /// <summary>The smoothstep curve 3t² - 2t³: a smooth start and stop.</summary>
    public static float Smoothstep(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }

    public static float OutCubic(float t) { t = Clamp01(t); var u = 1f - t; return 1f - u * u * u; }

    public static float InCubic(float t) { t = Clamp01(t); return t * t * t; }

    public static float InOutCubic(float t)
    {
        t = Clamp01(t);
        return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }

    /// <summary>Overshoots by about 10 % and settles at 1: a pop-in.</summary>
    public static float OutBack(float t)
    {
        t = Clamp01(t) - 1f;
        return 1f + t * t * (2.70158f * t + 1.70158f);
    }

    /// <summary>A repeating blink: 1 for the first half of each period, 0 for the second, <paramref name="hertz"/> times a second.</summary>
    public static float Blink(float seconds, float hertz) => seconds * hertz % 1f < 0.5f ? 1f : 0f;

    /// <summary>A smooth 0..1..0 wave, <paramref name="hertz"/> times a second.</summary>
    public static float Pulse(float seconds, float hertz) => 0.5f - 0.5f * MathF.Cos(seconds * hertz * 6.2831855f);

    /// <summary>Moves <paramref name="current"/> towards <paramref name="target"/> exponentially, the same at any frame rate:
    /// about 63 % of the way after 1 / <paramref name="rate"/> seconds.</summary>
    public static float Damp(float current, float target, float rate, float dt) =>
        current + (target - current) * (1f - MathF.Exp(-Math.Max(0f, rate) * Math.Max(0f, dt)));
}
