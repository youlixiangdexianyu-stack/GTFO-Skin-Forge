using System;
using Agents;
using Enemies;
using HarmonyLib;
using UnityEngine;

namespace ForgeCore;

/// <summary>One call of <c>Dam_EnemyDamageBase.ProcessReceivedDamage</c>, the funnel every committed hit on an enemy passes. In
/// <c>Before</c> a subscriber may rewrite <see cref="Damage"/> (the receiver reads it afterwards); in <c>After</c> <see cref="Applied"/> is
/// the receiver's own answer. <see cref="LimbId"/> is the array position the caller named.</summary>
public sealed class EnemyDamageCall
{
    public EnemyDamageCall(Dam_EnemyDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction, ES_HitreactType hitreact,
        bool tryForceHitreact, int limbId, float staggerDamageMulti, DamageNoiseLevel noiseLevel, uint gearCategoryId)
    {
        Receiver = receiver;
        Damage = damage;
        Source = source;
        Position = position;
        Direction = direction;
        Hitreact = hitreact;
        TryForceHitreact = tryForceHitreact;
        LimbId = limbId;
        StaggerDamageMulti = staggerDamageMulti;
        NoiseLevel = noiseLevel;
        GearCategoryId = gearCategoryId;
    }

    public Dam_EnemyDamageBase Receiver { get; }
    public float Damage { get; set; }
    public Agent? Source { get; }
    public Vector3 Position { get; }
    public Vector3 Direction { get; }
    public ES_HitreactType Hitreact { get; }
    public bool TryForceHitreact { get; }
    public int LimbId { get; }
    public float StaggerDamageMulti { get; }
    public DamageNoiseLevel NoiseLevel { get; }
    public uint GearCategoryId { get; }

    /// <summary>The receiver's answer that the damage landed; false in <c>Before</c>.</summary>
    public bool Applied { get; internal set; }
}

public static partial class GameHooks
{
    // ---- Enemies: the agent's life (setup, death, despawn, destruction), its mode, the sync spawn and despawn, and every received hit.
    public static readonly Bracket<EnemyAgent> EnemySetup = new("GameHooks.EnemySetup", typeof(EnemySetupHook));
    public static readonly Bracket<EnemyAgent> EnemyDead = new("GameHooks.EnemyDead", typeof(EnemyDeadHook));
    public static readonly Bracket<EnemyAgent> EnemyDespawned = new("GameHooks.EnemyDespawned", typeof(EnemyDespawnedHook));
    public static readonly Bracket<EnemyAgent> EnemyDestroyed = new("GameHooks.EnemyDestroyed", typeof(EnemyDestroyedHook));
    public static readonly Bracket<EnemyAI> EnemyModeChanged = new("GameHooks.EnemyModeChanged", typeof(EnemyModeChangedHook));
    public static readonly Bracket<(EnemySync Sync, pEnemySpawnData Data)> EnemySyncSpawned = new("GameHooks.EnemySyncSpawned", typeof(EnemySyncSpawnedHook));
    public static readonly Bracket<EnemySync> EnemySyncDespawned = new("GameHooks.EnemySyncDespawned", typeof(EnemySyncDespawnedHook));
    public static readonly Bracket<EnemyDamageCall> EnemyDamageReceived = new("GameHooks.EnemyDamageReceived", typeof(EnemyDamageReceivedHook));
    /// <summary><c>Dam_EnemyDamageLimb.DestroyLimb</c>, the limb break of a plain limb. A limb of the custom class overrides it: for one that
    /// does not call the base body see <see cref="EnemyCustomLimbDestroyed"/>.</summary>
    public static readonly Bracket<Dam_EnemyDamageLimb> EnemyLimbDestroyed = new("GameHooks.EnemyLimbDestroyed", typeof(EnemyLimbDestroyedHook));
    /// <summary><c>Dam_EnemyDamageLimb_Custom.DestroyLimb</c>, the override of a custom limb (the same break through its own body).</summary>
    public static readonly Bracket<Dam_EnemyDamageLimb_Custom> EnemyCustomLimbDestroyed = new("GameHooks.EnemyCustomLimbDestroyed", typeof(EnemyCustomLimbDestroyedHook));

