using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// A readout at a place you choose (<see cref="HudPlacement"/>): text, a thin bar, a row of cells or a clock, in the game's HUD font and
/// colours. Make it with <see cref="UIKitMod.AddPlaced"/>. It is not laid out: it may overlap the game's HUD or anything else, and only you
/// keep it clear. It stands under the game's weapon list, so it sways and curves with the HUD, and it is off while one of the game's full
/// screens is open.
/// <para>With an unchanged value it costs a comparison a frame; with a <see cref="Rate"/>, a little arithmetic until the whole number shown
/// changes. Strings are made only then.</para>
/// </summary>
public sealed class HudPlaced : HudWidget
{
    private const float SmallShare = 0.4f, MinSmallPx = 12f, LineStep = 1.18f, GapPx = 14f, ThinPx = 3f, CellPx = 5f, CellGapPx = 6f, TrackAlpha = 0.2f;
    private const int MaxCells = 32;

    private HudPlacement _placement;
    private HudPlacedKind _kind;
    private UIText? _text;
    private Color? _colour;
    private Color? _lowColour;
    private float _lowBelow = float.NegativeInfinity, _textPx = 20f, _setAt;
    private double _value, _maximum, _rate;

    private HudCanvas? _canvas;
    private int _version, _drawnVersion = -1, _drawnScreen = -1, _drawnLanguage = -1;
    private PlacedReading _drawn;

    internal HudPlaced(HudPlacement placement) => _placement = placement;

    /// <summary>Where it stands; can be changed at any time.</summary>
    public HudPlacement Placement
    {
        get => _placement;
        set { _placement = value; _version++; }
    }

    /// <summary>What it draws (default <see cref="HudPlacedKind.Text"/>).</summary>
    public HudPlacedKind Kind
    {
        get => _kind;
        set { _kind = value; _version++; }
    }

    /// <summary>A plain string or a text key. It may hold the placeholders of <see cref="HudPlacedText"/> and rich text tags.</summary>
    public UIText? Text
    {
        get => _text;
        set { _text = value; _version++; }
    }

    /// <summary>The normal colour; null is the game's HUD text colour.</summary>
    public Color? Colour
    {
        get => _colour;
        set { _colour = value; _version++; }
    }

    /// <summary>The colour below <see cref="LowBelow"/>; null is the game's alert red.</summary>
    public Color? LowColour
    {
        get => _lowColour;
        set { _lowColour = value; _version++; }
    }

    /// <summary>Below this value it takes <see cref="LowColour"/>; negative infinity (the default) never.</summary>
    public float LowBelow
    {
        get => _lowBelow;
        set { _lowBelow = value; _version++; }
    }

    /// <summary>The height of its text in 1080p pixels (default 20, the game's weapon names).</summary>
    public float TextPx
    {
        get => _textPx;
        set { _textPx = value; _version++; }
    }

