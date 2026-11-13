using System;
using AIGraph;
using GameData;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeCore;

/// <summary>The call of a game method that takes no argument and has no instance (<c>Builder.OnLevelCleanup</c>).</summary>
public readonly struct NoCall { }

/// <summary>One call of <c>LG_MarkerSpawner.TrySpawnRandomPrefab</c>: the request the generator made of one marker and, in <c>After</c>,
/// what the game answered (<see cref="Result"/>, whether a function prefab was placed, and the placed object).</summary>
public sealed class MarkerSpawn
{
    public MarkerSpawn(LG_MarkerSpawner marker, ExpeditionFunction function, float randomSel, float randomRot, float randomRotNoise)
    {
        Marker = marker;
        Function = function;
        RandomSel = randomSel;
        RandomRot = randomRot;
        RandomRotNoise = randomRotNoise;
    }

    public LG_MarkerSpawner Marker { get; }
    public ExpeditionFunction Function { get; }
    public float RandomSel { get; }
    public float RandomRot { get; }
    public float RandomRotNoise { get; }

    /// <summary>The game's <c>out functionWasSpawned</c>; set for <c>After</c>.</summary>
    public bool FunctionWasSpawned { get; internal set; }

    /// <summary>The game's <c>out spawnedGO</c>; set for <c>After</c>.</summary>
    public GameObject? SpawnedObject { get; internal set; }

    /// <summary>What the body returned; false in <c>Before</c>.</summary>
    public bool Result { get; internal set; }
}

/// <summary>One call of <c>LG_PrefabSpawner.OnBuild</c>; <see cref="Built"/> is what the game returned, set for <c>After</c>.</summary>
public sealed class PrefabBuild
{
    public PrefabBuild(LG_PrefabSpawner spawner) { Spawner = spawner; }
    public LG_PrefabSpawner Spawner { get; }
    public GameObject? Built { get; internal set; }
}

/// <summary>One call of the position-carrying <c>Mastermind.TriggerSurvivalWave</c> (the overload that returns its wave identity through
/// <c>out ushort</c>). In <c>Before</c> a subscriber may rewrite <see cref="RefNode"/>, <see cref="SpawnType"/>, <see cref="Position"/>
/// and <see cref="WorldEventFilter"/>, or <see cref="Refuse"/> the wave: the body then does not run, the result is false and the event
/// id is cleared. Every other parameter is the caller's own.</summary>
public sealed class SurvivalWaveStart
{
    public SurvivalWaveStart(AIG_CourseNode refNode, uint settingsId, uint populationDataId, SurvivalWaveSpawnType spawnType, float spawnDelay,
        float areaDistance, Vector3 position, bool playScreamOnSpawn, bool customSpawnDirection, Vector3 spawnDirection, string? worldEventFilter)
    {
        RefNode = refNode;
        SettingsId = settingsId;
        PopulationDataId = populationDataId;
        SpawnType = spawnType;
        SpawnDelay = spawnDelay;
        AreaDistance = areaDistance;
        Position = position;
        PlayScreamOnSpawn = playScreamOnSpawn;
        CustomSpawnDirection = customSpawnDirection;
        SpawnDirection = spawnDirection;
        WorldEventFilter = worldEventFilter;
    }

    public AIG_CourseNode RefNode { get; set; }
    public uint SettingsId { get; }
    public uint PopulationDataId { get; }
    public SurvivalWaveSpawnType SpawnType { get; set; }
    public float SpawnDelay { get; }
    public float AreaDistance { get; }
    public Vector3 Position { get; set; }
    public bool PlayScreamOnSpawn { get; }
    public bool CustomSpawnDirection { get; }
    public Vector3 SpawnDirection { get; }
    public string? WorldEventFilter { get; set; }

    /// <summary>True once a subscriber refused the wave.</summary>
    public bool Refused { get; private set; }

    /// <summary>What the call answered (false when refused); set for <c>After</c>.</summary>
    public bool Result { get; internal set; }

