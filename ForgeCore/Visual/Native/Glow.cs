using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>Which materials a <see cref="GlowHandle"/> lights.</summary>
public enum GlowMode
{
    /// <summary>Only the materials with an emission map: their light strips and visors glow in the colour, the rest stays as it is.</summary>
    Lit,
    /// <summary>Every material glows over its whole surface (its emission map becomes plain white).</summary>
    Whole,
}

/// <summary>Glowing materials: a renderer's (or a whole object's) game-shaded materials copied once for this object and lit in a colour
/// that may exceed 1 (brighter under HDR). <see cref="GlowHandle.Set"/> writes one colour per material and allocates nothing, so it can
/// run every frame of an animated glow.</summary>
public static partial class Glow
{
    private static Texture2D? _white;

    /// <summary>The materials of <paramref name="renderer"/> as a glow; null (logged once) when it has none to light.</summary>
    public static GlowHandle? For(Renderer renderer, GlowMode mode = GlowMode.Lit)
    {
        if (renderer == null || renderer.WasCollected) throw new ArgumentNullException(nameof(renderer));
        return Build(new[] { renderer }, mode, renderer.name);
    }

    /// <summary>The materials of every renderer under <paramref name="root"/> as one glow; null (logged once) when none can be lit.</summary>
    public static GlowHandle? For(GameObject root, GlowMode mode = GlowMode.Lit)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        return Build(root.GetComponentsInChildren<Renderer>(true), mode, root.name);
    }

    private static GlowHandle? Build(IList<Renderer> renderers, GlowMode mode, string what)
    {
        var copies = new List<Material>();
        var owners = new List<(Renderer Renderer, Material[] Shared)>();
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.WasCollected) continue;
            var shared = renderer.sharedMaterials;
            var original = new Material[shared.Length];
            var copied = false;
            for (var i = 0; i < shared.Length; i++)
            {
                var material = shared[i];
                original[i] = material;
                if (material == null || !material.HasProperty(EmissiveColor)) continue;
                var lit = material.HasProperty(EmissiveMap) && material.GetTexture(EmissiveMap) != null;
                if (mode == GlowMode.Lit && !lit) continue;
                var own = new Material(material) { name = material.name + " (glow)" };
                if (mode == GlowMode.Whole)
                {
                    own.SetTexture(EmissiveMap, White());
                    if (own.HasProperty(EnableEmissive)) own.SetFloat(EnableEmissive, 1f);
                    own.EnableKeyword("ENABLE_EMISSIVE");
                }
                copies.Add(own);
                shared[i] = own;
                copied = true;
            }
            if (!copied) continue;
            renderer.sharedMaterials = shared;
            owners.Add((renderer, original));
        }
        if (copies.Count != 0) return new GlowHandle(copies.ToArray(), owners.ToArray());
        Plugin.Logger.ErrorOnce("visual-glow-has-no-material", "ForgeVisual glow: " + what + " has no material that can glow" + (mode == GlowMode.Lit ? " (none has an emission map)" : "") + ".");
        return null;
    }

    private static Texture2D White()
    {
        if (_white != null && !_white.WasCollected) return _white;
        _white = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ForgeVisual white", hideFlags = HideFlags.HideAndDontSave };
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++) _white.SetPixel(x, y, Color.white);
        _white.Apply(false, true);
        return _white;
    }
}

/// <summary>An object's own glowing materials.</summary>
public sealed class GlowHandle : IDisposable
{
    private readonly Material[] _materials;
    private readonly (Renderer Renderer, Material[] Shared)[] _owners;

    internal GlowHandle(Material[] materials, (Renderer, Material[])[] owners)
    {
        _materials = materials;
        _owners = owners;
    }

    /// <summary>The colour written last.</summary>
    public Color Colour { get; private set; }

    public int Materials => _materials.Length;

    /// <summary>Lights every material in <paramref name="hdr"/> (components may exceed 1). One call per material, no allocation.</summary>
    public void Set(Color hdr)
    {
        Colour = hdr;
        for (var i = 0; i < _materials.Length; i++)
        {
            var material = _materials[i];
            if (material != null && !material.WasCollected) material.SetColor(Glow.EmissiveColor, hdr);
        }
    }

