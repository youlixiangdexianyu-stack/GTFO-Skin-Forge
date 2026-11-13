using System;
using System.Collections.Generic;
using Enemies;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// Outlines round enemies and any object: one line round the outer edge of the target's whole silhouette, laid over the finished frame
/// in the colour given, so it shows in fog and darkness. Two ways in, drawn together:
/// <list type="bullet">
/// <item><c>Add</c>: an outline on one object (an enemy, a door, an item) with a colour, a priority and a time. When several mods
/// outline the same object, the highest priority is drawn (the latest added among equals).</item>
/// <item><see cref="AddEnemyOutline"/>: the automatic enemy outline: while switched on, the nearest enemies in view are outlined in a
/// colour for their state (asleep, awake, hunting).</item>
/// </list>
/// An outline is depth tested by default (behind a wall nothing shows, behind cover only the visible part). X-ray (seen through walls,
/// round the whole silhouette) is an opt-in for developers: <c>xRay: true</c> on <c>Add</c> or <see cref="EnemyOutlineStyle.XRay"/>; it
/// is off unless asked for. Forge Frames' cockpit does not use it: its enemies are outlined only where the camera sees them. The player turns every outline off in ForgeVisual's config (<c>Outlines / Enabled</c>). Main thread only.
/// </summary>
public static class Outlines
{
    private static readonly Dictionary<string, EnemyOutline> Registered = new(StringComparer.Ordinal);

    /// <summary>The player's switch: false draws no outline of any mod.</summary>
    public static bool Enabled => Plugin.Outlines.Value;

    /// <summary>Outlines <paramref name="target"/> (and every mesh under it) in <paramref name="color"/> for <paramref name="seconds"/>
    /// (0: until removed). The renderers are read now; an object whose model changes later is outlined again by adding it again.
    /// Returns null (logged) when the target is missing or a value is out of range. Adding allocates a handle; drawing allocates nothing.</summary>
    /// <param name="modId">Your plugin ID, for the error log.</param>
    /// <param name="target">The object; an enemy's own body cuts (a shot-off head or limb) are followed.</param>
    /// <param name="color">The line's colour; alpha is its opacity.</param>
    /// <param name="priority">Against other outlines on the same object, and against the automatic enemy outline
    /// (<see cref="EnemyOutlineStyle.Priority"/>): the highest is drawn.</param>
    /// <param name="seconds">How long it lasts; 0 until <see cref="OutlineHandle.Remove"/>.</param>
    /// <param name="xRay">True draws the line through walls (seen behind cover). Off by default; turn it on only where your mod means to
    /// show what is behind a wall.</param>
    /// <example><c>var mark = Outlines.Add(MyPlugin.Id, door.gameObject, new OutlineColor(0.3f, 0.9f, 1f), priority: 10, seconds: 5f, xRay: true);</c></example>
    public static OutlineHandle? Add(string modId, GameObject target, OutlineColor color, int priority = 0, float seconds = 0f, bool xRay = false)
    {
        if (ForgeCore.NativeObjects.Gone(target))
        {
            Plugin.Logger.ErrorOnce("visual-outline-not-added-target-object-missing", modId + ": outline not added: the target object is missing.");
            return null;
        }
        if (OutlineRules.Problem(color, seconds) is { } problem)
        {
            Plugin.Logger.ErrorOnce("visual-outline-not-added", modId + ": outline not added to " + target.name + ": " + problem + ".");
            return null;
        }
        return OutlineDraw.Claim(target, color, priority, seconds, xRay);
    }

    /// <summary><see cref="Add(string, GameObject, OutlineColor, int, float, bool)"/> on an enemy: its own body cuts (a shot-off head or
    /// limb) are followed, and it meets the automatic enemy outline by <paramref name="priority"/>.</summary>
    public static OutlineHandle? Add(string modId, EnemyAgent enemy, OutlineColor color, int priority = 0, float seconds = 0f, bool xRay = false)
    {
        if (ForgeCore.NativeObjects.Gone(enemy))
        {
            Plugin.Logger.ErrorOnce("visual-outline-not-added-enemy-missing", modId + ": outline not added: the enemy is missing.");
            return null;
        }
        return Add(modId, enemy.gameObject, color, priority, seconds, xRay);
    }

