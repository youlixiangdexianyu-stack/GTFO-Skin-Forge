using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>
/// The speed bookkeeping behind <c>Enemies.Slow</c>: for each key, the speed it had before any slowing, the speed that was applied, and
/// the live leases. Several leases on one key never compound: the slowest factor wins. When the key's current speed is no longer the
/// one applied, the game changed it meanwhile and that value becomes the new original; when the last lease ends the original is put
/// back only if the applied speed is still in place, otherwise the game's own value stays. Pure arithmetic, no game types.
/// </summary>
internal sealed class SlowBook<TKey> where TKey : notnull
{
    /// <summary>Speeds closer than this are the same value read back.</summary>
    public const float Tolerance = 1e-3f;

    private sealed class Entry
    {
        internal float Original, Applied;
        internal readonly List<(int Id, float Factor)> Leases = new();
    }

    private readonly Dictionary<TKey, Entry> _entries = new();
    private int _nextId;

    /// <summary>How many keys are currently slowed.</summary>
    public int Count => _entries.Count;

    /// <summary>True while lease <paramref name="id"/> of <paramref name="key"/> is held.</summary>
    public bool Has(TKey key, int id)
    {
        if (!_entries.TryGetValue(key, out var entry)) return false;
        foreach (var lease in entry.Leases) if (lease.Id == id) return true;
        return false;
    }

    /// <summary>Takes a lease slowing <paramref name="key"/> to <paramref name="factor"/> (clamped to 0..1) of its own speed, whose
    /// speed reads <paramref name="current"/> now. Returns the speed to set; <paramref name="id"/> is the lease (always above 0).</summary>
    public float Add(TKey key, float current, float factor, out int id)
    {
        if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry { Original = current, Applied = current };
        else Sync(entry, current);
        id = ++_nextId;
        entry.Leases.Add((id, Math.Clamp(factor, 0f, 1f)));
        return entry.Applied = entry.Original * Slowest(entry);
    }

    /// <summary>The speed to set so lease <paramref name="id"/> still holds after the game may have changed the speed; false when the
    /// lease is not held.</summary>
    public bool Refresh(TKey key, int id, float current, out float speed)
    {
        speed = current;
        if (!Has(key, id)) return false;
        var entry = _entries[key];
        Sync(entry, current);
        speed = entry.Applied = entry.Original * Slowest(entry);
        return true;
    }

    /// <summary>Ends lease <paramref name="id"/>. <paramref name="speed"/> is what to set: the slowest remaining lease's speed, or the
    /// original when none remain; false (nothing to set) when the lease was not held or the game has set a speed of its own since.</summary>
    public bool Remove(TKey key, int id, float current, out float speed)
    {
        speed = current;
        if (!Has(key, id)) return false;
        var entry = _entries[key];
        Sync(entry, current);
        entry.Leases.RemoveAll(l => l.Id == id);
        if (entry.Leases.Count > 0)
        {
            speed = entry.Applied = entry.Original * Slowest(entry);
            return true;
        }
        _entries.Remove(key);
        speed = entry.Original;
        return Math.Abs(current - entry.Applied) <= Tolerance && Math.Abs(current - entry.Original) > Tolerance;
    }

    /// <summary>Ends every lease of <paramref name="key"/> (death, reuse). Same result as <see cref="Remove"/> for the last lease.</summary>
    public bool RemoveAll(TKey key, float current, out float speed)
    {
        speed = current;
        if (!_entries.Remove(key, out var entry)) return false;
        speed = entry.Original;
        return Math.Abs(current - entry.Applied) <= Tolerance && Math.Abs(current - entry.Original) > Tolerance;
    }

    /// <summary>Forgets everything without restoring (level left, the enemies are gone).</summary>
    public void Clear() => _entries.Clear();

    private static void Sync(Entry entry, float current)
    {
        if (Math.Abs(current - entry.Applied) > Tolerance) entry.Original = current;
    }

    private static float Slowest(Entry entry)
    {
        var factor = 1f;
        foreach (var lease in entry.Leases) factor = Math.Min(factor, lease.Factor);
        return factor;
    }
}
