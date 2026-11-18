using System;

namespace ForgeUI;

/// <summary>The rhythm of a <see cref="PowerOnTimeline"/>: when each block of panels comes on and how the power-down runs. Seconds.</summary>
public sealed class PowerOnRhythm
{
    /// <summary>When each block comes on (block 0 first is usual, not required); its panels are black until then.</summary>
    public float[] Starts { get; init; } = Array.Empty<float>();

    /// <summary>How long a block's glitch takes to settle to the plain picture.</summary>
    public float ShiftSeconds { get; init; } = 0.26f;

    /// <summary>The seams stay this long after the last block settled, then fade over <see cref="SeamFade"/>.</summary>
    public float SeamHold { get; init; } = 0.06f;

    public float SeamFade { get; init; } = 0.10f;

    /// <summary>Powering down, the blocks go in reverse order (the last block first), one every <see cref="DownStep"/>, each glitching
    /// for <see cref="DownShiftSeconds"/> and turning black over the last 40 % of it.</summary>
    public float DownStep { get; init; } = 0.055f;

    public float DownShiftSeconds { get; init; } = 0.14f;

    /// <summary>The black holds until this moment of the power-down, then clears over <see cref="DownClear"/>.</summary>
    public float DownClearStart { get; init; } = 1f;

    public float DownClear { get; init; } = 0.2f;
}

/// <summary>
/// The timeline of a screen power-on cut into panels, without Unity: every panel belongs to a block; a block is black until its start,
/// comes on with a strong glitch that settles to the plain picture over the rhythm's shift time, and the seams between the panels fade
/// once the last block has settled. Powering down runs the blocks in reverse: glitch up, black, hold, clear. Nothing here allocates
/// after it is made.
/// </summary>
public sealed class PowerOnTimeline
{
    private readonly int[] _groupOf;
    private readonly float[] _starts;
    private readonly PowerOnRhythm _rhythm;

    /// <param name="groupOf">The block of each panel (0 .. blocks − 1).</param>
    /// <param name="rhythm">When the blocks come on and how the power-down runs.</param>
    public PowerOnTimeline(int[] groupOf, PowerOnRhythm rhythm)
    {
        _groupOf = (int[])(groupOf ?? throw new ArgumentNullException(nameof(groupOf))).Clone();
        _rhythm = rhythm ?? throw new ArgumentNullException(nameof(rhythm));
        _starts = (float[])rhythm.Starts.Clone();
        if (_starts.Length == 0) throw new ArgumentException("the rhythm names no block start", nameof(rhythm));
        foreach (var g in _groupOf)
            if (g < 0 || g >= _starts.Length) throw new ArgumentException("panel block " + g + " has no start", nameof(groupOf));
        var last = float.MinValue;
        foreach (var s in _starts) last = Math.Max(last, s);
        LastLit = last + rhythm.ShiftSeconds;
        Assembled = LastLit + rhythm.SeamHold + rhythm.SeamFade;
        DownDark = (Groups - 1) * rhythm.DownStep + rhythm.DownShiftSeconds;
        DownSeconds = rhythm.DownClearStart + rhythm.DownClear;
    }

    public int Panels => _groupOf.Length;

    public int Groups => _starts.Length;

    public PowerOnRhythm Rhythm => _rhythm;

    public int GroupOf(int panel) => _groupOf[panel];

    public float GroupStart(int group) => _starts[group];

    public float StartOf(int panel) => _starts[_groupOf[panel]];

    /// <summary>When the last block has settled.</summary>
    public float LastLit { get; }

    /// <summary>The picture is whole: every block has settled and the seams are gone, so the power-on layer is done. A caller that holds
    /// its player still during the power-on gives control back at this moment.</summary>
    public float Assembled { get; }

    /// <summary>Whether the picture is whole at <paramref name="s"/> (<see cref="Assembled"/>).</summary>
    public bool IsAssembled(float s) => s >= Assembled;

    /// <summary>A number that makes each block's glitch different (the two panels of a block alike).</summary>
    public float Seed(int panel) => _groupOf[panel] * 1.7f + 0.3f;

    /// <summary>Whether panel <paramref name="panel"/> is still black at <paramref name="s"/> (before its block comes on).</summary>
    public bool Black(int panel, float s) => s < StartOf(panel);

    /// <summary>Whether the panel shows the plain picture again (its glitch has settled).</summary>
    public bool Plain(int panel, float s) => s >= StartOf(panel) + _rhythm.ShiftSeconds;

    /// <summary>How strong the panel's glitch is at <paramref name="s"/> (0 plain, 1 the strongest, just as it comes on).</summary>
    public float Shift(int panel, float s)
    {
        var u = (s - StartOf(panel)) / Math.Max(1e-4f, _rhythm.ShiftSeconds);
        if (u < 0f || Plain(panel, s)) return 0f;
        return MathF.Pow(1f - u, 1.6f);
    }

    /// <summary>Opacity of the seams: whole until the seam hold after the last block settled, then fading; none once assembled.</summary>
    public float SeamAlpha(float s) => IsAssembled(s) ? 0f : 1f - Math.Clamp((s - LastLit - _rhythm.SeamHold) / Math.Max(1e-4f, _rhythm.SeamFade), 0f, 1f);

    // ---- powering down ---------------------------------------------------------------------------------------------

    /// <summary>Every block is black from here on.</summary>
    public float DownDark { get; }

    /// <summary>The power-down is over (the black has cleared).</summary>
    public float DownSeconds { get; }

    /// <summary>Powering down, <paramref name="d"/> seconds in: how far block <paramref name="group"/> has gone (0 plain picture, 1 gone); the
    /// last block first.</summary>
    public float DownGroup(int group, float d) =>
        Math.Clamp((d - (Groups - 1 - group) * _rhythm.DownStep) / Math.Max(1e-4f, _rhythm.DownShiftSeconds), 0f, 1f);

    /// <summary>How far panel <paramref name="panel"/> has gone; its glitch grows with it.</summary>
    public float DownShift(int panel, float d) => DownGroup(_groupOf[panel], d);

    /// <summary>How black the panel is (0 none, 1 fully), over the last 40 % of its glitch.</summary>
    public float DownBlack(int panel, float d) => Math.Clamp((DownShift(panel, d) - 0.6f) / 0.4f, 0f, 1f);

    /// <summary>Opacity of the whole power-down layer: 1 until the clear starts, then to 0; the seams follow it.</summary>
    public float DownAlpha(float d) => 1f - Math.Clamp((d - _rhythm.DownClearStart) / Math.Max(1e-4f, _rhythm.DownClear), 0f, 1f);
}
