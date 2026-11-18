using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeVisual;

/// <summary>How a sprite or particle is blended over the scene.</summary>
public enum FxBlend
{
    /// <summary>Ordinary alpha blending: smoke, dust, debris, scorch marks.</summary>
    Alpha,
    /// <summary>Adds light: fire, flashes, glows, sparks, shock rings. Nothing behind it darkens.</summary>
    Additive,
}

/// <summary>The library's shaders (particle, marker, outline, procedural, the toon shader's three entries and the lit cut-out), loaded once from the bundle embedded
/// in this DLL through <c>ForgeCore.Assets</c>, the base textures made in code, and the shared quad meshes. A failure is logged once and
/// every caller gets null (its drawing is skipped, nothing falls back to another look).</summary>
internal static class Shaders
{
    private static readonly int Src = Shader.PropertyToID("_Src"), Dst = Shader.PropertyToID("_Dst"), Additive = Shader.PropertyToID("_Additive");
    private static bool _tried;
    private static Shader? _particle, _marker, _outline, _procedural, _toonForward, _toonDeferred, _toonOverlay, _litCutout;
    private static Material? _markerMaterial;
    private static Mesh? _quad;

    internal static Shader? Particle => Load() ? _particle : null;
    internal static Shader? Outline => Load() ? _outline : null;
    internal static Shader? Procedural => Load() ? _procedural : null;
    internal static Shader? ToonForward => Load() ? _toonForward : null;
    internal static Shader? ToonDeferred => Load() ? _toonDeferred : null;
    internal static Shader? ToonOverlay => Load() ? _toonOverlay : null;
    internal static Shader? LitCutout => Load() ? _litCutout : null;

    /// <summary>The one shared material of the marker shader; each renderer sets its own state through a property block.</summary>
    internal static Material? Marker()
    {
        if (_markerMaterial != null && !_markerMaterial.WasCollected) return _markerMaterial;
        if (!Load()) return null;
        return _markerMaterial = new Material(_marker) { name = "ForgeVisual marker", hideFlags = HideFlags.HideAndDontSave };
    }

    /// <summary>A new material of the particle shader showing <paramref name="texture"/> with <paramref name="blend"/>.</summary>
    internal static Material? Make(Texture texture, FxBlend blend, string name)
    {
        var shader = Particle;
        if (shader == null) return null;
        var material = new Material(shader) { name = name, mainTexture = texture, hideFlags = HideFlags.HideAndDontSave };
        Apply(material, blend);
        return material;
    }

    internal static void Apply(Material material, FxBlend blend)
    {
        var add = blend == FxBlend.Additive;
        material.SetFloat(Src, add ? 1f : 5f);
        material.SetFloat(Dst, add ? 1f : 10f);
        material.SetFloat(Additive, add ? 1f : 0f);
    }

    /// <summary>The material a sprite sheet draws with (made once per sheet and blend).</summary>
    internal static Material? SpriteMaterial(SpriteSheet sheet, FxBlend blend) => sheet.Material(blend);

    /// <summary>A unit quad facing +Z, white vertex colours (the shader multiplies by them).</summary>
    internal static Mesh Quad() => _quad != null && !_quad.WasCollected ? _quad : _quad = Cell(1, 1, 0, "ForgeVisual quad");

