using System;
using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace ForgeVisual;

/// <summary>The model side of a pose copy (<see cref="Models.ReadPoseTarget(Transform, SkinnedMeshRenderer, IReadOnlyList{PoseLink}, string)"/>, or
/// <see cref="Models.ReadPoseTarget(GameObject, ModelHandle, string)"/> for a body model), read once per model instance from its skinned mesh's
/// bind poses: the model bones in link order, each bone's frame at rest, and the bones as built to go back to.</summary>
public sealed class PoseTarget
{
    private readonly Transform[] _bones;
    private readonly Vector3[] _restPosition;
    private readonly Quaternion[] _restRotation;

    private PoseTarget(Transform root, IReadOnlyList<PoseLink> links, IReadOnlyList<PoseReach> reach, Transform[] bones, bool[] skipped, NQuaternion[] unframe, NVector3[] restAt, float headHeight)
    {
        Root = root;
        Reach = reach;
        RestAt = restAt;
        RootRest = restAt[0];
        Skipped = skipped;
        HeadHeight = headHeight;
        Links = links;
        _bones = bones;
        Unframe = unframe;
        _restPosition = new Vector3[bones.Length];
        _restRotation = new Quaternion[bones.Length];
        for (var i = 0; i < bones.Length; i++) if (bones[i] != null) { _restPosition[i] = bones[i].localPosition; _restRotation[i] = bones[i].localRotation; }
    }

    /// <summary>Per link: left out of the copy because the model lacks its bone (or an optional bone it points at).</summary>
    internal bool[] Skipped { get; }

    /// <summary>The model's root (its size and turn are the copy's frame).</summary>
    public Transform Root { get; }

    /// <summary>The height of the head bone at rest in the root's own units (the model standing on the root's floor); 0 without a head link.
    /// A caller fits a model to a body by the ratio of the body's head height to this.</summary>
    public float HeadHeight { get; }

    public IReadOnlyList<PoseLink> Links { get; }

    /// <summary>The limbs a copy onto this target bends (a body model's declared reach, <see cref="ModelHandle.FollowsBodies"/>); empty when read from links alone.</summary>
    public IReadOnlyList<PoseReach> Reach { get; }

    /// <summary>The link whose model bone holds a copy of what the body holds (a body model's declaration, <see cref="ModelHandle.FollowsBodies"/>);
    /// -1: the model holds nothing of the body's.</summary>
    public int HoldsIn { get; internal set; } = -1;

    /// <summary>The copy a recording was told this model follows, until it rests or that copy is released.</summary>
    internal PoseCopy? Reported { get; set; }

    internal NQuaternion[] Unframe { get; }

    /// <summary>Link 0's rest position in the root's space.</summary>
    internal NVector3 RootRest { get; }

    /// <summary>Each link's rest position in the root's space (zero for a skipped one).</summary>
    internal NVector3[] RestAt { get; }

    /// <summary>The model bone of link <paramref name="link"/>; null for an optional link the model lacks.</summary>
    public Transform Bone(int link) => _bones[link];

    internal Transform[] Bones => _bones;

    /// <summary>The bones as built (the model standing in its rest pose); a recording hears that it follows no body now.</summary>
    public void Rest()
    {
        Reported?.Unreport();
        for (var i = 0; i < _bones.Length; i++)
            if (_bones[i] != null && !_bones[i].WasCollected) { _bones[i].localPosition = _restPosition[i]; _bones[i].localRotation = _restRotation[i]; }
    }

