using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

public static partial class UIKit
{
    private static readonly Dictionary<string, Shader?> Shaders = new(StringComparer.Ordinal);

    /// <summary>Loads shader <paramref name="asset"/> from an asset bundle you ship (for example embedded in your plugin and built with the
    /// game's Unity), once per <paramref name="id"/>: later calls return the same shader, a failure is logged once and returns null every
    /// time. Call it ahead (a prewarm), not in the frame something is first shown.</summary>
    public static Shader? LoadShader(string id, Func<byte[]> bundle, string asset)
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        if (Shaders.TryGetValue(id ?? "", out var known)) return known;
        Shader? shader = null;
        try
        {
            var loaded = ForgeCore.Assets.LoadBytes(Plugin.Id, id ?? "", bundle()) ?? throw new InvalidOperationException("the bundle cannot be opened");
            shader = ForgeCore.Assets.Get<Shader>(Plugin.Id, loaded, asset) ?? throw new InvalidOperationException("the bundle has no shader " + asset);
            if (!shader.isSupported) throw new InvalidOperationException("the shader is not supported on this device");
            shader.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception error)
        {
            shader = null;
            Plugin.Error("ui-shader-could-not-be-loaded", "Shader '" + id + "' could not be loaded: " + error);
        }
        Shaders[id ?? ""] = shader;
        return shader;
    }

}

/// <summary>Full-screen layers. Two kinds share the screen with every other mod's: an exclusive layer (<see cref="ScreenPanels"/>: a
/// power-on, anything that covers the whole picture) holds the screen alone, arbitrated by priority
/// (<see cref="ScreenArbiter"/>): a higher priority layer takes the screen and the holder is put away; an equal or lower one waits and
/// draws once the screen is free. An overlay layer (the hit edges and the visor) never waits: any
/// number draw together, over the game's picture and under an exclusive layer (overlays render at 3900, exclusive panels
/// at 4000 to 4100).</summary>
public sealed partial class UIKitMod
{
    private static readonly Registry<object> ScreenEntries = new();

    private readonly Dictionary<object, int> _warmedFor = new();

    /// <summary>Builds and compiles the next full-screen layer this mod registered (exclusive panels, overlays, hit edges, visors) that is
    /// not ready for the current screen yet: its pieces made and drawn once off screen, so the first real frame that shows it creates and
    /// compiles nothing. One layer per call, so a prewarm that calls it once a frame spreads the cost; returns the layer's ID, or null
    /// once every layer is ready (call it again after the screen size changed). A layer that cannot be built logs why and counts as done.</summary>
    /// <example><c>while (ui.WarmNextLayer() is { } id) yield return null;</c></example>
    public string? WarmNextLayer()
    {
        var prefix = Id + ModIds.Separator;
        var entries = ScreenEntries.Ordered;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var id = entry switch { ScreenPanels p => p.Id, ScreenPowerOn o => o.Id, HitEdge h => h.Id, Visor v => v.Id, _ => "" };
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (_warmedFor.TryGetValue(entry, out var generation) && generation == ScreenLayer.Generation) continue;
            _ = entry switch { ScreenPanels p => p.Warm(), ScreenPowerOn o => o.Warm(), HitEdge h => h.Warm(), Visor v => v.Warm(), _ => false };
            _warmedFor[entry] = ScreenLayer.Generation;
            return id;
        }
        return null;
    }

    internal static bool Register(string id, object entry)
    {
        var problem = ScreenEntries.Add(id, entry);
        if (problem == null)
        {
            return true;
        }
        Plugin.Error("ui-screen-layer-not-registered", "Screen layer not registered: " + problem + ".");
        return false;
    }
}
/// <summary>What a <see cref="ScreenPanels"/> overlay is made of. Coordinates are of the screen as a unit square: (0, 0) bottom left,
/// (1, 1) top right, whatever the aspect.</summary>
internal sealed class ScreenPanelsSpec
{
    /// <summary>Each panel a convex polygon, its corners in order as x0, y0, x1, y1, ...</summary>
    public IReadOnlyList<float[]> Panels { get; init; } = Array.Empty<float[]>();

