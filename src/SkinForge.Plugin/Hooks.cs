using System.Reflection;
using Gear;
using Agents;
using HarmonyLib;
using Player;
using SkinForge.Format;
using UnityEngine;

namespace SkinForge.Plugin;

// dump.cs 614836: runs once every part of a gear is spawned and its GearMaterialFeeder is set up.
[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
static class PartsSpawnedHook
{
    static void Postfix(GearPartHolder __instance) => SkinnedGear.Spawned(__instance);
}

// dump.cs 562404: the feeder re-scans its renderers; our material instances are put back afterwards.
[HarmonyPatch(typeof(GearMaterialFeeder), nameof(GearMaterialFeeder.ScanAndSetupParts))]
static class FeederScanHook
{
    static void Postfix(GearMaterialFeeder __instance) => SkinnedGear.FeederScanned(__instance.Pointer);
}

// dump.cs 544371: the funnel every hit on an enemy passes on this machine (the shooter's own hits are applied locally
// too). With the local player as source and the enemy alive before: dead after is a kill, alive after is a hit.
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
static class EnemyDamageHook
{
    static void Prefix(Dam_EnemyDamageBase __instance, out bool __state) => __state = __instance.Owner != null && __instance.Owner.Alive;

    static void Postfix(Dam_EnemyDamageBase __instance, Agent damageSource, bool __state)
    {
        if (__state) SkinnedGear.Damaged(damageSource, !__instance.Owner.Alive);
    }
}

// dump.cs 565311 / 565304 / 565314 / 565317: the item's own fire, reload, equip and aim animation sequences.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TriggerFireAnimationSequence))]
static class FireHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TryTriggerReloadAnimationSequence))]
static class ReloadHook
{
    static void Postfix(ItemEquippable __instance, bool __result)
    {
        if (__result) SkinnedGear.Event(__instance.Pointer, Trigger.ReloadStart);
    }
}

// The completion callback of the reload sequence started above (the point ForgeCore hooks as ReloadDone): it runs
// only when the reload animation finished.
[HarmonyPatch(typeof(ItemEquippable), "_TryTriggerReloadAnimationSequence_b__145_0")]
static class ReloadEndHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.ReloadEnd);
}

// dump.cs 617326 / 617360: the trigger was pulled on an empty clip; m_weapon is the item.
[HarmonyPatch(typeof(BWA_Auto), nameof(BWA_Auto.OnFireShotEmptyClip))]
static class AutoDryFireHook
{
    static void Postfix(BWA_Auto __instance) => SkinnedGear.Event(__instance.m_weapon.Pointer, Trigger.DryFire);
}

[HarmonyPatch(typeof(BWA_Semi), nameof(BWA_Semi.OnFireShotEmptyClip))]
static class SemiDryFireHook
{
    static void Postfix(BWA_Semi __instance) => SkinnedGear.Event(__instance.m_weapon.Pointer, Trigger.DryFire);
}

// dump.cs 565473 and the overrides of the item kinds the catalog has: the item is put away. An override that also runs
// the base is one event (deduplicated per frame).
[HarmonyPatch]
static class UnwieldHook
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(ItemEquippable), typeof(BulletWeapon), typeof(MeleeWeaponFirstPerson), typeof(EnemyScanner), typeof(GlueGun),
                typeof(MineDeployerFirstPerson), typeof(SentryGunFirstPerson) }
            .Select(t => (MethodBase?)AccessTools.DeclaredMethod(t, nameof(ItemEquippable.OnUnWield))).Where(m => m != null)!;

    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Unequip);
}

// dump.cs 565476: the item's attached flashlight is switched on or off; only a change is an event.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.SetAttachedFlashlightEnabled))]
static class FlashlightHook
{
    static void Postfix(ItemEquippable __instance, bool mode) => SkinnedGear.Flashlight(__instance.Pointer, mode);
}

