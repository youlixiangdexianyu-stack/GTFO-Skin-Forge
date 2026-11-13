using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>How a <see cref="TimelineMark"/> is drawn in a lane, so marks of different kinds differ by shape and not by colour alone.</summary>
public enum TimelineShape : byte { Tick, Triangle, Cross, Plus, Diamond, Square }

/// <summary>A point on a <see cref="HudTimeline"/>: when, its colour, the short text shown while the pointer is on it, the lane it sits in
/// (0 the top one; see <see cref="HudTimeline.Lanes"/>) and its shape there.</summary>
public readonly record struct TimelineMark(uint TimeMs, Color Colour, string Label, int Lane = 0, TimelineShape Shape = TimelineShape.Tick);

/// <summary>A stretch of time drawn as a thin coloured band under a <see cref="HudTimeline"/>'s bar (an objective in progress, a part done).</summary>
public readonly record struct TimelineSpan(uint FromMs, uint ToMs, Color Colour);

/// <summary>
/// A time bar across the bottom of the screen for playing something back (a replay, a cutscene): the bar with marks, a draggable play
/// head, step back, play / pause, step forward, the clock and the speeds. It draws what you set every frame (<see cref="DurationMs"/>,
/// <see cref="PositionMs"/>, <see cref="Playing"/>, <see cref="Speed"/>, <see cref="Marks"/>) and reports the player's clicks through
/// the callbacks; it never moves time itself. With <see cref="Lanes"/> the marks sit in named rows above the bar, each in its shape; with
/// <see cref="Spans"/> bands run under the bar; with <see cref="Skip"/> and <see cref="JumpMark"/> the lower row has buttons that jump
/// seconds and marks; the wheel over the band moves the head a second a notch; the mark under the pointer is named in a strip along the
/// band's top, inside the band (<see cref="Height"/> counts it), so nothing stands over the bar or above the band. The band keeps the player's
/// safe margin on every side; its buttons are <see cref="HudCanvas.Button"/>s, the speed in use lit. With <see cref="Bare"/> the owner draws the
/// plate (a panel holding its own rows above the band). Clicks need the HUD pointer (<see cref="HudPointer.Hold"/>),
/// which the owner holds while the bar is in use. Made with <see cref="UIKitMod.AddTimeline"/>, in <see cref="HudRegion.World"/>.
/// </summary>
public sealed class HudTimeline : HudWidget
{
    // TipRow: the strip along the band's top where the name of the mark under the pointer is shown, inside the band, so it covers neither the
    // bar nor what stands above the band.
    private const float Button = 34f, TextPx = 20f, SmallPx = 16f, LaneRow = 20f, LanePx = 14f, TipRow = 32f;
    private const float SeekEverySeconds = 0.1f;
    private static readonly Color Track = new(0.871f, 0.886f, 0.875f, 0.22f);
    private HudCanvas? _canvas;
    private bool _dragging;
    private uint _dragMs, _lastSentMs = uint.MaxValue;
    private float _lastSent;
    private uint _clockSeconds = uint.MaxValue, _clockDuration = uint.MaxValue, _clockWidthFor = uint.MaxValue;
    private float _clockWidth;
    private string _clock = "";
    private readonly string[] _speedTexts = new string[8];
    private float[] _speedsShown = Array.Empty<float>();

    private readonly Func<int, uint> _markTime;

    internal HudTimeline() => _markTime = i => Marks[i].TimeMs;

    public uint DurationMs { get; set; }
    public uint PositionMs { get; set; }
    public bool Playing { get; set; }
    public float Speed { get; set; } = 1f;

    /// <summary>The speed buttons, slowest first (at most 8).</summary>
    public float[] Speeds { get; set; } = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

    /// <summary>The marks on the bar; change the list whenever you like.</summary>
    public List<TimelineMark> Marks { get; } = new();

    /// <summary>The bands under the bar; change the list whenever you like.</summary>
    public List<TimelineSpan> Spans { get; } = new();

