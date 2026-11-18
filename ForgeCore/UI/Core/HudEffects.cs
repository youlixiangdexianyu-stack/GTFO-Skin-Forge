using System;

namespace ForgeUI;

/// <summary>The soft glow every piece a <c>HudCanvas</c> draws gets (a blurred copy behind it), shared by every mod: see
/// <c>UIKit.HudGlow</c>. The player's <c>Screen / Glow</c> multiplies <see cref="Strength"/>.</summary>
public sealed class HudGlowSettings
{
    /// <summary>Off draws no glow at all. Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far the glow reaches past a piece's edge, in 1080p pixels. Default 5.</summary>
    public float Radius { get; set; } = 5f;

    /// <summary>The glow's opacity as a share of its piece's, 0..1. Default 0.55.</summary>
    public float Strength { get; set; } = 0.55f;

    /// <summary>The glow strength in effect with the player's multiplier; 0 draws none.</summary>
    public float Effective(float playerScale) =>
        Enabled && Radius > 0f ? Math.Clamp(Strength, 0f, 1f) * Math.Max(0f, playerScale) : 0f;
}

/// <summary>The faint colour fringe every piece a <c>HudCanvas</c> draws gets: a red copy shifted left and a blue copy shifted
/// right, like a display seen through a curved visor. Shared by every mod: see <c>UIKit.HudFringe</c>; the player's
/// <c>Screen / Fringe</c> turns it off. <see cref="Pulse"/> strengthens it for a moment (power-up, a heavy hit).</summary>
public sealed class HudFringeSettings
{
    private float _pulseSeconds, _pulseStart = float.NaN, _pulseBoost;

    /// <summary>Off draws no fringe. Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far each copy is shifted, in 1080p pixels. Default 1.</summary>
    public float Pixels { get; set; } = 1f;

    /// <summary>Each copy's opacity as a share of its piece's, 0..1. Default 0.16.</summary>
    public float Strength { get; set; } = 0.16f;

    /// <summary>Strengthens the fringe at once to <paramref name="boost"/> times (shift and opacity), falling back over
    /// <paramref name="seconds"/>.</summary>
    public void Pulse(float seconds, float boost = 3f)
    {
        if (!(seconds > 0f) || !(boost > 1f)) return;
        _pulseSeconds = seconds;
        _pulseBoost = boost;
        _pulseStart = float.NaN;
    }

    /// <summary>The pulse factor at <paramref name="now"/> (1 with no pulse running); the first call after <see cref="Pulse"/> starts
    /// it.</summary>
    public float Boost(float now)
    {
        if (_pulseSeconds <= 0f) return 1f;
        if (float.IsNaN(_pulseStart)) _pulseStart = now;
        var age = (now - _pulseStart) / _pulseSeconds;
        if (age >= 1f || age < 0f) { _pulseSeconds = 0f; return 1f; }
        return 1f + (_pulseBoost - 1f) * (1f - age);
    }

    /// <summary>The fringe opacity share in effect at <paramref name="now"/>; 0 draws none.</summary>
    public float Effective(bool playerOn, float now) =>
        Enabled && playerOn && Pixels > 0f ? Math.Clamp(Strength * Boost(now), 0f, 1f) : 0f;
}
