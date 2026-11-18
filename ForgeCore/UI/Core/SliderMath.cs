using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>The one slider arithmetic of the UI part: a value in a range [<c>min</c>, <c>max</c>] with a step (0 for continuous), its place along a
/// track, nudges and the number written beside it. Two users: the slider rows a mod adds to the game's settings pages
/// (<see cref="SettingsPages.AddAudioSliders"/>: a copy of the game's slider row whose handle moves between the row's two ends
/// (<c>m_handleLocalXPosMinMax</c>), value 0 to 1 shown in whole percent; a drag or step moves the handle by <c>-delta</c> as the game's own
/// <c>DoScrollDelta</c> does) and the menu sliders (<see cref="UISlider"/>).</summary>
internal static class SliderMath
{
    /// <summary><paramref name="value"/> kept in the range and, with a step, on the nearest step counted from <paramref name="min"/>.</summary>
    internal static float Snap(float value, float min, float max, float step)
    {
        if (!float.IsFinite(value)) return min;
        var lo = Math.Min(min, max);
        var hi = Math.Max(min, max);
        value = Math.Clamp(value, lo, hi);
        if (step > 0f && float.IsFinite(step)) value = Math.Clamp(lo + MathF.Round((value - lo) / step) * step, lo, hi);
        return value;
    }

    /// <summary>Where <paramref name="value"/> sits along the range: 0 at <paramref name="min"/>, 1 at <paramref name="max"/>.</summary>
    internal static float Fraction(float value, float min, float max)
        => max - min is var span && Math.Abs(span) > 1e-6f && float.IsFinite(value) ? Math.Clamp((value - min) / span, 0, 1) : 0;

    /// <summary>The value at <paramref name="fraction"/> of the range, snapped to the step.</summary>
    internal static float ValueAtFraction(float fraction, float min, float max, float step)
        => Snap(min + (max - min) * (float.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0), min, max, step);

    /// <summary><paramref name="value"/> moved by <paramref name="steps"/> steps (a hundredth of the range when continuous), kept in the range.</summary>
    internal static float Nudge(float value, int steps, float min, float max, float step)
        => Snap(Snap(value, min, max, step) + steps * (step > 0f ? step : Math.Abs(max - min) / 100f), min, max, step);

    /// <summary>The number written beside a slider: as many decimals as the step has (two when continuous).</summary>
    internal static string Text(float value, float step)
    {
        var decimals = step <= 0f ? 2 : step >= 1f ? 0 : Math.Clamp((int)MathF.Ceiling(-MathF.Log10(step) - 1e-4f), 0, 4);
        return (float.IsFinite(value) ? value : 0f).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>The handle position after a drag or step of <paramref name="delta"/>, kept between the ends.</summary>
    internal static float Step(float position, float delta, float left, float right)
        => float.IsFinite(position) && float.IsFinite(delta) ? Math.Clamp(position - delta, Math.Min(left, right), Math.Max(left, right)) : left;

    /// <summary>The value at handle position <paramref name="position"/>: 0 at the left end, 1 at the right.</summary>
    internal static float ValueAt(float position, float left, float right) => right > left ? Fraction(position, left, right) : 0;

    /// <summary>The handle position showing <paramref name="value"/>.</summary>
    internal static float PositionOf(float value, float left, float right) => left + (right - left) * Fraction(value, 0, 1);

    /// <summary>The value written next to the handle: whole percent.</summary>
    internal static string Text(float value) => ((int)MathF.Round(Fraction(value, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture);
}