    internal static PoseTarget? Read(Transform root, SkinnedMeshRenderer skin, IReadOnlyList<PoseLink> links, string label, IReadOnlyList<PoseReach>? reach = null)
    {
        if (PoseSolver.Validate(links) is { } bad) { Error(label, "the links are wrong: " + bad); return null; }
        var bones = skin.bones;
        var mesh = skin.sharedMesh;
        var binds = mesh != null ? mesh.bindposes : null;
        if (bones == null || binds == null || binds.Length != bones.Length) { Error(label, "the model has no bind poses"); return null; }
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var b = 0; b < bones.Length; b++) if (bones[b] != null) index.TryAdd(bones[b].name, b);
        var toRoot = root.worldToLocalMatrix * skin.transform.localToWorldMatrix;
        var found = new Transform[links.Count];
        var position = new NVector3[links.Count];
        var rotation = new NQuaternion[links.Count];
        var missing = "";
        var absent = new bool[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            if (!index.TryGetValue(links[i].Model, out var b))
            {
                if (links[i].Optional) absent[i] = true;
                else missing += (missing.Length > 0 ? ", " : "") + links[i].Model;
                continue;
            }
            var rest = toRoot * binds[b].inverse;
            found[i] = bones[b];
            position[i] = BodyConvert.N(rest.GetColumn(3));
            rotation[i] = BodyConvert.N(rest.rotation);
        }
        if (missing.Length > 0) { Error(label, "the model lacks the bones " + missing); return null; }
        var head = 0f;
        for (var i = 0; i < links.Count; i++) if (links[i].Body == BodyBone.Head) head = position[i].Y;
        return new PoseTarget(root, links, reach ?? Array.Empty<PoseReach>(), found, PoseSolver.Skipped(links, absent), PoseSolver.Unframe(links, position, rotation), position, head);
    }

    internal static void Error(string label, string why)
        => Plugin.Logger.ErrorOnce("visual-pose-copy-model-does-not-follow", "ForgeVisual pose copy " + label + ": " + why + "; the model does not follow the body.");
}

/// <summary>
/// A body's humanoid pose copied onto a <see cref="PoseTarget"/> of other proportions (<see cref="Models.CopyPose(Animator, PoseTarget, IReadOnlyList{PoseReach}, string)"/>, or from bones the caller found): the body's bones
/// in link order (by the game's own <c>Animator.GetBoneTransform</c>), each with its fixed correction to the model bone, read once from
/// the body's bind poses. Call <see cref="Apply"/> every frame after the game animated the body: each model bone takes the turn of its
/// body bone and the root its height and sway, scaled by root height over root height, then each <see cref="PoseReach"/> limb bends as
/// the body's does at the limb's own proportions (turns alone put a limb of other proportions elsewhere). A player's body carries what it
/// holds in its hand socket to the model's hand, and the other hand grips it where the body's does, as much as the body's IK holds it
/// there; the socket is back where the game keeps it before the game's next frame. While bound the body's animator always animates
/// (a body hidden inside a model would otherwise stop); <see cref="Release"/> hands it back as it was. Applying looks nothing up and
/// allocates nothing.
/// </summary>
public sealed class PoseCopy
{
    private readonly Animator? _animator;
    private readonly AnimatorCullingMode _culling;
    private readonly Transform _body;
    private readonly Transform[] _source;
    private readonly Quaternion[] _correction;
    private readonly PoseTarget _target;
    private readonly NVector3 _rootRest;
    private readonly float _ratio;
    private readonly PoseReach[] _reach;
    private readonly float[] _limb; // per reach: world units on the model per world unit on the body, along that limb
    private readonly int[] _active;
    private readonly Quaternion[] _turned; // per link: the turn this frame's copy gave its model bone
    // The item the body holds (a player's hand socket, PlayerSyncModelData.ItemAlign): carried to the model's hand each frame and put back
    // where the game keeps it before the game's next frame. _holding is the reach whose hand holds it, _other the other hand's (-1: none).
    private Transform? _socket;
    private int _holding = -1, _other = -1;
    private IKSolver? _otherSolver; // the body's IK that puts the other hand on the item: its weight says how much it is there
    private bool _moved;
    private Vector3 _socketPosition;
    private Quaternion _socketRotation;
    // What the body holds, copied into the model's holding bone (PoseTarget.HoldsIn), re-read every HeldCheck seconds.
    private HeldMirror? _held;
    private float _nextHeld;
    private const float HeldCheck = 0.25f;

    private PoseCopy(Transform body, Animator? animator, PoseTarget target, Transform[] source, int[] active, Quaternion[] correction, NVector3 rootRest, float ratio, float scale, PoseReach[] reach, float[] limb)
    {
        _limb = limb;
        _active = active;
        _turned = new Quaternion[source.Length];
        _animator = animator;
        _body = body;
        _target = target;
        _source = source;
        _correction = correction;
        _rootRest = rootRest;
        _ratio = ratio;
        _reach = reach;
        Scale = scale;
        if (animator == null) return;
        _culling = animator.cullingMode;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
    }

    /// <summary>The model's size against the body's (root height over root height, in the world).</summary>
    public float Scale { get; }

