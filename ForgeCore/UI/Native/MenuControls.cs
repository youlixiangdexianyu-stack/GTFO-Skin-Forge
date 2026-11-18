using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>A control a menu surface can show and the player can change (a picker's adjust page, <see cref="PickerSource.Controls"/>): the
/// library draws it in the game's menu look and handles mouse, keyboard and gamepad; the value lives with you, read through a getter and
/// written through a setter. The label is shown as written (already in the player's language).</summary>
public abstract class UIControl
{
    private protected UIControl(string label) => Label = label ?? "";

    public string Label { get; }

    /// <summary>A press at <paramref name="fraction"/> (0 to 1) across the control's active span; true when the value changed.</summary>
    internal abstract bool Press(float fraction);

    /// <summary>One or more steps right (negative: left) from the keys or the shoulder buttons; true when the value changed.</summary>
    internal abstract bool Move(int steps);

    /// <summary>Whether a held press keeps following the cursor (a slider) rather than acting once.</summary>
    internal virtual bool Drags => false;
}

/// <summary>An on/off switch. A press flips it; right turns it on, left off. <c>on</c> and <c>off</c> are the words shown beside it
/// (default the library's "on" and "off" in the game's language).</summary>
public sealed class UIToggle : UIControl
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly string? _on;
    private readonly string? _off;

    public UIToggle(string label, Func<bool> get, Action<bool> set, string? on = null, string? off = null) : base(label)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _on = on;
        _off = off;
    }

    public string On => _on ?? UIKit.Kit.Text("toggle.on");
    public string Off => _off ?? UIKit.Kit.Text("toggle.off");
    public bool Value => _get();

    /// <summary>Puts <paramref name="value"/> through the setter when it differs.</summary>
    public bool SetValue(bool value)
    {
        if (value == Value) return false;
        _set(value);
        return true;
    }

    internal override bool Press(float fraction) => SetValue(!Value);
    internal override bool Move(int steps) => steps != 0 && SetValue(steps > 0);
}

/// <summary>One of a fixed row of options side by side (<see cref="UIChoice"/>, <see cref="UISwatches"/>). A press picks the option under it;
/// left and right move one option. <c>get</c> returns the chosen index, kept in range when read.</summary>
public abstract class UIPick : UIControl
{
    private readonly Func<int> _get;
    private readonly Action<int> _set;

    private protected UIPick(string label, int count, Func<int> get, Action<int> set) : base(label)
    {
        if (count == 0) throw new ArgumentException("a pick needs at least one option");
        Count = count;
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    public int Count { get; }
    public int Value => Math.Clamp(_get(), 0, Count - 1);

    /// <summary>Puts <paramref name="index"/> (kept in range) through the setter when it differs; true when it did.</summary>
    public bool SetValue(int index)
    {
        var v = Math.Clamp(index, 0, Count - 1);
        if (v == Value) return false;
        _set(v);
        return true;
    }

    internal override bool Press(float fraction) => SetValue(Math.Clamp((int)(fraction * Count), 0, Count - 1));
    internal override bool Move(int steps) => SetValue(Value + steps);
}

/// <summary>A segmented choice: a few named options (already in the player's language).</summary>
public sealed class UIChoice : UIPick
{
    public UIChoice(string label, IReadOnlyList<string> options, Func<int> get, Action<int> set) : base(label, options?.Count ?? 0, get, set) => Options = options!;

    public IReadOnlyList<string> Options { get; }
}

/// <summary>Colour swatches, colours as the player sees them (sRGB).</summary>
public sealed class UISwatches : UIPick
{
    public UISwatches(string label, IReadOnlyList<Color> colours, Func<int> get, Action<int> set) : base(label, colours?.Count ?? 0, get, set) => Colours = colours!;

    public IReadOnlyList<Color> Colours { get; }
}

/// <summary>
/// A number on a track: <see cref="Min"/> to <see cref="Max"/> in steps of <see cref="Step"/> (0: continuous). The player drags along the track
/// (mouse, or the menu cursor with the gamepad's press held), clicks to jump, and nudges one step with the arrow keys or the
/// gamepad's shoulder buttons while the row is under the cursor. <c>set</c> is called with each new value, already in range and on a step,
/// during a drag as well; <c>get</c> is read whenever the control is drawn. <c>format</c> writes the number beside the track (default: as many
/// decimals as the step has).
/// </summary>
public sealed class UISlider : UIControl
{
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly Func<float, string>? _format;