    /// <summary>
    /// Adds a switch for the automatic enemy outline: while it is <see cref="EnemyOutline.Active"/>, the nearest enemies in view are
    /// outlined in a colour for their state. The enemies are the game's own (ForgeCore's registry); you give only the look. Outlines are
    /// drawn while any mod's switch is on, in the style of the switch turned on last.
    /// </summary>
    /// <param name="modId">Your plugin ID.</param>
    /// <param name="name">Unique and stable inside your mod, for example <c>Cockpit</c>.</param>
    /// <param name="style">Range, colours, width, X-ray and priority; null for the defaults.</param>
    /// <example><c>var outline = Outlines.AddEnemyOutline(MyPlugin.Id, "Cockpit"); outline.Active = worn;</c></example>
    public static EnemyOutline? AddEnemyOutline(string modId, string name, EnemyOutlineStyle? style = null)
    {
        var id = (modId ?? "") + "/" + (name ?? "");
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(name))
        {
            Plugin.Logger.ErrorOnce("visual-enemy-outline-not-added-give-your", "Enemy outline '" + id + "' not added: give your plugin ID and a name.");
            return null;
        }
        style ??= new EnemyOutlineStyle();
        if (style.Problem() is { } problem) { Plugin.Logger.ErrorOnce("visual-enemy-outline-not-added", id + ": enemy outline not added: " + problem + "."); return null; }
        if (Registered.ContainsKey(id)) { Plugin.Logger.ErrorOnce("visual-enemy-outline-not-added-name-already", id + ": enemy outline not added: the name is already taken."); return null; }
        var outline = new EnemyOutline(id, style);
        Registered.Add(id, outline);
        OutlineDraw.Used();
        return outline;
    }

    internal static void Forget(EnemyOutline outline)
    {
        if (Registered.TryGetValue(outline.Id, out var current) && ReferenceEquals(current, outline)) Registered.Remove(outline.Id);
    }

    internal static bool Any => Registered.Count > 0;
}

/// <summary>An outline added to one object (see <c>Outlines.Add</c>).</summary>
public sealed class OutlineHandle
{
    internal readonly int Slot, Generation;
    internal OutlineHandle(int slot, int generation) { Slot = slot; Generation = generation; }

    /// <summary>True until removed, its time is up, the object is destroyed or the level ends.</summary>
    public bool Active => OutlineDraw.Holds(this);

    /// <summary>Stops drawing it; nothing happens when it already ended.</summary>
    public void Remove() => OutlineDraw.Release(this);
}

/// <summary>One mod's switch for the automatic enemy outline (see <see cref="Outlines.AddEnemyOutline"/>). Off when added.</summary>
public sealed class EnemyOutline
{
    private EnemyOutlineStyle _style;
    internal EnemyOutline(string id, EnemyOutlineStyle style) { Id = id; _style = style; }

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id { get; }

    /// <summary>Outlines are drawn while any mod's switch is on; the style is that of the switch turned on last. Cheap to set every frame.</summary>
    public bool Active
    {
        get => OutlineDraw.IsOn(this);
        set => OutlineDraw.SetActive(this, value);
    }

    /// <summary>The look. A style with a problem is refused (logged) and the old one kept.</summary>
    public EnemyOutlineStyle Style
    {
        get => _style;
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Problem() is { } problem) { Plugin.Logger.ErrorOnce("visual-enemy-outline-style-refused", Id + ": enemy outline style refused: " + problem + "."); return; }
            _style = value;
            OutlineDraw.StyleChanged(this);
        }
    }

    /// <summary>Turns the switch off and frees the name.</summary>
    public void Remove()
    {
        OutlineDraw.SetActive(this, false);
        Outlines.Forget(this);
    }
}

/// <summary>
/// Draws every outline. The automatic enemy outline reads ForgeCore's registry (<c>Enemies.Live</c>, slots and generations): every
/// frame a fixed number of enemies have their position and alertness read (<see cref="OutlineRules.RefreshPerFrame"/>), the nearest in
/// view are chosen (<see cref="EnemyOutlineTracker.Select"/>); outlines added to objects are claims (<see cref="OutlineClaims"/>) with
/// one winner per object. Only when what is drawn changes (or an enemy goes in or out of the game's culling) is the camera's command buffer
/// rebuilt. It runs after the camera's transparent pass and fills each target's silhouette and, for the line's strength, its visible area
/// (or, X-ray, its whole silhouette) unlit into a screen-sized mask that shares the scene's depth. The game's <c>UI_Apply</c> adds its HUD
/// as the camera's last image effect; just before that the line is laid over the frame: only pixels outside every silhouette within the
/// line's width are drawn, so a target gets one line round its outer edge, none inside it, and the HUD stays on top. Not in the scene
/// because the game's exposure brightens its dark scenes many times over, and a colour drawn into the scene came out white.
/// </summary>
internal static class OutlineDraw
{
    private const int MaxRenderers = 48, MaxSubmeshes = 8, CameraRecheckFrames = 30, CollectsPerFrame = 4, MaxCuts = 4;
    private const int PassFill = 0, PassComposite = 1, PassFillXRay = 2, PassSilhouette = 3;
    private static readonly OutlineScopes<EnemyOutline> Scopes = new();
    private static readonly EnemyOutlineTracker Tracker = new();
    private static readonly OutlineClaims Claims = new();
    private static Body[] _tracked = new Body[64];
    private static Body[] _claimed = new Body[16];
    private static ClaimLook[] _looks = new ClaimLook[16];
    private static int _version = -1, _cameraWait, _screenHeight, _settleFrames;
    private static bool _failed, _dirty, _warmTried, _patched, _shown;
    private static float _lineWidth;
    private static IntPtr _hud;
    private static CommandBuffer? _buffer;
    private static Material? _material;
    private static RenderTexture? _mask;
    private static Camera? _camera;
    private static Transform? _cameraTransform;
    private static readonly int ColorId = Shader.PropertyToID("_ForgeOutlineColor"), ParamsId = Shader.PropertyToID("_ForgeOutlineParams");
    // The names the game's own depth shader (and so the fill pass) reads its body cuts by.
    private static readonly int CutMtxId = Shader.PropertyToID("_EllipsoidMtx"), CutInvertsId = Shader.PropertyToID("_EllipsoidInverts"),
        CutCountId = Shader.PropertyToID("_EllipsoidCount");