    /// <summary>Each seam a straight line x0, y0, x1, y1.</summary>
    public IReadOnlyList<float[]> Seams { get; init; } = Array.Empty<float[]>();

    /// <summary>The panels' shader; it gets the panel's state vector (<see cref="StateProperty"/>) and can grab the screen. Null: panels are
    /// plain black at the alpha given to <see cref="ScreenPanels.Panel"/>.</summary>
    public Shader? Shader { get; init; }

    public string StateProperty { get; init; } = "_State";

    /// <summary>Colours set once on every panel's material (shader property, colour), for example a glitch's tint.</summary>
    public IReadOnlyList<(string Property, Color Colour)> Colours { get; init; } = Array.Empty<(string, Color)>();

    /// <summary>Seam width in pixels (at the screen's height) and the width of the soft dark outline around it.</summary>
    public float SeamPixels { get; init; } = 2.2f;
    public float OutlinePixels { get; init; } = 5f;
    public Color SeamColour { get; init; } = new(0.02f, 0.02f, 0.025f, 1f);
    public float OutlineAlpha { get; init; } = 0.45f;
}

/// <summary>A registered exclusive panel overlay (see <see cref="UIKitMod.AddPowerOn"/>). Every method only switches pieces and writes
/// material values, so it can run in any frame without creating anything once <see cref="Warm"/> ran; the pieces are rebuilt when the
/// screen size changes. While another exclusive layer holds the screen, whatever is switched on is put away again in the same call and
/// the layer waits (logged once per wait).</summary>
internal sealed class ScreenPanels
{
    private readonly ScreenPanelsSpec _spec;
    private readonly int _stateId;
    private readonly ScreenLayer.User _user = new();
    private GameObject? _fade, _seams;
    private GameObject?[] _panel = Array.Empty<GameObject?>();
    private Mesh?[] _mesh = Array.Empty<Mesh?>();
    private Material?[] _material = Array.Empty<Material?>();
    private Material? _fadeMaterial, _coreMaterial, _outlineMaterial;
    private float _fadeShown = -1f, _seamShown = -1f;
    private int _generation = -1;
    private bool _failed;
    private readonly Action _preempted;

    internal ScreenPanels(string id, ScreenPanelsSpec spec, int priority)
    {
        Id = id;
        _spec = spec;
        Priority = priority;
        _stateId = Shader.PropertyToID(spec.StateProperty);
        _preempted = () =>
        {
            Park();
            _user.Use(false);
        };
    }

    public string Id { get; }

    /// <summary>Against other exclusive layers: higher takes the screen, equal or lower waits.</summary>
    public int Priority { get; }

    public int PanelCount => _spec.Panels.Count;

    /// <summary>Builds the pieces, draws them all once off screen (so the shaders and the screen grab compile) and puts them away. Call it
    /// ahead; false when nothing can be drawn (logged).</summary>
    public bool Warm()
    {
        if (!Build()) return false;
        Active(_fade!, true);
        foreach (var p in _panel) Active(p!, true);
        Active(_seams!, true);
        ScreenLayer.RenderOnce();
        Park();
        return true;
    }

    /// <summary>Solid black at <paramref name="alpha"/> over everything (0 removes it).</summary>
    public void Fade(float alpha)
    {
        if (!Prepare()) return;
        SetAlpha(_fadeMaterial!, ref _fadeShown, alpha);
        Active(_fade!, _fadeShown > 0f);
        Changed();
    }

    /// <summary>Panel <paramref name="index"/> on or off; when on, its shader gets <paramref name="state"/> (without a shader the panel is
    /// black at <paramref name="plainAlpha"/>).</summary>
    public void Panel(int index, bool on, Vector4 state, float plainAlpha)
    {
        if (!Prepare() || index < 0 || index >= _panel.Length) return;
        if (on)
        {
            if (_spec.Shader == null) _material[index]!.color = new Color(0f, 0f, 0f, Mathf.Clamp01(plainAlpha));
            else _material[index]!.SetVector(_stateId, state);
        }
        Active(_panel[index]!, on);
        Changed();
    }