    /// <summary>Puts the original materials back and destroys the copies.</summary>
    public void Dispose()
    {
        foreach (var (renderer, shared) in _owners)
            if (renderer != null && !renderer.WasCollected) renderer.sharedMaterials = shared;
        foreach (var material in _materials)
            if (material != null && !material.WasCollected) Object.Destroy(material);
    }
}

/// <summary>A soft glowing spot under a parent (the ball in a hand, a lamp on a model): a camera-facing quad of <c>Forge:soft-dot</c>,
/// drawn additively. <see cref="Pulse"/> swells it and fades it out on its own; <see cref="Show"/> holds it steady. A spot is made once
/// and reused.</summary>
public sealed class GlowSpot
{
    private static readonly List<GlowSpot> Live = new();
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private readonly GameObject _object;
    private readonly MeshRenderer _renderer;
    private readonly MaterialPropertyBlock _block = new();
    private Color _colour;
    private float _start = -1f, _seconds, _size0, _size1, _alpha;
    private bool _steady;

    private GlowSpot(GameObject o, MeshRenderer r)
    {
        _object = o;
        _renderer = r;
    }

    /// <summary>A spot under <paramref name="parent"/> at <paramref name="localPosition"/>; null (logged once) when the library's
    /// shaders are not available.</summary>
    public static GlowSpot? Create(Transform parent, Vector3 localPosition)
    {
        var material = Shaders.SpriteMaterial(SpriteSheet.SoftDot, FxBlend.Additive);
        if (material == null) return null;
        var o = new GameObject("ForgeVisual.GlowSpot");
        o.transform.SetParent(parent, false);
        o.transform.localPosition = localPosition;
        o.AddComponent<MeshFilter>().sharedMesh = Shaders.Quad();
        var r = o.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        o.SetActive(false);
        return new GlowSpot(o, r);
    }

    public bool Alive => _object != null && !_object.WasCollected;

    /// <summary>Swells from <paramref name="size0"/> to <paramref name="size1"/> metres over <paramref name="seconds"/>, fading out from
    /// <paramref name="alpha"/>, then hides.</summary>
    public void Pulse(Color colour, float seconds, float size0, float size1, float alpha = 0.85f)
    {
        if (!Alive) return;
        _colour = colour;
        _seconds = Math.Max(0.01f, seconds);
        _size0 = size0;
        _size1 = size1;
        _alpha = alpha;
        _steady = false;
        Begin();
    }

    /// <summary>Shows the spot steadily at <paramref name="size"/> until <see cref="Hide"/>.</summary>
    public void Show(Color colour, float size)
    {
        if (!Alive) return;
        _colour = colour;
        _size0 = _size1 = size;
        _alpha = colour.a;
        _steady = true;
        Begin();
    }

    /// <summary>Moves a spot to a world position (a lamp that follows a moving model); it keeps facing the camera.</summary>
    public void Place(Vector3 position)
    {
        if (Alive) _object.transform.position = position;
    }

    public bool Shown => Alive && _object.activeSelf;

    public void Hide()
    {
        _start = -1f;
        if (Alive) _object.SetActive(false);
        Live.Remove(this);
    }

    public void Destroy()
    {
        Hide();
        if (Alive) Object.Destroy(_object);
    }

    private void Begin()
    {
        _start = Scene.Now;
        _object.SetActive(true);
        if (!Live.Contains(this)) Live.Add(this);
        Step(_start);
    }

    private bool Step(float now)
    {
        if (!Alive) return false;
        var k = _steady ? 0f : (now - _start) / _seconds;
        if (k >= 1f) { Hide(); return false; }
        var ease = 1f - (1f - k) * (1f - k);
        _object.transform.localScale = Vector3.one * (_size0 + (_size1 - _size0) * ease);
        if (Scene.HasEye)
        {
            var facing = _object.transform.position - Scene.Eye;
            if (facing.sqrMagnitude > 1e-6f) _object.transform.rotation = Quaternion.LookRotation(facing);
        }
        _block.SetColor(ColorId, new Color(_colour.r, _colour.g, _colour.b, _alpha * (1f - k)));
        _renderer.SetPropertyBlock(_block);
        return true;
    }

    internal static void TickAll(float now)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
            if (i < Live.Count) Live[i].Step(now);
    }

    internal static void ClearAll()
    {
        for (var i = Live.Count - 1; i >= 0; i--) Live[i].Hide();
    }
}
