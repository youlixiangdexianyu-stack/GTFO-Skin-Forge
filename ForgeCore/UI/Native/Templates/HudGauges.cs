using EnemyAlert = ForgeCore.EnemyAlert;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// A compact segmented bar for the top of the screen (a cockpit's armour and shield) that says everything with the bar itself: the
/// main bar in slanted segments, lit in its colour with a lighter upper and a darker lower half, a lost segment left as a hollow
/// outline; an optional layer riding its top edge (<see cref="Overlay"/>, a shield: lit while it stands, flashing white and leaving a
/// fading trail when struck, flashing brighter when it breaks, a red track filling back in its colour while broken
/// (<see cref="OverlayProgress"/>), its place left empty while <see cref="OverlayShown"/> says no, its own number left of the bar with
/// <see cref="OverlayReadoutMax"/>);
/// an optional hairline under it (<see cref="Under"/>, the pilot's own health, with an infection share from its right end); folded
/// brackets at both ends and one number (<see cref="Readout"/>) beside the right one. The bar is centred on its region. Segments fill
/// in part; a drop leaves a fading trail; a segment that fills again flashes white; the layer above flashes white when it breaks.
/// Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddSegmentBar"/>.
/// </summary>
public sealed class HudSegmentBar : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const int MaxSegments = 64;
    private readonly Func<float> _value;
    private readonly float[] _filledAt = new float[MaxSegments];
    private HudCanvas? _canvas;
    private MeterTrail _trail, _overlayTrail;
    private readonly SegmentBarMotion _motion = new(), _overlayMotion = new();
    private readonly LayerHits _overlayHits = new();
    private readonly Measured _readoutWidth = new(), _overlayWidth = new();
    private bool _trailSet, _overlayTrailSet;
    private int _lit = -1;
    private float _criticalSince = -1f;

    internal HudSegmentBar(Func<float> value, int segments)
    {
        _value = value;
        Segments = segments;
    }

    /// <summary>How many segments the main bar has (1 to 64).</summary>
    public int Segments { get; set; }

    /// <summary>The thin layer on the main bar's top edge, 0..1 (a shield); null draws none. At 0 it reads as broken.</summary>
    public Func<float>? Overlay { get; set; }

    /// <summary>Segments of the thin layer; 0 (default) uses <see cref="Segments"/>.</summary>
    public int OverlaySegments { get; set; }

    /// <summary>Whether the layer and its number are drawn now (a shield that stands only in one mode); false leaves their place empty so
    /// the bar does not move. Null always draws them.</summary>
    public Func<bool>? OverlayShown { get; set; }

    /// <summary>The layer's number left of the bar: its drawn fill times this, rounded up (shield points rolling with it), in
    /// <see cref="OverlayColour"/>, red at 0; 0 (default) draws none.</summary>
    public float OverlayReadoutMax { get; set; }

    /// <summary>The layer's number's size, 1080p pixels; default 20.</summary>
    public float OverlayReadoutPx { get; set; } = 20f;

    /// <summary>A small picture before the layer's number (a shield mark); null draws none.</summary>
    public Sprite? OverlayIcon { get; set; }

    /// <summary>A broken layer's way back, 0..1, filled faintly in its colour over the red track; negative draws none.</summary>
    public Func<float>? OverlayProgress { get; set; }

    /// <summary>The thin bar below, 0..1 (for example <c>() =&gt; UIKit.Vitals.Health</c>); null draws none.</summary>
    public Func<float>? Under { get; set; }

    /// <summary>Segments of the thin bar below; default 1 (continuous).</summary>
    public int UnderSegments { get; set; } = 1;

    /// <summary>A share of the thin bar below, 0..1, drawn from its right end in <see cref="InfectionColour"/> (for example
    /// <c>() =&gt; UIKit.Vitals.Infection</c>); null draws none.</summary>
    public Func<float>? Infection { get; set; }

    /// <summary>The infection share's colour; default green.</summary>
    public Color InfectionColour { get; set; } = new(0.35f, 0.85f, 0.45f, 0.9f);

    /// <summary>The thin bar below is <see cref="UnderColour"/> from this value up and blends to red below it.</summary>
    public float UnderWarnAt { get; set; } = 0.5f;

    /// <summary>The thin bar below is fully red at and below this value.</summary>
    public float UnderCriticalAt { get; set; } = 0.25f;

    /// <summary>The one number right of the bar, in the bar's colour (return the same string while it is the same); null draws
    /// none, or the bar's own number when <see cref="ReadoutMax"/> is set.</summary>
    public Func<string>? Readout { get; set; }

    /// <summary>Without <see cref="Readout"/>: the number right of the bar is the drawn fill times this, rounded up (armour points rolling
    /// with the bar); 0 (default) draws none.</summary>
    public float ReadoutMax { get; set; }

    /// <summary>How fast the drawn fill follows the value (per second, exponential); default 14.</summary>
    public float EaseRate { get; set; } = 14f;

    /// <summary>With <see cref="MeterPulse.FlashOnLoss"/>: under this the bar flashes without pause (about to fail); negative (default)
    /// never.</summary>
    public float FailAt { get; set; } = -1f;

    /// <summary>The fill starts again from 0 and rolls up to the value, its number with it (the bar coming online).</summary>
    public void RollUp()
    {
        _motion.RollUp();
        _overlayMotion.RollUp();
    }

    /// <summary>The next frame shows the value at once, with no easing, trail or alarm from before (a new subject, for example another
    /// suit).</summary>
    public void Restart()
    {
        _motion.Restart();
        _overlayMotion.Restart();
        _overlayHits.Restart();
        _trailSet = _overlayTrailSet = false;
        _lit = -1;
    }

    /// <summary>The readout's size, 1080p pixels; default 26.</summary>
    public float ReadoutPx { get; set; } = 26f;

    /// <summary>The main bar's length, 1080p pixels; default 420.</summary>
    public float Width { get; set; } = 420f;

    /// <summary>The main bar's height; default 12.</summary>
    public float Height { get; set; } = 12f;

    /// <summary>The thin layer's height; default 3.</summary>
    public float OverlayHeight { get; set; } = 3f;

    /// <summary>The hairline's height; default 2.</summary>
    public float UnderHeight { get; set; } = 2f;

    /// <summary>How far the bars' ends sit below their middle (a curved visor); default 0.</summary>
    public float Curve { get; set; }

    /// <summary>How far each segment's top edge is shifted right of its bottom (slanted segments); default 6.</summary>
    public float Skew { get; set; } = 6f;

    /// <summary>The gap between segments; default 5.</summary>
    public float SegmentGap { get; set; } = 5f;

    /// <summary>Opacity of the dark chamfered plate behind the bars (0 draws none); default 0.</summary>
    public float Plate { get; set; }

    /// <summary>Ticks under the main bar: a long one per segment and 40 short ones along it. Default false.</summary>
    public bool Ruler { get; set; }


    public Color Colour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.86f);
    public Color OverlayColour { get; set; } = UIKitColors.Faded(UIKitColors.Cyan, 0.85f);
    public Color UnderColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.8f);
    /// <summary>At or below this the main bar turns amber.</summary>
    public float WarnAt { get; set; } = 0.4f;
    /// <summary>At or below this the main bar turns red; negative turns it off.</summary>
    public float CriticalAt { get; set; } = 0.2f;
    public MeterPulse CriticalPulse { get; set; } = MeterPulse.FlashThenSteady;
    /// <summary>What a drop takes away lingers in orange (red when critical) and fades. Default true.</summary>
    public bool Trail { get; set; } = true;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _trailSet = false;
        _lit = -1;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var count = Math.Clamp(Segments, 1, MaxSegments);
        var target = Read(_value, "value");
        if (!_trailSet) { _trail.Reset(target); _trailSet = true; }
        _trail.Update(target, dt);
        // The colour follows the real value (a fill rolling up from 0 is not low); the drawn fill and its number ease.
        var tone = CriticalAt < 0f ? (target <= WarnAt ? MeterTone.Warn : MeterTone.Normal) : MeterMath.Tone(target, WarnAt, CriticalAt, false);
        _motion.Update(target, dt, now, EaseRate, count, tone == MeterTone.Critical && CriticalPulse == MeterPulse.FlashOnLoss, FailAt, ReadoutMax);
        var value = _motion.Shown;

        // A segment that fills again flashes white for a moment.
        var lit = (int)MathF.Ceiling(value * count - 0.001f);
        if (_lit >= 0) for (var i = _lit; i < lit && i < count; i++) _filledAt[i] = now;
        _lit = lit;

        var overlay = Overlay != null ? Read(Overlay, "overlay") : -1f;
        var overlayProgress = OverlayProgress != null ? Read(OverlayProgress, "overlay progress", true) : -1f;
        var under = Under != null ? Read(Under, "under") : -1f;
        var infection = Infection != null ? Read(Infection, "infection") : 0f;
        var overlayShown = Overlay != null && (OverlayShown == null || ReadBool(OverlayShown));
        var overlayCount = Math.Clamp(OverlaySegments > 0 ? OverlaySegments : count, 1, MaxSegments);
        if (Overlay != null)
        {
            if (!_overlayTrailSet || !overlayShown) { _overlayTrail.Reset(overlay); _overlayTrailSet = true; }
            _overlayTrail.Update(overlay, dt);
            _overlayHits.Update(overlay, overlayShown, now);
            _overlayMotion.Update(overlay, dt, now, EaseRate, overlayCount, false, -1f, OverlayReadoutMax);
        }
        var overlayDrawn = Overlay != null ? _overlayMotion.Shown : 0f;

        if (tone == MeterTone.Critical) { if (_criticalSince < 0f) _criticalSince = now; }
        else _criticalSince = -1f;
        var main = UIKitColors.Tone(tone, Colour);
        main = UIKitColors.Faded(main, GaugeMath.CriticalAlpha(CriticalPulse, _criticalSince < 0f ? -1f : now - _criticalSince, now));
        if (_motion.Alarm > 0f) main = new Color(Mathf.Lerp(main.r, 1f, _motion.Alarm * 0.7f), Mathf.Lerp(main.g, 1f, _motion.Alarm * 0.7f), Mathf.Lerp(main.b, 1f, _motion.Alarm * 0.7f), main.a);

        // The one number, measured only when it changes; the bar stays centred with room for it on both sides.
        var readout = Readout != null ? Text(Readout, "readout") : ReadoutMax > 0f ? _motion.Readout : "";
        var overlayReadout = overlayShown && OverlayReadoutMax > 0f ? _overlayMotion.Readout : "";
        var ends = 22f * u;
        var iconSize = 16f * u;
        var overlaySide = overlayReadout.Length > 0 ? _overlayWidth.Of(_canvas, overlayReadout, OverlayReadoutPx, u) + (OverlayIcon != null ? iconSize + 4f * u : 0f) : 0f;
        var side = ends + Math.Max(_readoutWidth.Of(_canvas, readout, ReadoutPx, u), overlaySide);
        var width = Width * u;
        var curve = Curve * u;
        var lean = Skew * u;
        var gap = SegmentGap * u;
        var stack = (Overlay != null ? OverlayHeight + 2f : 0f) + Height + (Under != null ? 4f + UnderHeight : 0f) + (Ruler ? 7f : 0f);
        var lineHeight = Math.Max(stack, ReadoutPx) * u;
        var (fx, fy) = HudFlow.Of(hud);
        var left = ScreenMath.Span(fx, side + width + side) + side;
        var right = left + width;
        var mid = ScreenMath.Span(fy, lineHeight + curve) + lineHeight / 2f + curve;
        var y = mid + stack * u / 2f;
        var mainCentre = y - ((Overlay != null ? OverlayHeight + 2f : 0f) + Height / 2f) * u;

        if (Plate > 0f)
            _canvas.Plate((left + right) / 2f, mid, side + width + side + 24f * u, lineHeight + 14f * u, 10f * u,
                UIKitColors.Faded(UIKitColors.Plate, Ease.Clamp01(Plate)));

        // The thin layer rides the slanted cells' top edges, the hairline their bottom edges: cells a lean shorter, gaps a lean wider.
        if (Overlay != null)
        {
            var h = OverlayHeight * u;
            var centre = y - h / 2f;
            var top = left + lean;
            var edges = width - lean;
            var edgeGap = overlayCount == count ? gap + lean : gap;
            if (overlayShown)
            {
                if (overlay <= 0f)
                {
                    Band(top, centre, h, overlayCount, 0f, edgeGap, curve, 0f, 1f, UIKitColors.Faded(UIKitColors.Red, 0.4f), BandLook.Flat, edges);
                    if (overlayProgress > 0f) Band(top, centre, h, overlayCount, 0f, edgeGap, curve, 0f, Ease.Clamp01(overlayProgress), UIKitColors.Faded(OverlayColour, 0.5f), BandLook.Flat, edges);
                }
                else
                {
                    Band(top, centre, h, overlayCount, 0f, edgeGap, curve, 0f, overlayDrawn, OverlayColour, BandLook.Flat, edges);
                    if (Trail && _overlayTrail.Trail > overlayDrawn + 0.002f)
                        Band(top, centre, h, overlayCount, 0f, edgeGap, curve, overlayDrawn, Math.Min(_overlayTrail.Trail, 1f), UIKitColors.Faded(Color.white, 0.55f), BandLook.Flat, edges);
                    Band(top, centre, h, overlayCount, 0f, edgeGap, curve, Math.Max(overlayDrawn, _overlayTrail.Trail), 1f, UIKitColors.Faded(OverlayColour, 0.14f), BandLook.Flat, edges);
                }
                var flash = _overlayHits.Flash(now);
                if (flash > 0f) Band(top, centre, h + 2f * u, overlayCount, 0f, edgeGap, curve, 0f, 1f, UIKitColors.Faded(Color.white, flash), BandLook.Flat, edges);
                if (overlayReadout.Length > 0)
                {
                    var shade = overlay <= 0f ? UIKitColors.Red : OverlayColour;
                    var number = Color.Lerp(UIKitColors.Faded(shade, 0.95f), Color.white, flash);
                    var nx = left - ends;
                    var w = _canvas.Text(overlayReadout, nx, mainCentre - curve, OverlayReadoutPx, number, HudAlign.Right, true);
                    if (OverlayIcon != null) _canvas.Icon(OverlayIcon, nx - w - 4f * u - iconSize / 2f, mainCentre - curve, iconSize, iconSize, number);
                }
            }
            y -= h + 2f * u;
        }

        {
            var h = Height * u;
            var centre = y - h / 2f;
            var hollow = UIKitColors.Tone(tone, Colour);
            Band(left, centre, h, count, lean, gap, curve, value, 1f, hollow, BandLook.Hollow);
            if (Trail && _trail.Trail > value + 0.002f)
                Band(left, centre, h, count, lean, gap, curve, value, Math.Min(_trail.Trail, 1f), UIKitColors.Faded(tone == MeterTone.Critical ? UIKitColors.Red : UIKitColors.Orange, 0.8f), BandLook.Flat);
            Band(left, centre, h, count, lean, gap, curve, 0f, value, main, BandLook.Lit);
            var cell = (width - gap * (count - 1)) / count;
            for (var i = 0; i < count; i++)
            {
                if (now - _filledAt[i] >= 0.08f) continue;
                var x = left + i * (cell + gap) + cell / 2f;
                var cy = centre - GaugeMath.Droop((i + 0.5f) / count, curve);
                _canvas.Skew(x, cy, cell, h, Math.Min(lean, cell / 2f), UIKitColors.Faded(Color.white, 0.7f));
            }
            y -= h;
            if (Ruler)
            {
                var ry = y - 2f * u;
                var minor = UIKitColors.Faded(UIKitColors.Cockpit, 0.15f);
                var major = UIKitColors.Faded(UIKitColors.Cockpit, 0.38f);
                for (var i = 0; i <= 40; i++)
                {
                    var t = i / 40f;
                    var x = left + width * t;
                    var dy = GaugeMath.Droop(t, curve);
                    var isMajor = i * count % 40 == 0;
                    var length = (isMajor ? 4f : 2f) * u;
                    _canvas.Line(x, ry - dy, x, ry - dy - length, u, isMajor ? major : minor);
                }
                y -= 7f * u;
            }
        }

        if (Under != null)
        {
            y -= 4f * u;
            var h = UnderHeight * u;
            var centre = y - h / 2f;
            var edges = width - lean;
            var warn = Math.Max(UnderWarnAt, UnderCriticalAt + 0.001f);
            var redness = 1f - Ease.Clamp01((under - UnderCriticalAt) / (warn - UnderCriticalAt));
            var colour = Color.Lerp(UnderColour, UIKitColors.Faded(UIKitColors.Red, UnderColour.a), redness);
            var underCount = Math.Clamp(UnderSegments, 1, MaxSegments);
            var underGap = underCount == count ? gap + lean : underCount > 1 ? gap : 0f;
            Band(left, centre, h, underCount, 0f, underGap, curve, 0f, under, colour, BandLook.Flat, edges);
            Band(left, centre, h, underCount, 0f, underGap, curve, under, 1f, UIKitColors.Faded(UIKitColors.Cockpit, 0.13f), BandLook.Flat, edges);
            if (infection > 0f) Band(left, centre, h, underCount, 0f, underGap, curve, 1f - infection, 1f, InfectionColour, BandLook.Flat, edges);
        }
        Brackets(left, right, mid, stack * u + 6f * u, curve, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.45f));

        if (readout.Length > 0)
            _canvas.Text(readout, right + ends, mainCentre - curve, ReadoutPx, UIKitColors.Faded(main, 1f / Math.Max(0.01f, main.a) * 0.95f), HudAlign.Left, true);
        _canvas.End();
    }

    /// <summary>A number's width, measured again only when its string or the HUD scale changes (the text is the same string object
    /// while it reads the same).</summary>
    private sealed class Measured
    {
        private string? _text;
        private float _px, _width;

        internal float Of(HudCanvas canvas, string text, float px, float u)
        {
            if (text.Length == 0) return 0f;
            if (ReferenceEquals(_text, text) && _px == px * u) return _width;
            _text = text;
            _px = px * u;
            return _width = canvas.TextWidth(text, px, true);
        }
    }

    private enum BandLook : byte { Flat, Lit, Hollow }

    /// <summary>Paints the value range <paramref name="from"/>..<paramref name="to"/> (0..1 of the bar) of a row of
    /// <paramref name="count"/> slanted segments whose middles follow the curve: flat, lit (a lighter upper and a darker lower half with
    /// a bright top edge) or hollow (every segment not wholly lit drawn as an outline over a faint fill). <paramref name="span"/> is the row's
    /// length (0: <see cref="Width"/>).</summary>
    private void Band(float left, float centre, float h, int count, float lean, float gap, float curve, float from, float to, Color colour, BandLook look, float span = 0f)
    {
        if (to <= from || colour.a <= 0f) return;
        var u = _canvas!.Unit;
        var width = span > 0f ? span : Width * u;
        var cell = (width - gap * (count - 1)) / count;
        for (var i = 0; i < count; i++)
        {
            var a = Math.Max(from * count - i, 0f);
            var b = Math.Min(to * count - i, 1f);
            if (b <= a) continue;
            var cy = centre - GaugeMath.Droop((i + 0.5f) / count, curve);
            if (look == BandLook.Hollow)
            {
                var cx = left + i * (cell + gap) + cell / 2f;
                var slant = Math.Min(lean, cell / 2f);
                _canvas.Skew(cx + cell * (a + b - 1f) / 2f, cy, cell * (b - a), h, Math.Min(slant, cell * (b - a) / 2f), UIKitColors.Faded(colour, 0.1f));
                var line = UIKitColors.Faded(colour, 0.5f);
                float x0 = cx - cell / 2f, x1 = cx + cell / 2f, top = cy + h / 2f, bottom = cy - h / 2f, s = slant / 2f;
                _canvas.Line(x0 - s, bottom, x1 - s, bottom, u, line);
                _canvas.Line(x0 + s, top, x1 + s, top, u, line);
                _canvas.Line(x0 - s, bottom, x0 + s, top, u, line);
                _canvas.Line(x1 - s, bottom, x1 + s, top, u, line);
                continue;
            }
            var x = left + i * (cell + gap) + cell * a;
            var w = cell * (b - a);
            var l = Math.Min(lean, w / 2f);
            if (look == BandLook.Flat) { _canvas.Skew(x + w / 2f, cy, w, h, l, colour); continue; }
            var light = Color.Lerp(colour, Color.white, 0.3f);
            light.a = colour.a;
            var dark = new Color(colour.r * 0.7f, colour.g * 0.7f, colour.b * 0.7f, colour.a);
            _canvas.Skew(x + w / 2f + l / 4f, cy + h / 4f, w, h / 2f, l / 2f, light);
            _canvas.Skew(x + w / 2f - l / 4f, cy - h / 4f, w, h / 2f, l / 2f, dark);
            if (w > l + 2f * u)
                _canvas.Rect(x + w / 2f + l / 2f, cy + h / 2f - 0.5f * u, w - l, u, UIKitColors.Faded(Color.white, 0.45f * colour.a));
        }
    }

    /// <summary>Folded bracket ends just outside both ends of the main bar.</summary>
    private void Brackets(float left, float right, float centre, float height, float curve, float u, Color colour)
    {
        var cy = centre - curve;
        var half = height / 2f;
        var outX = 14f * u;
        var tip = 6f * u;
        _canvas!.Line(left - outX + tip, cy + half, left - outX, cy, 1.4f * u, colour);
        _canvas.Line(left - outX, cy, left - outX + tip, cy - half, 1.4f * u, colour);
        _canvas.Line(right + outX - tip, cy + half, right + outX, cy, 1.4f * u, colour);
        _canvas.Line(right + outX, cy, right + outX - tip, cy - half, 1.4f * u, colour);
    }

    private float Read(Func<float> f, string what, bool signed = false)
    {
        try
        {
            var v = f();
            return signed ? (float.IsFinite(v) ? v : -1f) : Ease.Clamp01(v);
        }
        catch (Exception error)
        {
            Plugin.Error("ui-segment-bar-failed", "Segment bar '" + Id + "' " + what + " failed: " + error);
            return signed ? -1f : 0f;
        }
    }

    private bool ReadBool(Func<bool> f)
    {
        try { return f(); }
        catch (Exception error) { Plugin.Error("ui-segment-bar-dim-check-failed", "Segment bar '" + Id + "' dim check failed: " + error); return false; }
    }

    private string Text(Func<string> f, string what)
    {
        try { return f() ?? ""; }
        catch (Exception error) { Plugin.Error("ui-segment-bar-failed", "Segment bar '" + Id + "' " + what + " failed: " + error); return ""; }
    }
}

