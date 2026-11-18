using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;
using Player;

namespace ForgeCore;

/// <summary>One call of the static <c>BulletWeapon.BulletHit</c>, the hit entry every weapon family and the sentry's firing reach. In
/// <c>Before</c> a subscriber may rewrite the hit data's fields (the data is the caller's: restore what you change in <c>After</c>) or
/// <see cref="Skip"/> the body with the answer it should return (a miss: the game's own miss branch runs). The instance is pooled and
/// valid only for the call: a subscriber must not keep it.</summary>
public sealed class BulletHitCall
{
    public Weapon.WeaponHitData Data { get; internal set; } = null!;
    public bool DoDamage { get; internal set; }
    public float AdditionalDistance { get; internal set; }
    public uint DamageSearchId { get; internal set; }
    public bool AllowDirectionalBonus { get; internal set; }

    /// <summary>True once a subscriber skipped the body.</summary>
    public bool Skipped { get; private set; }

    /// <summary>The answer when skipped; after the body ran, what it returned.</summary>
    public bool Result { get; internal set; }

    /// <summary>The body does not run and the call answers <paramref name="result"/>.</summary>
    public void Skip(bool result = false) { Skipped = true; Result = result; }

    internal void Reset() { Skipped = false; Result = false; Data = null!; }
}

/// <summary>One call of <c>PlayerBackpack.SetDeployed</c> (the slot's deployed marker). A subscriber of <c>Before</c> may <see cref="Skip"/>
/// the write.</summary>
public sealed class BackpackDeploy
{
    public BackpackDeploy(PlayerBackpack backpack, InventorySlot slot, bool mode) { Backpack = backpack; Slot = slot; Mode = mode; }
    public PlayerBackpack Backpack { get; }
    public InventorySlot Slot { get; }
    public bool Mode { get; }
    public bool Skipped { get; private set; }
    public void Skip() => Skipped = true;
}

public static partial class GameHooks
{
    // ---- Weapons and gear: the hit entry, wield and unwield, the empty-clip paths, gear assembly, the reload start, the deployed marker, the sentry spawn, the item unwield, the attached light, the C-foam burst and the throw.
    public static readonly Bracket<BulletHitCall> BulletHit = new("GameHooks.BulletHit", typeof(BulletHitHook));
    public static readonly Bracket<BulletWeapon> BulletWeaponWielded = new("GameHooks.BulletWeaponWielded", typeof(BulletWeaponWieldedHook));
    public static readonly Bracket<BulletWeapon> BulletWeaponUnwielded = new("GameHooks.BulletWeaponUnwielded", typeof(BulletWeaponUnwieldedHook));
    /// <summary><c>BulletWeapon.SetMagazineVisibile</c>: the game's reload shows or hides the gun's magazine.</summary>
    public static readonly Bracket<(BulletWeapon Weapon, bool Visible)> MagazineVisibility = new("GameHooks.MagazineVisibility", typeof(MagazineVisibilityHook));
    public static readonly Bracket<BWA_Auto> AutoDryFired = new("GameHooks.AutoDryFired", typeof(AutoDryFiredHook));
    public static readonly Bracket<BWA_Semi> SemiDryFired = new("GameHooks.SemiDryFired", typeof(SemiDryFiredHook));
    public static readonly Bracket<GearPartHolder> GearPartsSpawned = new("GameHooks.GearPartsSpawned", typeof(GearPartsSpawnedHook));
    public static readonly Bracket<ItemEquippable> GearSpawnCompleted = new("GameHooks.GearSpawnCompleted", typeof(GearSpawnCompletedHook));
    public static readonly Bracket<ItemEquippable> ReloadTriggered = new("GameHooks.ReloadTriggered", typeof(ReloadTriggeredHook));
    /// <summary>The reload animation's completion callback, which clears the item's <c>IsReloading</c> flag: the compiler closure of
    /// <c>TryTriggerReloadAnimationSequence</c>, which the game build's interop names <c>_TryTriggerReloadAnimationSequence_b__145_0</c>.
    /// A build that renames it fails the module's calls explicitly at the first subscription.</summary>
    public static readonly Bracket<ItemEquippable> ReloadDone = new("GameHooks.ReloadDone", typeof(ReloadDoneHook));
    public static readonly Bracket<BackpackDeploy> BackpackDeployed = new("GameHooks.BackpackDeployed", typeof(BackpackDeployedHook));
    /// <summary><c>ResourcePackFirstPerson.ApplyPack</c>: a resource pack (ammo, health, tool refill, disinfection) takes effect, which its timed hold reaches only when it completes.</summary>
    public static readonly Bracket<ResourcePackFirstPerson> ResourcePackApplied = new("GameHooks.ResourcePackApplied", typeof(ResourcePackAppliedHook));
    public static readonly Bracket<(SentryGunInstance Sentry, pGearSpawnData Data)> SentrySpawned = new("GameHooks.SentrySpawned", typeof(SentrySpawnedHook));
    /// <summary><c>ItemEquippable.OnUnWield</c>, the base unwield body. An override is its own method and reaches it only when it calls the base; the gun's is <see cref="BulletWeaponUnwielded"/>.</summary>
    public static readonly Bracket<ItemEquippable> ItemUnwielded = new("GameHooks.ItemUnwielded", typeof(ItemUnwieldedHook));
    /// <summary><c>BulletWeaponSynced.OnGearSpawnComplete</c>: a gun held by another player or a bot has built its gear (its own override of <see cref="GearSpawnCompleted"/>).</summary>
    public static readonly Bracket<BulletWeaponSynced> SyncedGearSpawnCompleted = new("GameHooks.SyncedGearSpawnCompleted", typeof(SyncedGearSpawnCompletedHook));
    /// <summary><c>ItemEquippable.SetAttachedFlashlightEnabled(bool)</c>: the item's attached light is told on or off, also when it already was.</summary>
    public static readonly Bracket<(ItemEquippable Item, bool Enabled)> FlashlightSet = new("GameHooks.FlashlightSet", typeof(FlashlightSetHook));
    /// <summary><c>GlueGun.FireBurst</c>: the C-foam launcher fires a burst.</summary>
    public static readonly Bracket<GlueGun> GlueBurstFired = new("GameHooks.GlueBurstFired", typeof(GlueBurstFiredHook));
    /// <summary><c>ThrowingWeapon.Throw(float)</c>: a throwable (glow stick, grenade, fog repeller) leaves the hand; a <c>Before</c> runs ahead of the throw sound and the item's replication.</summary>
    public static readonly Bracket<ThrowingWeapon> ThrowableThrown = new("GameHooks.ThrowableThrown", typeof(ThrowableThrownHook));
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.BulletHit))]
internal static class BulletHitHook
{
    private static readonly Stack<BulletHitCall> Pool = new();

