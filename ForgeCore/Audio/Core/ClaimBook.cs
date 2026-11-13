using System;
using System.Collections.Generic;

namespace ForgeAudio;

/// <summary>Which claim answers a game sound (a player body sound or a weapon action), looked up by the native pointer of the
/// object the game raises it on (a body transform, a weapon). A claim lists its pointers once, when it is made; a lookup is one
/// dictionary probe on a pointer and a walk over the few claims on it, with no allocation and no string. Claims on the same pointer
/// are kept highest priority first; at equal priority the newer claim comes first. Kinds are the flag bits of the sound enum.</summary>
internal sealed class ClaimBook<T> where T : class
{
    internal sealed class Entry
    {
        internal readonly T Owner;
        internal readonly int Kinds;
        internal readonly int Priority;
        internal readonly long Order;
        internal IntPtr[] Pointers = Array.Empty<IntPtr>();
        internal bool Live;
        internal Entry(T owner, int kinds, int priority, long order) { Owner = owner; Kinds = kinds; Priority = priority; Order = order; }
    }

    private readonly Dictionary<IntPtr, List<Entry>> _byPointer = new();
    private readonly List<Entry> _live = new();
    private long _order;

    /// <summary>Live claims.</summary>
    internal int Count => _live.Count;

    internal Entry Add(T owner, IntPtr[] pointers, int kinds, int priority)
    {
        var entry = new Entry(owner, kinds, priority, ++_order) { Live = true };
        _live.Add(entry);
        Index(entry, pointers);
        return entry;
    }

    /// <summary>Replaces a live claim's transforms (its body's hierarchy changed).</summary>
    internal void Reset(Entry entry, IntPtr[] pointers)
    {
        if (!entry.Live) return;
        Unindex(entry);
        Index(entry, pointers);
    }

    /// <summary>False when the claim had already ended.</summary>
    internal bool Remove(Entry entry)
    {
        if (!entry.Live) return false;
        entry.Live = false;
        Unindex(entry);
        _live.Remove(entry);
        return true;
    }

    /// <summary>Ends every claim (level exit, kit shutdown).</summary>
    internal void Clear()
    {
        foreach (var entry in _live)
        {
            entry.Live = false;
            entry.Pointers = Array.Empty<IntPtr>();
        }
        _live.Clear();
        _byPointer.Clear();
    }

    /// <summary>The claim that answers a <paramref name="kind"/> sound rooted on <paramref name="pointer"/>, or null.</summary>
    internal T? Find(IntPtr pointer, int kind)
    {
        if (_live.Count == 0 || pointer == IntPtr.Zero || !_byPointer.TryGetValue(pointer, out var claims)) return null;
        for (var i = 0; i < claims.Count; i++)
            if ((claims[i].Kinds & kind) != 0) return claims[i].Owner;
        return null;
    }

    private void Index(Entry entry, IntPtr[] pointers)
    {
        entry.Pointers = pointers;
        foreach (var pointer in pointers)
        {
            if (pointer == IntPtr.Zero) continue;
            if (!_byPointer.TryGetValue(pointer, out var claims)) _byPointer[pointer] = claims = new List<Entry>(1);
            if (claims.Contains(entry)) continue;
            var at = 0;
            while (at < claims.Count && (claims[at].Priority > entry.Priority || claims[at].Priority == entry.Priority && claims[at].Order > entry.Order)) at++;
            claims.Insert(at, entry);
        }
    }

    private void Unindex(Entry entry)
    {
        foreach (var pointer in entry.Pointers)
        {
            if (!_byPointer.TryGetValue(pointer, out var claims)) continue;
            claims.Remove(entry);
            if (claims.Count == 0) _byPointer.Remove(pointer);
        }
        entry.Pointers = Array.Empty<IntPtr>();
    }
}