    /// <summary>Change per second from the last <see cref="Set"/>: the client carries the value forward by itself, down to 0 or up to the
    /// maximum, and no further update is needed until the rate changes. Setting it restarts from the value reached.</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            var now = ForgeCore.Frame.GameTime;
            _value = Current(now);
            _setAt = now;
            _rate = value;
            _version++;
        }
    }

    /// <summary>The value and, optionally, what it is out of. Without a maximum a bar reads the value itself as the share 0..1.
    /// <paramref name="elapsed"/> says it was set that many seconds ago, so <see cref="Rate"/> has already carried it that far.</summary>
    public void Set(double value, double max = 0d, double elapsed = 0d)
    {
        _value = value;
        _maximum = max;
        _setAt = ForgeCore.Frame.GameTime - (float)elapsed;
        _version++;
    }

    private double Current(float now) => _rate == 0d ? _value : PlacedReading.Carried(_value, _maximum, _rate, now - _setAt);

    internal override void Build(HudElement hud)
    {
        // It is drawn where it was put, so nothing measures it.
        hud.SetDrawn(0f, 0f);
        hud.Effects = false;
        UIKitColors.Refresh();
        _canvas = new HudCanvas(hud);
        _drawnVersion = -1;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null) return;
        var same = _drawnVersion == _version && _drawnScreen == hud.ScreenVersion && _drawnLanguage == UITexts.Version;
        if (same && _rate == 0d) return;
        var value = Current(now);
        var reading = PlacedReading.Of(value, _maximum, _lowBelow);
        if (same && reading.Equals(_drawn)) return;
        _drawnVersion = _version;
        _drawnScreen = hud.ScreenVersion;
        _drawnLanguage = UITexts.Version;
        _drawn = reading;
        if (!_canvas.Begin()) return;
        Draw(hud, _canvas, reading, value);
        _canvas.End();
    }

    private void Draw(HudElement hud, HudCanvas canvas, in PlacedReading reading, double value)
    {
        var box = _placement.Box(hud.HalfWidth, hud.HalfHeight);
        var u = HudPlacement.Unit(hud.HalfHeight);
        var px = _textPx * u;
        var colour = reading.Low ? _lowColour ?? UIKitColors.Alert : _colour ?? UIKitColors.Text;
        var track = UIKitColors.Faded(colour, TrackAlpha);
        var text = hud.Show(_text);
        var clock = HudClock.Text(value);
        switch (_kind)
        {
            case HudPlacedKind.Bar:
                Split(text, out var head, out var tail);
                var left = box.Left;
                var right = box.Right;
                var y = (box.Bottom + box.Top) / 2f;
                if (head.Length > 0) left += canvas.Text(Filled(head, reading, clock), left, y, px, colour) + GapPx * u;
                if (tail.Length > 0) right -= canvas.Text(Filled(tail, reading, clock), right, y, px, colour, HudAlign.Right) + GapPx * u;
                if (right <= left) break;
                var fill = (right - left) * reading.Fill;
                canvas.Rect((left + right) / 2f, y, right - left, ThinPx * u, track);
                if (fill > 0f) canvas.Rect(left + fill / 2f, y, fill, ThinPx * u, colour);
                break;
            case HudPlacedKind.Segments:
                var cellHeight = CellPx * u;
                var gap = CellGapPx * u;
                var cells = (int)Math.Clamp(reading.Maximum, 1L, MaxCells);
                var cell = (box.Width - gap * (cells - 1)) / cells;
                for (var i = 0; i < cells; i++)
                    canvas.Rect(box.Left + (i + 0.5f) * cell + i * gap, box.Bottom + cellHeight / 2f, cell, cellHeight, i < reading.Value ? colour : track);
                Lines(canvas, Filled(text, reading, clock), box, box.Bottom + cellHeight + gap, box.Top, px, colour);
                break;
            case HudPlacedKind.Time:
                var at = HudPlacedText.FirstPlaceholder(text);
                var small = at < 0 ? text.Trim() : text.Substring(0, at).Trim();
                var body = at < 0 ? clock : Filled(text.Substring(at), reading, clock);
                var smallPx = Math.Max(MinSmallPx * u, px * SmallShare);
                var total = px * LineStep + (small.Length > 0 ? smallPx * LineStep : 0f);
                var top = (box.Bottom + box.Top + total) / 2f;
                if (small.Length > 0)
                {
                    Line(canvas, small, box, top - smallPx * LineStep / 2f, smallPx, colour);
                    top -= smallPx * LineStep;
                }
                Line(canvas, body, box, top - px * LineStep / 2f, px, colour);
                break;
            default:
                Lines(canvas, Filled(text, reading, clock), box, box.Bottom, box.Top, px, colour);
                break;
        }
    }

    private static string Filled(string text, in PlacedReading reading, string clock) =>
        HudPlacedText.Fill(text, reading.Value, reading.Maximum, reading.Percent, clock);

    /// <summary>The text before its first placeholder and the rest, trimmed (no placeholder: all of it is before).</summary>
    private static void Split(string text, out string head, out string tail)
    {
        var at = HudPlacedText.FirstPlaceholder(text);
        head = (at < 0 ? text : text.Substring(0, at)).Trim();
        tail = at < 0 ? "" : text.Substring(at).Trim();
    }

    /// <summary>The lines of <paramref name="text"/> centred between two heights.</summary>
    private void Lines(HudCanvas canvas, string text, HudBox box, float bottom, float top, float px, Color colour)
    {
        if (text.Length == 0) return;
        var lines = text.Split('\n');
        var step = px * LineStep;
        var y = (bottom + top + (lines.Length - 1) * step) / 2f;
        foreach (var line in lines)
        {
            Line(canvas, line, box, y, px, colour);
            y -= step;
        }
    }

    /// <summary>One line on the side of the box its anchor is on, shrunk to the box's width.</summary>
    private void Line(HudCanvas canvas, string line, HudBox box, float y, float px, Color colour)
    {
        var width = canvas.TextWidth(line, px);
        if (width > box.Width && box.Width > 0f) px *= box.Width / width;
        var side = _placement.AnchorX;
        var x = box.Left + side * box.Width;
        canvas.Text(line, x, y, px, colour, side <= 0f ? HudAlign.Left : side >= 1f ? HudAlign.Right : HudAlign.Centre);
    }
}