/// <summary>
/// A half arc beside the crosshair that fills from the bottom up (rounds in the magazine on the left, heat on the right): one cell per
/// round up to <see cref="MaxCells"/> (merged above) or continuous, a faint track for the empty part, amber and red past its marks
/// (red flashes, then holds), a refill sweep (reloading), optional ticks outside it, a faint inner ring, a value at its lower end, an
/// icon at its upper end and a label beside it while critical. Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddArcGauge"/>.
/// </summary>
public sealed class HudArcGauge : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const int Steps = 40;
    private readonly Func<float> _value;
    private readonly ArcSide _side;
    private HudCanvas? _canvas;
    private float _refillFrom, _refillStart = -1f, _refillSeconds, _shown = -1f, _criticalSince = -1f;
    private ArcFlare _flare;
    private string? _readoutRaw;
    private string _readoutText = "";

    internal HudArcGauge(Func<float> value, ArcSide side)
    {
        _value = value;
        _side = side;
    }

    /// <summary>Cells (for example one per round); 0 (default) fills continuously.</summary>
    public int Cells { get; set; }

    /// <summary>More cells than this are merged into this many; default 40, 0 for no limit.</summary>
    public int MaxCells { get; set; } = 40;

    /// <summary>Distance from the crosshair; default 96.</summary>
    public float Radius { get; set; } = 96f;

    /// <summary>The arc's angle in degrees, centred on the horizontal; default 72.</summary>
    public float Sweep { get; set; } = 72f;

    /// <summary>Line width; default 3.2.</summary>
    public float Thickness { get; set; } = 3.2f;

    public Color Colour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.9f);

    /// <summary>Ticks just outside the arc, evenly from bottom to top (5 marks quarters); default 0.</summary>
    public int OuterTicks { get; set; }

    /// <summary>A faint thin arc just inside, pairing the two arcs as one gauge. Default false.</summary>
    public bool InnerRing { get; set; }

    /// <summary>A value outside the arc's lower end (rounds left, heat with "°"), in tabular digits; null draws none. Return the same
    /// string while the value is unchanged.</summary>
    public Func<string>? EndReadout { get; set; }

    /// <summary>A small picture outside the arc's upper end (a heat icon); null draws none.</summary>
    public Sprite? EndIcon { get; set; }

    /// <summary>A label beside the arc while it is critical (for example "OVERHEAT"); null draws none.</summary>
    public UIText? CriticalLabel { get; set; }

    public float WarnAt { get; set; } = 0.25f;
    public float CriticalAt { get; set; } = 0.15f;
    /// <summary>For heat: high values turn it amber and red.</summary>
    public bool HigherIsWorse { get; set; }
    public MeterPulse CriticalPulse { get; set; } = MeterPulse.FlashThenSteady;
    /// <summary>A refill progress 0..1 given by you (negative: not refilling); when set, <see cref="Refill"/> is not needed.</summary>
    public Func<float>? Refilling { get; set; }

    /// <summary>True holds the arc critical whatever its value (an overheat the game reports); null (default) goes by the value.</summary>
    public Func<bool>? Critical { get; set; }

    /// <summary>The arc's opacity at rest; it draws at full strength for <see cref="FlareSeconds"/> after it starts refilling, turns
    /// amber or red, or <see cref="Flare"/> is called, and always while critical. Default 1.</summary>
    public float SteadyAlpha { get; set; } = 1f;

    /// <summary>See <see cref="SteadyAlpha"/>; default 0.8.</summary>
    public float FlareSeconds { get; set; } = 0.8f;

    /// <summary>Full strength for <see cref="FlareSeconds"/> from now (something about it changed).</summary>
    public void Flare() => _flare.Flare(ForgeCore.Frame.GameTime, FlareSeconds);

    /// <summary>Sweeps the arc full over <paramref name="seconds"/> from its current value (reloading); the value is not read meanwhile.</summary>
    public void Refill(float seconds)
    {
        _refillFrom = Read();
        _refillStart = ForgeCore.Frame.GameTime;
        _refillSeconds = Math.Max(0.05f, seconds);
    }

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        float value;
        var given = -1f;
        if (Refilling != null)
        {
            try { given = Refilling(); }
            catch (Exception error) { Plugin.Error("ui-arc-gauge-refill-failed", "Arc gauge '" + Id + "' refill failed: " + error); Refilling = null; }
        }
        if (given >= 0f) value = Ease.Clamp01(given);
        else if (_refillStart >= 0f && now - _refillStart < _refillSeconds) value = GaugeMath.Refill(_refillFrom, (now - _refillStart) / _refillSeconds);
        else
        {
            _refillStart = -1f;
            value = Read();
        }
        var refilling = given >= 0f || _refillStart >= 0f;
        _shown = _shown < 0f ? value : Ease.Damp(_shown, value, 16f, dt);
        value = _shown;
        var tone = MeterMath.Tone(value, HigherIsWorse ? 1f - WarnAt : WarnAt, HigherIsWorse ? 1f - CriticalAt : CriticalAt, HigherIsWorse);
        if (Critical != null)
        {
            try { if (Critical()) tone = MeterTone.Critical; }
            catch (Exception error) { Plugin.Error("ui-arc-gauge-critical-check-failed", "Arc gauge '" + Id + "' critical check failed: " + error); Critical = null; }
        }
        if (tone == MeterTone.Critical) { if (_criticalSince < 0f) _criticalSince = now; }
        else _criticalSince = -1f;
        var colour = UIKitColors.Tone(tone, Colour);
        colour = UIKitColors.Faded(colour, GaugeMath.CriticalAlpha(CriticalPulse, _criticalSince < 0f ? -1f : now - _criticalSince, now)
                                           * _flare.Alpha(tone, refilling, now, SteadyAlpha, FlareSeconds));
        var track = UIKitColors.Faded(UIKitColors.Cockpit, 0.13f);
        var radius = Radius * u;
        var thickness = Thickness * u;
        var cells = GaugeMath.ArcCells(Cells, MaxCells);
        var count = cells > 0 ? cells : Steps;
        var gap = cells > 0 ? Math.Min(3f, 0.25f * Sweep / count) : 0f;
        for (var i = 0; i < count; i++)
        {
            var a0 = GaugeMath.ArcAngle(_side, Sweep, i / (float)count) + (cells > 0 ? Sign() * gap / 2f : 0f);
            var a1 = GaugeMath.ArcAngle(_side, Sweep, (i + 1) / (float)count) - (cells > 0 ? Sign() * gap / 2f : 0f);
            var lit = cells > 0 ? i < MathF.Round(value * cells) : (i + 0.5f) / count <= value;
            Arc(a0, a1, radius, lit ? colour : track, thickness);
        }
        if (InnerRing)
        {
            const int segments = 24;
            var inner = radius - thickness - 5f * u;
            var faint = UIKitColors.Faded(UIKitColors.Cockpit, 0.10f);
            for (var i = 0; i < segments; i++)
                Arc(GaugeMath.ArcAngle(_side, Sweep, i / (float)segments), GaugeMath.ArcAngle(_side, Sweep, (i + 1) / (float)segments), inner, faint, u);
        }
        var ticks = Math.Max(0, OuterTicks);
        if (ticks > 0)
        {
            var tickColour = UIKitColors.Faded(UIKitColors.Cockpit, 0.38f);
            var from = radius + thickness / 2f + 3f * u;
            for (var i = 0; i < ticks; i++)
            {
                var r = GaugeMath.ArcAngle(_side, Sweep, ticks == 1 ? 0.5f : i / (float)(ticks - 1)) * Mathf.Deg2Rad;
                _canvas.Line(MathF.Cos(r) * from, MathF.Sin(r) * from, MathF.Cos(r) * (from + 5f * u), MathF.Sin(r) * (from + 5f * u), u, tickColour);
            }
        }
        var align = _side == ArcSide.Right ? HudAlign.Left : HudAlign.Right;
        if (EndReadout != null)
        {
            string text;
            try { text = EndReadout() ?? ""; }
            catch (Exception error) { Plugin.Error("ui-arc-gauge-readout-failed", "Arc gauge '" + Id + "' readout failed: " + error); EndReadout = null; text = ""; }
            if (!ReferenceEquals(text, _readoutRaw)) { _readoutRaw = text; _readoutText = text.Length > 0 ? NumberText.Tabular(text) : ""; }
            if (_readoutText.Length > 0)
            {
                var r = (GaugeMath.ArcAngle(_side, Sweep, 0f) - Sign() * 6f) * Mathf.Deg2Rad;
                var at = radius + 12f * u;
                _canvas.Text(_readoutText, MathF.Cos(r) * at, MathF.Sin(r) * at, 18f, colour, align, true);
            }
        }
        if (EndIcon != null)
        {
            var r = (GaugeMath.ArcAngle(_side, Sweep, 1f) + Sign() * 7f) * Mathf.Deg2Rad;
            var at = radius + 10f * u;
            _canvas.Icon(EndIcon, MathF.Cos(r) * at, MathF.Sin(r) * at, 14f * u, 14f * u, UIKitColors.Faded(colour, 0.8f));
        }
        if (tone == MeterTone.Critical && CriticalLabel != null)
        {
            var label = Show(CriticalLabel);
            if (label.Length > 0) _canvas.Text(label, Sign() * (radius + 16f * u), 0f, 14f, colour, align, true);
        }
        _canvas.End();
    }

    /// <summary>Which way angles grow from bottom to top on this side.</summary>
    private float Sign() => _side == ArcSide.Right ? 1f : -1f;

    private void Arc(float a0, float a1, float radius, Color colour, float thickness)
    {
        var r0 = a0 * Mathf.Deg2Rad;
        var r1 = a1 * Mathf.Deg2Rad;
        _canvas!.Line(MathF.Cos(r0) * radius, MathF.Sin(r0) * radius, MathF.Cos(r1) * radius, MathF.Sin(r1) * radius, thickness, colour);
    }

    private float Read()
    {
        try { return Ease.Clamp01(_value()); }
        catch (Exception error) { Plugin.Error("ui-arc-gauge-value-failed", "Arc gauge '" + Id + "' value failed: " + error); return 0f; }
    }
}

