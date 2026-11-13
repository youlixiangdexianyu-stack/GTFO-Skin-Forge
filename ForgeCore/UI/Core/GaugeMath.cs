using EnemyAlert = ForgeCore.EnemyAlert;
using System;

namespace ForgeUI;

/// <summary>How a bar shows a value below its critical mark.</summary>
public enum MeterPulse : byte
{
    /// <summary>Pulses for as long as it stays below.</summary>
    Pulse,
    /// <summary>Flashes for <see cref="GaugeMath.FlashSeconds"/> after it drops below, then stays steady red.</summary>
    FlashThenSteady,
    /// <summary>Red without flashing.</summary>
    None,
    /// <summary>Red with a white flash for <see cref="GaugeMath.FlashSeconds"/> when it drops below, again each time it loses another
    /// segment while below, and without pause under the bar's fail mark (<see cref="SegmentBarMotion"/>).</summary>
    FlashOnLoss,
}

/// <summary>Which side of the crosshair a <see cref="HudArcGauge"/> is on.</summary>
public enum ArcSide : byte
{
    Left,
    Right,
}

/// <summary>Segmented bars, arcs and pulses, without Unity.</summary>
public static class GaugeMath
{
    public const float FlashSeconds = 0.6f;

    /// <summary>How full segment <paramref name="index"/> of <paramref name="count"/> equal segments is at <paramref name="value"/> (0..1):
    /// 1 for a full one, 0 for an empty one, the share in between for the one the value ends in.</summary>
    public static float Segment(float value, int index, int count)
    {
        if (count < 1) count = 1;
        return Ease.Clamp01(Ease.Clamp01(value) * count - index);
    }

    /// <summary>The alpha factor of a bar in critical state <paramref name="since"/> seconds after it went critical (negative: not critical).</summary>
    public static float CriticalAlpha(MeterPulse mode, float since, float now)
    {
        if (since < 0f) return 1f;
        return mode switch
        {
            MeterPulse.Pulse => 0.55f + 0.45f * Ease.Pulse(now, 2.5f),
            MeterPulse.FlashThenSteady => since < FlashSeconds ? 0.35f + 0.65f * Ease.Blink(since, 6f) : 1f,
            _ => 1f,
        };
    }

    /// <summary>The angle (degrees, 0 = right, counter-clockwise) at fraction <paramref name="t"/> (0 bottom, 1 top) of an arc of
    /// <paramref name="sweep"/> degrees centred on the horizontal at <paramref name="side"/> of the centre; it always fills from the bottom up.</summary>
    public static float ArcAngle(ArcSide side, float sweep, float t)
    {
        var half = Math.Clamp(sweep, 1f, 180f) / 2f;
        var up = -half + Ease.Clamp01(t) * 2f * half;
        return side == ArcSide.Right ? up : 180f - up;
    }

    /// <summary>The value an arc shows while it refills: from <paramref name="from"/> up to 1 over <paramref name="progress"/> 0..1.</summary>
    public static float Refill(float from, float progress) => Ease.Lerp(Ease.Clamp01(from), 1f, Ease.OutCubic(progress));

    /// <summary>The colour band of a silhouette part at <paramref name="damage"/> 0..1: 0 normal below a third, 1 caution below two
    /// thirds, 2 danger above.</summary>
    public static int DamageBand(float damage) => damage < 0.34f ? 0 : damage < 0.67f ? 1 : 2;

    /// <summary>How far below its middle a bar curved by <paramref name="curve"/> is drawn at fraction <paramref name="t"/> of its
    /// width: 0 in the middle, <paramref name="curve"/> at both ends (a parabola, like a curved visor).</summary>
    public static float Droop(float t, float curve)
    {
        var u = 2f * Ease.Clamp01(t) - 1f;
        return curve * u * u;
    }

