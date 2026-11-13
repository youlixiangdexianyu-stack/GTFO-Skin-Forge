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
        _opening = false;
        DrawSlots();
        DrawRows();
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
                if (header != null) header.transform.position += new Vector3(0f, lift, 0f);
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
            var go = new GameObject("ForgeUI.ColumnHitArea") { layer = _column.layer };
            go.transform.SetParent(_column.transform, false);
            _columnHitArea = go.AddComponent<CM_Item>();
            _columnHitArea.m_collider = go.AddComponent<BoxCollider2D>();
            _columnHitArea.Setup();
            _columnHitArea.m_onBtnPress = new UnityEvent();
            _columnRoute = Input.Route(_columnHitArea, () => { }, null);
            _columnHitArea.ForcePopupLayer(true, null);
            _window.AddNonContentItem(_columnHitArea);
        }
        // Behind the buttons, in front of the native click-outside catcher.
        _columnHitArea.transform.localPosition = new Vector3((left + right) / 2f, (bottom + top) / 2f, 1f);
        _columnHitArea.m_collider.size = new Vector2(width, height);
        if (_columnSkin == null)
        {
            var label = _slots.Count > 0 ? _slots[0].Item.GetComponentInChildren<TextMeshPro>(true) : null;
            _columnSkin = new PickerColumnSkin(_decoration.transform, background, label);
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
        for (var i = 0; i < _slots.Count; i++)
            _slots[i].Skin?.Draw(_tile, Source.SlotCard(i, i == _slot), i + 1, i == _slot);
    }

    // ---- building ------------------------------------------------------------------------------------------

    /// <summary>The tabs. The game calls a header's callback with the header item's cell ID, not the key given to
    /// <c>AddHeader</c> (its own callbacks ignore the argument), so each tab gets a closure over its own index.</summary>
    private void Tabs()
    {
        _window.ResetHeaders();
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
        _window.SetContentItems(Content(_entryCards.Select(e => e.Card)), ContentPadding);
    }

    private void AddEntry(int entry, string name)
    {
        var card = Card(_window.transform, name);
        var route = Input.Route(card, () =>
        {
            Clicked(PickerClick.Entry);
            Pick(entry);
        }, inside =>
        {
            if (!inside || _disposed || _focus == entry) return;
            _focus = entry;
            _warning = null;
            try
            {
                DrawRows();
                Info();
            }
            catch (Exception error) { Plugin.Error("ui-picker-source-failed-describe-cards", "Picker source " + Source.GetType().FullName + " failed to describe its cards: " + error); }
        });
        _entryCards.Add((card, entry, PickerRowSkin.On(card), route));
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
        _slot = slot;
        _focus = -1;
        _warning = null;
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
        Refresh();
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
            var entries = _tab < 0 ? Array.Empty<PickerCard>() : Source.Entries(_slot, _tab);
            var clear = _tab < 0 ? null : Source.ClearCard;
            var entriesChanged = tabsChanged || _entriesTab != _tab || _hasClear != (clear != null) ||
                                 _entryCards.Count != entries.Count + (clear == null ? 0 : 1);
            if (entriesChanged)
            {
                _focus = -1;
                BuildEntries(entries.Count, clear != null);
            }
            if (_slot < 0 || _tab < 0 || _focus < -1 || _focus >= entries.Count) _focus = -1;
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
            DrawRows(entries, clear);
            if (tabsChanged || entriesChanged || slotsChanged)
            {
                if (_tab >= 0) _window.SelectHeader(_tab);
                _window.UpdatePopupChildItems();
                SettleLayout();
            }
            _tabSkin.Draw(_window, _tabs, _tab);
            Info();
        }
        catch (Exception error) { Plugin.Error("ui-picker-source-failed-describe-cards", "Picker source " + Source.GetType().FullName + " failed to describe its cards: " + error); }
    }

    private void DrawRows() => DrawRows(_tab < 0 ? Array.Empty<PickerCard>() : Source.Entries(_slot, _tab), _tab < 0 ? null : Source.ClearCard);

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
            skin.Draw(data, entry == _focus, listRight);
            grew |= skin.Grew;
        }
        if (grew) PickerSkin.Clip(_window);
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
        var info = _slot < 0 || _tab < 0 ? new PickerInfo() : Source.Info(_slot, _tab, _focus);
        _detail?.Draw(info, _warning);
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
                }
                if (_window.InfoBox?.m_infoMainIcon != null) _window.InfoBox.m_infoMainIcon.enabled = true;
                _window.transform.localScale = _windowScale;
                _window.transform.localPosition = _windowPosition;
            }
        }
        catch (Exception error) { Plugin.Error("ui-emptying-pop-up-lists-after-picker", "Emptying the pop-up lists after the picker failed: " + error); }
        // The tabs and the details box are the game's and stay: what was drawn on them goes now.
        try
        {
            _tabSkin.Destroy();
            _detail?.Destroy();
            _detail = null;
        }
        catch (Exception error) { Plugin.Error("ui-removing-pickers-tabs-details-failed", "Removing the picker's tabs and details failed: " + error); }
        foreach (var (item, _, route) in _slots) Release(item, route);
        foreach (var (card, _, _, route) in _entryCards) Release(card, route);
        _slots.Clear();
        _entryCards.Clear();
        Input.Unroute(_columnRoute);
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
