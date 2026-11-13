using System;
using CellMenu;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The kit's tooltip in the lobby and menus: a small dark chamfered box with a thin outline and one line of text, over its button when
/// there is room inside the given bounds and under it otherwise (<see cref="TooltipLayout"/>), kept inside them sideways. Drawn on a
/// <see cref="MenuCanvas"/> in the button's own space.
/// </summary>
internal static class MenuTooltip
{
    private static readonly Color Box = new(0.03f, 0.04f, 0.047f, 0.94f);

    /// <summary>Draws <paramref name="text"/> for a button whose box is <paramref name="anchor"/>; <paramref name="size"/> is the text
    /// size and <paramref name="bounds"/> the space it must stay in.</summary>
    internal static void Draw(MenuCanvas canvas, Rect anchor, string text, float size, Rect bounds)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pad = size * 0.6f;
        var maxText = Math.Max(size * 4f, bounds.Width() - 2f * pad);
        var width = Math.Min(canvas.TextWidth(text, size), maxText) + 2f * pad;
        var height = size * 1.7f;
        var (x, y) = TooltipLayout.Place(anchor.CenterX(), anchor.CenterY(), anchor.Height() / 2f, width, height, size * 0.4f,
            bounds.CenterX(), bounds.CenterY(), bounds.Width() / 2f, bounds.Height() / 2f, out _);
        var box = new Rect(x - width / 2f, y - height / 2f, width, height);
        var cut = height * 0.22f;
        canvas.Plate(box, cut, Box);
        canvas.Frame(box, cut, MenuLook.LineDim);
        canvas.Text(text, x, y, size, MenuLook.Text, HudAlign.Centre, maxText);
    }
}

/// <summary>The tooltip of one menu bar button: a <see cref="MenuCanvas"/> on the item, drawn while the pointer is over it (the game's
/// own hover state, <c>CM_Item.m_isHovering</c>, polled), under the bar.</summary>
internal sealed class MenuBarTooltip
{
    private readonly MenuCanvas _canvas;
    private readonly Rect _box;
    private string? _shown;

    private MenuBarTooltip(MenuCanvas canvas, Rect box)
    {
        _canvas = canvas;
        _box = box;
    }

    internal static MenuBarTooltip? On(CM_MenuBarItem item)
    {
        var sprite = item.GetComponentInChildren<SpriteRenderer>(true);
        var label = item.GetComponentInChildren<TextMeshPro>(true);
        if (sprite == null || label == null)
        {
            Plugin.Error("ui-menu-bar-button-has-no-sprite", "Menu bar button '" + item.gameObject.name + "' has no sprite or text to draw a tooltip with; it shows none.");
            return null;
        }
        var box = MenuCanvas.BoxIn(item.transform, item.GetComponent<BoxCollider2D>(), new Rect(0f, -20f, 160f, 40f));
        return new MenuBarTooltip(new MenuCanvas(item.transform, "ForgeUI.MenuBarTooltip", sprite, label, MenuCanvas.TopOrder(item) + 5), box);
    }

    /// <summary>Per frame: draws <paramref name="text"/> while <paramref name="hover"/>, clears it otherwise; touches the canvas only on
    /// a change.</summary>
    internal void Show(string? text, bool hover)
    {
        var want = hover && !string.IsNullOrEmpty(text) ? text : null;
        if (want == _shown || !_canvas.Alive) return;
        _shown = want;
        _canvas.Begin();
        // The bar is the top of the screen: no room above, so the box goes under the button.
        if (want != null)
            MenuTooltip.Draw(_canvas, _box, want, _box.Height() * 0.42f,
                new Rect(_box.CenterX() - _box.Width() * 4f, _box.YMin() - _box.Height() * 6f, _box.Width() * 8f, _box.Height() * 6f + _box.Height()));
        _canvas.End();
    }

    internal void Destroy() => _canvas.Destroy();
}
