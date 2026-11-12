using System.Reflection;
using System.Runtime.InteropServices;
using Gear;
using Agents;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using ForgeSkin.Format;
using UnityEngine;

namespace ForgeSkin.Plugin;

// IL2CPP folds identical native bodies (every empty virtual of the same shape, for one) into one function, so a hooked
// method can also run for unrelated objects of other classes, even during plugin load. Handlers that read anything from
// their arguments first check the object's real class; the others only compare pointers with gear they track.
static class Native
{
    public static bool Is<T>(Il2CppObjectBase? o) where T : Il2CppObjectBase =>
        o != null && o.Pointer != IntPtr.Zero &&
        IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<T>.NativeClassPtr, IL2CPP.il2cpp_object_get_class(o.Pointer));

    // An empty override compiles to a shared `ret` stub (MeleeWeaponFirstPerson.OnGearSpawnComplete is one): the detour
    // would catch every method folded into it and corrupt the bytes after it, which hangs the game at startup. Such a
    // target is skipped; its item is still handled where its parts spawn.
    public static bool Hookable(MethodBase? method)
    {
        if (method == null) return false;
        var info = Method(method);
        var entry = info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
        var first = entry == IntPtr.Zero ? (byte)0xC3 : Marshal.ReadByte(entry);
        if (first is not (0xC3 or 0xC2 or 0xE9 or 0xEB or 0xCC)) return true;
        ForgeSkinPlugin.Info($"skipped {method.DeclaringType?.Name}.{method.Name}: no native body of its own to hook");
        return false;
    }

    // The IL2CPP MethodInfo of a generated interop method.
    public static IntPtr Method(MethodBase method) =>
        (IntPtr)Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method)!.GetValue(null)!;

    // Per-frame reads without the managed wrapper a generated getter allocates for the object it returns: the object's
    // pointer from a parameterless (virtual) getter, or from a reference field.
    public static unsafe IntPtr Call(Il2CppObjectBase o, IntPtr method)
    {
        var self = o.Pointer;
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(IL2CPP.il2cpp_object_get_virtual_method(self, method), self, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return result;
    }

    public static IntPtr FieldInfo<T>(string name) where T : Il2CppObjectBase =>
        IL2CPP.GetIl2CppField(Il2CppClassPointerStore<T>.NativeClassPtr, name) is var f && f != IntPtr.Zero ? f
            : throw new MissingFieldException(typeof(T).Name, name);

    public static IntPtr Field(Il2CppObjectBase o, IntPtr field) => Marshal.ReadIntPtr(o.Pointer + (int)IL2CPP.il2cpp_field_get_offset(field));
}

// dump.cs 614836: runs once every part of a gear is spawned and its GearMaterialFeeder is set up.
[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
static class PartsSpawnedHook
{
    static void Postfix(GearPartHolder __instance) { if (Native.Is<GearPartHolder>(__instance)) SkinnedGear.Spawned(__instance); }
}

// dump.cs 544371: the funnel every hit on an enemy passes on this machine (the shooter's own hits are applied locally
// too). With the local player as source and the enemy alive before: dead after is a kill, alive after is a hit.
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
static class EnemyDamageHook
{
    static void Prefix(Dam_EnemyDamageBase __instance, out bool __state) =>
        __state = Native.Is<Dam_EnemyDamageBase>(__instance) && __instance.Owner != null && __instance.Owner.Alive;

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
    static void Postfix(BWA_Auto __instance) { if (Native.Is<BWA_Auto>(__instance)) SkinnedGear.Event(__instance.m_weapon?.Pointer ?? IntPtr.Zero, Trigger.DryFire); }
}

[HarmonyPatch(typeof(BWA_Semi), nameof(BWA_Semi.OnFireShotEmptyClip))]
static class SemiDryFireHook
{
    static void Postfix(BWA_Semi __instance) { if (Native.Is<BWA_Semi>(__instance)) SkinnedGear.Event(__instance.m_weapon?.Pointer ?? IntPtr.Zero, Trigger.DryFire); }
}

// dump.cs 565473 and the overrides of the item kinds the catalog has: the item is put away. An override that also runs
// the base is one event (deduplicated per frame).
[HarmonyPatch]
static class UnwieldHook
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(ItemEquippable), typeof(BulletWeapon), typeof(MeleeWeaponFirstPerson), typeof(EnemyScanner), typeof(GlueGun),
                typeof(MineDeployerFirstPerson), typeof(SentryGunFirstPerson) }
            .Select(t => (MethodBase?)AccessTools.DeclaredMethod(t, nameof(ItemEquippable.OnUnWield))).Where(Native.Hookable)!;

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
    }.Where(Native.Hookable)!;

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
    static void Prefix(ItemEquippable __instance) { if (Native.Is<ItemEquippable>(__instance)) SkinnedGear.BeforeItemSetup(__instance); }

    static void Postfix(ItemEquippable __instance) { if (Native.Is<ItemEquippable>(__instance)) SkinnedGear.ItemReady(__instance); }
}

// Overrides of the step for synced guns, melee weapons and tools (dump.cs 617542, 615703, 568870, 570177, 616222);
// readback and tracking skip a gear they already handled when an override also runs the base.
[HarmonyPatch]
static class OverriddenGearSpawnCompleteHook
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(BulletWeaponSynced), typeof(MeleeWeaponFirstPerson), typeof(MineDeployerFirstPerson), typeof(SentryGunFirstPerson), typeof(EnemyScanner) }
            .Select(t => (MethodBase?)AccessTools.DeclaredMethod(t, nameof(ItemEquippable.OnGearSpawnComplete))).Where(Native.Hookable)!;

    static void Prefix(ItemEquippable __instance) { if (Native.Is<ItemEquippable>(__instance)) SkinnedGear.BeforeItemSetup(__instance); }

    static void Postfix(ItemEquippable __instance) { if (Native.Is<ItemEquippable>(__instance)) SkinnedGear.ItemReady(__instance); }
}

// dump.cs 565452: the item puts its renderers on its view layer (first or third person).
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.SetItemLayer))]
static class ItemLayerHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.LayerSet(__instance.Pointer);
}

// The game state changes (a level starts generating, the level is entered).
[HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.DoChangeState))]
static class GameStateHook
{
    static void Postfix(GameStateManager __instance, eGameStateName nextState) { if (Native.Is<GameStateManager>(__instance)) SkinnedGear.StateChanged(nextState); }
}

// dump.cs 658791: the game switches a held object's materials to (or from) the first-person projection.
[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.SetFPSRendering))]
static class FpsRenderingHook
{
    static void Postfix(GameObject go, bool enable) { if (Native.Is<GameObject>(go)) SkinnedGear.FpsRenderingSet(go, enable); }
}
