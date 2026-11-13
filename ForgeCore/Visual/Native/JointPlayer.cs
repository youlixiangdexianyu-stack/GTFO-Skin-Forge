using System;
using System.Collections.Generic;
using Enemies;
using Player;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;

namespace ForgeVisual;

/// <summary>How a <see cref="JointClip"/> plays on a body (<see cref="JointPlayer.Play"/>).</summary>
public readonly struct JointPlayback
{
    /// <param name="start">This peer's <see cref="Time.time"/> at which the clip was at its first frame; another peer joins in with
    /// <c>Time.time - elapsed</c>, where elapsed is how far into the clip the sender was.</param>
    /// <param name="loop">Play until stopped instead of once.</param>
    /// <param name="mask">The bones the clip drives (default <see cref="JointMask.FullBody"/>); every other bone keeps the game's own
    /// animation and IK.</param>
    /// <param name="weight">How far the masked bones go from the game's pose to the clip's, 0 to 1.</param>
    /// <param name="fadeIn">Seconds from the pose the bones have to the clip's, counted from this call (a late joiner fades in too).</param>
    /// <param name="fadeOut">Seconds back to the game's pose when a clip that does not loop reaches its end.</param>
    public JointPlayback(float start, bool loop = false, JointMask? mask = null, float weight = 1f, float fadeIn = 0f, float fadeOut = 0f)
    {
        if (!(weight is >= 0f and <= 1f)) throw new ArgumentOutOfRangeException(nameof(weight), "A track weight is from 0 to 1.");
        Start = start;
        Loop = loop;
        Mask = mask;
        Weight = weight;
        FadeIn = fadeIn;
        FadeOut = fadeOut;
    }

    public float FadeIn { get; }
    public float FadeOut { get; }
    public float Start { get; }
    public bool Loop { get; }
    public JointMask? Mask { get; }
    public float Weight { get; }
}

/// <summary>One clip playing on a body. A handle kept after its track ended never touches the track that reuses the slot.</summary>
public readonly struct JointTrack
{
    private readonly JointPlayer? _player;
    private readonly int _slot, _generation;

    internal JointTrack(JointPlayer player, int slot, int generation)
    {
        _player = player;
        _slot = slot;
        _generation = generation;
    }

    public bool Playing => _player != null && _player.Live(_slot, _generation);

    /// <summary>Seconds since the clip's first frame, 0 once it has ended.</summary>
    public float Elapsed => Playing ? Time.time - _player!.StartOf(_slot) : 0f;

    public void Stop()
    {
        if (Playing) _player!.StopSlot(_slot);
    }

    /// <summary>Fades back to the game's pose over <paramref name="seconds"/>, then ends.</summary>
    public void Stop(float seconds)
    {
        if (Playing) _player!.FadeSlot(_slot, seconds);
    }

    /// <summary>Moves the clip to <paramref name="elapsed"/> seconds in (a replay driving it from its own clock, so it follows speed, pause and jumps); false once it has ended.</summary>
    public bool Seek(float elapsed)
    {
        if (!Playing) return false;
        _player!.SeekSlot(_slot, elapsed);
        return true;
    }
}

/// <summary>
/// <see cref="JointClip"/>s played on a body's own bones (<see cref="Models.BindJoints(PlayerAgent)"/>,
/// <see cref="Models.BindJoints(EnemyAgent, out string)"/>), after the game's animator has posed it, every frame until they end or are stopped.
/// A pose is a function of the time since its clip began, so a peer told the clip and how far into it the sender was shows the same
/// motion (each peer's <see cref="Time.time"/> is its own clock); nothing else needs sending.
/// <para>At bind the body's rest pose is read once from its skinned meshes' bind poses and each bone is lined up with the canonical
/// rig's direction to its child (<see cref="ClipRig.RestDirection"/>), so arms-down, arms-out and A-pose bodies all end up with the limbs
/// where the clip puts them. Up to <see cref="MaxTracks"/> tracks play at once, each on its own <see cref="JointMask"/>; tracks whose
/// masks do not overlap add up (one hand gestures while the other keeps its grip), and on a shared bone the later track wins. A track
/// over the hips writes the clip's pose in the body's own axes, the hips also the clip's travel scaled by leg length; any other track
/// writes each masked bone's turn against its parent onto wherever the game (or a track) put the parent, so a hand gesture rides on the
/// body's own walk and aim. A bone outside every mask keeps what the game's animator and IK gave it; only the IK that moves a masked
/// bone is stopped while its track plays (<see cref="IkPause"/>). Posing looks nothing up and allocates nothing.</para>
/// </summary>
public sealed class JointPlayer
{
    /// <summary>How many clips one body plays at once.</summary>
    public const int MaxTracks = 4;