    /// <summary>The box (HUD units from the crosshair, x right, y up) that a <see cref="HudArcGauge"/> of <paramref name="radius"/>,
    /// <paramref name="sweep"/> and <paramref name="thickness"/> can draw in: the arc, its value at the lower end (at most
    /// <paramref name="readoutWidth"/> wide, 18 tall), the icon at the upper end and a critical label <paramref name="labelWidth"/> wide
    /// beside it (0 for none).</summary>
    public static HudBox ArcExtent(ArcSide side, float radius, float sweep, float thickness, float readoutWidth, float labelWidth)
    {
        var sign = side == ArcSide.Right ? 1f : -1f;
        var outer = radius + thickness / 2f;
        var inner = radius - thickness - 5f - thickness / 2f;
        var near = float.MaxValue;
        var far = float.MinValue;
        var low = float.MaxValue;
        var high = float.MinValue;
        void Add(float x, float y)
        {
            near = Math.Min(near, x); far = Math.Max(far, x);
            low = Math.Min(low, y); high = Math.Max(high, y);
        }
        for (var i = 0; i <= 8; i++)
        {
            var a = ArcAngle(side, sweep, i / 8f) * MathF.PI / 180f;
            Add(MathF.Cos(a) * outer, MathF.Sin(a) * outer);
            Add(MathF.Cos(a) * inner, MathF.Sin(a) * inner);
        }
        var rb = (ArcAngle(side, sweep, 0f) - sign * 6f) * MathF.PI / 180f;
        var rx = MathF.Cos(rb) * (radius + 12f);
        var ry = MathF.Sin(rb) * (radius + 12f);
        Add(rx, ry - 9f);
        Add(rx + sign * readoutWidth, ry + 9f);
        var ru = (ArcAngle(side, sweep, 1f) + sign * 7f) * MathF.PI / 180f;
        var ix = MathF.Cos(ru) * (radius + 10f);
        var iy = MathF.Sin(ru) * (radius + 10f);
        Add(ix - 7f, iy - 7f);
        Add(ix + 7f, iy + 7f);
        if (labelWidth > 0f)
        {
            Add(sign * (radius + 16f), -7f);
            Add(sign * (radius + 16f + labelWidth), 7f);
        }
        return new HudBox(near, low, far, high);
    }
    /// <summary>The cells an arc of <paramref name="cells"/> is drawn with: one per cell up to <paramref name="maxCells"/>, merged
    /// into <paramref name="maxCells"/> above (0 or less: no limit).</summary>
    public static int ArcCells(int cells, int maxCells) => cells <= 0 ? 0 : maxCells > 0 && cells > maxCells ? maxCells : cells;

    /// <summary>How a vertical cell gauge looks at <paramref name="value"/> (0..1) with its warning at <paramref name="warnBelow"/>:
    /// 0 normal, 1 warning (below the mark), 2 empty (exactly 0: drawn with a red frame).</summary>
    public static int CellState(float value, float warnBelow) => value <= 0f ? 2 : value < warnBelow ? 1 : 0;

    /// <summary>The alpha of a world marker <paramref name="distance"/> metres away fading from <paramref name="start"/> to
    /// <paramref name="end"/>; 1 when the fade is off (<paramref name="end"/> not beyond <paramref name="start"/> or 0).</summary>
    public static float DistanceFade(float distance, float start, float end)
    {
        if (end <= 0f || end <= start) return 1f;
        return 1f - Ease.Clamp01((distance - start) / (end - start));
    }
}

/// <summary>
/// What a segmented bar shows of its value, without Unity: the fill eases towards the value at a rate (and rolls up from 0 after
/// <see cref="RollUp"/>), the number beside it follows the eased fill, and the <see cref="MeterPulse.FlashOnLoss"/> alarm. Allocates
/// only a new readout string when its number changes.
/// </summary>
internal sealed class SegmentBarMotion
{
    /// <summary>The eased fill lands on the value once it is this close.</summary>
    public const float Snap = 0.005f;

    private float _flashAt = float.NegativeInfinity;
    private int _segment = int.MinValue, _number = int.MinValue;
    private bool _critical, _started;

    /// <summary>The fill as drawn, 0..1.</summary>
    public float Shown { get; private set; }

    /// <summary>The alarm's white this frame, 0..1 (0 steady).</summary>
    public float Alarm { get; private set; }

    /// <summary>The shown fill times the readout's maximum, rounded up; the same string while the number is the same.</summary>
    public string Readout { get; private set; } = "";

    /// <summary>The fill starts again from 0 and rolls up to the value (a part coming online).</summary>
    public void RollUp()
    {
        _started = true;
        Shown = 0f;
    }

    /// <summary>Forgets the shown fill, the alarm and the readout: the next update shows the value at once (a new subject).</summary>
    public void Restart()
    {
        _started = _critical = false;
        _flashAt = float.NegativeInfinity;
        _segment = _number = int.MinValue;
        Alarm = 0f;
    }

