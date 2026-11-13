using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The stacked camera leases behind <c>CameraLease</c>. Only the newest lease owns the view: what it asks for (the eye
/// lift, the near clip plane) is what the camera shows, and an older lease's request comes back when the newer one is released.
/// With no lease the game's own camera values stand. Main thread only; reading allocates nothing.</summary>
internal sealed class CameraLeaseBook
{
    internal sealed class Entry
    {
        internal readonly string Owner;
        internal float Lift;
        internal float NearClip = float.NaN;
        internal Entry(string owner) => Owner = owner;
    }

    private readonly List<Entry> _held = new();

    internal int Count => _held.Count;

    /// <summary>The lease that owns the view, null when none is held.</summary>
    internal Entry? Owner => _held.Count == 0 ? null : _held[^1];

    internal Entry Take(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(owner));
        var entry = new Entry(owner);
        _held.Add(entry);
        return entry;
    }

    internal bool Holds(Entry entry) => _held.Contains(entry);

    /// <summary>Whether <paramref name="entry"/> is the lease that owns the view now.</summary>
    internal bool Owns(Entry entry) => _held.Count != 0 && ReferenceEquals(_held[^1], entry);

    internal bool Release(Entry entry) => _held.Remove(entry);

    internal int ReleaseAll()
    {
        var count = _held.Count;
        _held.Clear();
        return count;
    }

    /// <summary>The eye lift (metres) the view shows: the owner's, 0 when none is held.</summary>
    internal float Lift => Owner?.Lift ?? 0f;

    /// <summary>The near clip plane the owner asks for, NaN when it asks for none (the game's own stands).</summary>
    internal float NearClip => Owner?.NearClip ?? float.NaN;
}