    public void Refuse() => Refused = true;
}

/// <summary>The game's per-frame failure decision (<c>WardenObjectiveManager.CheckExpeditionFailed</c>). <see cref="Native"/> is the answer the
/// body gave; <see cref="Result"/> starts equal to it, a subscriber of <c>After</c> may change it, and the game gets the final value.
/// One instance serves every call (the method is not re-entered), so a subscriber must not keep it.</summary>
public sealed class ExpeditionFailedCheck
{
    internal static readonly ExpeditionFailedCheck Shared = new();
    public bool Native { get; internal set; }
    public bool Result { get; set; }
}

/// <summary>The game's per-frame success decision (<c>WardenObjectiveManager.CheckExpeditionSuccess</c>, static, no argument).
/// <see cref="Result"/> is what the game got, set for <c>After</c>; a <c>Skip</c> subscriber keeps the body from running and the game then
/// reads false (not won). One instance serves every call (the method is not re-entered), so a subscriber must not keep it.</summary>
public sealed class ExpeditionSuccessResult
{
    internal static readonly ExpeditionSuccessResult Shared = new();
    public bool Result { get; internal set; }
}

public static partial class GameHooks
{
    // ---- Level generation and lifetime: cleanup, marker and prefab placement, survival waves, the end and the success and failure checks of an expedition.
    public static readonly Bracket<NoCall> LevelCleanup = new("GameHooks.LevelCleanup", typeof(LevelCleanupHook));
    public static readonly Bracket<MarkerSpawn> MarkerSpawned = new("GameHooks.MarkerSpawned", typeof(MarkerSpawnedHook));
    public static readonly Bracket<PrefabBuild> PrefabBuilt = new("GameHooks.PrefabBuilt", typeof(PrefabBuiltHook));
    public static readonly Bracket<SurvivalWaveStart> SurvivalWaveStarted = new("GameHooks.SurvivalWaveStarted", typeof(SurvivalWaveStartedHook));
    /// <summary><c>WorldEventManager.ExecuteEvent_Internal</c>: the game takes a level event (it runs after its delay); <see cref="WorldEventCall.Result"/> is whether it was accepted, read in <c>After</c>.</summary>
    public static readonly Bracket<WorldEventCall> WorldEventExecuted = new("GameHooks.WorldEventExecuted", typeof(WorldEventExecutedHook));
    public static readonly Bracket<ExpeditionEndState> ExpeditionEnded = new("GameHooks.ExpeditionEnded", typeof(ExpeditionEndedHook));
    public static readonly Bracket<ExpeditionFailedCheck> ExpeditionFailedChecked = new("GameHooks.ExpeditionFailedChecked", typeof(ExpeditionFailedCheckedHook));
    public static readonly Bracket<ExpeditionSuccessResult> ExpeditionSuccessCheck = new("GameHooks.ExpeditionSuccessCheck", typeof(ExpeditionSuccessCheckHook));
    public static readonly Bracket<SNet_Player> PlayerDespawned = new("GameHooks.PlayerDespawned", typeof(PlayerDespawnedHook));
    /// <summary>A chained puzzle told to start or stop by the game's own logic (<c>ChainedPuzzleInstance.AttemptInteract</c>; on the host a security door's
    /// state handler does this). A <c>Skip</c> subscriber keeps the puzzle, its alarm and its waves from running; the states that show it are not touched.</summary>
    public static readonly Bracket<ChainedPuzzles.ChainedPuzzleInstance> ChainedPuzzleInteract = new("GameHooks.ChainedPuzzleInteract", typeof(ChainedPuzzleInteractHook));
}

