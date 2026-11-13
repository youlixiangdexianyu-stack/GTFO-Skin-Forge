using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>PNG or JPEG bytes to a texture, the one decoder the libraries and packages share. The header is read first
/// (<see cref="Png.TrySize"/>, <see cref="Png.TryJpegSize"/>) so a file that declares more than it may is refused before anything
/// allocates, and a texture the game could not decode is destroyed instead of left behind. Main thread only.</summary>
internal static class Images
{
    /// <summary>The texture of <paramref name="image"/>, or null with <paramref name="failure"/> naming why. <paramref name="linear"/> keeps the pixels
    /// as numbers (a metal map) instead of sRGB colour; <paramref name="readable"/> keeps a CPU copy for <c>GetPixels</c>; <paramref name="mipChain"/> builds mips
    /// (call <c>Apply(true, ...)</c> yourself after changing the filter). <paramref name="maxSide"/> caps either side; a non-zero
    /// <paramref name="width"/> x <paramref name="height"/> demands exactly that size. <paramref name="normalMap"/> reads a tangent-space normal map
    /// (OpenGL +Y, x in red) and stores it linear in the layout the game's own normal maps have on its gear shader (DXT5nm: x in alpha, y in
    /// green, red and blue 1), which both of Unity's normal unpackings read alike. <paramref name="compress"/> compresses it to DXT (a quarter
    /// of raw RGBA) when both sides are a multiple of 4, and leaves it raw otherwise. The caller owns the texture and destroys it.</summary>
    public static Texture2D? Decode(byte[] image, out string failure, bool linear = false, bool readable = false, bool mipChain = false, int maxSide = 8192,
        int width = 0, int height = 0, bool normalMap = false, bool compress = false)
    {
        failure = "";
        if (!Png.TrySize(image, out var declaredWidth, out var declaredHeight) && !Png.TryJpegSize(image, out declaredWidth, out declaredHeight))
        { failure = "the file is not a PNG or JPEG"; return null; }
        if (declaredWidth > maxSide || declaredHeight > maxSide) { failure = "the picture is larger than " + maxSide + " pixels on a side"; return null; }
        if (width != 0 && (declaredWidth != width || declaredHeight != height)) { failure = "the picture is not " + width + " x " + height; return null; }
        // Swizzling and compressing need the pixels; the CPU copy goes once they are done.
        var keepPixels = readable || normalMap || compress;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain && !normalMap, linear || normalMap);
        var native = new Il2CppStructArray<byte>(image);
        try
        {
            if (!ImageConversion.LoadImage(texture, native, !keepPixels))
            {
                failure = "the game could not decode the picture";
                UnityEngine.Object.Destroy(texture);
                return null;
            }
        }
        catch (Exception error)
        {
            failure = "the game could not decode the picture: " + error;
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        finally { GC.KeepAlive(native); }
        if (normalMap)
        {
            // LoadImage gives RGB24 for a picture without alpha; the swizzled copy needs an alpha channel.
            var pixels = texture.GetPixels32();
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = new Color32(255, p.g, 255, p.r);
            }
            var swizzled = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, mipChain, true);
            swizzled.SetPixels32(pixels);
            swizzled.Apply(mipChain, false);
            UnityEngine.Object.Destroy(texture);
            texture = swizzled;
        }
        if (compress && texture.width % 4 == 0 && texture.height % 4 == 0) texture.Compress(true);
        if (keepPixels && !readable) texture.Apply(false, true);
        return texture;
    }
}
