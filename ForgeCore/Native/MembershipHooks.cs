using System;
using GameEvent;
using HarmonyLib;
using Player;
using Metadata = Il2CppSystem.Collections.Generic.Dictionary<string, string>;

namespace ForgeCore;

/// <summary>One call of <c>GameEventManager.PostEvent</c>: its five native arguments.</summary>
public readonly record struct GameEventPost(eGameEvent Event, PlayerAgent? Player, float Value, string? Text, Metadata? Metadata);

public static partial class GameHooks
{
    /// <summary><c>GameEventManager.PostEvent</c>, the game's analytics event entry several packages read.</summary>
    public static readonly Bracket<GameEventPost> GameEventPosted = new("GameHooks.GameEventPosted", typeof(GameEventPostedHook));

    // The three entries every player's arrival and removal passes through. Their postfixes run at Priority.First, not the usual Last:
    // a subscriber reads the player list exactly as the game's body left it, before any other postfix changes it.
    /// <summary><c>PlayerReplicationManager.OnSpawn</c>: the spawn record of a replicated player.</summary>
    public static readonly Bracket<pPlayerSpawnData> PlayerReplicationSpawned = new("GameHooks.PlayerReplicationSpawned", typeof(PlayerReplicationSpawnedHook));
    /// <summary><c>PlayerReplicationManager.OnDeSpawn</c>: a replicated player was removed.</summary>
    public static readonly Bracket<PlayerReplicationManager> PlayerReplicationDespawned = new("GameHooks.PlayerReplicationDespawned", typeof(PlayerReplicationDespawnedHook));
    /// <summary><c>PlayerManager.OnPlayerSpawned</c>, called inside <see cref="PlayerReplicationSpawned"/>.</summary>
    public static readonly Bracket<pPlayerSpawnData> PlayerManagerSpawned = new("GameHooks.PlayerManagerSpawned", typeof(PlayerManagerSpawnedHook));
}

[HarmonyPatch(typeof(GameEventManager), nameof(GameEventManager.PostEvent), new[] { typeof(eGameEvent), typeof(PlayerAgent), typeof(float), typeof(string), typeof(Metadata) })]
internal static class GameEventPostedHook
{
    // Positional names are Harmony's public argument-binding contract, independent of generated parameter names.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(eGameEvent __0, PlayerAgent? __1, float __2, string? __3, Metadata? __4)
        => GameHooks.GameEventPosted.RunBefore(new(__0, __1, __2, __3, __4));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(eGameEvent __0, PlayerAgent? __1, float __2, string? __3, Metadata? __4, bool __runOriginal)
        => GameHooks.GameEventPosted.RunAfter(new(__0, __1, __2, __3, __4), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, eGameEvent __0, PlayerAgent? __1, float __2, string? __3, Metadata? __4)
    { if (__exception != null) GameHooks.GameEventPosted.RunFaulted(new(__0, __1, __2, __3, __4), __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerReplicationManager), nameof(PlayerReplicationManager.OnSpawn))]
internal static class PlayerReplicationSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(pPlayerSpawnData __0) => GameHooks.PlayerReplicationSpawned.RunBefore(__0);
    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void After(pPlayerSpawnData __0, bool __runOriginal) => GameHooks.PlayerReplicationSpawned.RunAfter(__0, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, pPlayerSpawnData __0) { if (__exception != null) GameHooks.PlayerReplicationSpawned.RunFaulted(__0, __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerReplicationManager), nameof(PlayerReplicationManager.OnDeSpawn))]
internal static class PlayerReplicationDespawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PlayerReplicationManager __instance) => GameHooks.PlayerReplicationDespawned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void After(PlayerReplicationManager __instance, bool __runOriginal) => GameHooks.PlayerReplicationDespawned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, PlayerReplicationManager __instance) { if (__exception != null) GameHooks.PlayerReplicationDespawned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.OnPlayerSpawned))]
internal static class PlayerManagerSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(pPlayerSpawnData __0) => GameHooks.PlayerManagerSpawned.RunBefore(__0);
    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void After(pPlayerSpawnData __0, bool __runOriginal) => GameHooks.PlayerManagerSpawned.RunAfter(__0, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, pPlayerSpawnData __0) { if (__exception != null) GameHooks.PlayerManagerSpawned.RunFaulted(__0, __exception); return __exception; }
}
