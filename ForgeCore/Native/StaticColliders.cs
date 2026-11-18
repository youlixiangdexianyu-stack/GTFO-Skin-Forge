using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeCore;

/// <summary>World collision built at runtime the way a level's own geometry is: a GameObject holding one non-trigger collider and
/// nothing else, on a named physics layer. The game's navigation build takes active, non-trigger box and mesh colliders on
/// <c>LayerManager.MASK_UNITY_NAVMESH_GENERATION</c> layers as walkable sources (read back in game: such a node parented into a level
/// before its navigation build carries navigation nodes; the player build has no static flag to set). A mesh comes from flat geometry
/// or from a vanilla MeshCollider found again in a loaded prefab, checked against the name and vertex count the caller recorded.
/// Main thread only.</summary>
public static class StaticColliders
{
    /// <summary>The layer index of a TagManager layer name; throws <c>static-collider-layer-missing</c> for a name the game does not have.</summary>
    public static int Layer(string name)
    {
        var layer = LayerMask.NameToLayer(name);
        return layer >= 0 ? layer : throw new InvalidOperationException("static-collider-layer-missing: " + name);
    }

    /// <summary>True when the navigation build takes box and mesh colliders on <paramref name="layer"/> as sources.</summary>
    public static bool Navigable(int layer) => (LayerManager.MASK_UNITY_NAVMESH_GENERATION & (1 << layer)) != 0;

    /// <summary>A new node under <paramref name="parent"/> with the local pose given; give it one collider below.</summary>
    public static GameObject Node(Transform parent, string name, int layer, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var node = new GameObject(name) { layer = layer };
        node.transform.SetParent(parent, false);
        node.transform.localPosition = position; node.transform.localRotation = rotation; node.transform.localScale = scale;
        return node;
    }

    public static void Box(GameObject node, Vector3 center, Vector3 size)
    {
        var box = node.AddComponent<BoxCollider>(); box.isTrigger = false; box.center = center; box.size = size;
    }

    public static void Sphere(GameObject node, Vector3 center, float radius)
    {
        var sphere = node.AddComponent<SphereCollider>(); sphere.isTrigger = false; sphere.center = center; sphere.radius = radius;
    }

    /// <summary>A capsule whose axis <c>direction</c> is 0 x, 1 y or 2 z.</summary>
    public static void Capsule(GameObject node, Vector3 center, float radius, float height, int direction)
    {
        var capsule = node.AddComponent<CapsuleCollider>(); capsule.isTrigger = false;
        capsule.center = center; capsule.radius = radius; capsule.height = height; capsule.direction = direction;
    }

    public static void MeshCollider(GameObject node, Mesh mesh)
    {
        var collider = node.AddComponent<MeshCollider>(); collider.isTrigger = false; collider.convex = false; collider.sharedMesh = mesh;
    }

    /// <summary>A collision mesh from flat positions (x, y, z per vertex) and triangle indices; throws <c>static-collider-mesh-invalid</c>
    /// for geometry Unity cannot cook (an incomplete vertex or triangle, an index out of range, a non-finite position).</summary>
    public static Mesh Mesh(string name, float[] positions, int[] indices)
    {
        int count = positions.Length / 3;
        if (positions.Length == 0 || positions.Length % 3 != 0 || indices.Length == 0 || indices.Length % 3 != 0)
            throw new InvalidOperationException("static-collider-mesh-invalid: " + name);
        var vertices = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new InvalidOperationException("static-collider-mesh-invalid: " + name);
            vertices[i] = new Vector3(x, y, z);
        }
        foreach (var index in indices) if ((uint)index >= (uint)count) throw new InvalidOperationException("static-collider-mesh-invalid: " + name);
        var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.vertices = vertices;
        mesh.triangles = indices;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>The mesh of the MeshCollider on <paramref name="node"/>, when it is <paramref name="name"/> with <paramref name="vertices"/> vertices.</summary>
    public static Mesh? ColliderMesh(Transform node, string name, int vertices)
    {
        var collider = node.GetComponent<MeshCollider>();
        return collider == null ? null : Matching(collider.sharedMesh, name, vertices);
    }

    private static Mesh? Matching(Mesh? mesh, string name, int vertices) => mesh != null && mesh.name == name && mesh.vertexCount == vertices ? mesh : null;
}
