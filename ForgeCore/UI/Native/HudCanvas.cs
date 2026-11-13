using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>How a <see cref="HudCanvas"/> text sits on its position.</summary>
public enum HudAlign : byte
{
    Left,
    Centre,
    Right
}

/// <summary>
/// Immediate-mode drawing on a HUD element: each frame call <see cref="Begin"/>, then <see cref="Rect"/>, <see cref="Line"/>,
/// <see cref="Cell"/>, <see cref="Plate"/>, <see cref="Frame"/>, <see cref="Skew"/>, <see cref="Sprite"/> and <see cref="Text"/> for
/// everything that is visible, then <see cref="End"/>. Pieces come from pools that grow on demand (and ahead of time with
/// <see cref="Reserve"/>), a piece is written to only when something about it changed since the frame before, and pieces not asked for
/// are hidden, so a steady picture allocates nothing and touches nothing. Positions are centres in the element's units.
/// <para>Every lit piece (not the dark plates behind readouts) gets the shared soft glow (<see cref="UIKit.HudGlow"/>) and colour
/// fringe (<see cref="UIKit.HudFringe"/>) unless the element's <see cref="HudElement.Effects"/> is off, and the element's
/// <see cref="HudElement.Tilt"/> stretches its outer side taller. The pools start empty again whenever the game rebuilds its HUD.</para>
/// <para>While the element comes online (<see cref="HudElement.Reveal"/>, <see cref="HudReveal"/>) the canvas traces its corner brackets,
/// shows only the pieces a scan line has passed (texts type on as it passes them), and flashes the whole element white at the end.</para>
/// </summary>
public sealed class HudCanvas
{
    private sealed class Pool<T> where T : Component
    {
        internal readonly List<T> Pieces = new();
        internal readonly List<Last> Last = new();
        internal readonly PieceActivity Activity = new();
        internal int Used;
        internal readonly int Order, Step;
        internal Pool(int order, int step) { Order = order; Step = step; }
    }

    private struct Last
    {
        internal float A, B, C, D, E, F, R, G, Bl, Al;
        internal string? Text;
        internal Sprite? Sprite;
        internal byte Align, Face;
        // Texts: 0 when every character shows, else the characters shown plus one (an element coming online types its texts on).
        internal int Typed;
    }

    private enum Fit : byte { Plain, Cut, Lean }

    private const byte GameRegular = 1, GameBold = 2, KitRegular = 3, KitBold = 4, Soft = 8;
    private static readonly Color FringeRed = new(1f, 0.25f, 0.25f, 1f), FringeBlue = new(0.3f, 0.55f, 1f, 1f);

    private UIKitFont? _font;
    private readonly HudElement _hud;
    private readonly Pool<TextMeshPro> _texts, _textGlows, _textFringes;
    private readonly Pool<SpriteRenderer> _rects, _lines, _cells, _sprites, _plates, _forms, _glows, _fringes;
    private readonly Pool<SpriteRenderer>[] _spritePools;
    private readonly Pool<TextMeshPro>[] _textPools;
    private readonly HudShape[] _shapes = new HudShape[ProgressBar.MaxShapes];
    private int _generation = -1;
    // Effects in force for the current frame (read once in Begin).
    private float _glow, _glowRadius, _fringe, _fringeShift, _tilt, _tiltWidth;
    private int _tiltFlow;
    // The element coming online this frame (HudReveal): its age, sweep, lock flash, flow and box from its root; overlay pieces (the scan
    // line and brackets) are drawn whole.
    private bool _revealing, _overlay;
    private float _revealAge, _sweep, _lock, _x0, _x1, _y0, _y1;
    private int _revealFlow;
    private static readonly Color RevealLine = new(0.96f, 0.97f, 0.96f, 0.9f), RevealBand = new(0.8f, 0.94f, 1f, 0.14f), RevealBracket = new(0.8f, 0.94f, 1f, 0.95f);

    /// <param name="hud">The element to draw on; use the canvas only while its <see cref="HudElement.Root"/> is set.</param>
    /// <param name="rectOrder">Sorting order of the first rectangle; each later one is one higher.</param>
    /// <param name="lineOrder">Sorting order of every line.</param>
    /// <param name="cellOrder">Sorting order of every cell.</param>
    /// <param name="spriteOrder">Sorting order of the first sprite or icon; each later one is one higher.</param>
    /// <param name="layer">Added to every sorting order: a canvas with a higher layer draws wholly over one with a lower layer, plates over texts included (on one canvas every plate is behind every text, whatever the order of the calls). Two thousand apart keeps a full canvas' pieces apart.</param>
    /// <param name="stackPlates">Each later plate draws over the earlier ones (a button on a panel), from far enough below the rectangles to leave them room for 400 plates; otherwise all plates share one order and overlapping ones have no set order.</param>
    public HudCanvas(HudElement hud, int rectOrder = -100, int lineOrder = -50, int cellOrder = -5, int spriteOrder = -20, int layer = 0, bool stackPlates = false)
    {
        _hud = hud ?? throw new ArgumentNullException(nameof(hud));
        _texts = new(layer, 0);
        _textGlows = new(layer - 2, 0);
        _textFringes = new(layer - 1, 0);
        _rects = new(layer + rectOrder, 1);
        _lines = new(layer + lineOrder, 0);
        _cells = new(layer + cellOrder, 0);
        _sprites = new(layer + spriteOrder, 1);
        _plates = stackPlates ? new(layer + rectOrder - 400, 1) : new(layer + rectOrder - 30, 0);
        _forms = new(layer + lineOrder - 10, 0);
        _glows = new(layer + rectOrder - 50, 0);
        _fringes = new(layer + rectOrder - 45, 0);
        _spritePools = new[] { _rects, _lines, _cells, _sprites, _plates, _forms, _glows, _fringes };
        _textPools = new[] { _texts, _textGlows, _textFringes };
    }

