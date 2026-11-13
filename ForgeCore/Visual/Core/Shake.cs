using System;

namespace ForgeVisual;

/// <summary>The arithmetic of a camera shake: the player's strength setting and the fall-off with distance.</summary>
internal static class ShakeMath
{
    /// <summary>Clamps the player's setting to 0 to 1; a value that is not a number counts as full.</summary>
    internal static float Setting(float configured) => float.IsNaN(configured) ? 1f : configured < 0f ? 0f : configured > 1f ? 1f : configured;

    /// <summary>1 within <paramref name="inner"/> metres, falling linearly to 0 at <paramref name="range"/>; 0 beyond it, for a range
    /// that is not positive or a distance that is not a number.</summary>
    internal static float Falloff(float distance, float inner, float range)
    {
        if (!(range > 0f) || !(distance < range)) return 0f;
        if (distance <= inner) return 1f;
        return 1f - (distance - inner) / (range - inner);
    }
}

/// <summary>
/// The shakes this library started on the local camera and that are still running, so a new one only tops the total up to a cap
/// instead of piling onto the others: ten blasts at once shake no harder than the cap. Each shake's share fades linearly over its
/// time. A fixed number of entries; when all run, a new shake takes the one with the least share left. Plain arrays, no allocation.
/// </summary>
internal sealed class ShakeStack
{
    private readonly float[] _start, _seconds, _amplitude;

    internal ShakeStack(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A stack holds at least one shake.");
        _start = new float[capacity];
        _seconds = new float[capacity];
        _amplitude = new float[capacity];
    }

    /// <summary>The amplitude the running shakes still add up to at <paramref name="now"/>.</summary>
    internal float Live(float now)
    {
        var total = 0f;
        for (var i = 0; i < _amplitude.Length; i++) total += Share(i, now);
        return total;
    }

    /// <summary>How much of a new shake of <paramref name="amplitude"/> fits under <paramref name="cap"/> now (0 when none does);
    /// what fits is recorded as running for <paramref name="seconds"/>.</summary>
    internal float Admit(float now, float amplitude, float seconds, float cap)
    {
        if (!(amplitude > 0f) || !(seconds > 0f) || !(cap > 0f)) return 0f;
        var room = cap - Live(now);
        if (room <= 0f) return 0f;
        var admitted = Math.Min(amplitude, room);
        var slot = 0;
        var least = float.MaxValue;
        for (var i = 0; i < _amplitude.Length; i++)
        {
            var share = Share(i, now);
            if (share < least) { least = share; slot = i; }
        }
        _start[slot] = now;
        _seconds[slot] = seconds;
        _amplitude[slot] = admitted;
        return admitted;
    }

    /// <summary>Forgets every running shake (level end).</summary>
    internal void Clear() => Array.Clear(_amplitude, 0, _amplitude.Length);

    private float Share(int i, float now)
    {
        if (_amplitude[i] <= 0f) return 0f;
        var age = now - _start[i];
        if (age < 0f) age = 0f;
        return age >= _seconds[i] ? 0f : _amplitude[i] * (1f - age / _seconds[i]);
    }
}