    /// <summary>This frame's turn from the body to the model (set by <see cref="Apply"/>).</summary>
    public Quaternion Turn { get; private set; } = Quaternion.identity;

    /// <summary>Whether this frame's <see cref="Apply"/> carried what the body holds to the model's hand (the item is there already).</summary>
    public bool CarriesHeld => _moved;

    public PoseTarget Target => _target;

    /// <summary>The copy of what the body holds in the model's holding bone, when the target declares one (<see cref="PoseTarget.HoldsIn"/>)
    /// and the body is a player's (an agent's, or a body with the game's hand socket such as a replay's stand-in); placed by every
    /// <see cref="Apply"/> and destroyed by <see cref="Release"/>.</summary>
    public HeldMirror? Held => _held;

    /// <summary>The body bone of link <paramref name="link"/>.</summary>
    public Transform Source(int link) => _source[link];

    internal static PoseCopy? Bind(Animator animator, PoseTarget target, IReadOnlyList<PoseReach> reach, string label)
    {
        if (animator == null || animator.WasCollected || !animator.isHuman) { PoseTarget.Error(label, "the body has no humanoid animator"); return null; }
        var links = target.Links;
        if (PoseSolver.ValidateReach(reach, links.Count) is { } badReach) { PoseTarget.Error(label, badReach); return null; }
        var body = animator.transform;
        var rest = BodyConvert.BindRest(animator, body);
        var source = new Transform[links.Count];
        var pose = new Matrix4x4[links.Count];
        var toward = new NVector3?[links.Count];
        var absent = (bool[])target.Skipped.Clone();
        var missing = "";
        for (var i = 0; i < links.Count; i++)
        {
            if (absent[i] || Resolve(animator, rest, links[i].Body, links[i].BodyAlt, out source[i], out pose[i])) continue;
            if (links[i].Optional) absent[i] = true;
            else missing += (missing.Length > 0 ? ", " : "") + links[i].Body;
        }
        if (missing.Length > 0) { PoseTarget.Error(label, "the body lacks the bones (or their bind poses) " + missing); return null; }
        for (var i = 0; i < links.Count; i++)
            if (links[i].TowardBody is { } t && Resolve(animator, rest, t, links[i].TowardBodyAlt, out _, out var m)) toward[i] = BodyConvert.N(m.GetColumn(3));
        var copy = Create(body, animator, target, source, pose, toward, PoseSolver.Skipped(links, absent), reach, label);
        copy?.FindHeld(animator, label);
        return copy;
    }

    // A player's body (an agent's, a lobby figure's, a replay stand-in's) hangs what it holds in its model's hand socket: the reach of
    // the hand above that socket holds it, the reach of the other hand grips it as the body's IK on that hand says. A target that holds what
    // the body holds gets its copy: of the player's wielded item for an agent's body, of what hangs in the socket for a body with no player.
    private void FindHeld(Animator animator, string label)
    {
        var model = animator.GetComponentInParent<PlayerSyncModelData>();
        var socket = model != null ? model.ItemAlign : null;
        if (socket == null) return;
        if (_target.HoldsIn >= 0 && _target.Bone(_target.HoldsIn) != null)
        {
            var owner = model!.Owner;
            _held = new HeldMirror(owner, owner == null ? socket : null, _target.Root.gameObject.layer, label);
        }
        for (var i = 0; i < _reach.Length; i++)
        {
            var hand = _source[_reach[i].End];
            if (socket.IsChildOf(hand)) _holding = i;
        }
        if (_holding < 0) return;
        _socket = socket;
        for (var i = 0; i < _reach.Length; i++)
            if (i != _holding && _target.Links[_reach[i].End].Body is BodyBone.LeftHand or BodyBone.RightHand) _other = i;
        if (_other < 0) return;
        var otherHand = _source[_reach[_other].End];
        foreach (var ik in BodySolvers(animator))
        {
            var solver = ik.GetIKSolver();
            var points = solver?.GetPoints();
            if (points == null) continue;
            foreach (var point in points)
                if (point?.transform is { } at && at.Pointer == otherHand.Pointer) { _otherSolver = solver; return; }
        }
    }

