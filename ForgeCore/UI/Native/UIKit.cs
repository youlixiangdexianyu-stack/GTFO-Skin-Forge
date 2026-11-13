using System;
using System.Collections.Generic;
using CellMenu;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// The public entry point. Declare <c>[BepInDependency("NAinfini.ForgeCore")]</c>, take your mod's handle with <see cref="Mod"/> and
/// register through it from your plugin's <c>Load</c> (or later). Everything runs on the main thread. Exceptions thrown by your
/// callbacks are logged with your ID and never reach the game.
/// </summary>
public static partial class UIKit
{
    private static readonly Dictionary<string, UIKitMod> Mods = new(StringComparer.Ordinal);

    /// <summary>Your mod's handle: every registration through it is named <c>modId/name</c>, so names never clash between mods. Pass
    /// your plugin GUID. Calling it again with the same ID returns the same handle (one mod may register from several classes).</summary>
    /// <exception cref="ArgumentException">The ID is empty, longer than 64 characters or has a space, a control character or '/'.</exception>
    public static UIKitMod Mod(string modId)
    {
        var problem = ModIds.Problem(modId, "mod ID");
        if (problem != null) throw new ArgumentException("ForgeUI: " + problem + ".", nameof(modId));
        if (!Mods.TryGetValue(modId, out var mod)) Mods.Add(modId, mod = new UIKitMod(modId));
        return mod;
    }

    /// <summary>The soft glow every lit HUD piece is drawn with (blur radius 5 px, 55 %), shared by every mod; the player's
    /// <c>Screen / Glow</c> multiplies it. Change it for a whole look (a cockpit), not per element: turn an element's off with
    /// <see cref="HudElement.Effects"/>.</summary>
    public static HudGlowSettings HudGlow { get; } = new();

    /// <summary>The faint 1 px red/blue colour fringe every lit HUD piece is drawn with (16 %), shared by every mod; the player's
    /// <c>Screen / Fringe</c> turns it off. <see cref="HudFringeSettings.Pulse"/> strengthens it for a moment.</summary>
    public static HudFringeSettings HudFringe { get; } = new();

    /// <summary>The kit's own handle, for the shared banner and notices elements and the picker's own words.</summary>
    internal static UIKitMod Kit => Mod(Plugin.Id);

    private static readonly Dictionary<string, UITexts> Folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current language code (for example <c>zh</c> or <c>en</c>): the player's <c>Language</c> setting, or the game's
    /// language when it is <c>Auto</c>.</summary>
    public static string Language => UITexts.Language;

    /// <summary>Raised on the main thread after <see cref="Language"/> changed, once ForgeUI has set its own buttons, pages and
    /// picker again. Subscribe when you put texts somewhere ForgeUI does not draw (for example the game's own gear names).</summary>
    public static event Action? LanguageChanged;

    /// <summary>The language files (<c>&lt;code&gt;.forge</c>) in <paramref name="folder"/>, for texts that do not belong to a plugin's
    /// own <c>lang</c> folder (for example a content pack's). One set per folder; read on first use.</summary>
    public static UITexts Texts(string folder)
    {
        if (string.IsNullOrEmpty(folder)) throw new ArgumentException("A language folder needs a path.", nameof(folder));
        var full = System.IO.Path.GetFullPath(folder);
        if (!Folders.TryGetValue(full, out var texts)) Folders.Add(full, texts = ForgeUI.Language.InFolder(full, full));
        return texts;
    }

    /// <summary>The language files of the mod that registered <paramref name="fullId"/> (<c>mod/name</c>).</summary>
    internal static UITexts TextsOf(string fullId) => Mod(ModIds.Split(fullId).Mod).Texts;