    /// <summary>A unit quad showing cell <paramref name="index"/> of a <paramref name="columns"/> x <paramref name="rows"/> sheet,
    /// counted from the top left as the image is drawn.</summary>
    internal static Mesh Cell(int columns, int rows, int index, string name)
    {
        var u0 = index % columns / (float)columns;
        var u1 = u0 + 1f / columns;
        var v1 = 1f - index / columns / (float)rows;
        var v0 = v1 - 1f / rows;
        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        mesh.vertices = new Il2CppStructArray<Vector3>(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
        mesh.uv = new Il2CppStructArray<Vector2>(new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) });
        var white = new Color32(255, 255, 255, 255);
        mesh.colors32 = new Il2CppStructArray<Color32>(new[] { white, white, white, white });
        mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 2, 1, 2, 3, 1 });
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>A round spot with a soft edge, white, alpha falling off squared toward the rim (tinted per use).</summary>
    internal static Texture2D SoftDot()
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = VisualNames.Join(VisualNames.Forge, ForgeCatalog.SoftDot), wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = x / (size - 1f) * 2f - 1f;
                var v = y / (size - 1f) * 2f - 1f;
                var a = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        texture.Apply(false, true);
        return texture;
    }

    /// <summary>Opaque along a line's length (U), soft across its width (V): a ribbon's glow fades out at both edges.</summary>
    internal static Texture2D SoftLine()
    {
        var texture = new Texture2D(4, 64, TextureFormat.RGBA32, false) { name = VisualNames.Join(VisualNames.Forge, ForgeCatalog.SoftLine), wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (var y = 0; y < 64; y++)
        {
            var a = Mathf.Clamp01(1f - Math.Abs(y / 63f * 2f - 1f));
            a = a * a * (3f - 2f * a);
            for (var x = 0; x < 4; x++) texture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        texture.Apply(false, true);
        return texture;
    }

    private static bool Load()
    {
        if (Alive(_particle) && Alive(_marker) && Alive(_outline) && Alive(_procedural) && Alive(_toonForward) && Alive(_toonDeferred) && Alive(_toonOverlay) && Alive(_litCutout)) return true;
        if (_tried)
        {
            // A failed load nulls them all (Forget, logged where it failed); a shader set but no longer alive was unloaded after it loaded.
            if (_particle is not null) Plugin.Logger.ErrorOnce("visual-forgevisual-shaders-lost", "ForgeVisual's shaders loaded but one of them has been unloaded since; its sprites, particles, ground marks, beams, outlines, toon and cut-out surfaces are no longer drawn.");
            return false;
        }
        _tried = true;
        try
        {
            var bundle = ForgeCore.Assets.LoadBytes(Plugin.Id, "forgevisual-shaders", ShaderBundle.Read());
            if (bundle == null) return false;
            Shader? Get(string asset) => ForgeCore.Assets.Get<Shader>(Plugin.Id, bundle, asset);
            _particle = Get(ShaderBundle.ParticleAsset);
            _marker = Get(ShaderBundle.MarkerAsset);
            _outline = Get(ShaderBundle.OutlineAsset);
            _procedural = Get(ShaderBundle.ProceduralAsset);
            _toonForward = Get(ShaderBundle.ToonForwardAsset);
            _toonDeferred = Get(ShaderBundle.ToonDeferredAsset);
            _toonOverlay = Get(ShaderBundle.ToonOverlayAsset);
            _litCutout = Get(ShaderBundle.LitCutoutAsset);
            var all = new[] { _particle, _marker, _outline, _procedural, _toonForward, _toonDeferred, _toonOverlay, _litCutout };
            if (Array.Exists(all, shader => shader == null)) { Forget(); return false; }
            if (Array.Exists(all, shader => !shader!.isSupported))
            {
                Plugin.Logger.Error("visual-forgevisuals-shaders-are-not-supported", "ForgeVisual's shaders are not supported on this device; its sprites, particles, ground marks, beams, outlines, toon and cut-out surfaces are not drawn.");
                Forget();
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.Error("visual-forgevisuals-shaders-could-not-be-loaded", "ForgeVisual's shaders could not be loaded; its sprites, particles, ground marks, beams, outlines, toon and cut-out surfaces are not drawn: " + error);
            Forget();
            return false;
        }
    }

    private static bool Alive(Shader? shader) => shader != null && !shader.WasCollected;

    private static void Forget() => _particle = _marker = _outline = _procedural = _toonForward = _toonDeferred = _toonOverlay = _litCutout = null;
}
