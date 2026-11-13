using System;
using System.Collections.Generic;
using Agents;
using HarmonyLib;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The local player's hits on enemies, read where the game applies them on this machine (<c>Dam_EnemyDamageBase.BulletDamage</c> /
/// <c>MeleeDamage</c>: the health before and after the call is the hit's damage; a drop to zero is the local player's kill) and the
/// game's own weak-spot indicator (<c>Dam_EnemyDamageLimb.ShowHitIndicator</c>), handed to the enemy health bars
/// (<see cref="EnemyHealthBars"/>) and the hit feedback (<see cref="HitFeedbacks"/>). Nothing is read while neither wants them.
/// </summary>
internal static class LocalHits
{
    // The hit in progress on this machine: the damage base and its health before the game's call.
    private static IntPtr _hitBase;
    private static float _hitBefore;

    private static bool Wanted => EnemyHealthBars.WantsLocalHits || HitFeedbacks.WantsLocalHits;

    private static void Before(Dam_EnemyDamageBase target, Agent? source)
    {
        _hitBase = IntPtr.Zero;
        if (source == null || target == null || !Wanted) return;
        var local = ForgeCore.Players.Local;
        if (local == null || local.Pointer != source.Pointer) return;
        _hitBase = target.Pointer;
        _hitBefore = target._Health_k__BackingField;
    }

    private static void After(Dam_EnemyDamageBase target, Vector3 at)
    {
        if (_hitBase == IntPtr.Zero || target == null || target.Pointer != _hitBase) return;
        _hitBase = IntPtr.Zero;
        float before = _hitBefore, after = target._Health_k__BackingField, max = target._HealthMax_k__BackingField;
        if (before <= 0f || after >= before) return;
        EnemyHealthBars.LocalHit(target, before, after, max);
        HitFeedbacks.LocalHit((long)target.Pointer, before - after, at, after <= 0f);
    }

    [PatchGroup(PatchGroup.LocalHits)]
    [HarmonyPatch]
    private static class HitPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            // Declared only: the base class Dam_SyncedDamageBase has its own overloads of both names (no limb parameter), which the
            // limbs never call for an enemy and which make a name-only lookup ambiguous.
            yield return AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage));
            yield return AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.MeleeDamage));
        }

        // Parameter 1 is the source agent and 2 the hit point in both methods.
        private static void Prefix(Dam_EnemyDamageBase __instance, Agent __1)
        {
            try { Before(__instance, __1); }
            catch (Exception error) { _hitBase = IntPtr.Zero; Plugin.Error("ui-reading-local-players-hit-failed", "Reading the local player's hit failed: " + error); }
        }

        private static void Postfix(Dam_EnemyDamageBase __instance, Vector3 __2)
        {
            try { After(__instance, __2); }
            catch (Exception error) { _hitBase = IntPtr.Zero; Plugin.Error("ui-reading-local-players-hit-failed", "Reading the local player's hit failed: " + error); }
        }
    }

    [PatchGroup(PatchGroup.LocalHits)]
    [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ShowHitIndicator))]
    private static class WeakSpotPatch
    {
        private static void Postfix(Dam_EnemyDamageLimb __instance, bool __0)
        {
            if (!__0 || !HitFeedbacks.WantsLocalHits) return;
            try
            {
                var target = __instance.m_base;
                if (target != null) HitFeedbacks.Critical((long)target.Pointer);
            }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-weak-spot-hit", "Reading the local player's weak-spot hit failed: " + error); }
        }
    }

    /// <summary>The level ended (<see cref="ForgeCore.GameEvents.LevelEnded"/>).</summary>
    internal static void LevelEnded()
    {
        if (EnemyHealthBars.Used) EnemyHealthBars.Clear();
        HitFeedbacks.Clear();
    }
}
