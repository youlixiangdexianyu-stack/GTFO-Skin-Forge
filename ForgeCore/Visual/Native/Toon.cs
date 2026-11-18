using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>How a toon surface reaches the game's light (both stay; a package lets each player choose, as ForgeSkin's toon setting does).</summary>
public enum ToonLighting
{
    /// <summary>ForgeVisual/ToonForward: a forward pass that lights itself from the game's clustered lights, fog and ambient volume, with its own
    /// outline pass. Not in the G-buffer (the thermal sight, screen reflections and decals do not see it).</summary>
    Forward,
    /// <summary>ForgeVisual/ToonDeferred: writes the game's G-buffer like its gear shader and is lit by the game; its look comes from
    /// <see cref="Toon.Overlay"/> drawn over it.</summary>
    Deferred,
}

/// <summary>
/// ForgeVisual's one toon shader, for any model with normals and UVs: an author gives textures and parameters, never a shader of their own.
/// The material properties are the shader's parameter list: <c>_MainTex</c> (base colour, sRGB) and <c>_Color</c>, <c>_BumpMap</c> with
/// <c>_UseNormalMap</c> 1, <c>_ShadeColor</c>, <c>_ShadeThreshold</c> and <c>_ShadeSoftness</c> or a ramp <c>_ShadeRamp</c> with
/// <c>_UseShadeRamp</c> 1, <c>_OutlineWidth</c> (pixels at 1080 lines) and <c>_OutlineColor</c>, <c>_RimColor</c>, <c>_RimPower</c>,
/// <c>_RimStrength</c>, <c>_HighlightColor</c>, <c>_HighlightSize</c>, <c>_HighlightSoftness</c> (forward only); glow through <c>Glow.Write</c>
/// (the gear shader's <c>_EmissiveColor</c>, <c>_EmissiveMap</c>, <c>_EnableEmissive</c>, so <c>LitMaterial.EmissionPerUnit</c> holds). A
/// first-person copy is made with <c>LitMaterial.FirstPersonCopy</c> as for the gear shader. The caller owns what it gets.
/// </summary>
public static class Toon
{
    private static readonly int MainTex = Shader.PropertyToID("_MainTex"), BumpMap = Shader.PropertyToID("_BumpMap"), UseNormalMap = Shader.PropertyToID("_UseNormalMap"),
        Cutoff = Shader.PropertyToID("_Cutoff");
    private const string AlphaTest = "_ALPHATEST_ON";

    /// <summary>A toon material with the shader's default parameters, or null when ForgeVisual's shaders could not be loaded (logged once).</summary>
    public static Material? Create(ToonLighting lighting, string name)
    {
        var shader = lighting == ToonLighting.Forward ? Shaders.ToonForward : Shaders.ToonDeferred;
        return shader == null ? null : new Material(shader) { name = name };
    }

    /// <summary>A toon material showing what the lit (gear shader) material <paramref name="lit"/> shows: its base colour, its normal map, its
    /// glow (<c>Glow.Preserve</c>) and its first-person drawing; null when the shaders are not loaded.</summary>
    public static Material? From(Material lit, ToonLighting lighting)
    {
        var toon = Create(lighting, lit.name + " (toon)");
        if (toon == null) return null;
        if (lit.HasProperty(MainTex)) toon.SetTexture(MainTex, lit.GetTexture(MainTex));
        var normal = lit.HasProperty(BumpMap) ? lit.GetTexture(BumpMap) : null;
        if (normal != null) { toon.SetTexture(BumpMap, normal); toon.SetFloat(UseNormalMap, 1f); }
        Glow.Preserve(lit, toon);
        if (LitMaterial.IsFirstPerson(lit)) LitMaterial.SetFirstPerson(toon, true);
        // A cut-out (LitMaterial's cut-out shader) stays cut out at the same threshold.
        if (lit.HasProperty(Cutoff))
        {
            toon.SetFloat(Cutoff, lit.GetFloat(Cutoff));
            toon.EnableKeyword(AlphaTest);
            toon.renderQueue = 2450;
        }
        return toon;
    }