    /// <summary>A copy from body bones the caller resolved itself (<see cref="Models.CopyPose(Transform, IReadOnlyList{Transform}, IReadOnlyList{Matrix4x4}, Animator?, PoseTarget, string)"/>):
    /// one per link, each with its rest pose in <paramref name="body"/>'s frame. No reach, no toward-body bones.</summary>
    internal static PoseCopy? Bind(Transform body, IReadOnlyList<Transform> bones, IReadOnlyList<Matrix4x4> rest, Animator? animator, PoseTarget target, string label)
    {
        var links = target.Links;
        if (bones.Count != links.Count || rest.Count != links.Count) { PoseTarget.Error(label, "the body has " + bones.Count + " bones for the " + links.Count + " links"); return null; }
        var source = new Transform[links.Count];
        var pose = new Matrix4x4[links.Count];
        var absent = (bool[])target.Skipped.Clone();
        for (var i = 0; i < links.Count; i++)
        {
            if (absent[i]) continue;
            if (bones[i] == null || bones[i].WasCollected)
            {
                if (links[i].Optional) { absent[i] = true; continue; }
                PoseTarget.Error(label, "the body has no bone for " + links[i].Model);
                return null;
            }
            source[i] = bones[i];
            pose[i] = rest[i];
        }
        return Create(body, animator, target, source, pose, new NVector3?[links.Count], PoseSolver.Skipped(links, absent), Array.Empty<PoseReach>(), label);
    }

    private static PoseCopy? Create(Transform body, Animator? animator, PoseTarget target, Transform[] source, Matrix4x4[] pose, NVector3?[] toward, bool[] skip, IReadOnlyList<PoseReach> reach, string label)
    {
        var links = target.Links;
        var position = new NVector3[links.Count];
        var rotation = new NQuaternion[links.Count];
        var active = new List<int>(links.Count);
        for (var i = 0; i < links.Count; i++)
        {
            if (skip[i]) continue;
            active.Add(i);
            position[i] = BodyConvert.N(pose[i].GetColumn(3));
            rotation[i] = BodyConvert.N(pose[i].rotation);
        }
        if (PoseSolver.Ratio(target.RootRest.Y, position[0].Y, out var ratio) is { } why) { PoseTarget.Error(label, why); return null; }
        if (ratio <= 0f)
            Plugin.Logger.ErrorOnce("visual-pose-copy-root-does-not-rise", "ForgeVisual pose copy " + label + ": the model's root (hips) rests at " + target.RootRest.Y.ToString("0.###") + " m, on its floor or under it, so there is no height to scale the body's by; the model keeps its rest height and does not rise, sink or sway with the body.");
        var corrections = PoseSolver.Correct(links, position, rotation, toward, target.Unframe);
        var correction = new Quaternion[links.Count];
        for (var i = 0; i < links.Count; i++) correction[i] = BodyConvert.U(corrections[i]);
        var world = target.Root.lossyScale.y / Math.Max(1e-4f, body.lossyScale.y);
        var scale = (ratio > 0f ? ratio : 1f) * world;
        // A limb with a link left out cannot be bent onto the body's: its bones keep their copied turns.
        var reachable = PoseSolver.Reachable(reach, skip);
        if (reachable.Count < reach.Count)
        {
            var left = "";
            foreach (var r in reach)
                if (skip[r.Upper] || skip[r.Lower] || skip[r.End]) left += (left.Length > 0 ? ", " : "") + links[r.End].Body;
            Plugin.Logger.ErrorOnce("visual-pose-copy-limb-does-not-reach", "ForgeVisual pose copy " + label + ": the limb ending at " + left + " lacks a bone on the model or the body, so it is not bent to where the body's ends; its bones only turn as the body's do, and what the body holds in that hand stays at the body's own hand.");
        }
        var reaches = new PoseReach[reachable.Count];
        var limb = new float[reaches.Length];
        var at = target.RestAt;
        for (var i = 0; i < reaches.Length; i++)
        {
            var r = reaches[i] = reachable[i];
            limb[i] = PoseSolver.LimbScale(at[r.Upper], at[r.Lower], at[r.End], position[r.Upper], position[r.Lower], position[r.End], ratio) * world;
        }
        return new PoseCopy(body, animator, target, source, active.ToArray(), correction, position[0], ratio, scale, reaches, limb);
    }

