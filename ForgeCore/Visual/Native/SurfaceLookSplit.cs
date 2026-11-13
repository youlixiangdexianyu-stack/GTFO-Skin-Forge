using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeVisual;

/// <summary>A mesh split by <see cref="SurfaceLook.SplitByBones"/>: the new mesh and, for each of its submeshes, the source submesh it
/// came from (the material slot it keeps) and the surface group it holds.</summary>
public sealed class SurfaceSplit
{
    internal SurfaceSplit(Mesh source, Mesh result, int[] originSubMesh, int[] group)
    { Source = source; Result = result; OriginSubMesh = originSubMesh; Group = group; }

    public Mesh Source { get; }
    /// <summary>The caller owns it (destroy it with the surfaces that draw it, or through <see cref="Destroy"/>).</summary>
    public Mesh Result { get; }
    public int[] OriginSubMesh { get; }
    public int[] Group { get; }
    /// <summary>The shadow-only twin <see cref="SurfaceLook.LeaveOutSlots"/> made for the invisible slots, and its mesh (another split).</summary>
    public SurfaceSplit? Shadow { get; internal set; }
    public GameObject? ShadowObject { get; internal set; }

    /// <summary>Destroys the split mesh and, when there is one, the shadow twin and its mesh.</summary>
    public void Destroy()
    {
        if (ShadowObject != null) Object.Destroy(ShadowObject);
        Shadow?.Destroy();
        if (Result != null) Object.Destroy(Result);
    }
}

public static partial class SurfaceLook
{
    /// <summary>
    /// A copy of <paramref name="mesh"/> whose triangles are sorted into surface groups, one submesh per non-empty (source submesh, group),
    /// leaving out the pairs this mesh does not draw (invisible or removed). A vertex belongs to the group of its heaviest bone
    /// (<paramref name="groupOfBone"/> per renderer bone; a bone outside it, or no weight, is <paramref name="fallbackGroup"/>); with
    /// <paramref name="groupOfBone"/> null every vertex is group 0 and only whole submeshes (material slots) are left out. Vertices, bones,
    /// bind poses and bounds are the source's own. Null (and <paramref name="reason"/>) when the mesh cannot be read or split, or nothing
    /// is left to draw.
    /// </summary>
    public static SurfaceSplit? SplitByBones(Mesh mesh, int[]? groupOfBone, int groupCount, int fallbackGroup, bool[]? hiddenGroup,
        bool[]? hiddenSubMesh, string name, out string reason)
    {
        reason = string.Empty;
        if (!mesh.isReadable) { reason = name + " mesh is not readable"; return null; }
        int subMeshes = mesh.subMeshCount;
        for (int sub = 0; sub < subMeshes; sub++)
            // Mesh.GetTopology is a managed interop body whose out-of-range message formats a string through a method the game does not
            // have (MissingMethodException); GetSubMesh is the native call.
            if (mesh.GetSubMesh(sub).topology != MeshTopology.Triangles || mesh.GetBaseVertex(sub) != 0)
            { reason = name + " submesh " + sub + " is not a plain triangle list"; return null; }
        int vertexCount = mesh.vertexCount;
        int[] groups;
        if (groupOfBone == null) groups = new int[vertexCount];
        else
        {
            var weights = mesh.boneWeights;
            if (weights == null || weights.Length == 0) { reason = name + " has no bone weights"; return null; }
            var primary = new int[weights.Length];
            for (int vertex = 0; vertex < primary.Length; vertex++)
            {
                var weight = weights[vertex];
                primary[vertex] = PrimaryBone(weight.boneIndex0, weight.weight0, weight.boneIndex1, weight.weight1,
                    weight.boneIndex2, weight.weight2, weight.boneIndex3, weight.weight3);
            }
            groups = VertexGroups(primary, groupOfBone, fallbackGroup);
        }
        var origin = new List<int>();
        var group = new List<int>();
        var parts = new List<int[]>();
        for (int sub = 0; sub < subMeshes; sub++)
        {
            if (hiddenSubMesh != null && sub < hiddenSubMesh.Length && hiddenSubMesh[sub]) continue;
            var read = mesh.GetTriangles(sub);
            var triangles = new int[read.Length];
            for (int index = 0; index < triangles.Length; index++)
            {
                int vertex = read[index];
                if (vertex < 0 || vertex >= groups.Length) { reason = name + " submesh " + sub + " names vertex " + vertex + " of " + groups.Length; return null; }
                triangles[index] = vertex;
            }
            if (triangles.Length % 3 != 0) { reason = name + " submesh " + sub + " is not whole triangles"; return null; }
            var sorted = Split(triangles, groups, groupCount);
            for (int index = 0; index < sorted.Length; index++)
            {
                if (sorted[index].Length == 0 || hiddenGroup != null && index < hiddenGroup.Length && hiddenGroup[index]) continue;
                origin.Add(sub);
                group.Add(index);
                parts.Add(sorted[index]);
            }
        }
        if (parts.Count == 0) { reason = name + " has nothing left to draw"; return null; }
        var copy = Object.Instantiate<Mesh>(mesh);
        copy.subMeshCount = parts.Count;
        for (int index = 0; index < parts.Count; index++)
            copy.SetTriangles(new Il2CppStructArray<int>(parts[index]), index, false, 0);
        copy.bounds = mesh.bounds;
        return new SurfaceSplit(mesh, copy, origin.ToArray(), group.ToArray());
    }