    /// <summary>One outlined object: its renderers and the body cuts the game made (a shot-off head or limb, read from its property
    /// block when the renderers are collected and given to the fill pass as globals, so the cut does not depend on the draw call applying
    /// the block). <see cref="Cut"/> marks the renderers the game cuts (its destruction component's own list); the others are drawn whole.</summary>
    private struct Body
    {
        public IntPtr Pointer;
        public GameObject Root;
        public EnemyAgent? Agent;
        // The agent's parts read every frame, kept so a read makes no interop wrapper (each property read makes one).
        public CullingSystem.C_MovingCuller? Culler;
        public EnemyAI? AI;
        public EnemyLocomotion? Locomotion;
        public Transform Transform;
        public Renderer[]? Renderers;
        public GameObject[]? Objects;
        public int[]? Submeshes;
        public bool[]? Cut;
        public Matrix4x4[]? CutMtx;
        public Vector4 CutInverts;
        public int CutCount;
        public bool LimbHit;
        public bool Culled;
        public bool Known;
        public ForgeCore.EnemyAlert Seen;
        public float WakeUntil, NextScan;
    }

    private struct ClaimLook
    {
        public OutlineColor Color;
        public bool XRay;
    }

    /// <summary>The first registration: the limb hook goes on (an enemy's shot-off parts are followed).</summary>
    internal static void Used()
    {
        if (_patched) return;
        _patched = true;
        ForgeCore.Patches.Lazy(Plugin.Id, harmony => harmony.PatchAll(typeof(OutlineOverFramePatch))).Ensure();
        ForgeCore.GameHooks.EnemyLimbDestroyed.After((limb, _) => OutlineLimbPatch.Destroyed(limb));
        ForgeCore.GameHooks.EnemyCustomLimbDestroyed.After((limb, _) => OutlineLimbPatch.Destroyed(limb));
    }

    internal static OutlineHandle Claim(GameObject target, OutlineColor color, int priority, float seconds, bool xRay)
    {
        Used();
        // An enemy is keyed by its agent, as the automatic enemy outline knows it, so the two meet on one enemy.
        var agent = target.GetComponent<EnemyAgent>();
        var key = agent != null ? agent.Pointer : target.Pointer;
        var slot = Claims.Add(key.ToInt64(), priority, OutlineRules.Until(Time.time, seconds));
        if (_claimed.Length < Claims.Capacity)
        {
            Array.Resize(ref _claimed, Math.Max(Claims.Capacity, _claimed.Length * 2));
            Array.Resize(ref _looks, _claimed.Length);
        }
        var now = Time.time;
        _claimed[slot] = new Body { Pointer = key, Root = target, Agent = agent, Transform = target.transform, WakeUntil = now + OutlineRules.WakeSeconds, NextScan = now + OutlineRules.WakeScanSeconds };
        Collect(ref _claimed[slot]);
        _looks[slot] = new ClaimLook { Color = color, XRay = xRay };
        _dirty = true;
        return new OutlineHandle(slot, Claims.Generation(slot));
    }

    internal static bool Holds(OutlineHandle handle) => Claims.Live(handle.Slot) && Claims.Generation(handle.Slot) == handle.Generation;

    internal static void Release(OutlineHandle handle)
    {
        if (!Claims.Remove(handle.Slot, handle.Generation)) return;
        _claimed[handle.Slot] = default;
        Blank();
    }

    internal static bool IsOn(EnemyOutline outline) => Scopes.IsOn(outline);

    internal static void SetActive(EnemyOutline outline, bool on)
    {
        if (Scopes.Set(outline, on)) _dirty = true;
    }

    internal static void StyleChanged(EnemyOutline outline)
    {
        if (ReferenceEquals(Scopes.Current, outline)) _dirty = true;
    }

    /// <summary>The level ended or a checkpoint was reloaded: every outline on an object ends (the objects go with the level).</summary>
    internal static void Clear()
    {
        Claims.Clear();
        Array.Clear(_claimed, 0, _claimed.Length);
        Stop();
    }

