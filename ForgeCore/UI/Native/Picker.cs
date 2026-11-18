using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CellMenu;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// The picker, shown in the bar's own loadout pop-up (<c>CM_PlayerLobbyBar.m_popupScrollWindow</c>, the window the game's weapon
/// and apparel pickers share), opened and closed through the bar's own <c>ShowPopup</c> / <c>HidePopup</c>, so its layer and
/// click-outside behaviour are the game's. It is sized and placed like the game's weapon picker (<c>SetSize(1600, 760)</c>,
/// <c>SetPosition(0, 350)</c>), the list and tabs fill the window and the details go in the weapon info box
/// (<c>m_popupInfoBoxWeaponPrefab</c>). The slots are a column of copies of the bar's APPAREL button, each made as tall as a tile,
/// parented to the window against its left edge; each is registered as one of the window's non-content items, the way the game
/// registers its header tabs, because the window's ray filter drops every hit inside it that lies outside its list unless the item is one.
/// The game's pieces keep the clicks; their look is drawn over them (<see cref="PickerSkin"/>).
/// <para>The column and window together are wider than the game's picker, so the window is scaled and placed into the game
/// picker's footprint, and held still against the page's cursor panning while open, so no part leaves
/// the screen as the cursor moves. Its own scale and position are put back on closing.</para>
/// <para>When the game shows its own picker in the same window, or the window closes in any way, this one is taken down and
/// its source's <see cref="PickerSource.Closed"/> runs. Pieces are destroyed a frame later, after the click that closed them has
/// reached every listener.</para>
/// </summary>
internal sealed class Picker
{
    /// <summary>Where the game puts the window for its weapon and apparel pickers (<c>ShowWeaponSelectionPopup</c>).</summary>
    private const float NativeWindowY = 350f;
    private const float Gap = 12f, ContentPadding = 5f, ColumnPadding = 14f, TileGap = 10f, TitleHeight = 52f;
    /// <summary>A slot tile's height: as tall as the column allows, between the APPAREL button's height and this.</summary>
    private const float MaxTile = 140f;
    /// <summary>The strip between the list's rows and the details box, where the list's scrollbar runs.</summary>
    private const float ListGap = 30f;
    /// <summary>The picker's window is narrower than the game's (1600): list rows only use the left part of their width, so the room
    /// goes to the scale instead. Slot tiles are narrower than the APPAREL button for the same reason. The block then fits the
    /// game's footprint at about 1.19 times the size it had with the full 1600 window and the button-wide tiles.</summary>
    private const float PickerWidth = 1380f, TileWidth = 330f;
    private const int MaxTabs = 5;
    /// <summary>Window units: the gap between the tabs' plates and the frame's top edge, how far a tab plate reaches below its box (the
    /// current tab joins the frame), and how far inside the frame the first row starts.</summary>
    private const float TabGap = 4f, TabReach = 6f, ListInset = 8f;

    internal static Picker? Current { get; private set; }
    private static PickerSource? _lastClosed;
    private static int _closedFrame = -1;
    private static long _openRequest;
    private static readonly List<GameObject> Dying = new();

    internal PickerSource Source { get; }
    private readonly CM_PlayerLobbyBar _bar;
    private readonly CM_ScrollWindow _window;
    private readonly GameObject _column;
    private readonly GameObject _decoration;
    private readonly GameObject _cardPrefab;
    private readonly List<(CM_LobbyScrollItem Item, PickerSlotSkin? Skin, IntPtr Route)> _slots = new();
    private readonly List<(CM_LobbyScrollItem Card, int Entry, PickerRowSkin? Skin, IntPtr Route)> _entryCards = new();
    private readonly PickerTabsSkin _tabSkin = new();
    /// <summary>The window's shared tab headers <see cref="SettleLayout"/> moved, with their place before it did; put back on closing.</summary>
    private readonly List<(Transform Header, Vector3 Place)> _lifted = new();
    private PickerColumnSkin? _columnSkin;
    private PickerDetailSkin? _detail;
    private IntPtr _detailBox;
    private Rect _tile;
    private CM_Item? _columnHitArea;
    private IntPtr _columnRoute;
    private readonly Vector3 _windowScale, _windowPosition;
    private Vector2 _pinned;
    private bool _opening, _disposed;
    private int _slot, _tab = -1, _focus = -1;
    private string? _warning;
    private string[] _tabs = Array.Empty<string>();
    private int _entriesTab = -2;
    private bool _hasClear;
    private string? _slotsHeader;
    /// <summary>The current tab's controls when it is an adjust page (<see cref="PickerSource.Controls"/>), else null; row i is control i.</summary>
    private IReadOnlyList<UIControl>? _controls;
    /// <summary>The control row being dragged, -1 for none.</summary>
    private int _drag = -1;
    private float _listRight = float.PositiveInfinity;
    /// <summary>The title bar and the more-below arrow (<see cref="PickerFrameSkin"/>), the gear's press target, and whether the source's
    /// settings are open in the side panel.</summary>
    private PickerFrameSkin? _frame;
    private CM_Item? _gear, _titleBar;
    private IntPtr _gearRoute, _barRoute;
    private bool _settings, _more;
    /// <summary>The side panel as last drawn (<see cref="PickerSource.Panel"/>, or the settings), the details it was drawn under, what was
    /// drawn as press targets, the press targets themselves (pooled under the details box), and the span of the control being dragged.</summary>
    private PickerPanel? _panel;
    private PickerInfo? _info;
    private PickerPanelView _view = new() { Focus = -1, Drag = -1 };
    private readonly List<PickerHit> _hits = new();
    private readonly List<(CM_Item Item, IntPtr Route)> _hitAreas = new();
    private Rect _dragTrack;
    /// <summary>The details box's transform as of the last draw: the space the side panel's spans are in.</summary>
    private Transform? _detailSpace;
    /// <summary>A grid list (<see cref="PickerLayout.Grid"/>): the distance between two of the window's rows and a row's own box, measured
    /// on the first layout after the cards were made (zero until then).</summary>
    private bool _grid;
    private float _pitch;
    private Rect _rowBox;

    internal static void Toggle(CM_PlayerLobbyBar bar, PickerSource source)
    {
        if (Current?.Source == source || (_closedFrame == Time.frameCount && _lastClosed == source)) { Close(); return; }
        Open(bar, source);
    }

    internal static void Open(CM_PlayerLobbyBar bar, PickerSource source)
    {
        PatchGroups.Ensure(PatchGroup.Picker);
        var request = ++_openRequest;
        Close();
        // Closed() is public user code: a newer open from it owns the shared window.
        if (request != _openRequest) return;
        try { _ = new Picker(bar, source); }
        catch (Exception error)
        {
            Plugin.Error("ui-picker-could-not-be-opened", "The picker could not be opened: " + error);
            if (request == _openRequest && Current != null) Close();
        }
    }