    /// <summary>The language changed: menu bar titles and pages are set again, the mods' listeners run, the gear names already drawn follow (<see cref="GearName(UITexts, string, string)"/>), then the open picker asks its
    /// source for everything again. Lobby buttons and HUD texts follow on their next frame by themselves.</summary>
    internal static void LanguageSwitched()
    {
        try { MenuBar.LanguageChanged(); }
        catch (Exception error) { Plugin.Error("ui-menu-bar-pages-could-not-change", "Menu bar pages could not change language: " + error); }
        var listeners = LanguageChanged?.GetInvocationList();
        if (listeners != null)
            foreach (var listener in listeners)
            {
                try { ((Action)listener)(); }
                catch (Exception error) { Plugin.Error("ui-language-change-listener-failed", "A language change listener (" + listener.Method.DeclaringType?.FullName + "." + listener.Method.Name + ") failed: " + error); }
            }
        try { GearNames.LanguageChanged(); }
        catch (Exception error) { Plugin.Error("ui-gear-names-screen-could-not-change", "Gear names on screen could not change language: " + error); }
        try { RefreshPicker(); }
        catch (Exception error) { Plugin.Error("ui-picker-could-not-change-language", "The picker could not change language: " + error); }
    }
    /// <summary>
    /// What a button that opens a picker should call: closes the picker when <paramref name="source"/> is showing (or was closed
    /// by this same click landing outside it), otherwise opens it.
    /// </summary>
    public static void TogglePicker(CM_PlayerLobbyBar bar, PickerSource source) => Picker.Toggle(bar, source);

    /// <summary>Closes the open picker, if any.</summary>
    public static void ClosePicker() => Picker.Close();

    /// <summary>True while <paramref name="source"/> is the one shown.</summary>
    public static bool IsShowing(PickerSource source) => Picker.Current?.Source == source;

    /// <summary>Refreshes the open picker's cards, tabs and slot count after source data changes. Changed tabs select the first
    /// tab; invalid slots are clamped, and list structure changes reset focus. Empty slots or tabs show no details and accept
    /// no picks.</summary>
    public static void RefreshPicker() => Picker.Current?.Refresh();

}

/// <summary>
/// One mod's registrations (see <see cref="UIKit.Mod"/>). Every <c>Add…</c> takes a name unique inside your mod; the full ID is
/// <c>modId/name</c>. A name that is empty, has a space or '/' or is already taken is refused: the call returns null and logs an error.
/// </summary>
public sealed partial class UIKitMod
{
    internal UIKitMod(string id) => Id = id;

    /// <summary>The mod ID.</summary>
    public string Id { get; }

    private UITexts? _texts;

    /// <summary>Your language files: <c>lang/&lt;code&gt;.forge</c> beside your plugin DLL (so the mod ID must be your plugin GUID),
    /// read and checked the first time a text is asked for. Every <see cref="UIText.Key"/> you register is looked up here.</summary>
    public UITexts Texts => _texts ??= ForgeUI.Language.ForPlugin(Id);

    /// <summary>The text of <paramref name="key"/> in the current language (see <see cref="UITexts.Text(string)"/>): a cached string,
    /// cheap enough for every frame.</summary>
    public string Text(string key) => Texts.Text(key);

    /// <summary>The text of <paramref name="key"/> in the current language with its <c>{0}</c>, <c>{1}</c>… filled from
    /// <paramref name="args"/>; makes a new string each call.</summary>
    public string Text(string key, params object?[] args) => Texts.Text(key, args);

    /// <summary>The full ID of <paramref name="name"/>, or null (logged) when the name cannot be used.</summary>
    internal string? Full(string name, string what)
    {
        var problem = ModIds.Problem(name, what + " name");
        if (problem == null) return ModIds.Join(Id, name);
        Plugin.Error("ui-not-added", Id + ": " + what + " not added: " + problem + ".");
        return null;
    }

    /// <summary>
    /// Adds a button to the lobby player bar, under the game's APPAREL button. Buttons from all mods stack downwards in the order
    /// of their full IDs.
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Loadout</c>.</param>
    /// <param name="title">Shown on the button, upper case to match the game; a plain string or a <see cref="UIText.Key"/>.</param>
    /// <param name="onClick">Called with the bar whose button was pressed.</param>
    /// <param name="note">Optional small grey text after the title (for example <c>2/4</c>); read every frame while shown.</param>
    /// <param name="visible">Optional; by default the button shows only on the local player's own bar.</param>
    public LobbyButton? AddLobbyButton(string name, UIText title, Action<CM_PlayerLobbyBar> onClick, Func<string>? note = null,
        Func<CM_PlayerLobbyBar, bool>? visible = null)
    {
        if (onClick == null) throw new ArgumentNullException(nameof(onClick));
        var id = Full(name, "lobby button");
        return id == null ? null : LobbyButtons.Add(new LobbyButton(id, this, title ?? UIText.Empty, onClick, note, visible));
    }

