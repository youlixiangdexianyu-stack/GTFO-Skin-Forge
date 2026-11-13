using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>The base looks of a ground marker (all procedural, additive, in the marker's colours).</summary>
public enum GroundMarkerStyle
{
    /// <summary>A crisp rim whose lit arc is the time left (<see cref="GroundMarker.Remaining"/>), 24 ticks, four turning brackets, a
    /// dashed inner ring, a small centre dot and a thin ring pinging out past the rim, all thin lines with no fill (so bloom never turns it
    /// into a disc); tightens with <see cref="GroundMarker.Urgency"/>.</summary>
    Countdown,
    /// <summary>A hazard band of slanted stripes creeping round the rim and a faint drifting fill.</summary>
    Hatched,
    /// <summary>A rim whose glow rises outward in flickering tongues, with a heat wash.</summary>
    Flames,
    /// <summary>A crisp rim, a slowly turning dial of 60 ticks and echo rings sinking inward.</summary>
    Dial,
    /// <summary>Three arcs spinning round a burning point and four inward ticks: a small aim point.</summary>
    Reticle,
}

/// <summary>
/// A mark laid on the floor: a disc dropped sample by sample onto the floor below it, so it follows floors, slopes and stairs, hangs down
/// steps, stops at walls (carrying on behind them only as a faint dashed rim drawn where something hides it, and only while the local eye
/// is near) and never spills into the next room. Colours, radius and timing are the caller's; the library animates time, the ping, the
/// draw-in sweep and the hidden rim every frame through one property block. Layers and renderers are pooled; placing one after the
/// first few allocates nothing.
/// </summary>
public sealed class GroundMarker
{
    private static readonly int ColorId = Shader.PropertyToID("_Color"), HotId = Shader.PropertyToID("_Hot"), StateId = Shader.PropertyToID("_State"),
        ShapeId = Shader.PropertyToID("_Shape"), StyleId = Shader.PropertyToID("_Style"), RevealId = Shader.PropertyToID("_Reveal");
    private static readonly List<GroundMarker> Live = new();
    private static readonly List<GroundLay> Layers = new();
    private static readonly Stack<GroundMarker> Free = new();
    private static readonly MaterialPropertyBlock Block = new();
    /// <summary>A moving marker is laid again once it has moved this far (m), at most this often (s).</summary>
    private const float RelayMetres = 0.15f, RelaySeconds = 0.05f;

    private readonly GameObject _object;
    private readonly MeshFilter _filter;
    private readonly MeshRenderer _renderer;
    private GroundLay _lay = null!;
    private Vector3 _laidAt;
    private float _born, _laidTime, _last, _ping, _blinkAt = float.NegativeInfinity, _reach;
    private bool _live;
    // A strip: its direction, half length (0 for a disc) and whether chevrons walk along it.
    private Vector3 _forward;
    private float _halfLength;
    private bool _chevrons;

    private GroundMarker(GameObject o, MeshFilter f, MeshRenderer r)
    {
        _object = o;
        _filter = f;
        _renderer = r;
    }

    public GroundMarkerStyle Style { get; private set; }
    public float Radius { get; private set; }
    /// <summary>The accent colour (rim, ticks, stripes).</summary>
    public Color Colour { get; set; }
    /// <summary>The core colour (hot points, the lit rim's core).</summary>
    public Color Hot { get; set; }
    /// <summary>Overall brightness (0..; above 1 flashes).</summary>
    public float Opacity { get; set; } = 1f;
    /// <summary>A countdown's time left, 1 at the start to 0 at the end.</summary>
    public float Remaining { get; set; } = 1f;
    /// <summary>A countdown's urgency, 0 calm to 1 at the end: brackets and inner ring close in, the accent warms.</summary>
    public float Urgency { get; set; }
    /// <summary>Pings per second (a countdown's wave running out past the rim); 0 for none.</summary>
    public float PingRate { get; set; }
    /// <summary>An area mark sweeps on clockwise over this long when placed (0: at once).</summary>
    public float SweepSeconds { get; set; }
    public bool Alive => _live && _object != null && !_object.WasCollected;
    public Vector3 Centre => _laidAt;

    /// <summary>Lays a marker of <paramref name="style"/> and <paramref name="radius"/> metres around <paramref name="centre"/>, reaching
    /// <paramref name="reach"/> radii (NaN: the style's own). Null (logged once) when the library's shaders are not available.</summary>
    public static GroundMarker? Place(Vector3 centre, float radius, Color colour, GroundMarkerStyle style, float reach = float.NaN)
        => Lay(centre, radius, 0f, Vector3.forward, false, colour, style, reach);

    /// <summary>Lays a strip-shaped marker of <paramref name="style"/> (its rim the rectangle <paramref name="halfWidth"/> metres to each side of
    /// <paramref name="centre"/> and <paramref name="halfLength"/> along <paramref name="forward"/>, drawn the way a disc's rim is), following the floor and
    /// stopping at walls as a disc does; <paramref name="chevrons"/>: chevrons walk along it toward <paramref name="forward"/> (a strike that moves).
    /// <see cref="Radius"/> is its half width. Null (logged once) when the library's shaders are not available.</summary>
    public static GroundMarker? PlaceStrip(Vector3 centre, Vector3 forward, float halfWidth, float halfLength, Color colour, GroundMarkerStyle style, bool chevrons = false)
        => Lay(centre, halfWidth, Math.Max(0.05f, halfLength), forward, chevrons, colour, style, float.NaN);

    private static GroundMarker? Lay(Vector3 centre, float radius, float halfLength, Vector3 forward, bool chevrons, Color colour, GroundMarkerStyle style, float reach)
    {
        var material = Shaders.Marker();
        if (material == null) return null;
        var marker = Rent(material);
        radius = Math.Max(0.05f, radius);
        marker._halfLength = halfLength;
        marker._forward = forward;
        marker._chevrons = chevrons;
        var countdown = style == GroundMarkerStyle.Countdown;
        marker._reach = float.IsNaN(reach) ? (countdown ? 1.32f : 1.26f) : Math.Max(1f, reach);
        marker.Style = style;
        marker.Radius = radius;
        marker.Colour = colour;
        marker.Hot = Color.Lerp(colour, Color.white, countdown ? 0.65f : 0.5f);
        marker.Opacity = 1f;
        marker.Remaining = 1f;
        marker.Urgency = 0f;
        marker.PingRate = 0f;
        marker.SweepSeconds = countdown || style == GroundMarkerStyle.Reticle ? 0f : 0.6f;
        marker._born = marker._last = Scene.Now;
        marker._ping = 0f;
        marker._blinkAt = float.NegativeInfinity;
        marker._lay = Layer(style, Math.Max(radius, halfLength) * marker._reach, halfLength > 0f);
        marker._lay.InUse = true;
        marker._filter.sharedMesh = marker._lay.Mesh;
        marker.Relay(centre, Scene.Now);
        marker._live = true;
        marker._object.SetActive(true);
        Live.Add(marker);
        marker.Draw(Scene.Now);
        return marker;
    }

    /// <summary>A countdown's tick: the rim and fill flash and fade.</summary>
    public void Blink() => _blinkAt = Scene.Now;

    /// <summary>Follows a moving point (a beam's touch): the disc slides with it at the height it was laid, and is laid again on the
    /// floor once it has moved <see cref="RelayMetres"/>, at most every <see cref="RelaySeconds"/>.</summary>
    public void Move(Vector3 centre)
    {
        if (!Alive) return;
        var now = Scene.Now;
        if ((centre - _laidAt).sqrMagnitude > RelayMetres * RelayMetres && now - _laidTime >= RelaySeconds) Relay(centre, now);
        else _object.transform.position = new Vector3(centre.x, _laidAt.y, centre.z);
    }

    /// <summary>Takes the marker away (it returns to the pool).</summary>
    public void Remove()
    {
        if (!_live) return;
        _live = false;
        _lay.InUse = false;
        Live.Remove(this);
        if (_object != null && !_object.WasCollected)
        {
            _object.SetActive(false);
            Free.Push(this);
        }
    }

    private void Relay(Vector3 centre, float now)
    {
        // A strip reaches as far past its rim as a disc of its half width does past its own.
        if (_halfLength > 0f) _lay.LayStrip(centre, _forward, Radius, _halfLength, Radius * (_reach - 1f));
        else _lay.Lay(centre, Radius, Radius * _reach);
        _laidAt = centre;
        _laidTime = now;
        _object.transform.position = centre;
    }

    private void Draw(float now)
    {
        var t = now - _born;
        _ping = MarkerMath.Advance(_ping, PingRate, Math.Min(now - _last, 0.1f));
        _last = now;
        var reveal = 0f;
        if (Scene.HasEye)
        {
            var at = _object.transform.position;
            var eye = Scene.Eye;
            reveal = MarkerMath.Reveal(new Vector2(eye.x - at.x, eye.z - at.z).magnitude, eye.y - at.y, Math.Max(Radius, _halfLength) * _reach);
        }
        Block.SetFloat(RevealId, reveal);
        Block.SetColor(ColorId, Colour);
        Block.SetColor(HotId, Hot);
        Block.SetVector(StateId, new Vector4(t, _ping, Urgency, Opacity));
        Block.SetVector(ShapeId, new Vector4(Radius, 1f, ForgeUI.Ease.Clamp01(Remaining), MarkerMath.Blink(now - _blinkAt)));
        Block.SetVector(StyleId, new Vector4((float)Style, MarkerMath.Sweep(t, SweepSeconds), _halfLength / Radius, _chevrons ? 1f : 0f));
        _renderer.SetPropertyBlock(Block);
    }

    private static GroundMarker Rent(Material material)
    {
        while (Free.Count > 0)
        {
            var m = Free.Pop();
            if (m._object != null && !m._object.WasCollected) return m;
        }
        var o = new GameObject("ForgeVisual.GroundMarker");
        Object.DontDestroyOnLoad(o);
        var f = o.AddComponent<MeshFilter>();
        var r = o.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return new GroundMarker(o, f, r);
    }

    /// <summary>A free floor layer of the style's resolution for a mark reaching <paramref name="extent"/> metres: the countdown is fine
    /// (64 spokes, a ring every 0.2 m), areas coarser (48 spokes, every 0.4 m), the reticle small (32 spokes, 3 rings), a strip 96 spokes
    /// round its longer outline and a ring every 0.6 m up to 20.</summary>
    private static GroundLay Layer(GroundMarkerStyle style, float extent, bool strip)
    {
        int spokes, rings;
        switch (style)
        {
            case var _ when strip: spokes = 96; rings = Math.Clamp((int)Math.Ceiling(extent / 0.6f), 5, 20); break;
            case GroundMarkerStyle.Countdown: spokes = 64; rings = Math.Clamp((int)Math.Ceiling(extent / 0.2f), 6, 24); break;
            case GroundMarkerStyle.Reticle: spokes = 32; rings = 3; break;
            default: spokes = 48; rings = Math.Clamp((int)Math.Ceiling(extent / 0.4f), 5, 16); break;
        }
        foreach (var lay in Layers)
            if (!lay.InUse && lay.Spokes == spokes && lay.Rings == rings) return lay;
        var made = new GroundLay("ForgeVisual ground mark " + spokes + "x" + rings, spokes, rings);
        Layers.Add(made);
        return made;
    }

    internal static void TickAll(float now)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            var m = Live[i];
            if (m._object == null || m._object.WasCollected) { m._live = false; m._lay.InUse = false; Live.RemoveAt(i); continue; }
            m.Draw(now);
        }
    }

    internal static void ClearAll()
    {
        for (var i = Live.Count - 1; i >= 0; i--) Live[i].Remove();
    }
}
