using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>A look on the local player's body drawn the way the game draws that body in first person: the game turns the body's head
/// and arm parts off and leaves the chest, waist and legs, so looking down shows them, and the first-person arms are the only arms.
/// <see cref="Models.LeaveOutInFirstPerson"/> gives the look's renderer a copy of its mesh without the triangles skinned at least half to the
/// given bones and everything below them (the head with its hair, the upper arms down to the fingers); a vertex at the seam keeps its own
/// weights, so the shoulders and neck end where the cut runs. <see cref="Models.ShowOwnBody"/> puts the whole mesh back while it shows the
/// body from outside (a dance seen from behind) and the cut one after. The shadow is cast by what is drawn, as the game's is.
/// <see cref="Dispose"/> puts the whole mesh back for good and destroys the copy.</summary>
public sealed class OwnBodyCut : IDisposable
{
    private static readonly List<OwnBodyCut> Live = new();
    private readonly SkinnedMeshRenderer _renderer;
    private static int _views; // own-body views showing: the looks are whole meanwhile
    private readonly Mesh _whole, _cut;

    private OwnBodyCut(SkinnedMeshRenderer renderer, Mesh whole, Mesh cut)
    {
        _renderer = renderer;
        _whole = whole;
        _cut = cut;
        if (_views == 0) Swap(_whole, _cut);
        Live.Add(this);
    }

    internal static OwnBodyCut? Make(SkinnedMeshRenderer renderer, IReadOnlyList<Transform> leftOut)
    {
        var source = renderer.sharedMesh;
        if (source == null) return null;
        var skin = renderer.bones;
        var off = new HashSet<IntPtr>();
        foreach (var bone in leftOut) if (bone != null) off.Add(bone.Pointer);
        var kept = new bool[skin.Length];
        for (var b = 0; b < skin.Length; b++)
        {
            kept[b] = true;
            for (var at = skin[b]; at != null; at = at.parent)
                if (off.Contains(at.Pointer)) { kept[b] = false; break; }
        }
        var native = ForgeCore.Il2CppArrays.ToManaged(source.boneWeights);
        var weights = new Weights4[native.Length];
        for (var v = 0; v < native.Length; v++)
        {
            var w = native[v];
            weights[v] = new Weights4 { I0 = w.boneIndex0, I1 = w.boneIndex1, I2 = w.boneIndex2, I3 = w.boneIndex3, W0 = w.weight0, W1 = w.weight1, W2 = w.weight2, W3 = w.weight3 };
        }
        var keep = MeshCut.KeepVertices(weights, kept);
        var cut = Object.Instantiate(source);
        cut.name = source.name + ".first-person";
        for (var sub = 0; sub < source.subMeshCount; sub++)
            cut.SetTriangles(ForgeCore.Il2CppArrays.ToGame(MeshCut.KeepTriangles(ForgeCore.Il2CppArrays.ToManaged(source.GetTriangles(sub)), keep)), sub, true);
        return new OwnBodyCut(renderer, source, cut);
    }

    // The renderer and the copies hung under it (a toon overlay) that draw `from`, drawn with `to`.
    private void Swap(Mesh from, Mesh to)
    {
        if (_renderer == null || _renderer.WasCollected) return;
        foreach (var r in _renderer.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (r.sharedMesh != null && r.sharedMesh.Pointer == from.Pointer) r.sharedMesh = to;
    }

    // Models.ShowOwnBody: every cut look whole while a view shows the body from outside.
    internal static void ShowWhole(bool whole)
    {
        var was = _views > 0;
        _views = Math.Max(0, _views + (whole ? 1 : -1));
        if (was == _views > 0) return;
        Live.RemoveAll(c => c._renderer == null || c._renderer.WasCollected);
        foreach (var c in Live) c.Swap(was ? c._whole : c._cut, was ? c._cut : c._whole);
    }

    /// <summary>The whole mesh back for good; the copy destroyed.</summary>
    public void Dispose()
    {
        if (!Live.Remove(this)) return;
        if (_views == 0) Swap(_cut, _whole);
        if (_cut != null && !_cut.WasCollected) Object.Destroy(_cut);
    }
}
