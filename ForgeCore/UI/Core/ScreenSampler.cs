using System;

namespace ForgeUI;

/// <summary>One measurement of the screen in the weapon list's space: its middle and half its size.</summary>
internal readonly record struct ScreenSample(float CentreX, float CentreY, float HalfWidth, float HalfHeight)
{
    /// <summary>A measurement the layout can use: finite and larger than a unit each way.</summary>
    public bool Usable => float.IsFinite(CentreX) && float.IsFinite(CentreY) && float.IsFinite(HalfWidth) && float.IsFinite(HalfHeight)
                          && HalfWidth > 1f && HalfHeight > 1f;
}

/// <summary>What the screen's measurement depends on: the resolution, the HUD camera's field of view and the HUD's scale. The screen
/// is measured again only when one of them changes, never because the HUD sways.</summary>
internal readonly record struct ScreenKey(int Width, int Height, float FieldOfView, float Scale)
{
    /// <summary>The same setup as <paramref name="other"/> (the field of view and the scale within rounding).</summary>
    public bool Same(in ScreenKey other) => Width == other.Width && Height == other.Height
                                            && Math.Abs(FieldOfView - other.FieldOfView) < 0.05f && Math.Abs(Scale - other.Scale) <= 0.001f * Math.Max(1f, Math.Abs(Scale));
}

/// <summary>
/// Settles the screen measurement. The game's HUD sways and bobs, so a single measurement in its space wanders by a few units from
/// frame to frame; laying out on every one moves every element with it. This takes <see cref="Samples"/> usable measurements after the
/// setup changed, keeps their median, and holds it until the setup (<see cref="ScreenKey"/>) changes again. Unusable measurements
/// (a camera that is not ready gives zero) are never taken. Nothing is laid out before the first median exists.
/// </summary>
internal sealed class ScreenSampler
{
    public const int Samples = 12;

    private readonly float[] _cx = new float[Samples], _cy = new float[Samples], _hw = new float[Samples], _hh = new float[Samples];
    private readonly float[] _sort = new float[Samples];
    private int _count;
    private ScreenKey _key;
    private bool _keyed;

    /// <summary>The settled screen, valid once <see cref="Ready"/>.</summary>
    public ScreenSample Settled { get; private set; }

    /// <summary>A settled screen exists (kept while a new one is being sampled after a change).</summary>
    public bool Ready { get; private set; }

    /// <summary>Counts the settled screens; the layout runs again whenever it changes.</summary>
    public int Version { get; private set; }

    /// <summary>The setup changed and new samples are being taken.</summary>
    public bool Sampling => _count < Samples;

    /// <summary>Feeds this frame's setup and, while sampling, its measurement (null when it could not be measured). Returns true when a
    /// new settled screen was just made.</summary>
    public bool Add(in ScreenKey key, ScreenSample? sample)
    {
        if (!_keyed || !key.Same(_key))
        {
            _key = key;
            _keyed = true;
            _count = 0;
        }
        if (!Sampling || sample is not { Usable: true } s) return false;
        _cx[_count] = s.CentreX;
        _cy[_count] = s.CentreY;
        _hw[_count] = s.HalfWidth;
        _hh[_count] = s.HalfHeight;
        if (++_count < Samples) return false;
        Settled = new ScreenSample(Median(_cx), Median(_cy), Median(_hw), Median(_hh));
        Ready = true;
        Version++;
        return true;
    }

    /// <summary>Forgets everything (the game made a new HUD).</summary>
    public void Reset()
    {
        _count = 0;
        _keyed = false;
        Ready = false;
    }

    private float Median(float[] values)
    {
        Array.Copy(values, _sort, Samples);
        Array.Sort(_sort);
        return (_sort[Samples / 2 - 1] + _sort[Samples / 2]) / 2f;
    }
}

/// <summary>Keeps a measured box steady: a new measurement replaces the kept one only when an edge moved by more than the tolerance, so
/// the game's HUD swaying under a measurement does not move the elements laid out around it.</summary>
internal static class HudBoxes
{
    /// <summary>How far an edge may wander before the box counts as moved, in HUD units.</summary>
    public const float Tolerance = 6f;

    /// <summary>The box to use: <paramref name="kept"/> unless <paramref name="measured"/> differs by more than <see cref="Tolerance"/>
    /// on any edge (or one of them is missing).</summary>
    public static HudBox? Settle(HudBox? kept, HudBox? measured)
    {
        if (kept is not { } k || measured is not { } m) return measured;
        return Math.Abs(k.Left - m.Left) > Tolerance || Math.Abs(k.Right - m.Right) > Tolerance || Math.Abs(k.Bottom - m.Bottom) > Tolerance
               || Math.Abs(k.Top - m.Top) > Tolerance ? m : k;
    }

    /// <summary>A drawn size rounded up to a step of <paramref name="step"/> units, so a text growing by a pixel does not move a region.</summary>
    public static float Step(float size, float step = 8f) => size <= 0f ? 0f : MathF.Ceiling(size / step) * step;
}