    /// <summary>
    /// Adds a button to the top menu bar of every page, after the game's left-hand buttons (SETTINGS, RUNDOWN, LOADOUT). Buttons
    /// from all mods follow in the order of their full IDs.
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Help</c>.</param>
    /// <param name="title">Shown on the button, upper case to match the game.</param>
    /// <param name="onClick">Called when the button is pressed, on whichever page.</param>
    public MenuBarButton? AddMenuBarButton(string name, UIText title, Action onClick)
    {
        if (onClick == null) throw new ArgumentNullException(nameof(onClick));
        var id = Full(name, "menu bar button");
        return id == null ? null : MenuBar.Add(new MenuBarButton(id, this, title ?? UIText.Empty, onClick, null, null));
    }

    /// <summary>
    /// Adds a menu bar button (as <see cref="AddMenuBarButton"/>) that opens and closes a page: a window under the menu bar with
    /// <paramref name="title"/> as its heading, closed by its close box, a click outside it or the button again. Its content is
    /// yours: <paramref name="build"/> is called the first time the page is opened from a page's menu bar (so once per game page
    /// it is opened on) and fills <see cref="MenuBarPage.Root"/>, for example with <see cref="MenuBarPage.AddText"/>. When the language
    /// changes the page is built again, so text keys given to the page show in the new language.
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Page</c>.</param>
    /// <param name="title">Shown on the button and as the page heading.</param>
    /// <param name="build">Fills the page the first time it opens on a game page.</param>
    /// <param name="clicked">Optional; called on every press of the page's button or close box, for example to play your click
    /// sound (the library has no audio).</param>
    public MenuBarButton? AddMenuBarPage(string name, UIText title, Action<MenuBarPage> build, Action? clicked = null)
    {
        if (build == null) throw new ArgumentNullException(nameof(build));
        var id = Full(name, "menu bar page");
        return id == null ? null : MenuBar.Add(new MenuBarButton(id, this, title ?? UIText.Empty, clicked, build, null));
    }

    /// <summary>
    /// Adds a menu bar button that opens a full screen on the game's settings page, built from that page's own pieces (see
    /// <see cref="MenuScreen"/>): <paramref name="build"/> adds sections, windows, rows and buttons every time it opens, so what it
    /// shows is current. Call <see cref="MenuScreen.Rebuild"/> to build it again while it is open; any page change closes it.
    /// </summary>
    public MenuBarButton? AddMenuBarScreen(string name, UIText title, Action<MenuScreen> build, Action? clicked = null)
    {
        if (build == null) throw new ArgumentNullException(nameof(build));
        var id = Full(name, "menu bar screen");
        return id == null ? null : MenuBar.Add(new MenuBarButton(id, this, title ?? UIText.Empty, clicked, null, build));
    }

    /// <summary>
    /// Adds an element to the in-level HUD in <paramref name="slot"/>'s region. ForgeUI places the elements of every mod: a region
    /// stacks its elements by priority with a gap, keeps them clear of each other and of the game's weapon list and vitals, and
    /// collapses (and logs) the lowest priority ones when it is full. <paramref name="build"/> runs each time the game creates its HUD
    /// (every level) and puts your pieces under <see cref="HudElement.Root"/> with <see cref="HudElement.AddText"/> and
    /// <see cref="HudElement.AddSprite"/>, which copy the game's own HUD text and sprites, so they are drawn by the game's HUD camera,
    /// sway with the HUD and hide with it. Draw from the root in the region's direction (<see cref="HudElement.FlowX"/>,
    /// <see cref="HudElement.FlowY"/>).
    /// </summary>
    public HudElement? AddHud(string name, HudSlot slot, Action<HudElement> build)
    {
        if (build == null) throw new ArgumentNullException(nameof(build));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(new HudElement(id, build, slot));
    }
}
/// <summary>A registered lobby button. <see cref="Remove"/> takes it off every bar.</summary>
public sealed class LobbyButton
{
    private UIText _title;