    /// <summary>The face of the texts drawn from now on: the element's <see cref="HudElement.Font"/> until set here. With
    /// <see cref="UIKitFont.Tech"/>, bold uses the kit's heavier weights rather than a synthetic bold; without the kit's font bundle it
    /// draws as the game's font.</summary>
    public UIKitFont Font
    {
        get => _font ?? _hud.Font;
        set => _font = value;
    }

    /// <summary>Element units per 1080p pixel: multiply a size taken from a 1080p layout by it.</summary>
    public float Unit => _hud.HalfHeight > 0f ? _hud.HalfHeight / 540f : 1f;

    /// <summary>Starts a frame. False while the element has no HUD (nothing can be drawn).</summary>
    public bool Begin()
    {
        if (!Sync()) return false;
        _texts.Used = _textGlows.Used = _textFringes.Used = 0;
        _rects.Used = _lines.Used = _cells.Used = _sprites.Used = _plates.Used = _forms.Used = _glows.Used = _fringes.Used = 0;
        var now = ForgeCore.Frame.GameTime;
        var effects = _hud.Effects;
        _glow = effects ? UIKit.HudGlow.Effective(Plugin.Glow) : 0f;
        _glowRadius = Math.Max(0.5f, UIKit.HudGlow.Radius * Unit);
        _fringe = effects ? UIKit.HudFringe.Effective(Plugin.Fringe, now) : 0f;
        _fringeShift = UIKit.HudFringe.Pixels * UIKit.HudFringe.Boost(now) * Unit;
        _tilt = _hud.Tilt;
        _tiltFlow = _hud.FlowX;
        _tiltWidth = Math.Max(1f, _hud.Slot.Width);
        _revealAge = now - _hud.RevealedAt;
        _revealing = HudReveal.Running(_revealAge);
        if (_revealing)
        {
            _sweep = HudReveal.Sweep(_revealAge);
            _lock = HudReveal.Lock(_revealAge);
            _revealFlow = _hud.FlowX;
            var entry = _hud.Entry;
            HudReveal.Extent(_revealFlow, Math.Max(_hud.Slot.Width, entry.DrawnWidth), out _x0, out _x1);
            HudReveal.Extent(_hud.FlowY, Math.Max(_hud.Slot.Height, entry.DrawnHeight), out _y0, out _y1);
        }
        return true;
    }

    /// <summary>A one-unit sprite (<see cref="UIKitSprites"/>) centred on (<paramref name="x"/>, <paramref name="y"/>), stretched to
    /// <paramref name="width"/> x <paramref name="height"/> and turned <paramref name="degrees"/> anticlockwise.</summary>
    public void Sprite(Sprite sprite, float x, float y, float width, float height, Color colour, float degrees = 0f)
    {
        var k = TiltAt(x);
        y *= k; height *= k;
        Put(_sprites, sprite, Fit.Plain, x, y, width, height, degrees, 0f, colour);
        if (!Lit(colour)) return;
        if (_fringe > 0f)
        {
            Put(_fringes, sprite, Fit.Plain, x - _fringeShift, y, width, height, degrees, 0f, Tint(FringeRed, colour, _fringe));
            Put(_fringes, sprite, Fit.Plain, x + _fringeShift, y, width, height, degrees, 0f, Tint(FringeBlue, colour, _fringe));
        }
        if (_glow > 0f)
        {
            var grow = 2f * _glowRadius;
            Put(_glows, sprite, Fit.Plain, x, y, width + grow * Math.Sign(width), height + grow * Math.Sign(height), degrees, 0f, Faded(colour, _glow * 0.5f));
        }
    }

    /// <summary>A picture of any size (an icon, a weapon render) scaled to the largest size that fits <paramref name="boxWidth"/> x
    /// <paramref name="boxHeight"/> with its ratio kept, centred on (<paramref name="x"/>, <paramref name="y"/>). Drawn once: a glow or
    /// colour fringe copy of a picture reads as a second, offset image.</summary>
    public void Icon(Sprite sprite, float x, float y, float boxWidth, float boxHeight, Color colour)
    {
        var size = sprite.bounds.size;
        var (width, height) = IconFit.Fit(boxWidth, boxHeight, size.x, size.y);
        if (width <= 0f) return;
        var k = TiltAt(x);
        Put(_sprites, sprite, Fit.Plain, x, y * k, width / size.x, height / size.y * k, 0f, 0f, colour);
    }

    /// <summary>A rectangle centred on (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Rect(float x, float y, float width, float height, Color colour)
    {
        var k = TiltAt(x);
        y *= k; height *= k;
        Put(_rects, UIKitSprites.Pixel, Fit.Plain, x, y, width, height, 0f, 0f, colour);
        Effects(UIKitSprites.Pixel, Fit.Plain, x, y, width, height, 0f, 0f, colour, UIKitSprites.SoftBox);
    }

    /// <summary>A slanted cell (<see cref="UIKitSprites.Slant"/>) whose bounding box is centred on (<paramref name="x"/>,
    /// <paramref name="y"/>); its top edge is shifted right by a quarter of <paramref name="width"/>.</summary>
    public void Cell(float x, float y, float width, float height, Color colour)
    {
        var k = TiltAt(x);
        y *= k; height *= k;
        Put(_cells, UIKitSprites.Slant, Fit.Plain, x, y, width / 32f, height / 16f, 0f, 0f, colour);
        if (!Lit(colour)) return;
        if (_fringe > 0f)
        {
            Put(_fringes, UIKitSprites.Slant, Fit.Plain, x - _fringeShift, y, width / 32f, height / 16f, 0f, 0f, Tint(FringeRed, colour, _fringe));
            Put(_fringes, UIKitSprites.Slant, Fit.Plain, x + _fringeShift, y, width / 32f, height / 16f, 0f, 0f, Tint(FringeBlue, colour, _fringe));
        }
        if (_glow > 0f) Glow(UIKitSprites.SoftBox, x, y, width, height, 0f, colour);
    }

