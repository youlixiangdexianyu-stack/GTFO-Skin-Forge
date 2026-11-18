using System;

namespace ForgeUI;

/// <summary>One point of a <see cref="WaypointBook"/> (and of a <c>HudWaypoints</c>): a slot and the generation it was made in, so an old id
/// never reaches a newer point that took its slot. <see cref="None"/> is no point.</summary>
public readonly struct WaypointId : IEquatable<WaypointId>
{
    internal WaypointId(int slot, int generation)
    {
        Slot = slot;
        Generation = generation;
    }

    internal int Slot { get; }
    internal int Generation { get; }

    /// <summary>No point (what an empty or full registration hands back).</summary>
    public static WaypointId None => default;

    public bool IsNone => Generation == 0;

    public bool Equals(WaypointId other) => Slot == other.Slot && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is WaypointId other && Equals(other);
    public override int GetHashCode() => (Slot * 397) ^ Generation;
}

/// <summary>
/// Which slots of a fixed set of world points are in use, without Unity. <see cref="Add"/> takes a free slot, or, when every slot is in use,
/// the one added longest ago (it is dropped); <see cref="Remove"/> frees one. Ids carry a generation, so
/// an id kept after its point was dropped or removed finds nothing. Every array is made once: adding, removing and reading allocate nothing.
/// </summary>
internal sealed class WaypointBook
{
    private readonly int[] _generation;
    private readonly long[] _order;
    private readonly bool[] _used;
    private long _next;
    private int _generations;

    public WaypointBook(int capacity)
    {
        capacity = Math.Max(1, capacity);
        _generation = new int[capacity];
        _order = new long[capacity];
        _used = new bool[capacity];
    }

    public int Capacity => _used.Length;

    /// <summary>Points in use.</summary>
    public int Count { get; private set; }

    /// <summary>A new point: a free slot, else the slot of the point added longest ago, which is dropped (<paramref name="dropped"/>).</summary>
    public WaypointId Add(out WaypointId dropped)
    {
        dropped = WaypointId.None;
        var slot = -1;
        for (var i = 0; i < _used.Length && slot < 0; i++) if (!_used[i]) slot = i;
        if (slot < 0)
        {
            slot = 0;
            for (var i = 1; i < _used.Length; i++) if (_order[i] < _order[slot]) slot = i;
            dropped = new WaypointId(slot, _generation[slot]);
            Count--;
        }
        _generations = _generations == int.MaxValue ? 1 : _generations + 1;
        _generation[slot] = _generations;
        _order[slot] = ++_next;
        _used[slot] = true;
        Count++;
        return new WaypointId(slot, _generations);
    }

    /// <summary>The slot of a point still in use; −1 for a point dropped, removed or never made.</summary>
    public int SlotOf(WaypointId id) =>
        !id.IsNone && (uint)id.Slot < (uint)_used.Length && _used[id.Slot] && _generation[id.Slot] == id.Generation ? id.Slot : -1;

    public bool Contains(WaypointId id) => SlotOf(id) >= 0;

    /// <summary>Whether <paramref name="slot"/> holds a point.</summary>
    public bool Used(int slot) => (uint)slot < (uint)_used.Length && _used[slot];

    /// <summary>Frees the point; false when it was already gone.</summary>
    public bool Remove(WaypointId id)
    {
        var slot = SlotOf(id);
        if (slot < 0) return false;
        _used[slot] = false;
        Count--;
        return true;
    }

    /// <summary>Frees every point.</summary>
    public void Clear()
    {
        Array.Clear(_used);
        Count = 0;
    }
}

/// <summary>Where a world point shows (<see cref="WaypointMath.Place"/>).</summary>
internal enum WaypointPlace : byte
{
    /// <summary>Not drawn: no camera, faded out by distance, or off screen without an edge arrow.</summary>
    Hidden,
    /// <summary>On the point.</summary>
    OnScreen,
    /// <summary>As an arrow on the screen's edge pointing the way.</summary>
    Edge
}

/// <summary>A world point's place on the HUD, without Unity: projected through the camera's clip transform (a point behind the camera
/// mirrored, as the camera's own viewport projection does), then kept on the point while it is on screen or moved onto the screen's edge
/// with an arrow (<see cref="ScreenMath.EdgeMarker"/>), and faded with distance.</summary>
internal static class WaypointMath
{
    /// <summary>The viewport point (0..1 from the bottom left) of world (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>),
    /// mirrored when it is behind the camera (<paramref name="behind"/>); false only for a point on the camera's own plane.</summary>
    public static bool Viewport(in ClipTransform clip, float x, float y, float z, out float viewportX, out float viewportY, out bool behind)
    {
        behind = false;
        if (clip.ToViewport(x, y, z, out viewportX, out viewportY)) return true;
        if (!clip.ToViewportBehind(x, y, z, out viewportX, out viewportY)) return false;
        behind = true;
        return true;
    }

    /// <summary>Where a point shows: HUD units from the middle of a screen of half size <paramref name="halfWidth"/> x
    /// <paramref name="halfHeight"/>, kept <paramref name="inset"/> inside the edge. <paramref name="distance"/> (metres, negative when unknown)
    /// fades it between <paramref name="fadeStart"/> and <paramref name="fadeEnd"/> (0 for <paramref name="fadeEnd"/>: no fading);
    /// <paramref name="edgeArrow"/> false hides it while off screen. <paramref name="angle"/> is the edge arrow's direction (0 right, 90 up).</summary>
    public static WaypointPlace Place(in ClipTransform clip, float x, float y, float z, float halfWidth, float halfHeight, float inset, float distance,
        float fadeStart, float fadeEnd, bool edgeArrow, out float hudX, out float hudY, out float angle, out float alpha)
    {
        hudX = hudY = angle = 0f;
        alpha = 0f;
        if (halfWidth <= 0f || halfHeight <= 0f || !Viewport(clip, x, y, z, out var vx, out var vy, out var behind)) return WaypointPlace.Hidden;
        alpha = distance >= 0f && fadeEnd > 0f ? GaugeMath.DistanceFade(distance, fadeStart, fadeEnd) : 1f;
        if (alpha <= 0.003f) return WaypointPlace.Hidden;
        var (px, py) = ScreenMath.ViewportToHud(vx, vy, halfWidth, halfHeight);
        var onScreen = ScreenMath.EdgeMarker(px, py, behind, halfWidth, halfHeight, inset, out hudX, out hudY, out angle);
        if (onScreen) return WaypointPlace.OnScreen;
        return edgeArrow ? WaypointPlace.Edge : WaypointPlace.Hidden;
    }
}
