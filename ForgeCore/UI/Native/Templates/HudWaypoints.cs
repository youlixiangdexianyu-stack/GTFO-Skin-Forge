using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>One world point of a <see cref="HudWaypoints"/>. Unset optional fields take the group's style.</summary>
public struct Waypoint
{
    public Waypoint(Vector3 position, UIText label, CompassPointKind kind = CompassPointKind.Here)
    {
        Position = position;
        Label = label;
        Kind = kind;
        Follow = null;
        Icon = null;
        Colour = null;
        Lift = 0f;
        Bar = null;
        EdgeArrow = null;
        Bearing = null;
    }

    /// <summary>The place in the world, used while <see cref="Follow"/> is null.</summary>
    public Vector3 Position;
    /// <summary>Tracks this object instead of <see cref="Position"/>; the point is not drawn once the object is destroyed.</summary>
    public Transform? Follow;
    /// <summary>Text above the mark; empty draws none.</summary>
    public UIText Label;
    /// <summary>What the point is: its line glyph on screen when it has no <see cref="Icon"/> (<see cref="CompassPointKind.Here"/> is the
    /// plain diamond) and its mark on a bearing ring (<see cref="HudBearingArc.Compass"/>).</summary>
    public CompassPointKind Kind;
    /// <summary>A picture drawn instead of the glyph (for example a model mark); null keeps the glyph.</summary>
    public Sprite? Icon;
    /// <summary>The point's colour; null takes <see cref="HudWaypoints.Colour"/>.</summary>
    public Color? Colour;
    /// <summary>Metres above the place (an over-the-head mark).</summary>
    public float Lift;
    /// <summary>The picture stands left of the label on one line instead of the label standing above it (a name plate: badge, then name).</summary>
    public bool Row;
    /// <summary>A small bar under the label, 0..1 (for example health); null draws none.</summary>
    public float? Bar;
    /// <summary>An arrow on the screen's edge while the point is off screen; null takes <see cref="HudWaypoints.EdgeArrow"/>.</summary>
    public bool? EdgeArrow;
    /// <summary>Also on every bearing ring that shows the compass (<see cref="UIKit.Compass"/>); null takes <see cref="HudWaypoints.Bearing"/>.</summary>
    public bool? Bearing;
}

/// <summary>
/// World points a mod places itself: a mark (a diamond, the line glyph of its <see cref="CompassPointKind"/>, or a picture), a label above,
/// an optional small bar and the distance below while the place is on screen; an arrow on the screen's edge pointing the way while it is
/// not (to the side, above, below or behind you), when the point asks for one; and, when it asks to be on the bearing ring, the same point
/// on every bearing ring that shows the compass (<see cref="UIKit.Compass"/>, next to the beacons and teammates). A fixed pool of
/// <see cref="Capacity"/> points: <see cref="Add"/> past it drops the point added longest ago (logged), ids of gone points find nothing, and
/// nothing is allocated after it is made. Points are projected with the player's camera every frame, so a teleport, a checkpoint or a
/// change of camera follows at once. Marks are sized in HUD units (not scaled by <see cref="HudCanvas.Unit"/>), texts in 1080p pixels. Make it with <see cref="UIKitMod.AddWaypoints"/>.
/// </summary>
public sealed class HudWaypoints : HudWidget
{
    private const float MarkSize = 11f, LabelPx = 15f, DistancePx = 14f, ArrowSize = 22f;
    private static readonly List<HudWaypoints> All = new();

    private readonly WaypointBook _book;
    private readonly Waypoint[] _points;
    private HudCanvas? _canvas;
    private int _tickFrame = -10;

    internal HudWaypoints(int capacity)
    {
        _book = new WaypointBook(capacity);
        _points = new Waypoint[_book.Capacity];
        All.Add(this);
    }

    /// <summary>How many points it holds at most.</summary>
    public int Capacity => _book.Capacity;

    /// <summary>Points held now.</summary>
    public int Count => _book.Count;

    /// <summary>The distance under every point on screen and beside every edge arrow. Default true.</summary>
    public bool ShowDistance { get; set; } = true;

    /// <summary>The edge arrow for points that do not say. Default true.</summary>
    public bool EdgeArrow { get; set; } = true;

    /// <summary>The bearing ring for points that do not say. Default false.</summary>
    public bool Bearing { get; set; }