    /// <summary>The names of the mark rows, top first (at most 3): a mark sits in the row of its <see cref="TimelineMark.Lane"/>, drawn in its
    /// <see cref="TimelineMark.Shape"/>. Empty: every mark is a tick on the bar.</summary>
    public string[] Lanes { get; set; } = Array.Empty<string>();

    /// <summary>The play head was dragged or the bar or a mark clicked: the moment to go to. While dragging it is sent at most ten times a second, and once more on release.</summary>
    public Action<uint>? Seek { get; set; }

    /// <summary>A mark was clicked: its moment. Unset, the click goes to <see cref="Seek"/>.</summary>
    public Action<uint>? SeekMark { get; set; }

    public Action? PlayPause { get; set; }

    /// <summary>-1 one step back, +1 one step forward.</summary>
    public Action<int>? Step { get; set; }

    public Action<float>? SpeedChosen { get; set; }

    /// <summary>Jump by this many milliseconds (negative: back). Set, the lower row gets a back and a forward button of <see cref="SkipMs"/>.</summary>
    public Action<int>? Skip { get; set; }

    /// <summary>How far one skip button jumps, in milliseconds (whole seconds are shown on the buttons).</summary>
    public int SkipMs { get; set; } = 5000;

    /// <summary>The previous (-1) or next (+1) mark. Set, the lower row gets the two buttons, labelled <see cref="PreviousLabel"/> and <see cref="NextLabel"/>.</summary>
    public Action<int>? JumpMark { get; set; }

    public string PreviousLabel { get; set; } = "<";
    public string NextLabel { get; set; } = ">";

    /// <summary>The band's background; the kit's shade by default. An opaque one keeps the bar readable over a bright scene.</summary>
    public Color? Backing { get; set; }

    /// <summary>The owner draws the plate the band sits on (one panel with its own rows above the band, <see cref="Height"/> high and as wide as the
    /// screen inside the safe margin): the band draws no plate or frame of its own.</summary>
    public bool Bare { get; set; }

    /// <summary>The colour of what is played; the kit's accent by default.</summary>
    public Color Colour { get; set; } = UIKitColors.Accent;

    // The band grows a row for each lane past the first.
    private int LaneCount => Math.Min(Lanes?.Length ?? 0, 3);
    private float BandHeight => 104f + TipRow + Math.Max(0, LaneCount - 1) * LaneRow;

    /// <summary>The band's height in HUD units from the bottom edge, so other pieces can stand above it.</summary>
    public float Height => BandHeight + UIKit.SafeMargin;

    /// <summary>True while the pointer is on the band or dragging the head: a click (or the wheel) there belongs to the bar.</summary>
    public bool Busy { get; private set; }