// Fire for gear without a gun's fire animation: a melee attack (dump.cs 615568 class), a C-foam burst (571756), a mine
// placed (568870 class) and a sentry put down (570177 class).
[HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoTriggerAttack))]
static class MeleeAttackHook
{
    static void Postfix(MeleeWeaponFirstPerson __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.FireBurst))]
static class GlueFireHook
{
    static void Postfix(GlueGun __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

[HarmonyPatch(typeof(MineDeployerFirstPerson), nameof(MineDeployerFirstPerson.PlaceMine))]
static class MinePlacedHook
{
    static void Postfix(MineDeployerFirstPerson __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.PlaceOnGround))]
static class SentryPlacedHook
{
    static void Postfix(SentryGunFirstPerson __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

// Fire for items with their own prefabs, the moment each is used: a resource pack applied (dump.cs 615108 class), a
// throwable thrown (561007), a lock melter used (560783), a syringe applied (560904; the call starts the injection)
// and a carry item inserted, e.g. a power cell into a generator (613847). Consumable mines are
// placed through MineDeployerFirstPerson.PlaceMine above.
[HarmonyPatch]
static class ItemUsedHook
{
    static IEnumerable<MethodBase> TargetMethods() => new MethodBase?[]
    {
        AccessTools.DeclaredMethod(typeof(ResourcePackFirstPerson), nameof(ResourcePackFirstPerson.ApplyPack)),
        AccessTools.DeclaredMethod(typeof(ThrowingWeapon), nameof(ThrowingWeapon.Throw)),
        AccessTools.DeclaredMethod(typeof(LockMelterFirstPerson), nameof(LockMelterFirstPerson.UseLockMelter)),
        AccessTools.DeclaredMethod(typeof(SyringeFirstPerson), nameof(SyringeFirstPerson.ApplySyringe)),
        AccessTools.DeclaredMethod(typeof(CarryItemEquippableFirstPerson), nameof(CarryItemEquippableFirstPerson.InsertItem)),
    }.Where(m => m != null)!;

    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TriggerEquipAnimationSequence))]
static class EquipHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Equip);
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TriggerAimAnimationSequence))]
static class AimHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Aim);
}

// dump.cs 565443 (ItemEquippable) and 617542 (BulletWeaponSynced override): the item has finished building its gear and
// knows its GearPartHolder. Covers items whose holder is not under them when the parts spawn.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnGearSpawnComplete))]
static class GearSpawnCompleteHook
{
    static void Prefix(ItemEquippable __instance) => SkinnedGear.BeforeItemSetup(__instance);

    static void Postfix(ItemEquippable __instance) => SkinnedGear.ItemReady(__instance);
}

// Overrides of the step for synced guns, melee weapons and tools (dump.cs 617542, 615703, 568870, 570177, 616222);
// readback and tracking skip a gear they already handled when an override also runs the base.
[HarmonyPatch]
static class OverriddenGearSpawnCompleteHook
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(BulletWeaponSynced), typeof(MeleeWeaponFirstPerson), typeof(MineDeployerFirstPerson), typeof(SentryGunFirstPerson), typeof(EnemyScanner) }
            .Select(t => (MethodBase?)AccessTools.DeclaredMethod(t, nameof(ItemEquippable.OnGearSpawnComplete))).Where(m => m != null)!;

    static void Prefix(ItemEquippable __instance) => SkinnedGear.BeforeItemSetup(__instance);

    static void Postfix(ItemEquippable __instance) => SkinnedGear.ItemReady(__instance);
}

// dump.cs 565452: the item puts its renderers on its view layer (first or third person).
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.SetItemLayer))]
static class ItemLayerHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.LayerSet(__instance.Pointer);
}

// dump.cs 658791: the game switches a held object's materials to (or from) the first-person projection.
[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.SetFPSRendering))]
static class FpsRenderingHook
{
    static void Postfix(GameObject go, bool enable) => SkinnedGear.FpsRenderingSet(go, enable);
}