    /// <summary>Writes an author's toon parameters onto <paramref name="toon"/> by name: <c>shadeColor</c> is <c>_ShadeColor</c>; one number a
    /// float, three or four a colour (sRGB, alpha 1 when three). Returns the names the shader does not have or whose count fits nothing.</summary>
    public static List<string> Apply(Material toon, IReadOnlyDictionary<string, float[]>? parameters)
    {
        var unknown = new List<string>();
        if (parameters == null) return unknown;
        foreach (var (name, values) in parameters)
        {
            var property = name.Length == 0 ? "" : "_" + char.ToUpperInvariant(name[0]) + name[1..];
            if (property.Length == 0 || !toon.HasProperty(property)) { unknown.Add(name); continue; }
            if (values.Length == 1) toon.SetFloat(property, values[0]);
            else if (values.Length is 3 or 4) toon.SetColor(property, new Color(values[0], values[1], values[2], values.Length == 4 ? values[3] : 1f));
            else unknown.Add(name);
        }
        return unknown;
    }

    /// <summary>The overlay a <see cref="ToonLighting.Deferred"/> surface needs to look toon: drawn over the same mesh with the same values (copied
    /// from <paramref name="deferred"/>), after the game's lighting; null when the shaders are not loaded.</summary>
    public static Material? Overlay(Material deferred)
    {
        var shader = Shaders.ToonOverlay;
        if (shader == null) return null;
        var overlay = new Material(shader) { name = deferred.name + " (toon overlay)" };
        overlay.CopyPropertiesFromMaterial(deferred);
        overlay.shaderKeywords = deferred.shaderKeywords;
        return overlay;
    }

    // What an overlay draws for a slot that is not toon: nothing (the cut-out shader cutting every pixel). Shared, never changed.
    private static Material? _nothing;

    internal static Material? Nothing()
    {
        if (_nothing != null && !_nothing.WasCollected) return _nothing;
        var shader = Shaders.LitCutout;
        if (shader == null) return null;
        _nothing = new Material(shader) { name = "ForgeVisual toon overlay (no toon)", hideFlags = HideFlags.HideAndDontSave };
        _nothing.SetFloat(Cutoff, 2f);
        return _nothing;
    }
}

/// <summary>
/// The toon look on chosen material slots of one renderer, with the lighting switched at any time (<see cref="Set"/>): the formal way to put
/// <see cref="Toon"/> on a model. <see cref="Wear"/> keeps the renderer's lit materials; <c>Set(Forward)</c> or <c>Set(Deferred)</c> replaces the
/// toon slots with toon copies (<see cref="Toon.From"/> plus the author's parameters, <see cref="Toon.Apply"/>), the deferred lighting with an
/// overlay renderer under it drawing the same mesh (other slots draw nothing there; it casts no shadow and follows the renderer's shadow-only
/// state); <c>Set(null)</c> puts the lit materials back. <see cref="Dispose"/> does that and destroys what it made. Not per frame: a switch
/// rebuilds the copies. The caller owns the lit materials and the renderer.
/// </summary>
public sealed class ToonDress : IDisposable
{
    private readonly Renderer _renderer;
    private readonly Material[] _lit;
    private readonly bool[] _toon;
    private readonly IReadOnlyDictionary<string, float[]>?[] _parameters;
    private readonly List<Object> _made = new();
    private GameObject? _overlay;

    private ToonDress(Renderer renderer, Material[] lit, bool[] toon, IReadOnlyDictionary<string, float[]>?[] parameters)
    {
        _renderer = renderer;
        _lit = lit;
        _toon = toon;
        _parameters = parameters;
    }

    /// <summary>The renderer's current materials as its lit look; slot i is toon when <paramref name="toon"/>[i] (a slot past the list is lit),
    /// with <paramref name="parameters"/>[i] (null: the shader's defaults). Draws lit until <see cref="Set"/>.</summary>
    public static ToonDress Wear(Renderer renderer, IReadOnlyList<bool> toon, IReadOnlyList<IReadOnlyDictionary<string, float[]>?>? parameters = null)
    {
        if (renderer == null || renderer.WasCollected) throw new ArgumentNullException(nameof(renderer));
        var shared = renderer.sharedMaterials;
        var lit = new Material[shared.Length];
        for (var i = 0; i < lit.Length; i++) lit[i] = shared[i];
        var slots = new bool[lit.Length];
        var values = new IReadOnlyDictionary<string, float[]>?[lit.Length];
        for (var i = 0; i < lit.Length; i++)
        {
            slots[i] = i < toon.Count && toon[i] && lit[i] != null;
            values[i] = parameters != null && i < parameters.Count ? parameters[i] : null;
        }
        return new ToonDress(renderer, lit, slots, values);
    }