    /// <summary>A filled plate centred on (<paramref name="x"/>, <paramref name="y"/>) with its top left and bottom right corners cut
    /// at 45° by <paramref name="cut"/> (0 draws a plain rectangle). Plates sit behind every other piece and get no glow or fringe:
    /// they are the dark backing that keeps readouts legible.</summary>
    public void Plate(float x, float y, float width, float height, float cut, Color colour)
    {
        var k = TiltAt(x);
        y *= k; height *= k;
        if (cut <= 0f) Put(_plates, UIKitSprites.Pixel, Fit.Plain, x, y, width, height, 0f, 0f, colour);
        else Put(_plates, UIKitSprites.Chamfer, Fit.Cut, x, y, width, height, 0f, Math.Min(cut, Math.Min(width, height) / 2f), colour);
    }

    /// <summary>The outline of a plate (see <see cref="Plate"/>): its line is an eighth of <paramref name="cut"/> thick (a cut of 12
    /// draws a 1.5 line); 0 draws a plain rectangle outline 1.5 thick.</summary>
    public void Frame(float x, float y, float width, float height, float cut, Color colour)
    {
        if (cut <= 0f)
        {
            var hw = width / 2f; var hh = height / 2f;
            Line(x - hw, y - hh, x + hw, y - hh, 1.5f, colour);
            Line(x + hw, y - hh, x + hw, y + hh, 1.5f, colour);
            Line(x + hw, y + hh, x - hw, y + hh, 1.5f, colour);
            Line(x - hw, y + hh, x - hw, y - hh, 1.5f, colour);
            return;
        }
        var k = TiltAt(x);
        y *= k; height *= k;
        cut = Math.Min(cut, Math.Min(width, height) / 2f);
        Put(_forms, UIKitSprites.ChamferLine, Fit.Cut, x, y, width, height, 0f, cut, colour);
        Effects(UIKitSprites.ChamferLine, Fit.Cut, x, y, width, height, 0f, cut, colour, UIKitSprites.SoftRing);
    }

    /// <summary>A parallelogram centred on (<paramref name="x"/>, <paramref name="y"/>): its top edge is shifted
    /// <paramref name="lean"/> to the right of its bottom edge (negative leans left; 0 draws a rectangle). The bounding box is
    /// <paramref name="width"/> wide.</summary>
    public void Skew(float x, float y, float width, float height, float lean, Color colour)
    {
        if (Math.Abs(lean) < 0.01f) { Rect(x, y, width, height, colour); return; }
        var k = TiltAt(x);
        y *= k; height *= k;
        lean = Math.Clamp(lean, -width / 2f, width / 2f);
        Put(_forms, UIKitSprites.Skew, Fit.Lean, x, y, width, height, 0f, lean, colour);
        Effects(UIKitSprites.Skew, Fit.Lean, x, y, width, height, 0f, lean, colour, UIKitSprites.SoftBox);
    }

    /// <summary>A line of <paramref name="thickness"/> from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), a little longer than that so joined lines meet without a notch.</summary>
    public void Line(float x0, float y0, float x1, float y1, float thickness, Color colour)
    {
        y0 *= TiltAt(x0);
        y1 *= TiltAt(x1);
        var dx = x1 - x0;
        var dy = y1 - y0;
        var cx = (x0 + x1) / 2f;
        var cy = (y0 + y1) / 2f;
        var length = MathF.Sqrt(dx * dx + dy * dy) + thickness * 0.5f;
        var degrees = MathF.Atan2(dy, dx) * 57.29578f;
        Put(_lines, UIKitSprites.Pixel, Fit.Plain, cx, cy, length, thickness, degrees, 0f, colour);
        Effects(UIKitSprites.Pixel, Fit.Plain, cx, cy, length, thickness, degrees, 0f, colour, UIKitSprites.SoftBox);
    }

    /// <summary>A text <paramref name="px"/> tall (1080p pixels) on (<paramref name="x"/>, <paramref name="y"/>): that point is the
    /// left end, middle or right end of the text's line, and the line is centred on y. Every text is drawn left-aligned from its left
    /// edge; a centred or right-aligned one is moved left by its measured width (measured again only when the string, size or weight
    /// changes). The text object is set again only when the string is a different one. Returns the text's width in the element's units.</summary>
    public float Text(string text, float x, float y, float px, Color colour, HudAlign align = HudAlign.Left, bool bold = false)
    {
        y *= TiltAt(x);
        var face = Face(bold);
        var width = PutText(_texts, text, x, y, px, colour, align, face, -1f);
        if (width <= 0f || !Lit(colour)) return width;
        if (_fringe > 0f)
        {
            PutText(_textFringes, text, x - _fringeShift, y, px, Tint(FringeRed, colour, _fringe), align, face, width);
            PutText(_textFringes, text, x + _fringeShift, y, px, Tint(FringeBlue, colour, _fringe), align, face, width);
        }
        if (_glow > 0f) PutText(_textGlows, text, x, y, px, Faded(colour, _glow * 0.6f), align, (byte)(face | Soft), width);
        return width;
    }

