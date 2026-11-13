using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>One mesh drawn at <see cref="Local"/> (the anchor's frame) with its sub-mesh materials; <see cref="ShadowOnly"/> marks sub-meshes drawn as a shadow alone.</summary>
public readonly record struct InstancedDraw(Mesh Mesh, Material?[] Materials, Matrix4x4 Local, bool[]? ShadowOnly = null);

/// <summary>
/// Draws the instanced render features of placed models (grass, vines, tentacles) in batches, the way the game's own
/// area batches do: per mesh and material set the instance matrices are split into chunks of at most 1023 (the
/// Graphics.DrawMeshInstanced limit), each chunk is frustum-culled by its bounding box and submitted once per sub-mesh
/// with shadows off, or as a shadow alone for an invisible submesh. No GameObject exists per instance. The game's own batch path cannot take a placed feature: its
/// instance poses point at transforms of the source room (absent from a placed copy) and its area batches are built
/// once while the level is generated. Matrices live in pre-allocated arrays; a frame allocates nothing. Imported models draw through the same
/// groups without instancing: their gear and surface shaders have no instancing variant, so each matrix is one Graphics.DrawMesh.
/// </summary>
public static class InstancedMeshBatches
{
    /// <summary>Graphics.DrawMeshInstanced accepts at most this many matrices per call.</summary>
    public const int ChunkSize = 1023;

    private sealed class Chunk
    {
        internal readonly Il2CppStructArray<Matrix4x4> World = new(ChunkSize);
        internal Matrix4x4[] Local = Array.Empty<Matrix4x4>();
        internal int Count;
        internal Bounds LocalBounds;
        internal Bounds WorldBounds;
    }

    private sealed class Group
    {
        internal Transform Anchor = null!;
        internal Mesh Mesh = null!;
        internal Material?[] Materials = Array.Empty<Material?>();
        internal bool[]? ShadowOnly;
        internal int SubMeshes;
        // Null: the anchor's layer, read each frame (a gun model moves between the world and the first-person layer).
        internal int? Layer;
        internal Material?[]? FirstPerson;
        // Whether the anchor is on the local player's first-person item, read again whenever the anchor's layer changes (a gun is first person only
        // from its wield on, and the anchor is parented after it registers). The game draws a first-person item with its first-person material
        // keyword, whatever its layer.
        internal bool OnFirstPersonItem;
        internal ItemEquippable? Item;
        internal int SeenLayer = -1;
        // False: the materials' shader has no instancing variant (the game's gear and surface shaders an imported model takes), so each
        // matrix is drawn alone; DrawMeshInstanced draws nothing with such a shader.
        internal bool Instanced;
        internal Chunk[] Chunks = Array.Empty<Chunk>();
        internal Matrix4x4 Last;
        internal bool HasLast;
        // Read once: every gameObject read makes a new interop wrapper.
        internal GameObject? AnchorObject;
    }

    private static readonly List<Group> Groups = new();
    private static readonly Il2CppStructArray<Plane> Planes = new(6);
    private static Camera? _camera;

    /// <summary>
    /// Registers the draws of one placed leaf; their matrices are in <paramref name="anchor"/>'s local frame. The groups end
    /// when the anchor is destroyed. They draw on <paramref name="layer"/>, or on the anchor's current layer when none is given.
    /// <paramref name="instanced"/> false draws each matrix alone, lit and shadowed like a renderer, for materials whose shader cannot instance.
    /// </summary>
    public static void Register(Transform anchor, List<InstancedDraw> draws, int? layer = null, bool instanced = true)
    {
        var byKey = new Dictionary<(IntPtr, Material?[], bool[]?), List<Matrix4x4>>();
        foreach (var draw in draws)
        {
            if (draw.Mesh == null) continue;
            var key = (draw.Mesh.Pointer, draw.Materials, draw.ShadowOnly);
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<Matrix4x4>();
            list.Add(draw.Local);
        }
        foreach (var draw in draws)
        {
            if (draw.Mesh == null || !byKey.Remove((draw.Mesh.Pointer, draw.Materials, draw.ShadowOnly), out var matrices)) continue;
            if (instanced) foreach (var material in draw.Materials) if (material != null) material.enableInstancing = true;
            // Slabs along one axis keep each chunk's bounding box tight enough for the culling to drop whole chunks.
            matrices.Sort((a, b) => a.m03.CompareTo(b.m03));
            var chunks = new Chunk[(matrices.Count + ChunkSize - 1) / ChunkSize];
            for (int index = 0; index < chunks.Length; index++)
            {
                var chunk = chunks[index] = new Chunk();
                chunk.Count = Math.Min(ChunkSize, matrices.Count - index * ChunkSize);
                chunk.Local = new Matrix4x4[chunk.Count];
                matrices.CopyTo(index * ChunkSize, chunk.Local, 0, chunk.Count);
                chunk.LocalBounds = Enclose(draw.Mesh.bounds, chunk.Local, chunk.Count, Matrix4x4.identity);
                for (int slot = 0; slot < chunk.Count; slot++) chunk.World[slot] = chunk.Local[slot];
            }
            lock (Groups)
                Groups.Add(new Group
                {
                    Anchor = anchor,
                    Mesh = draw.Mesh,
                    Materials = draw.Materials,
                    ShadowOnly = draw.ShadowOnly,
                    Chunks = chunks,
                    SubMeshes = Math.Min(draw.Mesh.subMeshCount, draw.Materials.Length),
                    Layer = layer,
                    Instanced = instanced,
                });
        }
    }

    /// <summary>Submits every live group; run once per frame after transforms settled.</summary>
    public static void Draw()
    {
        lock (Groups)
        {
            if (Groups.Count == 0) return;
            if (_camera == null) _camera = Camera.main;
            var camera = _camera;
            if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, Planes);
            for (int index = Groups.Count - 1; index >= 0; index--)
            {
                var group = Groups[index];
                // The native pointer, not the Unity == overload (a call into the game for every group every frame).
                if (ForgeCore.NativeObjects.Gone(group.Anchor)) { Groups.RemoveAt(index); continue; }
                var anchorObject = group.AnchorObject ??= group.Anchor.gameObject;
                if (!anchorObject.activeInHierarchy) continue;
                var layer = group.Layer ?? anchorObject.layer;
                // On the local player's first-person item the item camera draws it, with the first-person copies a gun's own meshes take (made once).
                // A model hung on a part of the gun (a magazine) keeps its world layer until the wield: the gun's item decides, read every frame.
                var item = group.Item;
                if (layer != group.SeenLayer || (item != null && ForgeCore.NativeObjects.Gone(item)))
                {
                    group.SeenLayer = layer;
                    item = group.Item = group.Anchor.GetComponentInParent<ItemEquippable>();
                    group.OnFirstPersonItem = item != null && item.IsFirstPerson;
                    if (item != null) ReportView(group, layer);
                }
                else if (item != null)
                {
                    var held = item.IsFirstPerson;
                    if (held != group.OnFirstPersonItem) { group.OnFirstPersonItem = held; ReportView(group, held && group.Layer == null ? GunMount.FirstPersonLayer : layer); }
                }
                if (group.OnFirstPersonItem && group.Layer == null) layer = GunMount.FirstPersonLayer;
                bool firstPerson = group.OnFirstPersonItem || layer == GunMount.FirstPersonLayer;
                var materials = firstPerson ? group.FirstPerson ??= Array.ConvertAll(group.Materials, LitMaterial.FirstPerson) : group.Materials;
                var anchor = group.Anchor.localToWorldMatrix;
                bool moved = !group.HasLast || !ForgeCore.Vec.Same(anchor, group.Last);
                if (moved) { group.Last = anchor; group.HasLast = true; }
                foreach (var chunk in group.Chunks)
                {
                    if (moved)
                    {
                        for (int slot = 0; slot < chunk.Count; slot++) chunk.World[slot] = ForgeCore.Vec.Multiply(anchor, chunk.Local[slot]);
                        chunk.WorldBounds = Transformed(chunk.LocalBounds, anchor);
                    }
                    if (camera != null && !GeometryUtility.TestPlanesAABB(Planes, chunk.WorldBounds)) continue;
                    for (int sub = 0; sub < group.SubMeshes; sub++)
                    {
                        var material = materials[sub];
                        if (material == null) continue;
                        bool shadowOnly = group.ShadowOnly?[sub] == true;
                        if (group.Instanced)
                        {
                            Graphics.DrawMeshInstanced(group.Mesh, sub, material, chunk.World, chunk.Count, null,
                                shadowOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off, false, layer);
                            continue;
                        }
                        // As a renderer: shadows in the world, none in the hands (the game's first-person guns cast none).
                        bool world = !firstPerson;
                        var shadows = shadowOnly ? ShadowCastingMode.ShadowsOnly : world ? ShadowCastingMode.On : ShadowCastingMode.Off;
                        for (int slot = 0; slot < chunk.Count; slot++)
                            Graphics.DrawMesh(group.Mesh, chunk.World[slot], material, layer, null, sub, null, shadows, world);
                    }
                }
            }
        }
    }

    public static void Clear() { lock (Groups) Groups.Clear(); }

    /// <summary>Where a first-person view change of a gun's model is reported (one line per change, not one per placed model); null reports nothing.</summary>
    public static Action<string>? ViewReport { get; set; }

    private static void ReportView(Group group, int layer)
        => ViewReport?.Invoke("model-draw-view: anchor=" + group.Anchor.name + " parent=" + (group.Anchor.parent != null ? group.Anchor.parent.name : "none")
            + " layer=" + layer + " firstPerson=" + group.OnFirstPersonItem);

    /// <summary>The box that holds <paramref name="mesh"/> under each of the first <paramref name="count"/> matrices, then <paramref name="outer"/>.</summary>
    private static Bounds Enclose(Bounds mesh, Matrix4x4[] matrices, int count, Matrix4x4 outer)
    {
        Bounds result = default;
        int boxes = 0;
        for (int slot = 0; slot < count; slot++) Models.Encapsulate(ref result, ref boxes, ForgeCore.Vec.Multiply(outer, matrices[slot]), mesh);
        return result;
    }

    private static Bounds Transformed(Bounds local, Matrix4x4 matrix)
    {
        Bounds result = default;
        int boxes = 0;
        Models.Encapsulate(ref result, ref boxes, matrix, local);
        return result;
    }
}
