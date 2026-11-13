using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>The game HUD's look as colours: the text, the accent and the translucent slabs the weapon list is drawn with.</summary>
public static partial class UIKitColors
{
    /// <summary>Normal text on the HUD.</summary>
    public static readonly Color Text = new(0.871f, 0.886f, 0.875f, 0.92f);
    /// <summary>Emphasised text.</summary>
    public static readonly Color Bright = new(0.957f, 0.965f, 0.957f, 1f);
    /// <summary>Unavailable or secondary text.</summary>
    public static readonly Color Dim = new(0.871f, 0.886f, 0.875f, 0.38f);
    /// <summary>The accent (the colour of "ready").</summary>
    public static readonly Color Accent = new(1f, 0.561f, 0.169f, 1f);
    /// <summary>Text on an <see cref="Accent"/> fill.</summary>
    public static readonly Color OnAccent = new(0.086f, 0.086f, 0.086f, 1f);
    /// <summary>Caution (a bar running low, a warning): the game's low-ammunition notice colour once <see cref="Refresh"/> has read it.</summary>
    public static Color Warn { get; private set; } = new(0.98f, 0.78f, 0.25f, 1f);
    /// <summary>Danger (a bar nearly empty, an alert): the game's red, #E24851 sampled from the game's chat line.</summary>
    public static Color Alert { get; private set; } = Hex(0xE24851);
    /// <summary>Good news (a pickup, a revive): the game's green pop.</summary>
    public static Color Good { get; private set; } = new(0.5f, 0.86f, 0.5f, 1f);
    /// <summary>The translucent grey behind values.</summary>
    public static readonly Color Slab = new(0.588f, 0.604f, 0.596f, 0.20f);
    /// <summary>Thin lines and outlines.</summary>
    public static readonly Color Line = new(0.871f, 0.886f, 0.875f, 0.45f);
    /// <summary>The dark behind a cooldown sweep.</summary>
    public static readonly Color Shade = new(0.039f, 0.047f, 0.051f, 0.72f);

    /// <summary>The colour of a notice tone.</summary>
    public static Color Of(NoticeTone tone) => tone switch
    {
        NoticeTone.Good => Good,
        NoticeTone.Warn => Warn,
        NoticeTone.Alert => Alert,
        _ => Accent
    };

    /// <summary>The colour of a bar tone, <paramref name="normal"/> when it is normal.</summary>
    public static Color Of(MeterTone tone, Color normal) => tone switch
    {
        MeterTone.Warn => Warn,
        MeterTone.Critical => Alert,
        _ => normal
    };

    /// <summary>The cockpit templates' single main colour, ice white #D9F2FA (lines, texts and fills of segment bars, arcs, the
    /// loadout, teammates, silhouettes).</summary>
    public static readonly Color Cockpit = new(0.851f, 0.949f, 0.980f, 1f);

    /// <summary>Cockpit cyan #5CD6FF: shields only.</summary>
    public static readonly Color Cyan = new(0.361f, 0.839f, 1f, 1f);

    /// <summary>Cockpit orange #FF7A2E: what is selected or in effect (the held item, a running ability, a speaker, a layer tag).</summary>
    public static readonly Color Orange = new(1f, 0.478f, 0.180f, 1f);

    /// <summary>Cockpit amber #FFB547: caution (a value running low, heat building, an awake enemy).</summary>
    public static readonly Color Amber = new(1f, 0.710f, 0.278f, 1f);

    /// <summary>Cockpit red #FF3B3B: danger (nearly empty, overheated, a hunting enemy, low ammo).</summary>
    public static readonly Color Red = new(1f, 0.231f, 0.231f, 1f);

    /// <summary>Cockpit teal #4F8A94: sleeping enemies.</summary>
    public static readonly Color Teal = new(0.310f, 0.541f, 0.580f, 1f);

    /// <summary>Cockpit plate #08131A: the dark backing behind blocks of text that must stay legible (use at 26–42 %).</summary>
    public static readonly Color Plate = new(0.031f, 0.075f, 0.102f, 1f);

    /// <summary>The colour of a tone in the cockpit templates: <paramref name="normal"/>, <see cref="Amber"/> when low,
    /// <see cref="Red"/> when critical.</summary>
    public static Color Tone(MeterTone tone, Color normal) => tone switch
    {
        MeterTone.Warn => Amber,
        MeterTone.Critical => Red,
        _ => normal
    };
    /// <summary><paramref name="colour"/> with its opacity multiplied by <paramref name="alpha"/>.</summary>
    public static Color Faded(Color colour, float alpha) => new(colour.r, colour.g, colour.b, colour.a * alpha);
}

