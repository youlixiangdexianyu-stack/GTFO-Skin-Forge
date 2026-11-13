using System;
using UnityEngine;

namespace ForgeVisual;

public static partial class Glow
{
    internal static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor"), EmissiveMap = Shader.PropertyToID("_EmissiveMap"),
        EnableEmissive = Shader.PropertyToID("_EnableEmissive");
    /// <summary>The game's measured flat surface shader: an author tint on it replaces a model's colour without the source albedo.</summary>
    public const string SurfaceShader = "Cell/CellSurface/Default";
    /// <summary>The flat surface shader's emission multiple per unit of author brightness (measured).</summary>
    public const float SurfaceEmissionPerBrightness = 0.02f;
    private const string EmissionKeyword = "_EMISSION", EmissiveKeyword = "ENABLE_EMISSIVE", SelfIllumKeyword = "ENABLE_SELF_ILLUM";
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor"), EmissionMap = Shader.PropertyToID("_EmissionMap");

    /// <summary>Whether <paramref name="material"/> uses the flat surface shader (<see cref="SurfaceShader"/>).</summary>
    public static bool IsSurface(Material material)
        => material.shader != null && material.shader.name == SurfaceShader;

    /// <summary>The extended sRGB transfer the website preview applies before lighting: <paramref name="value"/> (r, g, b) to linear, times
    /// <paramref name="scale"/>. Components above one stay HDR; a non-finite result throws <c>model-appearance-colour-range</c>.</summary>
    public static Color Srgb(float[] value, float scale, float alpha = 1f)
        => new(SrgbToLinear(value[0]) * scale, SrgbToLinear(value[1]) * scale, SrgbToLinear(value[2]) * scale, alpha);

    /// <summary>The inverse of <see cref="Srgb"/> for one channel: what a material's linear colour reads back as, in the author's sRGB.</summary>
    public static float LinearToSrgb(float value)
        => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

    private static float SrgbToLinear(float value)
    {
        var linear = value < 0.04045f ? value * 0.0773993808f : MathF.Pow(value * 0.9478672986f + 0.0521327014f, 2.4f);
        if (!float.IsFinite(linear)) throw new InvalidOperationException("model-appearance-colour-range");
        return linear;
    }

    /// <summary>Writes one uniform glow into <paramref name="material"/> itself, on both emission families the game's model shaders declare:
    /// Unity's <c>_EmissionColor</c>/<c>_EmissionMap</c>/<c>_EMISSION</c> and the game's <c>_EmissiveColor</c>/<c>_EmissiveMap</c>/
    /// <c>_EnableEmissive</c>. The flat surface shader takes <c>ENABLE_SELF_ILLUM</c> (scale the colour by <see cref="SurfaceEmissionPerBrightness"/>
    /// per author brightness); the game's own materials keep <c>ENABLE_EMISSIVE</c>. The emission map is <paramref name="map"/> (a model's own glow mask) or white,
    /// so the glow is even. This is the one emission write of the libraries and packages.
    /// The caller owns <paramref name="material"/> (a copy); a material with neither family throws <c>model-glow-unsupported</c>.</summary>
    public static void Write(Material material, Color emission, Texture? map = null)
    {
        var written = false;
        if (material.HasProperty(EmissionColor))
        {
            material.SetColor(EmissionColor, emission);
            if (material.HasProperty(EmissionMap)) material.SetTexture(EmissionMap, map ?? Texture2D.whiteTexture);
            material.EnableKeyword(EmissionKeyword);
            written = true;
        }
        if (material.HasProperty(EmissiveColor))
        {
            material.SetColor(EmissiveColor, emission);
            if (material.HasProperty(EmissiveMap)) material.SetTexture(EmissiveMap, map ?? Texture2D.whiteTexture);
            if (material.HasProperty(EnableEmissive)) material.SetFloat(EnableEmissive, 1f);
            material.EnableKeyword(IsSurface(material) ? SelfIllumKeyword : EmissiveKeyword);
            written = true;
        }
        if (!written) throw new InvalidOperationException("model-glow-unsupported");
    }

    /// <summary>Changes only the colour of <paramref name="material"/>'s glow, on both families' colour properties it has: no map, toggle or
    /// keyword is touched and nothing is allocated, so it runs every frame of an animated glow on a material <see cref="Write"/> lit (on one
    /// it did not, the colour waits for the next <see cref="Write"/>).</summary>
    public static void SetColour(Material material, Color emission)
    {
        if (material.HasProperty(EmissiveColor)) material.SetColor(EmissiveColor, emission);
        if (material.HasProperty(EmissionColor)) material.SetColor(EmissionColor, emission);
    }

    /// <summary>Switches <paramref name="material"/>'s glow off on both families (the toggle and the keywords <see cref="Write"/> sets); its colour
    /// and map stay for a later <see cref="Write"/>. The other half of the one emission write; the caller owns the material.</summary>
    public static void Clear(Material material)
    {
        material.DisableKeyword(EmissionKeyword);
        if (material.HasProperty(EnableEmissive)) material.SetFloat(EnableEmissive, 0f);
        material.DisableKeyword(IsSurface(material) ? SelfIllumKeyword : EmissiveKeyword);
    }

    /// <summary>Carries <paramref name="source"/>'s active emission (either family) onto <paramref name="target"/> when it has one, so a
    /// recoloured copy keeps the light strips the game's material had. A source with no active emission leaves the target as it is.</summary>
    public static void Preserve(Material source, Material target)
    {
        Color colour;
        Texture? map;
        if (source.HasProperty(EmissionColor) && source.IsKeywordEnabled(EmissionKeyword))
        {
            colour = source.GetColor(EmissionColor);
            map = source.HasProperty(EmissionMap) ? source.GetTexture(EmissionMap) : null;
        }
        else if (source.HasProperty(EmissiveColor)
            && (source.IsKeywordEnabled(EmissiveKeyword) || source.IsKeywordEnabled(SelfIllumKeyword))
            && (!source.HasProperty(EnableEmissive) || source.GetFloat(EnableEmissive) != 0f))
        {
            colour = source.GetColor(EmissiveColor);
            map = source.HasProperty(EmissiveMap) ? source.GetTexture(EmissiveMap) : null;
        }
        else return;
        var standard = target.HasProperty(EmissionColor);
        var colourId = standard ? EmissionColor : EmissiveColor;
        var mapId = standard ? EmissionMap : EmissiveMap;
        if (!target.HasProperty(colourId)) throw new InvalidOperationException("model-surface-emission-unavailable");
        if (standard && IsSurface(source))
            colour = new Color(colour.r / SurfaceEmissionPerBrightness, colour.g / SurfaceEmissionPerBrightness,
                colour.b / SurfaceEmissionPerBrightness, colour.a);
        target.SetColor(colourId, colour);
        if (target.HasProperty(mapId)) target.SetTexture(mapId, map ?? Texture2D.whiteTexture);
        if (target.HasProperty(EnableEmissive)) target.SetFloat(EnableEmissive, 1f);
        target.EnableKeyword(standard ? EmissionKeyword : SelfIllumKeyword);
    }
}