    internal static void Close()
    {
        var picker = Current;
        if (picker == null) return;
        try { picker._bar.HidePopup(); }
        catch (Exception error) { Plugin.Error("ui-hiding-picker-failed", "Hiding the picker failed: " + error); }
        picker.TakeDown(true); // the HidePopup postfix normally did this already
    }

    internal static void Tick()
    {
        var current = Current;
        if (current != null) current.Watch();
        if (Time.frameCount == _closedFrame) return;
        _lastClosed = null;
        foreach (var go in Dying) if (go != null && !go.WasCollected) Object.Destroy(go);
        Dying.Clear();
    }

    /// <summary>After the page has moved its content for the cursor this frame: puts the window back where it was placed.</summary>
    internal static void Pin()
    {
        var current = Current;
        if (current == null || current._disposed || current._opening) return;
        try { current.HoldStill(); }
        catch (Exception error) { Plugin.Error("ui-holding-picker-place-failed", "Holding the picker in place failed: " + error); }
    }

    private Picker(CM_PlayerLobbyBar bar, PickerSource source)
    {
        _bar = bar;
        Source = source;
        Current = this;
        _opening = true;
        _window = bar.m_popupScrollWindow ?? throw new InvalidOperationException("the bar has no loadout pop-up window");
        _cardPrefab = bar.m_scrollWindowCardPrefab ?? throw new InvalidOperationException("the bar has no card prefab");
        var infoPrefab = bar.m_popupInfoBoxWeaponPrefab ?? throw new InvalidOperationException("the bar has no weapon info box prefab");
        var size = new Vector2(PickerWidth, CM_PlayerLobbyBar.POPUP_HEIGHT);
        _windowScale = _window.transform.localScale;
        _windowPosition = _window.transform.localPosition;

        _slot = Math.Clamp(source.FirstSlot(), 0, Math.Max(0, source.SlotCount - 1));
        _window.hasInfoBox = true;
        _window.m_infoBoxWidth = CM_PlayerLobbyBar.WEAPON_INFOBOX_WIDTH;
        _window.RespawnInfoBoxFromPrefab(infoPrefab);
        DropDeadItems(_window);
        _window.SetSize(size);
        _window.SetPosition(new Vector2(0f, NativeWindowY));

        _column = new GameObject("ForgeUI.PickerSlots") { layer = _window.gameObject.layer };
        _column.transform.SetParent(_window.transform, false);
        _decoration = new GameObject("ForgeUI.ColumnDecoration") { layer = _column.layer };
        _decoration.transform.SetParent(_column.transform, false);
        Refresh();

        bar.ShowPopup();
        LayOutColumn(out var blockLeft);
        Fit(blockLeft);
        _window.UpdatePopupChildItems();
        SettleLayout();
        if (_grid) Grid();
        _opening = false;
        DrawSlots();
        DrawRows();
        DrawFrame();
        Info();
        PickerSkin.Clip(_window);
    }

    /// <summary>Per frame while open: takes the picker down if the window was hidden by a path that did not go through
    /// <c>HidePopup</c>.</summary>
    private void Watch()
    {
        if (_disposed || _opening) return;
        if (_window == null || _window.WasCollected) { TakeDown(false); return; }
        var shown = _window.gameObject.activeInHierarchy;
        if (!shown) { Close(); return; }
        if (_controls != null || _view.Drag >= 0) Adjust();
        Cull();
    }

    /// <summary>Per frame: every row is kept inside the list's clip area (<see cref="PickerRowSkin.Cull"/>), whatever the list's scroll; the
    /// window's own clipping cuts only the game's sprites cleanly, and rows drawn later or texts rebuilt after it took them showed past the
    /// frame, over the tabs and under the bottom edge.</summary>
    private void Cull()
    {
        var clip = _window.m_clipRect;
        if (clip == null) return;
        var r = clip.rect;
        float a = clip.TransformPoint(new Vector3(r.center.x, r.yMin, 0f)).y, b = clip.TransformPoint(new Vector3(r.center.x, r.yMax, 0f)).y;
        float bottom = Math.Min(a, b), top = Math.Max(a, b);
        var below = false;
        foreach (var (_, _, skin, _) in _entryCards) if (skin != null) below |= skin.Cull(bottom, top);
        if (below == _more) return;
        _more = below;
        _frame?.ShowMore(below);
    }

    // ---- adjust pages --------------------------------------------------------------------------------------

    /// <summary>Per frame on an adjust page: a drag follows the menu cursor while the press is held (mouse button or the menu's click,
    /// which is the gamepad's press); otherwise the control under the cursor takes one-step nudges from the arrow keys and the
    /// shoulder buttons.</summary>
    private void Adjust()
    {
        try
        {
            if (_view.Drag >= 0)
            {
                if (Held()) FollowPanel();
                else { _view.Drag = -1; RedrawPanel(); }
                return;
            }
            if (_controls == null) return;
            if (_drag >= 0)
            {
                if (Held()) Follow(_drag);
                else { _drag = -1; DrawControls(); }
                return;
            }
            if (_focus < 0 || _focus >= _controls!.Count) return;
            var steps = (Down(KeyCode.RightArrow, InputAction.ScrollItemRight) ? 1 : 0) - (Down(KeyCode.LeftArrow, InputAction.ScrollItemLeft) ? 1 : 0);
            if (steps != 0 && _controls[_focus].Move(steps)) DrawControls();
        }
        catch (Exception error) { Plugin.Error("ui-picker-control-failed", "A control of picker source " + Source.GetType().FullName + " failed: " + error); }
    }

    private static bool Held() =>
        UnityEngine.Input.GetMouseButton(0) || (InputMapper.GetButton?.Invoke(InputAction.MenuClick, ForgeCore.Frame.Focus) ?? false);

    private static bool Down(KeyCode key, InputAction action) =>
        UnityEngine.Input.GetKeyDown(key) || (InputMapper.GetButtonDown?.Invoke(action, ForgeCore.Frame.Focus) ?? false);

    /// <summary>A press on control row <paramref name="entry"/>: the control takes it where the cursor is (a slider then follows the cursor
    /// while the press is held).</summary>
    private void Grab(int entry)
    {
        if (_controls == null || entry < 0 || entry >= _controls.Count) return;
        _focus = entry;
        _warning = null;
        if (_controls[entry].Drags) _drag = entry;
        Follow(entry);
        DrawRows();
        Info();
    }