    /// <summary>The width of <paramref name="text"/> at <paramref name="px"/> (1080p pixels) in the element's units, as the HUD font
    /// draws it. 0 while the element has no HUD. Each (text, size, weight) is measured once and kept (up to <see cref="MaxWidths"/> per
    /// canvas, all forgotten when that is passed), so measuring the same strings every frame, typed-on steps included, costs a lookup.</summary>
    public float TextWidth(string text, float px, bool bold = false)
    {
        if (!Sync() || string.IsNullOrEmpty(text)) return 0f;
        var size = _hud.Px(px);
        var face = Face(bold);
        // Elements measure the same few strings every frame; measuring re-lays the text out, so each width is kept.
        var key = (text, size, face);
        if (_widths.TryGetValue(key, out var known)) return known;
        var label = Take(_texts);
        if (Math.Abs(label.fontSize - size) > 0.001f) label.fontSize = size;
        Apply(label, face);
        // The piece's size and face were changed for the measurement: its next text is applied in full.
        _texts.Last[_texts.Used] = default;
        if (_widths.Count >= MaxWidths) _widths.Clear();
        return _widths[key] = Width(label, text);
    }

    public const int MaxWidths = 1024;
    private readonly Dictionary<(string Text, float Size, byte Face), float> _widths = new();

    /// <summary><paramref name="text"/> as it fits <paramref name="width"/> at <paramref name="px"/>: whole when it does, else cut with an
    /// ellipsis, empty when not even that fits. Each answer is kept, so calling it every frame costs a lookup.</summary>
    public string FitText(string text, float px, bool bold, float width)
    {
        if (string.IsNullOrEmpty(text) || TextWidth(text, px, bold) <= width) return text;
        var key = (text, px, bold, width);
        if (_cut.TryGetValue(key, out var known)) return known;
        var n = text.Length - 1;
        while (n > 0 && TextWidth(text.Substring(0, n) + "…", px, bold) > width) n--;
        if (_cut.Count >= MaxWidths) _cut.Clear();
        return _cut[key] = n == 0 && TextWidth("…", px, bold) > width ? "" : text.Substring(0, n) + "…";
    }

    private readonly Dictionary<(string Text, float Px, bool Bold, float Width), string> _cut = new();

    /// <summary>The lines of <paramref name="text"/> no wider than <paramref name="width"/> at <paramref name="px"/> (<see cref="TextWrap"/>), written into <paramref name="into"/> (cleared first).</summary>
    public void Wrap(string text, float px, bool bold, float width, List<string> into) => TextWrap.Lines(text, s => TextWidth(s, px, bold), width, int.MaxValue, into);

    /// <summary>A progress bar in the shared look (<see cref="ProgressBar"/>): from <paramref name="left"/>, <paramref name="width"/>
    /// long, centred on <paramref name="y"/>, filled to the gauge's <see cref="ProgressGauge.Fill"/> in <paramref name="colour"/>, flashing
    /// and folding away after completion. Draws nothing while the gauge is not visible.</summary>
    public void Progress(ProgressGauge gauge, float left, float y, float width, float height, Color colour, float now)
    {
        if (!gauge.Visible) return;
        var count = ProgressBar.Build(_shapes, left, y, width, height, gauge.Fill, new HudColour(colour.r, colour.g, colour.b, colour.a), gauge.DoneAge, now);
        Shapes(_shapes.AsSpan(0, count));
    }

    /// <summary>The gauge as a ring <paramref name="diameter"/> across centred on (<paramref name="x"/>, <paramref name="y"/>): its
    /// <see cref="ProgressGauge.Sweep"/> clockwise from the top in <paramref name="colour"/>, over a faint full ring in <paramref name="track"/>
    /// (fully transparent: none); <paramref name="disc"/> fills the sector instead of drawing a ring. After completion the full ring flashes
    /// white and fades with the bar's flash and fold. Draws nothing while the gauge is not visible.</summary>
    public void Ring(ProgressGauge gauge, float x, float y, float diameter, Color colour, Color track, bool disc = false)
    {
        if (!gauge.Visible) return;
        if (track.a > 0f) Sprite(UIKitSprites.Pie(CooldownMath.PieSteps, !disc), x, y, diameter, diameter, track);
        if (gauge.Running)
        {
            var step = gauge.PieStep;
            if (step > 0) Sprite(UIKitSprites.Pie(step, !disc), x, y, diameter, diameter, colour);
            return;
        }
        var age = gauge.DoneAge;
        if (age < 0f) return;
        var flash = age <= ProgressBar.FlashSeconds ? 1f - age / ProgressBar.FlashSeconds : 0f;
        var fade = age <= ProgressBar.FlashSeconds ? 1f : 1f - Ease.Clamp01((age - ProgressBar.FlashSeconds) / ProgressBar.FoldSeconds);
        var lit = Color.Lerp(colour, Color.white, flash * 0.85f);
        Sprite(UIKitSprites.Pie(CooldownMath.PieSteps, !disc), x, y, diameter, diameter, Faded(lit, fade));
    }

    /// <summary>Draws shapes written by Core drawing code (<see cref="ProgressBar.Build"/> and the like).</summary>
    public void Shapes(ReadOnlySpan<HudShape> shapes)
    {
        foreach (var s in shapes)
        {
            var c = new Color(s.R, s.G, s.B, s.A);
            switch (s.Kind)
            {
                case HudShapeKind.Rect: Rect(s.X, s.Y, s.W, s.H, c); break;
                case HudShapeKind.Cell: Cell(s.X, s.Y, s.W, s.H, c); break;
                default: Line(s.X, s.Y, s.W, s.H, s.T, c); break;
            }
        }
    }

