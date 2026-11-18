using System.Diagnostics;
using BepInEx;
using ForgeCore;
using ForgeSkin.Format;
using ForgeUI;
using ForgeVisual;
using Gear;
using Player;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace ForgeSkin.Plugin;

// Weapon inspect (PLAN §4.14). The inspect key plays one of the held gun's authored motions in the local player's first-person hands
// (Core/InspectMotion: the motion table, inspect-motions.json embedded here, entries replaced by BepInEx/config/ForgeSkin.inspect.json).
// A gun the table names by its offline gear id plays its own list in turn; any other gun a motion picked from its measured length, said
// once in the log. When the look starts, the gun's drawn bounds (in its own space), the item camera, both hands' IK targets and both
// arms' bones are measured and the motion is solved for them (InspectLook: the grip hand's place and elbow swivel per key, the wrist
// kept within its limits, the off hand's places on the gun taken from the bounds, every frame held to the frame rules); a look whose
// frames break a rule in these hands is not played and says so once (skin-inspect-unframed). Each frame, before the first-person holder's
// LateUpdate puts the arms' IK targets on the grips (GameHooks.FirstPersonHolderLateUpdate), the gun is put where the motion has it
// between its keys (the game's pose for a rest key), so the grip hand follows it; after the holder, the off hand's IK target is moved off
// its grip where the motion says. The holder's Update writes the gun's pose and the LateUpdate the targets again every frame, so nothing
// builds up. Every player's motion goes to the others as a per-player state on ForgeSkin's Net channel (the motion's number); each body
// plays that motion's named clip "ForgeSkin/inspect/<id>" on its right hand (third person shows the wrist turning; its off hand stays on
// the gun), which ForgeCore's joint player reports to Recording, so a replay shows it. No input is held back; what the player does (fire,
// aim, reload, melee, use, sprint, a hit, a menu) puts the gun back within FadeOut. Nothing here reads a skin; no gun is a branch here.
static class Inspect
{
    const string ClipPrefix = ForgeSkinPlugin.Guid + "/inspect/";
    const float HandYaw = 0.5f;                      // the share of the motion's turn the third-person wrist bends
    const float BodyFadeIn = 0.2f, BodyFadeOut = 0.3f;

    struct State { public uint Motion; }

    sealed class Body { public IntPtr Agent; public JointPlayer? Joints; public JointTrack Track; }

    static KeyAction? _key;
    static PlayerStateSlot<State>? _slot;
    static readonly InspectTable Table = new();
    static readonly Dictionary<uint, JointClip> Clips = new();
    static readonly Dictionary<uint, int> Next = new();      // per gun: which of its motions the next press plays
    static readonly HashSet<uint> Told = new();               // gun ids whose fallback, and motion numbers missing here, were logged
    static readonly JointMask Hand = new JointMask().Add(BodyPart.RightHand);
    static readonly Dictionary<ulong, Body> Bodies = new();

    // The look playing on this machine's first-person gun; null: none.
    static InspectLook? _look;
    static Transform? _item, _view, _offTarget, _gripTarget;
    static Camera? _camera;
    static IntPtr _gun, _damage;
    static float _began, _stopped = -1f, _restView;
    // This frame's: time into the motion, its weight, the view's rotation and the gun's pose in view space (set before the holder's
    // LateUpdate, read after it).
    static float _time, _weight;
    static NQuaternion _viewTurn, _gunTurn;
    static NVector3 _gunAt;
    // The grip arm's IK while a look plays: bent toward _elbow (the solve's elbow, swung out), with the game's own bend setting kept to
    // put back.
    static RootMotion.FinalIK.IKSolverLimb? _bend;
    static RootMotion.FinalIK.IKSolverLimb.BendModifier _bendWas;
    static Transform? _bendGoalWas, _elbow;
    static float _bendWeightWas;