    /// <summary>The seams at <paramref name="alpha"/> (0 removes them).</summary>
    public void Seams(float alpha)
    {
        if (!Prepare()) return;
        SetAlpha(_coreMaterial!, ref _seamShown, alpha);
        _outlineMaterial!.color = new Color(0f, 0f, 0f, _spec.OutlineAlpha * Mathf.Clamp01(alpha));
        Active(_seams!, _seamShown > 0.003f);
        Changed();
    }

    /// <summary>Everything off.</summary>
    public void Hide()
    {
        Release();
        if (_generation != ScreenLayer.Generation) return;
        Park();
        _user.Use(false);
    }

    private void Park()
    {
        Active(_fade!, false);
        foreach (var p in _panel) Active(p!, false);
        Active(_seams!, false);
        _fadeShown = _seamShown = -1f;
    }

    private void Changed()
    {
        var any = _fade!.activeSelf || _seams!.activeSelf;
        foreach (var p in _panel) any |= p!.activeSelf;
        if (!any) Release();
        else if (!Claim())
        {
            Park();
            any = false;
        }
        _user.Use(any);
    }

    /// <summary>Asks for the screen; false while another exclusive layer holds it or has the better claim.</summary>
    private bool Claim()
    {
        var arbiter = ScreenLayer.Arbiter;
        switch (arbiter.Request(this, Id, Priority, ForgeCore.Frame.UnscaledTime, _preempted))
        {
            case ScreenGrant.Waiting:
                return false;
            default:
                return true;
        }
    }

    private void Release()
    {
        ScreenLayer.Arbiter.Release(this);
    }

    private bool Prepare() => Build();

    private bool Build()
    {
        if (_failed || !ScreenLayer.Ensure()) return false;
        if (_generation == ScreenLayer.Generation) return true;
        try { BuildPieces(); }
        catch (Exception error)
        {
            _failed = true;
            Plugin.Error("ui-screen-overlay-could-not-be-built", "Screen overlay '" + Id + "' could not be built and is off: " + error);
            return false;
        }
        return true;
    }

    private void BuildPieces()
    {
        Destroy();
        _user.Reset();
        var sprite = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("the game has no Sprites/Default shader");
        var black = new Color(0f, 0f, 0f, 1f);
        _fadeMaterial = ScreenLayer.Material(sprite, black, 4000);
        _fade = ScreenLayer.Piece("Fade", ScreenLayer.Quad, _fadeMaterial);
        _fade.transform.localScale = new Vector3(ScreenLayer.Aspect * 1.05f, 1.05f, 1f);
        var count = _spec.Panels.Count;
        _panel = new GameObject?[count];
        _mesh = new Mesh?[count];
        _material = new Material?[count];
        for (var p = 0; p < count; p++)
        {
            _material[p] = ScreenLayer.Material(_spec.Shader ?? sprite, black, 4000);
            if (_spec.Shader != null) foreach (var (property, colour) in _spec.Colours) _material[p]!.SetColor(property, colour);
            _mesh[p] = Polygon(_spec.Panels[p], Id + ".Panel" + p);
            _panel[p] = ScreenLayer.Piece("Panel" + p, _mesh[p]!, _material[p]!);
        }
        _coreMaterial = ScreenLayer.Material(sprite, _spec.SeamColour, 4100);
        _outlineMaterial = ScreenLayer.Material(sprite, new Color(0f, 0f, 0f, _spec.OutlineAlpha), 4099);
        _seams = ScreenLayer.Group("Seams");
        for (var i = 0; i < _spec.Seams.Count; i++)
        {
            var s = _spec.Seams[i];
            if (s == null || s.Length != 4) throw new ArgumentException("seam " + i + " needs four numbers");
            Strip(ScreenLayer.Piece("SeamOutline" + i, ScreenLayer.Quad, _outlineMaterial, _seams), s, _spec.OutlinePixels);
            Strip(ScreenLayer.Piece("SeamCore" + i, ScreenLayer.Quad, _coreMaterial, _seams), s, _spec.SeamPixels);
        }
        _generation = ScreenLayer.Generation;
        Park();
    }

