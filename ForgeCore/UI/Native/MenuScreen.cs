using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// A full screen opened from the menu bar (<see cref="UIKitMod.AddMenuBarScreen"/>), shown on the game's own settings page and made of
/// copies of that page's own pieces as the game set them up: section buttons copied from the settings sections and put where they stand,
/// windows copied from a settings window (its frame, header, scroll bar and clipping) with rows copied from its rows (the game's row box,
/// text and hover), buttons copied from the section button and the page's red button. While it is open the settings page's own sections
/// and windows are put away; they come back when it closes. Everything else (backdrop, menu bar, cursor) is the settings page as the game
/// shows it.
/// </summary>
public sealed class MenuScreen
{
    // The settings page's own layout, in its moving content's units (read from the page: sections at x -1180 from y 550 every 80;
    // windows at x -830, y 550, 880 tall; rows 50 tall with 5 between).
    // A filled screen's windows: their top just under the menu bar (the header tab stands above it) and the space between them.
    internal const float FillTop = 530f, FillGap = 24f, BelowWindow = 45f;
    internal const float SectionX = -1180f, SectionTop = 550f, SectionStep = 80f;
    internal const float WindowX = -830f, WindowTop = 550f, WindowGap = 60f, WindowHeight = 880f, RowHeight = 50f, RowGap = 5f, RowInset = 20f;
    // Table cells: the text's inset from the row's edges and the space between columns.
    internal const float CellPad = 14f, CellGap = 12f;
    internal const string RowName = "ForgeUI.ScreenRow";

    private readonly UITexts _texts;
    private readonly List<MenuScreenWindow> _windows = new();
    private int _sections;
    private float _nextX = WindowX;
    private float _gap = WindowGap;
    private bool _fill;
    private float _fillRoom;

    internal MenuScreen(CM_PageSettings page, Transform holder, UITexts texts, GameObject section, GameObject? redButton, CM_ScrollWindow window, CM_SettingsItem row)
    {
        Page = page;
        Holder = holder;
        _texts = texts;
        SectionTemplate = section;
        RedTemplate = redButton;
        WindowTemplate = window;
        RowTemplate = row;
    }

    internal CM_PageSettings Page { get; }
    internal Transform Holder { get; }
    internal GameObject SectionTemplate { get; }
    internal GameObject? RedTemplate { get; }
    internal CM_ScrollWindow WindowTemplate { get; }
    internal CM_SettingsItem RowTemplate { get; }
    internal readonly List<GameObject> Created = new();
    internal readonly HashSet<IntPtr> Mine = new();
    internal readonly List<IntPtr> Routes = new();

    internal string Text(UIText text) => (text ?? UIText.Empty).In(_texts);

    /// <summary>A section button in the left column, under the ones before it, as the settings sections are; the selected one keeps its
    /// text bright.</summary>
    public void AddSection(UIText text, bool selected, Action onClick)
    {
        var item = Button(SectionTemplate, Text(text), onClick);
        item.transform.localPosition = new Vector3(SectionX, SectionTop - SectionStep * _sections++, 0f);
        Bright(item, selected);
    }

    /// <summary>A window like the settings windows with <paramref name="header"/>, <paramref name="width"/> across, right of the ones
    /// before it; its rows scroll inside it. On a screen that <see cref="FillScreen"/> it reaches the bottom of the screen less <paramref name="below"/>
    /// (the room its buttons take); otherwise it is as tall as the settings windows. With <paramref name="fit"/> it is only as tall as its rows need, up
    /// to that, and the buttons under it follow it up.</summary>
    public MenuScreenWindow AddWindow(UIText header, float width, float below = 0f, bool fit = false)
    {
        var top = (_fill ? FillTop : WindowTop) - TabsHeight;
        var height = (_fill ? _fillRoom : WindowHeight) - below - TabsHeight;
        var go = Copy(WindowTemplate.gameObject, "ForgeUI.ScreenWindow");
        var window = go.GetComponent<CM_ScrollWindow>() ?? throw new InvalidOperationException("the settings window copy has no CM_ScrollWindow");
        // The copy brings the settings rows and buttons along: only the frame is kept.
        var content = window.m_contentContainer != null ? window.m_contentContainer.transform : null;
        if (content != null) for (var i = content.childCount - 1; i >= 0; i--) Drop(content.GetChild(i).gameObject);
        foreach (var button in go.GetComponentsInChildren<CM_TimedButton>(true)) Drop(button.gameObject);
        // A copy carries only what the game saves with the window: its set-up (content lists that SetSize walks, the clipping that
        // shows the rows) runs again, as the settings page runs it on each window it makes.
        window.Setup();
        window.SetSize(new Vector2(width, height));
        window.SetHeader(Text(header));
        go.transform.localPosition = new Vector3(_nextX, top, 0f);
        var result = new MenuScreenWindow(this, window, width, height, fit, _nextX, top - height - BelowWindow);
        _nextX += width + _gap;
        _windows.Add(result);
        return result;
    }

    // The tabs across the top of a filled screen (AddTab): the rows made so far, where the next one goes from the left, and the right end of the right-aligned ones.
    internal const float TabGap = 16f, TabRowStep = 66f, HeaderTab = 40f;
    private int _tabRows;
    private float _tabX, _tabRight;
    private float TabsHeight => _tabRows * TabRowStep;

    /// <summary>A tab in a row across the top of a <see cref="FillScreen"/> screen, under the menu bar and over the windows, which start under the
    /// tab rows: a copy of the settings section button at its own width (a longer text ends in an ellipsis). <paramref name="selected"/> keeps its text bright as the settings
    /// sections do; with no <paramref name="onClick"/> it is a dim label that takes no press. <paramref name="right"/> puts it at the right end of the
    /// first row; the others run left to right and start a new row where the screen ends (or after <see cref="NextTabRow"/>). Add the tabs before the windows.</summary>
    public void AddTab(UIText text, bool selected, Action? onClick, bool right = false)
    {
        if (_tabRows == 0) { _tabRows = 1; _tabX = _nextX; }
        var item = Button(SectionTemplate, onClick == null ? MenuText.Tint(Text(text), MenuTone.Dim) : Text(text), onClick ?? (() => { }));
        // The section button keeps its own width: a text longer than it ends in an ellipsis rather than running out of the frame.
        if (item.GetComponentInChildren<TMPro.TextMeshPro>(true) is { } label) { label.enableWordWrapping = false; label.overflowMode = TMPro.TextOverflowModes.Ellipsis; }
        item.transform.localPosition = Vector3.zero;
        var box = MenuCanvas.BoxIn(Holder, item.GetComponentInChildren<BoxCollider2D>(), new Rect(0f, -25f, 300f, 50f));
        float left;
        if (right) { left = _tabRight - box.width; _tabRight = left - TabGap; }
        else
        {
            if (_tabX > _nextX && _tabX + box.width > _tabRight) NextTabRow();
            left = _tabX; _tabX += box.width + TabGap;
        }
        // The first row stands where a window's header tab would (above FillTop); each row pushes the windows, and their header tabs, a row further down.
        var top = FillTop + HeaderTab - (right ? 0f : _tabRows - 1) * TabRowStep;
        item.transform.localPosition = new Vector3(left - box.xMin, top - box.yMax, 0f);
        if (onClick == null && item.m_collider != null) item.m_collider.enabled = false;
        Bright(item, selected);
    }