/// <summary>
/// Contacts round the crosshair (nearby enemies) on an invisible ring: four short bearing ticks and a diamond per contact, sized by
/// strength and coloured by alert state the way ForgeVisual's enemy outline is (sleeping teal, awake amber, hunting red); awake and hunting
/// contacts trail two short arcs. With <see cref="HideInView"/> the contacts inside the camera's view are left to the outline. With
/// <see cref="RearIcon"/> or <see cref="RearText"/> set, enough hunting contacts straight behind show a "turn around" hint below. With
/// <see cref="Compass"/> the game's compass points (beacons, teammates) are drawn just outside the ring as well.
/// Fill it with <see cref="Begin"/> and <see cref="Add"/> as often as you scan; it draws the last set every frame without allocating.
/// Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddBearingArc"/>.
/// </summary>
public sealed class HudBearingArc : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private readonly BearingContacts _contacts;
    private HudCanvas? _canvas;
    private string? _rearSource;
    private int _rearCount = -1;
    private string _rearShown = "";

    private float _autoRadius, _autoEvery, _autoAt;

    internal HudBearingArc(int capacity) => _contacts = new BearingContacts(capacity);

    /// <summary>The ring's radius; default 300.</summary>
    public float Radius { get; set; } = 300f;
    /// <summary>Degrees covered, centred straight ahead; 360 for the full ring.</summary>
    public float Span { get; set; } = 360f;
    /// <summary>Bearing ticks round the ring (4: ahead, right, behind, left); 0 for none.</summary>
    public int Ticks { get; set; } = 4;
    /// <summary>Draws the ring itself as a faint line. Default false.</summary>
    public bool Ring { get; set; }
    /// <summary>Degrees of the short arcs either side of awake and hunting contacts; 0 for none. Default 12.</summary>
    public float Tail { get; set; } = 12f;
    /// <summary>Contacts inside the camera's horizontal view are not drawn (an enemy outline shows them). Default false.</summary>
    public bool HideInView { get; set; }
    public Color HuntingColour { get; set; } = UIKitColors.Faded(UIKitColors.Red, 0.95f);
    public Color AwakeColour { get; set; } = UIKitColors.Faded(UIKitColors.Amber, 0.8f);
    public Color SleepColour { get; set; } = UIKitColors.Faded(UIKitColors.Teal, 0.45f);
    public Color TickColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.16f);

    /// <summary>The "turn around" hint's picture (an arrow); null with an empty <see cref="RearText"/> shows no hint.</summary>
    public Sprite? RearIcon { get; set; }
    /// <summary>The hint's text; "{0}" is replaced by the number of hunting contacts behind (for example "BEHIND ×{0}").</summary>
    public UIText RearText { get; set; } = UIText.Empty;
    /// <summary>The hint shows from this many hunting contacts behind; default 2.</summary>
    public int RearMin { get; set; } = 2;
    /// <summary>Degrees either side of straight behind that count as behind; default 40.</summary>
    public float RearArc { get; set; } = 40f;
    /// <summary>How far below the ring's centre the hint sits; default 332.</summary>
    public float RearOffset { get; set; } = 332f;

    /// <summary>Also draws the compass points (<see cref="UIKit.Compass"/>: the locator beacon, terminal PING results, teammates,
    /// and the world points mods placed with <see cref="Waypoint.Bearing"/>) just outside the ring, in and out of view alike: each kind its
    /// own line glyph on a dark plate, a teammate in that player's colour, a mod's point in its own colour, the beacons in
    /// <see cref="BeaconColour"/>, the distance under every point but a teammate. Default false.</summary>
    public bool Compass { get; set; }
    /// <summary>Whether <see cref="Compass"/> includes the teammates' arrows; false leaves the beacons and mod points only. Default true.</summary>
    public bool CompassTeammates { get; set; } = true;
    /// <summary>How far outside the ring the compass points sit; default 30.</summary>
    public float CompassOffset { get; set; } = 30f;
    /// <summary>The locator beacon's and a terminal PING result's colour.</summary>
    public Color BeaconColour { get; set; } = UIKitColors.Faded(UIKitColors.Orange, 0.95f);

    /// <summary>Starts a new set of contacts.</summary>
    public void Begin() => _contacts.Begin();

    /// <summary>Fills the contacts itself from ForgeCore's living enemies (<c>Enemies.Live</c>): every awake or hunting enemy and every sleeping
    /// one within <paramref name="radius"/> metres of the camera, rescanned every <paramref name="interval"/> seconds; strength is nearness
    /// (1 at the camera, 0 at the radius). Colours stay the widget's own; the library holds no enemy table. The next scan replaces what
    /// <see cref="Add"/> put in; <see cref="StopAuto"/> ends it.</summary>
    public void AutoEnemies(float radius, float interval = 0.25f)
    {
        _autoRadius = Math.Max(1f, radius);
        _autoEvery = Math.Max(0.05f, interval);
        _autoAt = 0f;
    }

    /// <summary>Ends <see cref="AutoEnemies"/>; the contacts stay as they were until <see cref="Begin"/>.</summary>
    public void StopAuto() => _autoRadius = 0f;

    private void ScanEnemies(float now)
    {
        var camera = HudScreen.WorldCamera();
        if (camera == null) return;
        _autoAt = now + _autoEvery;
        _contacts.Begin();
        var origin = camera.transform.position;
        var limit = _autoRadius * _autoRadius;
        var live = ForgeCore.Enemies.Live;
        for (var i = 0; i < live.Length; i++)
        {
            var enemy = live[i];
            if (enemy == null) continue;
            var state = ForgeCore.Enemies.Alert(enemy);
            if (state == EnemyAlert.Off) continue;
            var position = enemy.Position;
            var distance = (position - origin).sqrMagnitude;
            if (distance > limit) continue;
            var azimuth = UIKit.Bearing(position);
            if (float.IsNaN(azimuth)) continue;
            if (!_contacts.Add(azimuth, 1f - MathF.Sqrt(distance) / _autoRadius, state)) break;
        }
    }

    /// <summary>Adds a contact at <paramref name="azimuth"/> degrees (0 straight ahead, positive to the right) with
    /// <paramref name="strength"/> 0..1 (nearness); false when full.</summary>
    public bool Add(float azimuth, float strength, EnemyAlert state) => _contacts.Add(azimuth, strength, state);

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        if (_autoRadius > 0f && now >= _autoAt) ScanEnemies(now);
        var u = _canvas.Unit;
        var radius = Radius * u;
        var span = Math.Clamp(Span, 10f, 360f);
        var ticks = Math.Max(0, Ticks);
        for (var i = 0; i < ticks; i++)
        {
            var azimuth = span >= 360f ? i * 360f / ticks : -span / 2f + i * span / Math.Max(1, ticks - 1);
            var (x0, y0, _) = Bearing.ArcPoint(azimuth, radius - 5f * u, span);
            var (x1, y1, _) = Bearing.ArcPoint(azimuth, radius + 5f * u, span);
            _canvas.Line(x0, y0, x1, y1, 1.4f * u, TickColour);
        }
        if (Ring)
        {
            const int segments = 72;
            for (var i = 0; i < segments; i++)
            {
                var (x0, y0, _) = Bearing.ArcPoint(-span / 2f + span * i / segments, radius, span);
                var (x1, y1, _) = Bearing.ArcPoint(-span / 2f + span * (i + 1) / segments, radius, span);
                _canvas.Line(x0, y0, x1, y1, u, UIKitColors.Faded(TickColour, 0.6f));
            }
        }
        var halfFov = HideInView ? ViewHalfAngle() : -1f;
        for (var i = 0; i < _contacts.Count; i++)
        {
            var azimuth = _contacts.Azimuth(i);
            if (halfFov > 0f && Bearing.InView(azimuth, halfFov)) continue;
            var strength = _contacts.Strength(i);
            var state = _contacts.State(i);
            var colour = state switch
            {
                EnemyAlert.Hunting => HuntingColour,
                EnemyAlert.Awake => AwakeColour,
                _ => SleepColour
            };
            var (x, y, inside) = Bearing.ArcPoint(azimuth, radius, span);
            if (!inside) colour = UIKitColors.Faded(colour, 0.6f);
            var size = (4f + 4f * strength) * u;
            _canvas.Sprite(UIKitSprites.Pixel, x, y, size, size, colour, 45f + azimuth);
            if (Tail > 0f && state >= EnemyAlert.Awake)
            {
                var tail = UIKitColors.Faded(colour, 0.5f);
                for (var side = -1; side <= 1; side += 2)
                {
                    var (ax, ay, _) = Bearing.ArcPoint(azimuth + side * 2.5f, radius, span);
                    var (bx, by, _) = Bearing.ArcPoint(azimuth + side * (2.5f + Tail / 2f), radius, span);
                    var (cx, cy, _) = Bearing.ArcPoint(azimuth + side * (2.5f + Tail), radius, span);
                    _canvas.Line(ax, ay, bx, by, 1.4f * u, tail);
                    _canvas.Line(bx, by, cx, cy, 1.4f * u, UIKitColors.Faded(tail, 0.4f));
                }
            }
        }
        if (Compass) DrawCompass(radius + CompassOffset * u, span, u);
        var template = Show(RearText);
        if (RearIcon != null || template.Length > 0)
        {
            var behind = _contacts.Behind(EnemyAlert.Hunting, RearArc);
            if (behind >= Math.Max(1, RearMin))
            {
                var hy = -RearOffset * u;
                var red = UIKitColors.Faded(UIKitColors.Red, 0.95f);
                if (RearIcon != null) _canvas.Icon(RearIcon, 0f, hy, 40f * u, 40f * u, red);
                if (template.Length > 0)
                {
                    if (!ReferenceEquals(template, _rearSource) || behind != _rearCount)
                    {
                        _rearSource = template;
                        _rearCount = behind;
                        _rearShown = template.Replace("{0}", NumberText.Count(behind));
                    }
                    // Beside the arrow on the same row, so the hint stays clear of the subtitles 58 px below.
                    if (RearIcon != null) _canvas.Text(_rearShown, 28f * u, hy, 14f, red, HudAlign.Left, true);
                    else _canvas.Text(_rearShown, 0f, hy, 14f, red, HudAlign.Centre, true);
                }
            }
        }
        _canvas.End();
    }

    private void DrawCompass(float radius, float span, float u)
    {
        var compass = UIKit.Compass;
        var plate = UIKitColors.Faded(UIKitColors.Plate, 0.55f);
        var plain = UIKitColors.Faded(UIKitColors.Cockpit, 0.9f);
        for (var i = 0; i < compass.Count; i++)
        {
            var point = compass.Get(i);
            if (!float.IsFinite(point.Azimuth) || !CompassPoints.Shown(point.Kind, CompassTeammates)) continue;
            var (x, y, inside) = Bearing.ArcPoint(point.Azimuth, radius, span);
            var colour = point.PlayerColour || point.Mod ? new Color(point.R, point.G, point.B, 0.95f)
                : point.Kind is CompassPointKind.Locator or CompassPointKind.TerminalPing ? BeaconColour : plain;
            if (!inside) colour = UIKitColors.Faded(colour, 0.6f);
            var teammate = point.Kind == CompassPointKind.Teammate;
            var half = (teammate ? 5.5f : 7f) * u;
            _canvas!.Plate(x, y, 2f * half + 7f * u, 2f * half + 7f * u, 3f * u, plate);
            var glyph = CompassPoints.Glyph(point.Kind);
            for (var s = 0; s + 3 < glyph.Length; s += 4)
                _canvas.Line(x + glyph[s] * half, y + glyph[s + 1] * half, x + glyph[s + 2] * half, y + glyph[s + 3] * half, 1.6f * u, colour);
            var below = y - half - 5f * u;
            if (!teammate)
                _canvas.Text(NumberText.Meters((int)MathF.Round(point.Distance)), x, below - 7f * u, 11f, UIKitColors.Faded(colour, 0.85f), HudAlign.Centre);
        }
    }

    /// <summary>Half the world camera's horizontal view in degrees; 0 while there is no camera.</summary>
    private static float ViewHalfAngle()
    {
        var camera = HudScreen.WorldCamera();
        if (camera == null) return 0f;
        return Bearing.HalfHorizontalFov(camera.fieldOfView, camera.aspect);
    }
}

