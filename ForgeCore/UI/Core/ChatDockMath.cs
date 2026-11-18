using System;

namespace ForgeUI;

/// <summary>
/// The chat dock's geometry (HUD units, x right, y up, in the weapon list's space). The game's chat box is laid out from the bottom up:
/// the input row (the line being typed, with its cursor), then the chat lines, the newest lowest. A mod's chat move reserves a slot
/// as tall as its line count plus the input row and a pad above and below; the chat box's lower left corner sits a pad inside the
/// slot's, lines that would reach above the slot are not drawn, and the lit plate behind the chat while the player types is only as
/// tall as what is shown, standing on the slot's bottom edge. While not typed in, only the newest few lines are drawn and they fade
/// out a while after the newest one came.
/// </summary>
internal static class ChatDockMath
{
    /// <summary>The space between the plate's edges and the chat's text.</summary>
    public const float Pad = 10f;

    /// <summary>The game's chat box, in its own units, until it is measured: the input row (20 for the typed line, 5 more to the first
    /// chat line) and the height of a chat line.</summary>
    public const float InputRow = 25f, Line = 20f;

    /// <summary>The chat lines a move shows unless it asks for another count, and the most it can ask for.</summary>
    public const int DefaultLines = 6, MaxLines = 20;

    public const float FadeInSeconds = 0.12f, FadeOutSeconds = 0.2f;

    /// <summary>The lines a move asks for, kept to 1 .. <see cref="MaxLines"/>.</summary>
    public static int Lines(int lines) => Math.Clamp(lines, 1, MaxLines);

    /// <summary>The height of a slot holding <paramref name="lines"/> chat lines of <paramref name="line"/> over an input row of
    /// <paramref name="inputRow"/>.</summary>
    public static float SlotHeight(int lines, float line, float inputRow) => 2f * Pad + Math.Max(0f, inputRow) + Lines(lines) * Math.Max(0f, line);

    /// <summary>The highest a chat line's top may reach in <paramref name="slot"/>: a line above it is not drawn.</summary>
    public static float Ceiling(HudBox slot) => slot.Top - Pad;

    /// <summary>The ceiling while the chat is not typed in: the newest <paramref name="idleLines"/> lines over the input row (of
    /// <paramref name="inputRow"/> and <paramref name="line"/> each), never above the slot's own ceiling.</summary>
    public static float IdleCeiling(HudBox slot, float inputRow, float line, int idleLines) =>
        Math.Min(Ceiling(slot), Corner(slot).Y + Math.Max(0f, inputRow) + Lines(idleLines) * Math.Max(0f, line));

    /// <summary>True for a chat line whose top is above <paramref name="ceiling"/> (half a unit of slack for measuring).</summary>
    public static bool Overflows(float ceiling, float lineTop) => lineTop > ceiling + 0.5f;

    /// <summary>How long the chat lines take to fade out once their idle time is over.</summary>
    public const float IdleFadeSeconds = 1.5f;

    /// <summary>The chat lines' opacity while the chat is not typed in, <paramref name="since"/> seconds after the newest line came (or
    /// the chat was closed): <paramref name="opacity"/> for <paramref name="idleSeconds"/>, then down to 0 over
    /// <see cref="IdleFadeSeconds"/>.</summary>
    public static float IdleAlpha(float since, float idleSeconds, float opacity)
    {
        var o = Math.Clamp(opacity, 0f, 1f);
        var over = Math.Max(0f, since) - Math.Max(0f, idleSeconds);
        return over <= 0f ? o : o * Math.Clamp(1f - over / IdleFadeSeconds, 0f, 1f);
    }

    /// <summary>Where the chat box's lower left corner goes on <paramref name="slot"/> (and on a plate drawn on it).</summary>
    public static (float X, float Y) Corner(HudBox slot) => (slot.Left + Pad, slot.Bottom + Pad);

    /// <summary>The plate of a moved chat: on the slot's bottom edge, as wide as the slot, and as tall as what is shown (the highest
    /// shown line's top, <paramref name="contentTop"/>, above the chat's bottom; the input row when no line is shown) plus the pads,
    /// never taller than the slot.</summary>
    public static HudBox Plate(HudBox slot, float contentTop, float inputRow)
    {
        var (_, bottom) = Corner(slot);
        var content = Math.Max(Math.Max(0f, inputRow), contentTop - bottom);
        var height = Math.Min(slot.Height, content + 2f * Pad);
        return new HudBox(slot.Left, slot.Bottom, slot.Right, slot.Bottom + height);
    }

    /// <summary>A chat text's size in the chat's own units for <paramref name="px"/> 1080p pixels: the kit's HUD text size
    /// (<paramref name="hudSize"/> for 20 px, HUD units) times the chat's units per HUD unit (<paramref name="scale"/>), never above
    /// 0.95 of a chat line (<paramref name="line"/>, chat units) so a line never runs into the next; 0 (keep the game's) without a size.</summary>
    public static float TextSize(float hudSize, float px, float scale, float line)
    {
        var size = hudSize * px / 20f * scale;
        return size > 0f ? Math.Min(size, 0.95f * line) : 0f;
    }

    /// <summary>The plate's opacity after <paramref name="dt"/> seconds: up to 1 while the chat is typed in, down to 0 after.</summary>
    public static float Fade(float alpha, bool typing, float dt)
    {
        var step = Math.Max(0f, dt) / (typing ? FadeInSeconds : FadeOutSeconds);
        return Math.Clamp(typing ? alpha + step : alpha - step, 0f, 1f);
    }
}