    /// <summary>The body bone <paramref name="first"/> (else <paramref name="second"/>) and its rest in the body's frame.</summary>
    private static bool Resolve(Animator animator, Dictionary<IntPtr, Matrix4x4> rest, BodyBone first, BodyBone? second, out Transform bone, out Matrix4x4 pose)
    {
        var t = animator.GetBoneTransform((HumanBodyBones)(int)first);
        if (t != null && rest.TryGetValue(t.Pointer, out pose)) { bone = t; return true; }
        if (second is { } alt)
        {
            t = animator.GetBoneTransform((HumanBodyBones)(int)alt);
            if (t != null && rest.TryGetValue(t.Pointer, out pose)) { bone = t; return true; }
        }
        bone = null!;
        pose = default;
        return false;
    }

    /// <summary>Every frame, after the game animated the body: the model's root bone at the body's (height and sway scaled) and every
    /// model bone turned as its body bone is. False when the body or the model is gone.</summary>
    public bool Apply()
    {
        var root = _target.Root;
        if (_animator != null && ForgeCore.NativeObjects.Gone(_animator) || ForgeCore.NativeObjects.Gone(_body) || ForgeCore.NativeObjects.Gone(root)) return false;
        var turn = root.rotation * Quaternion.Inverse(_body.rotation);
        Turn = turn;
        var bones = _target.Bones;
        var hips = BodyConvert.N(_body.InverseTransformPoint(_source[0].position));
        bones[0].position = root.TransformPoint(BodyConvert.U(PoseSolver.Root(_target.RootRest, hips, _rootRest, _ratio)));
        for (var k = 0; k < _active.Length; k++) { var i = _active[k]; bones[i].rotation = _turned[i] = turn * _source[i].rotation * _correction[i]; }
        // The holding hand first: the item goes with it, and the other hand reaches the item there.
        var carried = false;
        Vector3 hand = default, bodyHand = default;
        if (_holding >= 0 && !ForgeCore.NativeObjects.Gone(_socket))
        {
            var end = _reach[_holding].End;
            Reach(_holding, turn, bones, default, 0f);
            hand = bones[end].position;
            bodyHand = _source[end].position;
            Carry(hand, bodyHand, turn);
            carried = true;
        }
        for (var i = 0; i < _reach.Length; i++)
        {
            if (i == _holding && carried) continue;
            // On the item as the body's hand is (seen from the holding hand: the item keeps its size), as far as the body's IK puts it there.
            if (i == _other && carried && _otherSolver != null)
                Reach(i, turn, bones, hand + turn * (_source[_reach[i].End].position - bodyHand), Mathf.Clamp01(_otherSolver.GetIKPositionWeight()));
            else Reach(i, turn, bones, default, 0f);
        }
        PlaceHeld();
        if (_target.Reported != this && ForgeCore.Recording.Watched) Report();
        return true;
    }

    /// <summary>Puts the copy of what the body holds (<see cref="Held"/>) at the model's holding bone as it is posed now: <see cref="Apply"/>
    /// does; a caller that turns the model's bones further after it calls it again.</summary>
    public void PlaceHeld()
    {
        if (_held == null) return;
        var now = ForgeCore.Frame.UnscaledTime;
        if (now >= _nextHeld) { _nextHeld = now + HeldCheck; _held.Refresh(); }
        _held.Place(this, _source[_target.HoldsIn], _target.Bones[_target.HoldsIn]);
    }