    /// <summary>The tabs added next start a row of their own under the ones before.</summary>
    public void NextTabRow()
    {
        if (_tabRows == 0) { _tabRows = 1; _tabX = _nextX; }
        _tabRows++;
        _tabX = _nextX;
    }

    /// <summary>How wide the window added next can be and still reach the right edge of this screen with one more window of
    /// <paramref name="nextWidth"/> after it (0: none). A wide screen gets a wide window. Not below <paramref name="minimum"/>: on a narrower
    /// screen the whole screen is scaled to fit.</summary>
    public float RoomAcross(float nextWidth, float minimum)
    {
        var size = Page.m_screenResVec2;
        var unit = Holder.localScale.x != 0f ? Holder.localScale.x : 1f;
        var reserve = nextWidth > 0f ? nextWidth + _gap : 0f;
        return size.x <= 0f ? minimum : Math.Max(minimum, size.x / 2f / unit - EdgeGap - _nextX - reserve);
    }

    /// <summary>Builds the screen again at once (after a click changed what it shows).</summary>
    public void Rebuild() => MenuScreens.Rebuild();

    /// <summary>The windows use the whole screen, for a screen with no sections: they start at its left edge just under the menu bar, stand closer together and reach
    /// its bottom (<see cref="AddWindow"/>; <see cref="RoomAcross"/> gives the width left). <paramref name="minimumWidth"/> is the width they cannot go under: where it does
    /// not fit, the screen is scaled down to fit, so they get that much more height and still fill it.</summary>
    public void FillScreen(float minimumWidth)
    {
        var size = Page.m_screenResVec2;
        if (size.x <= 0f || size.y <= 0f) return;
        var unit = Holder.localScale.x != 0f ? Holder.localScale.x : 1f;
        var width = size.x / unit - 2f * EdgeGap;
        _fill = true;
        _gap = FillGap;
        _nextX = -size.x / 2f / unit + EdgeGap;
        _tabRight = size.x / 2f / unit - EdgeGap;
        _fillRoom = (FillTop + size.y / 2f / unit - EdgeGap) / Math.Min(1f, width / minimumWidth);
    }

    /// <summary>What a window that shares the bottom of the screen leaves under it while <see cref="Toast"/> has a message there.</summary>
    public float ToastRoom => MenuScreens.ToastShowing ? RowHeight + BelowWindow : 0f;

    /// <summary>A one-line message under the widest window for <paramref name="seconds"/> (the game's menu shows no HUD, so the corner notices
    /// cannot be seen here): what could not be done, in the alert colour, or what was done. It covers nothing, a long text is cut with an ellipsis, and it has
    /// a close button, <paramref name="action"/> beside that when given (<paramref name="onAction"/> runs on a press). It stays over rebuilds; a new one replaces it.</summary>
    public void Toast(UIText text, NoticeTone tone = NoticeTone.Alert, float seconds = 4f, UIText? action = null, Action? onAction = null)
        => MenuScreens.Toast(Text(text), tone, seconds, action == null ? null : Text(action), onAction);

    internal void ShowToast(string text, NoticeTone tone, string? action, Action? onAction)
    {
        MenuScreenWindow? widest = null;
        foreach (var window in _windows) if (widest == null || window.Width > widest.Width) widest = window;
        ToastPiece = widest?.Strip(MenuText.Tint(text, tone switch { NoticeTone.Alert => MenuTone.Alert, NoticeTone.Warn => MenuTone.Warn, _ => MenuTone.Bright }), action, onAction);
    }

    internal GameObject? ToastPiece { get; private set; }

    internal void Finish()
    {
        foreach (var window in _windows) window.Finish();
        MenuScreens.ShowToast(this);
        _rest.Clear();
        foreach (var go in Created) _rest.Add((go.transform, go.transform.localPosition, go.transform.localScale));
        _fitFor = Vector2.zero;
        Hold();
    }

    // The pieces as built at the settings page's coordinates, and the scale and shift that keep all of them on this screen.
    private readonly List<(Transform Piece, Vector3 Position, Vector3 Scale)> _rest = new();
    private Vector2 _fitFor;
    private float _scale = 1f;
    private Vector2 _shift;

    /// <summary>Room kept between the screen and the edges (and under the menu bar), in the page's units.</summary>
    internal const float EdgeGap = 30f;

    /// <summary>Per frame: the screen is built at the settings page's own coordinates, which fit the game's large screens; on a smaller or
    /// narrower one (a window, a low resolution, 4:3) they run past the edges, and the page's cursor panning moves them further. So the
    /// whole screen is scaled down and shifted (never up) until it sits between the screen edges and under the menu bar, worked out
    /// again whenever the page's screen size changes, and held still against the panning (as the picker is).</summary>
    internal void Hold()
    {
        if (_rest.Count == 0) return;
        var size = Page.m_screenResVec2;
        if (size != _fitFor && size.x > 0f && size.y > 0f) { _fitFor = size; Fit(size); }
        var pan = PagePan.Offset(Page, _rest[0].Piece);
        foreach (var (piece, position, _) in _rest)
        {
            if (piece == null || piece.WasCollected) continue;
            var at = new Vector3(position.x * _scale + _shift.x - pan.x, position.y * _scale + _shift.y - pan.y, position.z);
            if (piece.localPosition != at) piece.localPosition = at;
        }
    }

    private void Fit(Vector2 size)
    {
        // What the pieces cover at the settings layout (scale 1, no shift), in the holder's units.
        foreach (var (piece, position, scale) in _rest) { piece.localPosition = position; piece.localScale = scale; }
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
        foreach (var (piece, _, _) in _rest)
            foreach (var renderer in piece.GetComponentsInChildren<Renderer>(false))
            {
                // A row is clipped to its window, and its box can report the size of the row it was copied from, wider than the window.
                if (renderer.GetComponentInParent<CM_SettingsItem>() != null) continue;
                var a = Holder.InverseTransformPoint(renderer.bounds.min); var b = Holder.InverseTransformPoint(renderer.bounds.max);
                left = Math.Min(left, Math.Min(a.x, b.x)); right = Math.Max(right, Math.Max(a.x, b.x));
                bottom = Math.Min(bottom, Math.Min(a.y, b.y)); top = Math.Max(top, Math.Max(a.y, b.y));
            }
        _scale = 1f; _shift = Vector2.zero;
        if (right <= left || top <= bottom) return;
        // The screen around the holder's rest position, in the holder's units (the page's screen size is in the units it sits in).
        var unit = Holder.localScale.x != 0f ? Holder.localScale.x : 1f;
        var halfW = size.x / 2f / unit; var halfH = size.y / 2f / unit;
        var roomTop = halfH - EdgeGap;
        if (Page.GetComponentInChildren<CM_MenuBar>() is { } bar)
        {
            var pan = PagePan.Offset(Page, _rest[0].Piece);
            foreach (var renderer in bar.GetComponentsInChildren<Renderer>(false))
                roomTop = Math.Min(roomTop, Holder.InverseTransformPoint(renderer.bounds.min).y + pan.y - EdgeGap);
        }
        var (s, x, y) = BoxFit.Fit(new HudBox(left, bottom, right, top), new HudBox(-halfW + EdgeGap, -halfH + EdgeGap, halfW - EdgeGap, roomTop));
        _scale = s; _shift = new Vector2(x, y);
        foreach (var (piece, _, scale) in _rest) piece.localScale = scale * s;
    }

