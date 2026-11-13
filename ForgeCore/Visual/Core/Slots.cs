using System;

namespace ForgeVisual;

/// <summary>Which slot of a fixed pool a new item takes. A free slot first; when every slot is busy, the live item of the lowest
/// priority below the request's (the oldest among equals) is taken over; otherwise the request is dropped. Pinned slots (an item a
/// caller still moves every frame) are never taken over. Plain arrays, no allocation.</summary>
internal sealed class SlotBook
{
    private readonly bool[] _live, _pinned;
    private readonly int[] _priority, _generation;
    private readonly long[] _started;
    private long _sequence;
    private int _scan;

    internal SlotBook(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A pool holds at least one item.");
        _live = new bool[capacity];
        _pinned = new bool[capacity];
        _priority = new int[capacity];
        _generation = new int[capacity];
        _started = new long[capacity];
    }

    internal int Capacity => _live.Length;
    internal int LiveCount { get; private set; }

    internal bool Live(int slot) => _live[slot];
    internal int Generation(int slot) => _generation[slot];

    /// <summary>The slot for a new item of <paramref name="priority"/>, or -1. A taken-over
    /// slot is reported through <paramref name="replaced"/> so the caller can end what it was showing.</summary>
    internal int Take(int priority, bool pinned, out bool replaced)
    {
        replaced = false;
        var n = _live.Length;
        for (var i = 0; i < n; i++)
        {
            var slot = (_scan + i) % n;
            if (_live[slot]) continue;
            _scan = (slot + 1) % n;
            Start(slot, priority, pinned);
            LiveCount++;
            return slot;
        }
        var victim = -1;
        for (var slot = 0; slot < n; slot++)
        {
            if (_pinned[slot] || _priority[slot] >= priority) continue;
            if (victim < 0 || _priority[slot] < _priority[victim] || (_priority[slot] == _priority[victim] && _started[slot] < _started[victim])) victim = slot;
        }
        if (victim < 0) return -1;
        replaced = true;
        Start(victim, priority, pinned);
        return victim;
    }

    /// <summary>The item in <paramref name="slot"/> ended; a handle of its generation no longer reaches the slot.</summary>
    internal void Free(int slot)
    {
        if (!_live[slot]) return;
        _live[slot] = false;
        _pinned[slot] = false;
        _generation[slot]++;
        LiveCount--;
    }

    /// <summary>Everything ends (level left).</summary>
    internal void Clear()
    {
        for (var i = 0; i < _live.Length; i++) Free(i);
    }

    private void Start(int slot, int priority, bool pinned)
    {
        _live[slot] = true;
        _pinned[slot] = pinned;
        _priority[slot] = priority;
        _generation[slot]++;
        _started[slot] = ++_sequence;
    }
}

/// <summary>The slots of a fixed set of long-lived marks (decals): a new mark takes a free slot, or, when every slot holds a live
/// mark, the oldest one. It never grows and never starves anything else. A slot's generation changes whenever its mark starts or
/// ends, so a handle to a replaced mark goes stale. Plain arrays, no allocation.</summary>
internal sealed class DecalSlots
{
    private readonly bool[] _live;
    private readonly int[] _generation;
    private readonly long[] _started;
    private long _sequence;

    internal DecalSlots(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A set of marks holds at least one.");
        _live = new bool[capacity];
        _generation = new int[capacity];
        _started = new long[capacity];
    }

    internal int Capacity => _live.Length;
    internal int LiveCount { get; private set; }

    internal bool Live(int slot) => _live[slot];
    internal int Generation(int slot) => _generation[slot];

    /// <summary>The slot for a new mark: a free one, else the oldest live one.</summary>
    internal int Take()
    {
        var slot = -1;
        for (var i = 0; i < _live.Length; i++)
        {
            if (!_live[i]) { slot = i; break; }
            if (slot < 0 || _started[i] < _started[slot]) slot = i;
        }
        if (!_live[slot]) LiveCount++;
        _live[slot] = true;
        _generation[slot]++;
        _started[slot] = ++_sequence;
        return slot;
    }

    internal bool Current(int slot, int generation) => slot >= 0 && slot < _live.Length && _live[slot] && _generation[slot] == generation;

    /// <summary>The mark in <paramref name="slot"/> ended (faded out, removed).</summary>
    internal void End(int slot)
    {
        if (!_live[slot]) return;
        _live[slot] = false;
        _generation[slot]++;
        LiveCount--;
    }

    /// <summary>Every mark ends (level left); the count of recycled marks stays.</summary>
    internal void Clear()
    {
        for (var i = 0; i < _live.Length; i++) End(i);
    }
}

/// <summary>The numbers of a decal's life: it shows fully, then fades out over its last seconds.</summary>
internal static class DecalMath
{
    /// <summary>The fraction of <paramref name="life"/> before the fade of <paramref name="fade"/> seconds starts (0 to 1).</summary>
    internal static float Hold(float life, float fade)
    {
        if (!(life > 0f)) return 0f;
        if (!(fade > 0f)) return 1f;
        var hold = 1f - fade / life;
        return hold < 0f ? 0f : hold;
    }
}

/// <summary>Which of a fixed set of effect lights a new one takes, and how a blinking light switches.</summary>
internal static class LightSlots
{
    /// <summary>A free slot (<paramref name="used"/> false) first; when every slot is in use, the flash closest to its end
    /// (<paramref name="ends"/>); a held light (<paramref name="held"/>) is never taken. -1 when every light is held.</summary>
    internal static int Pick(bool[] used, bool[] held, float[] ends, float now)
    {
        for (var i = 0; i < used.Length; i++) if (!used[i]) return i;
        var slot = -1;
        var best = float.MaxValue;
        for (var i = 0; i < used.Length; i++)
        {
            if (held[i]) continue;
            var left = ends[i] - now;
            if (left < best) { best = left; slot = i; }
        }
        return slot;
    }

    /// <summary>A warning light's switch: on for the first half of every 1 / <paramref name="rate"/> seconds of
    /// <paramref name="age"/>, off for the second; always on for a rate that is not positive.</summary>
    internal static float Blink(float age, float rate)
    {
        if (!(rate > 0f)) return 1f;
        var phase = age * rate;
        return phase - (float)Math.Floor(phase) < 0.5f ? 1f : 0f;
    }
}