    private static Mesh Polygon(float[] corners, string name)
    {
        if (corners == null || corners.Length < 6 || corners.Length % 2 != 0) throw new ArgumentException("panel " + name + " needs at least three corners");
        var n = corners.Length / 2;
        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        var vertices = new Vector3[n];
        for (var k = 0; k < n; k++) vertices[k] = ScreenLayer.At(corners[k * 2], corners[k * 2 + 1]);
        var triangles = new int[(n - 2) * 3];
        for (var k = 0; k < n - 2; k++) { triangles[k * 3] = 0; triangles[k * 3 + 1] = k + 1; triangles[k * 3 + 2] = k + 2; }
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void Strip(GameObject piece, float[] s, float px)
    {
        var a = ScreenLayer.At(s[0], s[1]);
        var b = ScreenLayer.At(s[2], s[3]);
        var d = b - a;
        var t = piece.transform;
        t.localPosition = (a + b) * 0.5f + new Vector3(0f, 0f, 1f);
        t.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        t.localScale = new Vector3(d.magnitude + px / ScreenLayer.Height, px / ScreenLayer.Height, 1f);
    }

    private static void SetAlpha(Material material, ref float shown, float alpha)
    {
        alpha = Mathf.Clamp01(alpha);
        if (Math.Abs(alpha - shown) < 0.004f && (alpha > 0f || shown <= 0f)) return;
        shown = alpha;
        var c = material.color;
        material.color = new Color(c.r, c.g, c.b, alpha);
    }

    private static void Active(GameObject go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    private void Destroy()
    {
        foreach (var m in _mesh) if (m != null && !m.WasCollected) Object.Destroy(m);
        foreach (var m in _material) if (m != null && !m.WasCollected) Object.Destroy(m);
        foreach (var m in new[] { _fadeMaterial, _coreMaterial, _outlineMaterial }) if (m != null && !m.WasCollected) Object.Destroy(m);
        _fade = _seams = null;
        _fadeMaterial = _coreMaterial = _outlineMaterial = null;
    }
}

/// <summary>The shared screen-space layer behind <see cref="ScreenPanels"/> and the overlays: one camera of its own on a spare
/// layer, rendering after every other camera (over the game's HUD) and looking at a unit square in orthographic projection, so anything
/// under it is a fixed piece of the screen. The camera draws only while some user shows something. Rebuilt when the screen size changes
/// (<see cref="Generation"/> moves on and every user rebuilds its pieces).</summary>
internal static class ScreenLayer
{
    private static GameObject? _root;
    private static Camera? _camera;
    private static Mesh? _quad;
    private static int _users;
    private static bool _unavailable;

    internal static int Layer { get; private set; }
    internal static int Width { get; private set; }
    internal static int Height { get; private set; }
    internal static float Aspect { get; private set; } = 1f;
    internal static int Generation { get; private set; }
    internal static Mesh Quad => _quad!;

    /// <summary>Who holds the screen among the exclusive layers of every mod.</summary>
    internal static readonly ScreenArbiter Arbiter = new();

    /// <summary>One user's switch: counted once while on.</summary>
    internal sealed class User
    {
        private bool _on;
        private int _generation = -1;

        internal void Use(bool on)
        {
            if (_generation != Generation) { _on = false; _generation = Generation; }
            if (_on == on) return;
            _on = on;
            _users += on ? 1 : -1;
            if (_users < 0) _users = 0;
            Refresh();
        }

        internal void Reset()
        {
            _on = false;
            _generation = Generation;
        }
    }

    /// <summary>The layer draws while some user shows something and no game screen is open (<see cref="HudFocus"/>).</summary>
    internal static void Refresh()
    {
        if (_root == null || _root.WasCollected) return;
        var active = _users > 0 && !HudFocus.Away;
        if (_root.activeSelf != active) _root.SetActive(active);
        if (_camera!.enabled != active) _camera.enabled = active;
    }

    internal static bool Ensure()
    {
        if (_unavailable) return false;
        if (_root != null && !_root.WasCollected && Width == ForgeCore.Frame.ScreenWidth && Height == ForgeCore.Frame.ScreenHeight) return true;
        try { Build(); }
        catch (Exception error)
        {
            _unavailable = true;
            Plugin.Error("ui-screen-overlay-layer", "The screen overlay layer could not be built; screen overlays are off: " + error);
            return false;
        }
        return true;
    }

    private static void Build()
    {
        if (_root != null && !_root.WasCollected) Object.Destroy(_root);
        if (_quad != null && !_quad.WasCollected) Object.Destroy(_quad);
        Width = ForgeCore.Frame.ScreenWidth;
        Height = ForgeCore.Frame.ScreenHeight;
        Aspect = Width / (float)Math.Max(1, Height);
        Layer = FreeLayer();
        _root = new GameObject("ForgeUI.Screen") { layer = Layer };
        Object.DontDestroyOnLoad(_root);
        var cameraObject = new GameObject("Camera") { layer = Layer };
        cameraObject.transform.SetParent(_root.transform, false);
        _camera = cameraObject.AddComponent<Camera>();
        _camera.orthographic = true;
        _camera.orthographicSize = 0.5f;
        _camera.nearClipPlane = 0.1f;
        _camera.farClipPlane = 5f;
        _camera.clearFlags = CameraClearFlags.Depth;
        _camera.cullingMask = 1 << Layer;
        _camera.depth = 500f;
        _camera.allowHDR = false;
        _camera.allowMSAA = false;
        _camera.useOcclusionCulling = false;
        _camera.enabled = false;
        _quad = new Mesh { name = "ForgeUI.Screen.Quad", hideFlags = HideFlags.HideAndDontSave };
        _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
        _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        _quad.RecalculateBounds();
        _root.SetActive(false);
        _users = 0;
        Generation++;
    }

    /// <summary>The first unnamed layer from the top (spare layers are unnamed, nothing else renders them).</summary>
    private static int FreeLayer()
    {
        for (var layer = 31; layer >= 8; layer--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
        return 31;
    }

    internal static Material Material(Shader shader, Color colour, int queue)
        => new(shader) { color = colour, renderQueue = queue, hideFlags = HideFlags.HideAndDontSave };

    internal static GameObject Group(string name)
    {
        var go = new GameObject(name) { layer = Layer };
        go.transform.SetParent(_root!.transform, false);
        return go;
    }

    internal static GameObject Piece(string name, Mesh mesh, Material material, GameObject? parent = null)
    {
        var go = new GameObject(name) { layer = Layer };
        go.transform.SetParent((parent ?? _root!).transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, 1f);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    /// <summary>Camera space of unit-square point (u, v).</summary>
    internal static Vector3 At(float u, float v) => new((u - 0.5f) * Aspect, v - 0.5f, 0f);

    /// <summary>Draws whatever is active once into a 16 x 16 temporary texture without showing it, so the first real draw of every shader
    /// on the layer (and its screen grab) costs nothing.</summary>
    internal static void RenderOnce()
    {
        if (_root == null || _root.WasCollected || _camera == null) return;
        var wasActive = _root.activeSelf;
        var wasEnabled = _camera.enabled;
        var target = RenderTexture.GetTemporary(16, 16, 0);
        _root.SetActive(true);
        _camera.enabled = false;
        _camera.targetTexture = target;
        _camera.Render();
        _camera.targetTexture = null;
        _camera.enabled = wasEnabled;
        _root.SetActive(wasActive);
        RenderTexture.ReleaseTemporary(target);
    }
}
