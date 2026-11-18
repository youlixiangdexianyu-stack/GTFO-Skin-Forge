using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>A swept-shape query (a ray, or a sphere when <c>radius</c> is above 0) that fills storage it owns and reuses, so a sweep
/// allocates nothing in the usual case. A caller keeps one per call site and so owns its hits until its own next
/// <see cref="Cast"/>. It does only the physics question: the hits come back complete, nearest first, with the contact point of a hit
/// the sphere already overlapped at the start filled in. What a hit means (flying on, piercing, bouncing, settling) is the caller's.
/// <para>The game cannot say whether a completely filled buffer held exactly that many hits or more, so a full buffer is replaced by
/// one twice as large and the sweep repeated until it fits; the capacity stays at the larger size and only that repeat allocates, as
/// <see cref="ColliderQuery"/> with <c>grow</c> does. Main thread only.</para></summary>
public sealed class SweepQuery
{
    private Il2CppStructArray<RaycastHit> _hits;
    private int _count;

    /// <param name="capacity">How many hits one sweep holds before the buffer grows.</param>
    public SweepQuery(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A sweep holds at least one hit.");
        _hits = new Il2CppStructArray<RaycastHit>(capacity);
    }

    /// <summary>How many hits the last <see cref="Cast"/> found.</summary>
    public int Count => _count;

    /// <summary>The <paramref name="rank"/>th nearest hit of the last <see cref="Cast"/> (0 is the nearest); a collider the game
    /// destroyed since compares equal to null, as everywhere in Unity.</summary>
    public RaycastHit this[int rank]
    {
        get
        {
            if ((uint)rank >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(rank));
            return _hits[rank];
        }
    }

    /// <summary>Sweeps from <paramref name="start"/> along <paramref name="direction"/> for <paramref name="distance"/> metres against
    /// the <paramref name="mask"/> layers (<paramref name="triggers"/>: the project's setting unless given) and returns how many hits it holds: a ray when
    /// <paramref name="radius"/> is 0 or less, else a sphere of that radius. A hit with distance 0 (the sphere began inside the
    /// collider, where the game leaves the point empty) gets the collider's closest point to <paramref name="start"/> as its point (<paramref name="start"/> itself for a concave mesh).</summary>
    public int Cast(Vector3 start, float radius, Vector3 direction, float distance, int mask, QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        var count = Run(start, radius, direction, distance, mask, triggers);
        while (count >= _hits.Length)
        {
            _hits = new Il2CppStructArray<RaycastHit>(checked(_hits.Length * 2));
            count = Run(start, radius, direction, distance, mask, triggers);
        }
        for (var i = 0; i < count; i++)
        {
            var hit = _hits[i];
            if (radius > 0f && hit.distance <= 0f && hit.collider != null)
            {
                hit.point = ClosestTo(hit.collider, start);
                _hits[i] = hit;
            }
        }
        // Nearest first (the game returns them unordered); insertion sort, because a sweep holds a handful.
        for (var i = 1; i < count; i++)
        {
            var hit = _hits[i];
            var at = i;
            while (at > 0 && _hits[at - 1].distance > hit.distance)
            {
                _hits[at] = _hits[at - 1];
                at--;
            }
            _hits[at] = hit;
        }
        _count = count;
        return count;
    }

    // ClosestPoint only answers for primitives and convex meshes and logs an error for a concave mesh, which most of a level is: the
    // sphere's start is the answer there.
    private static Vector3 ClosestTo(Collider collider, Vector3 start)
    {
        var mesh = collider.TryCast<MeshCollider>();
        return mesh != null && !mesh.convex ? start : collider.ClosestPoint(start);
    }

    private int Run(Vector3 start, float radius, Vector3 direction, float distance, int mask, QueryTriggerInteraction triggers)
        => radius > 0f
            ? Physics.SphereCastNonAlloc(start, radius, direction, _hits, distance, mask, triggers)
            : Physics.RaycastNonAlloc(start, direction, _hits, distance, mask, triggers);
}