/// <summary>Setting HUD pieces without waste: every call writes to the game's objects only when the value changed, and none
/// allocates once its strings exist. Pieces are placed by their right edge and vertical centre in the element's units.</summary>
public static class HudDraw
{
    private static readonly Dictionary<IntPtr, string> Shown = new();
    private static readonly Dictionary<IntPtr, (string Value, float Size, float Width)> Measured = new();

    internal static void Forget()
    {
        Shown.Clear();
        Measured.Clear();
    }

    public static void SetActive(Component component, bool active)
    {
        if (component.gameObject.activeSelf != active) component.gameObject.SetActive(active);
    }

    /// <summary>Sets a text, skipping the game's call (which allocates) when it already shows <paramref name="value"/>. Only for
    /// texts set through this method.</summary>
    public static void SetText(TextMeshPro text, string value)
    {
        var key = text.Pointer;
        if (Shown.TryGetValue(key, out var shown) && (ReferenceEquals(shown, value) || string.Equals(shown, value, StringComparison.Ordinal))) return;
        text.SetText(value);
        Shown[key] = value;
    }

    /// <summary>Places a sprite as a box whose right edge is <paramref name="right"/> and vertical centre <paramref name="centre"/>.
    /// <paramref name="aspect"/> corrects sprites that are not one unit tall (<see cref="UIKitSprites.Fade"/> is 1/16 as tall as wide).</summary>
    public static void Box(SpriteRenderer sprite, float right, float centre, float width, float height, Color colour, float aspect = 1f)
    {
        SetActive(sprite, true);
        var transform = sprite.transform;
        var position = new Vector3(right - width / 2f, centre, 0f);
        if (transform.localPosition != position) transform.localPosition = position;
        var scale = new Vector3(width, height * aspect, 1f);
        if (transform.localScale != scale) transform.localScale = scale;
        if (sprite.color != colour) sprite.color = colour;
    }

    /// <summary>A one-pixel rectangle outline from four pixel sprites (exact line width at any box size): top, bottom, left, right.</summary>
    public static void Frame(SpriteRenderer[] lines, float right, float centre, float width, float height, Color colour)
    {
        Box(lines[0], right, centre + height / 2f - 0.5f, width, 1f, colour);
        Box(lines[1], right, centre - height / 2f + 0.5f, width, 1f, colour);
        Box(lines[2], right - width + 1f, centre, 1f, height, colour);
        Box(lines[3], right, centre, 1f, height, colour);
    }

    /// <summary>Sets a text line at <paramref name="px"/> (1080p pixels: the HUD's own item names are 20), right-aligned at
    /// <paramref name="x"/> (or centred on it), shrunk to <paramref name="maxWidth"/> when given. Returns the width it takes in
    /// the element's units.</summary>
    public static float Line(HudElement hud, TextMeshPro text, string value, float px, float x, float centre, Color colour, bool centred, float maxWidth = 0f)
    {
        SetActive(text, true);
        SetText(text, value);
        var size = hud.Px(px);
        if (Math.Abs(text.fontSize - size) > 0.001f) text.fontSize = size;
        var width = Measure(text, value, size);
        if (maxWidth > 0f && width > maxWidth) { size *= maxWidth / width; width = maxWidth; text.fontSize = size; }
        // Every HUD text is left-aligned from its left edge; right and centred lines are placed by their measured width.
        if (text.alignment != TextAlignmentOptions.MidlineLeft) text.alignment = TextAlignmentOptions.MidlineLeft;
        if (text.rectTransform.pivot.x != 0f) text.rectTransform.pivot = new Vector2(0f, 0.5f);
        var position = new Vector3(centred ? x - width / 2f : x - width, centre, 0f);
        if (text.transform.localPosition != position) text.transform.localPosition = position;
        if (text.color != colour) text.color = colour;
        return width;
    }

    /// <summary>The width of <paramref name="value"/> at font size <paramref name="size"/> in the element's units, measured once per
    /// text and value. When the text already shows that value at that size, it is the laid-out text (TMP's preferred width, or the
    /// glyphs' bounds when they are wider, as the game font's capitals are), so a frame drawn to it holds the text in any language.</summary>
    public static float Measure(TextMeshPro text, string value, float size)
    {
        var key = text.Pointer;
        if (Measured.TryGetValue(key, out var known) && known.Value == value && known.Size == size) return known.Width;
        float width;
        if (text.text == value && Math.Abs(text.fontSize - size) <= 0.001f)
        {
            text.ForceMeshUpdate(true, false);
            width = Math.Max(text.preferredWidth, text.textBounds.size.x) * text.transform.localScale.x;
        }
        else width = text.GetPreferredValues(value, float.PositiveInfinity, float.PositiveInfinity).x * text.transform.localScale.x * (size / Math.Max(text.fontSize, 0.01f));
        Measured[key] = (value, size, width);
        return width;
    }
}