/// <summary>
/// The hull status disc: pictures of the same size drawn on top of each other (the front, back, left and right of a vehicle as ring
/// segments), each coloured by its own damage (ice white below a third, amber below two thirds, red above), flashing red when hit,
/// round a centre picture (a model mark) with its name under it, inside a ring of ticks with a caption above. Sizes are 1080p pixels.
/// Make it with <see cref="UIKitMod.AddSilhouette"/>.
/// </summary>
public sealed class HudSilhouette : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private readonly Sprite?[] _layers;
    private readonly float[] _damage, _flashAt;
    private readonly DamageShares _shares;
    private HudCanvas? _canvas;

    internal HudSilhouette(IReadOnlyList<Sprite?> layers)
    {
        _layers = new Sprite?[layers.Count];
        for (var i = 0; i < layers.Count; i++) _layers[i] = layers[i];
        _damage = new float[_layers.Length];
        _shares = new DamageShares(_layers.Length);
        _flashAt = new float[_layers.Length];
        for (var i = 0; i < _flashAt.Length; i++) _flashAt[i] = -10f;
    }

    /// <summary>The disc's diameter; default 124.</summary>
    public float Size { get; set; } = 124f;

    /// <summary>A picture in the middle (a model mark), drawn 48 across; null draws none.</summary>
    public Sprite? Center { get; set; }

    /// <summary>The name under the centre picture; empty draws none.</summary>
    public UIText CenterLabel { get; set; } = UIText.Empty;

    /// <summary>72 ticks round the disc (a longer one every 45°). Default false.</summary>
    public bool TickRing { get; set; }

    /// <summary>Small text above the disc (for example "HULL"); empty draws none.</summary>
    public UIText Caption { get; set; } = UIText.Empty;

    /// <summary>Layer <paramref name="layer"/>'s damage, 0..1.</summary>
    public void Set(int layer, float damage)
    {
        if ((uint)layer < (uint)_damage.Length) _damage[layer] = Ease.Clamp01(damage);
    }

    /// <summary>A short red flash on layer <paramref name="layer"/> (it was hit).</summary>
    public void Flash(int layer)
    {
        if ((uint)layer < (uint)_flashAt.Length) _flashAt[layer] = ForgeCore.Frame.GameTime;
    }

    /// <summary>A hit of <paramref name="amount"/> on layer <paramref name="layer"/>: it flashes, and every layer shows the overall
    /// <paramref name="loss"/> (0..1) shared out by the damage each has taken since <see cref="Reset"/> (the most hit side shows all of it).</summary>
    public void Hit(int layer, float amount, float loss)
    {
        _shares.Add(layer, amount);
        Flash(layer);
        for (var k = 0; k < _damage.Length; k++) _damage[k] = _shares.Shown(k, loss);
    }

    /// <summary>A hit from <paramref name="azimuth"/> (degrees from where the player looks, <see cref="UIKit.Bearing"/>) on a disc whose
    /// four layers are front, back, left and right (<see cref="Bearing.Quadrant"/>); see <see cref="Hit(int, float, float)"/>.</summary>
    public void Hit(float azimuth, float amount, float loss) => Hit(Bearing.Quadrant(azimuth), amount, loss);

    /// <summary>Forgets the damage taken: every layer clear (a new vehicle).</summary>
    public void Reset()
    {
        _shares.Reset();
        Array.Clear(_damage, 0, _damage.Length);
    }

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var size = Size * u;
        var outer = TickRing ? size / 2f + 13f * u : size / 2f;
        var caption = Show(Caption);
        var box = 2f * outer;
        var (fx, fy) = HudFlow.Of(hud);
        var cx = ScreenMath.Span(fx, box) + box / 2f;
        var cy = ScreenMath.Span(fy, box + (caption.Length > 0 ? 16f * u : 0f)) + box / 2f;
        if (TickRing)
        {
            var faint = UIKitColors.Faded(UIKitColors.Cockpit, 0.15f);
            var strong = UIKitColors.Faded(UIKitColors.Cockpit, 0.38f);
            for (var i = 0; i < 72; i++)
            {
                var major = i % 9 == 0;
                var a = i * 5f * Mathf.Deg2Rad;
                var r0 = size / 2f + 4f * u;
                var r1 = r0 + (major ? 9f : 4f) * u;
                _canvas.Line(cx + MathF.Cos(a) * r0, cy + MathF.Sin(a) * r0, cx + MathF.Cos(a) * r1, cy + MathF.Sin(a) * r1, u, major ? strong : faint);
            }
        }
        for (var i = 0; i < _layers.Length; i++)
        {
            var picture = _layers[i];
            if (picture == null) continue;
            var colour = GaugeMath.DamageBand(_damage[i]) switch
            {
                0 => UIKitColors.Faded(UIKitColors.Cockpit, 0.55f),
                1 => UIKitColors.Faded(UIKitColors.Amber, 0.85f),
                _ => UIKitColors.Faded(UIKitColors.Red, 0.95f)
            };
            _canvas.Icon(picture, cx, cy, size, size, colour);
            var flash = now - _flashAt[i];
            if (flash < 0.4f) _canvas.Icon(picture, cx, cy, size * 1.04f, size * 1.04f, UIKitColors.Faded(UIKitColors.Red, 0.9f * (1f - flash / 0.4f)));
        }
        var label = Show(CenterLabel);
        if (Center != null) _canvas.Icon(Center, cx, cy + (label.Length > 0 ? 6f * u : 0f), 48f * u, 48f * u, UIKitColors.Faded(UIKitColors.Orange, 0.95f));
        if (label.Length > 0) _canvas.Text(label, cx, cy - 26f * u, 14f, UIKitColors.Faded(UIKitColors.Cockpit, 0.82f), HudAlign.Centre, true);
        if (caption.Length > 0) _canvas.Text(caption, cx, cy + outer + 10f * u, 14f, UIKitColors.Faded(UIKitColors.Cockpit, 0.7f), HudAlign.Centre);
        _canvas.End();
    }
}