    /// <summary>Presses control row <paramref name="entry"/> where the menu cursor is along its active span.</summary>
    private void Follow(int entry)
    {
        if (_controls == null || entry >= _controls.Count || entry >= _entryCards.Count) return;
        var skin = _entryCards[entry].Skin;
        // Only the rows show values; the details box is drawn on presses and hovers, not on every step of a drag.
        if (skin != null && Press(_controls[entry], skin.Space, skin.Active)) DrawControls();
    }

    /// <summary>The side panel's pressed control takes the menu cursor where it is along its span; the panel is redrawn when its value moved.</summary>
    private void FollowPanel()
    {
        var panel = _panel;
        var i = _view.Drag >= 0 ? _view.Drag : _view.Focus;
        if (_detailSpace == null || panel == null || i < 0 || i >= panel.ControlCount) return;
        if (Press(panel.Control(i), _detailSpace, _dragTrack)) RedrawPanel();
    }

    /// <summary>Presses <paramref name="control"/> where the menu cursor is along <paramref name="track"/> (in <paramref name="space"/>'s units).</summary>
    private static bool Press(UIControl control, Transform space, Rect track)
    {
        var page = CM_PageLoadout.Current;
        var cursor = page == null ? null : page.m_cursor;
        if (cursor == null) return false;
        float from = track.XMin(), width = track.Width();
        if (width <= 0f) return false;
        var local = space.InverseTransformPoint(cursor.transform.position);
        return control.Press((local.x - from) / width);
    }

    // ---- placement -----------------------------------------------------------------------------------------

    /// <summary>Scales and places the window so the column, list and details together take the footprint of the game's own
    /// picker (1600 wide around x 0 at the same height, which the game keeps on screen), with the page's cursor panning taken
    /// out, then holds it there (<see cref="HoldStill"/>). The UI is drawn through the game's own render pass, so the UI
    /// camera's projection does not match the screen and is not used to measure it.</summary>
    private void Fit(float blockLeft)
    {
        var background = _window.m_background ?? throw new InvalidOperationException("the pop-up window has no background");
        var t = _window.transform;
        var (frameLeft, _, frameBottom, frameTop) = LocalBounds(background.bounds, t);
        var low = blockLeft;
        var high = frameLeft + PickerWidth;
        var footprint = CM_PlayerLobbyBar.POPUP_WIDTH;
        var pan = PanOffset(t.parent);
        var (scale, x) = PickerFit.Fit(low, high, -footprint / 2f, footprint / 2f);
        var y = t.localPosition.y;
        t.localScale = _windowScale * scale;
        _pinned = new Vector2(x, y);
        t.localPosition = new Vector3(x - pan.x, y - pan.y, t.localPosition.z);
    }

    /// <summary>How far the page's cursor panning has moved the window's parent from its rest position, in that parent's
    /// units. The page moves <c>m_movingContentHolder</c> to its start offset plus <c>m_localHolderOffset</c>
    /// (<c>CM_PageBase.MoveContentHolder</c>); zero when the window is not under it.</summary>
    private Vector3 PanOffset(Transform? parent) => parent == null ? Vector3.zero : PagePan.Offset(CM_PageLoadout.Current, _window.transform);

    private void HoldStill()
    {
        if (_window == null || _window.WasCollected) return;
        var t = _window.transform;
        var pan = PanOffset(t.parent);
        var target = new Vector3(_pinned.x - pan.x, _pinned.y - pan.y, t.localPosition.z);
        if (t.localPosition != target) t.localPosition = target;
    }

    /// <summary>The window's own list and tabs overlap in this picker: the tabs reach below the window's top edge and the list
    /// starts above it, so the first row is cut by the clip. Measured from the real pieces, in world y, and corrected only where
    /// they overlap, so a layout the game already got right is left alone (and a second call does nothing): the tabs are lifted
    /// to sit <see cref="TabGap"/> above the frame's top edge (their plates reach <see cref="TabReach"/> below their boxes), and
    /// the list origin is lowered until the first row's top is <see cref="ListInset"/> inside the frame.</summary>
    private void SettleLayout()
    {
        var top = _window.m_background?.bounds.max.y;
        if (top == null) return;
        var unit = _window.transform.lossyScale.y;
        var headers = _window.m_headers;
        var lift = 0f;
        if (headers != null)
            foreach (var header in headers)
            {
                var box = header?.GetComponentInChildren<BoxCollider2D>(true);
                if (box != null) lift = Math.Max(lift, top.Value + (TabGap + TabReach) * unit - WorldY(box, false));
            }
        if (lift > 0f && headers != null)
            foreach (var header in headers)
            {
                if (header == null) continue;
                var t = header.transform;
                if (!_lifted.Exists(l => l.Header.Pointer == t.Pointer)) _lifted.Add((t, t.localPosition));
                t.position += new Vector3(0f, lift, 0f);
            }
        var first = _entryCards.Count > 0 ? _entryCards[0].Card.GetComponentInChildren<BoxCollider2D>(true) : null;
        var drop = first == null ? 0f : WorldY(first, true) - (top.Value - ListInset * unit);
        if (drop <= 0f) return;
        var container = _window.m_contentContainer.transform;
        var local = container.parent.InverseTransformVector(new Vector3(0f, -drop, 0f)).y;
        container.position += new Vector3(0f, -drop, 0f);
        var origin = _window.m_originalContentPosition;
        origin.y += local;
        _window.m_originalContentPosition = origin;
    }

    /// <summary>A box collider's top or bottom edge, a world y.</summary>
    private static float WorldY(BoxCollider2D box, bool topEdge) =>
        box.transform.TransformPoint(new Vector3(0f, box.offset.y + (topEdge ? 1f : -1f) * box.size.y / 2f, 0f)).y;

    private static (float Left, float Right, float Bottom, float Top) LocalBounds(Bounds bounds, Transform t)
    {
        var a = t.InverseTransformPoint(bounds.min);
        var b = t.InverseTransformPoint(bounds.max);
        return (Math.Min(a.x, b.x), Math.Max(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.y, b.y));
    }

    // ---- slot column ---------------------------------------------------------------------------------------

