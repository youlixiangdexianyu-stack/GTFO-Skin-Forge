using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

public static partial class Glow
{
    internal const string EmissionColorName = "_EmissionColor";
    /// <summary>The game shaders' glow colour property, for <see cref="Value"/> and <see cref="IsHdr"/>.</summary>
    public const string EmissiveColorName = "_EmissiveColor";
    internal static readonly int EmissiveColor = Shader.PropertyToID(EmissiveColorName), EmissiveMap = Shader.PropertyToID("_EmissiveMap"),
        EnableEmissive = Shader.PropertyToID("_EnableEmissive");
    /// <summary>The game's measured flat surface shader: an author tint on it replaces a model's colour without the source albedo.</summary>
    public const string SurfaceShader = "Cell/CellSurface/Default";
    /// <summary>The flat surface shader's emission multiple per unit of author brightness (measured).</summary>
    public const float SurfaceEmissionPerBrightness = 0.02f;
    private const string EmissionKeyword = "_EMISSION", EmissiveKeyword = "ENABLE_EMISSIVE", SelfIllumKeyword = "ENABLE_SELF_ILLUM";
    private static readonly int EmissionColor = Shader.PropertyToID(EmissionColorName), EmissionMap = Shader.PropertyToID("_EmissionMap");

    /// <summary>Whether <paramref name="material"/> uses the flat surface shader (<see cref="SurfaceShader"/>).</summary>
    public static bool IsSurface(Material material)
        => material.shader != null && material.shader.name == SurfaceShader;

    /// <summary>Whether <paramref name="property"/> of <paramref name="material"/>'s shader is <c>[HDR]</c> (SetColor passes it on as it is) rather
    /// than a plain colour (SetColor converts it from sRGB to linear); read from the shader once per shader and property. A property the
    /// shader lacks is plain.</summary>
    public static bool IsHdr(Material material, string property)
    {
        var shader = material.shader;
        if (shader == null) return false;
        var key = (shader.GetInstanceID(), property);
        if (!HdrProperties.TryGetValue(key, out var hdr))
        {
            var index = shader.FindPropertyIndex(property);
            HdrProperties[key] = hdr = index >= 0 && (shader.GetPropertyFlags(index) & ShaderPropertyFlags.HDR) != 0;
        }
        return hdr;
    }

    private static readonly Dictionary<(int Shader, string Property), bool> HdrProperties = new();

    /// <summary>The colour to set on <paramref name="property"/> of <paramref name="material"/> for <paramref name="srgb"/> (an author's colour,
    /// may exceed one) at <paramref name="gain"/> (the value white takes in the property's units; 1 for a tint), converted once whatever the
    /// property's kind (<see cref="ColourSpace.Glow(float, float, bool)"/>, <see cref="IsHdr"/>); alpha is kept. A non-finite result throws
    /// <c>model-appearance-colour-range</c>. Allocates nothing.</summary>
    public static Color Value(Material material, string property, Color srgb, float gain)
    {
        var hdr = IsHdr(material, property);
        var value = new Color(ColourSpace.Glow(srgb.r, gain, hdr), ColourSpace.Glow(srgb.g, gain, hdr), ColourSpace.Glow(srgb.b, gain, hdr), srgb.a);
        if (!float.IsFinite(value.r) || !float.IsFinite(value.g) || !float.IsFinite(value.b)) throw new InvalidOperationException("model-appearance-colour-range");
        return value;
    }

    /// <summary>Writes one uniform glow into <paramref name="material"/> itself, on both emission families the game's model shaders declare:
    /// Unity's <c>_EmissionColor</c>/<c>_EmissionMap</c>/<c>_EMISSION</c> and the game's <c>_EmissiveColor</c>/<c>_EmissiveMap</c>/
    /// <c>_EnableEmissive</c>. The flat surface shader takes <c>ENABLE_SELF_ILLUM</c> (scale the colour by <see cref="SurfaceEmissionPerBrightness"/>
    /// per author brightness); the game's own materials keep <c>ENABLE_EMISSIVE</c>. The emission map is <paramref name="map"/> (a model's own glow mask) or white,
    /// so the glow is even. This is the one emission write of the libraries and packages.
    /// The caller owns <paramref name="material"/> (a copy); a material with neither family throws <c>model-glow-unsupported</c>.</summary>
    public static void Write(Material material, Color emission, Texture? map = null) => Write(material, emission, emission, map);

    /// <summary><see cref="Write(Material, Color, Texture)"/> of an author's glow: <paramref name="srgb"/> at <paramref name="gain"/>, each family's
    /// colour property given its own <see cref="Value"/>.</summary>
    public static void Write(Material material, Color srgb, float gain, Texture? map = null)
        => Write(material, Value(material, EmissionColorName, srgb, gain), Value(material, EmissiveColorName, srgb, gain), map);

