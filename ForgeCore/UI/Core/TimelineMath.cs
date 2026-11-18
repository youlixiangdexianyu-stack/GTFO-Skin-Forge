using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>The arithmetic of a time bar (<see cref="HudTimeline"/>), without Unity: where a moment sits on the bar, which moment a
/// point on the bar stands for, which mark a pointer is on, and the clock text.</summary>
public static class TimelineMath
{
    /// <summary>How far through <paramref name="durationMs"/> the moment is, 0..1 (0 for an empty duration).</summary>
    public static float Fraction(uint positionMs, uint durationMs) => durationMs == 0 ? 0f : Math.Clamp(positionMs / (float)durationMs, 0f, 1f);

    /// <summary>The moment at <paramref name="x"/> on a bar from <paramref name="left"/>, <paramref name="width"/> long; clamped to the bar.</summary>
    public static uint At(float x, float left, float width, uint durationMs)
    {
        if (width <= 0f || durationMs == 0) return 0;
        var t = Math.Clamp((x - left) / width, 0f, 1f);
        return (uint)Math.Round(t * durationMs);
    }

    /// <summary>The index of the mark nearest to <paramref name="x"/> within <paramref name="reach"/> (bar units), or -1. Marks are given by
    /// count and time, so any list works without copying.</summary>
    public static int NearestMark(float x, float left, float width, uint durationMs, int count, Func<int, uint> timeOf, float reach)
    {
        var best = -1;
        var bestDistance = reach;
        for (var i = 0; i < count; i++)
        {
            var distance = Math.Abs(left + Fraction(timeOf(i), durationMs) * width - x);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>"m:ss", or "h:mm:ss" from an hour on.</summary>
    public static string Clock(uint ms)
    {
        var seconds = ms / 1000;
        var h = seconds / 3600; var m = seconds / 60 % 60; var s = seconds % 60;
        return h > 0
            ? h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture)
            : (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>A speed as the bar shows it: "0.25×", "1×", "8×".</summary>
    public static string Speed(float speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}