    /// <summary>A limb of this enemy was shot off. The game cuts the body away with ellipsoids (read here into the fill pass) and may
    /// hide or add renderers: the enemy's renderers and cuts are read again at once, and once more when the settle frames end, because
    /// its destruction effects run over several frames. A renderer no longer drawn leaves the set; the others are untouched.</summary>
    internal static void LimbDestroyed(EnemyAgent? agent)
    {
        if (agent == null) return;
        var pointer = agent.Pointer;
        var found = false;
        for (int slot = 0; slot < Tracker.Capacity; slot++)
        {
            if (!Tracker.Used(slot) || _tracked[slot].Pointer != pointer) continue;
            _tracked[slot].LimbHit = true;
            if (_tracked[slot].Renderers != null) Collect(ref _tracked[slot]);
            found = true;
        }
        for (int slot = 0; slot < Claims.Capacity; slot++)
        {
            if (!Claims.Live(slot) || _claimed[slot].Agent == null || _claimed[slot].Agent!.Pointer != pointer) continue;
            _claimed[slot].LimbHit = true;
            Collect(ref _claimed[slot]);
            found = true;
        }
        if (!found) return;
        _settleFrames = OutlineRules.LimbSettleFrames;
        _dirty = true;
    }

    /// <summary>The last settle frame: every enemy that lost a limb reads its renderers and cuts once more.</summary>
    private static void SettleEnded()
    {
        for (int slot = 0; slot < Tracker.Capacity; slot++)
        {
            if (!Tracker.Used(slot) || !_tracked[slot].LimbHit) continue;
            _tracked[slot].LimbHit = false;
            _tracked[slot].Renderers = null;
            _tracked[slot].Submeshes = null;
        }
        for (int slot = 0; slot < Claims.Capacity; slot++)
        {
            if (!Claims.Live(slot) || !_claimed[slot].LimbHit) continue;
            _claimed[slot].LimbHit = false;
            Collect(ref _claimed[slot]);
        }
    }

    /// <summary>Forgets a slot whose enemy is gone. A renderer the buffer still draws may be destroyed right after this: stop drawing
    /// now, rebuild on the next frame.</summary>
    private static void Drop(int slot)
    {
        _tracked[slot] = default;
        if (Tracker.Release(slot)) Stop();
    }

    /// <summary>The registry changed: forget every slot whose enemy left or was replaced.</summary>
    private static void Reconcile(ReadOnlySpan<EnemyAgent?> live)
    {
        for (int slot = 0; slot < Tracker.Capacity; slot++)
            if (Tracker.Used(slot) && (slot >= live.Length || live[slot] is not { } agent || agent.Pointer != _tracked[slot].Pointer)) Drop(slot);
    }

    private static void Stop()
    {
        Blank();
        Tracker.ClearSelection();
    }

    /// <summary>The buffer stops drawing now (a renderer in it may be destroyed right after this); what is left is drawn again before the camera next renders.</summary>
    private static void Blank()
    {
        _shown = false;
        _buffer?.Clear();
        _dirty = true;
    }

    /// <summary>Claims whose time is up or whose object was destroyed end now.</summary>
    private static void ExpireClaims()
    {
        if (Claims.Count == 0) return;
        if (Claims.Expire(Time.time)) Stop();
        for (int slot = 0; slot < Claims.Capacity; slot++)
        {
            if (!Claims.Live(slot)) continue;
            ref var body = ref _claimed[slot];
            if (!ForgeCore.NativeObjects.Gone(body.Root) && !ForgeCore.NativeObjects.Gone(body.Transform)) continue;
            Claims.Remove(slot, Claims.Generation(slot));
            body = default;
            Stop();
        }
    }