    /// <summary>Ends the frame: every piece that was not asked for is hidden.</summary>
    public void End()
    {
        if (_revealing) RevealOverlay();
        Trim(_rects); Trim(_lines); Trim(_cells); Trim(_sprites); Trim(_plates); Trim(_forms); Trim(_glows); Trim(_fringes);
        Trim(_texts); Trim(_textGlows); Trim(_textFringes);
    }

    /// <summary>Creates a quarter and four more pieces of every kind than the last frame used, so that a state that draws more than
    /// the fullest frame so far finds its pieces ready (call it once after drawing the fullest scene you expect, while hidden).
    /// Everything is hidden afterwards.</summary>
    public void Reserve()
    {
        if (!Sync()) return;
        foreach (var pool in Sprites()) { pool.Used = 0; Pad(pool); }
        foreach (var pool in Texts())
        {
            pool.Used = 0;
            var target = pool.Pieces.Count + pool.Pieces.Count / 4 + 4;
            while (pool.Pieces.Count < target) AddText(pool);
        }
        End();
    }

    /// <summary>Makes every text object build its glyphs now, so the first real frame does not pay for it.</summary>
    public void PrepareText()
    {
        if (!Sync()) return;
        foreach (var pool in Texts())
            foreach (var text in pool.Pieces) text.ForceMeshUpdate(true, true);
    }

    /// <summary>Hides everything and starts the next frame's pieces from nothing used.</summary>
    public void Clear()
    {
        foreach (var pool in Sprites()) pool.Used = 0;
        foreach (var pool in Texts()) pool.Used = 0;
        End();
    }

    /// <summary>A vertical cell gauge centred on (<paramref name="x"/>, <paramref name="y"/>): <paramref name="cells"/> stacked cells
    /// (gap a fifth of a cell) lit from the bottom up to <paramref name="value"/> (0..1), the cell the value ends in lit in proportion,
    /// the rest a faint track. Below <paramref name="warnBelow"/> the lit cells take <paramref name="warn"/>; at 0 the gauge is
    /// framed in red. Negative <paramref name="value"/> draws nothing (unknown).</summary>
    public void CellGauge(float x, float y, float width, float height, int cells, float value, float warnBelow, Color colour, Color warn)
    {
        if (value < 0f) return;
        cells = Math.Clamp(cells, 1, 32);
        value = Math.Min(1f, value);
        var cell = height / (cells + (cells - 1) * 0.2f);
        var step = cell * 1.2f;
        var state = GaugeMath.CellState(value, warnBelow);
        var lit = state == 1 ? warn : colour;
        var bottom = y - height / 2f + cell / 2f;
        for (var i = 0; i < cells; i++)
        {
            var level = GaugeMath.Segment(value, i, cells);
            Rect(x, bottom + i * step, width, cell, level >= 1f ? lit : level > 0f ? Faded(lit, 0.25f + 0.75f * level) : Faded(UIKitColors.Cockpit, 0.13f));
        }
        if (state == 2)
        {
            var pad = cell * 0.4f;
            Frame(x, y, width + 2f * pad, height + 2f * pad, 0f, Faded(UIKitColors.Red, 0.95f));
        }
    }

    // ---- Clickable pieces ---------------------------------------------------------------------------------------------------------

    /// <summary>A label on a plate starting at <paramref name="left"/>, centred on <paramref name="y"/> (34 tall, 16 px bold text), with
    /// an optional <paramref name="icon"/> before the text: a state badge. Returns its width.</summary>
    public float Chip(string text, float left, float y, Color fill, Color ink, Sprite? icon = null)
    {
        var pad = icon != null ? 34f : 15f;
        var width = TextWidth(text, 16f, true) + pad + 15f;
        Plate(left + width / 2f, y, width, 34f, 8f, fill);
        if (icon != null) Sprite(icon, left + 18f, y, 12f, 15f, ink, 25f);
        Text(text, left + pad, y, 16f, ink, HudAlign.Left, bold: true);
        return width;
    }

    /// <summary>A text button whose right edge is at <paramref name="right"/> (which then moves left past it, for a row of buttons
    /// laid out from the right), centred on <paramref name="y"/>: lit in <paramref name="accent"/> while <paramref name="on"/>, framed in
    /// it while the HUD pointer is over it. True on the frame it is clicked. Three looks that never pass for each other: lit (filled in
    /// the accent, dark text, bright outline), available (dark plate, light text) and, when not <paramref name="enabled"/>, unavailable
    /// (no plate, faint outline, dim text; it does not answer the pointer).</summary>
    public bool Button(ref float right, float y, string text, bool on, Color accent, bool enabled = true)
    {
        var width = ButtonWidth(text);
        var cx = right - width / 2f;
        right -= width + 10f;
        if (!enabled)
        {
            Frame(cx, y, width, 34f, 8f, Faded(UIKitColors.Line, 0.25f));
            Text(text, cx, y, 16f, UIKitColors.Dim, HudAlign.Centre, bold: true);
            HudPointer.Click(_hud, cx, y, width, 34f, false);
            return false;
        }
        var over = HudPointer.Hover(_hud, cx, y, width, 34f);
        var down = HudPointer.PressedOn(_hud, cx, y, width, 34f);
        Plate(cx, y, width, 34f, 8f, on ? accent : UIKitColors.Shade);
        Pointed(cx, y, width, 34f, 8f, accent, over, down, on);
        Text(text, cx, y, 16f, on || over ? UIKitColors.Bright : UIKitColors.Text, HudAlign.Centre, bold: true);
        return HudPointer.Click(_hud, cx, y, width, 34f);
    }