    private static readonly List<JointPlayer> Active = new();

    // The humanoid bone each canonical joint is, in ClipRig order, and which of them the clip cannot be played without.
    private static readonly HumanBodyBones[] Humanoid =
    {
        HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
        HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
        HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
        HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
        HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
        HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
        HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
        HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
        HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
        HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
    };

    private sealed class Track
    {
        internal JointClip? Clip;
        internal float Start, Weight, Began, FadeIn, FadeOut, Ending;
        internal bool Loop, Shown, Fading;
        internal int Generation, Order;
        internal readonly float[] Mask = new float[ClipRig.Count];
    }

    private readonly Transform _root;
    private readonly Transform?[] _bone = new Transform?[ClipRig.Count];
    private readonly Quaternion[] _base = new Quaternion[ClipRig.Count];
    private readonly Vector3 _restHip;
    private readonly float _legHeight;
    private readonly IkPause _ik;
    private readonly NQuaternion[] _turn = new NQuaternion[ClipRig.Count];
    private readonly Quaternion[] _clipWorld = new Quaternion[ClipRig.Count];
    private readonly Track[] _tracks = { new(), new(), new(), new() };
    private readonly float[] _covered = new float[ClipRig.Count];
    // Each bone's local pose from just before a track first wrote it, put back when no track covers it any more: a bone the game's
    // animator does not write every frame (one it has no curve for, or a culled animator) would otherwise keep the clip's last pose.
    private readonly Quaternion[] _heldRotation = new Quaternion[ClipRig.Count];
    private readonly Vector3[] _heldPosition = new Vector3[ClipRig.Count];
    private readonly bool[] _held = new bool[ClipRig.Count];
    private int _playing, _order;

    private JointPlayer(Transform root, Quaternion[] align, Transform?[] bone, Vector3 restHip, float legHeight, Func<Transform?[], IkPause> ik)
    {
        _root = root;
        _restHip = restHip;
        _legHeight = legHeight;
        for (var j = 0; j < ClipRig.Count; j++) { _bone[j] = bone[j]; _base[j] = align[j]; }
        foreach (var b in bone) if (b != null) Matched++;
        _ik = ik(_bone);
    }

    /// <summary>How many of the canonical joints the body has.</summary>
    public int Matched { get; }

    /// <summary>A clip is posing the body now.</summary>
    public bool Playing => _playing > 0;

    /// <summary>The body's bone for canonical joint <paramref name="joint"/> (index in <see cref="ClipRig.Names"/>), or null.</summary>
    public Transform? Bone(int joint) => _bone[joint];

    /// <summary>The body's root, whose axes the clip's turns are laid in.</summary>
    public Transform Root => _root;

    /// <summary>The bones of an animator's body: its humanoid avatar names them, a rig without one is looked up by the same names (the
    /// game's enemies use them, and the fingers' names are the canonical joints' with a capital). A body that cannot be bound answers
    /// <paramref name="refusal"/>: <c>no-humanoid-body</c> (no animator, or the clip's required limbs are not there) or <c>skeleton</c> (the
    /// limbs are there but do not stand like the canonical rig, e.g. one side swapped). A body resting turned half round against its root
    /// plays facing the root (<see cref="RigMap.FacesBackwards"/>).</summary>
    internal static JointPlayer? Bind(Animator animator, Func<Transform?[], IkPause> ik, string label, out string? refusal, RigShape shape = RigShape.Body)
    {
        refusal = NoHumanoidBody;
        if (animator == null || animator.WasCollected) { Plugin.Logger.ErrorOnce("visual-joint-player-body-has-no-animator", "ForgeVisual joint player " + label + ": the body has no animator; no clip plays on it."); return null; }
        var root = animator.transform;
        var bone = new Transform?[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++)
        {
            var name = j < ClipRig.BodyCount ? Humanoid[j].ToString() : char.ToUpperInvariant(ClipRig.Names[j][0]) + ClipRig.Names[j].Substring(1);
            bone[j] = animator.GetBoneTransform(Humanoid[j]) ?? Models.Find(root, name);
        }
        return Bind(root, bone, ik, label, shape, out refusal);
    }