    /// <summary>The whole mark stands above its place: its lowest edge (the bar or distance under the mark) sits on it instead of the mark's
    /// middle, so a name plate over a head keeps a set gap at any distance. Default false.</summary>
    public bool Above { get; set; }

    /// <summary>The colour of points that do not give one.</summary>
    public Color Colour { get; set; } = UIKitColors.Accent;

    public Color BarColour { get; set; } = UIKitColors.Bright;

    /// <summary>Metres: beyond <see cref="FadeStart"/> points fade, and past <see cref="FadeEnd"/> they are not drawn; 0 (the default) for
    /// <see cref="FadeEnd"/> turns fading off.</summary>
    public float FadeStart { get; set; }

    public float FadeEnd { get; set; }

    /// <summary>Places a point. When all <see cref="Capacity"/> are in use the point added longest ago is dropped for it (logged once). Returns its id.</summary>
    public WaypointId Add(in Waypoint point)
    {
        var id = _book.Add(out _);
        _points[id.Slot] = point;
        return id;
    }

    /// <summary>Changes a point in place; false when it is gone.</summary>
    public bool Set(WaypointId id, in Waypoint point)
    {
        var slot = _book.SlotOf(id);
        if (slot < 0) return false;
        _points[slot] = point;
        return true;
    }

    /// <summary>Moves a point; false when it is gone.</summary>
    public bool Move(WaypointId id, Vector3 position)
    {
        var slot = _book.SlotOf(id);
        if (slot < 0) return false;
        _points[slot].Position = position;
        return true;
    }

    /// <summary>Whether the point is still held (not removed, not dropped for a newer one).</summary>
    public bool Contains(WaypointId id) => _book.Contains(id);

    /// <summary>Takes a point off; false when it was already gone.</summary>
    public bool Remove(WaypointId id)
    {
        var slot = _book.SlotOf(id);
        if (slot < 0) return false;
        _points[slot] = default;
        return _book.Remove(id);
    }

    /// <summary>Takes every point off (for a list filled again every frame: <see cref="Clear"/>, then <see cref="Add"/> each).</summary>
    public void Clear()
    {
        _book.Clear();
        Array.Clear(_points);
    }

    internal override void Build(HudElement hud)
    {
        // Drawn once: over the game world a soft glow copy of a label reads as a blurred second name.
        hud.Effects = false;
        _canvas = new HudCanvas(hud);
    }

    /// <summary>Where the point is now; false when its followed object is gone.</summary>
    private static bool Locate(in Waypoint point, out Vector3 at)
    {
        at = point.Position;
        var follow = point.Follow;
        if (follow != null)
        {
            // A destroyed object keeps its managed wrapper: ForgeCore's liveness check reads the native pointer.
            if (ForgeCore.NativeObjects.Gone(follow)) return false;
            at = ForgeCore.EngineCalls.Position(follow);
        }
        at.y += point.Lift;
        return true;
    }

