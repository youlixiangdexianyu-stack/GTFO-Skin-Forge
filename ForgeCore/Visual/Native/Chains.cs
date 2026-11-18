using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>A collider a bound chain is kept out of: a capsule on a bone from A to B in the bone's own space (a sphere when A is B), its
/// radius in metres.</summary>
public readonly struct ChainCapsule
{
    public ChainCapsule(Transform bone, Vector3 a, Vector3 b, float radius) { Bone = bone; A = a; B = b; Radius = radius; }
    public Transform Bone { get; }
    public Vector3 A { get; }
    public Vector3 B { get; }
    public float Radius { get; }
}

/// <summary>A chain physics (<see cref="ChainState"/>) bound to bone chains: hair, a ponytail, a skirt, a tail. Every frame, after the pose
/// copies, it steps and turns each bone so its child lands where the chain put it; a chain more than <see cref="Chains.CullDistance"/>
/// from the local eye is not stepped (its bones keep the animated pose) and starts again at rest when it comes back.</summary>
public sealed class ChainBinding
{
    private readonly Transform _anchor;
    private readonly Transform? _body;
    private readonly Renderer? _seenBy;
    private readonly Transform[] _bones; // every strand's bones, root first, in node order (the tips have none)
    private readonly int[] _child; // per bone, the node its child is
    private readonly Vector3[] _tip; // per bone, its child (the next bone, or the strand's tip) in its own space as bound
    private readonly Quaternion[] _rest; // local rotations as bound, given back on release
    private readonly ChainCapsule[] _capsules;
    private readonly ChainState _state;
    private readonly double[] _matrix = new double[16], _body3 = new double[3];
    internal bool Bound = true;

    internal ChainBinding(Transform anchor, IReadOnlyList<Transform[]> strands, ChainSpec spec, IReadOnlyList<ChainCapsule>? capsules, bool linkSide, bool loop, Transform? body, Renderer? seenBy)
    {
        _anchor = anchor;
        _body = body;
        _seenBy = seenBy;
        var shape = new ChainShape();
        var bones = new List<Transform>();
        var children = new List<int>();
        var tips = new List<Vector3>();
        var side = new List<int[]>();
        var turnBack = Quaternion.Inverse(anchor.rotation);
        var origin = anchor.position;
        foreach (var strand in strands)
        {
            if (strand == null || strand.Length < 2) throw new ArgumentException("a strand needs at least two bones");
            var ids = new int[strand.Length + 1];
            var r = turnBack * (strand[0].position - origin);
            ids[0] = shape.AddRoot(r.x, r.y, r.z);
            for (int j = 1; j <= strand.Length; j++)
            {
                // The tip continues the last bone by its length.
                Vector3 from = strand[j - 1].position, to = j < strand.Length ? strand[j].position : from + (from - strand[j - 2].position);
                var d = turnBack * (to - from);
                ids[j] = shape.Add(ids[j - 1], Math.Max(d.magnitude, 1e-4), d.x, d.y, d.z);
            }
            for (int j = 0; j < strand.Length; j++)
            {
                bones.Add(strand[j]);
                children.Add(ids[j + 1]);
                var p = strand[j].position;
                tips.Add(strand[j].InverseTransformPoint(j + 1 < strand.Length ? strand[j + 1].position : p + (p - strand[j - 1].position)));
            }
            side.Add(ids);
        }
        if (linkSide) shape.LinkSide(side, loop);
        _bones = bones.ToArray();
        _child = children.ToArray();
        _tip = tips.ToArray();
        _rest = new Quaternion[_bones.Length];
        for (int i = 0; i < _bones.Length; i++) _rest[i] = _bones[i].localRotation;
        _capsules = capsules == null ? Array.Empty<ChainCapsule>() : new List<ChainCapsule>(capsules).ToArray();
        _state = new ChainState(shape, spec) { Capsules = _capsules.Length };
    }

