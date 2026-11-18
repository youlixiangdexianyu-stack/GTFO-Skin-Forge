using System;
using System.Collections.Generic;
using CellMenu;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// Copies of the APPAREL button (<c>CM_PlayerLobbyBar.m_clothesButton</c>), one per registered button and bar, placed in their
/// <see cref="LobbyArea"/> around the player's panel (<see cref="LobbyLayout"/>). Kept in step every frame: a copy exists for every bar,
/// shows only when the APPAREL button shows and the registration's visibility allows, and follows the APPAREL button's position; the
/// panel, the screen edges, the player's name and the next player's panel are measured again every half second, so players joining or
/// leaving, a rebuilt page and a resolution change all settle on their own. A copy keeps the button's click and height; its width is
/// the registration's share of a row (<see cref="LobbyWidth"/>); it is scaled down whole where an area beside the panel is narrow, and under
/// APPAREL only made lower (<see cref="LobbySpots.Squash"/>), never narrower than APPAREL; its look is the lobby skin (<see cref="LobbyButtonSkin"/>): a chamfered plate with a white outline,
/// a white band along the cut corner, the title in the middle and the note at the right end.
/// </summary>
internal static class LobbyButtons
{
    private const float MeasureEvery = 0.5f;
    /// <summary>A bar that keeps moving this long (s) is measured where it stands, so its buttons are never left unmeasured.</summary>
    private const float MeasureMovingAfter = 3f;
    private static readonly Registry<LobbyButton> Registered = new();
    /// <summary>Per bar, per button ID: the copy.</summary>
    private static readonly Dictionary<IntPtr, Dictionary<string, Copy>> Copies = new();
    /// <summary>Per bar: the room around its panel.</summary>
    private static readonly Dictionary<IntPtr, Room> Rooms = new();
    private static readonly List<IntPtr> Gone = new();
    private static HudBox _box;

    // This frame's shown buttons of one bar, in layout order, and where they go; grown, never shrunk, so a frame allocates nothing.
    private static LobbyButton[] _shown = Array.Empty<LobbyButton>();
    private static Copy[] _copies = Array.Empty<Copy>();
    private static string?[] _notes = Array.Empty<string?>();
    private static LobbyArea[] _wanted = Array.Empty<LobbyArea>();
    private static int[] _area = Array.Empty<int>(), _row = Array.Empty<int>();
    private static float[] _widths = Array.Empty<float>(), _offsets = Array.Empty<float>();
    private static readonly int[] RowsUsed = new int[3];
    private static readonly LobbySpots[] Spots = new LobbySpots[3];
    // The buttons and outcomes (no room, or another area) already written to the log.
    private static readonly HashSet<(string, int)> Reported = new();
    /// <summary>This bar's lobby strip rows and room under APPAREL (to the screen's bottom), for the log.</summary>
    private static int _strips;
    private static float _room;

    /// <summary>Under APPAREL before anything is measured: full-size rows a gap apart, as many as needed.</summary>
    private static readonly LobbySpots Unmeasured = new(int.MaxValue, 1f, 0f, -LobbyLayout.Step, -LobbyLayout.Step, 1f);

    private sealed class Copy
    {
        internal GameObject Root = null!;
        internal IntPtr Route;
        internal LobbyButtonSkin? Skin;
        internal string? Title, Note, Tip;
        internal bool Hover, ShownHover, Drawn;
        /// <summary>The area it was last shown in (-1: none; -2: not yet placed), so a move is logged once, not every frame.</summary>
        internal int Placed = -2;
        /// <summary>The click box as copied from APPAREL (a whole row) and the share of it the copy now has.</summary>
        internal BoxCollider2D? Collider;
        internal Vector2 FullSize, FullOffset;
        internal float Share = 1f, Squash = 1f;
    }

    /// <summary>Relative to the APPAREL button's pivot, in its parent's units (see <see cref="LobbyLayout"/>).</summary>
    private sealed class Room
    {
        internal HudBox Panel;
        internal float Floor = float.NegativeInfinity, Ceiling = float.PositiveInfinity;
        /// <summary>For the right column: the next player's panel; for the top row also the next player's name and status.</summary>
        internal float RightEdge = float.PositiveInfinity, TopRightEdge = float.PositiveInfinity;
        internal float At = float.NegativeInfinity;
        /// <summary>Since when the bar has been moving without a measurement (NaN: standing still).</summary>
        internal float Moving = float.NaN;
        /// <summary>APPAREL's place on the page last frame (in the page's moving content holder), to tell a bar still sliding in.</summary>
        internal Vector3 Seen = new(float.NaN, float.NaN, float.NaN);
        /// <summary>The rows under APPAREL and how many of them its lobby buttons fill.</summary>
        internal LobbySpots BelowSpots;
        internal int Below;
    }