    /// <summary>A slot: a copy of the bar's APPAREL button for its click, its own label emptied (the tile draws the slot).</summary>
    private (CM_LobbyScrollItem, PickerSlotSkin?, IntPtr) SlotButton(CM_LobbyScrollItem prefab, int slot)
    {
        var go = Object.Instantiate(prefab.gameObject, _column.transform);
        go.name = "ForgeUI.Slot" + (slot + 1);
        NativeUi.StripLocalization(go);
        if (!go.activeSelf) go.SetActive(true);
        var item = go.GetComponent<CM_LobbyScrollItem>() ?? throw new InvalidOperationException("the APPAREL button copy has no CM_LobbyScrollItem");
        item.m_onBtnPress = new UnityEvent();
        item.m_parentBar = _bar;
        item.m_useInfoPopup = false;
        item.ForcePopupLayer(true, null);
        var labels = go.GetComponentsInChildren<TextMeshPro>(true);
        foreach (var label in labels) NativeUi.SetText(label, "");
        var route = Input.Route(item, () =>
        {
            Clicked(PickerClick.Slot);
            ChooseSlot(slot);
        }, null);
        _window.AddNonContentItem(item);
        return (item, PickerSlotSkin.On(item, labels.Length > 0 ? labels[0] : null), route);
    }