    /// <summary>One frame: false once the anchor or a bone is gone (the binding then stops).</summary>
    internal bool Step(double time, Vector3 eye)
    {
        if (ForgeCore.NativeObjects.Gone(_anchor)) return false;
        var m = _anchor.localToWorldMatrix;
        // Not stepped far away or unseen: it comes back at rest (the gap).
        float ex = m.m03 - eye.x, ey = m.m13 - eye.y, ez = m.m23 - eye.z;
        if (ex * ex + ey * ey + ez * ez > Chains.CullDistance * Chains.CullDistance) return true;
        if (_seenBy != null && !ForgeCore.NativeObjects.Gone(_seenBy) && !_seenBy.isVisible) return true;
        _matrix[0] = m.m00; _matrix[1] = m.m10; _matrix[2] = m.m20; _matrix[3] = m.m30;
        _matrix[4] = m.m01; _matrix[5] = m.m11; _matrix[6] = m.m21; _matrix[7] = m.m31;
        _matrix[8] = m.m02; _matrix[9] = m.m12; _matrix[10] = m.m22; _matrix[11] = m.m32;
        _matrix[12] = m.m03; _matrix[13] = m.m13; _matrix[14] = m.m23; _matrix[15] = m.m33;
        if (_body != null && !ForgeCore.NativeObjects.Gone(_body))
        {
            var b = _body.position;
            _body3[0] = b.x; _body3[1] = b.y; _body3[2] = b.z;
        }
        else { _body3[0] = m.m03; _body3[1] = m.m13; _body3[2] = m.m23; }
        for (int c = 0; c < _capsules.Length; c++)
        {
            var cap = _capsules[c];
            if (ForgeCore.NativeObjects.Gone(cap.Bone)) { _state.SetCapsule(c, 0, -1e6, 0, 0, -1e6, 0, 0); continue; }
            Vector3 pa = cap.Bone.TransformPoint(cap.A), pb = cap.Bone.TransformPoint(cap.B);
            _state.SetCapsule(c, pa.x, pa.y, pa.z, pb.x, pb.y, pb.z, cap.Radius);
        }
        _state.Update(time, _matrix, _body3);
        // Top down: each bone turned by the shortest turn that takes where its child is now onto where the chain put it.
        var p = _state.Nodes;
        for (int i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            if (ForgeCore.NativeObjects.Gone(bone)) return false;
            int k = _child[i] * 3;
            var from = bone.position;
            var now = bone.TransformPoint(_tip[i]);
            var want = new Vector3((float)p[k], (float)p[k + 1], (float)p[k + 2]);
            bone.rotation = Quaternion.FromToRotation(now - from, want - from) * bone.rotation;
        }
        return true;
    }

    /// <summary>Stops stepping and gives the bones back their bound local rotations (nothing to give back once the game destroyed them).</summary>
    public void Release()
    {
        if (!Bound) return;
        Bound = false;
        Chains.Forget(this);
        for (int i = 0; i < _bones.Length; i++)
            if (!ForgeCore.NativeObjects.Gone(_bones[i])) _bones[i].localRotation = _rest[i];
    }
}

/// <summary>Binds <see cref="ChainState"/> to bone chains and steps every binding once a frame after the pose copies (VisualFrame.LateUpdate).</summary>
public static class Chains
{
    /// <summary>Metres from the local eye beyond which a bound chain is not stepped.</summary>
    public const float CullDistance = 25f;

    private static readonly List<ChainBinding> Live = new();

    /// <summary>Binds strands of bones (each from its root bone down, at least two) to one chain set hanging from `anchor` (the head, the
    /// hips), shaped as the bones are now. `linkSide` links neighbouring strands node to node (a skirt), `loop` also the last to the first.
    /// `body` is what the chain rides along with (the character's root; the anchor when null); while `seenBy` (the model's renderer) is
    /// not visible this frame the chain is not stepped.</summary>
    public static ChainBinding Bind(Transform anchor, IReadOnlyList<Transform[]> strands, ChainSpec spec, IReadOnlyList<ChainCapsule>? capsules = null,
        bool linkSide = false, bool loop = false, Transform? body = null, Renderer? seenBy = null)
    {
        var binding = new ChainBinding(anchor, strands, spec, capsules, linkSide, loop, body, seenBy);
        Live.Add(binding);
        return binding;
    }

    internal static void Forget(ChainBinding binding) => Live.Remove(binding);

    internal static void TickAll()
    {
        if (Live.Count == 0) return;
        double time = Scene.Now;
        var eye = Scene.Eye;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var binding = Live[i];
            try { if (binding.Step(time, eye)) continue; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("visual-chain-failed", "ForgeVisual chain physics failed and stopped: " + error); }
            binding.Bound = false;
            Live.RemoveAt(i);
        }
    }
}