    /// <summary>The lighting drawn now; null while lit.</summary>
    public ToonLighting? Lighting { get; private set; }

    /// <summary>Parameter names the toon shader does not have, from the last <see cref="Set"/> (for the caller's author log).</summary>
    public List<string> Unknown { get; } = new();

    /// <summary>Draws the toon slots with <paramref name="lighting"/>, or lit when null. False (and lit) when ForgeVisual's shaders are not loaded.</summary>
    public bool Set(ToonLighting? lighting)
    {
        if (_renderer == null || _renderer.WasCollected) return false;
        Clear();
        Lighting = null;
        if (lighting is not { } mode) return true;
        var drawn = (Material[])_lit.Clone();
        var over = mode == ToonLighting.Deferred ? new Material[_lit.Length] : null;
        Unknown.Clear();
        for (var i = 0; i < _lit.Length; i++)
        {
            if (!_toon[i]) { if (over != null) over[i] = Toon.Nothing()!; continue; }
            var toon = Toon.From(_lit[i], mode);
            if (toon == null) return Missing(mode == ToonLighting.Forward ? "ForgeVisual/ToonForward" : "ForgeVisual/ToonDeferred");
            _made.Add(toon);
            Unknown.AddRange(Toon.Apply(toon, _parameters[i]));
            drawn[i] = toon;
            if (over == null) continue;
            var overlay = Toon.Overlay(toon);
            if (overlay == null) return Missing("ForgeVisual/ToonOverlay");
            _made.Add(overlay);
            over[i] = overlay;
        }
        if (over != null && Array.Exists(over, m => m == null)) return Missing("ForgeVisual/LitCutout");
        _renderer.sharedMaterials = drawn;
        if (over != null) Overlay(over);
        Lighting = mode;
        return true;
    }

    private bool Missing(string shader)
    {
        Clear();
        Plugin.Logger.ErrorOnce("visual-toon-shader-missing", "ForgeVisual's toon shader " + shader + " is not loaded; toon surfaces draw lit.");
        return false;
    }

    // The same mesh again under the renderer, drawn with the overlay materials after the game's lighting.
    private void Overlay(Material[] materials)
    {
        _overlay = new GameObject("ForgeToon overlay") { layer = _renderer.gameObject.layer };
        _overlay.transform.SetParent(_renderer.transform, false);
        Renderer copy;
        var skinned = _renderer.TryCast<SkinnedMeshRenderer>();
        if (skinned != null)
        {
            var s = _overlay.AddComponent<SkinnedMeshRenderer>();
            s.sharedMesh = skinned.sharedMesh;
            s.bones = skinned.bones;
            s.rootBone = skinned.rootBone;
            s.localBounds = skinned.localBounds;
            copy = s;
        }
        else
        {
            _overlay.AddComponent<MeshFilter>().sharedMesh = _renderer.GetComponent<MeshFilter>().sharedMesh;
            copy = _overlay.AddComponent<MeshRenderer>();
        }
        // A body that only casts its shadow (the local player's third-person body) shows no overlay either.
        copy.shadowCastingMode = _renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off;
        copy.receiveShadows = false;
        copy.sharedMaterials = materials;
    }

    private void Clear()
    {
        if (_renderer != null && !_renderer.WasCollected) _renderer.sharedMaterials = _lit;
        if (_overlay != null && !_overlay.WasCollected) Object.Destroy(_overlay);
        _overlay = null;
        foreach (var made in _made) if (made != null && !made.WasCollected) Object.Destroy(made);
        _made.Clear();
    }

    /// <summary>Puts the lit materials back and destroys the toon copies and the overlay.</summary>
    public void Dispose()
    {
        Clear();
        Lighting = null;
    }
}