    internal static LobbyButton? Add(LobbyButton button)
    {
        var problem = Registered.Add(button.Id, button);
        if (problem == null)
        {
            return button;
        }
        Plugin.Error("ui-lobby-button-not-added", "Lobby button not added: " + problem + ".");
        return null;
    }

    internal static void Remove(LobbyButton button)
    {
        if (!Registered.Remove(button.Id, button)) return;
        foreach (var perBar in Copies.Values)
            if (perBar.Remove(button.Id, out var copy)) Destroy(copy);
    }

    private static void Destroy(Copy copy)
    {
        Input.Unroute(copy.Route);
        if (copy.Root != null && !copy.Root.WasCollected)
        {
            copy.Root.SetActive(false);
            Object.Destroy(copy.Root);
        }
    }

    /// <summary>The rows under APPAREL on the bar and the first one after its lobby buttons there, where the lobby strips go.</summary>
    internal static (LobbySpots Spots, int First) StripRows(IntPtr bar)
        => Rooms.TryGetValue(bar, out var room) && room.BelowSpots.Scale > 0f ? (room.BelowSpots, room.Below) : (Unmeasured, 0);

    /// <summary>The APPAREL button's clickable box relative to its pivot, in its parent's units. Every bar is the same prefab, so the
    /// box measured on a bar whose button is shown (the local player's) is used on bars whose button is hidden (other players').
    /// False until one has been measured.</summary>
    internal static bool ButtonBox(CM_LobbyScrollItem clothes, Vector3 origin, out HudBox box)
    {
        var collider = clothes.m_collider;
        if (collider != null && collider.enabled && clothes.gameObject.activeInHierarchy)
        {
            var b = Local(clothes.transform.parent, origin, collider.bounds);
            if (b.Width > 1f && b.Height > 1f) _box = b;
        }
        box = _box;
        return box.Width > 1f;
    }

