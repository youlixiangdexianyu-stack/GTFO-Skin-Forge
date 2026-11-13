using System;
using HarmonyLib;

namespace ForgeCore;

public static partial class GameHooks
{
    /// <summary><c>Dam_EnemyDamageBase.ReceiveBulletDamage</c>, the game's own receiver for every stock and Forge bullet packet on an enemy,
    /// run on every machine. <c>Combat.Killed</c> is read from it.</summary>
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, pBulletDamageData Data)> EnemyBulletReceived = new("GameHooks.EnemyBulletReceived", typeof(EnemyBulletReceivedHook));
    /// <summary><c>Dam_EnemyDamageBase.ReceiveMeleeDamage</c>, the receiver of a melee packet on an enemy, run on every machine.</summary>
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, pFullDamageData Data)> EnemyMeleeReceived = new("GameHooks.EnemyMeleeReceived", typeof(EnemyMeleeReceivedHook));
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveBulletDamage))]
internal static class EnemyBulletReceivedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageBase __instance, pBulletDamageData __0, out (Dam_EnemyDamageBase, pBulletDamageData) __state)
    { __state = (__instance, __0); GameHooks.EnemyBulletReceived.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((Dam_EnemyDamageBase Receiver, pBulletDamageData Data) __state, bool __runOriginal) => GameHooks.EnemyBulletReceived.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (Dam_EnemyDamageBase Receiver, pBulletDamageData Data) __state) { if (__exception != null) GameHooks.EnemyBulletReceived.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveMeleeDamage))]
internal static class EnemyMeleeReceivedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageBase __instance, pFullDamageData __0, out (Dam_EnemyDamageBase, pFullDamageData) __state)
    { __state = (__instance, __0); GameHooks.EnemyMeleeReceived.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((Dam_EnemyDamageBase Receiver, pFullDamageData Data) __state, bool __runOriginal) => GameHooks.EnemyMeleeReceived.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (Dam_EnemyDamageBase Receiver, pFullDamageData Data) __state) { if (__exception != null) GameHooks.EnemyMeleeReceived.RunFaulted(__state, __exception); return __exception; }
}