    /// <summary>Places the slot column against the left edge of the window's frame: as tall as the frame, a heading inside its top,
    /// then one tile per slot, as tall as the room allows (between the APPAREL button's height and <see cref="MaxTile"/>). Each tile is
    /// its APPAREL copy with the clickable box made the tile's size. The column sits at the depth of the window's list, where the game's
    /// cards are in front of the window's background collider. Returns the column's left edge in window units.</summary>
    private void LayOutColumn(out float columnLeft)
    {
        var background = _window.m_background ?? throw new InvalidOperationException("the pop-up window has no background");
        var windowTransform = _window.transform;
        var (frameLeft, _, bottom, top) = LocalBounds(background.bounds, windowTransform);
        var height = top - bottom;

        var button = _slots.Count > 0 ? ItemRect(_slots[0].Item) : new Rect(-225f, -27.5f, 450f, 55f);
        var count = Math.Max(1, _slots.Count);
        var room = height - TitleHeight - ColumnPadding;
        var tileHeight = Math.Clamp((room - (count - 1) * TileGap) / count, Math.Min(button.height, room / count), MaxTile);
        _tile = new Rect(button.center.x - TileWidth / 2f, button.center.y - tileHeight / 2f, TileWidth, tileHeight);
        var width = TileWidth + 2f * ColumnPadding;
        var right = frameLeft - Gap;
        var left = right - width;
        columnLeft = left;

        var listTransform = _window.m_contentContainer != null ? _window.m_contentContainer.transform : windowTransform;
        var depth = windowTransform.InverseTransformPoint(listTransform.position).z;
        _column.transform.localRotation = Quaternion.identity;
        _column.transform.localScale = Vector3.one;
        _column.transform.localPosition = new Vector3(0f, 0f, depth);

        if (_columnHitArea == null)
        {
            // A real item makes the whole column part of the popup, including its title and gaps.
            _columnHitArea = HitArea(_column.transform, "ForgeUI.ColumnHitArea", () => { }, out _columnRoute);
        }
        // Behind the buttons, in front of the native click-outside catcher.
        _columnHitArea.transform.localPosition = new Vector3((left + right) / 2f, (bottom + top) / 2f, 1f);
        _columnHitArea.m_collider.size = new Vector2(width, height);
        if (_columnSkin == null)
        {
            var label = _slots.Count > 0 ? _slots[0].Item.GetComponentInChildren<TextMeshPro>(true) : null;
            _columnSkin = new PickerColumnSkin(_decoration.transform, background, label);
            _frame = new PickerFrameSkin(_decoration.transform, background, label);
        }
        _columnSkin.Draw(Rect.MinMaxRect(left, bottom, right, top), Source.SlotsHeader);
        var y = top - TitleHeight;
        foreach (var (item, _, _) in _slots)
        {
            item.transform.localRotation = Quaternion.identity;
            item.transform.localScale = Vector3.one;
            var box = item.m_collider ?? item.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                box.offset = _tile.center;
                box.size = new Vector2(_tile.width, _tile.height);
            }
            item.transform.localPosition = new Vector3((left + right) / 2f - _tile.center.x, y - tileHeight / 2f - _tile.center.y, 0f);
            y -= tileHeight + TileGap;
        }
    }

    /// <summary>An invisible press target under <paramref name="parent"/>: a plain item with a box collider, on the pop-up layer and registered
    /// as one of the window's non-content items (the window's ray filter drops hits outside its list otherwise); presses go to
    /// <paramref name="click"/>. Place it with <see cref="Place"/>.</summary>
    private CM_Item HitArea(Transform parent, string name, Action click, out IntPtr route)
    {
        var go = new GameObject(name) { layer = _column.layer };
        go.transform.SetParent(parent, false);
        var item = go.AddComponent<CM_Item>();
        item.m_collider = go.AddComponent<BoxCollider2D>();
        item.Setup();
        item.m_onBtnPress = new UnityEvent();
        route = Input.Route(item, click, null);
        item.ForcePopupLayer(true, null);
        _window.AddNonContentItem(item);
        return item;
    }

    /// <summary>Puts a press target over <paramref name="box"/> (its parent's units) at depth <paramref name="z"/> (lower is nearer the camera).</summary>
    private static void Place(CM_Item item, Rect box, float z)
    {
        item.transform.localPosition = new Vector3(box.center.x, box.center.y, z);
        item.m_collider.offset = Vector2.zero;
        item.m_collider.size = box.size;
    }

    /// <summary>The item's clickable rectangle in its own local units (its box collider).</summary>
    private static Rect ItemRect(CM_Item item)
    {
        var box = item.m_collider ?? item.GetComponent<BoxCollider2D>();
        if (box == null) return new Rect(-225f, -27.5f, 450f, 55f);
        return new Rect(box.offset - box.size / 2f, box.size);
    }

    private void DrawSlots()
    {
        if (_tile.width <= 0f) return;
        var numbers = Source.NumberSlots;
        for (var i = 0; i < _slots.Count; i++)
            _slots[i].Skin?.Draw(_tile, Source.SlotCard(i, i == _slot), numbers ? i + 1 : 0, i == _slot);
    }

    /// <summary>Window units: the title bar's height, and the room left at its right end for the window's close box.</summary>
    private const float TitleBar = 50f, CloseRoom = 64f;

    /// <summary>The title bar of a picker with one tab (with the gear when the source has settings) and the more-below arrow on the list's
    /// bottom edge, in window units.</summary>
    private void DrawFrame()
    {
        var background = _window.m_background;
        if (_frame == null || background == null) return;
        var (left, right, bottom, top) = LocalBounds(background.bounds, _window.transform);
        var heading = _tabs.Length == 1 ? _tabs[0] : "";
        var bar = Rect.MinMaxRect(left, top + TabGap, right - CloseRoom, top + TabGap + TitleBar);
        var listRight = ListRight();
        var end = float.IsInfinity(listRight) ? right : _window.transform.InverseTransformPoint(new Vector3(listRight, 0f, 0f)).x;
        var edge = Rect.MinMaxRect(left, bottom - 2f, end, bottom + 26f);
        var button = _frame.Draw(bar, heading, Source.Settings != null && heading.Length > 0, _settings, edge);
        // The bar is part of the pop-up, like the tabs it replaces: a press on it does not count as one outside the window.
        if (heading.Length > 0)
        {
            _titleBar ??= HitArea(_column.transform, "ForgeUI.TitleBar", () => { }, out _barRoute);
            NativeUi.SetActive(_titleBar, true);
            Place(_titleBar, bar, 1f);
        }
        else if (_titleBar != null) NativeUi.SetActive(_titleBar, false);
        if (button.width > 0f)
        {
            _gear ??= HitArea(_column.transform, "ForgeUI.Gear", ToggleSettings, out _gearRoute);
            NativeUi.SetActive(_gear, true);
            Place(_gear, button, -1f);
        }
        else if (_gear != null) NativeUi.SetActive(_gear, false);
    }

    private void ToggleSettings()
    {
        if (_disposed) return;
        _settings = !_settings;
        _view = new PickerPanelView { Focus = -1, Drag = -1 };
        _warning = null;
        DrawFrame();
        Info();
    }

    // ---- building ------------------------------------------------------------------------------------------

    /// <summary>The tabs. The game calls a header's callback with the header item's cell ID, not the key given to
    /// <c>AddHeader</c> (its own callbacks ignore the argument), so each tab gets a closure over its own index.</summary>
    private void Tabs()
    {
        _window.ResetHeaders();
        // One tab has no strip: its name heads the title bar (DrawFrame).
        if (_tabs.Length < 2) return;
        for (var i = 0; i < _tabs.Length; i++)
        {
            var index = i;
            _window.AddHeader(RichText.Plain(_tabs[i]), i, (Action<int>)(_ => SelectTab(index)));
        }
    }

    private void SelectTab(int tab)
    {
        if (_disposed) return;
        try
        {
            if (tab < 0 || tab >= Math.Min(Source.Tabs.Count, MaxTabs)) { Refresh(); return; }
            _tab = tab;
            _focus = -1;
            _warning = null;
            Refresh();
        }
        catch (Exception error) { Plugin.Error("ui-switching-picker-tab-failed", "Switching the picker to tab " + tab + " failed: " + error); }
    }

    private CM_LobbyScrollItem Card(Transform parent, string name)
    {
        var go = Object.Instantiate(_cardPrefab, parent);
        go.name = name;
        NativeUi.StripLocalization(go);
        var card = go.GetComponent<CM_LobbyScrollItem>() ?? throw new InvalidOperationException("the card prefab has no CM_LobbyScrollItem");
        var slotItem = card.TryCast<CM_InventorySlotItem>();
        if (slotItem != null) slotItem.SetupAsWeaponPicker();
        // The weapon picker setup turns on the card's gear pop-up, which would show on hover with nothing behind it.
        card.m_useInfoPopup = false;
        if (card.m_infoPopup != null) card.m_infoPopup.gameObject.SetActive(false);
        card.m_onBtnPress = new UnityEvent();
        card.ForcePopupLayer(true, null);
        return card;
    }

    private static Il2CppSystem.Collections.Generic.List<iScrollWindowContent> Content(IEnumerable<CM_LobbyScrollItem> cards)
    {
        var list = new Il2CppSystem.Collections.Generic.List<iScrollWindowContent>();
        foreach (var card in cards) list.Add(new iScrollWindowContent(card.Pointer));
        return list;
    }

    private void BuildEntries(int count, bool hasClear)
    {
        foreach (var (card, _, _, route) in _entryCards) Release(card, route);
        _entryCards.Clear();
        if (hasClear) AddEntry(-1, "ForgeUI.Clear");
        for (var i = 0; i < count; i++) AddEntry(i, "ForgeUI.Entry" + i);
        _entriesTab = _tab;
        _hasClear = hasClear;
        _pitch = 0f;
        _window.SetContentItems(Content(_entryCards.Select(e => e.Card)), ContentPadding);
    }

    private void AddEntry(int entry, string name)
    {
        var card = Card(_window.transform, name);
        var route = Input.Route(card, () =>
        {
            Clicked(PickerClick.Entry);
            if (_controls != null) Grab(entry);
            else Pick(entry);
        }, inside => Hover(entry, inside));
        _entryCards.Add((card, entry, PickerRowSkin.On(card), route));
    }

    /// <summary>The card the details follow (-1: what the slot holds), read back by ForgeDevelopment's console after <see cref="Hover"/>.</summary>
    private int Focus => _focus;

    /// <summary>The cursor entered (<paramref name="inside"/>) or left list card <paramref name="entry"/>: the details follow it. ForgeDevelopment's
    /// console calls it directly (<c>picker hover</c>) to show a card's hover without moving the mouse.</summary>
    private void Hover(int entry, bool inside)
    {
        if (_disposed || _drag >= 0) return;
        // A grid's details go back to what the slot holds when the cursor leaves the card.
        if (!inside && (!_grid || _focus != entry)) return;
        if (inside && _focus == entry) return;
        _focus = inside ? entry : -1;
        _warning = null;
        try
        {
            DrawRows();
            Info();
        }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-describe-cards", "Picker source " + Source.GetType().FullName + " failed to describe its cards: " + error); }
    }

    private static void Release(CM_LobbyScrollItem? card, IntPtr route)
    {
        Input.Unroute(route);
        if (card == null || card.WasCollected) return;
        card.gameObject.SetActive(false);
        Dying.Add(card.gameObject);
    }

    /// <summary>Removes list and non-content entries whose objects are gone (cards a previous picker destroyed after the window
    /// kept them), so the window's layout and ray filter do not walk into them.</summary>
    private static void DropDeadItems(CM_ScrollWindow window)
    {
        var items = window.m_contentItems;
        if (items != null)
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var unityObject = items[i]?.TryCast<Object>();
                if (unityObject == null || unityObject.WasCollected) items.RemoveAt(i);
            }
        var others = window.m_nonContentItems;
        if (others != null)
            for (var i = others.Count - 1; i >= 0; i--)
                if (others[i] == null || others[i].WasCollected) others.RemoveAt(i);
    }

    // ---- actions -------------------------------------------------------------------------------------------

    private void Clicked(PickerClick click)
    {
        if (_disposed) return;
        try { Source.Clicked(click); }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-click", "Picker source " + Source.GetType().FullName + " failed on a " + click + " click: " + error); }
    }

    private void ChooseSlot(int slot)
    {
        if (_disposed) return;
        if (slot < 0 || slot >= Source.SlotCount) { Refresh(); return; }
        if (slot != _slot) _view = new PickerPanelView { Focus = -1, Drag = -1 };
        _slot = slot;
        _focus = -1;
        _warning = null;
        CloseSettings();
        Refresh();
    }

    private void Pick(int entry)
    {
        if (_disposed) return;
        string? refusal;
        try
        {
            // Source data can also change between a refresh and the next click.
            if (_slot < 0 || _slot >= Source.SlotCount || _tab < 0 || _tab >= Math.Min(Source.Tabs.Count, MaxTabs) ||
                entry < -1 || (entry == -1 ? Source.ClearCard == null : entry >= Source.Entries(_slot, _tab).Count))
            {
                Refresh();
                return;
            }
            refusal = Source.Pick(_slot, _tab, entry);
        }
        catch (Exception error)
        {
            Plugin.Error("ui-picker-source-failed-pick", "Picker source " + Source.GetType().FullName + " failed to pick: " + error);
            return;
        }
        _focus = entry;
        _warning = refusal;
        if (refusal == null) _slot = Source.SlotAfterPick(_slot);
        // What the slot wears changed: its panel starts again from the first pages.
        _view = new PickerPanelView { Focus = -1, Drag = -1 };
        CloseSettings();
        Refresh();
    }

    private void CloseSettings()
    {
        if (!_settings) return;
        _settings = false;
        DrawFrame();
    }

    /// <summary>A press on side panel target <paramref name="i"/> (as last drawn, <see cref="_hits"/>).</summary>
    private void PanelPress(int i)
    {
        if (_disposed || i < 0 || i >= _hits.Count || _panel == null) return;
        var hit = _hits[i];
        try
        {
            switch (hit.Kind)
            {
                case PickerHitKind.Scheme:
                    Clicked(PickerClick.Entry);
                    _warning = _settings ? null : Source.PickScheme(_slot, hit.Index);
                    _view.Focus = -1;
                    Refresh();
                    return;
                case PickerHitKind.SchemePage:
                    _view.SchemePage += hit.Index;
                    break;
                case PickerHitKind.ControlPage:
                    _view.ControlPage += hit.Index;
                    _view.Focus = -1;
                    break;
                case PickerHitKind.Control:
                    Clicked(PickerClick.Entry);
                    if (hit.Index >= _panel.ControlCount) return;
                    _view.Focus = hit.Index;
                    if (_panel.Control(hit.Index).Drags) _view.Drag = hit.Index;
                    _dragTrack = hit.Active;
                    FollowPanel();
                    break;
            }
            Info();
        }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-pick", "Picker source " + Source.GetType().FullName + " failed to pick: " + error); }
    }

    // ---- display -------------------------------------------------------------------------------------------

    internal void Refresh()
    {
        if (_disposed) return;
        try
        {
            var tabs = Source.Tabs.Take(MaxTabs).ToArray();
            var tabsChanged = _entriesTab == -2 || !_tabs.SequenceEqual(tabs);
            if (tabsChanged)
            {
                _tabs = tabs;
                _tab = tabs.Length > 0 ? 0 : -1;
                _focus = -1;
                _warning = null;
                Tabs();
            }
            var slotCount = Math.Max(0, Source.SlotCount);
            var slot = slotCount == 0 ? -1 : Math.Clamp(_slot, 0, slotCount - 1);
            if (slot != _slot) { _slot = slot; _focus = -1; _warning = null; }
            var slotsChanged = _slots.Count != slotCount;
            if (slotsChanged)
            {
                var others = _window.m_nonContentItems;
                foreach (var (item, _, route) in _slots)
                {
                    if (others != null && item != null && !item.WasCollected) others.Remove(item);
                    Release(item, route);
                }
                _slots.Clear();
                var prefab = _bar.m_clothesButton ?? throw new InvalidOperationException("the bar has no APPAREL button");
                for (var i = 0; i < slotCount; i++) _slots.Add(SlotButton(prefab, i));
            }
            _controls = _tab < 0 || _slot < 0 ? null : Source.Controls(_slot, _tab);
            _grid = _controls == null && Source.Layout == PickerLayout.Grid;
            var entries = _tab < 0 || _controls != null ? Array.Empty<PickerCard>() : Source.Entries(_slot, _tab);
            var clear = _tab < 0 || _controls != null ? null : Source.ClearCard;
            var count = _controls?.Count ?? entries.Count;
            var entriesChanged = tabsChanged || _entriesTab != _tab || _hasClear != (clear != null) ||
                                 _entryCards.Count != count + (clear == null ? 0 : 1);
            if (entriesChanged)
            {
                _focus = -1;
                _drag = -1;
                BuildEntries(count, clear != null);
            }
            if (_slot < 0 || _tab < 0 || _focus < -1 || _focus >= count) _focus = -1;
            if (_drag >= count) _drag = -1;
            var heading = Source.SlotsHeader;
            if (!_opening && (slotsChanged || _slotsHeader != heading))
            {
                // Refit in the window's original units, not in its already fitted scale.
                _window.transform.localScale = _windowScale;
                _window.transform.localPosition = new Vector3(0f, NativeWindowY, _windowPosition.z);
                LayOutColumn(out var left);
                Fit(left);
                PickerSkin.Clip(_window);
            }
            _slotsHeader = heading;
            if (!_opening) DrawSlots();
            // An adjust page draws its controls here too: drawn only on a hover, its rows were empty dark cards until the cursor came by.
            if (_controls != null) DrawRows();
            else DrawRows(entries, clear);
            if (tabsChanged || entriesChanged || slotsChanged)
            {
                if (_tab >= 0 && _tabs.Length > 1) _window.SelectHeader(_tab);
                _window.UpdatePopupChildItems();
                SettleLayout();
                // The window has just laid its rows out: a grid pairs them up again and draws the cards in their cells.
                if (_grid && _controls == null)
                {
                    Grid();
                    DrawRows(entries, clear);
                }
            }
            _tabSkin.Draw(_window, _tabs.Length > 1 ? _tabs : Array.Empty<string>(), _tab);
            if (!_opening) DrawFrame();
            Info();
        }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-describe-cards", "Picker source " + Source.GetType().FullName + " failed to describe its cards: " + error); }
    }

    private void DrawRows()
    {
        if (_controls == null) { DrawRows(_tab < 0 ? Array.Empty<PickerCard>() : Source.Entries(_slot, _tab), _tab < 0 ? null : Source.ClearCard); return; }
        _listRight = ListRight();
        DrawControls();
    }

    /// <summary>Draws every control row of an adjust page (each frame of a drag, so it touches nothing else). The list's right edge
    /// is the one measured by the last full <see cref="DrawRows()"/>: the window is held still on screen.</summary>
    private void DrawControls()
    {
        var grew = false;
        var listRight = _listRight;
        foreach (var (_, entry, skin, _) in _entryCards)
        {
            if (skin == null || entry < 0 || entry >= _controls!.Count) continue;
            skin.DrawControl(_controls[entry], entry == _focus, entry == _drag, listRight);
            grew |= skin.Grew;
        }
        if (grew) PickerSkin.Clip(_window);
    }

    /// <summary>Draws every list row, the focused one framed; new pieces are handed to the list's clipping.</summary>
    private void DrawRows(IReadOnlyList<PickerCard> entries, PickerCard? clear)
    {
        var grew = false;
        var listRight = ListRight();
        foreach (var (_, entry, skin, _) in _entryCards)
        {
            if (skin == null) continue;
            var data = entry < 0 ? clear : entry < entries.Count ? entries[entry] : null;
            if (data == null) continue;
            if (_grid) skin.DrawCard(data, entry == _focus);
            else skin.Draw(data, entry == _focus, listRight);
            grew |= skin.Grew;
        }
        if (grew) PickerSkin.Clip(_window);
    }

    /// <summary>Window units between the two cards of a grid row.</summary>
    private const float GridGap = 14f;

    /// <summary>
    /// Pairs the window's rows into a grid (<see cref="PickerLayout.Grid"/>) after it laid them out one under another: card 2k keeps its
    /// place and becomes the left card of grid row k, card 2k+1 moves up beside it, and each card's box becomes its cell, half the list wide
    /// and as tall as the two rows it now covers. The window keeps spacing its rows as before, so its scroll length stays right and nothing
    /// depends on how the game sizes a row; the row pitch and box are measured once after the cards were made.
    /// </summary>
    private void Grid()
    {
        if (_entryCards.Count == 0) return;
        var first = _entryCards[0].Card.transform;
        if (_pitch <= 0f)
        {
            _rowBox = PickerSkin.Box(_entryCards[0].Card, new Rect(-400f, -60f, 800f, 120f));
            _pitch = _entryCards.Count > 1 ? Math.Abs(first.InverseTransformPoint(_entryCards[1].Card.transform.position).y) : 0f;
            if (_pitch < _rowBox.height) _pitch = _rowBox.height * 1.08f;
        }
        var listRight = ListRight();
        var right = float.IsInfinity(listRight) ? _rowBox.xMax : Math.Min(_rowBox.xMax, first.InverseTransformPoint(new Vector3(listRight, first.position.y, first.position.z)).x);
        float left = _rowBox.xMin, top = _rowBox.yMax, bottom = top - (_pitch + _rowBox.height), middle = (left + right) / 2f;
        var leftCell = Rect.MinMaxRect(left, bottom, middle - GridGap / 2f, top);
        var rightCell = Rect.MinMaxRect(middle + GridGap / 2f, bottom, right, top);
        for (var i = 0; i < _entryCards.Count; i += 2)
        {
            var a = _entryCards[i].Card;
            SetBox(a, leftCell);
            if (i + 1 >= _entryCards.Count) break;
            var b = _entryCards[i + 1].Card;
            b.transform.position = a.transform.position;
            SetBox(b, rightCell);
        }
    }

    /// <summary>Makes <paramref name="item"/>'s clickable box <paramref name="box"/> (in the item's own units).</summary>
    private static void SetBox(Component item, Rect box)
    {
        var collider = item.GetComponent<BoxCollider2D>() ?? item.GetComponentInChildren<BoxCollider2D>(true);
        if (collider == null) return;
        var t = collider.transform;
        var a = t.InverseTransformPoint(item.transform.TransformPoint(new Vector3(box.xMin, box.yMin, 0f)));
        var b = t.InverseTransformPoint(item.transform.TransformPoint(new Vector3(box.xMax, box.yMax, 0f)));
        collider.offset = new Vector2((a.x + b.x) / 2f, (a.y + b.y) / 2f);
        collider.size = new Vector2(Math.Abs(b.x - a.x), Math.Abs(b.y - a.y));
    }

    /// <summary>Where the list ends, a world x: <see cref="ListGap"/> window units (room for the scrollbar) left of the details box.
    /// Without a details box the list is not cut.</summary>
    private float ListRight()
    {
        var box = _window.InfoBox?.m_collider;
        if (box == null || box.WasCollected) return float.PositiveInfinity;
        var edge = box.transform.TransformPoint(new Vector3(box.offset.x - box.size.x / 2f, box.offset.y, 0f)).x;
        return edge - ListGap * _window.transform.lossyScale.x;
    }

    private void Info()
    {
        var box = _window.InfoBox;
        if (box == null) return;
        if (_detail == null || _detailBox != box.Pointer)
        {
            _detail?.Destroy();
            // The box's own texts, buttons and hexagon go; the details are drawn over it.
            box.SetInfoBox("", "", "", "", "", null);
            if (box.m_infoMainIcon != null) box.m_infoMainIcon.enabled = false;
            _detail = PickerDetailSkin.On(box);
            _detailBox = box.Pointer;
        }
        if (_settings && Source.Settings is { } settings)
        {
            _info = Source.SettingsInfo;
            _panel = new PickerPanel { Controls = settings };
        }
        else
        {
            _info = _slot < 0 || _tab < 0 ? new PickerInfo() : Source.Info(_slot, _tab, _focus);
            _panel = _slot < 0 ? null : Source.Panel(_slot, _tab < 0 ? -1 : _focus);
        }
        if (_panel == null) { _detail?.Draw(_info, _warning); _hits.Clear(); }
        else _detail?.Draw(_info, _warning, _panel, _view, _hits);
        _detailSpace = box.transform;
        PlaceHits(_detailSpace);
    }

    /// <summary>Redraws the side panel's controls as last asked for (a drag in progress moved one); nothing else moves.</summary>
    private void RedrawPanel()
    {
        if (_panel != null) _detail?.DrawControls(_panel, _view);
    }

    /// <summary>One press target over each target the side panel drew, from a pool in the slot column (which goes with the picker), placed
    /// over the details box in front of it; the rest are switched off.</summary>
    private void PlaceHits(Transform box)
    {
        var column = _column.transform;
        var z = column.InverseTransformPoint(box.position).z - 1f;
        for (var i = 0; i < _hits.Count; i++)
        {
            if (i == _hitAreas.Count)
            {
                var index = i;
                var item = HitArea(column, "ForgeUI.PanelHit" + i, () => PanelPress(index), out var route);
                _hitAreas.Add((item, route));
            }
            var area = _hitAreas[i].Item;
            if (area == null || area.WasCollected) continue;
            var r = _hits[i].Box;
            var a = column.InverseTransformPoint(box.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            var b = column.InverseTransformPoint(box.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            NativeUi.SetActive(area, true);
            Place(area, Rect.MinMaxRect(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y)), z);
        }
        for (var i = _hits.Count; i < _hitAreas.Count; i++)
            if (_hitAreas[i].Item is { } area && !area.WasCollected) NativeUi.SetActive(area, false);
    }

    // ---- closing -------------------------------------------------------------------------------------------

    /// <summary>Removes everything this picker added and puts the window's scale and position back; the window itself belongs
    /// to the game and stays. With <paramref name="closing"/> the window's list is emptied too; without it the game has just
    /// filled it with its own picker.</summary>
    private void TakeDown(bool closing)
    {
        if (_disposed) return;
        _disposed = true;
        if (Current == this) Current = null;
        _lastClosed = Source;
        _closedFrame = Time.frameCount;
        // First of all: the shared window's lists must not hold our items when they are released, whether the constructor got
        // half way, the window is closing, or the game is about to fill it (it resizes the window before replacing the content,
        // and that walks every listed item). ClearContent is not relied on to empty it.
        try
        {
            if (_window != null && !_window.WasCollected)
            {
                if (closing) _window.SetVisible(false);
                _window.m_contentItems = new Il2CppSystem.Collections.Generic.List<iScrollWindowContent>();
                var others = _window.m_nonContentItems;
                if (others != null)
                {
                    foreach (var (item, _, _) in _slots)
                        if (item != null && !item.WasCollected) others.Remove(item);
                    if (_columnHitArea != null && !_columnHitArea.WasCollected) others.Remove(_columnHitArea);
                    // The press targets live in the slot column and go with it.
                    foreach (var (item, _) in _hitAreas)
                        if (item != null && !item.WasCollected) others.Remove(item);
                    if (_gear != null && !_gear.WasCollected) others.Remove(_gear);
                    if (_titleBar != null && !_titleBar.WasCollected) others.Remove(_titleBar);
                }
                if (_window.InfoBox?.m_infoMainIcon != null) _window.InfoBox.m_infoMainIcon.enabled = true;
                _window.transform.localScale = _windowScale;
                _window.transform.localPosition = _windowPosition;
            }
        }
        catch (Exception error) { Plugin.Error("ui-emptying-pop-up-lists-after-picker", "Emptying the pop-up lists after the picker failed: " + error); }
        // The tabs and the details box are the game's and stay: what was drawn on them goes now, and the tabs go back where they were.
        try
        {
            _tabSkin.Destroy();
            foreach (var (header, place) in _lifted) if (header != null && !header.WasCollected) header.localPosition = place;
            _lifted.Clear();
            _detail?.Destroy();
            _detail = null;
            _frame?.Destroy();
            _frame = null;
        }
        catch (Exception error) { Plugin.Error("ui-removing-pickers-tabs-details-failed", "Removing the picker's tabs and details failed: " + error); }
        foreach (var (item, _, route) in _slots) Release(item, route);
        foreach (var (card, _, _, route) in _entryCards) Release(card, route);
        _slots.Clear();
        _entryCards.Clear();
        Input.Unroute(_columnRoute);
        Input.Unroute(_gearRoute);
        Input.Unroute(_barRoute);
        foreach (var (_, route) in _hitAreas) Input.Unroute(route);
        _hitAreas.Clear();
        if (_column != null && !_column.WasCollected)
        {
            _column.SetActive(false);
            Dying.Add(_column);
        }
        try { if (closing && _window != null && !_window.WasCollected) _window.ClearContent(); }
        catch (Exception error) { Plugin.Error("ui-clearing-pop-up-after-picker-failed", "Clearing the pop-up after the picker failed: " + error); }
        try { Source.Closed(); }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-closing", "Picker source " + Source.GetType().FullName + " failed on closing: " + error); }
    }

    /// <summary>The window closed (click outside, close box, page change, or <see cref="Close"/>).</summary>
    [PatchGroup(PatchGroup.Picker)]
    [HarmonyPatch(typeof(CM_PlayerLobbyBar), nameof(CM_PlayerLobbyBar.HidePopup))]
    private static class BarHidePopup
    {
        [HarmonyPostfix]
        private static void Postfix(CM_PlayerLobbyBar __instance)
        {
            var picker = Current;
            if (picker == null || picker._opening || picker._bar.Pointer != __instance.Pointer) return;
            picker.TakeDown(true);
        }
    }

    /// <summary>The game is about to fill the window with its own picker (weapons, tools, melee, apparel, boosters). It resizes the
    /// window before <c>ShowPopup</c>, so the picker is taken down here, and dead list entries are dropped in any case.</summary>
    // patch-targets: CM_PlayerLobbyBar.ShowWeaponSelectionPopup, CM_PlayerLobbyBar.ShowClothesSelect, CM_PlayerLobbyBar.ShowBoosterImplantTeamView, CM_PlayerLobbyBar.ShowBoosterImplantSelect
    [PatchGroup(PatchGroup.Picker)]
    [HarmonyPatch]
    private static class BarShowOwnPopup
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CM_PlayerLobbyBar), nameof(CM_PlayerLobbyBar.ShowWeaponSelectionPopup));
            yield return AccessTools.Method(typeof(CM_PlayerLobbyBar), nameof(CM_PlayerLobbyBar.ShowClothesSelect));
            yield return AccessTools.Method(typeof(CM_PlayerLobbyBar), nameof(CM_PlayerLobbyBar.ShowBoosterImplantTeamView));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(CM_PlayerLobbyBar)))
                if (method.Name == nameof(CM_PlayerLobbyBar.ShowBoosterImplantSelect)) yield return method;
        }

        [HarmonyPrefix]
        private static void Prefix(CM_PlayerLobbyBar __instance)
        {
            try
            {
                var picker = Current;
                if (picker != null && !picker._opening) picker.TakeDown(false);
                if (__instance.m_popupScrollWindow != null) DropDeadItems(__instance.m_popupScrollWindow);
            }
            catch (Exception error) { Plugin.Error("ui-preparing-games-own-pop-up-failed", "Preparing the game's own pop-up failed: " + error); }
        }
    }

}
