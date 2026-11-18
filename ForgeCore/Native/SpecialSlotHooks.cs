using System;
using HarmonyLib;
using Player;

namespace ForgeCore;

/// <summary>The one special ammunition redirect for every lent gun (<see cref="SpecialSlot"/>): while the local player's own special
/// weapon is away, the game's own special pickup runs against its kept pool (the slot's pool is swapped for it around the call and
/// put back after), so the own weapon is refilled by the game's own amounts and cap and a lent gun never is. Other players' storage,
/// primary and tool pickups are the game's own. Put on by <see cref="Gear.SpecialSlot"/> the first time it is used.</summary>
internal static class SpecialSlotHooks
{
    [HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.PickupAmmo))]
    [HarmonyPrefix]
    private static void PickupPrefix(PlayerAmmoStorage __instance, AmmoType ammoType, out float __state)
    {
        __state = float.NaN;
        try
        {
            if (ammoType != AmmoType.Special || !Redirects(__instance)) return;
            var pool = __instance.SpecialAmmo;
            __state = pool.AmmoInPack;
            pool.AmmoInPack = SpecialSlot.Local.Own!.Pool;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-redirecting-special-ammunition-pickup-failed", "Redirecting a special ammunition pickup failed: " + error); }
    }

    [HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.PickupAmmo))]
    [HarmonyPostfix]
    private static void PickupPostfix(PlayerAmmoStorage __instance, float __state)
    {
        if (float.IsNaN(__state)) return;
        try
        {
            var slot = SpecialSlot.Local;
            var pool = __instance.SpecialAmmo;
            slot.Refilled(pool.AmmoInPack);
            pool.AmmoInPack = __state;
            __instance.UpdateSlotAmmoUI(InventorySlot.GearSpecial);
            PlayerBackpackManager.ForceLocalAmmoStorageUpdate();
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-restoring-slots-ammunition-after-redirected-pickup", "Restoring the slot's ammunition after a redirected pickup failed: " + error); }
    }

    /// <summary>An ammunition pack counts the own special weapon as needing rounds while it is away and its kept pool is not full (the
    /// game would only look at the lent gun's pool in the slot).</summary>
    [HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.NeedWeaponAmmo))]
    [HarmonyPostfix]
    private static void NeedPostfix(PlayerAmmoStorage __instance, ref bool __result)
    {
        try
        {
            if (__result || !Redirects(__instance)) return;
            if (SpecialSlot.Local.Own!.Pool < __instance.SpecialAmmo.AmmoMaxCap) __result = true;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-reading-own-special-weapons-need-ammunition", "Reading the own special weapon's need for ammunition failed: " + error); }
    }

    /// <summary>Whether a pickup into <paramref name="storage"/> goes to the own weapon's kept pool: only the local player's storage.</summary>
    private static bool Redirects(PlayerAmmoStorage storage)
    {
        if (!SpecialSlot.Local.RedirectsRefill) return false;
        var local = PlayerBackpackManager.LocalBackpack;
        return local != null && local.AmmoStorage != null && local.AmmoStorage.Pointer == storage.Pointer;
    }

    /// <summary>Subscribed when ForgeCore loads, before any mod that depends on it, so their own checkpoint handlers read the
    /// recalled slot.</summary>
    internal static void CheckpointReached() => SpecialSlot.Local.CaptureCheckpoint();

    internal static void CheckpointReloaded()
    {
        SpecialSlot.Local.RecallCheckpoint();
    }
}