    private static void Release(BulletHitCall call) { call.Reset(); Pool.Push(call); }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(Weapon.WeaponHitData weaponRayData, bool doDamage, float additionalDis, uint damageSearchID, bool allowDirectionalBonus,
        ref bool __result, out BulletHitCall __state)
    {
        var call = __state = Pool.Count > 0 ? Pool.Pop() : new BulletHitCall();
        call.Data = weaponRayData;
        call.DoDamage = doDamage;
        call.AdditionalDistance = additionalDis;
        call.DamageSearchId = damageSearchID;
        call.AllowDirectionalBonus = allowDirectionalBonus;
        GameHooks.BulletHit.RunBefore(call);
        if (!call.Skipped) return true;
        __result = call.Result;
        return false;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletHitCall __state, bool __result, bool __runOriginal)
    {
        __state.Result = __result;
        GameHooks.BulletHit.RunAfter(__state, __runOriginal);
        Release(__state);
    }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletHitCall __state)
    {
        if (__exception != null) { GameHooks.BulletHit.RunFaulted(__state, __exception); Release(__state); }
        return __exception;
    }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.OnWield))]
internal static class BulletWeaponWieldedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeapon __instance) => GameHooks.BulletWeaponWielded.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeapon __instance, bool __runOriginal) => GameHooks.BulletWeaponWielded.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeapon __instance) { if (__exception != null) GameHooks.BulletWeaponWielded.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.SetMagazineVisibile))]
internal static class MagazineVisibilityHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeapon __instance, bool value) => GameHooks.MagazineVisibility.RunBefore((__instance, value));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeapon __instance, bool value, bool __runOriginal) => GameHooks.MagazineVisibility.RunAfter((__instance, value), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeapon __instance, bool value) { if (__exception != null) GameHooks.MagazineVisibility.RunFaulted((__instance, value), __exception); return __exception; }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.OnUnWield))]
internal static class BulletWeaponUnwieldedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeapon __instance) => GameHooks.BulletWeaponUnwielded.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeapon __instance, bool __runOriginal) => GameHooks.BulletWeaponUnwielded.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeapon __instance) { if (__exception != null) GameHooks.BulletWeaponUnwielded.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BWA_Auto), nameof(BWA_Auto.OnFireShotEmptyClip))]
internal static class AutoDryFiredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BWA_Auto __instance) => GameHooks.AutoDryFired.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BWA_Auto __instance, bool __runOriginal) => GameHooks.AutoDryFired.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BWA_Auto __instance) { if (__exception != null) GameHooks.AutoDryFired.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BWA_Semi), nameof(BWA_Semi.OnFireShotEmptyClip))]
internal static class SemiDryFiredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BWA_Semi __instance) => GameHooks.SemiDryFired.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BWA_Semi __instance, bool __runOriginal) => GameHooks.SemiDryFired.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BWA_Semi __instance) { if (__exception != null) GameHooks.SemiDryFired.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
internal static class GearPartsSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(GearPartHolder __instance) => GameHooks.GearPartsSpawned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(GearPartHolder __instance, bool __runOriginal) => GameHooks.GearPartsSpawned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, GearPartHolder __instance) { if (__exception != null) GameHooks.GearPartsSpawned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnGearSpawnComplete))]
internal static class GearSpawnCompletedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ItemEquippable __instance) => GameHooks.GearSpawnCompleted.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemEquippable __instance, bool __runOriginal) => GameHooks.GearSpawnCompleted.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemEquippable __instance) { if (__exception != null) GameHooks.GearSpawnCompleted.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ResourcePackFirstPerson), nameof(ResourcePackFirstPerson.ApplyPack))]
internal static class ResourcePackAppliedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ResourcePackFirstPerson __instance) => GameHooks.ResourcePackApplied.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ResourcePackFirstPerson __instance, bool __runOriginal) => GameHooks.ResourcePackApplied.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ResourcePackFirstPerson __instance) { if (__exception != null) GameHooks.ResourcePackApplied.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TryTriggerReloadAnimationSequence))]
internal static class ReloadTriggeredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ItemEquippable __instance) => GameHooks.ReloadTriggered.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemEquippable __instance, bool __runOriginal) => GameHooks.ReloadTriggered.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemEquippable __instance) { if (__exception != null) GameHooks.ReloadTriggered.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ItemEquippable), "_TryTriggerReloadAnimationSequence_b__145_0")]
internal static class ReloadDoneHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ItemEquippable __instance) => GameHooks.ReloadDone.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemEquippable __instance, bool __runOriginal) => GameHooks.ReloadDone.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemEquippable __instance) { if (__exception != null) GameHooks.ReloadDone.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.SetDeployed))]
internal static class BackpackDeployedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(PlayerBackpack __instance, InventorySlot slot, bool mode, out BackpackDeploy __state)
    {
        __state = new BackpackDeploy(__instance, slot, mode);
        GameHooks.BackpackDeployed.RunBefore(__state);
        return !__state.Skipped;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BackpackDeploy __state, bool __runOriginal) => GameHooks.BackpackDeployed.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BackpackDeploy __state) { if (__exception != null) GameHooks.BackpackDeployed.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnSpawn))]
