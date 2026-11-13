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

    /// <summary>Draws the row over the card's box as it is now (the game sizes and places cards when it lays the list out, after
    /// the card was made), its right edge held at <paramref name="listRight"/>, a world x: the card's own box is as wide as the
    /// game's full-size list, which would run under the details.</summary>
    internal void Draw(PickerCard data, bool focus, float listRight)
    {
        var box = PickerSkin.Box(_card, new Rect(-400f, -60f, 800f, 120f));
        var edge = _card.transform.InverseTransformPoint(new Vector3(listRight, _card.transform.position.y, _card.transform.position.z)).x;
        if (edge < box.xMax) box = Rect.MinMaxRect(box.xMin, box.yMin, Math.Max(box.xMin + box.height, edge), box.yMax);
        var r = MenuCanvas.Inset(box, 2f);
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
        else c.Rect(new Rect(bay.center.x - bay.width * 0.15f, bay.center.y - 1f, bay.width * 0.3f, 2f), MenuLook.LineDim);
        var x = bay.xMax + h * 0.2f;
        var right = held ? r.xMax - h * 0.95f : r.xMax - h * 0.2f;
        var hasLine = data.Subtitle.Length > 0;
        c.Text(data.Title, x, hasLine ? cy + h * 0.13f : cy, h * 0.3f, focus || held ? MenuLook.Line : MenuLook.Text, HudAlign.Left, right - x);
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

    /// <summary>Draws the tile over <paramref name="box"/> (the slot's clickable box in its own units).</summary>
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
        c.Text(MenuLook.Number(number), r.xMax - 12f, r.yMax - 15f, 16f, chosen ? MenuLook.Line : MenuLook.Dim, HudAlign.Right);
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

/// <summary>The details box (see <see cref="PickerSkin"/>), drawn on the game's weapon info box.</summary>
internal sealed class PickerDetailSkin
{
    private const int MaxBadges = 12, MaxReadouts = 4;
    private readonly MenuCanvas _canvas;
    private readonly Rect _box;
    private readonly StringBuilder _body = new();

    private PickerDetailSkin(MenuCanvas canvas, Rect box)
    {
        _canvas = canvas;
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
        return new PickerDetailSkin(new MenuCanvas(box.transform, "ForgeUI.Details", like, text, MenuCanvas.TopOrder(box) + 1), area);
    }

    internal void Destroy() => _canvas.Destroy();

    internal void Draw(PickerInfo info, string? warning)
    {
        var c = _canvas;
        var b = _box;
        var W = b.width;
        var H = b.height;
        var p = Math.Max(12f, W * 0.04f);
        var x = b.xMin + p;
        var inner = W - 2f * p;
        c.Begin();
        c.Plate(b, 0f, MenuLook.Ink);
        var hero = new Rect(x, b.yMax - p - H * 0.4f, inner, H * 0.4f);
        c.Plate(hero, 14f, MenuLook.Bay);
        if (info.Accent is { } glow)
        {
            var side = Math.Min(hero.width, hero.height) * 1.05f;
            c.Picture(UIKitSprites.GlowBlob, new Rect(hero.center.x - side / 2f, hero.center.y - side / 2f, side, side), MenuLook.Faded(glow, 0.3f));
        }
        var picture = PickerSkin.Picture(info.Icon);
        if (picture != null) c.Icon(picture, MenuCanvas.Inset(hero, H * 0.035f), Color.white);
        Brackets(c, MenuCanvas.Inset(hero, 8f));
        var y = hero.yMin - H * 0.045f;
        if (info.Subtitle.Length > 0)
        {
            var s = H * 0.02f;
            var accent = info.Accent ?? MenuLook.Dim;
            c.Rect(new Rect(x, y - s / 2f, s, s), accent);
            c.Text(info.Subtitle, x + s * 1.7f, y, H * 0.03f, accent, HudAlign.Left, inner - s * 1.7f);
        }
        y -= H * 0.065f;
        c.Text(info.Title, x, y, H * 0.065f, MenuLook.Line, HudAlign.Left, inner);
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