    /// <summary>The one look of every clickable thing on the HUD, drawn over its own plate: at rest a faint frame (a selected one: the bright frame of its solid fill), under the pointer a fill and
    /// a frame in <paramref name="accent"/> (a selected one lights its frame), pressed a deeper fill drawn a pixel in. <paramref name="over"/> and <paramref name="down"/> come from
    /// <see cref="HudPointer.Hover"/> and <see cref="HudPointer.PressedOn"/>. A row or a card with no frame of its own passes <paramref name="rest"/> false.</summary>
    public void Pointed(float x, float y, float width, float height, float cut, Color accent, bool over, bool down, bool selected, bool rest = true)
    {
        if (down && over) Plate(x, y, width - 2f, height - 2f, cut, Faded(accent, 0.45f));
        else if (over) Plate(x, y, width, height, cut, Faded(accent, selected ? 0.25f : 0.18f));
        var frame = selected ? over ? UIKitColors.Text : UIKitColors.Bright : over || down ? accent : Faded(UIKitColors.Line, 0.5f);
        if (rest || over || down || selected) Frame(x, y, width, height, cut, frame);
    }

    /// <summary>How wide <see cref="Button"/> draws <paramref name="text"/>, without its 10 gap to the next one.</summary>
    public float ButtonWidth(string text) => TextWidth(text, 16f, true) + 36f;

    // ---- Coming online ------------------------------------------------------------------------------------------------------------

    /// <summary>The scan line (two from the middle for a centred element) and the corner brackets: traced out from the root's corner,
    /// jumping outwards and fading at the lock.</summary>
    private void RevealOverlay()
    {
        _overlay = true;
        var u = Unit;
        if (_sweep > 0f && _sweep < 1f)
        {
            var front = HudReveal.Front(_revealFlow, _x0, _x1, _sweep, out var other);
            var mid = (_y0 + _y1) / 2f;
            var tall = _y1 - _y0 + 12f * u;
            Rect(front, mid, 28f * u, _y1 - _y0, RevealBand);
            Rect(front, mid, 2.5f * u, tall, RevealLine);
            if (!float.IsNaN(other))
            {
                Rect(other, mid, 28f * u, _y1 - _y0, RevealBand);
                Rect(other, mid, 2.5f * u, tall, RevealLine);
            }
        }
        var fade = _lock > 0f ? _lock : 1f;
        var grow = HudReveal.Burst(_revealAge) * 10f * u;
        float x0 = _x0 - grow, x1 = _x1 + grow, y0 = _y0 - grow, y1 = _y1 + grow;
        var arm = Math.Min(x1 - x0, y1 - y0) * 0.35f * HudReveal.Trace(_revealAge) + 2f * u;
        var colour = new Color(RevealBracket.r, RevealBracket.g, RevealBracket.b, RevealBracket.a * fade);
        var thick = 2f * u;
        Bracket(x0, y0, 1f, 1f, arm, thick, colour);
        Bracket(x1, y0, -1f, 1f, arm, thick, colour);
        Bracket(x0, y1, 1f, -1f, arm, thick, colour);
        Bracket(x1, y1, -1f, -1f, arm, thick, colour);
        _overlay = false;
    }

    private void Bracket(float x, float y, float sx, float sy, float arm, float thick, Color colour)
    {
        Line(x, y, x + sx * arm, y, thick, colour);
        Line(x, y, x, y + sy * arm, thick, colour);
    }

    /// <summary>While coming online: whether a piece spanning [<paramref name="left"/>, <paramref name="right"/>] shows yet.</summary>
    private bool Hidden(float left, float right) =>
        _revealing && !_overlay && HudReveal.Passed(_revealFlow, _x0, _x1, _sweep, left, right) <= 0f;

    /// <summary>While coming online, the lock flash lightens every lit piece towards white.</summary>
    private Color Flashed(Color c) =>
        !_revealing || _overlay || _lock <= 0f || !Lit(c) ? c : new Color(c.r + (1f - c.r) * _lock * 0.85f, c.g + (1f - c.g) * _lock * 0.85f, c.b + (1f - c.b) * _lock * 0.85f, c.a);

    // ---- Effects ----------------------------------------------------------------------------------------------------------------

    /// <summary>How much taller the element's outer side is drawn at <paramref name="x"/> (1 with no tilt).</summary>
    private float TiltAt(float x)
    {
        if (_tilt == 0f || _tiltFlow == 0) return 1f;
        var outward = _tiltFlow > 0 ? _tiltWidth - x : x + _tiltWidth;
        return 1f + _tilt * Math.Clamp(outward / _tiltWidth, 0f, 1f);
    }

    private void Effects(Sprite sprite, Fit fit, float x, float y, float w, float h, float degrees, float cut, Color colour, Sprite glow)
    {
        if (!Lit(colour)) return;
        if (_fringe > 0f)
        {
            Put(_fringes, sprite, fit, x - _fringeShift, y, w, h, degrees, cut, Tint(FringeRed, colour, _fringe));
            Put(_fringes, sprite, fit, x + _fringeShift, y, w, h, degrees, cut, Tint(FringeBlue, colour, _fringe));
        }
        if (_glow > 0f) Glow(glow, x, y, w, h, degrees, colour);
    }

    private void Glow(Sprite soft, float x, float y, float w, float h, float degrees, Color colour)
    {
        var r = _glowRadius;
        Put(_glows, soft, Fit.Cut, x, y, Math.Abs(w) + 2f * r, Math.Abs(h) + 2f * r, degrees, r, Faded(colour, _glow * 0.5f));
    }

    /// <summary>A piece bright enough to glow: dark plates and shades get neither glow nor fringe.</summary>
    private static bool Lit(Color c) => c.a > 0.02f && Math.Max(c.r, Math.Max(c.g, c.b)) > 0.35f;

    private static Color Faded(Color c, float share) => new(c.r, c.g, c.b, c.a * share);

