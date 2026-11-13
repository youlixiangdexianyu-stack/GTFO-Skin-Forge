using System.Reflection;
using System.Runtime.InteropServices;
using ForgeCore;
using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using ForgeSkin.Format;
using UnityEngine;

namespace ForgeSkin.Plugin;

// IL2CPP folds identical native bodies (every empty virtual of the same shape, for one) into one function, so a hooked
// method can also run for unrelated objects of other classes, even during plugin load. Handlers that read anything from
// their arguments first check the object's real class (ForgeCore NativeObjects.Is); the others only compare pointers with
// gear they track.
//
// A TargetMethods list leaves out a target ForgeCore's patcher would refuse (Hooks.Unsupported: an empty override compiled
// to a shared `ret` stub, such as MeleeWeaponFirstPerson.OnGearSpawnComplete, or a prologue it cannot relocate) before the
// class is patched, because a refused target also takes the class's later targets down. Such an item is still handled where
// its parts spawn.
static class Native
{
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

    public static unsafe float Float(Il2CppObjectBase o, IntPtr field) => *(float*)(o.Pointer + (int)IL2CPP.il2cpp_field_get_offset(field));
}

// The game methods other packages read as well are ForgeCore's GameHooks; ForgeSkin subscribes to them. Each subscription
// stands alone: one whose method the game no longer has loses that event only, not the plugin.
static class SharedHooks
{
    public static void Subscribe()
    {
        // dump.cs 614836: runs once every part of a gear is spawned and its GearMaterialFeeder is set up.
        On(GameHooks.GearPartsSpawned, () => GameHooks.GearPartsSpawned.After(static (holder, _) => { if (NativeObjects.Is<GearPartHolder>(holder)) SkinnedGear.Spawned(holder); }));
        // dump.cs 544371: the funnel every hit on an enemy passes on this machine (the shooter's own hits are applied locally
        // too). With the local player as source and the enemy alive before (its health, else -1): dead after is a kill, alive
        // after is a hit; the damage is the health it lost (before minus after, never below 0). The source is compared first (two
        // pointers), so the host's hits by everybody else read nothing more.
        On(GameHooks.EnemyDamageReceived, () => GameHooks.EnemyDamageReceived.Around(
            static call => SkinnedGear.IsLocal(call.Source) && NativeObjects.Is<Dam_EnemyDamageBase>(call.Receiver) && call.Receiver.Owner is { } owner && owner.Alive ? call.Receiver.Health : -1f,
            static (call, before, _) =>
            {
                if (before >= 0) SkinnedGear.Damaged(!call.Receiver.Owner.Alive, Math.Max(0, before - Math.Max(0, call.Receiver.Health)));
            }));
        // dump.cs 565304: a reload starts when the call sets the item's reloading flag (the bracket carries no answer; the flag
        // is what the game's own `true` stands for, and a call while already reloading starts nothing).
        On(GameHooks.ReloadTriggered, () => GameHooks.ReloadTriggered.Around(static item => item.IsReloading,
            static (item, was, ran) => { if (ran && !was && item.IsReloading) SkinnedGear.Event(item.Pointer, Trigger.ReloadStart); }));
        // The completion callback of that reload sequence: it runs only when the reload animation finished.
        On(GameHooks.ReloadDone, () => GameHooks.ReloadDone.After(static (item, _) => SkinnedGear.Event(item.Pointer, Trigger.ReloadEnd)));
        // dump.cs 617326 / 617360: the trigger was pulled on an empty clip; m_weapon is the item. The semi body is one folded
        // native body with BWA_Burst's, so it also runs for burst guns (as ForgeEngine reads it).
        On(GameHooks.AutoDryFired, () => GameHooks.AutoDryFired.After(static (action, _) =>
        {
            if (NativeObjects.Is<BWA_Auto>(action)) SkinnedGear.Event(action.m_weapon?.Pointer ?? IntPtr.Zero, Trigger.DryFire);
        }));
        On(GameHooks.SemiDryFired, () => GameHooks.SemiDryFired.After(static (action, _) =>
        {
            if (NativeObjects.Is<BWA_Semi>(action) || NativeObjects.Is<BWA_Burst>(action)) SkinnedGear.Event(action.m_weapon?.Pointer ?? IntPtr.Zero, Trigger.DryFire);
        }));
        // dump.cs 565473: the item is put away (the base body and the gun's override; the other kinds' overrides are UnwieldHook).
        // An override that also runs the base is one event (deduplicated per frame).
        On(GameHooks.ItemUnwielded, () => GameHooks.ItemUnwielded.After(static (item, _) => SkinnedGear.Event(item.Pointer, Trigger.Unequip)));
        On(GameHooks.BulletWeaponUnwielded, () => GameHooks.BulletWeaponUnwielded.After(static (item, _) => SkinnedGear.Event(item.Pointer, Trigger.Unequip)));
        // dump.cs 565476: the item's attached flashlight is switched on or off; only a change is an event.
        On(GameHooks.FlashlightSet, () => GameHooks.FlashlightSet.After(static (call, _) => SkinnedGear.Flashlight(call.Item.Pointer, call.Enabled)));
        // Fire for gear without a gun's fire animation: a C-foam burst (dump.cs 571756); for items with their own prefabs, a
        // throwable thrown (561007) and a resource pack applied (615108 class).
        On(GameHooks.GlueBurstFired, () => GameHooks.GlueBurstFired.After(static (glue, _) => SkinnedGear.Event(glue.Pointer, Trigger.Fire)));
        On(GameHooks.ThrowableThrown, () => GameHooks.ThrowableThrown.After(static (thrown, _) => SkinnedGear.Event(thrown.Pointer, Trigger.Fire)));
        On(GameHooks.ResourcePackApplied, () => GameHooks.ResourcePackApplied.After(static (pack, _) => SkinnedGear.Event(pack.Pointer, Trigger.Fire)));
        // dump.cs 565443 (ItemEquippable) and 617542 (BulletWeaponSynced override): the item has finished building its gear and
        // knows its GearPartHolder. Covers items whose holder is not under them when the parts spawn. The other kinds'
        // overrides are OverriddenGearSpawnCompleteHook.
        On(GameHooks.GearSpawnCompleted, () => GameHooks.GearSpawnCompleted.Before(BeforeItemSetup));
        On(GameHooks.GearSpawnCompleted, () => GameHooks.GearSpawnCompleted.After(static (item, _) => ItemReady(item)));
        On(GameHooks.SyncedGearSpawnCompleted, () => GameHooks.SyncedGearSpawnCompleted.Before(BeforeItemSetup));
        On(GameHooks.SyncedGearSpawnCompleted, () => GameHooks.SyncedGearSpawnCompleted.After(static (item, _) => ItemReady(item)));
        // The local player goes down (ForgeCore's own event) and their revive completes (GameHooks.PlayerRevived, filtered
        // to them): player states, not gear states, so the skin of the item in their hands hears them.
        GameEvents.LocalPlayerDown += static () => SkinnedGear.PlayerState(Trigger.Downed);
        On(GameHooks.PlayerRevived, () => GameHooks.PlayerRevived.After(static (state, ran) =>
        {
            if (ran && SkinnedGear.IsLocal(state.m_owner)) SkinnedGear.PlayerState(Trigger.Revived);
        }));
        // The game state changes (a level starts generating, the level is entered).
        GameEvents.StateChanged += static () => SkinnedGear.StateChanged(GameStateManager.CurrentStateName);
    }

