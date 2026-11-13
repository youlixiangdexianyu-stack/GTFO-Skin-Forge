using System;

namespace ForgeUI;

/// <summary>The channels of the screen effect stack. Each channel shows one merged value, drawn in one full-screen pass.</summary>
public enum ScreenChannel : byte
{
    /// <summary>A colour over the whole screen (a hit, a flashbang).</summary>
    Flash,
    /// <summary>Darkened corners in a colour, clear inside 55 % of the way to the edge (a visor shade, tunnel vision).</summary>
    Vignette,
    /// <summary>A coloured edge glow reaching further in than the vignette, pulsing at the effect's rate (low health).</summary>
    LowHealth,
    /// <summary>The picture blurred, up to <see cref="ScreenEffectBook.MaxBlur1080"/> pixels at full strength.</summary>
    Blur,
    /// <summary>The picture split into red and blue toward the corners, up to <see cref="ScreenEffectBook.MaxSplit1080"/> pixels.</summary>
    Chromatic,
}

/// <summary>
/// The screen effects every mod pushed, without Unity: a fixed number of slots, each a channel, a strength, a colour, a priority and an
/// envelope (fade in, hold for its seconds or until withdrawn, fade out). Each frame <see cref="Evaluate"/> merges each channel:
/// <list type="bullet">
/// <item>only the effects of the highest priority present in the channel count;</item>
/// <item>the channel's strength is the strongest of them (the same effect from two mods does not add up to more than either);</item>
/// <item>its colour is their colours averaged by strength (two flashes of different colours blend, the stronger one dominates);</item>
/// <item>its pulse rate (low health) is the strongest one's.</item>
/// </list>
/// A slot's generation changes on every reuse, so an old handle never touches a newer effect. Nothing allocates after construction.
/// </summary>
internal sealed class ScreenEffectBook
{
    /// <summary>Effects held at once, all channels together.</summary>
    public const int Capacity = 64;
    public const int Channels = 5;
    /// <summary>Blur radius at full strength, in pixels at 1080p (scaled with the screen height).</summary>
    public const float MaxBlur1080 = 6f;
    /// <summary>Colour split at the corners at full strength, in pixels at 1080p (scaled with the screen height).</summary>
    public const float MaxSplit1080 = 8f;
    /// <summary>Below this a merged channel counts as off.</summary>
    public const float Off = 0.002f;

    private readonly bool[] _used = new bool[Capacity], _ended = new bool[Capacity];
    private readonly ScreenChannel[] _channel = new ScreenChannel[Capacity];
    private readonly int[] _priority = new int[Capacity], _generation = new int[Capacity];
    private readonly float[] _strength = new float[Capacity], _r = new float[Capacity], _g = new float[Capacity], _b = new float[Capacity], _a = new float[Capacity];
    private readonly float[] _start = new float[Capacity], _hold = new float[Capacity], _fadeIn = new float[Capacity], _fadeOut = new float[Capacity];
    private readonly float[] _endAt = new float[Capacity], _endLevel = new float[Capacity], _rate = new float[Capacity];
    private readonly float[] _mergedStrength = new float[Channels], _mr = new float[Channels], _mg = new float[Channels], _mb = new float[Channels],
        _ma = new float[Channels], _mRate = new float[Channels];
    private readonly int[] _topPriority = new int[Channels];

    /// <summary>Effects held now.</summary>
    public int Count { get; private set; }