    // A recording hears which body the model follows from its first applied frame, and that it follows none once it rests or the copy is
    // released (a replay copies the same body's pose onto the same model: ModelHandle.FollowsBodies).
    private void Report()
    {
        _target.Reported?.Unreport();
        _target.Reported = this;
        ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Follows, 0, null, _body, _target.Root));
    }

    internal void Unreport()
    {
        if (_target.Reported != this) return;
        _target.Reported = null;
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Follows, 0, null, null, _target.Root));
    }

    /// <summary>Limb <paramref name="i"/> bent (in the body limb's bend plane) so its end joint sits where the body's is seen from the
    /// limb's upper joint, at the limb's proportions (<see cref="PoseReach"/>), moved toward <paramref name="grip"/> by <paramref name="weight"/>.</summary>
    private void Reach(int i, Quaternion turn, Transform[] bones, Vector3 grip, float weight)
    {
        var r = _reach[i];
        Transform upper = bones[r.Upper], lower = bones[r.Lower], end = bones[r.End];
        var bodyUpper = _source[r.Upper].position;
        var target = upper.position + turn * (_source[r.End].position - bodyUpper) * _limb[i];
        if (weight > 0f) target = Vector3.Lerp(target, grip, weight);
        var keep = _turned[r.End];
        LimbReach.Bend(upper, lower, end, target, turn * (_source[r.Lower].position - bodyUpper));
        end.rotation = keep;
    }

    // The held item moved with the model's hand: the socket keeps its place and turn against the hand as on the body. Where the game keeps
    // it is put back before the game's next frame (RestoreHeld), so the game's own aim and hand IK never see the move.
    private void Carry(Vector3 hand, Vector3 bodyHand, Quaternion turn)
    {
        var socket = _socket!;
        // Applied twice in one frame (one more solver solved): from where the game keeps it again, never from last time's move.
        if (_moved) { socket.localPosition = _socketPosition; socket.localRotation = _socketRotation; }
        else
        {
            _socketPosition = socket.localPosition;
            _socketRotation = socket.localRotation;
        }
        socket.SetPositionAndRotation(hand + turn * (socket.position - bodyHand), turn * socket.rotation);
        if (!_moved) Carried.Add(this);
        _moved = true;
    }

    private static readonly List<PoseCopy> Carried = new();

    // Before the game's frame: every carried socket back where the game keeps it, the last carried first (two copies of one body: the
    // first one's capture is where the game keeps it).
    internal static void RestoreHeld()
    {
        for (var i = Carried.Count - 1; i >= 0; i--) Carried[i].PutBack();
        Carried.Clear();
    }

    private void PutBack()
    {
        if (!_moved) return;
        _moved = false;
        if (ForgeCore.NativeObjects.Gone(_socket)) return;
        _socket.localPosition = _socketPosition;
        _socket.localRotation = _socketRotation;
    }

    private static readonly List<PoseCopy> Following = new();
    private bool _following;

    // The body's own FinalIK solvers (a player's, driven by the game's PlayerSyncIK), which pose the body after its animation in their own
    // late update, in an order against this library's late update that nothing fixes. A following copy of such a body is applied once every
    // one of them that solves this frame has solved (each solver's OnPostUpdate), so the model takes the solved pose (hands on the grip,
    // spine along the aim) the same way on a real player and on any other body; with none solving it is applied in this library's late update.
    private IK[] _solvers = Array.Empty<IK>();
    private IKSolver.UpdateDelegate? _solved;
    private int _frame = -1, _expected, _done;

    /// <summary>From now on the library applies the copy every frame itself, after the game animated the body, after the joint clips
    /// played on it (<see cref="JointPlayer"/>) and after the body's own IK solved, until <see cref="Release"/>, the body or the model is
    /// gone, or the level ends. For a caller with nothing of its own to do between (a skin on an enemy, a character on a player); a caller
    /// that poses more after the copy calls <see cref="Apply"/> itself.</summary>
    public void Follow()
    {
        if (_following) return;
        _following = true;
        Following.Add(this);
        if (_animator == null) return;
        _solvers = BodySolvers(_animator);
        if (_solvers.Length == 0) return;
        _solved = (IKSolver.UpdateDelegate)(Action)Solved;
        foreach (var ik in _solvers)
        {
            var solver = ik.GetIKSolver();
            var after = solver.OnPostUpdate;
            solver.OnPostUpdate = after == null ? _solved : Il2CppSystem.Delegate.Combine(after, _solved).Cast<IKSolver.UpdateDelegate>();
        }
    }

    // The FinalIK solvers that pose this body: under the model's object (the animator's parent), the player agent it belongs to and that
    // agent's PlayerSyncIK, whose solver root is this body or holds it.
    private static IK[] BodySolvers(Animator animator)
    {
        var body = animator.transform;
        var found = new List<IK>();
        var seen = new HashSet<IntPtr>();
        var agent = animator.GetComponentInParent<Player.PlayerAgent>();
        foreach (var root in new Component?[] { body.parent != null ? body.parent : body, agent, agent != null ? agent.SyncIK : null })
        {
            if (root == null || root.WasCollected) continue;
            foreach (var ik in root.GetComponentsInChildren<IK>(true))
                if (ik != null && seen.Add(ik.Pointer) && ik.GetIKSolver()?.GetRoot() is { } at && (at == body || at.IsChildOf(body) || body.IsChildOf(at)))
                    found.Add(ik);
        }
        return found.ToArray();
    }

    // How many of the body's solvers solve this frame: those whose late update runs and that are set up. A solver the game destroyed
    // (its wrapper stays) solves no more and leaves the list: the copy goes on with the rest, and in this library's late update with
    // none, where a body that is gone too stops it.
    private void Begin(int frame)
    {
        _frame = frame; _done = 0; _expected = 0;
        var gone = false;
        foreach (var ik in _solvers)
            if (ForgeCore.NativeObjects.Gone(ik)) gone = true;
            else if (ik.isActiveAndEnabled && ik.GetIKSolver().initiated) _expected++;
        if (gone) _solvers = Array.FindAll(_solvers, ik => !ForgeCore.NativeObjects.Gone(ik));
    }

    // A solver of the body solved: once every one that solves this frame has, the copy is applied (again, should one more solve after).
    private void Solved()
    {
        var frame = ForgeCore.Frame.Count;
        if (frame != _frame) Begin(frame);
        if (++_done < _expected || !_following) return;
        var i = Following.IndexOf(this);
        if (i >= 0) ApplyFollowing(i);
    }

    /// <summary>Every following copy applied (LateUpdate, after the joint clips) unless its body's IK solves this frame (then applied when
    /// it has solved); a copy whose body or model is gone stops following.</summary>
    internal static void TickAll()
    {
        var frame = ForgeCore.Frame.Count;
        for (var i = Following.Count - 1; i >= 0; i--)
        {
            var copy = Following[i];
            if (copy._solvers.Length > 0)
            {
                if (copy._frame != frame) copy.Begin(frame);
                if (copy._expected > 0) continue;
            }
            ApplyFollowing(i);
        }
    }

    private static void ApplyFollowing(int i)
    {
        var copy = Following[i];
        try { if (copy.Apply()) return; }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-pose-copy-failed", "ForgeVisual pose copy failed and stopped following: " + error); }
        copy.Unfollow();
        Following.RemoveAt(i);
    }

    internal static void StopAll()
    {
        foreach (var copy in Following) copy.Unfollow();
        Following.Clear();
        RestoreHeld();
    }

    // Stops following: the solvers' callback goes (a solver destroyed with its body has nothing to take back).
    private void Unfollow()
    {
        _following = false;
        if (_solved != null)
            foreach (var ik in _solvers)
                if (!ForgeCore.NativeObjects.Gone(ik) && ik.GetIKSolver() is { } solver && solver.OnPostUpdate is { } after)
                    solver.OnPostUpdate = Il2CppSystem.Delegate.Remove(after, _solved)?.Cast<IKSolver.UpdateDelegate>();
        _solvers = Array.Empty<IK>();
        _solved = null;
    }

    /// <summary>The copy stops following and the body's animator is back as it was (nothing to give back once the game has destroyed it, as a
    /// level unload does).</summary>
    public void Release()
    {
        if (_following) { Unfollow(); Following.Remove(this); }
        Unreport();
        PutBack();
        Carried.Remove(this);
        _held?.Destroy();
        _held = null;
        if (_animator != null && !_animator.WasCollected) _animator.cullingMode = _culling;
    }
}

