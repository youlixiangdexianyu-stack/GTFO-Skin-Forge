using System;
using BoosterImplants;
using DropServer;
using GameData;
using HarmonyLib;

namespace ForgeCore;

/// <summary>Local clear progress for every custom Rundown (<see cref="IsCustom"/>: MTFO-supplied game data or a Forge Rundown; the game's own
/// Rundowns keep the player's own progress from the game's drop server, untouched): each machine records its own clears and the game's own Rundown page, unlock rules and lobby read them, so a Rundown needs no code
/// to have progress. The unlock rules are the Rundown data block's own fields (tier requirements, each level's accessibility, the level
/// that unlocks it, hide while locked); this class only supplies the record the game otherwise asks its drop server for.
/// The records are kept per Rundown block id (<see cref="ProgressionBook"/>). Main thread only.</summary>
public static class Progression
{
    /// <summary>Raised on this machine after one of its clears was recorded.</summary>
    public static event Action<ExpeditionClear>? Cleared;

    /// <summary>What this machine recorded for one level: <paramref name="tier"/> 1 = A … 5 = E, <paramref name="index"/> its place in the tier from 0.</summary>
    public static ExpeditionProgress Get(uint rundownId, int tier, int index)
        => ProgressionBook.Get(rundownId, RundownManager.GetRundownProgressionExpeditionKey((eRundownTier)tier, index));

    /// <summary>Forgets every clear of one Rundown on this machine and asks the game to read its progress again.</summary>
    public static void Reset(uint rundownId)
    {
        ProgressionBook.Reset(rundownId);
        RundownManager.Current?.RequestRundownProgressionUpdate();
    }

    private static readonly System.Collections.Generic.HashSet<uint> Supplied = new();
    private static bool? _gameDataReplaced;

    /// <summary>Called by a package that loads a Rundown block of its own (ForgeEngine for a Forge Rundown): that Rundown is custom.</summary>
    public static void SuppliedByMod(uint rundownId) => Supplied.Add(rundownId);

    /// <summary>Whether a Rundown's content is custom: a package said it supplied the block, or MTFO loaded the game data from a folder of
    /// its own (MTFO's public <c>ConfigManager.HasGameDataPath</c>), and then no block is the game's own. A custom Rundown follows this
    /// machine's records; the game's own Rundowns are left to the game and its drop server, untouched.</summary>
    internal static bool IsCustom(uint rundownId)
    {
        _gameDataReplaced ??= Type.GetType("MTFO.Managers.ConfigManager, MTFO")?.GetField("HasGameDataPath")?.GetValue(null) is true;
        return _gameDataReplaced == true || Supplied.Contains(rundownId);
    }

    internal static void Start()
    {
        Patches.Ensure(Modules.Progression, typeof(ProgressionHook));
        GameHooks.ExpeditionEnded.After(Ended);
    }