    public static void BeforeItemSetup(ItemEquippable item) { if (NativeObjects.Is<ItemEquippable>(item)) SkinnedGear.BeforeItemSetup(item); }

    public static void ItemReady(ItemEquippable item) { if (NativeObjects.Is<ItemEquippable>(item)) SkinnedGear.ItemReady(item); }

    static void On<TCall>(Bracket<TCall> hook, Func<IDisposable> subscribe)
    {
        try { subscribe(); }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-hook-unavailable", $"hook {hook.Module} not installed", e); }
    }
}

// dump.cs 565311 / 565314 / 565317: the item's own fire, equip and aim animation sequences.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TriggerFireAnimationSequence))]
static class FireHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
}

// dump.cs 565473: the item kinds' own unwield overrides, which only ForgeSkin reads.
[HarmonyPatch]
static class UnwieldHook
{
    static IEnumerable<MethodBase> TargetMethods() => new MethodBase?[]
    {
        AccessTools.DeclaredMethod(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.OnUnWield)),
        AccessTools.DeclaredMethod(typeof(EnemyScanner), nameof(EnemyScanner.OnUnWield)),
        AccessTools.DeclaredMethod(typeof(GlueGun), nameof(GlueGun.OnUnWield)),
        AccessTools.DeclaredMethod(typeof(MineDeployerFirstPerson), nameof(MineDeployerFirstPerson.OnUnWield)),
        AccessTools.DeclaredMethod(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.OnUnWield)),
    }.Where(m => m != null && ForgeCore.Hooks.Unsupported(m) == null)!;

    static void Postfix(ItemEquippable __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Unequip);
}