    internal GameObject Copy(GameObject template, string name, Transform? parent = null)
    {
        var go = Object.Instantiate(template, parent ?? Holder);
        go.name = name;
        NativeUi.StripLocalization(go);
        go.SetActive(true);
        if (parent == null) { Created.Add(go); Mine.Add(go.Pointer); }
        return go;
    }

    internal CM_Item Button(GameObject template, string text, Action onClick)
    {
        var item = Copy(template, "ForgeUI.ScreenButton").GetComponent<CM_Item>() ?? throw new InvalidOperationException("the settings button copy has no CM_Item");
        NoTooltip(item);
        item.SetText(text);
        Routes.Add(Input.Route(item, Guard(onClick), null));
        return item;
    }

    /// <summary>A copy keeps the hover tooltip of the setting it was copied from ("Toggles the crouch mode..."): ours have none.</summary>
    internal static void NoTooltip(CM_Item item)
    {
        var info = item.m_tooltipInfo;
        if (info == null) return;
        info.UseTooptip = false;
        info.TooltipHeader = "";
        info.TooltipText = "";
        item.m_tooltipInfo = info;
    }
    internal static void Bright(CM_Item item, bool on)
    {
        if (on) item.m_textAlphaOut = 1f;
        item.CheckAndSetTextAlpha();
    }

    // Every press on the screen ends an asking button's question first (AddConfirmButton); when one was asking, the screen is built again
    // after the press so the button shows as it was.
    internal static Action Guard(Action onClick) => () =>
    {
        var asked = MenuScreens.StopAsking();
        try { onClick(); }
        catch (Exception error) { Plugin.Error("ui-menu-screen-click-failed", "Menu screen click failed: " + error); }
        if (asked) MenuScreens.Rebuild();
    };

    private static void Drop(GameObject go)
    {
        go.SetActive(false);
        Object.Destroy(go);
    }
}

/// <summary>A window of a <see cref="MenuScreen"/>: rows like the settings rows, buttons under it.</summary>
public sealed class MenuScreenWindow
{
    // The selected row's fill and outline, warm against the cool hover box (#6B8594). The width is in the row's own units (a row is 50 high).
    private static readonly Color SelectedFill = new(0.95f, 0.72f, 0.28f, 0.32f), SelectedLine = new(1f, 0.82f, 0.42f, 1f);
    private const float OutlineWidth = 2f;
    private readonly MenuScreen _screen;
    private readonly CM_ScrollWindow _window;
    private readonly float _width, _height, _x;
    private readonly bool _fit;
    private float _under;
    private readonly Il2CppSystem.Collections.Generic.List<iScrollWindowContent> _rows = new();
    // Each row's height, and the buttons placed under the window (they move up with a window that fits its rows).
    private readonly List<float> _rowHeights = new();
    private readonly List<Transform> _buttons = new();
    private CM_SettingsItem? _lastRow;
    private float[] _stops = Array.Empty<float>();
    private int[] _right = Array.Empty<int>();
    // A row of more than one line (AddEntry) is this much taller for each line past the first; the window's frame around its rows (header tab, insets).
    private const float LineStep = 36f, FramePad = 100f;

    internal MenuScreenWindow(MenuScreen screen, CM_ScrollWindow window, float width, float height, bool fit, float x, float under)
    {
        _under = under;
        _cursor = new[] { under };
        _screen = screen;
        _window = window;
        _width = width;
        _height = height;
        _fit = fit;
        _x = x;
    }

    /// <summary>One line of an <see cref="AddEntry"/>: its cells (rich text), where each column after the first starts in per cent of the row's width,
    /// and which columns line their text up on the right.</summary>
    public readonly record struct MenuLine(IReadOnlyList<string> Cells, IReadOnlyList<float> Stops, IReadOnlyList<int> RightAligned);

    /// <summary>A row of several lines, one box for all of them (a list entry with its details under its title): each line its own cells as
    /// <see cref="AddRow(IReadOnlyList{string}, Action, bool)"/> lays them out; with <paramref name="onClick"/> the whole box answers the cursor, and
    /// <paramref name="selected"/> marks the whole box.</summary>
    public void AddEntry(IReadOnlyList<MenuLine> lines, Action? onClick = null, bool selected = false)
    {
        var row = Row(onClick, selected, MenuScreen.RowHeight + Math.Max(0, lines.Count - 1) * LineStep);
        for (var i = 0; i < lines.Count; i++) Cells(row, lines[i].Cells, lines[i].Stops, lines[i].RightAligned, selected && i == 0, -i * LineStep);
    }

    /// <summary>A row like the settings rows with one line of rich text; with <paramref name="onClick"/> it answers the cursor like a
    /// settings row; <paramref name="selected"/> marks it (<see cref="Select"/>).</summary>
    public void AddRow(UIText text, Action? onClick = null, bool selected = false) => Row(onClick, selected).SetText(_screen.Text(text));

    /// <summary>The table columns of this window's following headers and cell rows: column <c>i</c> (after the first)
    /// starts at <paramref name="stops"/>[i - 1] per cent of the row's width and ends where the next starts; the columns in
    /// <paramref name="rightAligned"/> (0 is the first) line their text up on the right, as numbers are.</summary>
    public void Columns(float[] stops, params int[] rightAligned)
    {
        _stops = stops ?? Array.Empty<float>();
        _widths = null;
        _right = rightAligned ?? Array.Empty<int>();
    }

    /// <summary>Table columns by width in the page's units: column <c>i</c> is <paramref name="widths"/>[i] wide, except
    /// <paramref name="flexible"/>, which takes what the row's box leaves (none when the others already fill it); otherwise as
    /// <see cref="Columns"/>. For columns sized to their longest text, which per cent of a row cannot say.</summary>
    public void ColumnWidths(float[] widths, int flexible, params int[] rightAligned)
    {
        _widths = widths ?? Array.Empty<float>();
        _flexible = flexible;
        _stops = Array.Empty<float>();
        _right = rightAligned ?? Array.Empty<int>();
    }

    private float[]? _widths;
    private int _flexible;

