using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// Buttons on the top menu bar. Every page has its own <c>CM_MenuBar</c>; on each, every registered button is an item spawned
/// from the bar's <c>m_menuBarItemPrefab</c> and set up the way the game sets up its own (<c>SetAnchor(TopLeft)</c>,
/// <c>Setup(m_root, page)</c>, <c>SetXOffset</c>), placed after the game's left-hand buttons each time the game lays them out
/// (<c>UpdateMenuOptions</c>). A page button opens a window spawned from the bar's <c>m_popupWindowPrefab</c>, the window the game
/// uses for its exit and quit questions, set up as the game sets those up (<c>Setup</c>, <c>SetupFromButton</c> with the bar as
/// controller, <c>ForcePopupLayer</c>). A click outside it reaches the bar's <c>SetPopupVisible(false)</c>, which closes it.
/// </summary>
internal static class MenuBar
{
    /// <summary>The size of a page window, in menu bar units (about pixels at 1440p).</summary>
    internal static readonly Vector2 PageSize = new(1400f, 720f);

    private const float PageY = -90f, ContentPadding = 24f, MaxButtonWidth = 560f;

    private static readonly Registry<MenuBarButton> Registered = new();
    private static readonly Dictionary<IntPtr, Bar> Bars = new();
    private static readonly List<IntPtr> Gone = new();
    private static int _version;
    private static bool _opening;

    private sealed class Bar
    {
        internal CM_MenuBar Native = null!;
        internal int Version = -1;
        internal int Width, Height;
        internal readonly Dictionary<string, Entry> Entries = new();
    }

    private sealed class Entry
    {
        internal MenuBarButton Registration = null!;
        internal CM_MenuBarItem Item = null!;
        internal IntPtr Route;
        internal MenuBarPage? Page;
        internal int ClosedFrame = -1;
        internal MenuBarTooltip? Tip;
        internal bool TipTried;
    }

    internal static MenuBarButton? Add(MenuBarButton button)
    {
        var problem = Registered.Add(button.Id, button);
        if (problem != null)
        {
            Plugin.Error("ui-menu-bar-button-not-added", "Menu bar button not added: " + problem + ".");
            return null;
        }
        _version++;
        if (!PatchGroups.Has(PatchGroup.MenuBar))
        {
            PatchGroups.Ensure(PatchGroup.MenuBar);
            // A bar the game set up before the first registration is tracked now, so this button reaches it too.
            foreach (var native in UnityEngine.Object.FindObjectsOfType<CM_MenuBar>()) Track(native);
        }
        if (button.BuildScreen != null) PatchGroups.Ensure(PatchGroup.MenuScreen);
        return button;
    }

    internal static void Remove(MenuBarButton button)
    {
        if (!Registered.Remove(button.Id, button)) return;
        _version++;
    }

    internal static void Changed() => _version++;

    /// <summary>The language changed: every title is written again on the next update, and every built page is thrown away so it is
    /// built again in the new language; a page that was open is reopened at once.</summary>
    internal static void LanguageChanged()
    {
        _version++;
        MenuScreens.Rebuild();
        foreach (var bar in Bars.Values)
        {
            if (bar.Native == null || bar.Native.WasCollected) continue;
            foreach (var entry in bar.Entries.Values)
            {
                if (entry.Page == null) continue;
                var open = entry.Page.IsOpen;
                entry.Page.Destroy();
                entry.Page = null;
                if (!open) continue;
                try { Open(bar, entry.Registration, entry); }
                catch (Exception error) { Plugin.Error("ui-menu-bar-page", "Menu bar page '" + entry.Registration.Id + "' could not be reopened in the new language: " + error); }
            }
        }
    }