    // ---- The local player's death (the local damage base overrides the receive, so this is the local player's death on every machine).
    public static readonly Bracket<Dam_PlayerDamageLocal> LocalPlayerDied = new("GameHooks.LocalPlayerDied", typeof(LocalPlayerDiedHook));
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.Setup))]
internal static class EnemySetupHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemyAgent __instance) => GameHooks.EnemySetup.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyAgent __instance, bool __runOriginal) => GameHooks.EnemySetup.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyAgent __instance) { if (__exception != null) GameHooks.EnemySetup.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.OnDead))]
internal static class EnemyDeadHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemyAgent __instance) => GameHooks.EnemyDead.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyAgent __instance, bool __runOriginal) => GameHooks.EnemyDead.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyAgent __instance) { if (__exception != null) GameHooks.EnemyDead.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.OnDeSpawn))]
internal static class EnemyDespawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemyAgent __instance) => GameHooks.EnemyDespawned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyAgent __instance, bool __runOriginal) => GameHooks.EnemyDespawned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyAgent __instance) { if (__exception != null) GameHooks.EnemyDespawned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.OnDestroy))]
internal static class EnemyDestroyedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemyAgent __instance) => GameHooks.EnemyDestroyed.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyAgent __instance, bool __runOriginal) => GameHooks.EnemyDestroyed.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyAgent __instance) { if (__exception != null) GameHooks.EnemyDestroyed.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.ModeChange))]
internal static class EnemyModeChangedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemyAI __instance) => GameHooks.EnemyModeChanged.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyAI __instance, bool __runOriginal) => GameHooks.EnemyModeChanged.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyAI __instance) { if (__exception != null) GameHooks.EnemyModeChanged.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnSpawn))]
internal static class EnemySyncSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemySync __instance, pEnemySpawnData spawnData) => GameHooks.EnemySyncSpawned.RunBefore((__instance, spawnData));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemySync __instance, pEnemySpawnData spawnData, bool __runOriginal) => GameHooks.EnemySyncSpawned.RunAfter((__instance, spawnData), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemySync __instance, pEnemySpawnData spawnData) { if (__exception != null) GameHooks.EnemySyncSpawned.RunFaulted((__instance, spawnData), __exception); return __exception; }
}

[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnDespawn))]
internal static class EnemySyncDespawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(EnemySync __instance) => GameHooks.EnemySyncDespawned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemySync __instance, bool __runOriginal) => GameHooks.EnemySyncDespawned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemySync __instance) { if (__exception != null) GameHooks.EnemySyncDespawned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
internal static class EnemyDamageReceivedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageBase __instance, ref float damage, Agent damageSource, Vector3 position, Vector3 direction, ES_HitreactType hitreact,
        bool tryForceHitreact, int limbID, float staggerDamageMulti, DamageNoiseLevel damageNoiseLevel, uint gearCategoryId, out EnemyDamageCall __state)
    {
        __state = new EnemyDamageCall(__instance, damage, damageSource, position, direction, hitreact, tryForceHitreact, limbID, staggerDamageMulti, damageNoiseLevel, gearCategoryId);
        GameHooks.EnemyDamageReceived.RunBefore(__state);
        damage = __state.Damage;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(EnemyDamageCall __state, bool __result, bool __runOriginal) { __state.Applied = __result; GameHooks.EnemyDamageReceived.RunAfter(__state, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, EnemyDamageCall __state) { if (__exception != null) GameHooks.EnemyDamageReceived.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveSetDead))]
internal static class LocalPlayerDiedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_PlayerDamageLocal __instance) => GameHooks.LocalPlayerDied.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Dam_PlayerDamageLocal __instance, bool __runOriginal) => GameHooks.LocalPlayerDied.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, Dam_PlayerDamageLocal __instance) { if (__exception != null) GameHooks.LocalPlayerDied.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.DestroyLimb))]
internal static class EnemyLimbDestroyedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageLimb __instance) => GameHooks.EnemyLimbDestroyed.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Dam_EnemyDamageLimb __instance, bool __runOriginal) => GameHooks.EnemyLimbDestroyed.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, Dam_EnemyDamageLimb __instance) { if (__exception != null) GameHooks.EnemyLimbDestroyed.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_EnemyDamageLimb_Custom), nameof(Dam_EnemyDamageLimb_Custom.DestroyLimb))]
internal static class EnemyCustomLimbDestroyedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageLimb_Custom __instance) => GameHooks.EnemyCustomLimbDestroyed.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Dam_EnemyDamageLimb_Custom __instance, bool __runOriginal) => GameHooks.EnemyCustomLimbDestroyed.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, Dam_EnemyDamageLimb_Custom __instance) { if (__exception != null) GameHooks.EnemyCustomLimbDestroyed.RunFaulted(__instance, __exception); return __exception; }
}
