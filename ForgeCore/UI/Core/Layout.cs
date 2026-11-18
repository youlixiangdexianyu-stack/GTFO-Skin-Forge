using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>Where menu bar buttons go: one after another to the right of the game's left-hand buttons (SETTINGS, RUNDOWN,
/// LOADOUT), each one step on, the way the game spaces its own: a button's width or the bar's left spacing, whichever is larger.
/// A button that would reach into the game's right-hand group (expedition icon, exit buttons) is not shown.</summary>
internal static class MenuBarLayout
{
    /// <summary>The x offset of each button, in layout order, or null for a button that does not fit before
    /// <paramref name="limit"/>. Buttons after one that does not fit do not fit either, so the order never changes.</summary>
    /// <param name="start">Where the first button goes: the end of the game's last visible left-hand button.</param>
    /// <param name="spacing">The bar's left spacing (<c>m_buttonLeftSpacing</c>).</param>
    /// <param name="widths">Each button's width, in layout order.</param>
    /// <param name="limit">The left edge of the game's right-hand group.</param>
    internal static float?[] Place(float start, float spacing, IReadOnlyList<float> widths, float limit)
    {
        var placed = new float?[widths.Count];
        var x = start;
        for (var i = 0; i < widths.Count; i++)
        {
            if (x + widths[i] > limit) break;
            placed[i] = x;
            x += Math.Max(widths[i], spacing);
        }
        return placed;
    }
}

/// <summary>How the picker (slot column, list and details) is scaled and placed so that all of it is on screen.</summary>
internal static class PickerFit
{
    /// <summary>The smallest scale the picker is shrunk to; below it text stops being readable, so the picker is centred and
    /// may then reach past the screen edges.</summary>
    internal const float MinScale = 0.6f;

    /// <summary>Scale and centre for a block spanning <paramref name="low"/> to <paramref name="high"/> in its own units (around
    /// its origin), to sit inside <paramref name="visibleLow"/> to <paramref name="visibleHigh"/> in its parent's units: never
    /// larger than its own size, never below <see cref="MinScale"/>, and centred in the visible span.</summary>
    internal static (float Scale, float Origin) Fit(float low, float high, float visibleLow, float visibleHigh)
    {
        var size = Math.Max(1e-3f, high - low);
        var room = Math.Max(0f, visibleHigh - visibleLow);
        var scale = Math.Clamp(room / size, MinScale, 1f);
        var origin = (visibleLow + visibleHigh) / 2f - scale * (low + high) / 2f;
        return (scale, origin);
    }
}

/// <summary>How a lobby strip's cells share the width of the APPAREL button above them: equal cells with a fixed gap, the first
/// starting at the button's left edge and the last ending at its right edge, so the row lines up with the buttons as one block.</summary>
internal static class LobbyStripLayout
{
    /// <summary>The gap between two cells, in the lobby bar's units.</summary>
    internal const float Gap = 8f;

    /// <summary>The width of each of <paramref name="count"/> cells across <paramref name="width"/>; zero for no cells.</summary>
    internal static float CellWidth(float width, int count) => count <= 0 ? 0f : Math.Max(0f, (width - Gap * (count - 1)) / count);

    /// <summary>The centre of cell <paramref name="index"/> when the row starts at <paramref name="left"/>.</summary>
    internal static float CellCentre(float left, float cellWidth, int index) => left + cellWidth / 2f + index * (cellWidth + Gap);
}

/// <summary>How a picture is drawn inside its icon cell: scaled up or down to the largest size that fits the cell with its width
/// to height ratio kept (like <c>Image.preserveAspect</c>), so a tall figure and a wide weapon both show whole, never past the cell.</summary>
internal static class IconFit
{
    /// <summary>The displayed width and height of a <paramref name="spriteWidth"/> x <paramref name="spriteHeight"/> picture in a
    /// <paramref name="cellWidth"/> x <paramref name="cellHeight"/> cell. Zero when either size is empty.</summary>
    internal static (float Width, float Height) Fit(float cellWidth, float cellHeight, float spriteWidth, float spriteHeight)
    {
        if (cellWidth <= 0f || cellHeight <= 0f || spriteWidth <= 0f || spriteHeight <= 0f) return (0f, 0f);
        var scale = Math.Min(cellWidth / spriteWidth, cellHeight / spriteHeight);
        return (spriteWidth * scale, spriteHeight * scale);
    }
}

/// <summary>How a block is fitted into a room: scaled down (never up) until it is no wider or taller than the room, then moved the least
/// that puts it inside, so a block that already fits is left exactly where it is.</summary>
internal static class BoxFit
{
    /// <summary>The scale and shift for <paramref name="block"/> (about the origin of its units: a point p goes to p * Scale + (X, Y)).</summary>
    internal static (float Scale, float X, float Y) Fit(HudBox block, HudBox room)
    {
        if (block.Width <= 0f || block.Height <= 0f || room.Width <= 0f || room.Height <= 0f) return (1f, 0f, 0f);
        var scale = Math.Min(1f, Math.Min(room.Width / block.Width, room.Height / block.Height));
        return (scale, Into(block.Left * scale, block.Right * scale, room.Left, room.Right), Into(block.Bottom * scale, block.Top * scale, room.Bottom, room.Top));
    }

    // The least move that puts low..high inside from..to (the span is never longer than the room here).
    private static float Into(float low, float high, float from, float to) => low < from ? from - low : high > to ? to - high : 0f;
}