/// <summary>A two-bone limb (upper arm, forearm, hand; thigh, shin, foot) turned so its end joint lands on a point, with the bone math of
/// <see cref="PoseSolver.Elbow"/>. Call it after the animator has posed the body (late update): the animator writes the bones again next frame.</summary>
public static class LimbReach
{
    /// <summary>Turns <paramref name="upper"/> and <paramref name="lower"/> so <paramref name="end"/> sits on <paramref name="target"/>
    /// (or as near as the bones reach), the middle joint bending toward <paramref name="bend"/>. The end joint's own rotation turns with its parents.</summary>
    public static void Bend(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 bend)
    {
        Vector3 a = upper.position, b = lower.position, c = end.position;
        var elbow = BodyConvert.U(PoseSolver.Elbow(BodyConvert.N(a), BodyConvert.N(target), Vector3.Distance(a, b), Vector3.Distance(b, c), BodyConvert.N(bend), out var reached));
        var swing = Quaternion.FromToRotation(b - a, elbow - a);
        upper.rotation = swing * upper.rotation;
        // Turned about the upper joint at its own length, the middle joint is on the elbow and the end turned with it.
        c = a + swing * (c - a);
        lower.rotation = Quaternion.FromToRotation(c - elbow, BodyConvert.U(reached) - elbow) * lower.rotation;
    }
}
