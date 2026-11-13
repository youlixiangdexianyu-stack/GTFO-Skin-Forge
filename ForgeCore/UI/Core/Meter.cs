using System;

namespace ForgeUI;

/// <summary>How a resource bar is coloured.</summary>
public enum MeterTone : byte
{
    Normal,
    Warn,
    Critical
}

/// <summary>The arithmetic behind resource bars.</summary>
internal static class MeterMath
{
    /// <summary>The fill of <paramref name="value"/> out of <paramref name="max"/>, 0..1 (0 for a non-positive max or NaN).</summary>
    public static float Fraction(float value, float max) => max > 0f ? Ease.Clamp01(value / max) : 0f;

    /// <summary>The tone for <paramref name="fraction"/>. A bar where low is bad (health) turns <see cref="MeterTone.Warn"/> at or
    /// below <paramref name="warnAt"/> and <see cref="MeterTone.Critical"/> at or below <paramref name="criticalAt"/>; with
    /// <paramref name="higherIsWorse"/> (heat) it is the other way round, at or above.</summary>
    public static MeterTone Tone(float fraction, float warnAt, float criticalAt, bool higherIsWorse)
    {
        if (higherIsWorse) return fraction >= criticalAt ? MeterTone.Critical : fraction >= warnAt ? MeterTone.Warn : MeterTone.Normal;
        return fraction <= criticalAt ? MeterTone.Critical : fraction <= warnAt ? MeterTone.Warn : MeterTone.Normal;
    }

    /// <summary>How many of <paramref name="count"/> cells a bar of <paramref name="fraction"/> lights: any fill above zero lights at
    /// least one, a full bar lights all.</summary>
    public static int Cells(float fraction, int count)
    {
        if (count <= 0 || fraction <= 0f) return 0;
        return Math.Clamp((int)MathF.Ceiling(fraction * count - 1e-4f), 1, count);
    }
}

/// <summary>The chip that trails a bar's fill when it drops: it holds where the fill was for <see cref="HoldSeconds"/>, then
/// eases down to it. When the fill rises the chip rises with it.</summary>
internal struct MeterTrail
{
    public const float HoldSeconds = 0.35f;
    private const float CatchUp = 5f;

    private float _target;
    private float _hold;

    /// <summary>The trailing chip's level, 0..1.</summary>
    public float Trail { get; private set; }

    public void Update(float target, float dt)
    {
        target = Ease.Clamp01(target);
        if (target < _target - 1e-4f) _hold = HoldSeconds;
        _target = target;
        if (target >= Trail) { Trail = target; _hold = 0f; return; }
        if (_hold > 0f) { _hold -= dt; return; }
        Trail = Ease.Damp(Trail, target, CatchUp, dt);
        if (Trail - target < 1e-3f) Trail = target;
    }

    public void Reset(float value)
    {
        _target = Trail = Ease.Clamp01(value);
        _hold = 0f;
    }
}