    /// <summary>Per frame: brings every known bar up to date after a registration, removal or title change.</summary>
    internal static void Update()
    {
        MenuScreens.Update();
        if (Bars.Count == 0) return;
        Gone.Clear();
        foreach (var (pointer, bar) in Bars)
        {
            if (bar.Native == null || bar.Native.WasCollected) { Gone.Add(pointer); continue; }
            try
            {
                if (!bar.Native.gameObject.activeInHierarchy ||
                    (bar.Native.m_basePage != null && !bar.Native.m_basePage.gameObject.activeInHierarchy)) CloseAll(bar);
                if (bar.Version != _version) Sync(bar);
                else if (bar.Width != ForgeCore.Frame.ScreenWidth || bar.Height != ForgeCore.Frame.ScreenHeight) Layout(bar);
                foreach (var entry in bar.Entries.Values)
                {
                    if (entry.Page?.IsOpen == true) CenterWindow(bar.Native, entry.Page.Window);
                    Tooltip(entry);
                }
            }
            catch (Exception error) { Plugin.Error("ui-menu-bar-buttons-failed-update", "Menu bar buttons failed to update: " + error); }
        }
        foreach (var pointer in Gone)
        {
            foreach (var entry in Bars[pointer].Entries.Values) Destroy(entry);
            Bars.Remove(pointer);
        }
    }

    /// <summary>A bar hid its pop-ups (a click outside one, or the game opening another): windows of the kit's own made with
    /// <see cref="CreateWindow"/> close with them (the confirmation window).</summary>
    internal static event Action<CM_MenuBar>? PopupsHidden;

    /// <summary>A menu bar on screen now (the page the player is on), or null.</summary>
    internal static CM_MenuBar? ActiveBar()
    {
        foreach (var bar in Bars.Values)
            if (bar.Native != null && !bar.Native.WasCollected && bar.Native.gameObject.activeInHierarchy &&
                (bar.Native.m_basePage == null || bar.Native.m_basePage.gameObject.activeInHierarchy)) return bar.Native;
        return null;
    }

    private static Bar Track(CM_MenuBar native)
    {
        if (!Bars.TryGetValue(native.Pointer, out var bar)) Bars[native.Pointer] = bar = new Bar { Native = native };
        return bar;
    }