    internal LobbyButton(string id, UIKitMod mod, UIText title, Action<CM_PlayerLobbyBar> onClick, Func<string>? note, Func<CM_PlayerLobbyBar, bool>? visible)
    {
        Id = id;
        Mod = mod;
        _title = title;
        OnClick = onClick;
        Note = note;
        Visible = visible;
    }

    public string Id { get; }

    /// <summary>Can be changed at any time; the bars follow on the next frame.</summary>
    public UIText Title
    {
        get => _title;
        set => _title = value ?? UIText.Empty;
    }

    /// <summary>The title as shown now (a key in the current language).</summary>
    internal string ShownTitle => _title.In(Mod.Texts);

    /// <summary>A short line shown in a small box over the button while the pointer is on it; null (default) shows none.</summary>
    public UIText? Tooltip { get; set; }

    internal string? ShownTooltip => Tooltip?.In(Mod.Texts);

    internal UIKitMod Mod { get; }
    internal Action<CM_PlayerLobbyBar> OnClick { get; }
    internal Func<string>? Note { get; }
    internal Func<CM_PlayerLobbyBar, bool>? Visible { get; }
    internal int NoteFrame = -1;
    internal string? NoteValue;

    public void Remove() => LobbyButtons.Remove(this);
}

/// <summary>A registered menu bar button. <see cref="Remove"/> takes it (and its page) off every menu bar.</summary>
public sealed class MenuBarButton
{
    private UIText _title;

    internal MenuBarButton(string id, UIKitMod mod, UIText title, Action? onClick, Action<MenuBarPage>? build, Action<MenuScreen>? buildScreen)
    {
        Id = id;
        Mod = mod;
        _title = title;
        OnClick = onClick;
        Build = build;
        BuildScreen = buildScreen;
    }

    public string Id { get; }

    /// <summary>Can be changed at any time; the menu bars (and the page heading) follow on the next frame.</summary>
    public UIText Title
    {
        get => _title;
        set
        {
            if (ReferenceEquals(_title, value)) return;
            _title = value ?? UIText.Empty;
            MenuBar.Changed();
        }
    }

    /// <summary>The title as shown now (a key in the current language).</summary>
    internal string ShownTitle => _title.In(Mod.Texts);

    /// <summary>A short line shown in a small box under the button while the pointer is on it (not while its page is open); null
    /// (default) shows none.</summary>
    public UIText? Tooltip { get; set; }

    internal string? ShownTooltip => Tooltip?.In(Mod.Texts);

    internal UIKitMod Mod { get; }
    internal Action? OnClick { get; }
    internal Action<MenuBarPage>? Build { get; }

    /// <summary>A full screen (<see cref="UIKitMod.AddMenuBarScreen"/>): built on the settings page at every opening.</summary>
    internal Action<MenuScreen>? BuildScreen { get; }

    /// <summary>Opens its page or screen as a click would. False when the main menu is not up (try again on a later frame) or the button
    /// has no page.</summary>
    public bool Open() => BuildScreen != null ? MenuScreens.Open(this) : MenuBar.OpenNow(this);

    public void Remove() => MenuBar.Remove(this);
}

/// <summary>
/// A page opened from a menu bar button: the game's own pop-up window with your heading, and an empty <see cref="Root"/> for your
/// content. <see cref="Root"/> sits at the top left of the content area: x grows to the right, y grows upwards, so content goes at
/// negative y; the area is <see cref="Size"/> across and down. Use copies of the game's own text and items (for example of the
/// window's first header text) so it looks like the game. An item you want clicked must have its collider inside the content
/// area, or be added with <c>Window.AddNonContentItem</c>.
/// </summary>
public sealed partial class MenuBarPage
{
    private readonly Action _closed;
    private readonly List<IntPtr> _routes = new();
    private readonly CM_PageBase? _nativePage;
    private readonly IntPtr _listener;

    private readonly UITexts _texts;

    internal MenuBarPage(string id, UITexts texts, CM_ScrollWindow window, Transform root, Vector2 size, Action closed, Action? clicked)
    {
        Id = id;
        _texts = texts;
        Window = window;
        Root = root;
        Size = size;
        _closed = closed;
        _nativePage = window.m_pageBase;
        _listener = window.Pointer;
        foreach (var item in window.GetComponentsInChildren<CM_Item>(true))
            if (item.gameObject.name.Contains("Cross", StringComparison.OrdinalIgnoreCase))
                _routes.Add(Input.Route(item, () =>
                {
                    MenuBar.Notify(id, clicked);
                    Close();
                }, null));
    }