    /// <summary>World <paramref name="bounds"/> as a box in <paramref name="parent"/>'s units relative to <paramref name="origin"/>.</summary>
    private static HudBox Local(Transform parent, Vector3 origin, Bounds bounds)
    {
        var min = parent.InverseTransformPoint(bounds.min) - origin;
        var max = parent.InverseTransformPoint(bounds.max) - origin;
        return new HudBox(Math.Min(min.x, max.x), Math.Min(min.y, max.y), Math.Max(min.x, max.x), Math.Max(min.y, max.y));
    }
    internal static void Update()
    {
        // No button registered and no copy left to take down: nothing to look up.
        if (Copies.Count == 0 && Registered.Ordered.Count == 0) return;
        Gone.Clear();
        foreach (var (pointer, perBar) in Copies)
        {
            var alive = false;
            foreach (var copy in perBar.Values)
                if (copy.Root != null && !copy.Root.WasCollected) { alive = true; break; }
            if (!alive) Gone.Add(pointer);
        }
        foreach (var pointer in Gone)
        {
            foreach (var copy in Copies[pointer].Values) Destroy(copy);
            Copies.Remove(pointer);
            Rooms.Remove(pointer);
        }
        var page = CM_PageLoadout.Current;
        if (page == null || page.WasCollected || Registered.Ordered.Count == 0) return;
        var bars = page.m_playerLobbyBars;
        if (bars == null) return;
        // Indexed: enumerating the native array would allocate an enumerator every frame.
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null || bar.WasCollected) continue;
            try { Sync(page, bar); }
            catch (Exception error) { Plugin.Error("ui-lobby-buttons-failed-update", "Lobby buttons failed to update: " + error); }
        }
    }

    private static void Sync(CM_PageLoadout page, CM_PlayerLobbyBar bar)
    {
        var clothes = bar.m_clothesButton;
        if (clothes == null || clothes.WasCollected)
        {
            Plugin.Error("ui-lobby-buttons-cannot-be-shown-lobby", "Lobby buttons cannot be shown: a lobby bar has no APPAREL button to copy.");
            return;
        }
        if (!Copies.TryGetValue(bar.Pointer, out var perBar)) Copies[bar.Pointer] = perBar = new Dictionary<string, Copy>();
        if (!Rooms.TryGetValue(bar.Pointer, out var room)) Rooms[bar.Pointer] = room = new Room();
        var pose = clothes.transform;
        var origin = pose.localPosition;
        var shown = clothes.gameObject.activeInHierarchy;
        // The bars slide in from below the screen when the lobby opens (CM_PlayerLobbyBar's intro): a room measured then puts the screen's
        // bottom above APPAREL and every button reads as not fitting. A bar is measured only standing still, and once more when it stops;
        // one that never stands still is measured where it is every few seconds.
        var holder = page.m_movingContentHolder;
        var at = pose.position;
        var seen = holder != null ? holder.InverseTransformPoint(at) : at;
        var still = ForgeCore.Vec.SqrLength(seen - room.Seen) < 0.25f;
        room.Seen = seen;
        var now = Time.unscaledTime;
        if (!still)
        {
            room.At = float.NegativeInfinity;
            if (float.IsNaN(room.Moving)) room.Moving = now;
            else if (shown && now - room.Moving >= MeasureMovingAfter)
            {
                room.Moving = now;
                Measure(page, holder, bar, clothes, origin, room);
            }
        }
        else
        {
            room.Moving = float.NaN;
            if (shown && now - room.At >= MeasureEvery)
            {
                room.At = now;
                Measure(page, holder, bar, clothes, origin, room);
            }
        }
        var count = 0;
        var buttons = Registered.Ordered;
        Grow(buttons.Count);
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (!Registered.Contains(button.Id, button)) continue;
            bool visible;
            try { visible = (shown || button.WithoutApparel) && (button.Visible?.Invoke(bar) ?? (bar.m_player != null && bar.m_player.IsLocal)); }
            catch (Exception error)
            {
                Plugin.Error("ui-lobby-button-visibility-check-failed", "Lobby button '" + button.Id + "' visibility check failed: " + error);
                visible = false;
            }
            if (!Registered.Contains(button.Id, button)) continue;
            string? note = null;
            if (visible) note = NoteThisFrame(button);
            // Both callbacks may remove this or a later registration, or replace an ID in our old snapshot.
            if (!Registered.Contains(button.Id, button)) continue;
            if (!perBar.TryGetValue(button.Id, out var copy) || copy.Root == null || copy.Root.WasCollected)
            {
                if (copy != null) Destroy(copy);
                perBar[button.Id] = copy = Create(bar, clothes, button);
            }
            if (!visible)
            {
                if (copy.Root.activeSelf) copy.Root.SetActive(false);
                continue;
            }
            _shown[count] = button;
            _copies[count] = copy;
            _notes[count] = note;
            _wanted[count] = button.Area;
            count++;
        }

        var measured = ButtonBox(clothes, origin, out var box);
        for (var i = 0; i < count; i++) _widths[i] = measured ? LobbyLayout.Width(_shown[i].Width, box.Width) : 1f;
        _strips = LobbyStrips.ShownOn(bar.Pointer);
        if (measured)
        {
            Spots[1] = LobbyLayout.Spots(LobbyArea.Right, box, room.Panel, room.Floor, room.Ceiling, room.RightEdge);
            Spots[2] = LobbyLayout.Spots(LobbyArea.Top, box, room.Panel, room.Floor, room.Ceiling, room.TopRightEdge);
            // First with no floor under APPAREL, to learn how many rows its buttons take; then those rows and the strips' rows are fitted
            // between APPAREL and the screen's bottom, buttons first.
            Spots[0] = LobbyLayout.Below(box, float.NegativeInfinity, 0, 0);
            LobbyLayout.Assign(_wanted, _widths, count, Spots, _area, _row, _offsets, RowsUsed);
            _room = box.Bottom - room.Floor;
            Spots[0] = LobbyLayout.Below(box, room.Floor, RowsUsed[(int)LobbyArea.Below] + _strips, _strips);
        }
        else
        {
            // Nothing measured yet: the buttons go under APPAREL, one to a row, as many as there are.
            Spots[0] = Unmeasured;
            Spots[1] = Spots[2] = default;
            _room = float.PositiveInfinity;
        }
        LobbyLayout.Assign(_wanted, _widths, count, Spots, _area, _row, _offsets, RowsUsed);
        room.Below = RowsUsed[(int)LobbyArea.Below];
        room.BelowSpots = Spots[(int)LobbyArea.Below];
        var scale0 = pose.localScale;
        for (var i = 0; i < count; i++)
        {
            var button = _shown[i];
            var copy = _copies[i];
            var area = _area[i];
            if (area != copy.Placed)
            {
                // Once per button and outcome: the measurements in the text change with every resolution or panel, which would write it again each time.
                if (area != (int)button.Area && Reported.Add((button.Id, area)))
                {
                    if (area < 0)
                        Plugin.Error("ui-lobby-button-does-not-fit", "Lobby button '" + button.Id + "' is not shown: no lobby area has room for it on this screen" + RoomOf(button) + ".");
                    else
                        Plugin.Error("ui-lobby-button-moved-to-another-area", "Lobby button '" + button.Id + "' has no room in the " + button.Area + " area on this screen; it is shown in the " + (LobbyArea)area
                            + " area, row " + _row[i] + ", " + ForgeCore.Log.Num(_offsets[i]) + " from its left, at scale " + ForgeCore.Log.Num(Spots[area].Scale) + RoomOf(button) + ".");
                }
                copy.Placed = area;
            }
            if (area < 0)
            {
                if (copy.Root.activeSelf) copy.Root.SetActive(false);
                continue;
            }
            if (!copy.Root.activeSelf) copy.Root.SetActive(true);
            var spots = Spots[area];
            var (x, y) = spots.At(_row[i], _offsets[i]);
            var t = copy.Root.transform;
            var target = new Vector3(origin.x + x + Plugin.OffsetX, origin.y + y + Plugin.OffsetY, origin.z);
            if (t.localPosition != target) t.localPosition = target;
            var scale = scale0 * spots.Scale;
            if (t.localScale != scale) t.localScale = scale;
            if (measured) Shape(copy, _widths[i] / box.Width, spots.Squash);
            var note = _notes[i];
            var title = button.ShownTitle;
            var tip = copy.Hover ? button.ShownTooltip : null;
            if (copy.Drawn && copy.Title == title && copy.Note == note && copy.ShownHover == copy.Hover && copy.Tip == tip) continue;
            copy.Skin?.Draw(title, note, copy.Hover, tip);
            copy.Title = title;
            copy.Note = note;
            copy.Tip = tip;
            copy.ShownHover = copy.Hover;
            copy.Drawn = true;
        }
        Array.Clear(_shown, 0, count);
        Array.Clear(_copies, 0, count);
        Array.Clear(_notes, 0, count);
    }

    /// <summary>The rows this frame's layout gave the button's own area, and the room under APPAREL, for the log.</summary>
    private static string RoomOf(LobbyButton button)
    {
        var own = Spots[(int)button.Area];
        var below = Spots[(int)LobbyArea.Below];
        return " (its " + button.Area + " area has " + own.Rows + " row(s), each " + ForgeCore.Log.Num(own.Length) + " long; under APPAREL "
               + ForgeCore.Log.Num(_room) + " down to the screen's bottom, " + below.Rows + " row(s) for buttons and " + _strips + " for lobby strips, "
               + ForgeCore.Log.Num(-below.StepY) + " apart, rows " + ForgeCore.Log.Num(below.Squash) + " of their height)";
    }

    /// <summary>Gives the copy <paramref name="share"/> of a whole row's width, keeping its left edge, and <paramref name="squash"/> of its
    /// height about its middle: its click box and its drawn plate (redrawn at that size, its title sized to the height, not squeezed) both follow.</summary>
    private static void Shape(Copy copy, float share, float squash)
    {
        if (Math.Abs(copy.Share - share) < 1e-4f && Math.Abs(copy.Squash - squash) < 1e-4f) return;
        copy.Share = share;
        copy.Squash = squash;
        var collider = copy.Collider;
        if (collider != null)
        {
            var left = copy.FullOffset.x - copy.FullSize.x / 2f;
            collider.size = new Vector2(copy.FullSize.x * share, copy.FullSize.y * squash);
            collider.offset = new Vector2(left + copy.FullSize.x * share / 2f, copy.FullOffset.y);
        }
        copy.Skin?.Shape(share, squash);
        copy.Drawn = false;
    }

    private static void Grow(int count)
    {
        if (_shown.Length >= count) return;
        _shown = new LobbyButton[count];
        _copies = new Copy[count];
        _notes = new string?[count];
        _wanted = new LobbyArea[count];
        _area = new int[count];
        _row = new int[count];
        _widths = new float[count];
        _offsets = new float[count];
    }

    /// <summary>Measures the room around the bar's panel, relative to the APPAREL button's pivot in its parent's units: the panel
    /// (APPAREL, the weapon and booster slots and their headers), the screen edges (the page's screen size over its content holder, as
    /// <see cref="MenuScreen"/> reads it, kept <see cref="MenuScreen.EdgeGap"/> inside; at the page's rest position, since the panel pans
    /// with it), the bottom of the player's name and status where they stand over the panel, and the left edge of the next bar to the
    /// right (its panel, the same prefab as this one, and for the top row also its name and status).</summary>
    private static void Measure(CM_PageLoadout page, Transform? holder, CM_PlayerLobbyBar bar, CM_LobbyScrollItem clothes, Vector3 origin, Room room)
    {
        var parent = clothes.transform.parent;
        var panel = new Extent();
        if (clothes.m_collider != null && clothes.m_collider.enabled) panel.Add(Local(parent, origin, clothes.m_collider.bounds));
        var slots = bar.m_inventorySlotItems;
        if (slots != null)
            foreach (var item in slots.Values)
                if (item != null && item.gameObject.activeInHierarchy && item.m_collider != null && item.m_collider.enabled)
                    panel.Add(Local(parent, origin, item.m_collider.bounds));
        var boosters = bar.m_boosterImplantSlotHolder;
        if (boosters != null && boosters.gameObject.activeInHierarchy)
            foreach (var collider in boosters.GetComponentsInChildren<BoxCollider2D>(false))
                if (collider.enabled) panel.Add(Local(parent, origin, collider.bounds));
        if (TextBox(bar.m_weaponsHeaderText, parent, origin, out var header)) panel.Add(header);
        if (TextBox(bar.m_boosterImplantsHeaderText, parent, origin, out header)) panel.Add(header);
        room.Panel = panel.Box;

        float right = float.PositiveInfinity, top = float.PositiveInfinity;
        room.Floor = float.NegativeInfinity;
        var size = page.m_screenResVec2;
        var unit = holder != null && holder.localScale.x != 0f ? holder.localScale.x : 1f;
        if (holder != null && size.x > 0f && size.y > 0f)
        {
            var half = new Vector3(size.x / 2f / unit - MenuScreen.EdgeGap, size.y / 2f / unit - MenuScreen.EdgeGap, 0f);
            // The holder and the panel pan together, so these are the edges where they stand at rest.
            var low = parent.InverseTransformPoint(holder.TransformPoint(-half)) - origin;
            var high = parent.InverseTransformPoint(holder.TransformPoint(half)) - origin;
            room.Floor = Math.Min(low.y, high.y);
            top = Math.Max(low.y, high.y);
            right = Math.Max(low.x, high.x);
        }

        room.Ceiling = top;
        if (panel.Any)
        {
            if (TextBox(bar.m_nickText, parent, origin, out var name) && name.Bottom >= panel.Box.Top) room.Ceiling = Math.Min(room.Ceiling, name.Bottom);
            if (TextBox(bar.m_statusText, parent, origin, out var status) && status.Bottom >= panel.Box.Top) room.Ceiling = Math.Min(room.Ceiling, status.Bottom);
        }

        room.RightEdge = room.TopRightEdge = right;
        var next = NextBar(page, bar);
        if (next != null && panel.Any)
        {
            var step = parent.InverseTransformVector(new Vector3(next.transform.position.x - bar.transform.position.x, 0f, 0f)).x;
            room.RightEdge = Math.Min(right, panel.Box.Left + step - LobbyLayout.Gap);
            room.TopRightEdge = room.RightEdge;
            if (TextBox(next.m_nickText, parent, origin, out var name)) room.TopRightEdge = Math.Min(room.TopRightEdge, name.Left - LobbyLayout.Gap);
            if (TextBox(next.m_statusText, parent, origin, out var status)) room.TopRightEdge = Math.Min(room.TopRightEdge, status.Left - LobbyLayout.Gap);
        }
    }

    /// <summary>The nearest bar to the right of <paramref name="bar"/> on screen, or null.</summary>
    private static CM_PlayerLobbyBar? NextBar(CM_PageLoadout page, CM_PlayerLobbyBar bar)
    {
        var bars = page.m_playerLobbyBars;
        if (bars == null) return null;
        var x = bar.transform.position.x;
        CM_PlayerLobbyBar? next = null;
        var best = float.PositiveInfinity;
        for (var i = 0; i < bars.Length; i++)
        {
            var other = bars[i];
            if (other == null || other.WasCollected || other.Pointer == bar.Pointer) continue;
            var dx = other.transform.position.x - x;
            if (dx > 1e-3f && dx < best) { best = dx; next = other; }
        }
        return next;
    }

    /// <summary>The drawn box of a shown, non-empty text, relative to <paramref name="origin"/> in <paramref name="parent"/>'s units.</summary>
    private static bool TextBox(TextMeshPro? text, Transform parent, Vector3 origin, out HudBox box)
    {
        box = default;
        if (text == null || text.WasCollected || !text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text)) return false;
        var renderer = text.GetComponent<MeshRenderer>();
        if (renderer == null || !renderer.enabled) return false;
        box = Local(parent, origin, renderer.bounds);
        return box.Width > 1f && box.Height > 1f;
    }

    /// <summary>The smallest box around the boxes added.</summary>
    private struct Extent
    {
        private float _left, _bottom, _right, _top;
        internal bool Any;

        internal void Add(HudBox box)
        {
            if (box.Width <= 0f || box.Height <= 0f) return;
            if (!Any) { (_left, _bottom, _right, _top) = (box.Left, box.Bottom, box.Right, box.Top); Any = true; return; }
            _left = Math.Min(_left, box.Left);
            _bottom = Math.Min(_bottom, box.Bottom);
            _right = Math.Max(_right, box.Right);
            _top = Math.Max(_top, box.Top);
        }

        internal HudBox Box => Any ? new HudBox(_left, _bottom, _right, _top) : default;
    }

    /// <summary>The note is read once per frame, however many bars show the button.</summary>
    private static string? NoteThisFrame(LobbyButton button)
    {
        if (button.Note == null) return null;
        if (button.NoteFrame == Time.frameCount) return button.NoteValue;
        button.NoteFrame = Time.frameCount;
        try { button.NoteValue = button.Note(); }
        catch (Exception error)
        {
            Plugin.Error("ui-lobby-button-note-failed", "Lobby button '" + button.Id + "' note failed: " + error);
            button.NoteValue = null;
        }
        return button.NoteValue;
    }

    private static Copy Create(CM_PlayerLobbyBar bar, CM_LobbyScrollItem clothes, LobbyButton button)
    {
        var root = Object.Instantiate(clothes.gameObject, clothes.transform.parent);
        root.name = "ForgeUI.LobbyButton." + button.Id;
        NativeUi.StripLocalization(root);
        var item = root.GetComponent<CM_LobbyScrollItem>() ?? throw new InvalidOperationException("the APPAREL button copy has no CM_LobbyScrollItem");
        item.m_onBtnPress = new UnityEvent();
        item.m_parentBar = clothes.m_parentBar;
        var copy = new Copy { Root = root, Collider = item.m_collider ?? item.GetComponent<BoxCollider2D>() };
        if (copy.Collider != null) (copy.FullSize, copy.FullOffset) = (copy.Collider.size, copy.Collider.offset);
        copy.Skin = LobbyButtonSkin.On(item);
        copy.Route = Input.Route(item, () =>
        {
            try { button.OnClick(bar); }
            catch (Exception error) { Plugin.Error("ui-lobby-button-click-failed", "Lobby button '" + button.Id + "' click failed: " + error); }
        }, inside => copy.Hover = inside);
        return copy;
    }
}

