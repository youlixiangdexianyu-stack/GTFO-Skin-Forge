using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>How a beam is drawn; the mod that shows it owns these numbers.</summary>
public sealed class BeamStyle
{
    /// <summary>Width of the hot core (m), kept at least <see cref="CorePerMetre"/> per metre of distance from the eye so it never
    /// breaks up far away (about 1.5 pixels at 1080p for the default).</summary>
    public float CoreWidth { get; set; } = 0.03f;
    public float CorePerMetre { get; set; } = 0.0014f;
    /// <summary>Width of the soft glow around it (m).</summary>
    public float GlowWidth { get; set; } = 0.34f;
    /// <summary>How sharply the core and the glow fall off across their width.</summary>
    public float CoreSharpness { get; set; } = 1.1f;
    public float GlowSharpness { get; set; } = 3.2f;
    /// <summary>Brightness of the core and the glow (multiplied by <see cref="Beam.Opacity"/>).</summary>
    public float CoreOpacity { get; set; } = 0.9f;
    public float GlowOpacity { get; set; } = 0.45f;
    /// <summary>True: a column, brightest at its start (<see cref="Beam.Show"/>'s from), settling above head height and fading at its
    /// end. False: even along its length.</summary>
    public bool Column { get; set; } = true;
    /// <summary>Size (m) of a camera-facing flare where the beam starts or ends; 0 for none.</summary>
    public float FlareSize { get; set; }
    /// <summary>The flare sits at the beam's end (true) or its start (false).</summary>
    public bool FlareAtEnd { get; set; }
    public float FlareOpacity { get; set; } = 0.6f;
    /// <summary>How far above the flare's point it is drawn (m).</summary>
    public float FlareLift { get; set; }
}

/// <summary>
/// A beam of light between two points: a hot core inside a soft glow, both procedural (ForgeVisual's marker shader, additive), with
/// faint bands of energy climbing it, and an optional flare at one end. The caller moves it with <see cref="Show"/> and sets its
/// brightness and width; the library animates it every frame through one property block. Renderers are pooled.
/// </summary>
public sealed class Beam
{
    private static readonly int ColorId = Shader.PropertyToID("_Color"), HotId = Shader.PropertyToID("_Hot"), StateId = Shader.PropertyToID("_State"),
        ShapeId = Shader.PropertyToID("_Shape"), RevealId = Shader.PropertyToID("_Reveal");
    private static readonly MaterialPropertyBlock Block = new();

    private readonly GameObject _object;
    private readonly LineRenderer _core, _glow;
    private readonly MeshRenderer _flare;
    private Vector3 _from, _to;
    private float _born;
    // This use's own number in reports to ForgeCore.Recording: a pooled beam used again is another beam.
    private int _key;
    internal bool Live;

    internal Beam(GameObject o, LineRenderer core, LineRenderer glow, MeshRenderer flare)
    {
        _object = o;
        _core = core;
        _glow = glow;
        _flare = flare;
    }

    public BeamStyle Style { get; internal set; } = null!;
    public Color Colour { get; set; }
    public Color Hot { get; set; }
    /// <summary>Overall brightness (above 1 flashes).</summary>
    public float Opacity { get; set; } = 1f;
    /// <summary>Width multiplier of both core and glow.</summary>
    public float Width { get; set; } = 1f;
    /// <summary>Extra width multiplier of the core alone.</summary>
    public float CoreScale { get; set; } = 1f;
    /// <summary>Flare size multiplier.</summary>
    public float FlareScale { get; set; } = 1f;
    /// <summary>Extra brightness multipliers for the core, the glow and the flare alone (on top of <see cref="Opacity"/>), for a beam whose
    /// parts breathe differently.</summary>
    public float CoreLevel { get; set; } = 1f;
    public float GlowLevel { get; set; } = 1f;
    public float FlareLevel { get; set; } = 1f;
    /// <summary>Width multiplier of the glow alone (on top of <see cref="Width"/>).</summary>
    public float GlowWidthScale { get; set; } = 1f;
    public bool Visible { get; private set; }
    public bool Alive => Live && _object != null && !_object.WasCollected;

