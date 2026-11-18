using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>How a docked chat looks in a mod's HUD instead of the game's (<see cref="GameChatMove.Look"/>): the game's chat backings
/// (its plate, input bar and corner sprites) and its help line are not drawn, its texts take the kit's HUD face at these sizes and
/// colours and end inside the dock, and the cursor is a bar in <see cref="Caret"/>. Colours written in the text itself (the players'
/// names) are kept. While not typed in, only the newest <see cref="IdleLines"/> lines are drawn, at <see cref="IdleOpacity"/>, on a
/// faint backing, and they fade out <see cref="IdleSeconds"/> after the newest line came or the chat was closed.</summary>
public sealed class ChatLook
{
    /// <summary>The chat lines' and the typed line's size, in 1080p pixels like every kit text.</summary>
    public float TextPx { get; set; } = 18f;

    public float InputPx { get; set; } = 19f;

    /// <summary>Chat messages and game events (any part not coloured in the text itself).</summary>
    public Color Text { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.88f);

    /// <summary>The game's alert lines.</summary>
    public Color Alert { get; set; } = UIKitColors.Orange;

    /// <summary>The line being typed.</summary>
    public Color Input { get; set; } = UIKitColors.Bright;

    /// <summary>The blinking cursor after the typed text.</summary>
    public Color Caret { get; set; } = UIKitColors.Cockpit;

    /// <summary>The newest chat lines drawn while the chat is not typed in (1 .. the move's lines).</summary>
    public int IdleLines { get; set; } = 3;

    /// <summary>How long those lines stay after the newest line came or the chat was closed, seconds; then they fade out.</summary>
    public float IdleSeconds { get; set; } = 10f;

    /// <summary>Those lines' opacity (times the game's own fade).</summary>
    public float IdleOpacity { get; set; } = 0.85f;
}

/// <summary>
/// Puts a <see cref="ChatLook"/> on the game's chat box and takes it off again exactly. Everything changed is remembered per object
/// (renderers switched off, each text's face, material, size, style, colour, width and overflow, each line's type colours, the cursor,
/// the lines' opacity) and given back when the look ends (the move is released, another move wins, the chat box changes or the level
/// ends). Lines the game adds while the look is on take it the first time they are seen.
/// </summary>
internal static class ChatSkin
{
    private struct TextWas
    {
        internal TextMeshPro Text;
        internal TMP_FontAsset Font;
        internal Material Material;
        internal float Size;
        internal FontStyles Style;
        internal Color Colour;
        internal bool Coloured;
        internal bool Fitted;
        internal Vector2 SizeDelta;
        internal Vector3 Position;
        internal TextOverflowModes Overflow;
        internal bool Wrapping;
    }

    private struct LineWas
    {
        internal PUI_GameEventLog_Item Item;
        internal Color In, Out, Event, Alert, Positive;
    }

    /// <summary>A chat line's opacity: the game's own (it fades a line through its alpha) and what the look last wrote over it.</summary>
    private struct AlphaWas
    {
        internal TextMeshPro Text;
        internal Renderer Renderer;
        internal GameObject Object;
        internal float Game, Wrote;
    }

    private static readonly Dictionary<IntPtr, TextWas> Texts = new();
    private static readonly Dictionary<IntPtr, LineWas> Lines = new();
    private static readonly Dictionary<IntPtr, Renderer> Off = new();
    private static readonly List<AlphaWas> Alphas = new();
    private static PUI_GameEventLog? _log;
    private static string? _caretWas;
    private static ChatLook? _look;

    internal static bool On => _look != null;