[HarmonyPatch(typeof(Builder), nameof(Builder.OnLevelCleanup))]
internal static class LevelCleanupHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before() => GameHooks.LevelCleanup.RunBefore(default);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(bool __runOriginal) => GameHooks.LevelCleanup.RunAfter(default, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception) { if (__exception != null) GameHooks.LevelCleanup.RunFaulted(default, __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_MarkerSpawner), nameof(LG_MarkerSpawner.TrySpawnRandomPrefab))]
internal static class MarkerSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_MarkerSpawner __instance, ExpeditionFunction func, float randomSel, float randomRot, float randomRotNoise, out MarkerSpawn __state)
    { __state = new MarkerSpawn(__instance, func, randomSel, randomRot, randomRotNoise); GameHooks.MarkerSpawned.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(MarkerSpawn __state, bool functionWasSpawned, GameObject spawnedGO, bool __result, bool __runOriginal)
    {
        __state.FunctionWasSpawned = functionWasSpawned;
        __state.SpawnedObject = spawnedGO;
        __state.Result = __result;
        GameHooks.MarkerSpawned.RunAfter(__state, __runOriginal);
    }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, MarkerSpawn __state) { if (__exception != null) GameHooks.MarkerSpawned.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_PrefabSpawner), nameof(LG_PrefabSpawner.OnBuild))]
internal static class PrefabBuiltHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_PrefabSpawner __instance, out PrefabBuild __state) { __state = new PrefabBuild(__instance); GameHooks.PrefabBuilt.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(PrefabBuild __state, GameObject __result, bool __runOriginal) { __state.Built = __result; GameHooks.PrefabBuilt.RunAfter(__state, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, PrefabBuild __state) { if (__exception != null) GameHooks.PrefabBuilt.RunFaulted(__state, __exception); return __exception; }
}

/// <summary>One call of <see cref="GameHooks.WorldEventExecuted"/>: the event, the duration the caller passed, and what the game answered.</summary>
public sealed class WorldEventCall
{
    internal WorldEventCall(GameData.WardenObjectiveEventData data, float duration) { Data = data; Duration = duration; }
    public GameData.WardenObjectiveEventData Data { get; }
    public float Duration { get; }
    public bool Result { get; internal set; }
}

[HarmonyPatch(typeof(WorldEventManager), "ExecuteEvent_Internal")]
internal static class WorldEventExecutedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(GameData.WardenObjectiveEventData __0, float __1, out WorldEventCall __state) { __state = new WorldEventCall(__0, __1); GameHooks.WorldEventExecuted.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(WorldEventCall __state, bool __result, bool __runOriginal) { __state.Result = __result; GameHooks.WorldEventExecuted.RunAfter(__state, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, WorldEventCall __state) { if (__exception != null) GameHooks.WorldEventExecuted.RunFaulted(__state, __exception); return __exception; }
}

// The overload that carries a position and returns the wave identity through `out ushort`; the other overload forwards to it. A by-ref
// parameter cannot be named by an attribute, so the pin is the whole parameter list with ArgumentType.Out.
[HarmonyPatch(typeof(Mastermind), nameof(Mastermind.TriggerSurvivalWave),
    new[]
    {
        typeof(AIG_CourseNode), typeof(uint), typeof(uint), typeof(ushort), typeof(SurvivalWaveSpawnType),
        typeof(float), typeof(float), typeof(Vector3), typeof(bool), typeof(bool), typeof(Vector3), typeof(string)
    },
    new[]
    {
        ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal,
        ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
        ArgumentType.Normal, ArgumentType.Normal
    })]
internal static class SurvivalWaveStartedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(ref AIG_CourseNode refNode, uint settingsID, uint populationDataID, ref ushort eventID,
        ref SurvivalWaveSpawnType spawnType, float spawnDelay, float areaDistance, ref Vector3 position,
        bool playScreamOnSpawn, bool customSpawnDirection, Vector3 spawnDirection, ref string overrideWorldEventFilter,
        ref bool __result, out SurvivalWaveStart __state)
    {
        __state = new SurvivalWaveStart(refNode, settingsID, populationDataID, spawnType, spawnDelay, areaDistance, position,
            playScreamOnSpawn, customSpawnDirection, spawnDirection, overrideWorldEventFilter);
        GameHooks.SurvivalWaveStarted.RunBefore(__state);
        refNode = __state.RefNode;
        spawnType = __state.SpawnType;
        position = __state.Position;
        overrideWorldEventFilter = __state.WorldEventFilter!;
        if (!__state.Refused) return true;
        __result = false;
        eventID = 0;
        return false;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SurvivalWaveStart __state, bool __result, bool __runOriginal) { __state.Result = __result; GameHooks.SurvivalWaveStarted.RunAfter(__state, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SurvivalWaveStart __state) { if (__exception != null) GameHooks.SurvivalWaveStarted.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(RundownManager), nameof(RundownManager.OnExpeditionEnded))]
internal static class ExpeditionEndedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ExpeditionEndState endState) => GameHooks.ExpeditionEnded.RunBefore(endState);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ExpeditionEndState endState, bool __runOriginal) => GameHooks.ExpeditionEnded.RunAfter(endState, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ExpeditionEndState endState) { if (__exception != null) GameHooks.ExpeditionEnded.RunFaulted(endState, __exception); return __exception; }
}