    private static Color Tint(Color tint, Color c, float share) => new(tint.r * c.r, tint.g * c.g, tint.b * c.b, c.a * share);

    // ---- Pieces -----------------------------------------------------------------------------------------------------------------

    /// <summary>Places a sprite piece. <see cref="Fit.Plain"/> scales a one-unit sprite to w × h; <see cref="Fit.Cut"/> draws a
    /// sliced sprite whose one-unit border becomes <paramref name="cut"/>; <see cref="Fit.Lean"/> draws the sliced skew with a lean
    /// of <paramref name="cut"/>.</summary>
    private void Put(Pool<SpriteRenderer> pool, Sprite sprite, Fit fit, float x, float y, float w, float h, float degrees, float cut, Color colour)
    {
        if (_revealing)
        {
            var half = Math.Abs(w) / 2f;
            if (Hidden(x - half, x + half)) return;
            if (!ReferenceEquals(pool, _plates)) colour = Flashed(colour);
        }
        var piece = Take(pool, sprite);
        var index = pool.Used++;
        var revived = Revive(piece, pool, index);
        var last = pool.Last[index];
        if (!revived && last.Text != null && ReferenceEquals(last.Sprite, sprite) && last.A == x && last.B == y && last.C == w && last.D == h &&
            last.E == degrees && last.F == cut && last.R == colour.r && last.G == colour.g && last.Bl == colour.b && last.Al == colour.a) return;
        pool.Last[index] = new Last { A = x, B = y, C = w, D = h, E = degrees, F = cut, R = colour.r, G = colour.g, Bl = colour.b, Al = colour.a, Text = "", Sprite = sprite };
        if (!ReferenceEquals(last.Sprite, sprite) || revived) piece.sprite = sprite;
        var t = piece.transform;
        t.localPosition = new Vector3(x, y, 0f);
        t.localRotation = degrees == 0f ? Quaternion.identity : Quaternion.Euler(0f, 0f, degrees);
        switch (fit)
        {
            case Fit.Cut:
                if (piece.drawMode != SpriteDrawMode.Sliced) piece.drawMode = SpriteDrawMode.Sliced;
                t.localScale = new Vector3(cut, cut, 1f);
                piece.size = new Vector2(w / cut, h / cut);
                break;
            case Fit.Lean:
                if (piece.drawMode != SpriteDrawMode.Sliced) piece.drawMode = SpriteDrawMode.Sliced;
                // A left lean is the right lean mirrored.
                t.localScale = new Vector3(cut, 1f, 1f);
                piece.size = new Vector2(w / Math.Abs(cut), h);
                break;
            default:
                if (piece.drawMode != SpriteDrawMode.Simple) piece.drawMode = SpriteDrawMode.Simple;
                t.localScale = new Vector3(w, h, 1f);
                break;
        }
        piece.color = colour;
    }

    private float PutText(Pool<TextMeshPro> pool, string text, float x, float y, float px, Color colour, HudAlign align, byte face, float knownWidth)
    {
        var label = Take(pool);
        var index = pool.Used++;
        var go = label.gameObject;
        var revived = pool.Activity.Wake(index);
        if (revived) go.SetActive(true);
        var before = revived ? default : pool.Last[index];
        var sameText = before.Text != null && (ReferenceEquals(before.Text, text) || string.Equals(before.Text, text, StringComparison.Ordinal));
        if (!_revealing && !revived && sameText && before.Typed == 0 && before.A == x && before.B == y && before.C == px && before.R == colour.r &&
            before.G == colour.g && before.Bl == colour.b && before.Al == colour.a && before.Align == (byte)align && before.Face == face) return before.D;
        if (!sameText) label.SetText(text);
        var size = _hud.Px(px);
        if (Math.Abs(label.fontSize - size) > 0.001f) label.fontSize = size;
        if (before.Face != face) Apply(label, face);
        if (label.alignment != TextAlignmentOptions.MidlineLeft) label.alignment = TextAlignmentOptions.MidlineLeft;
        if (label.rectTransform.pivot.x != 0f) label.rectTransform.pivot = new Vector2(0f, 0.5f);
        var width = knownWidth >= 0f ? knownWidth : sameText && before.C == px && before.Face == face ? before.D : Width(label, text);
        var left = align == HudAlign.Left ? x : align == HudAlign.Centre ? x - width / 2f : x - width;
        // Coming online: the characters the scan line has passed (typing on), the colour flashing at the lock.
        var typed = 0;
        var shown = int.MaxValue;
        if (_revealing && !_overlay)
        {
            var passed = HudReveal.Passed(_revealFlow, _x0, _x1, _sweep, left, left + width);
            if (passed < 1f)
            {
                shown = (int)MathF.Ceiling(text.Length * passed);
                typed = shown + 1;
            }
            colour = Flashed(colour);
        }
        if (label.maxVisibleCharacters != shown) label.maxVisibleCharacters = shown;
        pool.Last[index] = new Last { A = x, B = y, C = px, D = width, R = colour.r, G = colour.g, Bl = colour.b, Al = colour.a, Align = (byte)align, Face = face, Text = text, Typed = typed };
        label.transform.localPosition = new Vector3(left, y - Lift(label.font) * size, 0f);
        label.color = colour;
        return width;
    }

    // TextMeshPro centres a line on the middle of its ascent and descent lines, which is not the middle of the capitals: a font with a deep descent line draws its text above the
    // centre of a block it was put on. The offset, in ems, from the line's middle to the capitals' middle (positive: the text sits high), read from the face's own metrics once per face.
    private static readonly Dictionary<TMP_FontAsset, float> Lifts = new();