    // The stops of this row: the per cent ones as given, or worked out from the widths on the room of this row's box.
    private IReadOnlyList<float> StopsFor(CM_SettingsItem row)
    {
        if (_widths == null) return _stops;
        var room = Span(row).Width;
        var fixedSum = 0f;
        for (var i = 0; i < _widths.Length; i++) if (i != _flexible) fixedSum += _widths[i];
        var stops = new float[Math.Max(0, _widths.Length - 1)];
        var at = 0f;
        for (var i = 0; i < stops.Length; i++)
        {
            at += i == _flexible ? Math.Max(0f, room - fixedSum) : _widths[i];
            stops[i] = Math.Min(100f, at / room * 100f);
        }
        return stops;
    }

    /// <summary>A table header: <paramref name="cells"/> in the game's dim label colour on the <see cref="Columns"/>, not clickable.</summary>
    public void AddHeader(params UIText[] cells) => AddHeader(null, -1, cells);

    /// <summary>A table header whose cells can be clicked (to sort by that column): <paramref name="onClick"/> gets the column clicked (0 is
    /// the first). Column <paramref name="current"/> is bright, the others dim; -1 none.</summary>
    public void AddHeader(Action<int>? onClick, int current, params UIText[] cells)
    {
        var texts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++) texts[i] = MenuText.Tint(_screen.Text(cells[i]), i == current ? MenuTone.Bright : MenuTone.Dim);
        CM_SettingsItem? made = null;
        IReadOnlyList<float> stops = _stops;
        made = Row(onClick == null ? null : () => { if (made != null && Column(made, stops, out var column)) onClick(column); }, false);
        stops = StopsFor(made);
        Cells(made, texts, stops, _right);
    }

    /// <summary>The game's own hover tooltip on the row added last (a header, a field): <paramref name="title"/> over <paramref name="text"/>.</summary>
    public void Tooltip(UIText title, UIText text)
    {
        var row = _lastRow;
        if (row == null) return;
        var info = row.m_tooltipInfo;
        if (info == null) return;
        info.UseTooptip = true;
        info.TooltipHeader = _screen.Text(title);
        info.TooltipText = _screen.Text(text);
        row.m_tooltipInfo = info;
        if (row.m_collider != null) row.m_collider.enabled = true;
    }

    // Which column of a row the mouse is over, from the mouse's place on the row (the cells start at the row's left edge plus the pad).
    private bool Column(CM_SettingsItem row, IReadOnlyList<float> stops, out int column)
    {
        column = 0;
        Camera? camera = null;
        foreach (var c in Camera.allCameras) if ((c.cullingMask & (1 << row.gameObject.layer)) != 0) { camera = c; break; }
        if (camera == null) return false;
        var mouse = UnityEngine.Input.mousePosition;
        var depth = camera.WorldToScreenPoint(row.transform.position).z;
        var x = row.transform.InverseTransformPoint(camera.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, depth))).x;
        var (left, width) = Span(row);
        var share = (x - left) / width * 100f;
        while (column < stops.Count && share >= stops[column]) column++;
        return true;
    }

    /// <summary>A table row: each of <paramref name="cells"/> (rich text; <see cref="MenuText"/> tints it and shows author text as
    /// written) is a text of its own in its <see cref="Columns"/> column, cut with an ellipsis where it would run into the next. An empty
    /// cell gives its room to the one before it.</summary>
    public void AddRow(IReadOnlyList<string> cells, Action? onClick = null, bool selected = false)
    {
        var row = Row(onClick, selected);
        Cells(row, cells, StopsFor(row), _right, selected);
    }

    /// <summary>A label and value row of a details window: <paramref name="label"/> dim at the left, <paramref name="value"/> (rich text)
    /// from <paramref name="at"/> per cent of the row's width. A label longer than its column is never cut: the value starts after it, a gap further.</summary>
    public void AddField(UIText label, string value, float at = 38f)
    {
        var made = Cells(Row(null, false), new[] { MenuText.Tint(_screen.Text(label), MenuTone.Dim), value }, new[] { at }, Array.Empty<int>());
        if (made.Count < 2) return;
        var (name, shown) = (made[0], made[1]);
        var over = name.GetPreferredValues(name.text).x - name.rectTransform.sizeDelta.x;
        if (over <= 0f) return;
        // A gap of one em (spaces measure nothing at a line's end) between the long label and its value, so they do not run together.
        over += name.GetPreferredValues("m").x;
        // Both cells are centred on their place (pivot x 0.5) and their widths are in the text's own units.
        Grow(name, over);
        Grow(shown, -over);
    }

    // A cell made wider by `by` (in its own units) to the right, its left edge kept; narrower when `by` is negative, its right edge kept.
    private static void Grow(TMPro.TextMeshPro cell, float by)
    {
        var rect = cell.rectTransform;
        var scale = cell.transform.localScale.x != 0f ? cell.transform.localScale.x : 1f;
        rect.sizeDelta = new Vector2(Math.Max(0f, rect.sizeDelta.x + by), rect.sizeDelta.y);
        var p = cell.transform.localPosition;
        cell.transform.localPosition = new Vector3(p.x + Math.Abs(by) * scale / 2f, p.y, p.z);
    }

    /// <summary>Text that runs on over as many rows as it needs, broken between words (after a space or a Chinese comma) where the next word
    /// would pass the row's edge; <paramref name="tint"/> colours each row's piece (rich text tags cannot be cut in two, so the text is plain).</summary>
    public void AddParagraph(UIText text, Func<string, string>? tint = null)
    {
        var full = _screen.Text(text);
        tint ??= s => s;
        var row = Row(null, false);
        var made = Cells(row, new[] { tint(full) }, Array.Empty<float>(), Array.Empty<int>());
        var cell = made.Count > 0 ? made[0] : null;
        if (cell == null) return;
        var room = cell.rectTransform.sizeDelta.x;
        bool Fits(string s) => cell.GetPreferredValues(tint(s)).x <= room;
        if (Fits(full)) return;
        var lines = new List<string>();
        TextWrap.Lines(full, s => cell.GetPreferredValues(tint(s)).x, room, int.MaxValue, lines);
        cell.text = tint(lines[0]);
        for (var i = 1; i < lines.Count; i++) Cells(Row(null, false), new[] { tint(lines[i]) }, Array.Empty<float>(), Array.Empty<int>());
    }

    // The text in pieces that may end a row: each piece runs up to and including a space or a CJK or ASCII punctuation mark after which a
    // break reads naturally.
    /// <summary>Where a row's cells go, in the row's own units: from the left edge of the row's box plus the text inset, as wide as the box
    /// less both insets. The box is measured from its sprite as drawn (as <see cref="Select"/> does; a row being built is not drawn yet), not
    /// taken from the size the row was given: the game draws the box narrower than that, and text placed by the given size ran past the box.</summary>
    private (float Left, float Width) Span(CM_SettingsItem row)
    {
        var fallback = (MenuScreen.CellPad, _width - MenuScreen.RowInset - 2f * MenuScreen.CellPad);
        var box = row.m_background;
        if (box == null || box.sprite == null) return fallback;
        var scale = box.transform.localScale;
        var drawn = box.drawMode == SpriteDrawMode.Simple ? (Vector2)box.sprite.bounds.size : box.size;
        var size = new Vector2(drawn.x * Mathf.Abs(scale.x), drawn.y * Mathf.Abs(scale.y));
        if (size.x <= 0f) return fallback;
        var sprite = box.sprite;
        var pivot = new Vector2(sprite.pivot.x / sprite.rect.width, sprite.pivot.y / sprite.rect.height);
        var centre = box.transform.localPosition + box.transform.localRotation * new Vector3((0.5f - pivot.x) * size.x, (0.5f - pivot.y) * size.y, 0f);
        var parent = box.transform.parent;
        var a = row.transform.InverseTransformPoint(parent.TransformPoint(centre - box.transform.localRotation * new Vector3(size.x / 2f, 0f, 0f))).x;
        var b = row.transform.InverseTransformPoint(parent.TransformPoint(centre + box.transform.localRotation * new Vector3(size.x / 2f, 0f, 0f))).x;
        var left = Math.Min(a, b);
        var end = Math.Max(a, b);
        // Never past what the window shows: in a narrow window the row box runs under the window's clipping on both sides, and cells
        // placed by the box were cut off at the window's edges.
        if (Shown(row) is { } shown) { left = Math.Max(left, shown.Left); end = Math.Min(end, shown.Right); }
        left += MenuScreen.CellPad;
        var width = end - left - MenuScreen.CellPad;
        return width > 0f ? (left, width) : fallback;
    }

    // The window's clipping area across, in the row's own units; null when the window has none.
    private (float Left, float Right)? Shown(CM_SettingsItem row)
    {
        var clip = _window.m_clipRect;
        if (clip == null) return null;
        var r = clip.rect;
        if (r.width <= 0f) return null;
        var a = row.transform.InverseTransformPoint(clip.TransformPoint(new Vector3(r.xMin, r.center.y, 0f))).x;
        var b = row.transform.InverseTransformPoint(clip.TransformPoint(new Vector3(r.xMax, r.center.y, 0f))).x;
        return (Math.Min(a, b), Math.Max(a, b));
    }

    internal float Width => _width;

    // The widths in per cent of a strip's action and close cells.
    private const float StripActionShare = 16f, StripCloseShare = 5f;

    /// <summary>The screen's message under this window: a row of its own that scrolls with nothing, the text, then the action and the close cell, told apart by
    /// where the press lands (as a clickable header's are).</summary>
    internal GameObject Strip(string text, string? action, Action? onAction)
    {
        var row = BareRow(null);
        row.transform.localPosition = new Vector3(_x, _under, 0f);
        var hasAction = !string.IsNullOrEmpty(action);
        var closeAt = 100f - StripCloseShare;
        var stops = hasAction ? new[] { closeAt - StripActionShare, closeAt } : new[] { closeAt };
        var cells = hasAction ? new[] { text, MenuText.Tint(action!, MenuTone.Mark), MenuText.Tint("X", MenuTone.Bright) } : new[] { text, MenuText.Tint("X", MenuTone.Bright) };
        Cells(row, cells, stops, Array.Empty<int>(), false);
        _screen.Routes.Add(Input.Route(row, MenuScreen.Guard(() =>
        {
            if (!Column(row, stops, out var column)) return;
            if (column == cells.Length - 1) MenuScreens.CloseToast();
            else if (column == 1) onAction?.Invoke();
        }), null));
        return row.gameObject;
    }

    // A copy of the settings row with nothing of the setting it was copied from. A row of this window is made beside the content, not in it: the window's
    // SetContentItems clears its content container and moves the items in, as the settings page does with the rows it makes.
    private CM_SettingsItem BareRow(Transform? parent, float height = MenuScreen.RowHeight)
    {
        var row = _screen.Copy(_screen.RowTemplate.gameObject, MenuScreen.RowName, parent).GetComponent<CM_SettingsItem>()
            ?? throw new InvalidOperationException("the settings row copy has no CM_SettingsItem");
        if (row.m_inputAlign != null)
            for (var i = row.m_inputAlign.childCount - 1; i >= 0; i--) Object.Destroy(row.m_inputAlign.GetChild(i).gameObject);
        MenuScreen.NoTooltip(row);
        row.SetSize(new Vector2(_width - MenuScreen.RowInset, height));
        Widen(row.m_background, _width - MenuScreen.RowInset);
        if (height > MenuScreen.RowHeight) Deepen(row, height);
        return row;
    }

    // A row taller than the game's: its box (and its press area) grows down to the height, its top edge kept, as the rows under it are laid out from its size.
    private static void Deepen(CM_SettingsItem row, float want)
    {
        var box = row.m_background;
        if (box == null || box.sprite == null) return;
        var scale = box.transform.localScale;
        var simple = box.drawMode == SpriteDrawMode.Simple;
        var now = (simple ? box.sprite.bounds.size.y : box.size.y) * Mathf.Abs(scale.y);
        if (now <= 0f || want <= now) return;
        if (simple) box.transform.localScale = new Vector3(scale.x, scale.y * want / now, scale.z);
        else box.size = new Vector2(box.size.x, box.size.y * want / now);
        var pivot = box.sprite.pivot.y / box.sprite.rect.height;
        // Scaled about its pivot: moved down by the part of the growth above the pivot, so the top stays.
        box.transform.localPosition -= box.transform.localRotation * new Vector3(0f, (1f - pivot) * (want - now), 0f);
        if (row.m_collider?.TryCast<BoxCollider2D>() is { } press && press.size.y < want)
        {
            var extra = want - press.size.y;
            press.size = new Vector2(press.size.x, want);
            press.offset = new Vector2(press.offset.x, press.offset.y - extra / 2f);
        }
    }

    // The game draws the row's box at its own width and leaves SetSize out of it: under a narrower window the clipping cuts it, but in a wider one it would stop short. So it
    // grows to the window, about its middle, where the game has put it.
    private static void Widen(SpriteRenderer? box, float want)
    {
        if (box == null || box.sprite == null) return;
        var scale = box.transform.localScale;
        var simple = box.drawMode == SpriteDrawMode.Simple;
        var now = (simple ? box.sprite.bounds.size.x : box.size.x) * Mathf.Abs(scale.x);
        if (now <= 0f || want <= now) return;
        if (simple) box.transform.localScale = new Vector3(scale.x * want / now, scale.y, scale.z);
        else box.size = new Vector2(box.size.x * want / now, box.size.y);
        var pivot = box.sprite.pivot.x / box.sprite.rect.width;
        box.transform.localPosition += box.transform.localRotation * new Vector3((0.5f - pivot) * (now - want), 0f, 0f);
    }

    private CM_SettingsItem Row(Action? onClick, bool selected, float height = MenuScreen.RowHeight)
    {
        var row = BareRow(_window.transform, height);
        if (selected) Select(row);
        if (onClick == null) { if (row.m_collider != null) row.m_collider.enabled = false; }
        else _screen.Routes.Add(Input.Route(row, MenuScreen.Guard(onClick), null));
        _rows.Add(new iScrollWindowContent(row.Pointer));
        _rowHeights.Add(height);
        _lastRow = row;
        return row;
    }

    /// <summary>A selected row has a fill and an outline of their own over its box, copies of the box the game's hover never touches, so hovering it or
    /// another row does not change them and a hovered row (the box itself turning the hover colour) looks different. They are children of the row: they
    /// scroll with it, and a rebuild draws them on whichever row is selected then. The cells are bold too (<see cref="Cells"/>).</summary>
    private static void Select(CM_SettingsItem row)
    {
        var box = row.m_background;
        if (box == null) return;
        if (box.sprite == null) return;
        var simple = box.drawMode == SpriteDrawMode.Simple;
        var scale = box.transform.localScale;
        var drawn = simple ? (Vector2)box.sprite.bounds.size : box.size;
        var size = new Vector2(drawn.x * Mathf.Abs(scale.x), drawn.y * Mathf.Abs(scale.y));
        if (size.x <= 0f || size.y <= 0f) return;
        var x = OutlineWidth / size.x;
        var y = OutlineWidth / size.y;
        Piece(box, SelectedFill, size, 0f, 0f, 1f, 1f);
        Piece(box, SelectedLine, size, 0f, 0.5f - y / 2f, 1f, y);
        Piece(box, SelectedLine, size, 0f, y / 2f - 0.5f, 1f, y);
        Piece(box, SelectedLine, size, x * 2f - 0.5f, 0f, x * 4f, 1f);
        Piece(box, SelectedLine, size, 0.5f - x / 2f, 0f, x, 1f);
    }

    // A copy of the box at the given share of its size, its centre moved by a share of its size, in the box's parent's units: worked out from the sprite and
    // not from what is drawn, because a row being built may not be drawn yet. The sprite's pivot decides where the middle of a piece is from its position.
    private static void Piece(SpriteRenderer box, Color colour, Vector2 size, float dx, float dy, float width, float height)
    {
        var copy = Object.Instantiate(box.gameObject, box.transform.parent);
        copy.name = "ForgeUI.Selected";
        foreach (var collider in copy.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
        var piece = copy.GetComponent<SpriteRenderer>();
        piece.color = colour;
        piece.sortingOrder = box.sortingOrder + 1;
        var scale = box.transform.localScale;
        if (piece.drawMode == SpriteDrawMode.Simple) copy.transform.localScale = new Vector3(scale.x * width, scale.y * height, scale.z);
        else piece.size = new Vector2(box.size.x * width, box.size.y * height);
        var sprite = box.sprite;
        var pivot = new Vector2(sprite.pivot.x / sprite.rect.width, sprite.pivot.y / sprite.rect.height);
        var middle = new Vector2((0.5f - pivot.x) * size.x, (0.5f - pivot.y) * size.y);
        var move = new Vector2(middle.x * (1f - width) + dx * size.x, middle.y * (1f - height) + dy * size.y);
        copy.transform.localPosition = box.transform.localPosition + box.transform.localRotation * (Vector3)move;
    }

    // Each cell a copy of the row's own title text, placed in its column of the row's box (<see cref="Span"/>), one line, cut with an
    // ellipsis; the row's title itself stays empty.
    // Returns the cells made.
    private List<TMPro.TextMeshPro> Cells(CM_SettingsItem row, IReadOnlyList<string> cells, IReadOnlyList<float> stops, IReadOnlyList<int> right, bool strong = false, float down = 0f)
    {
        var made = new List<TMPro.TextMeshPro>();
        var title = row.m_title;
        row.SetText("");
        if (title == null) return made;
        var (left, width) = Span(row);
        for (var i = 0; i < cells.Count; i++)
        {
            if (string.IsNullOrEmpty(cells[i])) continue;
            var last = i;
            while (last + 1 < cells.Count && string.IsNullOrEmpty(cells[last + 1])) last++;
            var from = i == 0 ? 0f : i - 1 < stops.Count ? stops[i - 1] : 100f;
            var to = last < stops.Count ? stops[last] : 100f;
            if (to <= from) continue;
            var cell = Object.Instantiate(title.gameObject, title.transform.parent).GetComponent<TMPro.TextMeshPro>();
            if (cell == null) continue;
            cell.gameObject.name = "ForgeUI.Cell";
            cell.transform.SetParent(row.transform, true);
            var scale = cell.transform.localScale.x != 0f ? cell.transform.localScale.x : 1f;
            var columnWidth = (to - from) / 100f * width - (last < cells.Count - 1 ? MenuScreen.CellGap : 0f);
            var rect = cell.rectTransform;
            // Only the horizontal pivot moves: the row's text keeps its own vertical placement on the row's box.
            rect.pivot = new Vector2(0.5f, rect.pivot.y);
            rect.sizeDelta = new Vector2(columnWidth / scale, rect.sizeDelta.y);
            var p = cell.transform.localPosition;
            cell.transform.localPosition = new Vector3(left + from / 100f * width + columnWidth / 2f, p.y + down, p.z);
            cell.enableWordWrapping = false;
            cell.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            cell.alignment = Contains(right, i) ? TMPro.TextAlignmentOptions.MidlineRight : TMPro.TextAlignmentOptions.MidlineLeft;
            if (strong) cell.fontStyle |= TMPro.FontStyles.Bold;
            cell.text = cells[i];
            made.Add(cell);
        }
        return made;
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return true;
        return false;
    }
    // The buttons under the window stand in columns across its width (see Buttons): each column's weight, and where the next button's top edge
    // goes in each. A button is placed by the box it really has (the settings button's and the red one's differ) and shrunk to its column.
    private float[] _weights = { 1f };
    private float[] _cursor;
    private const float ButtonGap = 30f, MainScale = 1.25f;

    /// <summary>The columns of the buttons under this window: one per weight, across the window's width, a wider weight a wider column.
    /// Call before adding the buttons; <see cref="AddButton"/> names the column of each.</summary>
    public void Buttons(params float[] weights)
    {
        _weights = weights.Length > 0 ? weights : new[] { 1f };
        _cursor = new float[_weights.Length];
        Array.Fill(_cursor, _under);
    }

    // Puts a button at the top free spot of its column, the main one larger, shrunk when its column is narrower. Returns its box.
    private Rect Place(CM_Item item, int column, bool main)
    {
        column = Math.Clamp(column, 0, _weights.Length - 1);
        var sum = 0f;
        foreach (var w in _weights) sum += w;
        var unit = (_width - ButtonGap * (_weights.Length - 1)) / sum;
        var left = _x;
        for (var i = 0; i < column; i++) left += _weights[i] * unit + ButtonGap;
        var room = _weights[column] * unit;
        var holder = _screen.Holder;
        item.transform.localPosition = Vector3.zero;
        var natural = MenuCanvas.BoxIn(holder, item.GetComponentInChildren<BoxCollider2D>(), new Rect(0f, -25f, 300f, 50f));
        var scale = Math.Min(main ? MainScale : 1f, natural.width > 0f ? room / natural.width : 1f);
        item.transform.localScale *= scale;
        var box = MenuCanvas.BoxIn(holder, item.GetComponentInChildren<BoxCollider2D>(), new Rect(0f, -25f * scale, 300f * scale, 50f * scale));
        var top = _cursor[column];
        item.transform.localPosition = new Vector3(left - box.xMin, top - box.yMax, 0f);
        _buttons.Add(item.transform);
        _cursor[column] = top - box.height - ButtonGap;
        return Rect.MinMaxRect(left, top - box.height, left + box.width, top);
    }

    /// <summary>A button under the window, in <paramref name="column"/> (see <see cref="Buttons"/>) below the ones already there: the
    /// settings section button, or the red one (larger) for the main action. With <paramref name="enabled"/> false it stands in its place dim and takes no
    /// press: the row that says why is the window's to write.</summary>
    public void AddButton(UIText text, Action onClick, bool main = false, int column = 0, bool enabled = true)
    {
        var template = main && enabled && _screen.RedTemplate != null ? _screen.RedTemplate : _screen.SectionTemplate;
        var item = _screen.Button(template, enabled ? _screen.Text(text) : MenuText.Tint(_screen.Text(text), MenuTone.Dim), onClick);
        Place(item, column, main);
        if (!enabled && item.m_collider != null) item.m_collider.enabled = false;
        MenuScreen.Bright(item, main && enabled);
    }

    /// <summary>A button under the window that asks before it acts, in place and without a pop-up: the first press turns it into the red
    /// <paramref name="confirm"/> button with a <paramref name="cancel"/> button under it in the same column; <paramref name="onConfirm"/>
    /// runs only when <paramref name="confirm"/> is pressed. Cancel, a press anywhere else on the screen or a few seconds without one put it
    /// back. <paramref name="id"/> names what it acts on (for example the recording), so the asking state never carries over to another one.</summary>
    public void AddConfirmButton(string id, UIText text, UIText confirm, UIText cancel, Action onConfirm, bool main = false, int column = 0)
    {
        if (!MenuScreens.Asking(id)) { AddButton(text, () => MenuScreens.Ask(id), main, column); return; }
        var yes = _screen.Button(_screen.RedTemplate ?? _screen.SectionTemplate, _screen.Text(confirm), onConfirm);
        Place(yes, column, main);
        MenuScreen.Bright(yes, true);
        // Its own handler has nothing to do: any press on the screen ends the asking and builds the screen again.
        Place(_screen.Button(_screen.SectionTemplate, _screen.Text(cancel), () => { }), column, false);
    }
    internal void Finish()
    {
        // Glyphs the game's font lacks (Chinese, among others) are drawn by sub-mesh objects a text makes when it first builds them, normally
        // just before it renders: built after SetContentItems handed the rows to the window's clipping, they would show outside the window.
        foreach (var label in _window.GetComponentsInChildren<TMPro.TextMeshPro>(true)) label.ForceMeshUpdate(true);
        if (_fit)
        {
            var need = FramePad;
            foreach (var h in _rowHeights) need += h + MenuScreen.RowGap;
            if (need < _height)
            {
                _window.SetSize(new Vector2(_width, need));
                var up = _height - need;
                _under += up;
                foreach (var button in _buttons) button.localPosition += new Vector3(0f, up, 0f);
            }
        }
        _window.SetContentItems(_rows, MenuScreen.RowGap);
        _window.SetVisible(true);
    }
}