    /// <param name="value">The real value, 0..1.</param>
    /// <param name="dt">Seconds since the last update.</param>
    /// <param name="now">The clock the alarm is timed on.</param>
    /// <param name="easeRate">How fast the drawn fill follows (per second, exponential).</param>
    /// <param name="segments">The bar's segments; losing one while critical flashes again.</param>
    /// <param name="critical">Whether the bar is in its critical tone (judged on the real value).</param>
    /// <param name="failAt">Under this the alarm flashes without pause; negative never.</param>
    /// <param name="readoutMax">What a full bar reads as; 0 or less makes no readout.</param>
    public void Update(float value, float dt, float now, float easeRate, int segments, bool critical, float failAt, float readoutMax)
    {
        value = Ease.Clamp01(value);
        if (!_started)
        {
            _started = true;
            Shown = value;
        }
        else
        {
            Shown = Ease.Damp(Shown, value, easeRate, dt);
            if (Math.Abs(Shown - value) < Snap) Shown = value;
        }

        var segment = (int)MathF.Ceiling(value * Math.Max(1, segments) - 0.001f);
        if (critical && (!_critical || segment < _segment)) _flashAt = now;
        _critical = critical;
        _segment = segment;
        var age = now - _flashAt;
        Alarm = !critical ? 0f
            : value < failAt ? 0.5f + 0.5f * MathF.Cos(now * 22f)
            : age >= 0f && age < GaugeMath.FlashSeconds ? 0.5f + 0.5f * MathF.Cos(age * 20f) : 0f;

        if (readoutMax <= 0f) return;
        var number = (int)MathF.Ceiling(Shown * readoutMax - 1e-3f);
        if (number == _number) return;
        _number = number;
        Readout = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A segmented bar's thin layer (a shield) as it is struck, without Unity: a drop is a hit and flashes the layer for
/// <see cref="HitSeconds"/>; a drop to empty is a break and flashes it brighter for <see cref="BreakSeconds"/>. The first reading after
/// <see cref="Restart"/>, and readings while the layer is not shown, flash nothing.
/// </summary>
internal sealed class LayerHits
{
    public const float HitSeconds = 0.18f, BreakSeconds = 0.3f;
    /// <summary>A drop smaller than this is the value settling, not a hit.</summary>
    public const float MinDrop = 0.002f;

    private float _last = -1f, _hitAt = float.NegativeInfinity, _brokeAt = float.NegativeInfinity;

    /// <param name="value">The layer's value, 0..1.</param>
    /// <param name="shown">Whether the layer is drawn now; a hidden layer only remembers its value.</param>
    /// <param name="now">The clock the flashes are timed on.</param>
    public void Update(float value, bool shown, float now)
    {
        value = Ease.Clamp01(value);
        if (shown && _last >= 0f && value < _last - MinDrop)
        {
            _hitAt = now;
            if (value <= 0f) _brokeAt = now;
        }
        _last = value;
    }

    /// <summary>The white over the layer this frame, 0..1: a break's flash, else a hit's.</summary>
    public float Flash(float now)
    {
        var broke = now - _brokeAt;
        if (broke >= 0f && broke < BreakSeconds) return 0.9f * (1f - broke / BreakSeconds);
        var hit = now - _hitAt;
        return hit >= 0f && hit < HitSeconds ? 0.6f * (1f - hit / HitSeconds) : 0f;
    }

    /// <summary>Forgets the last value and any flash (a new subject).</summary>
    public void Restart()
    {
        _last = -1f;
        _hitAt = _brokeAt = float.NegativeInfinity;
    }
}

/// <summary>The damage taken from each side of a directional damage display, without Unity: hits add up per layer since the last
/// <see cref="Reset"/>, and a layer's shown damage is the overall loss shared out by those sums (the most hit side shows the whole loss).
/// No allocation after construction.</summary>
internal sealed class DamageShares
{
    private readonly float[] _taken;

    public DamageShares(int layers) => _taken = new float[Math.Max(1, layers)];

    public int Layers => _taken.Length;

    public void Reset() => Array.Clear(_taken, 0, _taken.Length);

    /// <summary>Adds <paramref name="amount"/> to <paramref name="layer"/>; out of range or not above 0 does nothing.</summary>
    public void Add(int layer, float amount)
    {
        if ((uint)layer < (uint)_taken.Length && amount > 0f) _taken[layer] += amount;
    }

    /// <summary>Layer <paramref name="layer"/>'s shown damage, 0..1, for an overall loss of <paramref name="loss"/> (0..1).</summary>
    public float Shown(int layer, float loss)
    {
        if ((uint)layer >= (uint)_taken.Length) return 0f;
        var most = 0f;
        foreach (var t in _taken) most = Math.Max(most, t);
        return most > 0f ? Ease.Clamp01(loss) * _taken[layer] / most : 0f;
    }
}

/// <summary>When a <see cref="HudArcGauge"/> draws at full strength: for a while after it starts refilling, its tone gets worse (amber,
/// red) or <see cref="Flare"/> is called; always while critical; otherwise at its steady opacity. Without Unity, no allocation.</summary>
internal struct ArcFlare
{
    private float _until;
    private MeterTone _tone;
    private bool _refilling;

    public void Flare(float now, float seconds) => _until = now + Math.Max(0f, seconds);

    /// <summary>The opacity factor this frame.</summary>
    public float Alpha(MeterTone tone, bool refilling, float now, float steady, float seconds)
    {
        if (tone > _tone || refilling && !_refilling) Flare(now, seconds);
        _tone = tone;
        _refilling = refilling;
        return tone == MeterTone.Critical || now < _until ? 1f : Ease.Clamp01(steady);
    }
}

/// <summary>The contacts of a <see cref="HudBearingArc"/>: fixed arrays, filled between <see cref="Begin"/> calls, no allocation.</summary>
internal sealed class BearingContacts
{
    private readonly float[] _azimuth, _strength;
    private readonly EnemyAlert[] _state;

    public BearingContacts(int capacity)
    {
        capacity = Math.Max(1, capacity);
        _azimuth = new float[capacity];
        _strength = new float[capacity];
        _state = new EnemyAlert[capacity];
    }

    public int Capacity => _azimuth.Length;

    public int Count { get; private set; }

    public void Begin() => Count = 0;

    /// <summary>Adds one; false when full or the azimuth is not a number.</summary>
    public bool Add(float azimuth, float strength, EnemyAlert state)
    {
        if (Count >= _azimuth.Length || !float.IsFinite(azimuth)) return false;
        _azimuth[Count] = Bearing.Wrap(azimuth);
        _strength[Count] = Ease.Clamp01(strength);
        _state[Count] = state;
        Count++;
        return true;
    }

    public float Azimuth(int i) => _azimuth[i];

    public float Strength(int i) => _strength[i];

    public EnemyAlert State(int i) => _state[i];

    /// <summary>How many contacts in <paramref name="state"/> are within <paramref name="halfArc"/> degrees of straight behind.</summary>
    public int Behind(EnemyAlert state, float halfArc)
    {
        var n = 0;
        for (var i = 0; i < Count; i++)
            if (_state[i] == state && Bearing.Between(_azimuth[i], 180f) <= halfArc) n++;
        return n;
    }
}

/// <summary>
/// A prompt that fades once the player knows it: shown at full strength, it fades to <see cref="FadedAlpha"/> after
/// <see cref="IdleSeconds"/> without use or <see cref="UsedDelay"/> after the first use; <see cref="Attention"/> brings it back for
/// <see cref="AttentionSeconds"/>, holding it keeps it up, and showing it again restarts the clock. No allocation.
/// </summary>
internal sealed class FamiliarFade
{
    public const float UsedDelay = 1.5f, AttentionSeconds = 4f, Rate = 4f;

    private float _shownAt = float.NaN, _usedAt = float.NaN, _attentionUntil = float.MinValue, _alpha = 1f;

    public FamiliarFade(float idleSeconds, float fadedAlpha)
    {
        IdleSeconds = Math.Max(0f, idleSeconds);
        FadedAlpha = Ease.Clamp01(fadedAlpha);
    }

    public float IdleSeconds { get; }

    public float FadedAlpha { get; }

    /// <summary>The prompt was hidden: the next <see cref="Update"/> starts over.</summary>
    public void Hidden() => _shownAt = float.NaN;

    public void Used(float now) => _usedAt = now;

    public void Attention(float now) => _attentionUntil = now + AttentionSeconds;

    /// <summary>The alpha to draw with now (eased toward its target).</summary>
    public float Update(float now, float dt, bool holding)
    {
        if (float.IsNaN(_shownAt))
        {
            _shownAt = now;
            _usedAt = float.NaN;
            _alpha = 1f;
        }
        var familiar = now - _shownAt >= IdleSeconds || (!float.IsNaN(_usedAt) && now - _usedAt >= UsedDelay);
        var target = holding || now < _attentionUntil || !familiar ? 1f : FadedAlpha;
        _alpha = Ease.Damp(_alpha, target, Rate, dt);
        return _alpha;
    }
}