    public static void Register(NetChannel? channel)
    {
        try
        {
            using (var s = typeof(Inspect).Assembly.GetManifestResourceStream("ForgeSkin.inspect-motions.json")!)
            using (var r = new StreamReader(s)) Table.Add(r.ReadToEnd());
            var own = Path.Combine(Paths.ConfigPath, "ForgeSkin.inspect.json");
            if (File.Exists(own))
                try { Table.Add(File.ReadAllText(own)); ForgeSkinPlugin.Info($"inspect motions from {own}"); }
                catch (Exception e) when (e is FormatException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
                { ForgeSkinPlugin.Fail("skin-inspect-failed", $"{own} is not used: {e.Message}"); }
            foreach (var motion in Table.Motions) Clips[motion.Number] = BuildClip(motion);
            _key = UIKit.Mod(ForgeSkinPlugin.Guid).AddKeyAction("Inspect", "keys.inspect", "keys.group", "I", KeyMode.Press, "on-foot");
            GameHooks.FirstPersonHolderLateUpdate.Before(Pose);
            GameHooks.FirstPersonHolderLateUpdate.After(static (_, _) => PoseOffHand());
            GameHooks.PlayerReceived.After(static (call, _) => { if (_item != null && call.Receiver.Pointer == _damage) Stop(); });
            GameHooks.ItemUnwielded.After(static (item, _) => { if (item.Pointer == _gun) Stop(cut: true); });
            GameHooks.BulletWeaponUnwielded.After(static (item, _) => { if (item.Pointer == _gun) Stop(cut: true); });
            GameEvents.LevelEnded += Reset;
            GameEvents.CheckpointReloaded += Reset;
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-inspect-failed", $"weapon inspect is off: {e.Message}", e); _key = null; return; }
        if (channel == null) return; // no channel (logged as skin-look-failed): the motion is seen in first person only
        _slot = channel.PlayerState<State>("inspect");
        _slot.Changed += Seen;
    }

    // A motion's third-person clip: the right wrist rolls about the forearm with the motion's roll (within the wrist's twist) and bends
    // with a share of its turn. Only the hand is masked, so the rest of the arm keeps the game's pose and the off hand stays on the gun.
    static JointClip BuildClip(InspectMotion motion)
    {
        const float rate = 1f / 30f;
        const int joints = ClipRig.BodyCount;
        int frames = (int)MathF.Ceiling(motion.Seconds / rate) + 1, hand = JointMask.Joints(BodyPart.RightHand)[0];
        var turns = new NQuaternion[frames * joints];
        Array.Fill(turns, NQuaternion.Identity);
        var forearm = ClipRig.RestDirection(JointMask.Joints(BodyPart.RightForearm)[0], hand);
        for (var f = 0; f < frames; f++)
        {
            motion.Intent(f * rate, out var turn, out var roll);
            roll = -Math.Clamp(roll, -InspectArm.Twist, InspectArm.Twist); // the same turn the first-person solve gives a roll
            turns[f * joints + hand] = NQuaternion.CreateFromAxisAngle(forearm, roll * MathF.PI / 180f) * NQuaternion.CreateFromAxisAngle(NVector3.UnitY, turn * HandYaw * MathF.PI / 180f);
        }
        return JointClip.FromTurns(rate, joints, turns, new NVector3[frames], ClipPrefix + motion.Id);
    }

    // Main-thread tick (ForgeSkinPlugin.Update): the key, and what puts a motion back.
    public static void Tick()
    {
        if (_key == null) return;
        if (_press != null) { if (_press.Solve.IsCompleted) Begin(); return; }
        if (_item == null) { if (_key.Pressed) Begin(); return; }
        // Fading back: the holder's step clears it; a holder that stopped stepping (the view left the first person) leaves it here.
        if (_stopped >= 0f) { if (Frame.GameTime - _stopped > InspectMotion.FadeOut) Clear(); return; }
        var local = Players.Local;
        if (local == null || local.WasCollected || Frame.GameTime - _began >= _look!.Motion.Seconds) { Stop(cut: true); return; }
        if (Players.Motion(local) is not (PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill) || UIKit.GameScreenOpen
            || InputLease.Down(InputAction.Fire) || InputLease.Down(InputAction.Aim) || InputLease.Down(InputAction.Reload)
            || InputLease.Down(InputAction.Melee) || InputLease.Down(InputAction.Use)) Stop();
    }

    // A press of the key: a gun in the hands at rest (not aiming, sprinting, reloading or in the game's equip sequence) plays its next
    // motion, solved for this gun, camera and pair of arms. A press during a motion is ignored, so a motion never jumps.
    static void Begin()
    {
        if (_press != null) { Start(); return; } // the press's solve is done
        var pressed = Stopwatch.GetTimestamp();
        var local = Players.Local;
        if (local == null || local.WasCollected || UIKit.GameScreenOpen || InputLease.Down(InputAction.Aim)
            || Players.Motion(local) is not (PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill)) return;
        var holder = local.FPItemHolder;
        var item = holder != null ? holder.WieldedItem : null;
        if (item == null || item.TryCast<BulletWeapon>() == null || holder!.ItemIsBusy || holder.IsPlayingAnimationSequence || holder.ItemAimTrigger || item.IsReloading) return;
        var camera = holder.m_ItemLayerCamera;
        var arms = holder.FPSArms;
        if (camera == null || arms == null) return;
        var gunNode = item.transform;
        var view = camera.transform;
        if (!Measure(gunNode, out var centre, out var extents))
        {
            ForgeSkinPlugin.Fail("skin-inspect-failed", $"weapon inspect found no shown mesh under {item.name}; no look is played");
            return;
        }
        var right = Arm(arms.m_rightHandIK, arms.m_rightHandIKTarget, view);
        var left = Arm(arms.m_leftHandIK, arms.m_leftHandIKTarget, view);
        if (right == null || left == null)
        {
            ForgeSkinPlugin.Fail("skin-inspect-failed", "weapon inspect found no first-person arm bones or hand targets; no look is played");
            return;
        }
        var viewTurn = Q(view.rotation);
        var toView = NQuaternion.Inverse(viewTurn);
        var parts = item.GearPartHolder;
        var mag = parts != null && parts.m_aMag != null ? N(gunNode.InverseTransformPoint(parts.m_aMag.position)) : (NVector3?)null;
        var rig = new InspectRig
        {
            TanHalf = MathF.Tan(camera.fieldOfView * 0.5f * MathF.PI / 180f),
            Aspect = camera.aspect,
            Near = camera.nearClipPlane,
            Centre = centre,
            Extents = extents,
            GunAt = N(view.InverseTransformPoint(gunNode.position)),
            Gun = toView * Q(gunNode.rotation),
            GripAt = right.Value.At,
            Grip = right.Value.Turn,
            GripArm = right.Value.Arm,
            OffAt = left.Value.At,
            Off = left.Value.Turn,
            OffArm = left.Value.Arm,
            Mag = mag,
        };
        var gear = parts != null && parts.GearIDRange is { } range ? SkinnedGear.GearIdOf(range) : 0u;
        var length = extents.Z * 2f;
        var motions = Table.For(gear, length, out var fallback);
        if (fallback && Told.Add(gear)) ForgeSkinPlugin.Note($"inspect: {item.name} (gear {gear}) has no motion of its own; it plays {motions[0].Id}, picked from its length {length:0.00} m");
        Next.TryGetValue(gear, out var next);
        var motion = motions[next % motions.Count];
        Next[gear] = next + 1;
        // The solve (10–75 ms) runs off the main thread on what was just measured; the look starts on the tick it is done.
        var measured = (Stopwatch.GetTimestamp() - pressed) * 1000.0 / Stopwatch.Frequency;
        _press = new Press(Task.Run(() =>
        {
            var from = Stopwatch.GetTimestamp();
            var look = new InspectLook(motion, rig);
            return (look, (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency);
        }), item, camera, arms, rig.TanHalf, gear, fallback, extents, measured);
    }

    sealed record Press(Task<(InspectLook Look, double Ms)> Solve, ItemEquippable Item, Camera Camera, PlayerFPSBody Arms, float RestView, uint Gear, bool Fallback, NVector3 Extents, double MeasuredMs);
    static Press? _press;

    // The solve of the last press is done: the look starts, if the same gun is still in the hands at rest.
    static void Start()
    {
        var press = _press!;
        _press = null;
        var (look, solved) = press.Solve.Result;
        var local = Players.Local;
        if (local == null || local.WasCollected) return;
        var holder = local.FPItemHolder;
        var item = press.Item;
        if (holder == null || holder.WieldedItem == null || holder.WieldedItem.Pointer != item.Pointer || press.Camera.WasCollected || press.Arms.WasCollected
            || holder.ItemIsBusy || holder.ItemAimTrigger || Players.Motion(local) is not (PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill))
            return; // the player moved on while it was solved: a press like any other that found the gun busy
        var (camera, arms, gear, motion, extents) = (press.Camera, press.Arms, press.Gear, look.Motion, press.Extents);
        var gunNode = item.transform;
        var view = camera.transform;
        var length = extents.Z * 2f;
        var fallback = press.Fallback;
        if (!float.IsNaN(look.Fault))
        {
            ForgeSkinPlugin.Fail("skin-inspect-unframed", $"inspect {item.name} (gear {gear}), {motion.Id}: at {look.Fault:0.00} s {look.FaultRule} in this pair of hands; the look is not played");
            return;
        }
        _look = look;
        _camera = camera;
        _restView = press.RestView;
        _item = gunNode;
        _view = view;
        _offTarget = arms.m_leftHandIKTarget;
        _gripTarget = arms.m_rightHandIKTarget;
        BendToward(arms.m_rightHandIK);
        _gun = item.Pointer;
        _damage = local.Damage != null ? local.Damage.Pointer : IntPtr.Zero;
        _began = Frame.GameTime;
        _stopped = -1f;
        _slot?.Publish(new State { Motion = motion.Number }, _began);
        Play(local, motion.Number, _began);
        var views = string.Join(" ", _look.View.Select(v => (MathF.Atan(v) * 360f / MathF.PI).ToString("0")));
        var swivels = string.Join(" ", _look.Swivel.Select(v => v.ToString("0")));
        ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}): {motion.Id}{(fallback ? " (fallback)" : "")}, bounds {extents.X * 2f:0.00} x {extents.Y * 2f:0.00} x {length:0.00} m, fov {camera.fieldOfView:0.#} aspect {camera.aspect:0.##} near {camera.nearClipPlane:0.###}, item view per key {views}, elbow swivel per key {swivels}; measured in {press.MeasuredMs:0.0} ms on the press, solved in {solved:0} ms off the main thread");
    }

    // The gun's drawn bounds in its own space: every shown mesh under it (parts, attachments, a skin's models), each by its mesh's box
    // where it has one, else by its world box.
    // A mesh whose box alone makes the gun's box more than twice as wide or tall as every other mesh's together (a sling or rope whose
    // mesh box is a large placeholder) is left out and logged once; the gun's body cannot be, since the other parts span about as much.
    static bool Measure(Transform gun, out NVector3 centre, out NVector3 extents)
    {
        var boxes = new List<(string Name, NVector3 Min, NVector3 Max)>();
        foreach (var r in gun.GetComponentsInChildren<Renderer>())
        {
            if (!Models.IsMesh(r) || !Models.Drawn(r)) continue;
            Box(r, gun, out var lo, out var hi);
            boxes.Add((r.name, lo, hi));
        }
        var min = new NVector3(float.MaxValue);
        var max = new NVector3(float.MinValue);
        for (var i = 0; i < boxes.Count; i++)
        {
            NVector3 rmin = new(float.MaxValue), rmax = new(float.MinValue);
            for (var k = 0; k < boxes.Count; k++)
                if (k != i) (rmin, rmax) = (NVector3.Min(rmin, boxes[k].Min), NVector3.Max(rmax, boxes[k].Max));
            NVector3 all = NVector3.Max(rmax, boxes[i].Max) - NVector3.Min(rmin, boxes[i].Min), rest = rmax - rmin;
            if (boxes.Count > 1 && (all.X > 2f * rest.X || all.Y > 2f * rest.Y))
            {
                if (Told.Add(Fnv.Hash32(boxes[i].Name)))
                    ForgeSkinPlugin.Note($"inspect: {gun.name}'s mesh {boxes[i].Name} ({all.X:0.00} x {all.Y:0.00} m with it, {rest.X:0.00} x {rest.Y:0.00} without) is left out of its bounds");
                continue;
            }
            min = NVector3.Min(min, boxes[i].Min);
            max = NVector3.Max(max, boxes[i].Max);
        }
        centre = (min + max) / 2f;
        extents = (max - min) / 2f;
        return min.X <= max.X;
    }

    // One mesh's box in the gun's space: its mesh's box where it has one, else its world box.
    static void Box(Renderer r, Transform gun, out NVector3 min, out NVector3 max)
    {
        min = new NVector3(float.MaxValue);
        max = new NVector3(float.MinValue);
        var mesh = r.TryCast<MeshRenderer>() != null && r.GetComponent<MeshFilter>() is { } filter ? filter.sharedMesh : null;
        var box = mesh != null ? mesh.bounds : r.bounds;
        var space = mesh != null ? r.transform : null;
        for (var i = 0; i < 8; i++)
        {
            var corner = box.center + Vector3.Scale(box.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
            var p = N(gun.InverseTransformPoint(space != null ? space.TransformPoint(corner) : corner));
            min = NVector3.Min(min, p);
            max = NVector3.Max(max, p);
        }
    }

    // One arm, in view space: its two-bone IK's bones and the hand's IK target the game set last frame.
    static (InspectArm Arm, NVector3 At, NQuaternion Turn)? Arm(RootMotion.FinalIK.LimbIK? ik, Transform? target, Transform view)
    {
        var solver = ik != null ? ik.solver : null;
        Transform? shoulder = solver?.bone1?.transform, elbow = solver?.bone2?.transform, wrist = solver?.bone3?.transform;
        if (shoulder == null || elbow == null || wrist == null || target == null) return null;
        var at = N(view.InverseTransformPoint(target.position));
        var turn = NQuaternion.Inverse(Q(view.rotation)) * Q(target.rotation);
        return (new InspectArm(N(view.InverseTransformPoint(shoulder.position)), N(view.InverseTransformPoint(elbow.position)),
            N(view.InverseTransformPoint(wrist.position)), at, turn), at, turn);
    }

    // The holder is about to put the arms on the grips: put the gun where the motion has it this frame.
    static void Pose(FirstPersonItemHolder holder)
    {
        if (_look == null || _item == null || _view == null) return;
        var now = Frame.GameTime;
        _weight = _stopped < 0f ? 1f : 1f - (now - _stopped) / InspectMotion.FadeOut;
        if (_weight <= 0f) { Clear(); return; }
        _time = now - _began;
        _viewTurn = Turn(_view);
        var toView = NQuaternion.Inverse(_viewTurn);
        _look.GunPose(_time, _weight, N(_view.InverseTransformPoint(_item.TransformPoint(Vec.Zero))), toView * Turn(_item), _restView, out _gunAt, out _gunTurn, out var view);
        _item.SetPositionAndRotation(_view.TransformPoint(U(_gunAt)), U(_viewTurn * _gunTurn));
        _camera!.fieldOfView = MathF.Atan(view) * 360f / MathF.PI;
    }

    // The holder has put both hands' IK targets on the gun's grips: while the gun is free (spun, tossed) keep the grip hand where it held
    // the gun, and move the off hand where the motion has it. The hand keeps its closed grip pose: the first-person arms' fingers are not
    // posed by anything this plugin or ForgeCore drives.
    static void PoseOffHand()
    {
        if (_look == null || _view == null) return;
        if (_elbow != null) _elbow.position = _view.TransformPoint(U(_look.GripElbow()));
        if (_look.Free && _gripTarget != null)
        {
            _look.GripPose(_look.HeldAt, _look.HeldTurn, out var hand, out var handTurn);
            _gripTarget.SetPositionAndRotation(_view.TransformPoint(U(hand)), U(_viewTurn * handTurn));
        }
        if (_offTarget == null || !_look.HandOff(_time)) return;
        var toView = NQuaternion.Inverse(_viewTurn);
        _look.HandPose(_time, _weight, _gunAt, _gunTurn, N(_view.InverseTransformPoint(_offTarget.TransformPoint(Vec.Zero))), toView * Turn(_offTarget), out var at, out var turn);
        _offTarget.SetPositionAndRotation(_view.TransformPoint(U(at)), U(_viewTurn * turn));
    }

    // The grip arm's two-bone IK bends toward a goal the look moves (its elbow swung out), instead of its own bend setting, until Clear.
    static void BendToward(RootMotion.FinalIK.LimbIK? ik)
    {
        var solver = ik != null ? ik.solver : null;
        if (solver == null) { ForgeSkinPlugin.Fail("skin-inspect-failed", "weapon inspect found no grip arm IK solver; the elbow keeps the game's bend"); return; }
        if (_elbow == null || _elbow.WasCollected)
        {
            var go = new GameObject("ForgeSkin inspect elbow");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _elbow = go.transform;
        }
        (_bend, _bendWas, _bendGoalWas, _bendWeightWas) = (solver, solver.bendModifier, solver.bendGoal, solver.bendModifierWeight);
        solver.bendGoal = _elbow;
        solver.bendModifier = RootMotion.FinalIK.IKSolverLimb.BendModifier.Goal;
        solver.bendModifierWeight = 1f;
    }

    // A transform's world rotation from its axes (read through methods: no property of the game's is read each frame).
    static NQuaternion Turn(Transform t) => InspectLook.Look(N(t.TransformDirection(Vec.Forward)), N(t.TransformDirection(Vec.Up)));

    static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);

    static NQuaternion Q(Quaternion q) => new(q.x, q.y, q.z, q.w);

    static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    static Quaternion U(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    // Puts the gun back: over FadeOut, or at once (`cut`: the motion ran out, or the gun left the hands).
    static void Stop(bool cut = false)
    {
        if (_item == null) return;
        if (cut) Clear();
        else if (_stopped < 0f) _stopped = Frame.GameTime;
        _slot?.Clear();
        if (Players.Local is { WasCollected: false } local && Bodies.TryGetValue(local.Owner.Lookup, out var body)) body.Track.Stop(BodyFadeOut);
    }

    // The item camera gets back the field of view it had when the look started (the motion may have widened it).
    static void Clear()
    {
        if (_camera != null && !_camera.WasCollected) _camera.fieldOfView = MathF.Atan(_restView) * 360f / MathF.PI;
        if (_bend != null && !_bend.WasCollected)
        {
            _bend.bendModifier = _bendWas;
            _bend.bendGoal = _bendGoalWas;
            _bend.bendModifierWeight = _bendWeightWas;
        }
        (_bend, _bendGoalWas) = (null, null);
        _camera = null;
        _item = _view = _offTarget = _gripTarget = null;
        _look = null;
        _gun = IntPtr.Zero;
        _stopped = -1f;
    }

    static void Reset()
    {
        Clear();
        _press = null;
        _slot?.Clear();
        Bodies.Clear(); // the bodies are gone or rebuilt; ForgeCore stopped their clips
    }

    // A teammate started or stopped a motion.
    static void Seen(ulong player, bool active)
    {
        if (active && _slot!.TryGet(player, out var state, out var start) && ForgeCore.Level.PlayerByLookup(player) is { } agent) Play(agent, state.Motion, start);
        else if (Bodies.TryGetValue(player, out var body)) body.Track.Stop(BodyFadeOut);
    }

    // A motion this machine's table lacks (the other player's table was changed) is not shown on that body.
    static void Play(PlayerAgent agent, uint motion, float start)
    {
        if (!Clips.TryGetValue(motion, out var clip)) { if (Told.Add(motion)) ForgeSkinPlugin.Note($"inspect: motion {motion:x8} of player {agent.Owner.Lookup} is not in this table; that body shows none"); return; }
        var player = agent.Owner.Lookup;
        if (!Bodies.TryGetValue(player, out var body)) Bodies.Add(player, body = new Body());
        if (body.Agent != agent.Pointer || body.Joints?.Root == null || body.Joints.Root.WasCollected)
        {
            body.Agent = agent.Pointer;
            body.Joints = Models.BindJoints(agent); // null logged once by ForgeCore: that body shows no inspect
        }
        if (body.Joints != null) body.Track = body.Joints.CrossFade(body.Track, clip, new JointPlayback(start, false, Hand, 1f, BodyFadeIn, BodyFadeOut), BodyFadeOut);
    }
}
