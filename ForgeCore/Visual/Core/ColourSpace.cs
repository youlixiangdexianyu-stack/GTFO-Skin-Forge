using System;

namespace ForgeVisual;

/// <summary>
/// The one sRGB/linear conversion of the libraries and packages (pure, no game types). The game renders in linear space and an author's
/// colour is sRGB. A colour written with <c>Material.SetColor</c> reaches the shader converted from sRGB to linear when the property is a
/// plain Color and unchanged when it is <c>[HDR]</c> (measured on the game's Unity 2019.4.21, linear); <see cref="Glow(float, float, bool)"/>
/// is the value to write for either kind, so a colour is converted exactly once. <c>Glow.Value</c> reads the property's kind from its shader.
/// </summary>
public static class ColourSpace
{
    /// <summary>One sRGB component as linear (the extended transfer: above one stays above one).</summary>
    public static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>One linear component as sRGB, the inverse of <see cref="ToLinear"/>.</summary>
    public static float ToSrgb(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;

    /// <summary>The value to set on a colour property for the sRGB component <paramref name="c"/> at <paramref name="gain"/>, the value
    /// white takes in the property's own units (1 for a plain tint): the shader gets linear(c) times what white gives it, so the hue is
    /// the colour picked and white's brightness is the measured one. <paramref name="hdr"/>: the property is <c>[HDR]</c>.</summary>
    public static float Glow(float c, float gain, bool hdr) => hdr ? ToLinear(c) * gain : ToSrgb(ToLinear(c) * ToLinear(gain));

    /// <summary><see cref="Glow(float, float, bool)"/> for an RGB colour.</summary>
    public static float[] Glow(float[] c, float gain, bool hdr) => new[] { Glow(c[0], gain, hdr), Glow(c[1], gain, hdr), Glow(c[2], gain, hdr) };

    /// <summary>A colour property's value as its colour (sRGB, brightest channel 1) and gain: <see cref="Glow(float[], float, bool)"/> of the
    /// two gives it back.</summary>
    public static (float[] Colour, float Gain) FromGlow(float r, float g, float b, bool hdr)
    {
        float[] lin = hdr ? new[] { r, g, b } : new[] { ToLinear(r), ToLinear(g), ToLinear(b) };
        float k = Math.Max(lin[0], Math.Max(lin[1], lin[2]));
        return k > 0 ? (new[] { ToSrgb(lin[0] / k), ToSrgb(lin[1] / k), ToSrgb(lin[2] / k) }, hdr ? k : ToSrgb(k)) : (new float[3], 0f);
    }
}
