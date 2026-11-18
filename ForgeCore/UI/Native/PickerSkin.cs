using System;
using System.Collections.Generic;
using System.Text;
using CellMenu;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The picker's look, drawn over the game's own pieces with <see cref="MenuCanvas"/> (opaque plates, so the game's card frames, hexagon
/// and placeholder pictures underneath never show): list rows with the picture in a bay, the title and the category, a numbered amber
/// tag on a held entry and a white frame on the focused one; slot tiles with their number, picture and title, the slot being filled
/// framed white with a pointer into the list; chamfered tabs, the current one joined to the window; and the details box with the
/// picture large, the category, the title, key caps, the description and a row of readout blocks. Pictures are drawn in their own
/// colours (cropped copies, <see cref="IconSilhouette.Trimmed"/>); an accent colours only category marks, slot edges and the glow behind
/// the large picture. Each piece is redrawn only when the picker refreshes (a pick, a tab, a hover), never per frame.
/// </summary>
internal static class PickerSkin
{
    private static bool _clipFailed;

    /// <summary>Hands pieces made under the window's list to the window's clipping, so rows scrolled past the edge are cut like the
    /// game's own cards.</summary>
    internal static void Clip(CM_ScrollWindow window)
    {
        if (_clipFailed) return;
        try
        {
            var feeder = window.m_uiClipFeeder;
            if (feeder == null) return;
            feeder.Setup(true);
        }
        catch (Exception error)
        {
            _clipFailed = true;
            Plugin.Error("ui-picker-rows-could-not-be-handed", "The picker rows could not be handed to the list's clipping: " + error);
        }
    }

    internal static Sprite? Picture(Sprite? icon) => icon == null ? null : IconSilhouette.Trimmed(icon);

    internal static Rect Box(Component item, Rect fallback) =>
        MenuCanvas.BoxIn(item.transform, item.GetComponent<BoxCollider2D>() ?? item.GetComponentInChildren<BoxCollider2D>(true), fallback);
}

/// <summary>One list row (see <see cref="PickerSkin"/>), drawn on the game's card.</summary>
internal sealed class PickerRowSkin
{
    private readonly MenuCanvas _canvas;
    private readonly CM_LobbyScrollItem _card;

    private PickerRowSkin(MenuCanvas canvas, CM_LobbyScrollItem card)
    {
        _canvas = canvas;
        _card = card;
    }

    internal bool Grew => _canvas.TakeGrown();

    /// <summary>Where the last drawn control takes presses, in the card's own units (empty for a card row).</summary>
    internal Rect Active { get; private set; }

    internal Transform Space => _card.transform;

    private BoxCollider2D? _box;

    /// <summary>Keeps the row inside the list's visible band (<paramref name="bottom"/> to <paramref name="top"/>, world y): wholly inside it
    /// shows everything; partly past an edge it shows only its plates (the list's clipping cuts them; texts would show their glyphs past the
    /// edge); wholly past it shows nothing (<see cref="MenuCanvas.Show"/>). True when the row reaches below the band.</summary>
    internal bool Cull(float bottom, float top)
    {
        if (_box == null || _box.WasCollected) _box = _card.GetComponent<BoxCollider2D>() ?? _card.GetComponentInChildren<BoxCollider2D>(true);
        if (_box == null) return false;
        var t = _box.transform;
        float a = t.TransformPoint(new Vector3(0f, _box.offset.y - _box.size.y / 2f, 0f)).y, b = t.TransformPoint(new Vector3(0f, _box.offset.y + _box.size.y / 2f, 0f)).y;
        float low = Math.Min(a, b), high = Math.Max(a, b), slack = (high - low) * 0.02f;
        var past = high <= bottom || low >= top;
        _canvas.Show(!past, !past && low >= bottom - slack && high <= top + slack);
        return low < bottom - slack;
    }

    /// <summary>Draws the row as a control (an adjust page, <see cref="PickerSource.Controls"/>), with the same plate, frame and edge bar as a
    /// card row and the right edge held at <paramref name="listRight"/>.</summary>
    internal void DrawControl(UIControl control, bool focus, bool held, float listRight)
    {
        _canvas.Begin();
        Active = MenuControlLook.Row(_canvas, RowBox(listRight), control, focus, held);
        _canvas.End();
    }

    /// <summary>The card's box as it is now, its right edge held at <paramref name="listRight"/> (a world x), inset by 2.</summary>
    private Rect RowBox(float listRight)
    {
        var box = PickerSkin.Box(_card, new Rect(-400f, -60f, 800f, 120f));
        var space = _card.transform;
        var at = space.position;
        var edge = space.InverseTransformPoint(new Vector3(listRight, at.y, at.z)).x;
        float left = box.XMin(), bottom = box.YMin(), top = box.YMax();
        if (edge < box.XMax()) box = Rect.MinMaxRect(left, bottom, Math.Max(left + top - bottom, edge), top);
        return MenuCanvas.Inset(box, 2f);
    }

    internal static PickerRowSkin? On(CM_LobbyScrollItem card)
    {
        var like = card.GetComponentInChildren<SpriteRenderer>(true);
        var text = card.m_nameText ?? card.GetComponentInChildren<TextMeshPro>(true);
        if (like == null || text == null)
        {
            Plugin.Error("ui-picker-card-has-no-sprite", "A picker card has no sprite or text to draw its row with; it shows the game's card.");
            return null;
        }
        var canvas = new MenuCanvas(card.transform, "ForgeUI.Row", like, text, MenuCanvas.TopOrder(card) + 1);
        canvas.VeilOriginals();
        return new PickerRowSkin(canvas, card);
    }

    /// <summary>Draws a grid card (<see cref="PickerLayout.Grid"/>) over the card's box, which the picker made the card's cell: the picture
    /// large in a bay on top, the title and the category under it, each one line cut with an ellipsis (never drawn smaller); a card in use has
    /// an amber bar down its left edge and a tick in its top right corner.</summary>
    internal void DrawCard(PickerCard data, bool focus)
    {
        Active = default;
        var r = MenuCanvas.Inset(PickerSkin.Box(_card, new Rect(-170f, -115f, 340f, 230f)), 2f);
        float w = r.width, h = r.height, cut = Math.Min(14f, h * 0.06f), m = Math.Max(8f, h * 0.045f);
        var held = data.Equipped > 0;
        var c = _canvas;
        c.Begin();
        c.Plate(r, cut, focus ? MenuLook.Raised : MenuLook.Panel);
        c.Frame(r, cut, focus ? MenuLook.Line : held ? MenuLook.Faded(MenuLook.Amber, 0.8f) : MenuLook.LineDim);
        if (held) c.Rect(Rect.MinMaxRect(r.xMin + 2f, r.yMin + cut, r.xMin + 6f, r.yMax - cut), MenuLook.Amber);
        var titleSize = Math.Min(24f, h * 0.1f);
        var lineSize = titleSize * 0.72f;
        var hasLine = data.Subtitle.Length > 0;
        var textTop = r.yMin + m + (hasLine ? lineSize * 1.5f : 0f) + titleSize * 1.4f;
        var bay = Rect.MinMaxRect(r.xMin + m + 4f, textTop, r.xMax - m, r.yMax - m);
        c.Plate(bay, cut * 0.7f, MenuLook.Bay);
        var picture = PickerSkin.Picture(data.Icon);
        if (picture != null) c.Icon(picture, MenuCanvas.Inset(bay, m * 0.6f), Color.white);
        else if (data.Swatch is { } swatch) c.Plate(MenuCanvas.Inset(bay, m), cut * 0.6f, new Color(swatch.r, swatch.g, swatch.b, 1f));
        else c.Rect(new Rect(bay.center.x - bay.width * 0.1f, bay.center.y - 1f, bay.width * 0.2f, 2f), MenuLook.LineDim);
        if (held)
        {
            var side = Math.Min(30f, h * 0.13f);
            var tag = new Rect(bay.xMax - side - 6f, bay.yMax - side - 6f, side, side);
            c.Plate(tag, side * 0.25f, MenuLook.Amber);
            c.Picture(UIKitSprites.Tick, MenuCanvas.Inset(tag, side * 0.15f), MenuLook.Ink);
        }
        var x = r.xMin + m + 6f;
        var fit = r.xMax - m - x;
        var titleY = textTop - titleSize * 0.8f;
        c.Text(data.Title, x, titleY, titleSize, focus || held ? MenuLook.Line : MenuLook.Text, HudAlign.Left, fit, false, false);
        if (hasLine) c.Text(data.Subtitle, x, titleY - titleSize * 0.6f - lineSize * 0.75f, lineSize, data.Accent ?? MenuLook.Dim, HudAlign.Left, fit, false, false);
        c.End();
    }

    /// <summary>Draws the row over the card's box as it is now (the game sizes and places cards when it lays the list out, after
    /// the card was made), its right edge held at <paramref name="listRight"/>, a world x: the card's own box is as wide as the
    /// game's full-size list, which would run under the details.</summary>
    internal void Draw(PickerCard data, bool focus, float listRight)
    {
        Active = default;
        var r = RowBox(listRight);
        var h = r.height;
        var cy = r.center.y;
        var cut = h * 0.12f;
        var held = data.Equipped > 0;
        var c = _canvas;
        c.Begin();
        c.Plate(r, cut, focus ? MenuLook.Raised : MenuLook.Panel);
        c.Frame(r, cut, focus ? MenuLook.Line : held ? MenuLook.LineMid : MenuLook.LineDim);
        // A bar on the left edge: white on the focused row, the category's colour on the rest.
        c.Rect(Rect.MinMaxRect(r.xMin + 2f, r.yMin + h * 0.16f, r.xMin + (focus ? 6f : 4f), r.yMax - h * 0.16f), focus ? MenuLook.Line : MenuLook.Faded(data.Accent ?? MenuLook.LineDim, 0.7f));
        var m = h * 0.07f;
        var bay = Rect.MinMaxRect(r.xMin + m + 6f, r.yMin + m, r.xMin + m + 6f + h * 1.55f, r.yMax - m);
        c.Plate(bay, h * 0.08f, MenuLook.Bay);
        var picture = PickerSkin.Picture(data.Icon);
        if (picture != null) c.Icon(picture, MenuCanvas.Inset(bay, h * 0.05f), Color.white);
        else if (data.Swatch is { } swatch)
        {
            var tile = MenuCanvas.Inset(bay, h * 0.1f);
            c.Plate(tile, h * 0.08f, new Color(swatch.r, swatch.g, swatch.b, 1f));
            c.Frame(tile, h * 0.08f, focus ? MenuLook.Line : MenuLook.LineMid);
        }
        else c.Rect(new Rect(bay.center.x - bay.width * 0.15f, bay.center.y - 1f, bay.width * 0.3f, 2f), MenuLook.LineDim);
        var x = bay.xMax + h * 0.2f;
        var right = held ? r.xMax - h * 0.95f : r.xMax - h * 0.2f;
        var hasLine = data.Subtitle.Length > 0;
        var titleColour = focus || held ? MenuLook.Line : MenuLook.Text;
        // A title too long for one line even drawn smaller goes over two lines above the category line, rather than being cut.
        if (c.TextWidth(data.Title, h * 0.3f) * 0.6f > right - x)
            c.Paragraph(data.Title, Rect.MinMaxRect(x, hasLine ? cy - h * 0.1f : r.yMin + m, right, r.yMax - m), h * 0.22f, titleColour);
        else c.Text(data.Title, x, hasLine ? cy + h * 0.13f : cy, h * 0.3f, titleColour, HudAlign.Left, right - x);
        if (hasLine)
        {
            var s = h * 0.085f;
            var accent = data.Accent ?? MenuLook.Dim;
            var lineY = cy - h * 0.22f;
            c.Rect(new Rect(x, lineY - s / 2f, s, s), accent);
            c.Text(data.Subtitle, x + s * 1.8f, lineY, h * 0.17f, data.Accent ?? MenuLook.Dim, HudAlign.Left, right - x - s * 1.8f);
        }
        if (held)
        {
            var tag = new Rect(r.xMax - h * 0.85f, cy - h * 0.22f, h * 0.66f, h * 0.44f);
            c.Plate(tag, h * 0.1f, MenuLook.Amber);
            c.Text(MenuLook.Number(data.Equipped), tag.center.x, tag.center.y, h * 0.3f, MenuLook.Ink, HudAlign.Centre);
        }
        c.End();
    }
}

/// <summary>One slot tile in the column (see <see cref="PickerSkin"/>), drawn on the copy of the APPAREL button that takes its clicks.</summary>
internal sealed class PickerSlotSkin
{
    private readonly MenuCanvas _canvas;

    private PickerSlotSkin(MenuCanvas canvas) => _canvas = canvas;

    internal static PickerSlotSkin? On(CM_LobbyScrollItem slot, TextMeshPro? text)
    {
        var like = slot.GetComponentInChildren<SpriteRenderer>(true);
        if (like == null || text == null)
        {
            Plugin.Error("ui-picker-slot-has-no-sprite", "A picker slot has no sprite or text to draw its tile with; it shows the game's button.");
            return null;
        }
        var canvas = new MenuCanvas(slot.transform, "ForgeUI.SlotTile", like, text, MenuCanvas.TopOrder(slot) + 1);
        canvas.VeilOriginals();
        return new PickerSlotSkin(canvas);
    }

    /// <summary>Draws the tile over <paramref name="box"/> (the slot's clickable box in its own units); <paramref name="number"/> 0 shows no
    /// number but a tick when the slot's card is in use.</summary>
    internal void Draw(Rect box, PickerCard data, int number, bool chosen)
    {
        var c = _canvas;
        var r = box;
        var h = r.height;
        var w = r.width;
        const float cut = 12f;
        var picture = PickerSkin.Picture(data.Icon);
        var empty = picture == null;
        c.Begin();
        c.Plate(r, cut, chosen ? MenuLook.Raised : MenuLook.Panel);
        c.Frame(r, cut, chosen ? MenuLook.Line : MenuLook.LineDim);
        c.Rect(Rect.MinMaxRect(r.xMin + 3f, r.yMin + 3f, r.xMin + (chosen ? 9f : 7f), r.yMax - cut - 2f), data.Accent ?? (chosen ? MenuLook.Amber : MenuLook.LineDim));
        if (number > 0) c.Text(MenuLook.Number(number), r.xMax - 12f, r.yMax - 15f, 16f, chosen ? MenuLook.Line : MenuLook.Dim, HudAlign.Right);
        else if (data.Equipped > 0)
        {
            var tag = new Rect(r.xMax - 34f, r.yMax - 32f, 24f, 24f);
            c.Plate(tag, 6f, MenuLook.Amber);
            c.Picture(UIKitSprites.Tick, MenuCanvas.Inset(tag, 4f), MenuLook.Ink);
        }
        var nameColour = empty ? MenuLook.Dim : chosen ? MenuLook.Line : MenuLook.Text;
        Rect art;
        if (h >= 100f)
        {
            art = Rect.MinMaxRect(r.xMin + 24f, r.yMin + 46f, r.xMax - 40f, r.yMax - 10f);
            c.Text(data.Title, r.xMin + 18f, r.yMin + 25f, 22f, nameColour, HudAlign.Left, w - 36f);
        }
        else
        {
            art = Rect.MinMaxRect(r.xMin + 16f, r.yMin + 6f, r.xMin + 16f + h * 1.3f, r.yMax - 6f);
            c.Text(data.Title, art.xMax + 14f, r.center.y, 22f, nameColour, HudAlign.Left, r.xMax - 40f - art.xMax - 14f);
        }
        if (picture != null) c.Icon(picture, art, Color.white);
        else
        {
            var p = art.center;
            c.Rect(new Rect(p.x - 9f, p.y - 1f, 18f, 2f), MenuLook.LineDim);
            c.Rect(new Rect(p.x - 1f, p.y - 9f, 2f, 18f), MenuLook.LineDim);
        }
        if (chosen) c.Picture(UIKitSprites.Arrow(ArrowDirection.Right), new Rect(r.xMax + 4f, r.center.y - 9f, 12f, 18f), MenuLook.Line);
        c.End();
    }
}

/// <summary>The slot column's own panel: an opaque chamfered plate with a faint frame and the heading.</summary>
internal sealed class PickerColumnSkin
{
    private readonly MenuCanvas _canvas;

    internal PickerColumnSkin(Transform parent, SpriteRenderer like, TextMeshPro? text) =>
        _canvas = new MenuCanvas(parent, "ForgeUI.ColumnPanel", like, text, like.sortingOrder + 1);

    internal void Draw(Rect box, string heading)
    {
        var c = _canvas;
        c.Begin();
        c.Plate(box, 16f, MenuLook.Ink);
        c.Frame(box, 16f, MenuLook.LineMid);
        c.Text(heading, box.xMin + 18f, box.yMax - 22f, 16f, MenuLook.Dim, HudAlign.Left, box.width - 36f);
        c.Rect(Rect.MinMaxRect(box.xMin + 18f, box.yMax - 41f, box.xMax - 18f, box.yMax - 39.5f), MenuLook.LineDim);
        c.End();
    }
}

/// <summary>What the picker draws on the window itself, in window units: the title bar of a picker with one tab (a chamfered plate joined
/// to the frame like a current tab, the heading, and the gear button at its right end when the source has settings, amber while they are
/// open), and a downward arrow on the list's bottom edge while more cards lie below it.</summary>
internal sealed class PickerFrameSkin
{
    private readonly MenuCanvas _canvas, _more;

    internal PickerFrameSkin(Transform parent, SpriteRenderer like, TextMeshPro? text)
    {
        _canvas = new MenuCanvas(parent, "ForgeUI.Frame", like, text, like.sortingOrder + 2);
        _more = new MenuCanvas(parent, "ForgeUI.MoreBelow", like, null, like.sortingOrder + 40);
        _more.Show(false, false);
    }

    /// <summary>Whether the more-below arrow shows (it is drawn by <see cref="Draw"/>); touches nothing when unchanged.</summary>
    internal void ShowMore(bool shown) => _more.Show(shown, shown);

    /// <summary>Draws the title bar over <paramref name="bar"/> (an empty heading draws none) and the more-below arrow centred on
    /// <paramref name="edge"/> (shown by <see cref="ShowMore"/>). Returns the gear button's box, empty without one.</summary>
    internal Rect Draw(Rect bar, string heading, bool gear, bool gearOn, Rect edge)
    {
        var side = Math.Min(28f, edge.height);
        _more.Begin();
        _more.Plate(new Rect(edge.center.x - side * 1.4f, edge.center.y - side / 2f, side * 2.8f, side), side * 0.3f, MenuLook.Raised);
        _more.Frame(new Rect(edge.center.x - side * 1.4f, edge.center.y - side / 2f, side * 2.8f, side), side * 0.3f, MenuLook.LineMid);
        _more.Picture(UIKitSprites.Arrow(ArrowDirection.Down), new Rect(edge.center.x - side * 0.35f, edge.center.y - side * 0.35f, side * 0.7f, side * 0.7f), MenuLook.Amber);
        _more.End();
        var c = _canvas;
        var button = default(Rect);
        c.Begin();
        if (heading.Length > 0)
        {
            var h = bar.height;
            var cut = Math.Min(12f, h * 0.3f);
            c.Plate(bar, cut, MenuLook.Raised);
            c.Frame(bar, cut, MenuLook.Line);
            c.Rect(Rect.MinMaxRect(bar.xMin + cut, bar.yMax - 4f, bar.xMin + Math.Min(bar.width, 260f), bar.yMax - 1f), MenuLook.Amber);
            var right = bar.xMax - 16f;
            if (gear)
            {
                button = new Rect(bar.xMax - h - 6f, bar.yMin + 4f, h, h - 8f);
                c.Plate(button, cut * 0.6f, gearOn ? MenuLook.Faded(MenuLook.Amber, 0.25f) : MenuLook.Bay);
                c.Frame(button, cut * 0.6f, gearOn ? MenuLook.Amber : MenuLook.LineDim);
                var glyph = button.height * 0.62f;
                c.Picture(UIKitSprites.Gear, new Rect(button.center.x - glyph / 2f, button.center.y - glyph / 2f, glyph, glyph), gearOn ? MenuLook.Amber : MenuLook.Line);
                right = button.xMin - 16f;
            }
            c.Text(heading, bar.xMin + 18f, bar.center.y, h * 0.42f, MenuLook.Line, HudAlign.Left, right - bar.xMin - 18f, false, false);
        }
        c.End();
        return button;
    }

    internal void Destroy()
    {
        _canvas.Destroy();
        _more.Destroy();
    }
}

/// <summary>The tabs along the top of the window, each drawn on the game's own header (which keeps the click).</summary>
internal sealed class PickerTabsSkin
{
    private readonly List<(CM_ScrollWindowHeader Header, MenuCanvas? Canvas)> _tabs = new();

    internal void Draw(CM_ScrollWindow window, IReadOnlyList<string> names, int current)
    {
        var headers = window.m_headers;
        var count = headers == null ? 0 : Math.Min(names.Count, headers.Length);
        for (var i = 0; i < count; i++)
        {
            var header = headers![i];
            if (header == null) continue;
            if (i == _tabs.Count) _tabs.Add((header, Make(header)));
            else if (_tabs[i].Header.Pointer != header.Pointer)
            {
                _tabs[i].Canvas?.Destroy();
                _tabs[i] = (header, Make(header));
            }
            var canvas = _tabs[i].Canvas;
            if (canvas == null || !canvas.Alive) continue;
            // AddHeader just filled the header's label; the canvas draws the name, so the game's copy is emptied and turned off.
            canvas.SilenceOriginalTexts();
            var box = PickerSkin.Box(header, new Rect(-120f, -22f, 240f, 44f));
            var on = i == current;
            var cut = Math.Min(12f, box.height * 0.3f);
            canvas.Begin();
            if (on)
            {
                // Down over the window's top line, so the current tab and the window read as one piece.
                var tab = Rect.MinMaxRect(box.xMin, box.yMin - 6f, box.xMax, box.yMax);
                canvas.Plate(tab, cut, MenuLook.Raised);
                canvas.Frame(tab, cut, MenuLook.Line);
                canvas.Rect(Rect.MinMaxRect(box.xMin + cut, box.yMax - 4f, box.xMax - 3f, box.yMax - 1f), MenuLook.Amber);
                canvas.Text(names[i], box.xMin + 16f, box.center.y, box.height * 0.42f, MenuLook.Line, HudAlign.Left, box.width - 28f);
            }
            else
            {
                var tab = Rect.MinMaxRect(box.xMin, box.yMin + 2f, box.xMax, box.yMax - 4f);
                canvas.Plate(tab, cut, MenuLook.Ink);
                canvas.Frame(tab, cut, MenuLook.LineDim);
                canvas.Text(names[i], box.xMin + 16f, tab.center.y, box.height * 0.38f, MenuLook.Dim, HudAlign.Left, box.width - 28f);
            }
            canvas.End();
        }
        for (var i = count; i < _tabs.Count; i++) _tabs[i].Canvas?.Clear();
    }

    private static MenuCanvas? Make(CM_ScrollWindowHeader header)
    {
        var like = header.GetComponentInChildren<SpriteRenderer>(true);
        var text = header.GetComponentInChildren<TextMeshPro>(true);
        if (like == null || text == null)
        {
            Plugin.Error("ui-picker-tab-has-no-sprite", "A picker tab has no sprite or text to draw with; it shows the game's tab.");
            return null;
        }
        var canvas = new MenuCanvas(header.transform, "ForgeUI.Tab", like, text, MenuCanvas.TopOrder(header) + 1);
        canvas.VeilOriginals();
        return canvas;
    }

    internal void Destroy()
    {
        foreach (var (_, canvas) in _tabs) canvas?.Destroy();
        _tabs.Clear();
    }
}

internal enum PickerHitKind { Scheme, SchemePage, Control, ControlPage }

/// <summary>A press target the side panel drew, in the details box's units: <see cref="Box"/> takes the press, <see cref="Active"/> is the
/// span a control maps it to; <see cref="Index"/> is the scheme or control, or the page step (−1 or +1).</summary>
internal readonly struct PickerHit
{
    internal PickerHit(Rect box, Rect active, PickerHitKind kind, int index)
    {
        Box = box;
        Active = active;
        Kind = kind;
        Index = index;
    }

    internal Rect Box { get; }
    internal Rect Active { get; }
    internal PickerHitKind Kind { get; }
    internal int Index { get; }
}

/// <summary>The side panel's state the picker keeps: the shown pages and the control the cursor pressed or drags (−1 for none).</summary>
internal struct PickerPanelView
{
    internal int SchemePage, ControlPage, Focus, Drag;
}

/// <summary>The details box (see <see cref="PickerSkin"/>), drawn on the game's weapon info box.</summary>
internal sealed class PickerDetailSkin
{
    private const int MaxBadges = 12, MaxReadouts = 4;
    private readonly MenuCanvas _canvas, _rows;
    private readonly Rect _box;
    private readonly StringBuilder _body = new();

    private PickerDetailSkin(MenuCanvas canvas, MenuCanvas rows, Rect box)
    {
        _canvas = canvas;
        _rows = rows;
        _box = box;
    }

    internal static PickerDetailSkin? On(CM_ScrollWindowInfoBox box)
    {
        var like = box.m_infoMainIcon ?? box.GetComponentInChildren<SpriteRenderer>(true);
        var text = box.m_infoDescriptionText ?? box.m_infoMainTitleText;
        if (like == null || text == null)
        {
            Plugin.Error("ui-pickers-details-box-has-no-sprite", "The picker's details box has no sprite or text to draw with; it shows the game's box.");
            return null;
        }
        var area = MenuCanvas.BoxIn(box.transform, box.m_collider, new Rect(-300f, -350f, 600f, 700f));
        var order = MenuCanvas.TopOrder(box) + 1;
        // The control rows on a canvas of their own, ordered above everything the details draw (each piece takes the next order).
        return new PickerDetailSkin(new MenuCanvas(box.transform, "ForgeUI.Details", like, text, order),
            new MenuCanvas(box.transform, "ForgeUI.DetailRows", like, text, order + 1000), area);
    }

    internal void Destroy()
    {
        _canvas.Destroy();
        _rows.Destroy();
    }

    internal void Draw(PickerInfo info, string? warning) => Draw(info, warning, null, default, null);

    /// <summary>The details with <paramref name="panel"/> under them (null: the details alone): the picture, the category and the title, then
    /// each section of the panel with its heading, then the description in what room is left. Every press target drawn is added to
    /// <paramref name="hits"/> (cleared first), in the box's own units.</summary>
    internal void Draw(PickerInfo info, string? warning, PickerPanel? panel, PickerPanelView view, List<PickerHit>? hits)
    {
        hits?.Clear();
        var c = _canvas;
        var b = _box;
        var W = b.width;
        var H = b.height;
        var p = Math.Max(12f, W * 0.04f);
        var x = b.xMin + p;
        var inner = W - 2f * p;
        c.Begin();
        c.Plate(b, 0f, MenuLook.Ink);
        if (panel != null && hits != null)
        {
            DrawPanel(info, warning, panel, view, hits, x, inner, p);
            c.End();
            return;
        }
        _rows.Clear();
        var hero = new Rect(x, b.yMax - p - H * 0.4f, inner, H * 0.4f);
        Hero(info, hero, H);
        var y = hero.yMin - H * 0.045f;
        if (info.Subtitle.Length > 0)
        {
            var s = H * 0.02f;
            var accent = info.Accent ?? MenuLook.Dim;
            c.Rect(new Rect(x, y - s / 2f, s, s), accent);
            c.Text(info.Subtitle, x + s * 1.7f, y, H * 0.03f, accent, HudAlign.Left, inner - s * 1.7f);
        }
        y -= H * 0.065f;
        // A title too long for one line even drawn smaller takes two smaller lines in the same band, rather than being cut.
        if (c.TextWidth(info.Title, H * 0.065f) * 0.6f > inner) c.Paragraph(info.Title, Rect.MinMaxRect(x, y - H * 0.05f, x + inner, y + H * 0.035f), H * 0.04f, MenuLook.Line);
        else c.Text(info.Title, x, y, H * 0.065f, MenuLook.Line, HudAlign.Left, inner);
        y -= H * 0.07f;
        var badges = Math.Min(info.Badges.Count, MaxBadges);
        if (badges > 0)
        {
            var key = H * 0.056f;
            var gap = key * 0.2f;
            for (var i = 0; i < badges; i++)
            {
                var cap = new Rect(x + i * (key + gap), y - key / 2f, key, key);
                c.Plate(cap, key * 0.22f, MenuLook.Raised);
                c.Frame(cap, key * 0.22f, MenuLook.Line);
                var glyph = info.Badges[i];
                if (glyph != null) c.Picture(glyph, MenuCanvas.Inset(cap, key * 0.24f), MenuLook.Line);
            }
            y -= key / 2f + H * 0.035f;
        }
        else y += H * 0.02f;
        var readouts = Math.Min(info.Readouts.Count, MaxReadouts);
        var blockHeight = H * 0.15f;
        var bottom = readouts > 0 ? b.yMin + p + blockHeight + H * 0.03f : b.yMin + p;
        c.Rect(new Rect(x, y + H * 0.012f, inner, 1.5f), MenuLook.LineDim);
        _body.Clear();
        if (!string.IsNullOrEmpty(warning)) _body.Append("<color=#FF6B6B>").Append(RichText.Plain(warning)).Append("</color>\n\n");
        Stats(_body, info.Stats);
        _body.Append(info.Body);
        if (_body.Length > 0 && y > bottom) c.Paragraph(_body.ToString(), Rect.MinMaxRect(x, bottom, x + inner, y), H * 0.027f, MenuLook.Body, true);
        if (readouts > 0)
        {
            var gap = W * 0.02f;
            var width = (inner - gap * (readouts - 1)) / readouts;
            for (var i = 0; i < readouts; i++) Readout(c, info.Readouts[i], new Rect(x + i * (width + gap), b.yMin + p, width, blockHeight));
        }
        c.End();
    }

    /// <summary>The picture large in <paramref name="hero"/>: its glow, the colour it stands for, the picture and the corner brackets.</summary>
    private void Hero(PickerInfo info, Rect hero, float H)
    {
        var c = _canvas;
        c.Plate(hero, 14f, MenuLook.Bay);
        if (info.Accent is { } glow && info.Swatch == null)
        {
            var side = Math.Min(hero.width, hero.height) * 1.05f;
            c.Picture(UIKitSprites.GlowBlob, new Rect(hero.center.x - side / 2f, hero.center.y - side / 2f, side, side), MenuLook.Faded(glow, 0.3f));
        }
        var picture = PickerSkin.Picture(info.Icon);
        var art = MenuCanvas.Inset(hero, H * 0.035f);
        if (info.Swatch is { } swatch)
        {
            // Alone it fills the box; under a picture it is a band along the box's bottom, the picture above it.
            var colour = new Color(swatch.r, swatch.g, swatch.b, 1f);
            var band = picture == null ? art : Rect.MinMaxRect(art.xMin, art.yMin, art.xMax, art.yMin + art.height * 0.22f);
            c.Plate(band, 12f, colour);
            c.Frame(band, 12f, MenuLook.LineMid);
            if (picture != null) art = Rect.MinMaxRect(art.xMin, band.yMax + H * 0.01f, art.xMax, art.yMax);
        }
        if (picture != null) c.Icon(picture, art, Color.white);
        Brackets(c, MenuCanvas.Inset(hero, 8f));
    }

    /// <summary>The narrowest a scheme card is drawn, as a share of the box's height: as many cards share a row as fit at this width, the
    /// rest are paged.</summary>
    private const float SchemeWidth = 0.22f;

    /// <summary>The details over the panel (see <see cref="Draw(PickerInfo, string?, PickerPanel?, PickerPanelView, List{PickerHit}?)"/>).
    /// Details with no picture (settings) have no picture bay, so their text has the room.</summary>
    private void DrawPanel(PickerInfo info, string? warning, PickerPanel panel, PickerPanelView view, List<PickerHit> hits, float x, float inner, float p)
    {
        var c = _canvas;
        var b = _box;
        var H = b.height;
        var y = b.yMax - p;
        if (info.Icon != null || info.Swatch != null)
        {
            var hero = new Rect(x, y - H * 0.34f, inner, H * 0.34f);
            Hero(info, hero, H);
            y = hero.yMin;
        }
        y -= H * 0.04f;
        var titleSize = H * 0.048f;
        c.Text(info.Title, x, y - titleSize * 0.2f, titleSize, MenuLook.Line, HudAlign.Left, inner, false, false);
        y -= titleSize * 1.05f;
        if (info.Subtitle.Length > 0)
        {
            var s = H * 0.026f;
            c.Text(info.Subtitle, x, y, s, info.Accent ?? MenuLook.Dim, HudAlign.Left, inner, false, false);
            y -= s * 1.6f;
        }
        y -= H * 0.012f;
        var head = H * 0.024f;
        var row = H * 0.075f;
        var schemes = panel.Schemes.Count;
        var bottom = b.yMin + p;
        (_rowsX, _rowsWidth, _rowHeight, _rowsRoom) = (x, inner, row, 0);
        if (schemes > 0)
        {
            var gap = inner * 0.025f;
            var perPage = Math.Max(1, (int)((inner + gap) / (H * SchemeWidth + gap)));
            var pages = (schemes + perPage - 1) / perPage;
            var page = Math.Clamp(view.SchemePage, 0, pages - 1);
            Heading(panel.SchemesTitle, x, inner, y, head, page, pages, PickerHitKind.SchemePage, hits);
            y -= head * 1.6f;
            var cardHeight = H * 0.13f;
            var width = (inner - gap * (perPage - 1)) / perPage;
            for (var k = 0; k < perPage; k++)
            {
                var i = page * perPage + k;
                if (i >= schemes) break;
                var card = new Rect(x + k * (width + gap), y - cardHeight, width, cardHeight);
                Scheme(panel.Schemes[i], card);
                hits.Add(new PickerHit(card, card, PickerHitKind.Scheme, i));
            }
            y -= cardHeight + H * 0.015f;
            // The section's own rows (a scheme the player makes) right under the cards.
            _schemeRowsTop = y;
            y -= row * panel.SchemeControlCount;
            y -= H * 0.025f;
        }
        var controls = panel.Controls.Count;
        if (controls > 0)
        {
            var room = Math.Max(1, (int)((y - head * 1.6f - bottom) / row));
            var pages = (controls + room - 1) / room;
            var page = Math.Clamp(view.ControlPage, 0, pages - 1);
            Heading(panel.ControlsTitle, x, inner, y, head, page, pages, PickerHitKind.ControlPage, hits);
            y -= head * 1.6f;
            _rowsTop = y;
            _rowsFirst = page * room;
            _rowsRoom = room;
            y -= row * Math.Min(room, controls - _rowsFirst) + H * 0.02f;
        }
        if (panel.ControlCount > 0) DrawControls(panel, view, hits);
        else _rows.Clear();
        _body.Clear();
        if (!string.IsNullOrEmpty(warning)) _body.Append("<color=#FF6B6B>").Append(RichText.Plain(warning)).Append("</color>\n\n");
        _body.Append(info.Body);
        if (_body.Length > 0 && y - bottom > H * 0.04f) c.Paragraph(_body.ToString(), Rect.MinMaxRect(x, bottom, x + inner, y), H * 0.027f, MenuLook.Body, true);
    }

    /// <summary>Where the last full draw put the control rows: x, the width and one row's height; the top of the scheme section's rows; the
    /// top of the controls section's rows, the first of its controls shown and how many of its rows fit.</summary>
    private float _rowsX, _rowsWidth, _rowHeight, _schemeRowsTop, _rowsTop;
    private int _rowsFirst, _rowsRoom;

    /// <summary>The side panel's control rows alone, on their own canvas where the last full draw laid them out (each frame of a drag, so it
    /// touches nothing else): the scheme section's rows, then the controls section's page; with <paramref name="hits"/>, each row's press
    /// target is added to it, numbered as <see cref="PickerPanel.Control"/>.</summary>
    internal void DrawControls(PickerPanel panel, PickerPanelView view, List<PickerHit>? hits = null)
    {
        var c = _rows;
        var row = _rowHeight;
        var own = panel.SchemeControlCount;
        var shown = own + Math.Max(0, Math.Min(_rowsRoom, panel.Controls.Count - _rowsFirst));
        c.Begin();
        for (var k = 0; k < shown; k++)
        {
            var i = k < own ? k : own + _rowsFirst + (k - own);
            var top = k < own ? _schemeRowsTop - k * row : _rowsTop - (k - own) * row;
            var box = new Rect(_rowsX, top - row, _rowsWidth, row - 4f);
            var active = MenuControlLook.Row(c, box, panel.Control(i), i == view.Focus, i == view.Drag);
            hits?.Add(new PickerHit(box, active, PickerHitKind.Control, i));
        }
        c.End();
    }

    /// <summary>A section heading at <paramref name="y"/> with a rule under it and, over several pages, the page and its arrows at the right.</summary>
    private void Heading(string title, float x, float inner, float y, float size, int page, int pages, PickerHitKind arrows, List<PickerHit> hits)
    {
        var c = _canvas;
        var right = x + inner;
        if (pages > 1)
        {
            var side = size * 1.5f;
            var next = new Rect(right - side, y - side / 2f, side, side);
            // Room between the arrows for the page counter ("01/02") with a margin either side.
            var back = new Rect(next.xMin - side * 4f, next.yMin, side, side);
            Arrow(back, ArrowDirection.Left, page > 0);
            Arrow(next, ArrowDirection.Right, page < pages - 1);
            c.Text(MenuLook.Number(page + 1) + "/" + MenuLook.Number(pages), (back.xMax + next.xMin) / 2f, y, size * 0.85f, MenuLook.Dim, HudAlign.Centre);
            if (page > 0) hits.Add(new PickerHit(back, back, arrows, -1));
            if (page < pages - 1) hits.Add(new PickerHit(next, next, arrows, 1));
            right = back.xMin - size;
        }
        c.Text(title, x, y, size, MenuLook.Dim, HudAlign.Left, right - x, false, false);
        c.Rect(new Rect(x, y - size * 0.85f, inner, 1.5f), MenuLook.LineDim);
    }

    private void Arrow(Rect box, ArrowDirection direction, bool on)
    {
        _canvas.Plate(box, box.height * 0.2f, on ? MenuLook.Raised : MenuLook.Ink);
        _canvas.Frame(box, box.height * 0.2f, on ? MenuLook.LineMid : MenuLook.LineFaint);
        _canvas.Picture(UIKitSprites.Arrow(direction), MenuCanvas.Inset(box, box.height * 0.28f), on ? MenuLook.Line : MenuLook.LineDim);
    }

    /// <summary>A scheme card: its colours side by side across the top, its name under them in up to two lines; the scheme in use framed in
    /// amber with a tick.</summary>
    private void Scheme(PickerScheme scheme, Rect r)
    {
        var c = _canvas;
        var h = r.height;
        var cut = h * 0.1f;
        c.Plate(r, cut, scheme.Chosen ? MenuLook.Raised : MenuLook.Panel);
        c.Frame(r, cut, scheme.Chosen ? MenuLook.Amber : MenuLook.LineDim);
        var strip = Rect.MinMaxRect(r.xMin + h * 0.08f, r.yMin + h * 0.5f, r.xMax - h * 0.08f, r.yMax - h * 0.09f);
        var count = Math.Min(scheme.Colours.Count, 8);
        if (count > 0)
        {
            var each = strip.width / count;
            for (var i = 0; i < count; i++)
            {
                var colour = scheme.Colours[i];
                c.Rect(new Rect(strip.xMin + i * each, strip.yMin, each, strip.height), new Color(colour.r, colour.g, colour.b, 1f));
            }
        }
        else c.Rect(new Rect(strip.center.x - strip.width * 0.15f, strip.center.y - 1f, strip.width * 0.3f, 2f), MenuLook.LineDim);
        c.Frame(strip, 2f, MenuLook.LineFaint);
        if (scheme.Chosen)
        {
            var side = strip.height * 0.7f;
            var tag = new Rect(strip.xMax - side - 3f, strip.yMax - side - 3f, side, side);
            c.Plate(tag, side * 0.25f, MenuLook.Amber);
            c.Picture(UIKitSprites.Tick, MenuCanvas.Inset(tag, side * 0.15f), MenuLook.Ink);
        }
        c.Paragraph(scheme.Name, Rect.MinMaxRect(r.xMin + h * 0.08f, r.yMin + h * 0.05f, r.xMax - h * 0.08f, strip.yMin - h * 0.04f), h * 0.16f,
            scheme.Chosen ? MenuLook.Line : MenuLook.Text);
    }

    private static void Brackets(MenuCanvas c, Rect r)
    {
        const float arm = 16f, t = 2f;
        var colour = MenuLook.Faded(MenuLook.Line, 0.8f);
        c.Rect(new Rect(r.xMin, r.yMax - t, arm, t), colour);
        c.Rect(new Rect(r.xMin, r.yMax - arm, t, arm), colour);
        c.Rect(new Rect(r.xMax - arm, r.yMax - t, arm, t), colour);
        c.Rect(new Rect(r.xMax - t, r.yMax - arm, t, arm), colour);
        c.Rect(new Rect(r.xMin, r.yMin, arm, t), colour);
        c.Rect(new Rect(r.xMin, r.yMin, t, arm), colour);
        c.Rect(new Rect(r.xMax - arm, r.yMin, arm, t), colour);
        c.Rect(new Rect(r.xMax - t, r.yMin, t, arm), colour);
    }

    private static void Readout(MenuCanvas c, in PickerReadout readout, Rect r)
    {
        var h = r.height;
        var pad = Math.Min(14f, r.width * 0.08f);
        c.Plate(r, 10f, MenuLook.Panel);
        c.Frame(r, 10f, MenuLook.LineDim);
        c.Text(readout.Label, r.xMin + pad, r.yMax - h * 0.2f, h * 0.15f, MenuLook.Dim, HudAlign.Left, r.width - 2f * pad);
        if (readout.Endless) c.Picture(UIKitSprites.Infinity, new Rect(r.xMin + pad, r.yMin + h * 0.3f, h * 0.56f, h * 0.28f), MenuLook.Line);
        else
        {
            c.Quantity(readout.Value, readout.Unit, r.xMin + pad, r.yMin + h * 0.46f, h * 0.44f, MenuLook.Line, MenuLook.Dim, r.width - 2f * pad);
        }
        var track = new Rect(r.xMin + pad, r.yMin + h * 0.12f, r.width - 2f * pad, 4f);
        c.Rect(track, MenuLook.LineFaint);
        if (readout.Fill > 0f) c.Rect(new Rect(track.xMin, track.yMin, track.width * readout.Fill, track.height), MenuLook.Amber);
    }

    /// <summary>The stat rows as rich text over the description: the label, then one bar glyph per pip, lit ones in amber.</summary>
    private static void Stats(StringBuilder text, IReadOnlyList<PickerStat> stats)
    {
        if (stats == null || stats.Count == 0) return;
        foreach (var stat in stats)
            text.Append(RichText.Plain(stat.Label)).Append("<pos=45%><mspace=0.55em><color=#F2A33A>").Append('|', stat.Value)
                .Append("</color><color=#FFFFFF40>").Append('|', stat.Max - stat.Value).Append("</color></mspace>\n");
        text.Append('\n');
    }
}