    internal static void Tick()
    {
        WarmUp();
        ExpireClaims();
        var scope = Scopes.Current;
        if (Off())
        {
            if (_shown) Stop();
            return;
        }
        if (!EnsureCamera()) return;
        int height = ForgeCore.EngineCalls.ScreenHeight();
        if (height != _screenHeight) { _screenHeight = height; _dirty = true; }
        if (_settleFrames > 0) { _dirty = true; if (--_settleFrames == 0) SettleEnded(); }
        WatchCulling();
        RescanClaims(Time.time);
        if (scope == null)
        {
            if (Tracker.SelectedCount > 0) { Tracker.ClearSelection(); _dirty = true; }
            if (_dirty) Rebuild(null);
            return;
        }

        var live = ForgeCore.Enemies.Live;
        var now = Time.time;
        if (_tracked.Length < live.Length) Array.Resize(ref _tracked, Math.Max(live.Length, _tracked.Length * 2));
        if (ForgeCore.Enemies.Version != _version) { _version = ForgeCore.Enemies.Version; Reconcile(live); }
        for (int read = 0, seen = 0; read < OutlineRules.RefreshPerFrame && seen < live.Length; seen++)
        {
            int slot = Tracker.NextSlot(live.Length);
            if (live[slot] is not { } agent) continue;
            read++;
            ref var entry = ref _tracked[slot];
            if (entry.Pointer != agent.Pointer)
                entry = new Body { Pointer = agent.Pointer, Root = agent.gameObject, Agent = agent, Transform = agent.transform, AI = agent.AI, Locomotion = agent.Locomotion };
            if (ForgeCore.NativeObjects.Gone(agent) || ForgeCore.NativeObjects.Gone(entry.Transform)) { Drop(slot); continue; }
            var state = ForgeCore.Enemies.Alert(agent.Alive, entry.AI, entry.Locomotion);
            if (!entry.Known || entry.Seen != state)
            {
                entry.Known = true; entry.Seen = state;
                entry.WakeUntil = now + OutlineRules.WakeSeconds; entry.NextScan = now + OutlineRules.WakeScanSeconds;
            }
            var p = BodyPosition(ref entry);
            Tracker.Set(slot, ForgeCore.Enemies.Generation(slot), p.x, p.y, p.z, state);
        }
        // An enemy that just woke or appeared: its renderers are read again a few times a second while its animation plays.
        for (int i = 0, scans = 0; i < Tracker.SelectedCount && scans < CollectsPerFrame; i++)
        {
            ref var entry = ref _tracked[Tracker.SelectedSlot(i)];
            if (entry.Renderers == null || !OutlineRules.WakeScanDue(now, entry.WakeUntil, entry.NextScan)) continue;
            entry.NextScan = now + OutlineRules.WakeScanSeconds;
            Collect(ref entry);
            scans++;
            _dirty = true;
        }
        var c = ForgeCore.EngineCalls.Position(_cameraTransform!);
        var q = ForgeCore.EngineCalls.Rotation(_cameraTransform!);
        float fx = 2f * (q.x * q.z + q.w * q.y), fy = 2f * (q.y * q.z - q.w * q.x), fz = 1f - 2f * (q.x * q.x + q.y * q.y);
        var style = scope.Style;
        if (Tracker.Select(style, c.x, c.y, c.z, fx, fy, fz) || _dirty) Rebuild(style);
    }