internal static class SentrySpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SentryGunInstance __instance, pGearSpawnData spawnData, out (SentryGunInstance, pGearSpawnData) __state)
    { __state = (__instance, spawnData); GameHooks.SentrySpawned.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((SentryGunInstance Sentry, pGearSpawnData Data) __state, bool __runOriginal) => GameHooks.SentrySpawned.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (SentryGunInstance Sentry, pGearSpawnData Data) __state) { if (__exception != null) GameHooks.SentrySpawned.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnUnWield))]
internal static class ItemUnwieldedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ItemEquippable __instance) => GameHooks.ItemUnwielded.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemEquippable __instance, bool __runOriginal) => GameHooks.ItemUnwielded.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemEquippable __instance) { if (__exception != null) GameHooks.ItemUnwielded.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BulletWeaponSynced), nameof(BulletWeaponSynced.OnGearSpawnComplete))]
internal static class SyncedGearSpawnCompletedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeaponSynced __instance) => GameHooks.SyncedGearSpawnCompleted.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeaponSynced __instance, bool __runOriginal) => GameHooks.SyncedGearSpawnCompleted.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeaponSynced __instance) { if (__exception != null) GameHooks.SyncedGearSpawnCompleted.RunFaulted(__instance, __exception); return __exception; }
}

// The one parameter is injected by position: neither truth source names it.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.SetAttachedFlashlightEnabled))]
internal static class FlashlightSetHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ItemEquippable __instance, bool __0) => GameHooks.FlashlightSet.RunBefore((__instance, __0));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemEquippable __instance, bool __0, bool __runOriginal) => GameHooks.FlashlightSet.RunAfter((__instance, __0), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemEquippable __instance, bool __0) { if (__exception != null) GameHooks.FlashlightSet.RunFaulted((__instance, __0), __exception); return __exception; }
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.FireBurst))]
internal static class GlueBurstFiredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(GlueGun __instance) => GameHooks.GlueBurstFired.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(GlueGun __instance, bool __runOriginal) => GameHooks.GlueBurstFired.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, GlueGun __instance) { if (__exception != null) GameHooks.GlueBurstFired.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ThrowingWeapon), nameof(ThrowingWeapon.Throw), new[] { typeof(float) })]
internal static class ThrowableThrownHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ThrowingWeapon __instance) => GameHooks.ThrowableThrown.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ThrowingWeapon __instance, bool __runOriginal) => GameHooks.ThrowableThrown.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ThrowingWeapon __instance) { if (__exception != null) GameHooks.ThrowableThrown.RunFaulted(__instance, __exception); return __exception; }
}