/// <summary>
/// The lobby look of a button copy: its own sprites and labels are switched off and a <see cref="MenuCanvas"/> draws a chamfered plate
/// (top left and bottom right cut) with a white outline, a white band along the top left cut, the title in the middle and the note
/// small and grey at the right end; under the pointer the plate lightens. Texts are shown as written.
/// </summary>
internal sealed class LobbyButtonSkin
{
    private static readonly Color Plate = new(0.03f, 0.04f, 0.047f, 0.86f), PlateHover = new(0.09f, 0.115f, 0.13f, 0.92f);
    private readonly MenuCanvas _canvas;
    private readonly Rect _full;
    private Rect _box;

    private LobbyButtonSkin(MenuCanvas canvas, Rect box)
    {
        _canvas = canvas;
        _full = _box = box;
    }

    /// <summary>Draws the plate <paramref name="share"/> of the whole row's width from the same left edge and <paramref name="squash"/> of its
    /// height about the same middle (the title and note size to the height); takes effect on the next <see cref="Draw"/>.</summary>
    internal void Shape(float share, float squash)
    {
        var height = _full.Height() * squash;
        _box = new Rect(_full.XMin(), _full.CenterY() - height / 2f, _full.Width() * share, height);
    }

    internal static LobbyButtonSkin? On(CM_LobbyScrollItem item)
    {
        var sprites = item.GetComponentsInChildren<SpriteRenderer>(true);
        var labels = item.GetComponentsInChildren<TextMeshPro>(true);
        if (sprites.Length == 0 || labels.Length == 0)
        {
            Plugin.Error("ui-lobby-button-copy-has-no-sprite", "A lobby button copy has no sprite or text to draw with; it shows as the APPAREL button.");
            return null;
        }
        var order = MenuCanvas.TopOrder(item) + 1;
        var box = MenuCanvas.BoxIn(item.transform, item.m_collider ?? item.GetComponent<BoxCollider2D>(), new Rect(-225f, -27.5f, 450f, 55f));
        var canvas = new MenuCanvas(item.transform, "ForgeUI.ButtonLook", sprites[0], labels[0], order);
        // The copy's own labels are turned off, not only emptied: the game writes the APPAREL title into them again when the language
        // changes, and a language drawn with a fallback font (Chinese) then adds a glyph renderer no veil covered. (The copy is ours and
        // is destroyed whole, so nothing turns them back on, as MenuCanvas.SilenceOriginalTexts does for a game object.)
        foreach (var label in labels)
        {
            NativeUi.SetText(label, "");
            label.enabled = false;
        }
        // forceRenderingOff, not enabled: the game turns the button's sprites on again when its page comes back (intro, show).
        canvas.VeilOriginals();
        return new LobbyButtonSkin(canvas, box);
    }

