using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>A Unity mesh from flat vertex arrays, in Unity's space (left-handed, clockwise front faces, UV origin bottom left): the one
/// place the libraries and packages turn a model file's numbers into a mesh. The caller owns the mesh, and uploads it
/// (<c>UploadMeshData</c>) once it has added what it needs on top (bones).</summary>
public static class MeshArrays
{
    /// <summary>A readable mesh named <paramref name="name"/>: <paramref name="positions"/> xyz per vertex, <paramref name="normals"/> xyz
    /// (null: computed from the faces), <paramref name="uv"/> uv (null: none), <paramref name="tangents"/> xyzw (null: computed from the UVs
    /// when <paramref name="computeTangents"/> and there are UVs), <paramref name="indices"/> triangles, split into
    /// <paramref name="submeshes"/> (start and count in <paramref name="indices"/>; null: one submesh of all). More than 65535 vertices use
    /// 32-bit indices. The lengths are the caller's to have checked.</summary>
    public static Mesh Build(string name, float[] positions, float[]? normals, float[]? uv, float[]? tangents, int[] indices,
        IReadOnlyList<(int Start, int Count)>? submeshes = null, bool computeTangents = true)
    {
        var count = positions.Length / 3;
        var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        var points = new Vector3[count];
        for (var v = 0; v < count; v++) points[v] = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
        mesh.vertices = new Il2CppStructArray<Vector3>(points);
        if (normals != null)
        {
            var n = new Vector3[count];
            for (var v = 0; v < count; v++) n[v] = new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]);
            mesh.normals = new Il2CppStructArray<Vector3>(n);
        }
        if (uv != null)
        {
            var t = new Vector2[count];
            for (var v = 0; v < count; v++) t[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]);
            mesh.uv = new Il2CppStructArray<Vector2>(t);
        }
        if (tangents != null)
        {
            var t = new Vector4[count];
            for (var v = 0; v < count; v++) t[v] = new Vector4(tangents[v * 4], tangents[v * 4 + 1], tangents[v * 4 + 2], tangents[v * 4 + 3]);
            mesh.tangents = new Il2CppStructArray<Vector4>(t);
        }
        if (submeshes == null) mesh.triangles = new Il2CppStructArray<int>(indices);
        else
        {
            mesh.subMeshCount = submeshes.Count;
            for (var i = 0; i < submeshes.Count; i++)
            {
                var run = new int[submeshes[i].Count];
                Array.Copy(indices, submeshes[i].Start, run, 0, run.Length);
                mesh.SetTriangles(new Il2CppStructArray<int>(run), i);
            }
        }
        if (normals == null) mesh.RecalculateNormals();
        // The gear shader reads a normal map against tangents.
        if (tangents == null && uv != null && computeTangents) mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }
}
