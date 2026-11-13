using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The lit material of the world models this library builds: the game's own gear shader (<see cref="ShaderName"/>) with a base-colour
/// map and a metal-and-smoothness map (<see cref="SetRelief"/> adds a normal map, <c>Glow.Write</c> emission). The game's clustered renderer lights only through
/// its own effect lights and its own shaders (a Unity light lights nothing), so models that must sit in the game's light use this shader, and
/// model bundles with a profile and imported models are rebuilt through the same call. The caller owns what it returns.
/// </summary>
public static class LitMaterial
{
    /// <summary>The game's gear shader.</summary>
    public const string ShaderName = "Cell/Player/CustomGearShader";

    private static readonly int MainTex = Shader.PropertyToID("_MainTex"), MetallicMap = Shader.PropertyToID("_MetallicGlossMap"),
        ShadingType = Shader.PropertyToID("_ShadingType"), IsGear = Shader.PropertyToID("_IsCustomGearMaterial"),
        Cutoff = Shader.PropertyToID("_Cutoff"), BumpMap = Shader.PropertyToID("_BumpMap"), SupportsFps = Shader.PropertyToID("_SupportsFPSRendering");

    /// <summary>A material of the gear shader showing <paramref name="albedo"/> (sRGB) with <paramref name="metallicSmooth"/> (linear: metal in red,
    /// smoothness in green), or null while the game has not loaded the shader (it appears once a level has been built).
    /// <paramref name="cutout"/> alpha-tests the albedo's alpha at <paramref name="cutoff"/>; a shader without that returns null and sets
    /// <paramref name="why"/>.</summary>
    public static Material? Create(string name, Texture albedo, Texture metallicSmooth, bool cutout, float cutoff, out string? why)
    {
        why = null;
        var shader = Shader.Find(ShaderName);
        if (shader == null) { why = "the game's gear shader is not loaded yet"; return null; }
        var material = new Material(shader) { name = name };
        if (cutout)
        {
            if (!material.HasProperty(Cutoff)) { UnityEngine.Object.Destroy(material); why = "the gear shader has no cut-out"; return null; }
            material.SetFloat(Cutoff, cutoff);
            material.EnableKeyword("_ALPHATEST_ON");
        }
        material.SetTexture(MainTex, albedo);
        material.SetTexture(MetallicMap, metallicSmooth);
        material.SetFloat(ShadingType, 4f);
        material.SetFloat(IsGear, 1f);
        return material;
    }

    /// <summary>The game's glass shader.</summary>
    public const string GlassShaderName = "GTFO/Glass";

    /// <summary>A see-through pane of the game's glass shader tinted by <paramref name="tint"/> (alpha is its opacity), drawn after the opaque surfaces, or
    /// null while the game has not loaded the shader. The shader adds its own reflection on top of the tint, so a mirror finish (smoothness .85) turned
    /// a 5% tint into an opaque white pane: the smoothness stays low for a faint sheen, with no extra opacity at grazing angles. The caller owns it.</summary>
    public static Material? Glass(string name, Color tint)
    {
        var shader = Shader.Find(GlassShaderName);
        if (shader == null) return null;
        var glass = new Material(shader) { name = name, color = tint, renderQueue = 3000 };
        glass.SetFloat(SupportsFps, 1f);
        glass.SetFloat("_Smoothness", .1f); glass.SetFloat("_Metallic", 0f); glass.SetFloat("_FresnelAlpha", 0f);
        return glass;
    }

    /// <summary>A copy of <paramref name="world"/> drawn with the game's first-person item camera, as the game's own guns are in the hands (a gun on the first-person layer without it stands apart from the hands, drawn with the world camera). The caller owns it.</summary>
    public static Material FirstPersonCopy(Material world)
    {
        world.SetFloat(SupportsFps, 1f);
        var copy = new Material(world) { name = world.name + " (first person)" };
        copy.EnableKeyword(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING);
        return copy;
    }