    internal const string NoHumanoidBody = "no-humanoid-body", SkeletonMismatch = "skeleton";

    /// <summary>The bones a <see cref="RigMap"/> declares under <paramref name="root"/>.</summary>
    internal static JointPlayer? Bind(Transform root, RigMap map, Func<Transform?[], IkPause> ik, string label)
    {
        var bone = new Transform?[ClipRig.Count];
        var lost = "";
        for (var j = 0; j < ClipRig.Count; j++)
        {
            var path = map.PathOf(j);
            if (path == null) continue;
            bone[j] = root.Find(path) ?? Models.Find(root, path);
            if (bone[j] == null) lost += (lost.Length > 0 ? ", " : "") + ClipRig.Names[j] + " (" + path + ")";
        }
        if (lost.Length > 0) { Plugin.Logger.ErrorOnce("visual-joint-player-map-names-bones-body", "ForgeVisual joint player " + label + ": the map names bones the body does not have: " + lost + "; no clip plays on it."); return null; }
        return Bind(root, bone, ik, label, RigShape.Body, out _);
    }

    private static JointPlayer? Bind(Transform root, Transform?[] bone, Func<Transform?[], IkPause> ik, string label, RigShape shape, out string? refusal)
    {
        refusal = NoHumanoidBody;
        var required = RigMap.Required(shape);
        var lacking = "";
        foreach (var j in required) if (bone[j] == null) lacking += (lacking.Length > 0 ? ", " : "") + ClipRig.Names[j];
        if (lacking.Length > 0)
        {
            Plugin.Logger.ErrorOnce("visual-joint-player-body-lacks-bones", "ForgeVisual joint player " + label + ": the body lacks the bones " + lacking + "; no clip plays on it.");
            return null;
        }
        var rest = BodyConvert.BindRest(root, root);
        lacking = "";
        var present = new bool[ClipRig.Count];
        var position = new System.Numerics.Vector3[ClipRig.Count];
        var rotation = new NQuaternion[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++)
        {
            if (bone[j] == null) continue;
            if (!rest.TryGetValue(bone[j]!.Pointer, out var at))
            {
                bone[j] = null;
                if (Array.IndexOf(required, j) >= 0) lacking += (lacking.Length > 0 ? ", " : "") + ClipRig.Names[j];
                continue;
            }
            present[j] = true;
            position[j] = BodyConvert.N(at.GetColumn(3));
            rotation[j] = BodyConvert.N(at.rotation);
        }
        if (lacking.Length > 0)
        {
            Plugin.Logger.ErrorOnce("visual-joint-player-bones-are-no-skinned", "ForgeVisual joint player " + label + ": the bones " + lacking + " are in no skinned mesh, so their rest pose is unknown; no clip plays on it.");
            return null;
        }
        // The hips stay where they rest in the root's space; only the facing turns.
        var hip = BodyConvert.U(position[0]);
        if (RigMap.FacesBackwards(present, position)) RigMap.TurnAround(present, position, rotation);
        var wrong = "";
        for (var j = 0; j < ClipRig.Count; j++)
        {
            if (bone[j] == null) continue;
            for (var other = j + 1; other < ClipRig.Count; other++)
                if (bone[other] != null && bone[other]!.Pointer == bone[j]!.Pointer) wrong += " The joints " + ClipRig.Names[j] + " and " + ClipRig.Names[other] + " are one bone.";
            var above = ClipRig.Parent[j] >= 0 ? bone[ClipRig.Parent[j]] : null;
            if (above != null && !bone[j]!.IsChildOf(above)) wrong += " " + ClipRig.Names[j] + " is not under " + ClipRig.Names[ClipRig.Parent[j]] + ".";
        }
        wrong += RigMap.Check(present, position, shape);
        refusal = SkeletonMismatch;
        if (wrong.Length > 0) { Plugin.Logger.ErrorOnce("visual-joint-player-skeleton-does-not-fit", "ForgeVisual joint player " + label + ": the skeleton does not fit the canonical rig:" + wrong + " No clip plays on it."); return null; }
        RigMap.DropBrokenFingers(present, bone);
        var aligned = JointRetarget.Aligned(present, position, rotation);
        var align = new Quaternion[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++) align[j] = BodyConvert.U(aligned[j]);
        var ankles = 0f;
        var feet = 0;
        foreach (var foot in new[] { 3, 6 }) if (present[foot]) { ankles += position[foot].Y; feet++; }
        var legHeight = feet > 0 ? hip.y - ankles / feet : 0f;
        if (shape == RigShape.Body && !(legHeight > 0.1f)) { Plugin.Logger.ErrorOnce("visual-joint-player-bodys-legs-have", "ForgeVisual joint player " + label + ": the body's legs have no height; no clip plays on it."); return null; }
        refusal = null;
        return new JointPlayer(root, align, bone, hip, legHeight, ik);
    }