/// <summary>Opens, rebuilds and closes the one menu screen that can be open, on the settings page.</summary>
internal static class MenuScreens
{
    private static MenuBarButton? _button;
    private static MenuScreen? _open;
    private static CM_PageSettings? _page;
    private static readonly List<GameObject> PutAway = new();
    private static bool _switching;

    internal static bool IsOpen(MenuBarButton button) => _open != null && ReferenceEquals(_button, button);

    // The one toast of the open screen: its text, tone and the moment it goes (unscaled time), kept over rebuilds.
    private static string _toast = "";
    private static NoticeTone _toastTone;
    private static float _toastUntil = -1f;
    private static string? _toastAction;
    private static Action? _toastDo;

    internal static void Toast(string text, NoticeTone tone, float seconds, string? action, Action? onAction)
    {
        _toast = text ?? ""; _toastTone = tone; _toastUntil = ForgeCore.Frame.UnscaledTime + Math.Max(0.5f, seconds);
        _toastAction = action; _toastDo = onAction;
        if (_open?.ToastPiece is { } old && !old.WasCollected) old.SetActive(false);
        if (_open != null) Rebuild();
    }

    internal static bool ToastShowing => _toast.Length > 0 && ForgeCore.Frame.UnscaledTime < _toastUntil;

    internal static void ShowToast(MenuScreen screen)
    {
        if (ToastShowing) screen.ShowToast(_toast, _toastTone, _toastAction, _toastDo);
    }