    /// <summary>Creates the items for new registrations, removes those of removed ones, writes the titles and lays them out.</summary>
    private static void Sync(Bar bar)
    {
        var version = _version;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var buttons = Registered.Ordered;
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            keep.Add(button.Id);
            if (!bar.Entries.TryGetValue(button.Id, out var entry) || !ReferenceEquals(entry.Registration, button) ||
                entry.Item == null || entry.Item.WasCollected)
            {
                if (entry != null)
                {
                    Destroy(entry);
                    bar.Entries.Remove(button.Id);
                }
                bar.Entries[button.Id] = entry = Create(bar, button);
            }
            var title = button.ShownTitle;
            entry.Item.SetText(RichText.Plain(title));
            entry.Item.CorrectSizeToText(0f, MaxButtonWidth);
            entry.Page?.SetTitle(title);
        }
        var removed = new List<string>();
        foreach (var (id, entry) in bar.Entries)
            if (!keep.Contains(id))
            {
                removed.Add(id);
                Destroy(entry);
            }
        foreach (var id in removed) bar.Entries.Remove(id);
        Layout(bar);
        bar.Version = version;
    }

    /// <summary>The button's tooltip while the pointer is on it (the canvas is made at the first hover of a button that has one).</summary>
    private static void Tooltip(Entry entry)
    {
        var item = entry.Item;
        if (item == null || item.WasCollected) return;
        var hover = item.m_isHovering && entry.Page?.IsOpen != true;
        if (entry.Tip == null)
        {
            if (!hover || entry.TipTried || entry.Registration.Tooltip == null) return;
            entry.TipTried = true;
            entry.Tip = MenuBarTooltip.On(item);
            if (entry.Tip == null) return;
        }
        entry.Tip.Show(hover ? entry.Registration.ShownTooltip : null, hover);
    }

    private static void Destroy(Entry entry)
    {
        entry.Tip?.Destroy();
        Input.Unroute(entry.Route);
        entry.Page?.Destroy();
        if (entry.Item != null && !entry.Item.WasCollected) Object.Destroy(entry.Item.gameObject);
    }

    private static Entry Create(Bar bar, MenuBarButton button)
    {
        var native = bar.Native;
        var prefab = native.m_menuBarItemPrefab ?? throw new InvalidOperationException("the menu bar has no item prefab");
        var go = Object.Instantiate(prefab, native.transform);
        try
        {
            go.name = "ForgeUI.MenuBar." + button.Id;
            NativeUi.StripLocalization(go);
            var item = go.GetComponent<CM_MenuBarItem>() ?? throw new InvalidOperationException("the menu bar item prefab has no CM_MenuBarItem");
            item.SetAnchor(GuiAnchor.TopLeft, true);
            item.Setup(native.m_root, eCM_MenuPage.CMP_NONE);
            item.m_alphaTextOnHover = true;
            item.CheckAndSetTextAlpha();
            var entry = new Entry { Registration = button, Item = item };
            entry.Route = Input.Route(item, () => Click(bar, button, entry), null);
            return entry;
        }
        catch
        {
            go.SetActive(false);
            Object.Destroy(go);
            throw;
        }
    }

    /// <summary>Places the items after the game's visible left-hand buttons, stepping the way the game steps its own, and hides
    /// those that would reach the game's right-hand group.</summary>
    private static void Layout(Bar bar)
    {
        var native = bar.Native;
        var width = ForgeCore.Frame.ScreenWidth;
        var height = ForgeCore.Frame.ScreenHeight;
        var spacing = native.m_buttonLeftSpacing;
        var start = 0f;
        foreach (var left in new[] { native.m_settings, native.m_rundown, native.m_loadout })
            if (left != null && left.gameObject.activeSelf)
                start = Math.Max(start, left.transform.localPosition.x + Math.Max(left.GetSize().x, spacing));
        var limit = float.PositiveInfinity;
        foreach (var right in new[] { native.m_exitLobbyAndExpedition, native.m_returnToLobby, native.m_quitGame })
            if (right != null && right.gameObject.activeSelf) limit = Math.Min(limit, LeftEdge(native.transform, right.gameObject));
        if (native.m_expIcon != null && native.m_expIcon.gameObject.activeSelf)
            limit = Math.Min(limit, LeftEdge(native.transform, native.m_expIcon.gameObject));
        if (native.m_expText != null && native.m_expText.gameObject.activeInHierarchy)
            limit = Math.Min(limit, LeftEdge(native.transform, native.m_expText.gameObject));
        // Menu status instances live below this alignment, independently of the expedition icon.
        if (native.m_statusAlign != null && native.m_statusAlign.gameObject.activeInHierarchy)
            limit = Math.Min(limit, LeftEdge(native.transform, native.m_statusAlign.gameObject));

        var entries = new List<Entry>();
        var buttons = Registered.Ordered;
        for (var i = 0; i < buttons.Count; i++)
            if (bar.Entries.TryGetValue(buttons[i].Id, out var entry) && entry.Item != null && !entry.Item.WasCollected) entries.Add(entry);
        var widths = new List<float>();
        foreach (var entry in entries) widths.Add(entry.Item.GetSize().x);
        var placed = MenuBarLayout.Place(start, spacing, widths, limit);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var item = entry.Item;
            var x = placed[i];
            if (x == null)
            {
                Plugin.Error("ui-menu-bar-button-does-not-fit", "Menu bar button '" + item.gameObject.name + "' does not fit before the game's status and right-hand buttons and is not shown.");
                entry.Page?.Close();
                item.SetVisible(false);
                continue;
            }
            item.SetVisible(true);
            item.SetXOffset(x.Value);
        }
        bar.Width = width;
        bar.Height = height;
    }

    /// <summary>The left edge, in the bar's units, of what is drawn or clickable under <paramref name="go"/>.</summary>
    private static float LeftEdge(Transform bar, GameObject go)
    {
        var edge = float.PositiveInfinity;
        foreach (var renderer in go.GetComponentsInChildren<Renderer>(false))
            if (renderer.enabled) edge = Math.Min(edge, bar.InverseTransformPoint(renderer.bounds.min).x);
        foreach (var collider in go.GetComponentsInChildren<Collider2D>(false))
            if (collider.enabled) edge = Math.Min(edge, bar.InverseTransformPoint(collider.bounds.min).x);
        return edge;
    }

    private static void Click(Bar bar, MenuBarButton button, Entry entry)
    {
        if (button.BuildScreen != null)
        {
            Notify(button.Id, button.OnClick);
            if (!MenuScreens.Open(button)) Plugin.Error("ui-menu-bar-screen", "Menu bar screen '" + button.Id + "' could not be opened: the main menu has no settings page.");
            return;
        }
        if (button.Build == null)
        {
            try { button.OnClick!(); }
            catch (Exception error) { Plugin.Error("ui-menu-bar-button-click-failed", "Menu bar button '" + button.Id + "' click failed: " + error); }
            return;
        }
        Notify(button.Id, button.OnClick);
        if (entry.Page != null && entry.Page.IsOpen) { entry.Page.Close(); return; }
        // A click outside an open page closes it before the press reaches this button; that click must not reopen it.
        if (entry.ClosedFrame == Time.frameCount) return;
        try { Open(bar, button, entry); }
        catch (Exception error) { Plugin.Error("ui-menu-bar-menu-bar-page", "Menu bar page '" + button.Id + "' could not be opened: " + error); }
    }

    private static void Open(Bar bar, MenuBarButton button, Entry entry)
    {
        var native = bar.Native;
        _opening = true;
        try { native.SetPopupVisible(false); }
        finally { _opening = false; }
        CloseAll(bar);
        if (entry.Page == null)
        {
            var page = new MenuBarPage(button.Id, button.Mod.Texts, CreateWindow(native, RichText.Plain(button.ShownTitle), PageSize, out var root, out var size), root, size,
                () => Closed(entry), button.OnClick)
            { Bar = native };
            try { button.Build!(page); }
            catch
            {
                page.Destroy();
                throw;
            }
            entry.Page = page;
        }
        entry.Page.Show();
        CenterWindow(native, entry.Page.Window);
        entry.Item.m_alphaTextOnHover = false;
        if (entry.Item.m_text != null) entry.Item.m_text.alpha = 1f;
    }

    /// <summary>Opens <paramref name="button"/>'s page on the menu bar on screen now (<see cref="MenuBarButton.Open"/>).</summary>
    internal static bool OpenNow(MenuBarButton button)
    {
        if (button.Build == null) return false;
        var native = ActiveBar();
        if (native == null) return false;
        var bar = Track(native);
        if (bar.Version != _version) Sync(bar);
        if (!bar.Entries.TryGetValue(button.Id, out var entry)) return false;
        if (entry.Page?.IsOpen == true) return true;
        try { Open(bar, button, entry); return true; }
        catch (Exception error) { Plugin.Error("ui-menu-bar-menu-bar-page", "Menu bar page '" + button.Id + "' could not be opened: " + error); return false; }
    }

    /// <summary>A page's own click callback (its button or close box), for the author's click sound.</summary>
    internal static void Notify(string id, Action? clicked)
    {
        if (clicked == null) return;
        try { clicked(); }
        catch (Exception error) { Plugin.Error("ui-menu-bar-page-click-callback-failed", "Menu bar page '" + id + "' click callback failed: " + error); }
    }

    private static void Closed(Entry entry)
    {
        entry.ClosedFrame = Time.frameCount;
        if (entry.Item == null || entry.Item.WasCollected) return;
        entry.Item.m_alphaTextOnHover = true;
        entry.Item.CheckAndSetTextAlpha();
    }

    /// <summary>A menu screen opened or closed: its button stays lit on every bar while it is open, as the game's own page buttons do.</summary>
    internal static void ScreenChanged()
    {
        foreach (var bar in Bars.Values)
        {
            if (bar.Native == null || bar.Native.WasCollected) continue;
            foreach (var entry in bar.Entries.Values)
            {
                if (entry.Registration.BuildScreen == null || entry.Item == null || entry.Item.WasCollected) continue;
                var open = MenuScreens.IsOpen(entry.Registration);
                entry.Item.m_alphaTextOnHover = !open;
                entry.Item.CheckAndSetTextAlpha();
                if (open && entry.Item.m_text != null) entry.Item.m_text.alpha = 1f;
            }
        }
    }

    /// <summary>No bar is on screen (the player is in play): closes every open bar page and Forge screen.</summary>
    internal static void Hidden()
    {
        MenuScreens.Update();
        foreach (var bar in Bars.Values)
            if (bar.Native != null && !bar.Native.WasCollected)
            {
                try { CloseAll(bar); }
                catch (Exception error) { Plugin.Error("ui-closing-menu-bar-pages-failed", "Closing menu bar pages failed: " + error); }
            }
    }

    private static void CloseAll(Bar bar)
    {
        foreach (var entry in bar.Entries.Values)
            if (entry.Page != null && entry.Page.IsOpen) entry.Page.Close();
    }

    /// <summary>The page window, set up the way the game sets up its exit and quit windows, centred under the menu bar.</summary>
    internal static CM_ScrollWindow CreateWindow(CM_MenuBar native, string title, Vector2 windowSize, out Transform root, out Vector2 size)
    {
        var prefab = native.m_popupWindowPrefab ?? throw new InvalidOperationException("the menu bar has no pop-up window prefab");
        var go = Object.Instantiate(prefab, native.transform);
        CM_ScrollWindow? window = null;
        try
        {
            go.name = "ForgeUI.MenuBarPage";
            NativeUi.StripLocalization(go);
            window = go.GetComponent<CM_ScrollWindow>() ?? throw new InvalidOperationException("the pop-up window prefab has no CM_ScrollWindow");
            window.Setup(native.m_root);
            window.SetupFromButton(new iCellMenuPopupController(native.Pointer), native.m_basePage);
            window.SetAnchor(GuiAnchor.TopLeft, true);
            window.SetPosition(new Vector2(300f, PageY));
            window.SetSize(windowSize);
            window.SetHeader(title);
            window.ForcePopupLayer(true);
            window.SetVisible(true);

            var t = window.transform;
            var frame = window.m_background;

            var content = new GameObject("ForgeUI.PageContent") { layer = go.layer };
            content.transform.SetParent(t, false);
            size = windowSize - new Vector2(2f * ContentPadding, 2f * ContentPadding);
            if (frame != null)
            {
                var min = t.InverseTransformPoint(frame.bounds.min);
                var max = t.InverseTransformPoint(frame.bounds.max);
                var depth = window.m_contentContainer != null ? t.InverseTransformPoint(window.m_contentContainer.transform.position).z : 0f;
                content.transform.localPosition = new Vector3(Math.Min(min.x, max.x) + ContentPadding, Math.Max(min.y, max.y) - ContentPadding, depth);
                size = new Vector2(Math.Abs(max.x - min.x) - 2f * ContentPadding, Math.Abs(max.y - min.y) - 2f * ContentPadding);
            }
            root = content.transform;
            window.SetVisible(false);

            return window;
        }
        catch
        {
            if (window != null) MenuBarPage.RemoveListener(native.m_basePage, window.Pointer);
            go.SetActive(false);
            Object.Destroy(go);
            throw;
        }
    }

    private static void CenterWindow(CM_MenuBar native, CM_ScrollWindow window)
    {
        var span = ButtonSpan(native);
        var frame = window.m_background;
        if (span == null || frame == null) return;
        var a = native.transform.InverseTransformPoint(frame.bounds.min).x;
        var b = native.transform.InverseTransformPoint(frame.bounds.max).x;
        var shift = (span.Value.Left + span.Value.Right) / 2f - (a + b) / 2f;
        var t = window.transform;
        t.localPosition += t.parent.InverseTransformVector(native.transform.TransformVector(new Vector3(shift, 0f, 0f)));
    }

    private static (float Left, float Right)? ButtonSpan(CM_MenuBar native)
    {
        var left = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        void Include(CM_MenuBarItem? item)
        {
            if (item == null || !item.gameObject.activeSelf || item.m_collider == null) return;
            left = Math.Min(left, native.transform.InverseTransformPoint(item.m_collider.bounds.min).x);
            right = Math.Max(right, native.transform.InverseTransformPoint(item.m_collider.bounds.max).x);
        }
        Include(native.m_settings);
        Include(native.m_rundown);
        Include(native.m_loadout);
        Include(native.m_exitLobbyAndExpedition);
        Include(native.m_returnToLobby);
        Include(native.m_quitGame);
        return left < right ? (left, right) : null;
    }

    /// <summary>The game set up a page's menu bar: its buttons are added.</summary>
    [PatchGroup(PatchGroup.MenuBar)]
    [HarmonyPatch(typeof(CM_MenuBar), nameof(CM_MenuBar.Setup))]
    private static class BarSetup
    {
        [HarmonyPostfix]
        private static void Postfix(CM_MenuBar __instance)
        {
            try
            {
                // Tracked even with nothing registered, so a later registration still reaches this bar.
                var bar = Track(__instance);
                if (Registered.Ordered.Count > 0) Sync(bar);
                else bar.Version = _version;
            }
            catch (Exception error) { Plugin.Error("ui-adding-menu-bar-buttons-failed", "Adding menu bar buttons failed: " + error); }
        }
    }

    /// <summary>The game laid out its buttons (it does this again whenever lobby or expedition state changes).</summary>
    [PatchGroup(PatchGroup.MenuBar)]
    [HarmonyPatch(typeof(CM_MenuBar), nameof(CM_MenuBar.UpdateMenuOptions))]
    private static class BarLayout
    {
        [HarmonyPostfix]
        private static void Postfix(CM_MenuBar __instance)
        {
            if (Registered.Ordered.Count == 0) return;
            try
            {
                var bar = Track(__instance);
                if (bar.Version != _version) Sync(bar);
                else Layout(bar);
            }
            catch (Exception error) { Plugin.Error("ui-placing-menu-bar-buttons-failed", "Placing menu bar buttons failed: " + error); }
        }
    }

    /// <summary>The bar hides its pop-ups (a click outside one): pages close with them. The game's only call of the bar's
    /// <c>SetPopupVisible</c> is <c>CM_PopupOverlay.OnBtnPressAnywhere</c> (to its controller, with false): when the popup is enabled
    /// and no item under the cursor is the popup or one of its child items (compared by <c>ID</c>), it hands the press to its
    /// controller. This reads the same condition after the game's body, so a press whose controller is a bar and which the popup
    /// did not hit is the bar hiding its pop-ups. <c>SetPopupVisible</c> is not patched (its entry starts with a short conditional
    /// jump, which the detour backend relocates to the wrong target, so ForgeCore refuses it), and neither is
    /// <c>CM_ScrollWindow.SetVisible</c> (the compiler gave it the same native body as <c>RectTransformComp.SetVisible</c>, so a
    /// patch there also runs for every other component).</summary>
    [PatchGroup(PatchGroup.MenuBar)]
    [HarmonyPatch(typeof(CM_PopupOverlay), nameof(CM_PopupOverlay.OnBtnPressAnywhere))]
    private static class BarPopups
    {
        [HarmonyPostfix]
        private static void Postfix(CM_PopupOverlay __instance, Il2CppSystem.Collections.Generic.List<iCellMenuCursorItem> hoverItems)
        {
            var native = __instance.m_controller?.TryCast<CM_MenuBar>();
            if (native == null || _opening || !PressedOutside(__instance, hoverItems)) return;
            try { PopupsHidden?.Invoke(native); }
            catch (Exception error) { Plugin.Error("ui-window-menu-bar-failed-close", "A window on the menu bar failed to close: " + error); }
            if (!Bars.TryGetValue(native.Pointer, out var bar)) return;
            try { CloseAll(bar); }
            catch (Exception error) { Plugin.Error("ui-closing-menu-bar-pages-failed", "Closing menu bar pages failed: " + error); }
        }

        private static bool PressedOutside(CM_PopupOverlay popup, Il2CppSystem.Collections.Generic.List<iCellMenuCursorItem> hover)
        {
            if (!popup.m_enabled || hover == null) return false;
            var children = popup.m_childItems;
            for (var i = 0; i < hover.Count; i++)
            {
                var id = hover[i].ID;
                if (id == popup.ID) return false;
                if (children != null)
                    foreach (var child in children)
                        if (child.ID == id) return false;
            }
            return true;
        }
    }
}