    /// <summary>The button's ID.</summary>
    public string Id { get; }

    /// <summary>The game's pop-up window this page is shown in.</summary>
    public CM_ScrollWindow Window { get; }

    /// <summary>Put your content under this.</summary>
    public Transform Root { get; }

    /// <summary>Width and height of the content area, in <see cref="Root"/>'s units.</summary>
    public Vector2 Size { get; }

    public bool IsOpen { get; private set; }

    /// <summary>The menu bar the page belongs to (for copies of its items).</summary>
    internal CM_MenuBar? Bar { get; set; }

    /// <summary>A line of text (as <see cref="AddText"/>) with its top left at (<paramref name="x"/>, <paramref name="y"/>), one line
    /// <paramref name="width"/> wide, cut off with an ellipsis; for headings and cells of your own layout. Rich text is allowed.</summary>
    public void AddLabel(UIText text, float x, float y, float width)
    {
        var label = AddText(text);
        var scale = Math.Max(1e-4f, label.transform.localScale.x);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.rectTransform.sizeDelta = new Vector2(width / scale, label.rectTransform.sizeDelta.y);
        label.transform.localPosition = new Vector3(x, y, 0f);
    }

    /// <summary>A button copied from the menu bar's own item, its left edge at <paramref name="x"/> and its middle at <paramref name="y"/>
    /// (<see cref="Root"/> units, y negative down), at most <paramref name="maxWidth"/> wide. Rich text is allowed (for example
    /// <c>&lt;pos=40%&gt;</c> for columns). Hovers look like the game's; a press calls <paramref name="onClick"/>. Returns the item, whose
    /// <c>GetSize()</c> is its size.</summary>
    public CM_MenuBarItem AddButton(UIText text, float x, float y, Action onClick, float maxWidth = 560f)
    {
        if (onClick == null) throw new ArgumentNullException(nameof(onClick));
        var bar = Bar ?? throw new InvalidOperationException("menu bar page '" + Id + "' has no menu bar");
        var prefab = bar.m_menuBarItemPrefab ?? throw new InvalidOperationException("the menu bar has no item prefab");
        var go = Object.Instantiate(prefab, Root);
        go.name = "ForgeUI.PageButton";
        NativeUi.StripLocalization(go);
        var item = go.GetComponent<CM_MenuBarItem>() ?? throw new InvalidOperationException("the menu bar item prefab has no CM_MenuBarItem");
        item.Setup(bar.m_root, eCM_MenuPage.CMP_NONE);
        item.m_alphaTextOnHover = true;
        item.SetText((text ?? UIText.Empty).In(_texts));
        item.CorrectSizeToText(0f, maxWidth);
        item.CheckAndSetTextAlpha();
        go.transform.localPosition = new Vector3(x, y + item.GetSize().y / 2f, 0f);
        go.transform.localRotation = Quaternion.identity;
        _routes.Add(Input.Route(item, () =>
        {
            try { onClick(); }
            catch (Exception error) { Plugin.Error("ui-menu-bar-page-button-failed", "Menu bar page '" + Id + "' button failed: " + error); }
        }, null));
        return item;
    }

    /// <summary>A filled box with its top left at (<paramref name="x"/>, <paramref name="y"/>) in <see cref="Root"/> units (y negative
    /// down), drawn over the window's own background and under every text and button of the page: a panel, a row highlight, a rule.</summary>
    public void AddRect(float x, float y, float width, float height, Color colour)
    {
        var renderer = Fill(Root, colour, 1);
        renderer.transform.localPosition = new Vector3(x + width / 2f, y - height / 2f, 0f);
        renderer.transform.localScale = new Vector3(width, height, 1f);
    }

    private static Sprite? _white;

