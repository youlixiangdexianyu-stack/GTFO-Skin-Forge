using AIGraph;
using ChainedPuzzles;
using LevelGeneration;
using UnityEngine;

namespace ForgeCore;

/// <summary>Puts level objects in a given state through the game's own state handlers. With <c>instant</c> the handler is told the state
/// arrives as a drop-in or a recall, which is how the game applies a state to a player who joins late: the object ends in the state with no
/// opening animation, sound or effect (a jump or a checkpoint restore). Without it the handler runs as for a live change, with the game's
/// animation, sound and effect. Nothing is replicated and no game rule runs (no unlocking, no waves, no objectives); the state is only shown.
/// Main thread only.</summary>
public static class WorldStates
{
    // A door's or box's handler plays its animator's state, then switches the animator off a moment later. An animator that culls itself out of view never takes the pose in that moment,
    // so the object stays as it was until it is next played. It always animates here, and a state set at once takes its pose now, seen or not.
    private static void Settle(GameObject root, bool instant)
    {
        foreach (var animator in root.GetComponentsInChildren<Animator>(true))
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (instant && animator.isActiveAndEnabled) animator.Update(10f);
        }
    }

    public static void Door(LG_SecurityDoor door, eDoorStatus status, bool instant)
    {
        door.OnSyncDoorStatusChange(DoorState(status, instant), instant);
        Settle(door.gameObject, instant);
    }

    public static void Door(LG_WeakDoor door, eDoorStatus status, bool instant)
    {
        // A weak door's break is shown by its damage handler (the punch and the explosion of its destruction), which the game runs beside the state change; the state handler alone
        // shows a destroyed door only as a drop-in (the look a late joiner needs). So a live break runs the break handler too and the state then settles as a drop-in.
        var destroyed = status == eDoorStatus.Destroyed;
        if (destroyed && !instant) door.OnSyncDoorGotDestroyed(door.m_healthMax, false, false, null);
        door.OnSyncDoorStateChange(DoorState(status, instant || destroyed), instant || destroyed);
        Settle(door.gameObject, instant || destroyed);
    }

    public static void Generator(LG_PowerGenerator_Core generator, ePowerGeneratorStatus status, bool instant)
        // The game's handler does nothing when the old and new status are equal and it is not a drop-in, so the old one always differs.
        => generator.OnStateChange(new pPowerGeneratorState { status = status == ePowerGeneratorStatus.Powered ? ePowerGeneratorStatus.UnPowered : ePowerGeneratorStatus.Powered }, new pPowerGeneratorState { status = status }, instant);

    /// <summary>A pickup at <paramref name="status"/>. With <paramref name="place"/> the pickup is placed there, on the floor, the way the
    /// game tells everyone of an item put down somewhere else (the state's placement carries the place and its course node); without it the
    /// pickup keeps its placement.</summary>
    public static void Item(LG_PickupItem_Sync item, ePickupItemStatus status, bool instant, Vector3? place = null)
    {
        var state = item.GetCurrentState();
        state.status = status;
        if (place is { } at)
        {
            var placement = state.placement;
            placement.position = at;
            placement.droppedOnFloor = true;
            if (Level.CourseNodeAt(at, 1f, out var node, out _) && node != null)
            {
                var course = default(pCourseNode);
                course.Set(node);
                placement.node = course;
            }
            state.placement = placement;
        }
        item.OnStateChange(item.GetCurrentState(), state, instant);
    }

    /// <summary>Shows the fog of data block <paramref name="fogId"/> (<c>FogSettingsDataBlock</c>) in this game only, through the game's
    /// <c>EnvironmentStateManager.SetFogSettingsLocal</c>: the level's replicated environment state is not changed, so nothing is sent and
    /// <c>EnvironmentStateManager.InstantUpdateEnvironmentSettings</c> later puts the level's own fog back. False when the game has no
    /// environment manager yet.</summary>
    public static bool Fog(uint fogId)
    {
        if (EnvironmentStateManager.Current == null) return false;
        EnvironmentStateManager.SetFogSettingsLocal(fogId);
        return true;
    }

    /// <summary>The game's own fog block with no density at all (<c>Fog_ELEVATOR</c>, the one its elevator ride uses): <see cref="ClearFog"/> shows it.</summary>
    public const uint NoFogId = 21;

    /// <summary>Shows no fog in this game only (<see cref="Fog"/> with <see cref="NoFogId"/>); <see cref="LevelFog"/> puts the level's own back. False when the game
    /// has no environment manager yet.</summary>
    public static bool ClearFog() => Fog(NoFogId);

    /// <summary>The fog data block id of the level's environment state in the reality dimension (what <see cref="LevelFog"/> shows).</summary>
    public static uint CurrentFog() => EnvironmentStateManager.GetCurrentFogID(eDimensionIndex.Reality);

    /// <summary>Shows the light mode of every zone, as dimension, layer and zone indices into <paramref name="modes"/>, through the game's own
    /// <c>UpdateAllLightModes</c>, on this machine only; indices the level does not have are left out. <paramref name="instant"/> skips the lights' change animation.
    /// False when the level has no environment state.</summary>
    public static bool ZoneLights(byte[][][] modes, bool instant)
    {
        var manager = EnvironmentStateManager.Current;
        if (manager == null || manager.m_stateReplicator == null) return false;
        var state = manager.m_stateReplicator.State;
        var dimensions = state.DimensionLightState;
        if (dimensions == null) return false;
        // The state is a struct, but its arrays are the replicator's own: what is written here is put back after the game applied it,
        // so LevelFog (and anything the replicator sends) still sees the level's own light modes.
        var saved = new System.Collections.Generic.List<(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> Zones, byte[] Level)>();
        for (var d = 0; d < dimensions.Length && d < modes.Length; d++)
        {
            var layers = dimensions[d].LayerLightState;
            if (layers == null) continue;
            for (var l = 0; l < layers.Length && l < modes[d].Length; l++)
            {
                var zones = layers[l].ZoneLightsState;
                if (zones == null) continue;
                saved.Add((zones, Il2CppArrays.ToManaged(zones)));
                for (var z = 0; z < zones.Length && z < modes[d][l].Length; z++) zones[z] = modes[d][l][z];
            }
        }
        try { EnvironmentStateManager.UpdateAllLightModes(state, instant); }
        finally { foreach (var (zones, level) in saved) for (var z = 0; z < level.Length; z++) zones[z] = level[z]; }
        return true;
    }

    /// <summary>Shows the level's own environment again (fog and light modes of its state), the way the game applies a recalled state.</summary>
    public static void LevelFog()
    {
        if (EnvironmentStateManager.Current != null) EnvironmentStateManager.InstantUpdateEnvironmentSettings();
    }

    public static void Puzzle(ChainedPuzzleInstance puzzle, eChainedPuzzleStatus status, bool instant)
    {
        var state = new pChainedPuzzleState { status = status, isActive = status == eChainedPuzzleStatus.Active, isSolved = status == eChainedPuzzleStatus.Solved };
        puzzle.OnStateChange(state, state, instant);
        if (instant && status != eChainedPuzzleStatus.Active) Silence(puzzle.m_sound);
    }

    // A handler told the state arrives as a drop-in starts what the state sounds (an alarm's or a scan's loop) but stops nothing: a player who joins late has nothing playing.
    // Put into a state that ends those sounds, the object's own sound player is stopped here, so a loop an earlier drop-in started does not outlive its state.
    private static void Silence(CellSoundPlayer? sound) => sound?.Stop();

    /// <summary>A bioscan at <paramref name="status"/> with <paramref name="progress"/> (0 to 1), nobody inside it: the scan's own sync handler
    /// shows its ring, sound and finish.</summary>
    public static void Scan(CP_Bioscan_Core scan, eBioscanStatus status, float progress, bool instant)
        => scan.OnSyncStateChange(status, progress, NoPlayers ??= new(), 0, NoItems ??= new(0), instant);

    /// <summary><see cref="Scan(CP_Bioscan_Core, eBioscanStatus, float, bool)"/> with <paramref name="inside"/> standing in it, out of the <paramref name="needed"/> players it wants: the game's scan sound and ring count them.
    /// With <paramref name="instant"/> a scan past its reveal has its holopath fully drawn: the handler leaves a scanning one's as it finds it, and a reveal still running would end in setting the scan waiting.</summary>
    public static void Scan(CP_Bioscan_Core scan, eBioscanStatus status, float progress, Il2CppSystem.Collections.Generic.List<Player.PlayerAgent> inside, int needed, bool instant)
    {
        scan.OnSyncStateChange(status, progress, inside, needed, NoItems ??= new(0), instant);
        if (instant && status is eBioscanStatus.Waiting or eBioscanStatus.Scanning) PathDrawn(scan.m_spline);
        if (instant && status != eBioscanStatus.Scanning) Silence(scan.m_sound);
    }

    // The handler's finish always reports the puzzle done, which starts the next scan of the chain or solves it. It calls the
    // delegate without a null check (CP_Cluster_Core.OnSyncStateChange, Finished branch), so it must stay a callable one: a
    // no-op keeps the game rule out and does not throw where null did.
    private static readonly Il2CppSystem.Action<int> NoPuzzleDone = new System.Action<int>(static _ => { });

    /// <summary>A scan cluster (the ring that holds several scans) at <paramref name="status"/> with <paramref name="progress"/> (0 to 1): its
    /// own sync handler shows or hides its ring, its holopath and its HUD and switches its scans on or off. Only what it shows: the handler's finish
    /// also reports the puzzle done, always, which starts the next scan of the chain or solves it, so <c>OnPuzzleDone</c> is a no-op around it.
    /// False when the game refused the state (a cluster the level has not set up); the caller counts it as not restored.</summary>
    public static bool Cluster(CP_Cluster_Core cluster, eClusterStatus status, float progress, bool instant)
    {
        // A core the level never set up (a cluster prefab's child, not a built puzzle) has these empty; the handler would throw on
        // the first one. It is skipped with the others the caller counts, not called.
        if (cluster.m_sync == null || cluster.m_spline == null || cluster.m_childCores == null) return false;
        var done = cluster.OnPuzzleDone;
        cluster.OnPuzzleDone = NoPuzzleDone;
        try { cluster.OnSyncStateChange(status, progress, instant); }
        finally { cluster.OnPuzzleDone = done; }
        if (instant && status == eClusterStatus.ClusterActive) PathDrawn(cluster.m_spline);
        return true;
    }

    // Hidden first: that stops a reveal still running.
    private static void PathDrawn(iChainedPuzzleHolopathSpline? spline)
    {
        if (spline?.TryCast<CP_Holopath_Spline>() is not { } path) return;
        path.SetVisible(false);
        HolopathReveal(path, true);
    }

    /// <summary>A weak lock on a door (a padlock to break or a lock to melt or hack) at <paramref name="status"/>: its own sync handler shows the
    /// lock, the melter on it or neither. A lock shown locked again after it was opened gets its model back.</summary>
    public static void WeakLock(LG_WeakLock weakLock, eWeakLockStatus status, bool open, bool instant)
        => weakLock.OnSyncStatusChanged(status, open, instant);

    /// <summary>A disinfection station going from <paramref name="oldStatus"/> to <paramref name="newStatus"/>: the game's own status handler
    /// (<c>SyncStatusChanged</c>, the one the replicated state change ends in), so its look and sound change. Its replicated state, its interaction and
    /// the disinfecting of any player are not touched.</summary>
    public static void Disinfection(LG_DisinfectionStation station, eDisinfectionStationStatus oldStatus, eDisinfectionStationStatus newStatus)
        => station.SyncStatusChanged(oldStatus, newStatus);

    /// <summary>A supply box or locker at <paramref name="status"/>: its lid, light and sound.</summary>
    public static void Container(LG_ResourceContainer_Sync container, eResourceContainerStatus status, bool instant)
    {
        // The game's handler does nothing when the old and new status are equal and it is not a drop-in: the lid never opened in plain playback.
        var old = new pResourceContainerItemState { status = status == eResourceContainerStatus.Open ? eResourceContainerStatus.Closed : eResourceContainerStatus.Open };
        container.OnStateChange(old, new pResourceContainerItemState { status = status }, instant);
        Settle(container.gameObject, instant);
    }

    /// <summary>A reactor at <paramref name="status"/> with <paramref name="progress"/> (0 to 1) of its current stage.</summary>
    public static void Reactor(LG_WardenObjective_Reactor reactor, eReactorStatus status, float progress, bool instant)
    {
        // The game's handler returns at once when the old and new status are equal, so the old one always differs.
        var old = new pReactorState { status = status == eReactorStatus.Active_Idle ? eReactorStatus.Inactive_Idle : eReactorStatus.Active_Idle };
        reactor.OnStateChange(old, new pReactorState { status = status, stateProgress = progress }, instant);
    }

    /// <summary>A terminal at <paramref name="state"/> with <paramref name="user"/> at it (null: nobody), through the terminal's own receive
    /// handler for a state another machine sent (<c>SyncChangeState</c>): its screen wakes, sleeps or shows the interface. Only Sleeping, Awake
    /// and PlayerInteracting are shown; the other states belong to commands and run their rules, so they are refused (false).</summary>
    public static bool TerminalState(LG_ComputerTerminal terminal, TERM_State state, Player.PlayerAgent? user)
    {
        if (state is not (TERM_State.Sleeping or TERM_State.Awake or TERM_State.PlayerInteracting)) return false;
        terminal.SyncChangeState(state, user);
        return true;
    }

    /// <summary>A line on a terminal's screen, printed through the terminal's own output queue: it appears after the lines before it have, with
    /// its <paramref name="wait"/> (a progress line's), its print sound and its line type's look. With no <paramref name="type"/> the line is a plain
    /// one, broken to the screen's width as the game breaks it.</summary>
    public static void TerminalPrint(LG_ComputerTerminal terminal, TerminalLineType? type, string line, float wait)
    {
        if (type is { } typed) terminal.m_command.AddOutput(typed, line, wait, TerminalSoundType.LineTypeDefault, TerminalSoundType.LineTypeDefault);
        else terminal.m_command.AddOutput(line, false);
    }

    /// <summary>What is typed at a terminal so far. With <paramref name="sound"/> through the terminal's receive handler for another player's
    /// typing (<c>SyncIncomingTerminalString</c>), which plays a key sound per new character; otherwise the input line is only shown.</summary>
    public static void TerminalTyping(LG_ComputerTerminal terminal, string typed, bool sound)
    {
        if (sound) { terminal.SyncIncomingTerminalString(typed); return; }
        terminal.m_currentLine = typed;
        terminal.m_command.UpdateTerminalScreen(typed, false);
    }

    /// <summary>A terminal's screen at once: <paramref name="lines"/> (oldest first, each broken to the screen's width as the game breaks it, the
    /// screen keeping as many of the last as it holds) and the input line <paramref name="typed"/>. The lines still waiting to print are dropped;
    /// no sound plays.</summary>
    public static void TerminalScreen(LG_ComputerTerminal terminal, System.Collections.Generic.IReadOnlyList<string> lines, string typed)
    {
        var command = terminal.m_command;
        command.ClearOutputQueueAndScreenBuffer();
        var buffer = command.m_screenBuffer;
        foreach (var line in lines)
        {
            var broken = command.GetLinebreakedLines(line, 0, false);
            for (var i = 0; i < broken.Count; i++) buffer.Add(broken[i].Text);
        }
        var max = command.m_maxLines;
        if (max > 0 && buffer.Count > max) buffer.RemoveRange(0, buffer.Count - max);
        terminal.m_currentLine = typed;
        command.UpdateTerminalScreen(typed, false);
    }

    /// <summary>The lines on a terminal's screen now, oldest first, added to <paramref name="into"/>.</summary>
    public static void TerminalLines(LG_ComputerTerminal terminal, System.Collections.Generic.List<string> into)
    {
        var buffer = terminal.m_command.m_screenBuffer;
        for (var i = 0; i < buffer.Count; i++) into.Add(buffer[i]);
    }

    // The level machines below keep the replicated state they already have (item data, who changed it) and change only the fields named; each
    // runs the machine's own state handler with that state as the old one, so its look, animation and sound follow. False when the machine has no replicator yet.

    /// <summary>A bulkhead door controller (<c>eBulkheadDCStatus</c> as a byte) at <paramref name="status"/>.</summary>
    public static bool Bulkhead(LG_BulkheadDoorController_Core core, byte status, bool instant)
    {
        if (core.m_stateReplicator == null) return false;
        var old = core.m_stateReplicator.State;
        var state = old;
        state.status = (eBulkheadDCStatus)status;
        core.OnStateChange(old, state, instant);
        return true;
    }

    /// <summary>The status of <paramref name="core"/> now (<c>eBulkheadDCStatus</c>), false when it has no replicator.</summary>
    public static bool BulkheadStatus(LG_BulkheadDoorController_Core core, out byte status)
    {
        status = 0;
        if (core.m_stateReplicator == null) return false;
        status = (byte)core.m_stateReplicator.State.status;
        return true;
    }

    /// <summary>A HSU activator (<c>eHSUActivatorStatus</c> as a byte), with whether its insert sequence is incomplete.</summary>
    public static bool Hsu(LG_HSUActivator_Core core, byte status, bool sequenceIncomplete, bool instant)
    {
        if (core.m_stateReplicator == null) return false;
        var old = core.m_stateReplicator.State;
        var state = old;
        state.status = (eHSUActivatorStatus)status;
        state.isSequenceIncomplete = sequenceIncomplete;
        core.OnStateChange(old, state, instant);
        return true;
    }

    public static bool HsuStatus(LG_HSUActivator_Core core, out byte status, out bool sequenceIncomplete)
    {
        status = 0; sequenceIncomplete = false;
        if (core.m_stateReplicator == null) return false;
        var state = core.m_stateReplicator.State;
        status = (byte)state.status; sequenceIncomplete = state.isSequenceIncomplete;
        return true;
    }

    /// <summary>A dimension portal (<c>eDimensionPortalStatus</c> as a byte), whether it is closed and whether its insert sequence is incomplete.</summary>
    public static bool Portal(LG_DimensionPortal portal, byte status, bool closed, bool sequenceIncomplete, bool instant)
    {
        if (portal.m_stateReplicator == null) return false;
        var old = portal.m_stateReplicator.State;
        var state = old;
        state.status = (eDimensionPortalStatus)status;
        state.isClosed = closed;
        state.isSequenceIncomplete = sequenceIncomplete;
        portal.OnStateChange(old, state, instant);
        return true;
    }

    public static bool PortalStatus(LG_DimensionPortal portal, out byte status, out bool closed, out bool sequenceIncomplete)
    {
        status = 0; closed = sequenceIncomplete = false;
        if (portal.m_stateReplicator == null) return false;
        var state = portal.m_stateReplicator.State;
        status = (byte)state.status; closed = state.isClosed; sequenceIncomplete = state.isSequenceIncomplete;
        return true;
    }

    /// <summary>A generator cluster at fog step <paramref name="fogStep"/>.</summary>
    public static bool GeneratorCluster(LG_PowerGeneratorCluster cluster, int fogStep, bool instant)
    {
        if (cluster.m_stateReplicator == null) return false;
        var old = cluster.m_stateReplicator.State;
        var state = old;
        state.currentFogStepIndex = fogStep;
        cluster.OnStateChange(old, state, instant);
        return true;
    }

    public static bool GeneratorClusterStep(LG_PowerGeneratorCluster cluster, out int fogStep)
    {
        fogStep = 0;
        if (cluster.m_stateReplicator == null) return false;
        fogStep = cluster.m_stateReplicator.State.currentFogStepIndex;
        return true;
    }

    /// <summary>A fog turbine (<c>HeavyFogRepellerGlobalState</c>) at <paramref name="status"/>, owned by <paramref name="owner"/> (<c>eCarryItemWithGlobalStateOwner</c>) at <paramref name="at"/>.
    /// The owning player is not set: a state is shown with its place only.</summary>
    public static bool FogTurbine(HeavyFogRepellerGlobalState turbine, byte status, byte owner, Vector3 at, bool instant)
    {
        if (turbine.m_stateReplicator == null) return false;
        var old = turbine.m_stateReplicator.State;
        var state = old;
        state.status = status;
        state.owner = (eCarryItemWithGlobalStateOwner)owner;
        state.staticPosition = at;
        turbine.OnStateChange(old, state, instant);
        return true;
    }

    public static bool FogTurbineState(HeavyFogRepellerGlobalState turbine, out byte status, out byte owner, out Vector3 at)
    {
        status = owner = 0; at = default;
        if (turbine.m_stateReplicator == null) return false;
        var state = turbine.m_stateReplicator.State;
        status = state.status; owner = (byte)state.owner; at = state.staticPosition;
        return true;
    }

    /// <summary>A moving scan's position along its path (<paramref name="lerp"/>, 0 to 1) and whether it is paused: its own state handler moves it.</summary>
    public static bool ScanMove(CP_BasicMovable movable, float lerp, bool paused, bool instant)
    {
        if (movable.m_stateReplicator == null) return false;
        var old = movable.m_stateReplicator.State;
        var state = old;
        state.lerp = lerp;
        state.paused = paused;
        movable.OnStateChange(old, state, instant);
        return true;
    }

    public static bool ScanMoveState(CP_BasicMovable movable, out float lerp, out bool paused)
    {
        lerp = 0f; paused = false;
        if (movable.m_stateReplicator == null) return false;
        var state = movable.m_stateReplicator.State;
        lerp = state.lerp; paused = state.paused;
        return true;
    }

    /// <summary>A holopath at the end of its reveal: with <paramref name="instant"/> fully drawn at once, else the game's own reveal.</summary>
    public static void HolopathReveal(CP_Holopath_Spline spline, bool instant)
    {
        if (!instant) { spline.Reveal(); return; }
        spline.SetVisible(true);
        spline.SetSplineProgress(1f);
    }

    /// <summary>A spitter bursting or glued, through the bodies of its own explode and glue packets (nothing is sent). A spitter already in that state is left.</summary>
    public static void Spitter(InfectionSpitter spitter, bool glued)
    {
        if (glued) { if (!spitter.m_isGlued) spitter.DoGetGlued(); }
        else if (!spitter.m_isExploding) spitter.DoExplode();
    }

    private static Il2CppSystem.Collections.Generic.List<Player.PlayerAgent>? NoPlayers;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool>? NoItems;

    private static pDoorState DoorState(eDoorStatus status, bool instant)
    {
        var open = status is eDoorStatus.Open or eDoorStatus.Opening or eDoorStatus.Destroyed;
        // A door recorded Open is put there when it starts to open: the game's own live handler (`OnSyncDoorStatusChange` ->
        // `LG_SecurityDoor_Anim.OnDoorState`) plays `OpenSecurityDoor` over its three to four seconds from the closed pose. Setting the door's
        // animation progress to its end first put the blades in the open pose, so the opening was a snap. A drop-in (a jump or a recall) still
        // gets the end pose at once.
        var progress = open && (instant || status == eDoorStatus.Destroyed) ? 1f : 0f;
        return new pDoorState { status = status, animProgress = progress, hasBeenOpenedDuringGame = open };
    }
}