    private static void Write(Material material, Color standard, Color game, Texture? map)
    {
        var written = false;
        if (material.HasProperty(EmissionColor))
        {
            material.SetColor(EmissionColor, standard);
            if (material.HasProperty(EmissionMap)) material.SetTexture(EmissionMap, map ?? Texture2D.whiteTexture);
            material.EnableKeyword(EmissionKeyword);
            written = true;
        }
        if (material.HasProperty(EmissiveColor))
        {
            material.SetColor(EmissiveColor, game);
            if (material.HasProperty(EmissiveMap)) material.SetTexture(EmissiveMap, map ?? Texture2D.whiteTexture);
            if (material.HasProperty(EnableEmissive)) material.SetFloat(EnableEmissive, 1f);
            material.EnableKeyword(IsSurface(material) ? SelfIllumKeyword : EmissiveKeyword);
            written = true;
        }
        if (!written) throw new InvalidOperationException("model-glow-unsupported");
    }

    /// <summary>Changes only the colour of <paramref name="material"/>'s glow, on both families' colour properties it has: no map, toggle or
    /// keyword is touched and nothing is allocated, so it runs every frame of an animated glow on a material <see cref="Write(Material, Color, Texture)"/> lit (on one
    /// it did not, the colour waits for the next <see cref="Write(Material, Color, Texture)"/>).</summary>
    public static void SetColour(Material material, Color emission)
    {
        if (material.HasProperty(EmissiveColor)) material.SetColor(EmissiveColor, emission);
        if (material.HasProperty(EmissionColor)) material.SetColor(EmissionColor, emission);
    }

    /// <summary>Switches <paramref name="material"/>'s glow off on both families (the toggle and the keywords <see cref="Write(Material, Color, Texture)"/> sets); its colour
    /// and map stay for a later <see cref="Write(Material, Color, Texture)"/>. The other half of the one emission write; the caller owns the material.</summary>
    public static void Clear(Material material)
    {
        material.DisableKeyword(EmissionKeyword);
        if (material.HasProperty(EnableEmissive)) material.SetFloat(EnableEmissive, 0f);
        material.DisableKeyword(IsSurface(material) ? SelfIllumKeyword : EmissiveKeyword);
    }

    /// <summary>Carries <paramref name="source"/>'s active emission (either family) onto <paramref name="target"/> when it has one, so a
    /// recoloured copy keeps the light strips the game's material had (what the shader gets stays the same when one property is <c>[HDR]</c>
    /// and the other is not, <see cref="IsHdr"/>). A source with no active emission leaves the target as it is.</summary>
    public static void Preserve(Material source, Material target)
    {
        Color colour;
        Texture? map;
        bool hdr;
        if (source.HasProperty(EmissionColor) && source.IsKeywordEnabled(EmissionKeyword))
        {
            colour = source.GetColor(EmissionColor);
            map = source.HasProperty(EmissionMap) ? source.GetTexture(EmissionMap) : null;
            hdr = IsHdr(source, EmissionColorName);
        }
        else if (source.HasProperty(EmissiveColor)
            && (source.IsKeywordEnabled(EmissiveKeyword) || source.IsKeywordEnabled(SelfIllumKeyword))
            && (!source.HasProperty(EnableEmissive) || source.GetFloat(EnableEmissive) != 0f))
        {
            colour = source.GetColor(EmissiveColor);
            map = source.HasProperty(EmissiveMap) ? source.GetTexture(EmissiveMap) : null;
            hdr = IsHdr(source, EmissiveColorName);
        }
        else return;
        var standard = target.HasProperty(EmissionColor);
        var colourId = standard ? EmissionColor : EmissiveColor;
        var mapId = standard ? EmissionMap : EmissiveMap;
        if (!target.HasProperty(colourId)) throw new InvalidOperationException("model-surface-emission-unavailable");
        var targetHdr = IsHdr(target, standard ? EmissionColorName : EmissiveColorName);
        if (hdr && !targetHdr) colour = new Color(ColourSpace.ToSrgb(colour.r), ColourSpace.ToSrgb(colour.g), ColourSpace.ToSrgb(colour.b), colour.a);
        else if (!hdr && targetHdr) colour = new Color(ColourSpace.ToLinear(colour.r), ColourSpace.ToLinear(colour.g), ColourSpace.ToLinear(colour.b), colour.a);
        if (standard && IsSurface(source))
            colour = new Color(colour.r / SurfaceEmissionPerBrightness, colour.g / SurfaceEmissionPerBrightness,
                colour.b / SurfaceEmissionPerBrightness, colour.a);
        target.SetColor(colourId, colour);
        if (target.HasProperty(mapId)) target.SetTexture(mapId, map ?? Texture2D.whiteTexture);
        if (target.HasProperty(EnableEmissive)) target.SetFloat(EnableEmissive, 1f);
        target.EnableKeyword(standard ? EmissionKeyword : SelfIllumKeyword);
    }
}