// Fire for gear without a gun's fire animation: a melee attack (dump.cs 615568 class), a mine placed (568870 class) and a
// sentry put down (570177 class).
[HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoTriggerAttack))]
static class MeleeAttackHook
{
    static void Postfix(MeleeWeaponFirstPerson __instance) => SkinnedGear.Event(__instance.Pointer, Trigger.Fire);
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

// Fire for items with their own prefabs, the moment each is used (the pack and the throwable are SharedHooks): a lock
// melter used (dump.cs 560783), a syringe applied (560904; the call starts the injection) and a carry item inserted,
// e.g. a power cell into a generator (613847). Consumable mines are placed through MineDeployerFirstPerson.PlaceMine above.
[HarmonyPatch]
static class ItemUsedHook
{
    static IEnumerable<MethodBase> TargetMethods() => new MethodBase?[]
    {
        AccessTools.DeclaredMethod(typeof(LockMelterFirstPerson), nameof(LockMelterFirstPerson.UseLockMelter)),
        AccessTools.DeclaredMethod(typeof(SyringeFirstPerson), nameof(SyringeFirstPerson.ApplySyringe)),
        AccessTools.DeclaredMethod(typeof(CarryItemEquippableFirstPerson), nameof(CarryItemEquippableFirstPerson.InsertItem)),
    }.Where(m => m != null && ForgeCore.Hooks.Unsupported(m) == null)!;

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

// Overrides of the step for melee weapons and tools (dump.cs 615703, 568870, 570177, 616222; the base and the synced gun
// are SharedHooks); readback and tracking skip a gear they already handled when an override also runs the base.
[HarmonyPatch]
static class OverriddenGearSpawnCompleteHook
{
    static IEnumerable<MethodBase> TargetMethods() => new MethodBase?[]
    {
        AccessTools.DeclaredMethod(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.OnGearSpawnComplete)),
        AccessTools.DeclaredMethod(typeof(MineDeployerFirstPerson), nameof(MineDeployerFirstPerson.OnGearSpawnComplete)),
        AccessTools.DeclaredMethod(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.OnGearSpawnComplete)),
        AccessTools.DeclaredMethod(typeof(EnemyScanner), nameof(EnemyScanner.OnGearSpawnComplete)),
    }.Where(m => m != null && ForgeCore.Hooks.Unsupported(m) == null)!;

    static void Prefix(ItemEquippable __instance) => SharedHooks.BeforeItemSetup(__instance);

    static void Postfix(ItemEquippable __instance) => SharedHooks.ItemReady(__instance);
}

// dump.cs 565452: the item puts its renderers on its view layer (first or third person).
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.SetItemLayer))]
static class ItemLayerHook
{
    static void Postfix(ItemEquippable __instance) => SkinnedGear.LayerSet(__instance.Pointer);
}

// dump.cs 658791: the game switches a held object's materials to (or from) the first-person projection. ForgeCore's own
// drawing calls it too; the handler leaves every object it does not track alone.
[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.SetFPSRendering))]
static class FpsRenderingHook
{
    static void Postfix(GameObject go, bool enable) { if (NativeObjects.Is<GameObject>(go)) SkinnedGear.FpsRenderingSet(go, enable); }
}