    /// <summary>Puts the beam from <paramref name="from"/> to <paramref name="to"/> and shows it.</summary>
    public void Show(Vector3 from, Vector3 to)
    {
        if (!Alive) return;
        _from = from;
        _to = to;
        _core.SetPosition(0, from);
        _core.SetPosition(1, to);
        _glow.SetPosition(0, from);
        _glow.SetPosition(1, to);
        if (!Visible) { Visible = true; _object.SetActive(true); }
        // Reported every frame it is shown, with its style as the look and the opacity as the seconds field; the observer keeps only changes.
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Beam, 0, _key, from, to, Width, Colour, seconds: Opacity, look: Style));
        Draw(Scene.Now);
    }

    public void Hide()
    {
        if (Visible && ForgeCore.Recording.Watched) ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Release, 0, _key, default, default, float.NaN, Colour));
        Visible = false;
        if (_object != null && !_object.WasCollected) _object.SetActive(false);
    }

    /// <summary>Gives the beam back to the pool.</summary>
    public void Release() => Beams.Release(this);

    internal void Begin(BeamStyle style, Color colour)
    {
        Style = style;
        Colour = colour;
        Hot = Color.Lerp(colour, Color.white, 0.65f);
        Opacity = Width = FlareScale = CoreLevel = GlowLevel = FlareLevel = GlowWidthScale = CoreScale = 1f;
        _born = Scene.Now;
        _key = Fx.NewKey();
        Visible = false;
        Live = true;
        _object.SetActive(false);
    }

    internal void Draw(float now)
    {
        if (!Visible) return;
        var t = now - _born;
        var length = (_to - _from).magnitude;
        var even = Style.Column ? 0f : 1f;
        var distance = Scene.HasEye ? DistanceToSegment(Scene.Eye, _from, _to) : 10f;
        _core.widthMultiplier = Math.Max(Style.CoreWidth, distance * Style.CorePerMetre) * Width * CoreScale;
        _glow.widthMultiplier = Style.GlowWidth * Width * GlowWidthScale;
        Block.SetFloat(RevealId, 0f);
        Block.SetColor(ColorId, Colour);
        Block.SetColor(HotId, Hot);
        Block.SetVector(StateId, new Vector4(t, 0f, 0f, Style.CoreOpacity * Opacity * CoreLevel));
        Block.SetVector(ShapeId, new Vector4(length, 0f, Style.CoreSharpness, even));
        _core.SetPropertyBlock(Block);
        Block.SetVector(StateId, new Vector4(t, 0f, 0f, Style.GlowOpacity * Opacity * GlowLevel));
        Block.SetVector(ShapeId, new Vector4(length, 0f, Style.GlowSharpness, even));
        _glow.SetPropertyBlock(Block);
        var flare = Style.FlareSize > 0f;
        if (_flare.enabled != flare) _flare.enabled = flare;
        if (!flare) return;
        var at = Style.FlareAtEnd ? _to : _from;
        var ft = _flare.transform;
        at += Vector3.up * Style.FlareLift;
        ft.position = at;
        if (Scene.HasEye)
        {
            var facing = at - Scene.Eye;
            if (facing.sqrMagnitude > 1e-4f) ft.rotation = Quaternion.LookRotation(facing);
        }
        ft.localScale = Vector3.one * (Style.FlareSize * FlareScale);
        Block.SetVector(StateId, new Vector4(t, 0f, 0f, Style.FlareOpacity * Opacity * FlareLevel));
        Block.SetVector(ShapeId, new Vector4(0f, 2f, 0f, 0f));
        _flare.SetPropertyBlock(Block);
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var k = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return (a + ab * k - p).magnitude;
    }

    internal GameObject Object => _object;
}

/// <summary>The pool of beams.</summary>
public static class Beams
{
    private static readonly PooledBodies<Beam> Pool = new();

    /// <summary>A beam in <paramref name="colour"/> drawn with <paramref name="style"/>, hidden until its first <see cref="Beam.Show"/>;
    /// null (logged once) when the library's shaders are not available.</summary>
    public static Beam? Create(Color colour, BeamStyle style)
    {
        var material = Shaders.Marker();
        if (material == null) return null;
        var beam = Pool.Reuse(Usable) ?? Make(material);
        beam.Begin(style ?? new BeamStyle(), colour);
        Pool.Add(beam);
        return beam;
    }

    private static Beam Make(Material material)
    {
        var o = new GameObject("ForgeVisual.Beam");
        Object.DontDestroyOnLoad(o);
        var flare = new GameObject("flare");
        flare.transform.SetParent(o.transform, false);
        flare.AddComponent<MeshFilter>().sharedMesh = Shaders.Quad();
        var flareRenderer = flare.AddComponent<MeshRenderer>();
        flareRenderer.sharedMaterial = material;
        flareRenderer.shadowCastingMode = ShadowCastingMode.Off;
        flareRenderer.receiveShadows = false;
        var glow = Line(o, "glow", material);
        var core = Line(o, "core", material);
        return new Beam(o, core, glow, flareRenderer);
    }

    internal static LineRenderer Line(GameObject parent, string name, Material material)
    {
        var holder = new GameObject(name);
        holder.transform.SetParent(parent.transform, false);
        var line = holder.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.numCapVertices = 0;
        line.positionCount = 2;
        return line;
    }

    internal static void Release(Beam beam)
    {
        // Hidden while still live: the body must go inactive, or the last drawn frame stays in the world.
        if (Pool.Release(beam, b => b.Hide(), Usable(beam))) beam.Live = false;
    }

    private static bool Usable(Beam beam) => beam.Object != null && !beam.Object.WasCollected;

    internal static void TickAll(float now)
    {
        for (var i = Pool.LiveCount - 1; i >= 0; i--)
        {
            var b = Pool[i];
            if (!Usable(b)) { b.Live = false; Pool.RemoveAt(i); continue; }
            b.Draw(now);
        }
    }

    internal static void ClearAll()
    {
        for (var i = Pool.LiveCount - 1; i >= 0; i--) Release(Pool[i]);
    }
}
