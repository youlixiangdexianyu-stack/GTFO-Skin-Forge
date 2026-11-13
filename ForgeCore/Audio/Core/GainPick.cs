using System;

namespace ForgeAudio;

/// <summary>Picks the volume of a play whose cue gives a <c>[min, max]</c> range: evenly in decibels, so a range sounds as wide on
/// its quiet side as on its loud side. A range starting at silence picks linearly (silence has no decibel value).</summary>
internal static class GainPick
{
    /// <summary>The gain for <paramref name="unit"/> in 0..1 (0 the quietest, 1 the loudest).</summary>
    internal static float At(float min, float max, double unit)
    {
        if (min >= max) return max;
        var u = (float)Math.Clamp(unit, 0, 1);
        return min <= 0 ? min + (max - min) * u : min * MathF.Pow(max / min, u);
    }
}
