using System;
using CullingSystem;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>A mod's hold on the local player's view: it can put the view anywhere (<see cref="Look"/>, with the flashlight riding
/// along), lift the eye above the player's own height (<see cref="Lift"/>) and pull the near clip plane in (<see cref="NearClip"/>).
/// Leases stack and only the newest owns the view; an older lease's lift and near clip come back when the newer one is released.
/// The lift rides on the game's own camera offset, so its crouch, stand, terminal and grab transitions keep running. A lease ends
/// with <see cref="Dispose"/>, at level cleanup, and when the local player goes down; the view then returns to the game's own
/// values. Call <see cref="Lift"/> every frame the lease is held (the game rewrites its offset target when the player crouches
/// or stands, and the lift is added to each new target). Main thread only.</summary>
public sealed class CameraLease : IDisposable
{
    private static readonly CameraLeaseBook Book = new();
    private static bool _watchingDown;
    private static Vector3 _writtenTarget;
    private static float _appliedLift;
    private static float _gameNear;
    private static bool _nearApplied;
    private readonly CameraLeaseBook.Entry _entry;

    private CameraLease(CameraLeaseBook.Entry entry) => _entry = entry;

    /// <summary>A new lease for <paramref name="modId"/> that changes nothing yet.</summary>
    public static CameraLease Take(string modId)
    {
        WatchDown();
        var local = Players.Local;
        var camera = local == null || local.WasCollected ? null : local.FPSCamera;
        if (Book.Count == 0 && camera != null && !camera.WasCollected) _writtenTarget = camera.m_posOffsetTarget;
        return new(Book.Take(modId));
    }

    public string Owner => _entry.Owner;

    /// <summary>Not released yet (by <see cref="Dispose"/> or automatically).</summary>
    public bool IsHeld => Book.Holds(_entry);

    /// <summary>This lease owns the view: no newer lease is held.</summary>
    public bool OwnsView => Book.Owns(_entry);

    /// <summary>The eye height (metres above the player's own eye) this lease asks for. Applies at once when the lease owns the
    /// view; call it every frame while held.</summary>
    public void Lift(float meters)
    {
        _entry.Lift = meters;
        Apply();
    }

    /// <summary>The near clip plane this lease asks for, or null for the game's own.</summary>
    public void NearClip(float? plane)
    {
        _entry.NearClip = plane ?? float.NaN;
        Apply();
    }

    /// <summary>Puts the view at <paramref name="position"/> turned <paramref name="rotation"/> (world space), and the flashlight with
    /// it: the game aims the flashlight from its own view, which stays where the player stands, so the lamp keeps the place and turn
    /// it has relative to the view this frame (a lamp not mounted near the view, over a metre away, is left alone). Does nothing when
    /// a newer lease owns the view. Call it late in the frame, after the game placed its camera. <paramref name="room"/> is a point the view belongs with
    /// (the player it watches): when no room holds the view itself, as in the air over a large room, the game's culling starts from that point's room. The pose is also handed to the game's own `FPSCamera.OverridePositionAndRotation`: without it the game's `Update` puts the view back at the player every frame, so the view stayed where the player stood (read in game 2026-11-02).</summary>
    public bool Look(Vector3 position, Quaternion rotation, Vector3? room = null)
    {
        if (!OwnsView) return false;
        var agent = Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected) return false;
        var view = camera.transform;
        CullFrom(agent!, position, room);
        var light = agent!.Inventory != null ? agent.Inventory.m_flashlight : null;
        if (light == null || light.WasCollected) { view.SetPositionAndRotation(position, rotation); camera.OverridePositionAndRotation(position, rotation); return true; }
        var lamp = light.transform;
        var inverse = Quaternion.Inverse(view.rotation);
        var offset = inverse * (lamp.position - view.position);
        var turn = inverse * lamp.rotation;
        view.SetPositionAndRotation(position, rotation);
        camera.OverridePositionAndRotation(position, rotation);
        if (Vec.SqrLength(offset) <= 1f) lamp.SetPositionAndRotation(position + rotation * offset, rotation * turn);
        return true;
    }

    /// <summary>The view behind <paramref name="focus"/> (usually the local player's head), turned <paramref name="yaw"/> and
    /// <paramref name="pitch"/> degrees, <paramref name="distance"/> metres back and <paramref name="shoulder"/> to the side. A sphere swept from
    /// the focus against the game's world layers pulls the view in at walls, ceilings and elevator sides. It moves in from the player's own eye
    /// over <paramref name="blendSeconds"/>, and with <paramref name="leaving"/> back to it. Returns how far it is out, 0 to 1: leaving, 0 means
    /// it is back at the eye and the lease can go. Moves only the view, never the player (hold the look axes off with an <see cref="InputLease"/>
    /// and give the angles yourself). Call it late in the frame, every frame.</summary>
    public float Orbit(Vector3 focus, float yaw, float pitch, float distance, float shoulder, float blendSeconds, bool leaving = false)
    {
        var agent = Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected) return 0f;
        if (!_orbiting)
        {
            _orbiting = true;
            _orbitOut = 0f;
            _orbitFromRotation = camera.transform.rotation;
        }
        _orbitOut = OrbitMath.Blend(_orbitOut, !leaving, Time.deltaTime, blendSeconds);
        var turn = Quaternion.Euler(pitch, yaw, 0f);
        var back = turn * new Vector3(shoulder, 0f, -distance);
        var length = Vec.Length(back);
        var reach = length;
        if (length > 0.001f && Physics.SphereCast(focus, OrbitRadius, back / length, out var hit, length, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            reach = OrbitMath.Pulled(hit.distance, length, OrbitRadius);
        var at = focus + (length > 0.001f ? back / length * reach : Vec.Zero);
        var eased = ForgeUI.Ease.Smoothstep(_orbitOut);
        Look(Vector3.Lerp(agent!.EyePosition, at, eased), Quaternion.Slerp(_orbitFromRotation, turn, eased));
        if (leaving && _orbitOut <= 0f) _orbiting = false;
        return _orbitOut;
    }

    private const float OrbitRadius = 0.2f;
    private bool _orbiting;
    private float _orbitOut;
    private Quaternion _orbitFromRotation;

    // The game works out what is visible starting from the room its camera's culling agent is in, and that agent follows the player, not the view:
    // a view away from the player started from the wrong room and drew almost nothing. The room is looked up here, directly, and put on the agent
    // every frame (the player's own update takes the agent back to the player). Not through the agent's own update and refresh: its update does
    // nothing at all while the agent has no room and checks portal crossings along the line from its last place, so a view that jumped (a jump in
    // time, a camera flown far away and back) was given a room along that line or kept a far one, and the world round the view was not drawn.
    // The room is the one holding the view; else the one holding the floor under it (the game's rooms hold the space near their floor, not the
    // air of a tall room); else the given point's (the player watched); else the last one. Looked up again when the view or the point moved, and
    // a few times a second anyway, so a room missed once is found.
    private const float CullMoveSquared = 1f, CullEverySeconds = 0.25f, CullFloorReach = 40f;
    private static C_Node? _cullNode;
    private static Vector3 _cullAt, _cullPointAt;
    private static bool _cullMoved;
    private static float _cullNext;

    private static void CullFrom(PlayerAgent agent, Vector3 position, Vector3? room)
    {
        var culler = C_Camera.Current != null ? C_Camera.Current.m_cullAgent : null;
        if (culler == null || culler.WasCollected) return;
        var point = room ?? position;
        var now = Frame.UnscaledTime;
        if (!_cullMoved || Vec.SqrLength(position - _cullAt) >= CullMoveSquared || Vec.SqrLength(point - _cullPointAt) >= CullMoveSquared || now >= _cullNext)
        {
            _cullMoved = true; _cullAt = position; _cullPointAt = point; _cullNext = now + CullEverySeconds;
            if ((RoomAt(position) ?? (room is { } given ? RoomAt(given) : null)) is { } found) _cullNode = found;
        }
        Put(culler, _cullNode, position);
    }

    private static void Put(C_MovingCuller culler, C_Node? node, Vector3 position)
    {
        if (node == null) return;
        if (culler.CurrentNode?.Pointer != node.Pointer) culler.SetCurrentNode(node);
        // The agent's lights and its next portal check start from the view.
        culler.m_position = position;
        culler.lastCrossCheckPos = position;
    }

    private static C_Node? RoomAt(Vector3 at)
    {
        if (Dimension.TryGetCourseNodeFromPos(at, out var course) && course != null && course.m_cullNode != null) return course.m_cullNode;
        if (!Physics.Raycast(at, Vec.Down, out var hit, CullFloorReach, LayerManager.MASK_WORLD)) return null;
        return Dimension.TryGetCourseNodeFromPos(hit.point + Vec.Up * 0.3f, out course) && course != null ? course.m_cullNode : null;
    }

    private static void CullFromPlayer()
    {
        if (!_cullMoved) return;
        _cullMoved = false; _cullNode = null;
        var agent = Players.Local;
        var culler = C_Camera.Current != null && !C_Camera.Current.WasCollected ? C_Camera.Current.m_cullAgent : null;
        if (agent == null || agent.WasCollected || culler == null || culler.WasCollected) return;
        Put(culler, RoomAt(agent.Position), agent.Position);
        culler.UpdatePosition(agent.DimensionIndex, agent.Position);
        culler.RefreshNode(agent.DimensionIndex);
    }

    public void Dispose()
    {
        var owned = OwnsView;
        if (!Book.Release(_entry)) return;
        Apply();
        // Only the lease that owned the view puts it back; an older lease still held sets its own pose again at its next Look.
        if (!owned) return;
        var agent = Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera != null && !camera.WasCollected) { camera.m_hasPositionOverride = false; camera.m_hasRotationOverride = false; }
        CullFromPlayer();
    }

    internal static void ReleaseAll()
    {
        Book.ReleaseAll();
        Apply();
        // The node belonged to this level: the next level's first view looks its room up afresh, even at the same place.
        try { CullFromPlayer(); }
        catch (Exception error) { Log.For("ForgeCore").ErrorOnce("core-views-culling-could-not-go-back", "The view's culling could not go back to the player: " + error.Message); }
    }

    /// <summary>A local player who goes down loses the view; one who leaves the level loses every lease at level cleanup.</summary>
    private static void WatchDown()
    {
        if (_watchingDown) return;
        GameEvents.LocalPlayerDown += static () => ReleaseAll();
        _watchingDown = true;
    }

    private static void Apply()
    {
        var agent = Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected) { _appliedLift = 0f; _nearApplied = false; return; }
        if (Book.Count == 0) { camera.m_hasPositionOverride = false; camera.m_hasRotationOverride = false; }
        ApplyLift(camera, Book.Lift);
        ApplyNear(camera, Book.NearClip);
    }

    /// <summary>The lift rides on top of the game's own camera offset. A target the game wrote gets the lift added, and a change of
    /// lift shifts the current, start and target offsets together, so the game's own transitions keep running.</summary>
    private static void ApplyLift(FPSCamera camera, float lift)
    {
        var target = camera.m_posOffsetTarget;
        if (target != _writtenTarget)
        {
            target += Vec.Up * _appliedLift;
            camera.m_posOffsetTarget = target;
            _writtenTarget = target;
        }
        if (Math.Abs(lift - _appliedLift) < 0.0001f) return;
        var d = Vec.Up * (lift - _appliedLift);
        _appliedLift = lift;
        camera.m_posOffset += d;
        camera.m_posOffsetStart += d;
        camera.m_posOffsetTarget += d;
        _writtenTarget = camera.m_posOffsetTarget;
    }

    private static void ApplyNear(FPSCamera camera, float plane)
    {
        var lens = camera.m_camera;
        if (lens == null || lens.WasCollected) return;
        if (float.IsNaN(plane))
        {
            if (!_nearApplied) return;
            _nearApplied = false;
            lens.nearClipPlane = _gameNear;
            return;
        }
        if (!_nearApplied) { _gameNear = lens.nearClipPlane; _nearApplied = true; }
        lens.nearClipPlane = plane;
    }
}
