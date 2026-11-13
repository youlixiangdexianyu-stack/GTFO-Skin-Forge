using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>What <see cref="SkinRebind.Rebind"/> wrote: renderers and bone slots moved onto the target skeleton, and
/// the largest bind-pose difference it measured between a part bone and the target bone it now follows.</summary>
public readonly record struct SkinRebindResult(int Renderers, int Bones, float PositionError, float BasisError);

/// <summary>
/// Moves a copied skinned model onto another skeleton: every skinned renderer below the part follows the target bones a
/// verified mapping names, instead of the part's own copy of its skeleton. The mapping is data the caller resolved
/// (pairs read from content, never matched by bone name here). Each pair is checked before anything is written: the
/// part bone's bind pose in its own model root and the target bone's bind pose the caller gives (<see cref="BindRest"/>
/// of the target model as the game ships it, since a body whose own meshes were reshaped no longer carries it) must
/// agree within the caller's tolerance, so a mapping that no longer describes the game's skeletons is refused, not
/// drawn bent.
///
/// The new bind poses are the target bones' own (<c>targetBind⁻¹ · meshToPart</c>), so at rest the part sits exactly
/// where its copy was placed under the part root, and moves with the target bones from there. The mesh copies are the
/// caller's (cached per key and released by the caller); nothing runs per frame.
/// </summary>
public static class SkinRebind
{
    /// <summary>
    /// Rebinds the skinned renderers below <paramref name="part"/>. <paramref name="source"/> is the root the part's own
    /// skeleton was authored under (its bind poses are read in that frame); the part sits under the target model root with
    /// an identity pose and takes its frame. <paramref name="bones"/> maps each part bone (by pointer) to its target bone;
    /// <paramref name="targetRest"/> gives each target bone (by pointer) its bind pose in the target model root's frame.
    /// <paramref name="meshFor"/> returns the mesh copy for a renderer index, building it with the given bind poses on first use.
    /// Returns null, or the refusal code when any renderer cannot be rebound (then nothing was written).
    /// </summary>
    public static string? Rebind(Transform part, Transform source, IReadOnlyDictionary<IntPtr, Matrix4x4> targetRest,
        IReadOnlyDictionary<IntPtr, Transform> bones, float tolerance, Func<int, Mesh, Matrix4x4[], Mesh> meshFor, out SkinRebindResult result)
    {
        result = default;
        var plans = new List<(SkinnedMeshRenderer Renderer, Transform?[] Bones, Matrix4x4[] Binds, Transform? Root)>();
        float position = 0f, basis = 0f;
        int slots = 0;
        var renderers = part.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            var mesh = renderer == null ? null : renderer.sharedMesh;
            if (mesh == null) return "deform-mesh-missing";
            var own = renderer!.bones;
            var binds = mesh.bindposes;
            if (own == null || binds == null || binds.Length != own.Length) return "deform-bindpose-mismatch:" + renderer.name;
            var meshInSource = source.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var meshInPart = part.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var next = new Transform?[own.Length];
            var nextBinds = new Matrix4x4[own.Length];
            for (int index = 0; index < own.Length; index++)
            {
                var bone = own[index];
                if (bone == null) { nextBinds[index] = binds[index]; continue; }
                if (!bones.TryGetValue(bone.Pointer, out var mapped)) return "deform-bone-unmapped:" + bone.name;
                if (!targetRest.TryGetValue(mapped.Pointer, out var targetBind)) return "deform-target-bind-missing:" + mapped.name;
                var partBind = meshInSource * binds[index].inverse;
                position = Math.Max(position, (Column(partBind) - Column(targetBind)).magnitude);
                basis = Math.Max(basis, Basis(partBind, targetBind));
                if (position > tolerance || basis > tolerance) return "deform-pose-mismatch:" + bone.name;
                next[index] = mapped;
                nextBinds[index] = targetBind.inverse * meshInPart;
                slots++;
            }
            var root = renderer.rootBone != null && bones.TryGetValue(renderer.rootBone.Pointer, out var mappedRoot) ? mappedRoot : null;
            plans.Add((renderer, next, nextBinds, root));
        }
        if (plans.Count == 0) return "deform-no-renderer";
        for (int index = 0; index < plans.Count; index++)
        {
            var (renderer, next, binds, root) = plans[index];
            renderer.sharedMesh = meshFor(index, renderer.sharedMesh, binds);
            renderer.bones = new Il2CppReferenceArray<Transform>(next!);
            if (root != null) renderer.rootBone = root;
        }
        result = new SkinRebindResult(plans.Count, slots, position, basis);
        return null;
    }

    /// <summary>The bind pose of every bone the skinned renderers under <paramref name="root"/> skin, in its frame, by bone
    /// pointer (the first renderer that skins a bone gives it). Read it from a shipped model asset to get the game's own.</summary>
    public static Dictionary<IntPtr, Matrix4x4> BindRest(Transform root) => BodyConvert.BindRest(root, root);

    /// <summary>A copy of <paramref name="mesh"/> carrying <paramref name="binds"/> as its bind poses.</summary>
    public static Mesh WithBindposes(Mesh mesh, Matrix4x4[] binds)
    {
        var copy = Object.Instantiate(mesh);
        copy.name = mesh.name + " (Forge skin)";
        copy.bindposes = new Il2CppStructArray<Matrix4x4>(binds);
        return copy;
    }

    private static Vector3 Column(Matrix4x4 matrix) => new(matrix.m03, matrix.m13, matrix.m23);

    private static float Basis(Matrix4x4 a, Matrix4x4 b)
    {
        float most = 0f;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                most = Math.Max(most, Math.Abs(a[row, column] - b[row, column]));
        return most;
    }
}