    /// <summary>A plain box sprite under <paramref name="parent"/> in the window's own material and sorting layer, <paramref name="order"/>
    /// steps above the window's background (negative: below it). One unit wide and tall; scale it.</summary>
    internal SpriteRenderer Fill(Transform parent, Color colour, int order)
    {
        var texture = Texture2D.whiteTexture;
        if (_white == null || _white.WasCollected) _white = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), texture.width);
        var go = new GameObject("ForgeUI.PageFill") { layer = Window.gameObject.layer };
        go.transform.SetParent(parent, false);
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = _white;
        renderer.color = colour;
        var background = Window.m_background;
        if (background != null)
        {
            renderer.sharedMaterial = background.sharedMaterial;
            renderer.sortingLayerID = background.sortingLayerID;
            renderer.sortingOrder = background.sortingOrder + order;
        }
        return renderer;
    }

    /// <summary>A copy of the window's own heading text under <see cref="Root"/>: top left, wrapping across the whole content
    /// area. Rich text is allowed; move or resize it through its <c>rectTransform</c>. A text key shows in the current language (the
    /// page is built again when the language changes).</summary>
    public TextMeshPro AddText(UIText text)
    {
        var headers = Window.m_headers;
        var source = headers != null && headers.Length > 0 && headers[0] != null ? headers[0].GetComponentInChildren<TextMeshPro>(true) : null;
        if (source == null) throw new InvalidOperationException("menu bar page '" + Id + "' has no heading text to copy");
        var go = Object.Instantiate(source.gameObject, Root);
        go.name = "ForgeUI.PageText";
        NativeUi.StripLocalization(go);
        if (!go.activeSelf) go.SetActive(true);
        var label = go.GetComponent<TextMeshPro>() ?? throw new InvalidOperationException("the heading text copy has no TextMeshPro");
        var scale = source.transform.lossyScale.x / Math.Max(1e-4f, Root.lossyScale.x);
        go.transform.localScale = new Vector3(scale, scale, 1f);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localPosition = Vector3.zero;
        label.rectTransform.pivot = new Vector2(0f, 1f);
        label.rectTransform.sizeDelta = new Vector2(Size.x / Math.Max(1e-4f, scale), Size.y / Math.Max(1e-4f, scale));
        label.alignment = TextAlignmentOptions.TopLeft;
        label.richText = true;
        label.enableAutoSizing = false;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        label.SetText((text ?? UIText.Empty).In(_texts));
        return label;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (Window != null && !Window.WasCollected) Window.SetVisible(false);
        _closed();
    }

    private bool _layered;

    /// <summary>Moves every piece of the window <paramref name="steps"/> sorting steps up: a window opened over another page's window.</summary>
    internal void Raise(int steps)
    {
        foreach (var renderer in Window.GetComponentsInChildren<Renderer>(true)) renderer.sortingOrder += steps;
    }

    internal void Show()
    {
        Window.SetVisible(true);
        // Once: every piece of the window but its background and the page's own boxes moves two sorting steps up, so the boxes
        // (AddRect, one step above the background) lie between the background and every text and button.
        if (!_layered && Window.m_background != null)
        {
            _layered = true;
            var background = Window.m_background;
            foreach (var renderer in Window.GetComponentsInChildren<Renderer>(true))
                if (renderer.Pointer != background.Pointer && renderer.gameObject.name != "ForgeUI.PageFill" && renderer.sortingLayerID == background.sortingLayerID && renderer.sortingOrder >= background.sortingOrder)
                    renderer.sortingOrder += 2;
        }
        // The window takes everything under it as its own (clicks on it do not close it); content added since is included.
        Window.UpdatePopupChildItems();
        IsOpen = true;
    }

    internal void SetTitle(string title)
    {
        if (Window != null && !Window.WasCollected) Window.SetHeader(RichText.Plain(title));
    }

    internal void Destroy()
    {
        foreach (var route in _routes) Input.Unroute(route);
        _routes.Clear();
        Close();
        RemoveListener(_nativePage, _listener);
        if (Window != null && !Window.WasCollected) Object.Destroy(Window.gameObject);
    }

    internal static void RemoveListener(CM_PageBase? page, IntPtr listener)
    {
        if (page != null && !page.WasCollected && page.m_clickAnywhereListeners != null)
        {
            var listeners = page.m_clickAnywhereListeners;
            for (var i = listeners.Count - 1; i >= 0; i--)
                if (listeners[i]?.Pointer == listener) listeners.RemoveAt(i);
        }
    }
}