    /// <summary>The game ended an expedition on this machine (every peer passes through its own success state): a success is recorded.</summary>
    private static void Ended(ExpeditionEndState end, bool ran)
    {
        if (!ran || end != ExpeditionEndState.Success) return;
        if (!RundownManager.TryGetIdFromLocalRundownKey(RundownManager.ActiveRundownKey, out uint rundownId) || rundownId == 0 || !IsCustom(rundownId)) return;
        var level = RundownManager.ActiveExpedition;
        if (level == null || level.ExcludeFromProgression) return;
        var active = RundownManager.GetActiveExpeditionData();
        var state = WardenObjectiveManager.CurrentState;
        bool secondary = RundownManager.HasSecondaryLayer(level) && state.second_status == eWardenObjectiveStatus.WardenObjectiveItemSolved;
        bool third = RundownManager.HasThirdLayer(level) && state.third_status == eWardenObjectiveStatus.WardenObjectiveItemSolved;
        bool all = RundownManager.HasAllCompletetionPossibility(level)
            && (secondary || !RundownManager.HasSecondaryLayer(level)) && (third || !RundownManager.HasThirdLayer(level));
        var clear = new ExpeditionClear(rundownId, (int)active.tier, active.expeditionIndex, true, secondary, third, all, all && NoBoosterNoCheckpoint());
        ProgressionBook.Record(clear, RundownManager.GetRundownProgressionExpeditionKey(active.tier, active.expeditionIndex),
            RundownDataBlock.GetBlock(rundownId)?.name ?? "");
        try { Cleared?.Invoke(clear); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-progression-cleared-handler-failed", "A Progression.Cleared handler failed: " + error); }
    }

    private static bool NoBoosterNoCheckpoint()
    {
        if (CheckpointManager.CheckpointUsage != 0) return false;
        var players = BoosterImplantManager.Current?.m_boosterPlayers;
        if (players == null) return true;
        foreach (var player in players)
        {
            var implants = player?.BoosterImplantDatas;
            if (implants == null) continue;
            foreach (var implant in implants)
                if (implant.BoosterImplantID != 0) return false;
        }
        return true;
    }

    private static RundownManager? _asker;
    private static string? _pending;
    private static uint _pendingId;
    private static bool _pendingCurrent, _pumping;

    /// <summary>Every progress request passes here first (any key). A custom Rundown's request is taken (true) and answered on a later frame
    /// by <see cref="Deliver"/>, as the drop server's answer arrives: every page caller asks before it places the Rundown it asked for, and
    /// the page's progress handler reads that placement. Like the game's single request task, a newer request replaces one not yet
    /// answered; any other key goes on to the drop server and drops the pending one.</summary>
    internal static bool Request(RundownManager manager, string? rundownKey, bool updateCurrentRundown)
    {
        if (rundownKey == null || !RundownManager.TryGetIdFromLocalRundownKey(rundownKey, out uint rundownId) || rundownId == 0 || !IsCustom(rundownId))
        {
            _pending = null;
            return false;
        }
        _asker = manager;
        _pending = rundownKey;
        _pendingId = rundownId;
        _pendingCurrent = updateCurrentRundown;
        if (!_pumping) { _pumping = true; Frame.OnUpdate(Plugin.Id, Deliver); }
        return true;
    }

    private static void Deliver()
    {
        if (_pending == null) return;
        var key = _pending;
        _pending = null;
        try { Answer(_asker!, key, _pendingId, _pendingCurrent); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-progression-supply-failed", "Local progress could not be handed to the game; the Rundown shows no progress: " + error); }
    }

    /// <summary>A <c>Local_</c> Rundown's progress answered from this machine's records (through the game's own
    /// <c>RundownProgression.UpdateExpeditionCompletion</c>) to the game's own <c>SetPlayerRundownProgression</c>, which stores it,
    /// aggregates the unlocks and refreshes the page as for a drop server answer. Nothing is sent, so the same path runs online, offline
    /// and without a login.</summary>
    private static void Answer(RundownManager manager, string rundownKey, uint rundownId, bool updateCurrentRundown)
    {
        var native = new RundownProgression { Expeditions = new Il2CppSystem.Collections.Generic.Dictionary<string, RundownProgression.Expedition>() };
        foreach (var (key, c) in ProgressionBook.Expeditions(rundownId))
            for (int i = 0; i < c.main; i++)
            {
                var mask = ExpeditionLayerMask.Main | (i < c.secondary ? ExpeditionLayerMask.Secondary : 0) | (i < c.third ? ExpeditionLayerMask.Third : 0);
                native.UpdateExpeditionCompletion(key, mask, i < c.allClear);
            }
        manager.SetPlayerRundownProgression(new DropServerManager.RundownProgressionResponse
        {
            rundownName = rundownKey,
            rundownProgression = native,
            updateCurrentRundown = updateCurrentRundown
        });
    }
}

/// <summary>Put on when ForgeCore loads: <c>RundownManager.UpdateRundownProgression</c> is where every progress request starts (the Rundown
/// page's selection and placement through <c>RequestRundownProgressionUpdate</c>, the login, the end of a game session, the chain that asks
/// for every loaded Rundown, the debug refresh) before it asks the drop server. A prefix takes a custom Rundown's key and skips the request;
/// every other key, the game's own Rundowns among them, passes untouched.</summary>
internal static class ProgressionHook
{
    [HarmonyPatch(typeof(RundownManager), "UpdateRundownProgression")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Answer(RundownManager __instance, string __0, bool __1)
    {
        try { return !Progression.Request(__instance, __0, __1); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("core-progression-supply-failed", "Local progress could not be handed to the game; the request goes to the game's drop server: " + error);
            return true;
        }
    }
}