    /// <summary>Starts <paramref name="clip"/> on its own track, beside the tracks already playing. Throws when
    /// <see cref="MaxTracks"/> are playing or the mask covers none of the body's bones.</summary>
    public JointTrack Play(JointClip clip, in JointPlayback playback)
    {
        if (clip == null) throw new ArgumentNullException(nameof(clip));
        var slot = Array.FindIndex(_tracks, track => track.Clip == null);
        if (slot < 0) throw new InvalidOperationException("A body plays at most " + MaxTracks + " clips at once; stop one first.");
        var track = _tracks[slot];
        (playback.Mask ?? JointMask.FullBody).Resolve(track.Mask, UnderPath);
        var any = false;
        for (var j = 0; j < ClipRig.Count; j++)
        {
            if (_bone[j] == null || j >= clip.Joints) track.Mask[j] = 0f;
            any |= track.Mask[j] > 0f;
        }
        if (!any) throw new ArgumentException("The mask covers none of this body's bones.", nameof(playback));
        for (var j = 0; j < ClipRig.Count; j++)
        {
            if (track.Mask[j] <= 0f || _held[j]) continue;
            _heldRotation[j] = _bone[j]!.localRotation;
            _heldPosition[j] = _bone[j]!.localPosition;
            _held[j] = true;
        }
        track.Clip = clip;
        track.Start = playback.Start;
        track.Loop = playback.Loop;
        track.Weight = playback.Weight;
        track.Began = Time.time;
        track.FadeIn = playback.FadeIn;
        track.FadeOut = playback.FadeOut;
        track.Ending = float.PositiveInfinity;
        track.Generation++;
        track.Order = ++_order;
        track.Shown = false;
        track.Fading = false;
        if (_playing++ == 0) Active.Add(this);
        HoldIk();
        ReportStart(slot, track);
        return new JointTrack(this, slot, track.Generation);
    }

    /// <summary>Set for a body of a player or an enemy (<see cref="Models.BindJoints(PlayerAgent)"/>): its named clips are reported to ForgeCore <c>Recording</c>.</summary>
    internal bool Reports;

    private void ReportStart(int slot, Track track)
    {
        if (!Reports || !ForgeCore.Recording.Watched || track.Clip!.Name.Length == 0) return;
        var anchor = ForgeCore.Recording.Anchor(_root);
        if (anchor.Kind == ForgeCore.AnchorKind.World) return;
        track.Shown = true;
        ForgeCore.Recording.Pose(new ForgeCore.ShownPose(ForgeCore.ShownPoseKind.Start, anchor, slot, ForgeCore.RecordingNames.Of(track.Clip.Name), Time.time - track.Start, track.Loop, track.Weight, track.FadeIn, track.FadeOut, track.Mask));
    }