    internal void Draw(string title, string? note, bool hover, string? tip)
    {
        var r = _box;
        var h = r.Height();
        var cut = Math.Min(14f, h * 0.26f);
        var c = _canvas;
        c.Begin();
        c.Plate(r, cut, hover ? PlateHover : Plate);
        c.Frame(r, cut, hover ? MenuLook.Line : MenuLook.Faded(MenuLook.Line, 0.92f));
        c.Picture(UIKitSprites.CornerBand, new Rect(r.XMin(), r.YMax() - 2f * cut, 2f * cut, 2f * cut), MenuLook.Line);
        // The note shows only where it fits whole and the title still fits whole beside it (measured widths); on a narrow button it
        // is left out rather than pressed onto the title.
        var noteWidth = 0f;
        if (!string.IsNullOrEmpty(note))
        {
            var width = c.TextWidth(note!, h * 0.34f);
            var roomBeside = r.Width() - 2f * Math.Max(width + h * 0.3f + h * 0.4f, 2f * cut);
            if (width <= r.Width() * 0.3f && c.TextWidth(title, h * 0.5f) <= roomBeside)
                noteWidth = c.Text(note!, r.XMax() - h * 0.4f, r.CenterY(), h * 0.34f, MenuLook.Dim, HudAlign.Right, r.Width() * 0.3f) + h * 0.3f;
        }
        var room = r.Width() - 2f * Math.Max(noteWidth + h * 0.4f, 2f * cut);
        c.Text(title, r.CenterX(), r.CenterY(), h * 0.5f, MenuLook.Line, HudAlign.Centre, room);
        // The tooltip over the button, inside a band two buttons wide either side and a few buttons high.
        if (tip != null) MenuTooltip.Draw(c, r, tip, h * 0.36f, new Rect(r.XMin() - r.Width(), r.YMin() - 4f * h, 3f * r.Width(), 9f * h));
        c.End();
    }
}
