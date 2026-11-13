using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForgeVisual;

/// <summary>One material slot's own look inside a <see cref="SurfaceAppearance"/>: a vanilla leaf counts its slots over its renderers in
/// order (the website's <c>selection.materialSlots</c>), an imported model by its descriptor's <c>materials</c>. A member left null keeps
/// the leaf's value.</summary>
public sealed record SurfaceSlot(int Index, float[]? Tint, float[]? GlowColor, float GlowBrightness, bool? Invisible);

/// <summary>
/// The one surface look of every host (the website's <c>ModelPartAppearance</c>): a tint that replaces the surface colour (sRGB, may exceed
/// one), a uniform glow of <see cref="GlowColor"/> x <see cref="GlowBrightness"/> (sRGB), whether it is invisible (true: not drawn, its
/// shadow still cast, as the game's shadow enemies; false: shown, overriding an inner true; null keeps the source), and per-slot overrides. Colours are converted to linear once, when a material is written
/// (<see cref="Glow.Srgb"/>).
/// </summary>
public sealed record SurfaceAppearance(float[]? Tint, float[]? GlowColor, float GlowBrightness, bool? Invisible = null,
    IReadOnlyList<SurfaceSlot>? Slots = null)
{
    /// <summary>The most slots one look may name (a vanilla probe admits at most 32 per renderer).</summary>
    public const int MaximumSlots = 64;

    /// <summary>Only the surface's shadow is drawn (<c>SurfaceLook.SetShadowOnly</c>).</summary>
    public bool ShadowOnly => Invisible == true;

    /// <summary>No material is written: the source stays as it is (an invisible surface keeps its material for the shadow).</summary>
    public bool Empty => Tint == null && GlowColor == null;

    /// <summary>Outer members replace inner members wholesale (zero brightness or a shown surface is an explicit override); outer slots replace
    /// inner slots. The website's <c>nestedModelAppearance</c> is the same rule.</summary>
    public static SurfaceAppearance? Over(SurfaceAppearance? outer, SurfaceAppearance? inner)
        => outer == null ? inner : inner == null ? outer : new(outer.Tint ?? inner.Tint, outer.GlowColor ?? inner.GlowColor,
            outer.GlowColor != null ? outer.GlowBrightness : inner.GlowBrightness, outer.Invisible ?? inner.Invisible, outer.Slots ?? inner.Slots);

    /// <summary>The look of one slot: the slot's own members over this look's; no slots below it.</summary>
    public SurfaceAppearance ForSlot(int index)
    {
        if (Slots != null)
            foreach (var slot in Slots)
                if (slot.Index == index)
                    return new(slot.Tint ?? Tint, slot.GlowColor ?? GlowColor, slot.GlowColor != null ? slot.GlowBrightness : GlowBrightness,
                        slot.Invisible ?? Invisible);
        return Slots == null ? this : new(Tint, GlowColor, GlowBrightness, Invisible);
    }

    /// <summary>A text that two equal looks share, for keying material copies (built at placement, never per frame).</summary>
    public string Key()
    {
        var text = new StringBuilder(64);
        Append(text, Tint, GlowColor, GlowBrightness, Invisible);
        if (Slots != null)
            foreach (var slot in Slots)
            {
                text.Append('|').Append(slot.Index.ToString(CultureInfo.InvariantCulture)).Append(':');
                Append(text, slot.Tint, slot.GlowColor, slot.GlowBrightness, slot.Invisible);
            }
        return text.ToString();
    }

    private static void Append(StringBuilder text, float[]? tint, float[]? glow, float brightness, bool? invisible)
    {
        void Colour(float[]? value)
        {
            if (value == null) { text.Append('-'); return; }
            foreach (var channel in value) text.Append(channel.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }
        Colour(tint);
        text.Append('/');
        Colour(glow);
        if (glow != null) text.Append('x').Append(brightness.ToString("R", CultureInfo.InvariantCulture));
        text.Append('/').Append(invisible == null ? "-" : invisible.Value ? "i" : "s");
    }
}

/// <summary>
/// The one game implementation of a <see cref="SurfaceAppearance"/> (colour, glow, invisible, per slot) on vanilla and imported surfaces.
/// This part is the engine-free arithmetic: splitting a skinned mesh's triangles into surface groups by bone. The material and mesh writes
/// are in the native half.
/// </summary>
public static partial class SurfaceLook
{
    /// <summary>The index of the heaviest bone of one vertex (the first of equal weights), or -1 when it has none.</summary>
    public static int PrimaryBone(int bone0, float weight0, int bone1, float weight1, int bone2, float weight2, int bone3, float weight3)
    {
        int best = -1;
        float heaviest = 0f;
        if (weight0 > heaviest) { heaviest = weight0; best = bone0; }
        if (weight1 > heaviest) { heaviest = weight1; best = bone1; }
        if (weight2 > heaviest) { heaviest = weight2; best = bone2; }
        if (weight3 > heaviest) best = bone3;
        return best;
    }

    /// <summary>The group of every vertex from its heaviest bone; a vertex with no weight, or a bone outside <paramref name="groupOfBone"/>,
    /// is in <paramref name="fallback"/>.</summary>
    public static int[] VertexGroups(int[] primaryBone, int[] groupOfBone, int fallback)
    {
        var groups = new int[primaryBone.Length];
        for (int vertex = 0; vertex < groups.Length; vertex++)
        {
            int bone = primaryBone[vertex];
            groups[vertex] = bone >= 0 && bone < groupOfBone.Length ? groupOfBone[bone] : fallback;
        }
        return groups;
    }

    /// <summary>The triangles of each group, in input order: a triangle goes to the group most of its three vertices belong to (the first
    /// vertex's when all three differ). <paramref name="triangles"/> is a flat list of whole triangles; every one lands in exactly one group.</summary>
    public static int[][] Split(int[] triangles, int[] vertexGroup, int groupCount)
    {
        if (triangles.Length % 3 != 0) throw new ArgumentException("a triangle list holds whole triangles", nameof(triangles));
        var counts = new int[groupCount];
        var owner = new int[triangles.Length / 3];
        for (int triangle = 0; triangle < owner.Length; triangle++)
        {
            int g0 = vertexGroup[triangles[triangle * 3]], g1 = vertexGroup[triangles[triangle * 3 + 1]],
                g2 = vertexGroup[triangles[triangle * 3 + 2]];
            int group = g0 == g1 || g0 == g2 ? g0 : g1 == g2 ? g1 : g0;
            owner[triangle] = group;
            counts[group]++;
        }
        var result = new int[groupCount][];
        for (int group = 0; group < groupCount; group++) result[group] = new int[counts[group] * 3];
        var next = new int[groupCount];
        for (int triangle = 0; triangle < owner.Length; triangle++)
        {
            int group = owner[triangle];
            Array.Copy(triangles, triangle * 3, result[group], next[group], 3);
            next[group] += 3;
        }
        return result;
    }

    /// <summary>A key for one split of one mesh: the group of each bone and which (submesh, group) pairs are left out. Two renderers that
    /// share a mesh and agree on it split it the same way, so the split mesh is made once.</summary>
    public static string SplitKey(int[]? groupOfBone, int groupCount, bool[]? hiddenGroup, bool[]? hiddenSubMesh)
    {
        var text = new StringBuilder(16 + (groupOfBone?.Length ?? 0));
        if (groupOfBone != null) foreach (var group in groupOfBone) text.Append((char)('0' + group));
        text.Append('|').Append(groupCount.ToString(CultureInfo.InvariantCulture));
        if (hiddenGroup != null) for (int group = 0; group < hiddenGroup.Length; group++) if (hiddenGroup[group]) text.Append('g').Append(group);
        if (hiddenSubMesh != null) for (int sub = 0; sub < hiddenSubMesh.Length; sub++) if (hiddenSubMesh[sub]) text.Append('s').Append(sub);
        return text.ToString();
    }
}
