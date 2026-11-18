using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppArrays = ForgeCore.Il2CppArrays;

namespace ForgeUI;

/// <summary>
/// Copies of a picture for drawing on dark UI, made once per picture and look; a picture that cannot be copied is logged once and not
/// drawn. The HUD copy is one colour: the game's weapon icons are dark grey renders that a tint can only darken further, so the copy
/// keeps the picture's shape (its alpha) and its light and dark, drawn in white. The menu copy (lobby cells, picker rows and details)
/// keeps the picture's own colours, so the player sees what the model is, and is cropped to the drawn shape so every picture fills
/// its cell alike.
/// </summary>
internal static class IconSilhouette
{
    /// <summary>The HUD look: white, shading kept from <see cref="HudFloor"/> to 1, the picture's own margins kept.</summary>
    private static readonly Look Hud = new(true, false, "bright");

    /// <summary>The menu look: the picture's own colours, cropped to the drawn shape.</summary>
    private static readonly Look Menu = new(false, true, "menu");

    private const float HudFloor = 0.6f;

    private sealed record Look(bool White, bool Trim, string Name)
    {
        internal readonly Dictionary<IntPtr, (Sprite Source, Sprite? Made)> Made = new();
    }

    internal static Sprite? Of(Sprite source) => Get(source, Hud);

    /// <summary>The menu copy: the picture's own colours, cropped to its drawn shape.</summary>
    internal static Sprite? Trimmed(Sprite source) => Get(source, Menu);

    private static Sprite? Get(Sprite source, Look look)
    {
        // The same live picture: its copy. A destroyed one whose address was reused is copied again.
        if (look.Made.TryGetValue(source.Pointer, out var entry) && entry.Source != null && entry.Source.Pointer == source.Pointer) return entry.Made;
        Sprite? made;
        try
        {
            made = Build(source, look);
        }
        catch (Exception error)
        {
            Plugin.Error("ui-icon-could-not-be-copied", "Icon '" + source.name + "' could not be copied (" + look.Name + ") and is not drawn: " + error);
            made = null;
        }
        look.Made[source.Pointer] = (source, made);
        return made;
    }

    private static int Light(Color32 p) => Math.Max((int)p.r, Math.Max(p.g, p.b));

    private static Sprite Build(Sprite source, Look look)
    {
        var rect = source.textureRect;
        int x = (int)rect.x, y = (int)rect.y, w = Math.Max(1, (int)rect.Width()), h = Math.Max(1, (int)rect.Height());
        var texture = source.texture;
        // The game's icon textures are not readable from script: copy through a render texture. The menu copy keeps the colours, so its
        // render texture stores them as the picture does (sRGB); the HUD copy keeps only light and dark and is read raw.
        var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, look.White ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D copy;
        try
        {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            copy = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "ForgeUI " + look.Name + " " + source.name, hideFlags = HideFlags.HideAndDontSave };
            copy.ReadPixels(new Rect(x, y, w, h), 0, 0, false);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
        var pixels = Il2CppArrays.ToManaged(copy.GetPixels32());
        var brightest = 1;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.a <= 8) continue;
            brightest = Math.Max(brightest, Light(p));
            int px = i % w, py = i / w;
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }
        if (look.White)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                var level = (byte)Math.Round(255f * (HudFloor + (1f - HudFloor) * Math.Clamp(Light(p) / (float)brightest, 0f, 1f)));
                pixels[i] = new Color32(level, level, level, p.a);
            }
            Il2CppArrays.Upload(copy, pixels);
        }
        copy.Apply(false, true);
        var area = look.Trim && maxX >= minX ? new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1) : new Rect(0, 0, w, h);
        var sprite = Sprite.Create(copy, area, new Vector2(0.5f, 0.5f), source.pixelsPerUnit);
        sprite.name = copy.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