    /// <summary>Why these values cannot make an effect, or null.</summary>
    public static string? Problem(ScreenChannel channel, float strength, float r, float g, float b, float a, float seconds, float fadeIn, float fadeOut, float rate)
    {
        if ((int)channel >= Channels) return "unknown channel " + (int)channel;
        if (!float.IsFinite(strength) || strength < 0f) return "strength must be 0 or more";
        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a) || r < 0f || g < 0f || b < 0f || a < 0f || a > 1f)
            return "the colour must be finite, not negative, with alpha 0..1";
        if (!float.IsFinite(seconds) || seconds < 0f) return "seconds must be 0 (held until withdrawn) or more";
        if (!float.IsFinite(fadeIn) || fadeIn < 0f || !float.IsFinite(fadeOut) || fadeOut < 0f) return "fades must be 0 or more seconds";
        if (!float.IsFinite(rate) || rate < 0f) return "the pulse rate must be 0 or more per second";
        return null;
    }

    /// <summary>Starts an effect at <paramref name="now"/>; returns its slot, or -1 when every slot is taken. <paramref name="seconds"/>
    /// 0 holds it until <see cref="Withdraw"/>. Values are checked with <see cref="Problem"/> first.</summary>
    public int Push(ScreenChannel channel, float strength, float r, float g, float b, float a, float seconds, float fadeIn, float fadeOut,
        int priority, float rate, float now)
    {
        int slot = -1;
        for (int i = 0; i < Capacity; i++) if (!_used[i]) { slot = i; break; }
        if (slot < 0) return -1;
        _used[slot] = true;
        _ended[slot] = false;
        _channel[slot] = channel;
        _strength[slot] = Math.Min(strength, 1f);
        _r[slot] = r; _g[slot] = g; _b[slot] = b; _a[slot] = a;
        _start[slot] = now;
        _hold[slot] = seconds > 0f ? seconds : float.PositiveInfinity;
        _fadeIn[slot] = fadeIn;
        _fadeOut[slot] = fadeOut;
        _priority[slot] = priority;
        _rate[slot] = rate;
        Count++;
        return slot;
    }

    public int Generation(int slot) => (uint)slot < Capacity ? _generation[slot] : -1;

    /// <summary>True while the effect of this slot and generation is held or fading out.</summary>
    public bool Live(int slot, int generation) => (uint)slot < Capacity && _used[slot] && _generation[slot] == generation;

    /// <summary>Starts the effect's fade-out from where it is now (at once when its fade-out is 0). False when it already ended.</summary>
    public bool Withdraw(int slot, int generation, float now)
    {
        if (!Live(slot, generation)) return false;
        Settle(slot, now);
        if (_ended[slot]) return false;
        End(slot, now, Rise(slot, now));
        return true;
    }

    /// <summary>Ends the effect at once, without a fade (the level ended); its handles go stale. False when it already ended.</summary>
    public bool Drop(int slot, int generation)
    {
        if (!Live(slot, generation)) return false;
        Free(slot);
        return true;
    }

    /// <summary>Changes a held effect's strength and colour (for an effect driven every frame, such as a visor shade).</summary>
    public bool Set(int slot, int generation, float strength, float r, float g, float b, float a)
    {
        if (!Live(slot, generation) || _ended[slot]) return false;
        if (!float.IsFinite(strength) || strength < 0f) return false;
        _strength[slot] = Math.Min(strength, 1f);
        _r[slot] = r; _g[slot] = g; _b[slot] = b; _a[slot] = a;
        return true;
    }

    /// <summary>How strongly the effect in <paramref name="slot"/> shows at <paramref name="now"/>, 0..1.</summary>
    public float Level(int slot, float now)
    {
        if ((uint)slot >= Capacity || !_used[slot]) return 0f;
        Settle(slot, now);
        if (!_ended[slot]) return Rise(slot, now);
        var fade = _fadeOut[slot];
        var t = now - _endAt[slot];
        if (fade <= 0f) return t < 0f ? _endLevel[slot] : 0f;
        return _endLevel[slot] * (1f - Ease.Clamp01(t / fade));
    }

    /// <summary>Merges every channel at <paramref name="now"/> (rules in the class summary), frees the effects that have faded out and
    /// returns whether any channel shows something.</summary>
    public bool Evaluate(float now)
    {
        for (int c = 0; c < Channels; c++)
        {
            _mergedStrength[c] = _mr[c] = _mg[c] = _mb[c] = _ma[c] = _mRate[c] = 0f;
            _topPriority[c] = int.MinValue;
        }
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i]) continue;
            var level = Level(i, now);
            if (_ended[i] && level <= 0f && now - _endAt[i] >= _fadeOut[i]) { Free(i); continue; }
            if (level <= 0f) continue;
            var c = (int)_channel[i];
            if (_priority[i] > _topPriority[c]) _topPriority[c] = _priority[i];
        }
        // Sums by strength go in the colour fields first; the strongest sets strength and rate.
        Span<float> sum = stackalloc float[Channels];
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i]) continue;
            var c = (int)_channel[i];
            if (_priority[i] != _topPriority[c]) continue;
            var level = Level(i, now);
            if (level <= 0f) continue;
            _mr[c] += _r[i] * level; _mg[c] += _g[i] * level; _mb[c] += _b[i] * level; _ma[c] += _a[i] * level;
            sum[c] += level;
            if (level > _mergedStrength[c]) { _mergedStrength[c] = level; _mRate[c] = _rate[i]; }
        }
        bool any = false;
        for (int c = 0; c < Channels; c++)
        {
            if (sum[c] <= 0f) continue;
            _mr[c] /= sum[c]; _mg[c] /= sum[c]; _mb[c] /= sum[c]; _ma[c] /= sum[c];
            if (_mergedStrength[c] >= Off) any = true;
        }
        return any;
    }

    /// <summary>The merged strength of <paramref name="channel"/> after <see cref="Evaluate"/>, 0..1.</summary>
    public float Strength(ScreenChannel channel) => _mergedStrength[(int)channel];
    public float R(ScreenChannel channel) => _mr[(int)channel];
    public float G(ScreenChannel channel) => _mg[(int)channel];
    public float B(ScreenChannel channel) => _mb[(int)channel];
    public float A(ScreenChannel channel) => _ma[(int)channel];
    /// <summary>The pulse rate of the strongest effect in <paramref name="channel"/>, per second.</summary>
    public float Rate(ScreenChannel channel) => _mRate[(int)channel];

    /// <summary>The low-health pulse at <paramref name="now"/> for <paramref name="rate"/> pulses a second: between 0.6 and 1.</summary>
    public static float Pulse(float rate, float now) => rate <= 0f ? 1f : 0.6f + 0.4f * Ease.Pulse(now, rate);

    /// <summary>Ends every effect at once.</summary>
    public void Clear()
    {
        for (int i = 0; i < Capacity; i++) if (_used[i]) Free(i);
    }

    private float Rise(int slot, float now)
    {
        var fade = _fadeIn[slot];
        var t = now - _start[slot];
        return _strength[slot] * (fade <= 0f ? 1f : Ease.Clamp01(t / fade));
    }

    /// <summary>A timed effect whose seconds are over ends (its fade-out starts) at the moment its hold ended.</summary>
    private void Settle(int slot, float now)
    {
        if (_ended[slot]) return;
        var endAt = _start[slot] + _hold[slot];
        if (now >= endAt) End(slot, endAt, Rise(slot, endAt));
    }

    private void End(int slot, float at, float level)
    {
        _ended[slot] = true;
        _endAt[slot] = at;
        _endLevel[slot] = level;
    }

    private void Free(int slot)
    {
        _used[slot] = false;
        _generation[slot]++;
        Count--;
    }
}