    /// <summary>What a copy of a first-person surface draws in the world: <paramref name="material"/> itself unless it is drawn with the item camera, otherwise one kept copy without that (the library's, shared).</summary>
    internal static Material ForWorld(Material material)
    {
        if (!material.IsKeywordEnabled(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING)) return material;
        return KeptCopy(WorldCopies, material, static source =>
        {
            var copy = new Material(source) { name = source.name + " (world)" };
            copy.DisableKeyword(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING);
            return copy;
        });
    }

    private static readonly Dictionary<IntPtr, (Material Source, Material Copy)> WorldCopies = new();

    /// <summary>When a level ends: destroys the kept copies whose source material is gone, so copies of a level's own materials do not pile up.</summary>
    internal static void SweepDead()
    {
        Sweep(WorldCopies);
        Sweep(FirstPersonCopies);
    }

    private static void Sweep(Dictionary<IntPtr, (Material Source, Material Copy)> cache)
    {
        List<IntPtr>? dead = null;
        foreach (var (pointer, entry) in cache)
            if (entry.Source == null) (dead ??= new List<IntPtr>()).Add(pointer);
        if (dead == null) return;
        foreach (var pointer in dead)
        {
            var copy = cache[pointer].Copy;
            cache.Remove(pointer);
            if (copy != null) UnityEngine.Object.Destroy(copy);
        }
    }
    // Shared materials (bundles, imported models) live as long as the game or the level; each has one first-person copy.
    private static readonly Dictionary<IntPtr, (Material Source, Material Copy)> FirstPersonCopies = new();

    /// <summary>The library's one kept <see cref="FirstPersonCopy"/> of <paramref name="world"/> (shared: never destroy or change it); null stays null
    /// (an empty material slot).</summary>
    public static Material? FirstPerson(Material? world) => world == null ? null : KeptCopy(FirstPersonCopies, world, FirstPersonCopy);

    /// <summary>One kept copy of <paramref name="source"/> in <paramref name="cache"/>, made by <paramref name="make"/>. A destroyed source (a level's
    /// unused-asset sweep, an unloaded bundle) can leave its address to another material: an entry counts only while its source and its copy are
    /// both alive, otherwise the copy is made again.</summary>
    internal static Material KeptCopy(Dictionary<IntPtr, (Material Source, Material Copy)> cache, Material source, Func<Material, Material> make)
    {
        var kept = cache.TryGetValue(source.Pointer, out var entry);
        if (kept && entry.Source != null && entry.Copy != null) return entry.Copy;
        // The entry belongs to a destroyed source (its address now names this material): its copy is no one's any more.
        if (kept && entry.Copy != null) UnityEngine.Object.Destroy(entry.Copy);
        var copy = ForgeCore.Assets.Keep(make(source));
        cache[source.Pointer] = (source, copy);
        return copy;
    }

    /// <summary>Gives <paramref name="material"/> a tangent-space normal map (OpenGL +Y) in the layout the game's own gear normal maps have,
    /// DXT5nm (x in alpha, y in green, red 1): decode it with <c>ImageTextures</c>' normal-map mode. That layout is read right by both of
    /// Unity's normal unpackings (the DXT5nm one reads alpha and green, the RG-or-AG one red times alpha and green), a plain RGB map only by the
    /// second. The shader has no strength setting and no keyword for it, so the map is always read; the mesh needs tangents.</summary>
    public static void SetRelief(Material material, Texture normal) => material.SetTexture(BumpMap, normal);

    /// <summary>The `_EmissiveColor` multiple that shows a linear emission of 1 (glTF's full-brightness emission) as its own colour. The gear
    /// shader's self-illumination gain is very large: in game (S8-MESH, orange 1 / 0.35 / 0 in an unlit room) a multiple of 0.01 keeps the colour,
    /// 0.03 already shifts it to yellow and 0.1 or more washes it out to white.</summary>
    public const float EmissionPerUnit = 0.01f;

}