    private void ReportStop(int slot, float fade)
    {
        var track = _tracks[slot];
        if (!track.Shown || !ForgeCore.Recording.Watched) return;
        if (fade <= 0f) track.Shown = false;
        ForgeCore.Recording.Pose(new ForgeCore.ShownPose(ForgeCore.ShownPoseKind.Stop, ForgeCore.Recording.Anchor(_root), slot, 0, 0f, false, 0f, 0f, fade, null));
    }

    /// <summary><see cref="Play"/> in place of <paramref name="from"/>, which fades out over <paramref name="fade"/> seconds on its own track
    /// while the new clip fades in by its playback. Quick changes can have every track fading: the oldest then gives way at once, so this
    /// does not throw for want of a track.</summary>
    public JointTrack CrossFade(in JointTrack from, JointClip clip, in JointPlayback playback, float fade)
    {
        from.Stop(fade);
        if (_playing == MaxTracks)
        {
            var oldest = 0;
            for (var slot = 1; slot < MaxTracks; slot++) if (_tracks[slot].Order < _tracks[oldest].Order) oldest = slot;
            StopSlot(oldest);
        }
        return Play(clip, playback);
    }

    private const float CaptureRate = 1f / 30f;
    private static readonly Dictionary<(string Avatar, string Controller, string Clip), JointClip> Captured = new();

    /// <summary>A clip of the game's own by name, as a <see cref="JointClip"/> for this body, read once per skeleton and kept: it names no life
    /// and nothing is exported from the game. It is looked for under <paramref name="source"/>'s controller, then among the clips the game has
    /// loaded (an enemy's clips can sit under several controllers; any one loaded is the same clip). <paramref name="animator"/> is this body's.
    /// Null with <paramref name="refusal"/> <c>not-human</c> (the body or the clip has no humanoid motion) or <c>no-such-clip</c>.</summary>
    public JointClip? CaptureGameClip(string name, Animator animator, Animator? source, out string? refusal)
    {
        refusal = null;
        if (animator.avatar == null || !animator.avatar.isHuman) { refusal = "not-human"; return null; }
        var controller = source != null ? source.runtimeAnimatorController : null;
        var key = (animator.avatar.name, controller != null ? controller.name : "", name);
        if (Captured.TryGetValue(key, out var known)) return known;
        var found = FindGameClip(name, controller);
        if (found == null) { refusal = "no-such-clip"; return null; }
        if (!found.humanMotion) { refusal = "not-human"; return null; }
        var captured = Capture(found, animator.gameObject, CaptureRate);
        captured.Name = "game/" + name;
        return Captured[key] = captured;
    }

