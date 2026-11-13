using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>
/// Materials for a <see cref="SurfaceAppearance"/>: stateless construction, the caller owns and destroys what it gets. Every host (model
/// placements, weapon surfaces, enemy parts, imported models) paints through <see cref="Paint"/>; a glow is written only by
/// <see cref="Glow.Write"/>. Invisible is not a material: the surface keeps its own and only its shadow is drawn (<see cref="SetShadowOnly"/>).
/// </summary>
public static partial class SurfaceLook
{
    private static readonly int ColorId = Shader.PropertyToID("_Color"), BumpMap = Shader.PropertyToID("_BumpMap"),
        MetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap");
    private static Shader? _surfaceShader;

    /// <summary>
    /// A new material showing <paramref name="look"/> over <paramref name="source"/> (never written). A tint draws with the game's flat
    /// surface shader (<see cref="Glow.SurfaceShader"/>): source colour textures and layer weights cannot multiply the author's colour back,
    /// while the source normal, metal/gloss and active emission stay.
    /// </summary>
    public static Material Paint(Material source, SurfaceAppearance look)
    {
        var material = look.Tint is { } tint ? Recolour(source, tint) : new Material(source) { name = source.name + " (Forge look)" };
        try
        {
            if (look.GlowColor is { } glow)
                Glow.Write(material, Glow.Srgb(glow, look.GlowBrightness * (Glow.IsSurface(material) ? Glow.SurfaceEmissionPerBrightness : 1f)));
            return material;
        }
        catch { UnityEngine.Object.Destroy(material); throw; }
    }

    /// <summary>The one switch for an invisible surface: the body is not drawn, its shadow still is (ShadowsOnly, the way the game draws
    /// its shadow enemies); false draws it again with its shadow. A removed surface is <see cref="SetDrawn"/> false instead: no shadow either.</summary>
    public static void SetShadowOnly(Renderer renderer, bool shadowOnly)
        => renderer.shadowCastingMode = shadowOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;

    /// <summary>The flat colour a painted material holds, as the author's sRGB (the inverse of the write in <see cref="Paint"/>) and its
    /// alpha; false when the material has no colour property. This is the readback of a tint, next to <c>model-opacity-readback</c>.</summary>
    public static bool ReadTint(Material material, out float[] srgb, out float alpha)
    {
        srgb = Array.Empty<float>(); alpha = 1f;
        if (material == null || !material.HasProperty(ColorId)) return false;
        var colour = material.GetColor(ColorId);
        srgb = new[] { Glow.LinearToSrgb(colour.r), Glow.LinearToSrgb(colour.g), Glow.LinearToSrgb(colour.b) };
        alpha = colour.a;
        return true;
    }

    /// <summary>Replaces the source's colour-producing shader inputs with the game's flat surface shader. Only relief maps and an already
    /// active emission cross over; no albedo, vertex-layer or procedural colour does.</summary>
    private static Material Recolour(Material source, float[] tint)
    {
        var shader = _surfaceShader = _surfaceShader != null ? _surfaceShader : Shader.Find(Glow.SurfaceShader);
        if (shader == null) throw new InvalidOperationException("model-surface-shader-unavailable");
        var material = new Material(shader) { name = source.name + " (Forge look)" };
        try
        {
            if (!material.HasProperty(ColorId)) throw new InvalidOperationException("model-surface-colour-unavailable");
            CopyTexture(source, material, BumpMap);
            CopyTexture(source, material, MetallicGlossMap);
            float alpha = source.HasProperty(ColorId) ? source.GetColor(ColorId).a : 1f;
            material.SetColor(ColorId, Glow.Srgb(tint, 1f, alpha));
            Glow.Preserve(source, material);
            return material;
        }
        catch { UnityEngine.Object.Destroy(material); throw; }
    }

    private static void CopyTexture(Material source, Material target, int property)
    {
        if (!source.HasProperty(property) || !target.HasProperty(property)) return;
        var texture = source.GetTexture(property);
        if (texture != null) target.SetTexture(property, texture);
    }
}