    internal override void Build(HudElement hud)
    {
        // A bar of small texts and marks reads best with clean edges: no glow, no colour fringe.
        hud.Effects = false;
        _canvas = new HudCanvas(hud);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        if (!HudPointer.ScreenBounds(hud, out var screenLeft, out var screenBottom, out var screenRight, out _)) { _canvas.End(); return; }
        var band = BandHeight;
        var lanes = LaneCount;
        // The lane names' own width: a fixed one ran into the bar in longer languages.
        var labels = 0f;
        for (var lane = 0; lane < lanes; lane++) labels = Math.Max(labels, _canvas.TextWidth(Lanes[lane], LanePx) + 14f);
        var bottom = screenBottom + UIKit.SafeMargin;
        var centreX = (screenLeft + screenRight) / 2f;
        var centreY = bottom + band / 2f;
        var width = screenRight - screenLeft - UIKit.SafeMargin * 2f;
        var rowStart = screenLeft + UIKit.SafeMargin + 16f;
        var left = rowStart + labels; var barWidth = width - 32f - labels;
        var rowY = bottom + 32f;
        var barY = lanes == 0 ? bottom + 70f : bottom + band - TipRow - 22f - lanes * LaneRow;
        Busy = HudPointer.Over(hud, centreX, centreY, width, band) || _dragging;

        if (!Bare)
        {
            _canvas.Plate(centreX, centreY, width, band, 12f, Backing ?? UIKitColors.Shade);
            _canvas.Frame(centreX, centreY, width, band, 12f, UIKitColors.Faded(UIKitColors.Line, 0.5f));
        }

        // The bar, the played part, the bands, the lanes, the marks and the head.
        var shown = _dragging ? _dragMs : PositionMs;
        var fraction = TimelineMath.Fraction(shown, DurationMs);
        _canvas.Rect(left + barWidth / 2f, barY, barWidth, 4f, Track);
        if (fraction > 0f) _canvas.Rect(left + barWidth * fraction / 2f, barY, barWidth * fraction, 4f, Colour);
        foreach (var span in Spans)
        {
            var a = left + TimelineMath.Fraction(span.FromMs, DurationMs) * barWidth;
            var b = left + TimelineMath.Fraction(span.ToMs, DurationMs) * barWidth;
            if (b > a) _canvas.Rect((a + b) / 2f, barY - 6f, b - a, 3f, span.Colour);
        }
        for (var lane = 0; lane < lanes; lane++)
            _canvas.Text(Lanes[lane], rowStart, LaneY(barY, lane, lanes), LanePx, UIKitColors.Faded(UIKitColors.Text, 0.75f));
        var marksTop = lanes > 0 ? LaneY(barY, 0, lanes) + LaneRow / 2f : barY + 14f;
        var overBar = HudPointer.Over(hud, left + barWidth / 2f, (barY - 14f + marksTop) / 2f, barWidth + 16f, marksTop - barY + 14f);
        HudPointer.Position(hud, out var px, out _);
        var hovered = overBar && !_dragging ? TimelineMath.NearestMark(px, left, barWidth, DurationMs, Marks.Count, _markTime, 6f) : -1;
        for (var i = 0; i < Marks.Count; i++)
        {
            var mark = Marks[i];
            var x = left + TimelineMath.Fraction(mark.TimeMs, DurationMs) * barWidth;
            var big = i == hovered;
            if (lanes == 0) _canvas.Rect(x, barY + (big ? 11f : 8f), big ? 5f : 3f, big ? 14f : 9f, mark.Colour);
            else Shape(mark.Shape, x, LaneY(barY, mark.Lane, lanes), big ? 18f : 13f, mark.Colour);
        }
        var headX = left + barWidth * fraction;
        _canvas.Rect(headX, barY, 4f, 20f, UIKitColors.Bright);
        if (hovered >= 0)
        {
            var mark = Marks[hovered];
            var x = left + TimelineMath.Fraction(mark.TimeMs, DurationMs) * barWidth;
            var label = mark.Label.Length > 0 ? mark.Label + "  " + TimelineMath.Clock(mark.TimeMs) : TimelineMath.Clock(mark.TimeMs);
            var w = Math.Min(_canvas.TextWidth(label, SmallPx) + 20f, barWidth);
            var tx = Math.Clamp(x, left + w / 2f, left + barWidth - w / 2f);
            var ty = bottom + band - TipRow / 2f;
            _canvas.Plate(tx, ty, w, 28f, 6f, Backing ?? UIKitColors.Shade);
            _canvas.Rect(tx - w / 2f + 1.5f, ty, 3f, 28f, mark.Colour);
            _canvas.Text(_canvas.FitText(label, SmallPx, false, w - 20f), tx, ty, SmallPx, UIKitColors.Bright, HudAlign.Centre);
        }

        // Dragging: a press on the bar takes the head; moving sends a seek now and then; release sends the last one.
        if (HudPointer.Pressed && overBar)
        {
            if (hovered >= 0) { if (SeekMark != null) SeekMark(Marks[hovered].TimeMs); else Send(Marks[hovered].TimeMs, now, force: true); }
            else { _dragging = true; _dragMs = TimelineMath.At(px, left, barWidth, DurationMs); Send(_dragMs, now, force: true); }
        }
        if (_dragging)
        {
            if (HudPointer.Held) { _dragMs = TimelineMath.At(px, left, barWidth, DurationMs); Send(_dragMs, now, force: false); }
            else { _dragging = false; Send(_dragMs, now, force: true); }
        }
        // The wheel over the band, with the pointer shown: a second a notch.
        var wheel = UnityEngine.Input.mouseScrollDelta.y;
        if (Busy && HudPointer.Shown && wheel != 0f && !_dragging)
            Send((uint)Math.Clamp((long)PositionMs + (long)Math.Round(wheel) * 1000L, 0L, DurationMs), now, force: true);

        // The buttons: (back some seconds,) step back, play / pause, step forward(, forward some seconds).
        var bx = rowStart + Button / 2f;
        if (Skip != null)
        {
            if (Pressable(hud, bx, rowY)) Skip(-SkipMs);
            _canvas.Text("-" + SkipMs / 1000, bx, rowY, SmallPx, UIKitColors.Text, HudAlign.Centre, bold: true);
            bx += Button + 10f;
        }
        if (Pressable(hud, bx, rowY)) Step?.Invoke(-1);
        _canvas.Rect(bx - 8f, rowY, 3f, 14f, UIKitColors.Text);
        _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Left), bx + 3f, rowY, 14f, 14f, UIKitColors.Text);
        bx += Button + 10f;
        if (Pressable(hud, bx, rowY)) PlayPause?.Invoke();
        if (Playing) { _canvas.Rect(bx - 5f, rowY, 5f, 16f, Colour); _canvas.Rect(bx + 5f, rowY, 5f, 16f, Colour); }
        else _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Right), bx + 1f, rowY, 18f, 18f, Colour);
        bx += Button + 10f;
        if (Pressable(hud, bx, rowY)) Step?.Invoke(1);
        _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Right), bx - 3f, rowY, 14f, 14f, UIKitColors.Text);
        _canvas.Rect(bx + 8f, rowY, 3f, 14f, UIKitColors.Text);
        if (Skip != null)
        {
            bx += Button + 10f;
            if (Pressable(hud, bx, rowY)) Skip(SkipMs);
            _canvas.Text("+" + SkipMs / 1000, bx, rowY, SmallPx, UIKitColors.Text, HudAlign.Centre, bold: true);
        }

        // The clock, then the mark buttons.
        var seconds = shown / 1000;
        if (seconds != _clockSeconds || DurationMs != _clockDuration)
        {
            _clockSeconds = seconds; _clockDuration = DurationMs;
            _clock = TimelineMath.Clock(shown) + " / " + TimelineMath.Clock(DurationMs);
        }
        var clockLeft = bx + Button / 2f + 18f;
        _canvas.Text(_clock, clockLeft, rowY, TextPx, UIKitColors.Bright, HudAlign.Left, bold: true);
        if (JumpMark != null)
        {
            // After the widest the clock gets (the full length as of the recording's own length), so the buttons stand still as the digits change.
            if (_clockWidthFor != DurationMs) { _clockWidthFor = DurationMs; _clockWidth = _canvas.TextWidth(TimelineMath.Clock(DurationMs) + " / " + TimelineMath.Clock(DurationMs), TextPx, true); }
            var at = clockLeft + Math.Max(_clockWidth, _canvas.TextWidth(_clock, TextPx, true)) + 24f;
            if (TextButton(ref at, rowY, PreviousLabel)) JumpMark(-1);
            if (TextButton(ref at, rowY, NextLabel)) JumpMark(1);
        }

        // The speeds, right-aligned, the one in use lit.
        var speeds = Speeds ?? Array.Empty<float>();
        if (!ReferenceEquals(speeds, _speedsShown))
        {
            _speedsShown = speeds;
            for (var i = 0; i < speeds.Length && i < _speedTexts.Length; i++) _speedTexts[i] = TimelineMath.Speed(speeds[i]);
        }
        var right = left + barWidth;
        for (var i = Math.Min(speeds.Length, _speedTexts.Length) - 1; i >= 0; i--)
        {
            var current = Math.Abs(speeds[i] - Speed) < 0.001f;
            if (_canvas.Button(ref right, rowY, _speedTexts[i], current, UIKitColors.Accent) && !current) SpeedChosen?.Invoke(speeds[i]);
        }
        _canvas.End();
    }

    /// <summary>A square button with an icon drawn over it by the caller, in the look of <see cref="HudCanvas.Button"/>; true when it was clicked this frame.</summary>
    private bool Pressable(HudElement hud, float x, float y)
    {
        var over = HudPointer.Hover(hud, x, y, Button, Button);
        _canvas!.Plate(x, y, Button, Button, 8f, UIKitColors.Shade);
        _canvas.Pointed(x, y, Button, Button, 8f, UIKitColors.Accent, over, HudPointer.PressedOn(hud, x, y, Button, Button), false);
        return HudPointer.Click(hud, x, y, Button, Button);
    }

    /// <summary>A <see cref="HudCanvas.Button"/> starting at <paramref name="left"/>, which it moves past the button; true when clicked this frame.</summary>
    private bool TextButton(ref float left, float y, string text)
    {
        var right = left + _canvas!.ButtonWidth(text);
        left = right + 10f;
        return _canvas.Button(ref right, y, text, false, UIKitColors.Accent);
    }

    // Lane 0 is the top row; the last row sits just above the bar.
    private static float LaneY(float barY, int lane, int lanes) => barY + 14f + (lanes - 1 - Math.Clamp(lane, 0, lanes - 1)) * LaneRow;

    private void Shape(TimelineShape shape, float x, float y, float size, Color colour)
    {
        var c = _canvas!;
        var r = size * 0.4f;
        switch (shape)
        {
            case TimelineShape.Triangle: c.Sprite(UIKitSprites.Arrow(ArrowDirection.Down), x, y, size, size, colour); break;
            case TimelineShape.Cross: c.Line(x - r, y - r, x + r, y + r, 2f, colour); c.Line(x - r, y + r, x + r, y - r, 2f, colour); break;
            case TimelineShape.Plus: c.Rect(x, y, size * 0.8f, 2.5f, colour); c.Rect(x, y, 2.5f, size * 0.8f, colour); break;
            case TimelineShape.Diamond: c.Sprite(UIKitSprites.Pixel, x, y, size * 0.6f, size * 0.6f, colour, 45f); break;
            case TimelineShape.Square: c.Rect(x, y, size * 0.6f, size * 0.6f, colour); break;
            default: c.Rect(x, y, 3f, size, colour); break;
        }
    }

    private void Send(uint ms, float now, bool force)
    {
        if (!force && (ms == _lastSentMs || now - _lastSent < SeekEverySeconds)) return;
        _lastSentMs = ms; _lastSent = now;
        try { Seek?.Invoke(ms); }
        catch (Exception error) { Plugin.Error("ui-time-bar-seek-failed", "Time bar '" + Id + "' seek failed: " + error); }
    }
}

public sealed partial class UIKitMod
{
    /// <summary>A time bar across the bottom of the screen (<see cref="HudTimeline"/>), hidden until you set <see cref="HudWidget.Visible"/>.
    /// Null (logged) when the name cannot be used or is taken.</summary>
    public HudTimeline? AddTimeline(string name)
    {
        var id = Full(name, "time bar");
        if (id == null) return null;
        var bar = Hud.Add(id, new HudTimeline(), new HudSlot(HudRegion.World));
        if (bar != null) bar.Visible = false;
        return bar;
    }
}
