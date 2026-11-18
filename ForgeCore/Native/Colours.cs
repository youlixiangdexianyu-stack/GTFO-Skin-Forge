using UnityEngine;

namespace ForgeCore;

/// <summary>Colours as mods write them in data files and definitions.</summary>
public static class Colours
{
    /// <summary>An opaque colour from <c>0xRRGGBB</c> (the bits above the lowest 24 are ignored).</summary>
    public static Color FromRgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