    public UISlider(string label, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string>? format = null) : base(label)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max) || !(min < max)) throw new ArgumentException("a slider needs a finite min below its max", nameof(max));
        if (!float.IsFinite(step) || step < 0f || step > max - min) throw new ArgumentOutOfRangeException(nameof(step), step, "the step is outside 0 to the range");
        Min = min;
        Max = max;
        Step = step;
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _format = format;
    }

    public float Min { get; }
    public float Max { get; }
    public float Step { get; }

    /// <summary>The current value, in range and on a step.</summary>
    public float Value => SliderMath.Snap(_get(), Min, Max, Step);

    /// <summary>Puts <paramref name="value"/> (kept in range, snapped) through the setter when it differs from the current value.</summary>
    public void SetValue(float value)
    {
        var v = SliderMath.Snap(value, Min, Max, Step);
        if (v != Value) _set(v);
    }

    /// <summary>The value one or more steps away (negative: down).</summary>
    public void Nudge(int steps) => SetValue(SliderMath.Nudge(Value, steps, Min, Max, Step));

    /// <summary>The value at <paramref name="fraction"/> (0 to 1) of the track.</summary>
    public void SetFraction(float fraction) => SetValue(SliderMath.ValueAtFraction(fraction, Min, Max, Step));

    /// <summary>The number shown beside the track.</summary>
    public string Text(float value) => _format?.Invoke(value) ?? SliderMath.Text(value, Step);

    internal override bool Press(float fraction)
    {
        var before = Value;
        SetFraction(fraction);
        return Value != before;
    }

    internal override bool Move(int steps)
    {
        var before = Value;
        Nudge(steps);
        return Value != before;
    }

    internal override bool Drags => true;
}

/// <summary>How the menu controls are drawn on a <see cref="MenuCanvas"/>, in the picker rows' look (chamfered dark plates, white lines, amber
/// for what is set). Each returns the rectangle a press or drag acts on, in the canvas's units.</summary>
internal static class MenuControlLook
{
    /// <summary>A control as one row over <paramref name="r"/>: a chamfered plate, its frame and an edge bar (amber while pressed or held),
    /// and the control inside it. Returns the span a press maps to a value.</summary>
    internal static Rect Row(MenuCanvas c, Rect r, UIControl control, bool focus, bool held)
    {
        float left = r.XMin(), right = r.XMax(), bottom = r.YMin(), top = r.YMax(), h = top - bottom;
        var lit = focus || held;
        c.Plate(r, h * 0.12f, lit ? MenuLook.Raised : MenuLook.Panel);
        c.Frame(r, h * 0.12f, lit ? MenuLook.Line : MenuLook.LineDim);
        c.Rect(Rect.MinMaxRect(left + 2f, bottom + h * 0.16f, left + (focus ? 6f : 4f), top - h * 0.16f), lit ? MenuLook.Amber : MenuLook.LineDim);
        var inner = Rect.MinMaxRect(left + 10f, bottom, right - 4f, top);
        return control switch
        {
            UISlider slider => Slider(c, inner, slider, focus, held),
            UIToggle toggle => Toggle(c, inner, toggle, focus),
            UIPick pick => Pick(c, inner, pick, focus),
            _ => default,
        };
    }

    /// <summary>A slider row inside <paramref name="r"/>: the label top left, the number top right, the track below, filled in amber up to the
    /// value, with a handle; <paramref name="focus"/> brightens it, <paramref name="held"/> (a drag in progress) widens the handle. Returns the
    /// track, the span a press maps to a value.</summary>
    internal static Rect Slider(MenuCanvas c, Rect r, UISlider slider, bool focus, bool held)
    {
        float left = r.XMin(), right = r.XMax(), bottom = r.YMin(), top = r.YMax(), h = top - bottom;
        var value = slider.Value;
        var fraction = SliderMath.Fraction(value, slider.Min, slider.Max);
        var pad = h * 0.22f;
        var textY = top - h * 0.32f;
        var number = slider.Text(value);
        var numberWidth = c.TextWidth(number, h * 0.26f);
        // A gap of a whole pad between the label's end and the number: a long label is drawn smaller, then cut, never onto the number.
        c.Text(slider.Label, left + pad, textY, h * 0.26f, focus ? MenuLook.Line : MenuLook.Text, HudAlign.Left, right - left - 4f * pad - numberWidth);
        c.Text(number, right - pad, textY, h * 0.26f, focus || held ? MenuLook.Amber : MenuLook.Dim, HudAlign.Right);
        float x0 = left + pad, x1 = right - pad, y0 = bottom + h * 0.2f, thick = Math.Max(4f, h * 0.06f), span = x1 - x0;
        c.Rect(new Rect(x0, y0, span, thick), MenuLook.LineFaint);
        if (fraction > 0f) c.Rect(new Rect(x0, y0, span * fraction, thick), focus || held ? MenuLook.Amber : MenuLook.Faded(MenuLook.Amber, 0.7f));
        // Ticks at each step when there are few of them, so a stepped slider reads as stepped.
        var steps = slider.Step > 0f ? (int)MathF.Round((slider.Max - slider.Min) / slider.Step) : 0;
        if (steps is > 1 and <= 20)
            for (var i = 1; i < steps; i++)
                c.Rect(new Rect(x0 + span * i / steps - 0.75f, y0 - 3f, 1.5f, thick + 6f), MenuLook.LineDim);
        var handleWidth = held ? h * 0.16f : h * 0.11f;
        c.Plate(new Rect(x0 + span * fraction - handleWidth / 2f, y0 + thick / 2f - h * 0.14f, handleWidth, h * 0.28f), handleWidth * 0.3f, focus || held ? MenuLook.Line : MenuLook.LineMid);
        return Rect.MinMaxRect(x0, bottom, x1, top);
    }

