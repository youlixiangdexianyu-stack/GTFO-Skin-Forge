using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>The named screen regions every HUD element is placed in. Elements of all mods in a region stack by priority (highest
/// nearest the region's anchor) with a gap between them; nothing ever overlaps another element or the game's own HUD.</summary>
public enum HudRegion : byte
{
    /// <summary>Under the game's weapon list, right-aligned, stacking downwards.</summary>
    WeaponList,
    /// <summary>Above the game's health bar and vitals, centred on the bar, stacking upwards.</summary>
    Vitals,
    /// <summary>A little above the middle of the screen: one element at a time (the highest priority one that is shown).</summary>
    CenterBanner,
    /// <summary>The top edge of the screen, centred, stacking downwards.</summary>
    TopCenter,
    /// <summary>The top left corner under the game's own objective text, stacking downwards.</summary>
    Notices,
    /// <summary>The right edge in the middle of the screen, right-aligned, stacking downwards.</summary>
    RightColumn,
    /// <summary>The bottom left corner, stacking upwards.</summary>
    BottomLeft,
    /// <summary>The bottom right corner, right-aligned, stacking upwards.</summary>
    BottomRight,
    /// <summary>Free: the element's root is the middle of the screen and it follows places in the world (markers, hit ticks, damage
    /// numbers). It takes no room and is not laid out.</summary>
    World,
    /// <summary>The top right corner, right-aligned, stacking downwards.</summary>
    TopRight,
    /// <summary>The top left corner, stacking downwards (above <see cref="Notices"/>, which leaves room for the game's own objective text).</summary>
    TopLeft,
}

/// <summary>Where an element goes: its region, its priority (higher is placed nearer the region's anchor and keeps its place when
/// the region is full), the size it expects to draw, in HUD units (about pixels at 1080p), and its row. The element is laid out at
/// the larger of this size and what it actually draws.</summary>
public readonly struct HudSlot
{
    /// <summary>An element on the shared line <paramref name="row"/> of its region (see <see cref="Row"/>); row 0 is a line of its own.
    /// <paramref name="space"/> is extra room kept before it in its region's stack (see <see cref="Space"/>).</summary>
    public HudSlot(HudRegion region, int priority = 0, float width = 0f, float height = 0f, int row = 0, float space = 0f)
    {
        Region = region;
        Priority = priority;
        Width = Math.Max(0f, width);
        Height = Math.Max(0f, height);
        Row = row;
        Space = Math.Max(0f, space);
    }

    public HudRegion Region { get; }

    public int Priority { get; }

    public float Width { get; }

    public float Height { get; }

    /// <summary>0: the element takes a line of its region to itself. Any other number: the shown elements of the region with the same
    /// row share one line, side by side from the region's corner outwards (highest priority nearest the corner), lined up on the edge
    /// the region grows from; the line takes its place in the stack by its highest priority and is as tall as its tallest element.
    /// When the line does not fit, all of it collapses. Ignored in the single-slot and free regions.</summary>
    public int Row { get; }

    /// <summary>Room kept between the element and what it stacks on (the element or line before it in its region, the region's anchor,
    /// or whatever it is moved past), on top of <see cref="HudRegions.Gap"/>, in HUD units; 0 by default. A shared line keeps the largest
    /// space of its shown elements. Ignored in the single-slot and free regions.</summary>
    public float Space { get; }

    /// <summary>The same slot with another priority, size or both (the row and the space stay).</summary>
    public HudSlot With(int priority, float width, float height) => new(Region, priority, width, height, Row, Space);
}

/// <summary>A rectangle in the weapon list's space (HUD units, x right, y up).</summary>
public readonly struct HudBox
{
    public HudBox(float left, float bottom, float right, float top)
    {
        Left = left;
        Bottom = bottom;
        Right = right;
        Top = top;
    }

    public float Left { get; }

    public float Bottom { get; }

    public float Right { get; }

    public float Top { get; }

    public float Width => Right - Left;

    public float Height => Top - Bottom;

    /// <summary>True when the two share area; boxes that only touch do not overlap.</summary>
    public bool Overlaps(HudBox other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

    public override string ToString() => "(" + N(Left) + ", " + N(Bottom) + ")-(" + N(Right) + ", " + N(Top) + ")";

    internal static string N(float n) => n.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A region on the current screen: where its first element goes (<see cref="X"/>, <see cref="Y"/>), which way elements
/// grow (<see cref="FlowX"/>: -1 right-aligned at X growing left, 1 left-aligned growing right, 0 centred; <see cref="FlowY"/>: -1
/// downwards from Y, 1 upwards, 0 a single centred slot) and the farthest y an element may reach (<see cref="Limit"/>).</summary>
internal readonly struct RegionFrame
{
    public RegionFrame(float x, float y, int flowX, int flowY, float limit)
    {
        X = x;
        Y = y;
        FlowX = flowX;
        FlowY = flowY;
        Limit = limit;
    }

    public float X { get; }

    public float Y { get; }

    public int FlowX { get; }

    public int FlowY { get; }

    public float Limit { get; }
}

/// <summary>The screen and the game's own HUD as measured this frame, in the weapon list's space.</summary>
internal struct HudScene
{
    /// <summary>The visible screen.</summary>
    public HudBox Screen;
    /// <summary>The player's safe margin at the screen edges, in HUD units.</summary>
    public float Margin;
    /// <summary>The player's offset of the weapon list and vitals regions.</summary>
    public float OffsetX, OffsetY;
    /// <summary>Everything the game's weapon list draws (names, ammo); null while it draws nothing.</summary>
    public HudBox? WeaponList;
    /// <summary>The game's health bar with the vitals drawn above it (pulse, health, infection, shield); null while it is hidden.</summary>
    public HudBox? Vitals;
    /// <summary>The middle of the game's health bar (the vitals region is centred on it).</summary>
    public float BarCentre;
    /// <summary>The game's weapon list or vitals are hidden by a mod: their last box still anchors the region but is not occupied.</summary>
    public bool WeaponListFree, VitalsFree;
}

/// <summary>Region geometry: where each region is on a screen and how its elements grow.</summary>
public static class HudRegions
{
    /// <summary>The space between elements, and between the game's HUD and the first element, in HUD units.</summary>
    public const float Gap = 12f;
    /// <summary>The space between the top of the native vitals and the first vitals element.</summary>
    public const float VitalsGap = 8f;

    /// <summary>Room kept at the bottom right for the game's build label.</summary>
    public const float BuildLabel = 30f;
    /// <summary>How far above the middle of the screen the banner slot is.</summary>
    public const float BannerRise = 150f;
    /// <summary>How far below the top of the screen the notices start (the game draws its objective text above).</summary>
    public const float NoticesDrop = 150f;

    /// <summary>Which way elements of <paramref name="region"/> grow from their root (see <see cref="RegionFrame"/>).</summary>
    public static (int X, int Y) Flow(HudRegion region) => region switch
    {
        HudRegion.WeaponList => (-1, -1),
        HudRegion.Vitals => (0, 1),
        HudRegion.TopCenter => (0, -1),
        HudRegion.Notices => (1, -1),
        HudRegion.RightColumn => (-1, -1),
        HudRegion.BottomLeft => (1, 1),
        HudRegion.BottomRight => (-1, 1),
        HudRegion.TopRight => (-1, -1),
        HudRegion.TopLeft => (1, -1),
        _ => (0, 0),
    };

    /// <summary>The region on this screen, or null when it cannot be placed (the weapon list or the vitals are not drawn).</summary>
    internal static RegionFrame? Frame(HudRegion region, in HudScene scene)
    {
        var s = scene.Screen;
        var m = scene.Margin;
        var cx = (s.Left + s.Right) / 2f;
        var cy = (s.Bottom + s.Top) / 2f;
        var hh = s.Height / 2f;
        var (fx, fy) = Flow(region);
        switch (region)
        {
            case HudRegion.WeaponList:
                if (scene.WeaponList is not { } list) return null;
                return new RegionFrame(list.Right + scene.OffsetX, list.Bottom - Gap + scene.OffsetY, fx, fy, s.Bottom + m);
            case HudRegion.Vitals:
                if (scene.Vitals is not { } vitals) return null;
                return new RegionFrame(scene.BarCentre + scene.OffsetX, vitals.Top + VitalsGap + scene.OffsetY, fx, fy, cy);
            case HudRegion.CenterBanner:
                return new RegionFrame(cx, cy + BannerRise, fx, fy, cy + BannerRise);
            case HudRegion.TopCenter:
                return new RegionFrame(cx, s.Top - m, fx, fy, cy + BannerRise + 70f);
            case HudRegion.Notices:
                return new RegionFrame(s.Left + m, s.Top - m - NoticesDrop, fx, fy, cy);
            case HudRegion.RightColumn:
                return new RegionFrame(s.Right - m, cy + 0.25f * hh, fx, fy, cy - 0.45f * hh);
            case HudRegion.BottomLeft:
                return new RegionFrame(s.Left + m, s.Bottom + m, fx, fy, cy - 0.1f * hh);
            case HudRegion.BottomRight:
                // Up to half way above the middle: a replacement inventory over a moved chat, and a command list being typed beside them
                // (its table is taller than its idle rows), fit; anything placed higher up on the right is still moved past.
                return new RegionFrame(s.Right - m, s.Bottom + m + BuildLabel, fx, fy, cy + 0.5f * hh);
            case HudRegion.TopRight:
                return new RegionFrame(s.Right - m, s.Top - m, fx, fy, cy + 220f);
            case HudRegion.TopLeft:
                return new RegionFrame(s.Left + m, s.Top - m, fx, fy, cy + 220f);
            default:
                return new RegionFrame(cx, cy, 0, 0, cy);
        }
    }

    /// <summary>The box of an element of <paramref name="width"/> x <paramref name="height"/> whose root is at
    /// (<paramref name="x"/>, <paramref name="y"/>) and that grows in (<paramref name="fx"/>, <paramref name="fy"/>).</summary>
    public static HudBox Box(float x, float y, int fx, int fy, float width, float height)
    {
        var left = fx < 0 ? x - width : fx > 0 ? x : x - width / 2f;
        var bottom = fy < 0 ? y - height : fy > 0 ? y : y - height / 2f;
        return new HudBox(left, bottom, left + width, bottom + height);
    }

    /// <summary>The root of an element growing in (<paramref name="fx"/>, <paramref name="fy"/>) that fills <paramref name="box"/>:
    /// the corner it grows from (the middle on a centred axis); the inverse of <see cref="Box"/>.</summary>
    public static (float X, float Y) Root(HudBox box, int fx, int fy) =>
        (fx < 0 ? box.Right : fx > 0 ? box.Left : (box.Left + box.Right) / 2f, fy < 0 ? box.Top : fy > 0 ? box.Bottom : (box.Bottom + box.Top) / 2f);
}

/// <summary>What became of an element in the last layout.</summary>
public enum LayoutState : byte
{
    /// <summary>Not shown by its owner (or not built yet).</summary>
    Hidden,
    /// <summary>Placed in its region.</summary>
    Placed,
    /// <summary>Shown by its owner but left out: its region is full or its single slot is held by a higher priority element.</summary>
    Collapsed,
    /// <summary>Its region cannot be placed this frame (the game draws no weapon list or vitals).</summary>
    Unavailable,
    /// <summary>In the free <see cref="HudRegion.World"/> region: its root is the middle of the screen.</summary>
    Free,
}

/// <summary>One element as the layout sees it: what it asks for (set by the caller) and where it went (set by the layout).</summary>
internal sealed class LayoutEntry
{
    public LayoutEntry(string id, HudSlot slot)
    {
        Id = id;
        Slot = slot;
    }

    /// <summary>The namespaced ID, <c>mod/element</c>.</summary>
    public string Id { get; }

    public HudSlot Slot { get; set; }

    /// <summary>The owner shows the element this frame.</summary>
    public bool Visible { get; set; }

    /// <summary>The element redraws the game's inventory (a <c>HudLoadout</c>): while a mod hides the game's weapon list, the
    /// elements of <see cref="HudRegion.WeaponList"/> stack on from it instead of from the hidden list.</summary>
    public bool WeaponListStandIn { get; set; }

    /// <summary>The region it was stacked in at the last layout: its own, or for a <see cref="HudRegion.WeaponList"/> element while
    /// the game's list is hidden, the region of the inventory standing in for it (it then stacks on past that inventory, its root
    /// still the corner its own region grows from).</summary>
    public HudRegion Home { get; internal set; }

    /// <summary>Stacked with the inventory that stands in for the hidden weapon list (see <see cref="Home"/>).</summary>
    public bool BesideStandIn => Home != Slot.Region;

    /// <summary>The layout run that placed it, so a row is placed once.</summary>
    internal int Run;

    /// <summary>What the element drew at its last measure, in HUD units (0 before the first).</summary>
    public float DrawnWidth { get; set; }

    public float DrawnHeight { get; set; }

    public LayoutState State { get; internal set; }

    /// <summary>The element's root (the corner it grows from; the middle for a centred slot).</summary>
    public float RootX { get; internal set; }

    public float RootY { get; internal set; }

    public HudBox Box { get; internal set; }

    internal float Width => Math.Max(Slot.Width, DrawnWidth);

    internal float Height => Math.Max(Slot.Height, DrawnHeight);
}

/// <summary>
/// Places every element of every mod on the screen in one pass, without allocating: elements are taken by priority (highest first;
/// ties by ID), each region stacks its elements from its anchor in its direction with <see cref="HudRegions.Gap"/> between them, plus
/// the slot's own <see cref="HudSlot.Space"/> before it (elements sharing a <see cref="HudSlot.Row"/> stand side by side on one line of the stack), and an element that would overlap
/// anything already placed (the game's weapon list and vitals are placed first) is moved on past it. An element that would then reach
/// beyond its region's limit, or into a single slot already taken, is collapsed (not shown). While a mod hides the game's weapon list
/// and an inventory standing in for it is shown (<see cref="LayoutEntry.WeaponListStandIn"/>), the elements of
/// <see cref="HudRegion.WeaponList"/> are placed last, stacking on past everything in that inventory's region.
/// </summary>
internal sealed class HudLayout
{
    public const string GameWeaponList = "game/weapon list", GameVitals = "game/vitals";
    private const int MaxSteps = 32;

    private readonly List<LayoutEntry> _entries = new();
    private readonly List<LayoutEntry> _order = new();
    private readonly List<LayoutEntry> _row = new();
    private readonly List<HudBox> _occupied = new();
    private readonly float[] _cursor = new float[Enum.GetValues(typeof(HudRegion)).Length];
    private readonly LayoutEntry?[] _slotHolder = new LayoutEntry?[Enum.GetValues(typeof(HudRegion)).Length];
    private bool _orderDirty = true;
    private int _run;

    public IReadOnlyList<LayoutEntry> Entries => _entries;

    public void Add(LayoutEntry entry)
    {
        _entries.Add(entry);
        _orderDirty = true;
    }

    public void Remove(LayoutEntry entry)
    {
        if (_entries.Remove(entry)) _orderDirty = true;
    }

    /// <summary>Call after an entry's priority changed.</summary>
    public void Reorder() => _orderDirty = true;

    /// <summary>Lays out every entry on <paramref name="scene"/>.</summary>
    public void Run(in HudScene scene)
    {
        if (_orderDirty) Sort();
        _run++;
        _occupied.Clear();
        if (scene.WeaponList is { } list && !scene.WeaponListFree) _occupied.Add(list);
        if (scene.Vitals is { } vitals && !scene.VitalsFree) _occupied.Add(vitals);
        for (var r = 0; r < _cursor.Length; r++)
        {
            _cursor[r] = float.NaN;
            _slotHolder[r] = null;
        }
        LayoutEntry? standIn = null;
        if (scene.WeaponListFree)
            foreach (var entry in _order)
                if (entry.WeaponListStandIn && entry.Visible && entry.Slot.Region != HudRegion.WeaponList) { standIn = entry; break; }
        foreach (var entry in _order)
            if (standIn == null || entry.Slot.Region != HudRegion.WeaponList) Place(entry, scene, entry.Slot.Region);
        if (standIn == null) return;
        var home = standIn.State == LayoutState.Placed ? standIn.Slot.Region : HudRegion.WeaponList;
        foreach (var entry in _order)
            if (entry.Slot.Region == HudRegion.WeaponList) Place(entry, scene, home);
    }

    private void Sort()
    {
        _order.Clear();
        _order.AddRange(_entries);
        _order.Sort((a, b) => a.Slot.Priority != b.Slot.Priority ? b.Slot.Priority.CompareTo(a.Slot.Priority) : string.CompareOrdinal(a.Id, b.Id));
        _orderDirty = false;
    }

    private void Place(LayoutEntry entry, in HudScene scene, HudRegion home)
    {
        if (entry.Run == _run) return;
        entry.Run = _run;
        entry.Home = home;
        if (!entry.Visible)
        {
            entry.State = LayoutState.Hidden;
            return;
        }
        if (HudRegions.Frame(home, scene) is not { } frame)
        {
            entry.State = LayoutState.Unavailable;
            return;
        }
        if (home == HudRegion.World)
        {
            entry.State = LayoutState.Free;
            entry.RootX = frame.X;
            entry.RootY = frame.Y;
            entry.Box = new HudBox(frame.X, frame.Y, frame.X, frame.Y);
            return;
        }
        var index = (int)home;
        if (frame.FlowY == 0)
        {
            if (_slotHolder[index] != null)
            {
                Collapse(entry);
                return;
            }
            var single = HudRegions.Box(frame.X, frame.Y, frame.FlowX, 0, entry.Width, entry.Height);
            var blocker = Blocker(single);
            if (blocker >= 0)
            {
                Collapse(entry);
                return;
            }
            _slotHolder[index] = entry;
            Accept(entry, single);
            return;
        }
        if (entry.Slot.Row != 0)
        {
            PlaceRow(entry, frame, home);
            return;
        }
        if (!Find(index, frame, entry.Width, entry.Height, entry.Slot.Space, out var box))
        {
            Collapse(entry);
            return;
        }
        Accept(entry, box);
    }

    /// <summary>The shown elements of <paramref name="first"/>'s region and row, side by side on one line of the stack.</summary>
    private void PlaceRow(LayoutEntry first, in RegionFrame frame, HudRegion home)
    {
        _row.Clear();
        float width = 0f, height = 0f, space = 0f;
        foreach (var e in _order)
        {
            if (e.Slot.Region != first.Slot.Region || e.Slot.Row != first.Slot.Row || !e.Visible) continue;
            if (!ReferenceEquals(e, first))
            {
                if (e.Run == _run) continue;
                e.Run = _run;
                e.Home = home;
            }
            width += (_row.Count > 0 ? HudRegions.Gap : 0f) + e.Width;
            height = Math.Max(height, e.Height);
            space = Math.Max(space, e.Slot.Space);
            _row.Add(e);
        }
        if (!Find((int)home, frame, width, height, space, out var box))
        {
            foreach (var e in _row) Collapse(e);
            return;
        }
        // From the region's corner outwards; every element on the edge the region grows from.
        var x = frame.FlowX < 0 ? box.Right : box.Left;
        foreach (var e in _row)
        {
            var w = e.Width;
            var h = e.Height;
            var l = frame.FlowX < 0 ? x - w : x;
            var b = frame.FlowY < 0 ? box.Top - h : box.Bottom;
            Accept(e, new HudBox(l, b, l + w, b + h));
            x = frame.FlowX < 0 ? l - HudRegions.Gap : l + w + HudRegions.Gap;
        }
    }

    /// <summary>The first free place of <paramref name="width"/> x <paramref name="height"/> in the stack of region
    /// <paramref name="index"/>, <paramref name="space"/> further on than the stack's cursor (or past anything it is moved past), moving
    /// on past anything placed; the stack's cursor moves past it. False when it would reach beyond the region's limit or no place was
    /// found in time.</summary>
    private bool Find(int index, in RegionFrame frame, float width, float height, float space, out HudBox box)
    {
        var y = (float.IsNaN(_cursor[index]) ? frame.Y : _cursor[index]) + (frame.FlowY < 0 ? -space : space);
        for (var step = 0; step < MaxSteps; step++)
        {
            box = HudRegions.Box(frame.X, y, frame.FlowX, frame.FlowY, width, height);
            if (frame.FlowY < 0 ? box.Bottom < frame.Limit : box.Top > frame.Limit) return false;
            var blocker = Blocker(box);
            if (blocker < 0)
            {
                _cursor[index] = frame.FlowY < 0 ? box.Bottom - HudRegions.Gap : box.Top + HudRegions.Gap;
                return true;
            }
            var other = _occupied[blocker];
            y = frame.FlowY < 0 ? other.Bottom - HudRegions.Gap - space : other.Top + HudRegions.Gap + space;
        }
        box = default;
        return false;
    }

    private int Blocker(HudBox box)
    {
        if (box.Width <= 0f || box.Height <= 0f) return -1;
        for (var i = 0; i < _occupied.Count; i++)
            if (_occupied[i].Overlaps(box)) return i;
        return -1;
    }

    /// <summary>Places the element in <paramref name="box"/>; its root is the corner its own region grows from.</summary>
    private void Accept(LayoutEntry entry, HudBox box)
    {
        var (fx, fy) = HudRegions.Flow(entry.Slot.Region);
        var (x, y) = HudRegions.Root(box, fx, fy);
        entry.State = LayoutState.Placed;
        entry.RootX = x;
        entry.RootY = y;
        entry.Box = box;
        if (box.Width > 0f && box.Height > 0f) _occupied.Add(box);
    }

    private static void Collapse(LayoutEntry entry) => entry.State = LayoutState.Collapsed;
}