// Called by GS_InLevel.Update every frame: the shared call object keeps it allocation-free.
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.CheckExpeditionFailed))]
internal static class ExpeditionFailedCheckedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before() => GameHooks.ExpeditionFailedChecked.RunBefore(ExpeditionFailedCheck.Shared);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ref bool __result, bool __runOriginal)
    {
        var call = ExpeditionFailedCheck.Shared;
        call.Native = call.Result = __result;
        GameHooks.ExpeditionFailedChecked.RunAfter(call, __runOriginal);
        __result = call.Result;
    }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception) { if (__exception != null) GameHooks.ExpeditionFailedChecked.RunFaulted(ExpeditionFailedCheck.Shared, __exception); return __exception; }
}

// Polled while the level runs, like the failure check: the shared call object keeps it allocation-free. A skipped call answers
// false, so the game does not end the level as won.
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.CheckExpeditionSuccess))]
internal static class ExpeditionSuccessCheckHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(ref bool __result)
    {
        var call = ExpeditionSuccessResult.Shared;
        var skip = GameHooks.ExpeditionSuccessCheck.Skipped(call);
        GameHooks.ExpeditionSuccessCheck.RunBefore(call);
        if (!skip) return true;
        __result = false;
        return false;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(bool __result, bool __runOriginal)
    {
        var call = ExpeditionSuccessResult.Shared;
        call.Result = __result;
        GameHooks.ExpeditionSuccessCheck.RunAfter(call, __runOriginal);
    }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception) { if (__exception != null) GameHooks.ExpeditionSuccessCheck.RunFaulted(ExpeditionSuccessResult.Shared, __exception); return __exception; }
}

[HarmonyPatch(typeof(ChainedPuzzles.ChainedPuzzleInstance), nameof(ChainedPuzzles.ChainedPuzzleInstance.AttemptInteract), new[] { typeof(ChainedPuzzles.eChainedPuzzleInteraction) })]
internal static class ChainedPuzzleInteractHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(ChainedPuzzles.ChainedPuzzleInstance __instance)
    {
        var skip = GameHooks.ChainedPuzzleInteract.Skipped(__instance);
        GameHooks.ChainedPuzzleInteract.RunBefore(__instance);
        return !skip;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ChainedPuzzles.ChainedPuzzleInstance __instance, bool __runOriginal) => GameHooks.ChainedPuzzleInteract.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(ChainedPuzzles.ChainedPuzzleInstance __instance, Exception? __exception) { if (__exception != null) GameHooks.ChainedPuzzleInteract.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.OnPlayerDespawned))]
internal static class PlayerDespawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SNet_Player snetPlayer) => GameHooks.PlayerDespawned.RunBefore(snetPlayer);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SNet_Player snetPlayer, bool __runOriginal) => GameHooks.PlayerDespawned.RunAfter(snetPlayer, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SNet_Player snetPlayer) { if (__exception != null) GameHooks.PlayerDespawned.RunFaulted(snetPlayer, __exception); return __exception; }
}