    internal static void CloseToast()
    {
        _toast = ""; _toastUntil = -1f; _toastAction = null; _toastDo = null;
        if (_open?.ToastPiece is { } piece && !piece.WasCollected) piece.SetActive(false);
    }

    // The button of the open screen asking to be confirmed (MenuScreenWindow.AddConfirmButton) and when it gives up (unscaled time).
    private const float AskSeconds = 5f;
    private static string? _asking;
    private static float _askUntil;

    internal static bool Asking(string id) => _asking == id;

    internal static void Ask(string id)
    {
        _asking = id;
        _askUntil = ForgeCore.Frame.UnscaledTime + AskSeconds;
        Rebuild();
    }

    /// <summary>Ends the asking, if any; true when a button was asking.</summary>
    internal static bool StopAsking()
    {
        var was = _asking != null;
        _asking = null;
        return was;
    }

    /// <summary>Switches the main menu to the settings page and builds <paramref name="button"/>'s screen there.</summary>
    internal static bool Open(MenuBarButton button)
    {
        var layer = GuiManager.MainMenuLayer;
        if (layer == null || button.BuildScreen == null) return false;
        if (layer.GetPage(eCM_MenuPage.CMP_SETTINGS)?.TryCast<CM_PageSettings>() is not { } page) return false;
        Close();
        if (layer.m_currentPageEnum != eCM_MenuPage.CMP_SETTINGS)
        {
            _switching = true;
            try { layer.ChangePage(eCM_MenuPage.CMP_SETTINGS); }
            finally { _switching = false; }
        }
        _button = button;
        _page = page;
        Build();
        return _open != null;
    }