    /// <summary>Where the enemy's body is drawn now: the centre of its first drawn renderer's bounds (it follows the bones), not the agent's
    /// position, which differs while a wake-up or climb-out animation plays. The agent's position until its renderers are known.</summary>
    private static Vector3 BodyPosition(ref Body body)
    {
        var renderers = body.Renderers;
        if (renderers != null)
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (!ForgeCore.NativeObjects.Gone(renderer) && renderer.enabled && ForgeCore.EngineCalls.Bounds(renderer, out var center, out _)) return center;
            }
        return ForgeCore.EngineCalls.Position(body.Transform);
    }

    /// <summary>An enemy the game does not draw now (<c>MovingCuller</c>: its room is out of the camera's view) has no line: its
    /// body is gone from the picture and so are the rooms round it, which left the line hanging in the air. Drawn again when the game shows it.</summary>
    private static void WatchCulling()
    {
        for (int slot = 0; slot < Claims.Capacity; slot++)
            if (Claims.Live(slot)) Watch(ref _claimed[slot]);
        for (int i = 0; i < Tracker.SelectedCount; i++) Watch(ref _tracked[Tracker.SelectedSlot(i)]);
    }

    // The game hides a culled enemy by switching its renderers off, so renderers read while it was culled (or before its model was shown)
    // left it with no line once it came into view (11-06: many replay enemies had none). Coming into view, its renderers are read again.
    private static void Watch(ref Body body)
    {
        var culled = IsCulled(ref body);
        if (culled == body.Culled) return;
        body.Culled = culled;
        if (!culled && body.Renderers != null) Collect(ref body);
        _dirty = true;
    }

    /// <summary>An object outlined with <c>Add</c> reads its renderers again a few times a second for a while after it was added, as an enemy
    /// the automatic outline just picked does, and for as long as none of them was drawn when read.</summary>
    private static void RescanClaims(float now)
    {
        for (int slot = 0, scans = 0; slot < Claims.Capacity && scans < CollectsPerFrame; slot++)
        {
            if (!Claims.Live(slot)) continue;
            ref var body = ref _claimed[slot];
            if (body.Renderers == null || now < body.NextScan || (now >= body.WakeUntil && body.Renderers.Length > 0)) continue;
            body.NextScan = now + OutlineRules.WakeScanSeconds;
            Collect(ref body);
            scans++;
            _dirty = true;
        }
    }

    private static bool IsCulled(ref Body body)
    {
        if (ForgeCore.NativeObjects.Gone(body.Agent)) return false;
        var culler = body.Culler ??= body.Agent!.MovingCuller;
        return culler != null && !culler.IsShown;
    }

    private static bool Off() => _failed || (Scopes.Current == null && Claims.Count == 0) || !Plugin.Outlines.Value || Away();

    /// <summary>The HUD is about to go onto this frame: the line goes on first.</summary>
    internal static void LayOver(IntPtr hud, RenderTexture frame)
    {
        if (!_shown || hud != _hud || ForgeCore.NativeObjects.Gone(_mask)) return;
        var active = RenderTexture.active;
        Shader.SetGlobalVector(ParamsId, new Vector4(_lineWidth, 0f, 0f, 0f));
        Graphics.Blit(_mask, frame, _material, PassComposite);
        RenderTexture.active = active;
    }

    /// <summary>In a level with one of the game's own full screens open (map, terminal, menu, console): nothing is outlined.</summary>
    private static bool Away()
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return false;
        return FocusStateManager.CurrentState is eFocusState.Map or eFocusState.MainMenu or eFocusState.ComputerTerminal or eFocusState.Console
            or eFocusState.DebugMenu or eFocusState.GlobalPopupMessage;
    }

    private static bool EnsureCamera()
    {
        if (!ForgeCore.NativeObjects.Gone(_camera) && !ForgeCore.NativeObjects.Gone(_cameraTransform)) return true;
        if (_camera != null) { _camera = null; _cameraTransform = null; Tracker.ClearSelection(); }
        if (_cameraWait-- > 0) return false;
        _cameraWait = CameraRecheckFrames;
        var rendering = ClusteredRendering.Current;
        var camera = rendering == null ? null : rendering.m_camera;
        if (camera == null) return false;
        if (_material == null && !Prepare()) return false;
        var hud = camera.GetComponent<UI_Apply>();
        if (hud == null)
        {
            _failed = true;
            Plugin.Logger.ErrorOnce("visual-outlines-are-off-games-camera", "Outlines are off: the game's camera has no UI_Apply to lay the line under its HUD.");
            return false;
        }
        _hud = hud.Pointer;
        camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _buffer);
        _camera = camera;
        _cameraTransform = camera.transform;
        _dirty = true;
        return true;
    }

    /// <summary>The shader and the screen-sized mask are made during the elevator ride (or the first level frame) while a mod uses
    /// outlines, so the first outline (boarding a suit) does not pay for them: that frame took 189.61 ms on 10-28 13:23 before this.</summary>
    private static void WarmUp()
    {
        if (_material != null || _failed || _warmTried || !_patched || !Plugin.Outlines.Value) return;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.StopElevatorRide && state != eGameStateName.InLevel) return;
        _warmTried = true;
        // The screen-sized mask too (at 3840 x 2160 a 66 MB texture): made on the first outlined frame it is a stall of its own.
        // Two steps of ForgeCore LandingWork, a frame apart: the material and the mask, then the draw that makes the GPU programs.
        ForgeCore.LandingWork.Add(Plugin.Id, "outline-build", () =>
        {
            if (!_failed && (_material != null || Prepare())) Mask(Screen.width, Screen.height);
        });
        ForgeCore.LandingWork.Add(Plugin.Id, "outline-draw", () =>
        {
            if (!_failed && _material != null && !ForgeCore.NativeObjects.Gone(_mask)) DrawOnce();
        });
    }

    /// <summary>The four passes drawn once into a small temporary texture: a shader's GPU programs are made at its first draw, not when it loads.</summary>
    private static void DrawOnce()
    {
        var target = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(Texture2D.blackTexture, target, _material, PassFill);
            Graphics.Blit(Texture2D.blackTexture, target, _material, PassFillXRay);
            Graphics.Blit(Texture2D.blackTexture, target, _material, PassSilhouette);
            Graphics.Blit(_mask, target, _material, PassComposite);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-outline-passes-could-not-be-drawn", "Outline passes could not be drawn ahead of use (the first outlined frame pays for it): " + error); }
        finally { RenderTexture.ReleaseTemporary(target); }
    }

    private static bool Prepare()
    {
        try
        {
            if (!ForgeCore.EngineCalls.Prepare(out var why)) throw new InvalidOperationException("an engine call is missing (" + why + ")");
            if (!ForgeCore.EngineCalls.HasBounds)
                Plugin.Logger.ErrorOnce("visual-renderer-bounds-call-missing-so-outline", "The renderer bounds call is missing, so an outline follows the enemy's agent instead of its drawn body during wake-up and climb-out animations.");
            var shader = Shaders.Outline ?? throw new InvalidOperationException("the outline shader is not loaded");
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _buffer = new CommandBuffer { name = "ForgeVisual outline" };
            Camera.onPreRender = Camera.onPreRender == null ? (Camera.CameraCallback)BeforeRender : Il2CppSystem.Delegate.Combine(Camera.onPreRender, (Camera.CameraCallback)BeforeRender).Cast<Camera.CameraCallback>();
            return true;
        }
        catch (Exception error)
        {
            _failed = true;
            Plugin.Logger.ErrorOnce("visual-outlines-are-off", "Outlines are off: " + error + ".");
            return false;
        }
    }


    private static void Rebuild(EnemyOutlineStyle? style)
    {
        _dirty = false;
        var buffer = _buffer!;
        buffer.Clear();
        _shown = false;
        if (Tracker.SelectedCount == 0 && Claims.Count == 0) return;
        _shown = true;
        var mask = Mask();
        buffer.SetRenderTarget(mask, BuiltinRenderTextureType.CurrentActive);
        buffer.ClearRenderTarget(false, true, Color.clear);
        float width = OutlineRules.PixelWidth(style?.Width ?? OutlineRules.DefaultWidth, _screenHeight);
        _lineWidth = width;

        // Outlines added to objects: the winner on each object, unless the automatic enemy outline outranks it there.
        if (Claims.Count > 0)
        {
            buffer.SetGlobalVector(ParamsId, new Vector4(width, OutlineRules.Unlimited, 0.001f, 0f));
            for (int slot = 0; slot < Claims.Capacity; slot++)
            {
                if (!Claims.Live(slot) || Claims.Winner(Claims.Key(slot)) != slot) continue;
                if (style != null && Claims.Priority(slot) < style.Priority && AutomaticHolds(_claimed[slot].Pointer)) continue;
                Draw(buffer, ref _claimed[slot], _looks[slot].Color, _looks[slot].XRay ? PassFillXRay : PassFill);
            }
        }

        // The automatic enemy outline. An enemy's renderers are read the first time it is outlined; at most a few per frame, so switching
        // outlines on in front of a crowd spreads the reads over frames (the others are drawn from the next frames on).
        if (style != null && Tracker.SelectedCount > 0)
        {
            buffer.SetGlobalVector(ParamsId, new Vector4(width, style.Range, Math.Max(style.FadeDistance, 0.001f), 0f));
            var reads = 0;
            for (int i = 0; i < Tracker.SelectedCount; i++)
            {
                ref var entry = ref _tracked[Tracker.SelectedSlot(i)];
                if (Claims.Count > 0 && Claims.Overrides(entry.Pointer.ToInt64(), style.Priority)) continue;
                if (entry.Renderers == null)
                {
                    if (reads == CollectsPerFrame) { _dirty = true; continue; }
                    reads++;
                    Collect(ref entry);
                }
                Draw(buffer, ref entry, style.ColorOf(Tracker.SelectedState(i)), style.XRay ? PassFillXRay : PassFill);
            }
        }
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
    }

    /// <summary>Whether the automatic enemy outline draws the enemy at <paramref name="pointer"/> now.</summary>
    private static bool AutomaticHolds(IntPtr pointer)
    {
        for (int i = 0; i < Tracker.SelectedCount; i++) if (_tracked[Tracker.SelectedSlot(i)].Pointer == pointer) return true;
        return false;
    }

    private static void Draw(CommandBuffer buffer, ref Body body, OutlineColor color, int pass)
    {
        body.Culled = IsCulled(ref body);
        if (body.Culled) return;
        // The frame is still linear where the mask is laid over it and is encoded for the screen afterwards, so the colour goes in
        // decoded (a vector, which Unity passes unchanged) and the mask is a linear texture.
        buffer.SetGlobalVector(ColorId, new Vector4(Mathf.GammaToLinearSpace(color.R), Mathf.GammaToLinearSpace(color.G),
            Mathf.GammaToLinearSpace(color.B), color.A));
        // This target's body cuts, set before its draws so one target's cuts never reach another; the count is 0 for a renderer the
        // game does not cut.
        if (body.CutCount > 0)
        {
            buffer.SetGlobalMatrixArray(CutMtxId, body.CutMtx!);
            buffer.SetGlobalVector(CutInvertsId, body.CutInverts);
        }
        var renderers = body.Renderers;
        if (renderers == null) return;
        int count = -1;
        for (int r = 0; r < renderers.Length; r++)
        {
            var renderer = renderers[r];
            // A renderer the game switched off with its object (not with the component) is still in the set: the buffer would keep
            // drawing the body that is no longer shown, at the place it was left.
            if (ForgeCore.NativeObjects.Gone(renderer) || !renderer.enabled || !body.Objects![r].activeInHierarchy) continue;
            int cuts = body.Cut![r] ? body.CutCount : 0;
            if (cuts != count) { count = cuts; buffer.SetGlobalFloat(CutCountId, cuts); }
            for (int sub = 0; sub < body.Submeshes![r]; sub++)
            {
                if (pass == PassFill) buffer.DrawRenderer(renderer, _material, sub, PassSilhouette);
                buffer.DrawRenderer(renderer, _material, sub, pass);
            }
        }
    }

    /// <summary>The camera is about to render and an outline was added or removed after this frame's <see cref="Tick"/>: drawn now, so no frame goes without its lines.</summary>
    private static void BeforeRender(Camera camera)
    {
        if (camera == _camera && _dirty && !Off()) Rebuild(Scopes.Current?.Style);
    }

    /// <summary>The mask the outlines are drawn into, the size of the camera's picture; made again when that size changes.</summary>
    private static RenderTexture Mask() => Mask(_camera!.pixelWidth, _camera.pixelHeight);

    private static RenderTexture Mask(int width, int height)
    {
        if (!ForgeCore.NativeObjects.Gone(_mask) && _mask.width == width && _mask.height == height) return _mask;
        if (!ForgeCore.NativeObjects.Gone(_mask)) { _mask.Release(); Object.Destroy(_mask); }
        // Point sampled: the line reads whole pixels of the mask, inside or outside.
        _mask = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
        { name = "ForgeVisual outline mask", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        _mask.Create();
        return _mask;
    }

    /// <summary>The target's mesh renderers that are still drawn (alive and enabled; particles, lines and sprites are left out) and, for an
    /// enemy, the body cuts the game made: read when it is first outlined (after any model changes made at spawn) and again after a limb
    /// is shot off.</summary>
    private static void Collect(ref Body body)
    {
        var meshes = new List<Renderer>();
        foreach (var renderer in body.Root.GetComponentsInChildren<Renderer>(false))
            if ((renderer.TryCast<SkinnedMeshRenderer>() != null || renderer.TryCast<MeshRenderer>() != null) && renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly && Solid(renderer)) meshes.Add(renderer);
        // The largest first: when a target has more meshes than the limit (a player with every part of its gear), what goes is the small parts, not the body.
        meshes.Sort(static (a, b) => b.bounds.size.sqrMagnitude.CompareTo(a.bounds.size.sqrMagnitude));
        var found = new List<Renderer>();
        OutlineDrawSet.Collect(meshes, r => !ForgeCore.NativeObjects.Gone(r) && r.enabled && r.sharedMaterials.Length > 0, MaxRenderers, found);
        var subs = new int[found.Count];
        var objects = new GameObject[found.Count];
        for (int i = 0; i < subs.Length; i++)
        {
            subs[i] = Math.Min(found[i].sharedMaterials.Length, MaxSubmeshes);
            objects[i] = found[i].gameObject;
        }
        body.Renderers = found.ToArray();
        body.Submeshes = subs;
        body.Objects = objects;
        ReadCuts(ref body);
    }

    /// <summary>Whether any of the renderer's materials draws in the opaque queues. Light cones, flares and other effects are blended
    /// over the frame after the solid models; they are not part of a target's silhouette.</summary>
    private static bool Solid(Renderer renderer)
    {
        foreach (var material in renderer.sharedMaterials)
            if (material != null && material.renderQueue <= (int)RenderQueue.GeometryLast) return true;
        return false;
    }

    /// <summary>The ellipsoids the game cuts an enemy's body with, from the property block it hands to its renderers
    /// (<c>CD_CharacterDestruction.UpdateMaterialPropertyBlock</c>), and which of the collected renderers it hands them to.</summary>
    private static void ReadCuts(ref Body body)
    {
        body.CutCount = 0;
        body.CutMtx = null;
        var found = body.Renderers!;
        body.Cut = new bool[found.Length];
        if (body.Agent == null) return;
        var destruction = body.Agent.GetComponentInChildren<CharacterDestruction.CD_CharacterDestruction>();
        var block = destruction == null ? null : destruction.m_propertyBlock;
        var cutRenderers = destruction == null ? null : destruction.m_renderers;
        if (block == null || cutRenderers == null) return;
        for (int i = 0; i < found.Length; i++)
            for (int k = 0; k < cutRenderers.Length; k++)
                if (cutRenderers[k] != null && cutRenderers[k].Pointer == found[i].Pointer) { body.Cut[i] = true; break; }
        int count = Math.Min((int)block.GetFloat(CutCountId), MaxCuts);
        var matrices = count > 0 ? block.GetMatrixArray(CutMtxId) : null;
        if (matrices == null || matrices.Length < count) return;
        var copy = new Matrix4x4[MaxCuts];
        for (int i = 0; i < count; i++) copy[i] = matrices[i];
        body.CutMtx = copy;
        body.CutInverts = block.GetVector(CutInvertsId);
        body.CutCount = count;
    }
}

/// <summary>The game adds its HUD to the frame in its camera's last image effect. The outline goes onto the frame just before.</summary>
[HarmonyPatch(typeof(UI_Apply), nameof(UI_Apply.OnRenderImage))]
internal static class OutlineOverFramePatch
{
    private static void Prefix(UI_Apply __instance, RenderTexture src)
    {
        try { OutlineDraw.LayOver(__instance.Pointer, src); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-outline-could-not-be-laid-over", "The outline could not be laid over the frame: " + error); }
    }
}

/// <summary>The game destroys a limb through <c>DestroyLimb</c>, and the custom limb class overrides it, so both ForgeCore hooks are
/// watched (subscribed the first time a mod uses outlines).</summary>
internal static class OutlineLimbPatch
{
    internal static void Destroyed(Dam_EnemyDamageLimb __instance)
    {
        try
        {
            var owner = __instance.m_base;
            OutlineDraw.LimbDestroyed(owner == null ? null : owner.Owner);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-outline-limb-update-failed", "Outline limb update failed: " + error); }
    }
}