    /// <summary>The look on <paramref name="log"/> (again for anything the game added or switched back on). <paramref name="scale"/>
    /// is the chat's own units per HUD unit; every text ends at <paramref name="right"/> (HUD units), shortened with an ellipsis.</summary>
    internal static void Apply(PUI_GameEventLog log, ChatLook look, float scale, float right)
    {
        if (_log != null && (_log.WasCollected || _log.Pointer != log.Pointer)) Restore();
        _log = log;
        _look = look;
        var line = log.m_logItemHeight > 0f ? log.m_logItemHeight : ChatDockMath.Line;
        foreach (var sprite in log.GetComponentsInChildren<SpriteRenderer>(true)) TurnOff(sprite);
        // The game's help line ("[Return] to Type, ...") is longer than a dock: not drawn.
        var help = log.m_txtHelp;
        if (help != null && !help.WasCollected) TurnOff(help.renderer);
        // The typed line keeps its own box: cut, it would lose its end and the cursor.
        Skin(log.m_txtInput, Size(look.InputPx, scale, line), look.Input, bold: true, null);
        var items = log.m_logItems;
        if (items != null)
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected) continue;
                var text = item.m_text;
                Skin(text, Size(look.TextPx, scale, line), null, bold: false, right);
                Recolour(item, look);
                if (text != null && !text.WasCollected) Track(text);
            }
        _caretWas ??= log.m_blinkSuffix;
        var caret = "<color=#" + ColorUtility.ToHtmlStringRGB(look.Caret) + ">|</color>";
        if (log.m_blinkSuffix != caret) log.m_blinkSuffix = caret;
    }

    private static void TurnOff(Renderer? renderer)
    {
        if (renderer == null || renderer.WasCollected || !renderer.enabled) return;
        renderer.enabled = false;
        Off[renderer.Pointer] = renderer;
    }

    private static float Size(float px, float scale, float line) => ChatDockMath.TextSize(Hud.FontSize, px, scale, line);

    private static void Skin(TextMeshPro? text, float size, Color? colour, bool bold, float? right)
    {
        if (text == null || text.WasCollected) return;
        var key = text.Pointer;
        if (!Texts.TryGetValue(key, out var was))
        {
            was = new TextWas
            {
                Text = text,
                Font = text.font,
                Material = text.fontSharedMaterial,
                Size = text.fontSize,
                Style = text.fontStyle,
                Colour = text.color,
                Coloured = colour != null,
            };
            Texts[key] = was;
        }
        var kit = Hud.KitFaces;
        var font = kit != null ? bold ? kit.Bold : kit.Regular : Hud.GameFace;
        if (font != null && text.font != font)
        {
            text.font = font;
            text.fontSharedMaterial = kit != null ? font.material : Hud.GameMaterial ?? font.material;
        }
        if (text.fontStyle != FontStyles.Normal) text.fontStyle = FontStyles.Normal;
        if (size > 0f && Math.Abs(text.fontSize - size) > 0.01f) text.fontSize = size;
        if (colour is { } c)
        {
            var now = text.color;
            if (now.r != c.r || now.g != c.g || now.b != c.b) text.color = new Color(c.r, c.g, c.b, now.a * c.a);
        }
        if (right is { } edge) Fit(text, key, ref was, edge);
    }

    /// <summary>The text's box ends at <paramref name="right"/> (HUD units) with its left edge kept, one line, an ellipsis where it is
    /// cut: the game's chat box is 450 of its units wide, wider than a dock (720 HUD units at the game's 1.6 HUD units a chat unit).
    /// Left-aligned texts only (the chat's are); measured through the chat's camera like the dock.</summary>
    private static void Fit(TextMeshPro text, IntPtr key, ref TextWas was, float right)
    {
        if (text.horizontalAlignment != HorizontalAlignmentOptions.Left) return;
        var rt = text.rectTransform;
        var r = rt.rect;
        if (!ChatDock.ToHud(rt.TransformPoint(new Vector3(r.XMin(), 0f, 0f)), out var left, out _)
            || !ChatDock.ToHud(rt.TransformPoint(new Vector3(r.XMin() + 100f, 0f, 0f)), out var hundred, out _)) return;
        var hud = hundred - left;
        if (hud < 0.01f) return;
        var width = Math.Max(0f, (right - left) * 100f / hud);
        if (Math.Abs(r.Width() - width) < 0.5f && text.overflowMode == TextOverflowModes.Ellipsis && !text.enableWordWrapping) return;
        if (!was.Fitted)
        {
            was.Fitted = true;
            was.SizeDelta = rt.sizeDelta;
            was.Position = rt.localPosition;
            was.Overflow = text.overflowMode;
            was.Wrapping = text.enableWordWrapping;
            Texts[key] = was;
        }
        var grow = width - r.Width();
        rt.sizeDelta += new Vector2(grow, 0f);
        // The left edge stays: the box grows or shrinks about its pivot.
        rt.localPosition += rt.localRotation * new Vector3(rt.pivot.x * grow * rt.localScale.x, 0f, 0f);
        if (text.overflowMode != TextOverflowModes.Ellipsis) text.overflowMode = TextOverflowModes.Ellipsis;
        if (text.enableWordWrapping) text.enableWordWrapping = false;
    }

    private static void Track(TextMeshPro text)
    {
        var key = text.Pointer;
        for (var i = 0; i < Alphas.Count; i++) if (Alphas[i].Text.Pointer == key) return;
        var a = text.color.a;
        Alphas.Add(new AlphaWas { Text = text, Renderer = text.renderer, Object = text.gameObject, Game = a, Wrote = a });
    }

    /// <summary>Every frame while the look is on: each chat line drawn at the game's own opacity times <paramref name="factor"/>. A line
    /// whose opacity is not what the look last wrote was set by the game (its fade): that is its own opacity from then on. Returns the
    /// highest opacity of a line drawn now (0 with none).</summary>
    internal static float Fade(float factor)
    {
        var shown = 0f;
        for (var i = Alphas.Count - 1; i >= 0; i--)
        {
            var w = Alphas[i];
            var text = w.Text;
            if (text == null || text.WasCollected) { Alphas.RemoveAt(i); continue; }
            var colour = text.color;
            if (Math.Abs(colour.a - w.Wrote) > 1e-4f) w.Game = colour.a;
            var target = w.Game * factor;
            if (Math.Abs(colour.a - target) > 1e-4f) text.color = new Color(colour.r, colour.g, colour.b, target);
            w.Wrote = target;
            Alphas[i] = w;
            if (target > shown && w.Renderer != null && !w.Renderer.WasCollected && w.Renderer.enabled && w.Object.activeInHierarchy) shown = target;
        }
        return shown;
    }

    /// <summary>A chat line's type colours (the game colours a line by them and fades it through its alpha) set to the look's, and the
    /// line's current colour moved from its type's old colour to the new one, its alpha kept.</summary>
    private static void Recolour(PUI_GameEventLog_Item item, ChatLook look)
    {
        var key = item.Pointer;
        if (Lines.ContainsKey(key)) return;
        var was = new LineWas
        {
            Item = item,
            In = item.m_colorChatMsgIn,
            Out = item.m_colorChatMsgOut,
            Event = item.m_colorGameEvent,
            Alert = item.m_colorAlert,
            Positive = item.m_colorPositivePop,
        };
        Lines[key] = was;
        item.m_colorChatMsgIn = item.m_colorChatMsgOut = item.m_colorGameEvent = item.m_colorPositivePop = look.Text;
        item.m_colorAlert = look.Alert;
        var text = item.m_text;
        if (text == null || text.WasCollected) return;
        var now = text.color;
        var to = Same(now, was.Alert) ? look.Alert : look.Text;
        text.color = new Color(to.r, to.g, to.b, now.a);
    }

    private static bool Same(Color a, Color b) => Math.Abs(a.r - b.r) < 0.01f && Math.Abs(a.g - b.g) < 0.01f && Math.Abs(a.b - b.b) < 0.01f;

    /// <summary>Everything the look changed, back as it was.</summary>
    internal static void Restore()
    {
        if (_look == null && Texts.Count == 0 && Off.Count == 0 && Lines.Count == 0 && Alphas.Count == 0) return;
        Fade(1f);
        Alphas.Clear();
        foreach (var renderer in Off.Values)
            if (renderer != null && !renderer.WasCollected) renderer.enabled = true;
        Off.Clear();
        foreach (var line in Lines.Values)
        {
            var item = line.Item;
            if (item == null || item.WasCollected) continue;
            // A line shown now goes back to its type's colour (alert or message; the game colours it again when it reuses the line).
            var text = item.m_text;
            if (text != null && !text.WasCollected)
            {
                var now = text.color;
                var back = Same(now, item.m_colorAlert) ? line.Alert : line.In;
                text.color = new Color(back.r, back.g, back.b, now.a);
            }
            item.m_colorChatMsgIn = line.In;
            item.m_colorChatMsgOut = line.Out;
            item.m_colorGameEvent = line.Event;
            item.m_colorAlert = line.Alert;
            item.m_colorPositivePop = line.Positive;
        }
        Lines.Clear();
        foreach (var was in Texts.Values)
        {
            var text = was.Text;
            if (text == null || text.WasCollected) continue;
            text.font = was.Font;
            text.fontSharedMaterial = was.Material;
            text.fontSize = was.Size;
            text.fontStyle = was.Style;
            if (was.Fitted)
            {
                var rt = text.rectTransform;
                rt.sizeDelta = was.SizeDelta;
                rt.localPosition = was.Position;
                text.overflowMode = was.Overflow;
                text.enableWordWrapping = was.Wrapping;
            }
            if (!was.Coloured) continue;
            var now = text.color;
            text.color = new Color(was.Colour.r, was.Colour.g, was.Colour.b, now.a);
        }
        Texts.Clear();
        if (_log != null && !_log.WasCollected && _caretWas != null) _log.m_blinkSuffix = _caretWas;
        _caretWas = null;
        _log = null;
        _look = null;
    }
}