    internal static void Rebuild()
    {
        if (_button == null || _page == null) return;
        Discard();
        Build();
    }

    private static void Build()
    {
        var page = _page!;
        MenuScreen screen;
        try { screen = Templates(page, _button!.Mod.Texts); }
        catch (Exception error)
        {
            Plugin.Error("ui-menu-screen-cannot-be-shown", "Menu screen '" + _button!.Id + "' cannot be shown: " + error);
            Restore();
            _button = null;
            return;
        }
        try
        {
            _button.BuildScreen!(screen);
            screen.Finish();
        }
        catch (Exception error)
        {
            Plugin.Error("ui-menu-screen-could-not-be-built", "Menu screen '" + _button.Id + "' could not be built: " + error);
            Teardown(screen);
            Restore();
            _button = null;
            return;
        }
        _open = screen;
        Hide();
        MenuBar.ScreenChanged();
    }

    // The settings page's own pieces to copy: a section button, the red button, its first window and a row of it with an input.
    private static MenuScreen Templates(CM_PageSettings page, UITexts texts)
    {
        var holder = page.m_movingContentHolder ?? throw new InvalidOperationException("the settings page has no content holder");
        GameObject? section = null, red = null;
        for (var i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i).gameObject;
            if (child.name.StartsWith("ForgeUI.", StringComparison.Ordinal)) continue;
            if (red == null && child.name.StartsWith("CM_RedButtonFramed", StringComparison.Ordinal)) red = child;
            else if (section == null && child.name.StartsWith("CM_ButtonFramed", StringComparison.Ordinal)) section = child;
        }
        var windows = page.m_allSettingsWindows;
        var window = windows != null && windows.Count > 0 ? windows[0] : null;
        if (section == null || window == null) throw new InvalidOperationException("the settings page has not built its sections and windows yet");
        CM_SettingsItem? row = null;
        foreach (var item in window.GetComponentsInChildren<CM_SettingsItem>(true))
            if (row == null || (item.m_inputAlign != null && item.m_inputAlign.childCount > 0)) { row = item; if (item.m_inputAlign != null && item.m_inputAlign.childCount > 0) break; }
        if (row == null) throw new InvalidOperationException("the settings window has no rows to copy");
        return new MenuScreen(page, holder, texts, section, red, window, row);
    }

    /// <summary>Per frame: the screen closes when the settings page is gone, and the page's own sections and windows stay away while it
    /// is open (the page shows its current window again when it is enabled).</summary>
    internal static void Update()
    {
        if (_open == null) return;
        if (_page == null || _page.WasCollected || !_page.gameObject.activeInHierarchy) { Close(); return; }
        if (_toastUntil >= 0f && ForgeCore.Frame.UnscaledTime >= _toastUntil)
        {
            CloseToast();
        }
        if (_asking != null && ForgeCore.Frame.UnscaledTime >= _askUntil) { _asking = null; Rebuild(); }
        if (_open == null) return;
        Yield(FocusStateManager.CurrentState == eFocusState.GlobalPopupMessage);
        Hide();
        _open.Hold();
    }

    // While one of the game's pop-ups (the exit expedition question, a quit confirmation) is up, the screen's pieces are switched off, so
    // the game's pop-up is not drawn over by their text; they are switched on again when it closes, with their windows' state untouched.
    private static readonly List<GameObject> Yielded = new();

    private static void Yield(bool popup)
    {
        if (popup)
        {
            if (Yielded.Count > 0 || _open == null) return;
            foreach (var go in _open.Created)
                if (go != null && !go.WasCollected && go.activeSelf) { go.SetActive(false); Yielded.Add(go); }
            return;
        }
        foreach (var go in Yielded)
            if (go != null && !go.WasCollected) go.SetActive(true);
        Yielded.Clear();
    }

    internal static void Close()
    {
        _asking = null;
        if (_open == null) return;
        CloseToast();
        Discard();
        Restore();
        _button = null;
        _page = null;
        MenuBar.ScreenChanged();
    }

    private static void Discard()
    {
        if (_open == null) return;
        Teardown(_open);
        _open = null;
    }

    private static void Teardown(MenuScreen screen)
    {
        foreach (var route in screen.Routes) Input.Unroute(route);
        screen.Routes.Clear();
        foreach (var go in screen.Created)
            if (go != null && !go.WasCollected) { go.SetActive(false); Object.Destroy(go); }
        screen.Created.Clear();
        screen.Mine.Clear();
        Yielded.Clear();
    }

    // Everything the settings page shows in its moving content besides the screen and the resolution holder.
    private static void Hide()
    {
        var holder = _page?.m_movingContentHolder;
        if (holder == null || _open == null) return;
        var keep = _page!.m_resolutionHolder != null ? _page.m_resolutionHolder.gameObject.Pointer : IntPtr.Zero;
        // The page's hover tooltip lives here too: put away each frame, the game showing it again the next, it flashed for as long as the pointer stayed on a row.
        var tip = _page.m_tooltip != null ? _page.m_tooltip.gameObject.Pointer : IntPtr.Zero;
        for (var i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i).gameObject;
            if (!child.activeSelf || child.Pointer == keep || child.Pointer == tip || _open.Mine.Contains(child.Pointer)) continue;
            child.SetActive(false);
            PutAway.Add(child);
        }
    }

    private static void Restore()
    {
        foreach (var go in PutAway)
            if (go != null && !go.WasCollected) go.SetActive(true);
        PutAway.Clear();
    }

    /// <summary>Any page change (a menu bar button of the game, the settings button again) closes the screen first, so the page comes
    /// back as the game left it.</summary>
    internal static void PageChanging()
    {
        if (_switching) return;
        try { Close(); }
        catch (Exception error) { Plugin.Error("ui-closing-menu-screen-failed", "Closing the menu screen failed: " + error); }
    }

    [PatchGroup(PatchGroup.MenuScreen)]
    [HarmonyPatch(typeof(MainMenuGuiLayer), nameof(MainMenuGuiLayer.ChangePage))]
    private static class PageChange
    {
        [HarmonyPrefix]
        private static void Prefix() => PageChanging();
    }
}

/// <summary>How far a page's cursor panning has moved a piece under its moving content from its rest position, in the piece's parent's
/// units. The page moves <c>m_movingContentHolder</c> to its start offset plus <c>m_localHolderOffset</c> (<c>CM_PageBase.MoveContentHolder</c>);
/// zero when the piece is not under it.</summary>
internal static class PagePan
{
    internal static Vector3 Offset(CM_PageBase? page, Transform piece)
    {
        var holder = page?.m_movingContentHolder;
        var parent = piece.parent;
        if (page == null || holder == null || parent == null || holder.parent == null || !piece.IsChildOf(holder)) return Vector3.zero;
        var offset = page.m_localHolderOffset;
        var world = holder.parent.TransformVector(new Vector3(offset.x, offset.y, 0f));
        var local = parent.InverseTransformVector(world);
        return new Vector3(local.x, local.y, 0f);
    }
}