    private static float Lift(TMP_FontAsset? font)
    {
        if (font == null) return 0f;
        if (Lifts.TryGetValue(font, out var lift)) return lift;
        var face = font.faceInfo;
        lift = face.pointSize > 0f ? (face.capLine * 0.5f - (face.ascentLine + face.descentLine) * 0.5f) / face.pointSize : 0f;
        Lifts[font] = lift;
        return lift;
    }

    private byte Face(bool bold) =>
        Font == UIKitFont.Tech && Hud.KitFaces != null ? bold ? KitBold : KitRegular : bold ? GameBold : GameRegular;

    private static Material? _gameSoft, _kitRegularSoft, _kitBoldSoft;

    private static void Apply(TextMeshPro label, byte face)
    {
        var soft = (face & Soft) != 0;
        var bare = (byte)(face & ~Soft);
        var kit = bare >= KitRegular ? Hud.KitFaces : null;
        var font = kit != null ? bare == KitBold ? kit.Bold : kit.Regular : Hud.GameFace;
        if (font != null)
        {
            var material = kit != null ? font.material : Hud.GameMaterial ?? font.material;
            if (soft) material = SoftMaterial(material, kit == null ? 0 : bare == KitBold ? 2 : 1);
            label.font = font;
            label.fontSharedMaterial = material;
        }
        var style = bare == GameBold ? FontStyles.Bold : FontStyles.Normal;
        if (label.fontStyle != style) label.fontStyle = style;
    }

    /// <summary>The blurred twin of a font material the text glow is drawn with (made once per face): its glyphs are dilated and
    /// their edges softened.</summary>
    private static Material SoftMaterial(Material source, int slot)
    {
        ref var cached = ref slot == 0 ? ref _gameSoft : ref slot == 1 ? ref _kitRegularSoft : ref _kitBoldSoft;
        if (cached != null && !cached.WasCollected) return cached;
        var soft = new Material(source) { name = source.name + " (ForgeUI glow)", hideFlags = HideFlags.HideAndDontSave };
        if (soft.HasProperty("_FaceDilate")) soft.SetFloat("_FaceDilate", 0.35f);
        if (soft.HasProperty("_OutlineSoftness")) soft.SetFloat("_OutlineSoftness", 0.75f);
        if (soft.HasProperty("_UnderlayColor")) soft.SetColor("_UnderlayColor", Color.clear);
        cached = soft;
        return soft;
    }

    // The laid-out text: TMP's preferred width, or the glyphs' bounds when they are wider (the game font's capitals), so a box drawn to it
    // holds the text in any language.
    private static float Width(TextMeshPro label, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        label.text = text;
        label.ForceMeshUpdate(true, false);
        return Math.Max(label.preferredWidth, label.textBounds.size.x) * label.transform.localScale.x;
    }

    private Pool<SpriteRenderer>[] Sprites() => _spritePools;

    private Pool<TextMeshPro>[] Texts() => _textPools;

    /// <summary>Forgets the pieces when the game has rebuilt the HUD (they went with it). False while there is no HUD.</summary>
    private bool Sync()
    {
        if (_hud.Root == null) return false;
        if (_generation == _hud.Generation) return true;
        _generation = _hud.Generation;
        _widths.Clear();
        foreach (var pool in Sprites()) { pool.Pieces.Clear(); pool.Last.Clear(); pool.Activity.Clear(); pool.Used = 0; }
        foreach (var pool in Texts()) { pool.Pieces.Clear(); pool.Last.Clear(); pool.Activity.Clear(); pool.Used = 0; }
        return true;
    }

    private SpriteRenderer Take(Pool<SpriteRenderer> pool, Sprite sprite)
    {
        if (pool.Used == pool.Pieces.Count)
        {
            Add(pool, _hud.AddSprite(sprite, pool.Order + pool.Step * pool.Pieces.Count));
        }
        return pool.Pieces[pool.Used];
    }

    private TextMeshPro Take(Pool<TextMeshPro> pool)
    {
        if (pool.Used == pool.Pieces.Count) AddText(pool);
        return pool.Pieces[pool.Used];
    }

    private void AddText(Pool<TextMeshPro> pool)
    {
        var text = _hud.AddText();
        // Glow and fringe copies draw just behind the texts.
        if (pool.Order != 0)
        {
            var renderer = text.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sortingOrder += pool.Order;
        }
        Add(pool, text);
    }

    private void Pad(Pool<SpriteRenderer> pool)
    {
        var target = pool.Pieces.Count + pool.Pieces.Count / 4 + 4;
        while (pool.Pieces.Count < target)
        {
            Add(pool, _hud.AddSprite(UIKitSprites.Pixel, pool.Order + pool.Step * pool.Pieces.Count));
        }
    }

    /// <summary>Appends a made piece to its pool, with the state it was made in.</summary>
    private static void Add<T>(Pool<T> pool, T piece) where T : Component
    {
        pool.Pieces.Add(piece);
        pool.Last.Add(default);
        pool.Activity.Added(piece.gameObject.activeSelf);
    }

    /// <summary>Switches a hidden piece on; true when it was off (its remembered state is then void).</summary>
    private static bool Revive(SpriteRenderer sprite, Pool<SpriteRenderer> pool, int index)
    {
        if (!pool.Activity.Wake(index)) return false;
        sprite.gameObject.SetActive(true);
        pool.Last[index] = default;
        return true;
    }

    private static void Trim<T>(Pool<T> pool) where T : Component
    {
        var lit = pool.Activity.Lit;
        for (var i = pool.Used; i < lit; i++)
        {
            if (pool.Activity.Sleep(i)) pool.Pieces[i].gameObject.SetActive(false);
            pool.Last[i] = default;
        }
        pool.Activity.Settled(pool.Used);
    }
}