    /// <summary>A clip of the game's own by name: under <paramref name="controller"/> first, then among the humanoid clips the game has
    /// loaded. An enemy's prefab animator holds only its default controller; the game puts the controller of the enemy's animation set on it
    /// when the enemy is set up, so a clip of another set (a Striker's <c>RU_</c> clips under <c>EnemyRunner</c>) is found only the second way.
    /// A loaded clip of that name with no humanoid motion is returned when no humanoid one is loaded, so the caller
    /// reports it as <c>not-human</c> rather than as a clip the game does not have. Null when the game has no such clip loaded.</summary>
    public static AnimationClip? FindGameClip(string name, RuntimeAnimatorController? controller)
    {
        if (controller != null)
            foreach (var clip in controller.animationClips)
                if (clip.name == name) return clip;
        AnimationClip? generic = null;
        foreach (var loaded in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<AnimationClip>()))
            if (loaded.name == name && loaded.TryCast<AnimationClip>() is { } clip)
            {
                if (clip.humanMotion) return clip;
                generic ??= clip;
            }
        return generic;
    }

    // The clip is sampled on the body's animator object, so the engine retargets a humanoid clip, frame by frame and the bones are read back
    // the way Pose writes them; the hip keeps only its height, so the action stays on the spot. The bones and the root go back to where they were.
    private JointClip Capture(AnimationClip clip, GameObject animated, float rate)
    {
        var frames = Math.Max(2, Mathf.CeilToInt(clip.length / rate) + 1);
        var joints = ClipRig.BodyCount;
        var rotation = new Quaternion[ClipRig.Count];
        var position = new Vector3[ClipRig.Count];
        for (var j = 0; j < ClipRig.Count; j++) if (_bone[j] is { } bone) { rotation[j] = bone.localRotation; position[j] = bone.localPosition; }
        var rootPosition = _root.localPosition;
        var rootRotation = _root.localRotation;
        var turns = new NQuaternion[frames * joints];
        var hips = new System.Numerics.Vector3[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            clip.SampleAnimation(animated, Math.Min(frame * rate, clip.length));
            var unfacing = Quaternion.Inverse(_root.rotation);
            for (var j = 0; j < joints; j++)
            {
                // A joint this body lacks turns with its parent.
                var turn = _bone[j] is { } bone ? unfacing * bone.rotation * Quaternion.Inverse(_base[j]) : ClipRig.Parent[j] >= 0 ? new Quaternion(turns[frame * joints + ClipRig.Parent[j]].X, turns[frame * joints + ClipRig.Parent[j]].Y, turns[frame * joints + ClipRig.Parent[j]].Z, turns[frame * joints + ClipRig.Parent[j]].W) : Quaternion.identity;
                turns[frame * joints + j] = new NQuaternion(turn.x, turn.y, turn.z, turn.w);
            }
            var up = (_root.InverseTransformPoint(_bone[0]!.position) - _restHip).y * (ClipRig.LegHeight / _legHeight);
            hips[frame] = new System.Numerics.Vector3(0f, up, 0f);
        }
        for (var j = 0; j < ClipRig.Count; j++) if (_bone[j] is { } bone) { bone.localRotation = rotation[j]; bone.localPosition = position[j]; }
        _root.localPosition = rootPosition;
        _root.localRotation = rootRotation;
        return JointClip.FromTurns(rate, joints, turns, hips);
    }

    /// <summary>Every clip stops: each bone goes back to where it was before a clip first moved it, the IK runs again and the game's animator poses the body from the next frame.</summary>
    public void Stop()
    {
        for (var slot = 0; slot < MaxTracks; slot++) if (_tracks[slot].Clip != null) StopSlot(slot);
    }

    /// <summary>Every clip fades back to the game's pose over <paramref name="seconds"/>, then ends.</summary>
    public void Stop(float seconds)
    {
        for (var slot = 0; slot < MaxTracks; slot++) if (_tracks[slot].Clip != null) FadeSlot(slot, seconds);
    }

    internal void FadeSlot(int slot, float seconds)
    {
        if (seconds <= 0f) { StopSlot(slot); return; }
        var track = _tracks[slot];
        track.FadeOut = seconds;
        track.Fading = true;
        track.Ending = Math.Min(track.Ending, Time.time + seconds);
        ReportStop(slot, seconds);
    }

    internal void SeekSlot(int slot, float elapsed)
    {
        var track = _tracks[slot];
        track.Start = Time.time - elapsed;
        // A clip that ends by itself ends where its new start puts the end (worked out again when it is next posed); one fading out for a stop keeps the end it was given.
        if (!track.Fading) track.Ending = float.PositiveInfinity;
    }

    internal bool Live(int slot, int generation) => _tracks[slot].Clip != null && _tracks[slot].Generation == generation;

    internal float StartOf(int slot) => _tracks[slot].Start;

    internal void StopSlot(int slot)
    {
        if (_tracks[slot].Clip == null) return;
        ReportStop(slot, 0f);
        _tracks[slot].Clip = null;
        if (--_playing == 0) { Active.Remove(this); _ik.Release(); Array.Clear(_covered); }
        else HoldIk();
        for (var j = 0; j < ClipRig.Count; j++)
        {
            if (!_held[j] || _covered[j] > 0f) continue;
            _held[j] = false;
            var bone = _bone[j];
            if (bone == null || bone.WasCollected) continue;
            bone.localRotation = _heldRotation[j];
            bone.localPosition = _heldPosition[j];
        }
    }

    private bool UnderPath(int joint, string path, bool subtree)
    {
        var bone = _bone[joint];
        var at = _root.Find(path);
        return bone != null && at != null && (bone.Pointer == at.Pointer || (subtree && bone.IsChildOf(at)));
    }

    private void HoldIk()
    {
        Array.Clear(_covered);
        foreach (var track in _tracks)
            if (track.Clip != null)
                for (var j = 0; j < ClipRig.Count; j++) _covered[j] = Mathf.Max(_covered[j], track.Mask[j]);
        _ik.Hold(_covered);
    }

    /// <summary>Called every frame after the game's animation (LateUpdate): poses every body a clip is playing on.</summary>
    internal static void TickAll()
    {
        var now = Time.time;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var player = Active[i];
            if (player._root == null || player._root.WasCollected)
            {
                Active.RemoveAt(i);
                foreach (var track in player._tracks) track.Clip = null;
                player._playing = 0;
                continue;
            }
            player.PoseAll(now);
        }
    }

    /// <summary>Stops every clip (the level ended or a checkpoint reloaded): the bodies they were on are gone or rebuilt.</summary>
    internal static void StopAll()
    {
        for (var i = Active.Count - 1; i >= 0; i--) Active[i].Stop();
    }

    private void PoseAll(float now)
    {
        // Oldest first, so on a bone two tracks share the later one wins.
        for (var done = 0; done < _playing;)
        {
            Track? next = null;
            var slot = -1;
            for (var s = 0; s < MaxTracks; s++)
            {
                var track = _tracks[s];
                if (track.Clip == null || track.Order <= _lastOrder || (next != null && track.Order >= next.Order)) continue;
                next = track;
                slot = s;
            }
            if (next == null) break;
            _lastOrder = next.Order;
            var time = now - next.Start;
            if (!next.Loop && float.IsPositiveInfinity(next.Ending)) next.Ending = next.Began + (next.Clip!.Duration - (next.Began - next.Start));
            if (now >= next.Ending) { StopSlot(slot); continue; }
            var envelope = next.FadeIn > 0f ? Mathf.Clamp01((now - next.Began) / next.FadeIn) : 1f;
            if (next.FadeOut > 0f) envelope = Mathf.Min(envelope, Mathf.Clamp01((next.Ending - now) / next.FadeOut));
            Pose(next, time, envelope);
            done++;
        }
        _lastOrder = 0;
    }

    private int _lastOrder;

    private void Pose(Track track, float time, float envelope)
    {
        track.Clip!.Evaluate(time, track.Loop, _turn, out var travel);
        var facing = _root.rotation;
        var joints = track.Clip.Joints;
        for (var j = 0; j < joints; j++)
        {
            var t = _turn[j];
            _clipWorld[j] = facing * new Quaternion(t.X, t.Y, t.Z, t.W) * _base[j];
        }
        var world = track.Mask[0] > 0f;
        for (var j = 0; j < joints; j++)
        {
            var weight = track.Mask[j] * track.Weight * envelope;
            var bone = _bone[j];
            if (weight <= 0f || bone == null || bone.WasCollected) continue;
            Quaternion target;
            if (world) target = _clipWorld[j];
            else
            {
                // The clip's turn of this bone against its parent, laid on the parent where it is now.
                // A body without the parent bone (no UpperChest) takes the nearest bone above it that it has.
                var up = ClipRig.Parent[j];
                while (up >= 0 && _bone[up] == null) up = ClipRig.Parent[up];
                if (up < 0) continue;
                target = _bone[up]!.rotation * Quaternion.Inverse(_clipWorld[up]) * _clipWorld[j];
            }
            bone.rotation = weight >= 1f ? target : Quaternion.Slerp(bone.rotation, target, weight);
            if (j == 0)
            {
                var scale = _legHeight / ClipRig.LegHeight;
                var at = _root.TransformPoint(_restHip + new Vector3(travel.X, travel.Y, travel.Z) * scale);
                bone.position = weight >= 1f ? at : Vector3.Lerp(bone.position, at, weight);
            }
        }
    }
}