    /// <summary>Draws <paramref name="renderer"/> with its <paramref name="hiddenSlot"/> material slots invisible: its mesh becomes a
    /// split copy holding only the shown submeshes, and a shadow-only twin (<see cref="ShadowTwin"/>) draws the invisible ones, so their
    /// shadow stays. Null (and <paramref name="reason"/>) when the renderer has no readable mesh that matches its slots; nothing is changed
    /// then. The caller owns the split (<see cref="SurfaceSplit.Destroy"/>) and puts the source back with <see cref="PutBack"/> when the
    /// renderer is not its own.</summary>
    public static SurfaceSplit? LeaveOutSlots(Renderer renderer, Material[] materials, bool[] hiddenSlot, out string reason)
    {
        var skin = renderer.TryCast<SkinnedMeshRenderer>();
        var filter = skin == null ? renderer.GetComponent<MeshFilter>() : null;
        var mesh = skin != null ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
        if (mesh == null) { reason = "no mesh"; return null; }
        if (mesh.subMeshCount != materials.Length) { reason = materials.Length + " slots for " + mesh.subMeshCount + " submeshes"; return null; }
        var split = SplitByBones(mesh, null, 1, 0, null, hiddenSlot, renderer.name, out reason);
        if (split == null) return null;
        var shownSlot = new bool[hiddenSlot.Length];
        for (int slot = 0; slot < shownSlot.Length; slot++) shownSlot[slot] = !hiddenSlot[slot];
        var shadow = SplitByBones(mesh, null, 1, 0, null, shownSlot, renderer.name, out reason);
        if (shadow == null) { split.Destroy(); return null; }
        split.Shadow = shadow;
        split.ShadowObject = ShadowTwin(renderer, shadow, materials);
        var drawn = new Material[split.OriginSubMesh.Length];
        for (int index = 0; index < drawn.Length; index++) drawn[index] = materials[split.OriginSubMesh[index]];
        if (skin != null) skin.sharedMesh = split.Result;
        else filter!.sharedMesh = split.Result;
        renderer.sharedMaterials = drawn;
        return split;
    }

    /// <summary>A shadow-only copy of <paramref name="renderer"/> drawing <paramref name="split"/> (the invisible part of its mesh) with
    /// the source slots' <paramref name="materials"/>: a child object on the renderer's layer, skinned to the same bones when the renderer
    /// is skinned. The caller destroys it (it also goes with the renderer).</summary>
    public static GameObject ShadowTwin(Renderer renderer, SurfaceSplit split, Material[] materials)
    {
        var twin = new GameObject(renderer.name + " (Forge shadow)") { layer = renderer.gameObject.layer };
        twin.transform.SetParent(renderer.transform, false);
        var slots = new Material[split.OriginSubMesh.Length];
        for (int index = 0; index < slots.Length; index++) slots[index] = materials[split.OriginSubMesh[index]];
        Renderer drawn;
        if (renderer.TryCast<SkinnedMeshRenderer>() is { } skin)
        {
            var copy = twin.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = split.Result;
            copy.bones = skin.bones;
            copy.rootBone = skin.rootBone;
            copy.localBounds = skin.localBounds;
            drawn = copy;
        }
        else
        {
            twin.AddComponent<MeshFilter>().sharedMesh = split.Result;
            drawn = twin.AddComponent<MeshRenderer>();
        }
        drawn.sharedMaterials = slots;
        SetShadowOnly(drawn, true);
        return twin;
    }

    /// <summary>Gives <paramref name="renderer"/> back the source mesh of <paramref name="split"/> and drops its shadow twin (its materials
    /// are the caller's to restore).</summary>
    public static void PutBack(Renderer renderer, SurfaceSplit split)
    {
        if (split.ShadowObject != null) { Object.Destroy(split.ShadowObject); split.ShadowObject = null; }
        var skin = renderer.TryCast<SkinnedMeshRenderer>();
        if (skin != null) { if (skin.sharedMesh == split.Result) skin.sharedMesh = split.Source; return; }
        var filter = renderer.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh == split.Result) filter.sharedMesh = split.Source;
    }

    /// <summary>Whether <paramref name="renderer"/> draws at all, shadow included (a removed part); the same switch as
    /// <see cref="Models.HideRenderers"/>. An invisible part is <see cref="SetShadowOnly"/> instead.</summary>
    public static void SetDrawn(Renderer renderer, bool drawn) => renderer.forceRenderingOff = !drawn;
}
