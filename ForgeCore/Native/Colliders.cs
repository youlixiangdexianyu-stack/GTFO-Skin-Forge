using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>A collider-level sphere query with storage it owns and reuses, so a query allocates nothing. A caller keeps one per
/// call site and so owns its results until its own next <see cref="Sphere"/>. The game cannot say whether a completely filled buffer
/// held exactly that many colliders or more, so a full buffer counts as <see cref="Truncated"/>: the colliders are the first
/// <see cref="Capacity"/> found, never silently the complete set. The caller decides what that means (refuse, or query again with a
/// larger capacity), or constructs the query with <c>grow</c> so a full buffer is replaced by a larger one and the query repeated until it fits.
/// For the living enemies near a point use <see cref="Enemies.Near"/>. Main thread only.</summary>
public sealed class ColliderQuery
{
    private Il2CppReferenceArray<Collider> _hits;
    private readonly bool _grow;
    private int _count;

    /// <param name="capacity">How many colliders one query holds (the starting size when <paramref name="grow"/>).</param>
    /// <param name="grow">True: a full buffer is replaced by one twice as large and the query repeated until the result is below the
    /// capacity, so the result is always complete and <see cref="Truncated"/> is never true; the capacity stays at the larger size and
    /// the repeat allocates only when it grows. False (the default): a full buffer is reported as <see cref="Truncated"/>.</param>
    public ColliderQuery(int capacity, bool grow = false)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A query holds at least one collider.");
        _hits = new Il2CppReferenceArray<Collider>(capacity);
        _grow = grow;
    }

    public int Capacity => _hits.Length;

    /// <summary>How many colliders the last <see cref="Sphere"/> found.</summary>
    public int Count => _count;

    /// <summary>True when the last <see cref="Sphere"/> filled the buffer, so colliders in range may be missing from the result.</summary>
    public bool Truncated => _count >= _hits.Length;

    /// <summary>The <paramref name="index"/>th collider of the last query; a collider the game destroyed since compares equal to null, as everywhere in Unity.</summary>
    public Collider this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _hits[index];
        }
    }

    /// <summary>Finds the colliders on <paramref name="mask"/> layers within <paramref name="radius"/> of <paramref name="position"/>
    /// (a collider counts once however many of its parts are inside) and returns how many; check <see cref="Truncated"/> before
    /// trusting the result to be complete.</summary>
    public int Sphere(Vector3 position, float radius, int mask, QueryTriggerInteraction triggers)
    {
        var count = Physics.OverlapSphereNonAlloc(position, radius, _hits, mask, triggers);
        while (_grow && count >= _hits.Length)
        {
            _hits = new Il2CppReferenceArray<Collider>(checked(_hits.Length * 2));
            _count = 0;
            count = Physics.OverlapSphereNonAlloc(position, radius, _hits, mask, triggers);
        }
        // Drop what the previous, longer query left behind so the buffer does not keep dead colliders alive.
        for (var i = count; i < _count; i++) _hits[i] = null!;
        _count = count;
        return count;
    }

    /// <summary>Releases every collider the last query held (leaving a level), so the buffer keeps nothing alive; <see cref="Count"/> is 0 afterwards.</summary>
    public void Clear()
    {
        for (var i = 0; i < _count; i++) _hits[i] = null!;
        _count = 0;
    }
}