    /// <summary>A switch row: the label on the left, on the right a plate with a knob that sits right and amber when on, left and dim when
    /// off, with the state's word beside it. The whole row takes the press.</summary>
    internal static Rect Toggle(MenuCanvas c, Rect r, UIToggle toggle, bool focus)
    {
        float left = r.XMin(), right = r.XMax(), bottom = r.YMin(), top = r.YMax(), h = top - bottom;
        var on = toggle.Value;
        var pad = h * 0.22f;
        var size = h * 0.26f;
        var mid = bottom + h / 2f;
        float w = h * 0.9f, sh = h * 0.36f, sx = right - pad - w;
        var word = on ? toggle.On : toggle.Off;
        var wordWidth = c.TextWidth(word, size);
        c.Text(toggle.Label, left + pad, mid, size, focus ? MenuLook.Line : MenuLook.Text, HudAlign.Left, sx - left - 3f * pad - wordWidth);
        c.Text(word, sx - pad * 0.6f, mid, size, on ? MenuLook.Amber : MenuLook.Dim, HudAlign.Right);
        var plate = new Rect(sx, mid - sh / 2f, w, sh);
        c.Plate(plate, sh * 0.25f, MenuLook.Bay);
        c.Frame(plate, sh * 0.25f, focus ? MenuLook.Line : MenuLook.LineDim);
        var knob = sh - 6f;
        c.Plate(new Rect(on ? sx + w - 3f - knob * 1.4f : sx + 3f, mid - knob / 2f, knob * 1.4f, knob), knob * 0.25f, on ? MenuLook.Amber : MenuLook.LineMid);
        return Rect.MinMaxRect(left, bottom, right, top);
    }

    /// <summary>A pick row: the label top left, the options as a row of equal plates below; a choice shows each option's name, swatches
    /// their colour. The chosen one is amber (a choice) or framed in white (swatches). Returns the row of plates.</summary>
    internal static Rect Pick(MenuCanvas c, Rect r, UIPick pick, bool focus)
    {
        float left = r.XMin(), right = r.XMax(), bottom = r.YMin(), top = r.YMax(), h = top - bottom;
        var pad = h * 0.22f;
        var size = h * 0.26f;
        c.Text(pick.Label, left + pad, top - h * 0.32f, size, focus ? MenuLook.Line : MenuLook.Text, HudAlign.Left, right - left - 2f * pad);
        float x0 = left + pad, x1 = right - pad, y0 = bottom + h * 0.1f, ph = h * 0.36f, gap = h * 0.06f;
        var count = pick.Count;
        var each = (x1 - x0 - gap * (count - 1)) / count;
        var chosen = pick.Value;
        var choice = pick as UIChoice;
        var swatches = pick as UISwatches;
        for (var i = 0; i < count; i++)
        {
            var plate = new Rect(x0 + i * (each + gap), y0, each, ph);
            var on = i == chosen;
            if (swatches != null)
            {
                c.Plate(plate, ph * 0.2f, swatches.Colours[i]);
                c.Frame(plate, ph * 0.2f, on ? MenuLook.Line : MenuLook.LineDim);
                if (on) c.Rect(new Rect(x0 + i * (each + gap) + each * 0.2f, y0 - 4f, each * 0.6f, 2f), focus ? MenuLook.Line : MenuLook.LineMid);
                continue;
            }
            c.Plate(plate, ph * 0.2f, on ? MenuLook.Faded(MenuLook.Amber, focus ? 0.9f : 0.7f) : MenuLook.Bay);
            c.Frame(plate, ph * 0.2f, on ? MenuLook.Amber : MenuLook.LineDim);
            c.Text(choice!.Options[i], x0 + i * (each + gap) + each / 2f, y0 + ph / 2f, size * 0.9f, on ? MenuLook.Ink : MenuLook.Body, HudAlign.Centre, each - 6f);
        }
        return Rect.MinMaxRect(x0, bottom, x1, top);
    }
}