    /// <summary>How far a mark reaches below its middle (HUD units), for <see cref="Above"/>.</summary>
    private static float Reach(in Waypoint point, bool distance)
    {
        var bar = point.Bar.HasValue;
        if (point.Row && point.Icon != null) return MarkSize * 1.2f + (bar ? 12f : 0f) + (distance ? DistancePx : 0f);
        if (distance) return MarkSize * 2.3f + (bar ? 10f : 0f) + DistancePx / 2f;
        if (bar) return MarkSize * 2.3f + 2f;
        return point.Icon != null ? MarkSize * 1.2f : MarkSize;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        _tickFrame = Time.frameCount;
        if (_book.Count == 0 || !WorldView.Read(out var clip, out var eye) || hud.HalfWidth <= 0f) { _canvas.End(); return; }
        // Marks are sized in HUD units (as the world markers they replace were); texts in 1080p pixels.
        var inset = Plugin.HudSafeMargin + ArrowSize;
        for (var slot = 0; slot < _points.Length; slot++)
        {
            if (!_book.Used(slot)) continue;
            ref readonly var point = ref _points[slot];
            if (!Locate(point, out var at)) continue;
            float dx = at.x - eye.x, dy = at.y - eye.y, dz = at.z - eye.z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            var place = WaypointMath.Place(clip, at.x, at.y, at.z, hud.HalfWidth, hud.HalfHeight, inset, distance, FadeStart, FadeEnd,
                point.EdgeArrow ?? EdgeArrow, out var x, out var y, out var angle, out var fade);
            if (place == WaypointPlace.Hidden) continue;
            var colour = UIKitColors.Faded(point.Colour ?? Colour, fade);
            var meters = ShowDistance ? NumberText.Meters((int)MathF.Round(distance)) : "";
            if (place == WaypointPlace.Edge)
            {
                _canvas.Sprite(UIKitSprites.ArrowGlyph(ArrowDirection.Up), x, y, ArrowSize, ArrowSize, colour, angle - 90f);
                if (meters.Length > 0)
                {
                    // The distance sits on the inner side of the arrow.
                    var inward = -ArrowSize * 1.1f;
                    var rad = angle * 0.017453292f;
                    _canvas.Text(meters, x + MathF.Cos(rad) * inward, y + MathF.Sin(rad) * inward, DistancePx, UIKitColors.Faded(UIKitColors.Text, fade), HudAlign.Centre);
                }
                continue;
            }
            if (Above) y += Reach(point, meters.Length > 0);
            const float mark = MarkSize;
            var line = point.Row && point.Icon != null ? Show(point.Label) : "";
            if (line.Length > 0)
            {
                // One line, centred on the place: the picture, then the label to its right.
                const float gap = 6f;
                var picture = mark * 2.4f;
                var left = x - (picture + gap + _canvas.TextWidth(line, LabelPx)) / 2f;
                _canvas.Icon(point.Icon!, left + picture / 2f, y, picture, picture, colour);
                _canvas.Text(line, left + picture + gap, y, LabelPx, UIKitColors.Faded(UIKitColors.Bright, fade), HudAlign.Left);
            }
            else if (point.Icon != null) _canvas.Icon(point.Icon, x, y, mark * 2.4f, mark * 2.4f, colour);
            else if (point.Kind == CompassPointKind.Here)
            {
                _canvas.Sprite(UIKitSprites.Pixel, x, y, mark, mark, colour, 45f);
                _canvas.Sprite(UIKitSprites.Outline, x, y, mark * 2f, mark * 2f, UIKitColors.Faded(colour, 0.6f), 45f);
            }
            else
            {
                _canvas.Plate(x, y, mark * 2.2f, mark * 2.2f, 3f, UIKitColors.Faded(UIKitColors.Plate, 0.55f * fade));
                var glyph = CompassPoints.Glyph(point.Kind);
                for (var s = 0; s + 3 < glyph.Length; s += 4)
                    _canvas.Line(x + glyph[s] * mark * 0.8f, y + glyph[s + 1] * mark * 0.8f, x + glyph[s + 2] * mark * 0.8f, y + glyph[s + 3] * mark * 0.8f, 1.6f, colour);
            }
            var label = line.Length > 0 ? "" : Show(point.Label);
            if (label.Length > 0) _canvas.Text(label, x, y + mark * 2.3f, LabelPx, UIKitColors.Faded(UIKitColors.Bright, fade), HudAlign.Centre);
            var below = y - (line.Length > 0 ? mark * 1.4f : mark * 2.3f);
            if (point.Bar is { } value)
            {
                value = Ease.Clamp01(value);
                const float width = 44f, height = 4f;
                _canvas.Rect(x, below, width, height, UIKitColors.Faded(UIKitColors.Shade, fade));
                if (value > 0f) _canvas.Rect(x - width / 2f + width * value / 2f, below, width * value, height, UIKitColors.Faded(BarColour, fade));
                below -= 10f;
            }
            if (meters.Length > 0) _canvas.Text(meters, x, below, DistancePx, UIKitColors.Faded(UIKitColors.Text, fade), HudAlign.Centre);
        }
        _canvas.End();
    }

    /// <summary>Every point that asks to be on the bearing ring, from every group drawn this frame or the last (removed, hidden or
    /// not-yet-built groups are left out), into the compass reading; false once the reading is full.</summary>
    internal static bool AddToCompass(CompassReading reading)
    {
        var frame = Time.frameCount;
        for (var g = 0; g < All.Count; g++)
        {
            var group = All[g];
            if (frame - group._tickFrame > 1) continue;
            for (var slot = 0; slot < group._points.Length; slot++)
            {
                if (!group._book.Used(slot)) continue;
                ref readonly var point = ref group._points[slot];
                if (!(point.Bearing ?? group.Bearing) || !Locate(point, out var at)) continue;
                var c = point.Colour ?? group.Colour;
                if (!reading.Add(at.x, at.y, at.z, point.Kind, -1, false, c.r, c.g, c.b, mod: true)) return false;
            }
        }
        return true;
    }
}
