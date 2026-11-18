using System;
using System.Collections.Generic;
using System.Reflection;
using Agents;
using HarmonyLib;
using UnityEngine;

namespace ForgeCore;

public static partial class GameHooks
{
    /// <summary><c>Dam_EnemyDamageBase.ReceiveBulletDamage</c>, the game's own receiver for every stock and Forge bullet packet on an enemy,
    /// run on every machine. <c>Combat.Killed</c> is read from it.</summary>
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, pBulletDamageData Data)> EnemyBulletReceived = new("GameHooks.EnemyBulletReceived", typeof(EnemyBulletReceivedHook));
    /// <summary><c>Dam_EnemyDamageBase.ReceiveMeleeDamage</c>, the receiver of a melee packet on an enemy, run on every machine.</summary>
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, pFullDamageData Data)> EnemyMeleeReceived = new("GameHooks.EnemyMeleeReceived", typeof(EnemyMeleeReceivedHook));
    /// <summary><c>Dam_EnemyDamageBase.BulletDamage</c> and <c>MeleeDamage</c>, the request a hit on an enemy makes where it happens (the
    /// limbs forward to them): run on the machine of whoever shot or swung, host or client, in the hit's own frame. The host settles it
    /// in the same call (<see cref="EnemyDamageReceived"/>); a client only sends it to the host (<c>SendLocally</c> is <c>SNet.IsMaster</c>).
    /// The call: the receiver, the source agent, the hit point and the damage asked (after the limb's own multipliers, before the host's).</summary>
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, Agent? Source, Vector3 Position, float Damage)> EnemyHitSent = new("GameHooks.EnemyHitSent", typeof(EnemyHitSentHook));
}

[HarmonyPatch]
internal static class EnemyHitSentHook
{
    // Declared only: the base class Dam_SyncedDamageBase has its own overloads of both names (no limb parameter), which the limbs never
    // call for an enemy and which make a name-only lookup ambiguous.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage));
        yield return AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.MeleeDamage));
    }

    // Parameter 0 is the damage, 1 the source agent and 2 the hit point in both methods.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageBase __instance, float __0, Agent __1, Vector3 __2, out (Dam_EnemyDamageBase, Agent?, Vector3, float) __state)
    { __state = (__instance, __1, __2, __0); GameHooks.EnemyHitSent.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((Dam_EnemyDamageBase Receiver, Agent? Source, Vector3 Position, float Damage) __state, bool __runOriginal) => GameHooks.EnemyHitSent.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (Dam_EnemyDamageBase Receiver, Agent? Source, Vector3 Position, float Damage) __state) { if (__exception != null) GameHooks.EnemyHitSent.RunFaulted(__state, __exception); return __exception; }
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
