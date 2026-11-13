using System;
using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>One player agent of the level with who it belongs to. <see cref="Name"/> is read from the game when asked for (the game's
/// string is copied), so iterating with <see cref="Players.Each{TState}"/> allocates nothing until a name is used.</summary>
public readonly struct PlayerInfo
{
    public PlayerInfo(PlayerAgent agent, ulong lookup, int slot, bool isLocal, bool isBot)
    {
        Agent = agent; Lookup = lookup; Slot = slot; IsLocal = isLocal; IsBot = isBot;
    }

    public PlayerAgent Agent { get; }
    /// <summary>The network player's lookup, 0 when the agent has no owner.</summary>
    public ulong Lookup { get; }
    /// <summary>The agent's slot index in the team.</summary>
    public int Slot { get; }
    public bool IsLocal { get; }
    public bool IsBot { get; }
    /// <summary>The player's display name, empty when the agent has no owner.</summary>
    public string Name => Agent.Owner?.NickName ?? "";
}

/// <summary>A player the session listed or dropped (the roster of <see cref="Players.Joined"/> and <see cref="Players.Left"/>); its agent may not exist yet.</summary>
public readonly record struct SessionPlayer(ulong Lookup, bool IsBot, bool IsLocal);

/// <summary>Reads about the players in the level. Main thread only.</summary>
public static partial class Players
{
    private static readonly Dictionary<IntPtr, PlayerMotion> Seen = new();
    private static Action<PlayerAgent, PlayerMotion, PlayerMotion>? _motionChanged;
    private static bool _watching;
    private static Action<SessionPlayer>? _joined, _left;
    private static readonly Action<PlayerInfo> ObserveMotion = static info => Observe(info.Agent);

    /// <summary>The local player's agent; null when there is none (no level, between lives) or it was already collected.</summary>
    public static PlayerAgent? Local
    {
        get
        {
            var agent = PlayerManager.GetLocalPlayerAgent();
            return agent == null || agent.WasCollected ? null : agent;
        }
    }

    /// <summary>The camera the player's picture comes from: the game's current camera when it is active (dead and watching a teammate,
    /// a scripted shot), else the local player's first-person camera, else <c>Camera.main</c>; null when none is usable. Effects,
    /// sound and HUD placement read the view from this, not from the local player's body.</summary>
    public static Camera? WorldCamera()
    {
        var current = CameraManager.CurrentCam;
        var camera = current == null || current.WasCollected ? null : current.m_camera;
        if (!NativeObjects.Gone(camera) && camera!.isActiveAndEnabled) return camera;
        var fps = Local?.FPSCamera;
        camera = fps == null || fps.WasCollected ? null : fps.m_camera;
        if (!NativeObjects.Gone(camera)) return camera;
        camera = Camera.main;
        return NativeObjects.Gone(camera) ? null : camera;
    }

    /// <summary>The local player's agent while it is alive; null otherwise.</summary>
    public static PlayerAgent? LocalAlive
    {
        get
        {
            var agent = Local;
            return agent != null && agent.Alive ? agent : null;
        }
    }

    /// <summary>Calls <paramref name="visit"/> once for every live player agent of the level (the local one, remote ones and bots), in
    /// the game's list order, allocating nothing when <paramref name="visit"/> is a static lambda.</summary>
    public static void Each(Action<PlayerInfo> visit)
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) return;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (agent == null || agent.WasCollected) continue;
            var owner = agent.Owner;
            visit(new PlayerInfo(agent, owner == null ? 0UL : owner.Lookup, agent.PlayerSlotIndex, owner != null && owner.IsLocal, owner != null && owner.IsBot));
        }
    }

    /// <summary>As <see cref="Each(Action{PlayerInfo})"/>, handing <paramref name="state"/> to <paramref name="visit"/> so it needs no closure.</summary>
    public static void Each<TState>(TState state, Action<TState, PlayerInfo> visit)
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) return;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (agent == null || agent.WasCollected) continue;
            var owner = agent.Owner;
            visit(state, new PlayerInfo(agent, owner == null ? 0UL : owner.Lookup, agent.PlayerSlotIndex, owner != null && owner.IsLocal, owner != null && owner.IsBot));
        }
    }

    /// <summary>The session lists a player (also this machine's own and bots); the player's agent may not exist yet.</summary>
    public static event Action<SessionPlayer>? Joined { add => _joined += value; remove => _joined -= value; }

    /// <summary>The session no longer lists a player.</summary>
    public static event Action<SessionPlayer>? Left { add => _left += value; remove => _left -= value; }

    internal static void NoteSession(SessionPlayer player, bool added)
        => Log.For("ForgeCore").RaiseEach(added ? _joined : _left, player, "core-player-joined-left-handler-failed", added ? "player joined" : "player left");

    /// <summary>What <paramref name="agent"/>'s body is doing now (the local player and remote ones alike); <see cref="PlayerMotion.Empty"/> when it has no locomotion.</summary>
    public static PlayerMotion Motion(PlayerAgent agent)
    {
        var locomotion = agent == null || agent.WasCollected ? null : agent.Locomotion;
        return locomotion == null ? PlayerMotion.Empty : (PlayerMotion)(byte)locomotion.m_currentStateEnum;
    }

    /// <summary>Turns the local player's view to look level at <paramref name="target"/> (the game's own <c>FPSCamera.SetLookDirection</c>), as a mod does to face a teammate; nothing for a remote or collected agent.</summary>
    public static void Face(PlayerAgent agent, Vector3 target)
    {
        if (agent == null || agent.WasCollected || !agent.IsLocallyOwned || agent.FPSCamera == null) return;
        var level = target - agent.Position;
        level.y = 0f;
        if (level.sqrMagnitude > 1e-4f) agent.FPSCamera.SetLookDirection(level.normalized);
    }

    /// <summary>A player's motion changed (player, before, now). Every player is read once a frame, with no patch and no allocation.</summary>
    public static event Action<PlayerAgent, PlayerMotion, PlayerMotion>? MotionChanged
    {
        add
        {
            _motionChanged += value;
            if (_watching) return;
            _watching = true;
            Frame.OnUpdate("NAinfini.ForgeCore", Watch);
            GameEvents.LevelEnded += Seen.Clear;
        }
        remove
        {
            _motionChanged -= value;
            if (_motionChanged == null) Seen.Clear();
        }
    }

    private static void Watch()
    {
        // Nobody listens: nothing is read. The next listener starts from a clean slate.
        if (_motionChanged == null) return;
        Each(ObserveMotion);
    }

    private static void Observe(PlayerAgent agent)
    {
        var now = Motion(agent);
        if (!Seen.TryGetValue(agent.Pointer, out var was)) { Seen[agent.Pointer] = now; return; }
        if (was == now) return;
        Seen[agent.Pointer] = now;
        // A handler that throws must not cost the other players' changes this frame (Seen is already updated).
        try { _motionChanged?.Invoke(agent, was, now); }
        catch (Exception error) { Log.For("ForgeCore").ErrorOnce("core-player-motion-handler-failed", "A player motion handler failed: " + error); }
    }
}
