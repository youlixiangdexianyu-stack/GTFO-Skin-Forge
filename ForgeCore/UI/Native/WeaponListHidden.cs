using System;
using System.Collections.Generic;
using HarmonyLib;
using Player;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>Keeps the item with game item id <paramref name="itemId"/> (its <c>ItemDataBlock</c> id) out of the game's weapon list: while
    /// the local player's backpack holds it, the game's list, <see cref="Inventory"/> and every view of it (a <see cref="HudLoadout"/>) stay
    /// exactly as they were before it came in: no row, no name or icon, no count, and the item held before it still shows as held. For a
    /// tool a mod puts in a slot for a moment (a thrown marker), not for an item the player owns. Call it once, at load.</summary>
    public static void HideInWeaponList(uint itemId) => WeaponListHidden.Add(itemId);
}

/// <summary>
/// The game's weapon list held still while the local backpack holds an item a mod hid (<see cref="UIKit.HideInWeaponList"/>): the game's
/// own refresh of the whole list is skipped, and so is every per-item update of the hidden item (its name, icon, state and counts). Every
/// other slot's counts still update. Once the item leaves the backpack the game's next refresh redraws the list as usual. Only the local
/// player's list is held; the lobby's copies of other players' lists are untouched.
/// </summary>
internal static class WeaponListHidden
{
    private static readonly HashSet<uint> Ids = new();

    internal static void Add(uint id)
    {
        PatchGroups.Ensure(PatchGroup.WeaponListHidden);
        Ids.Add(id);
    }

    private static bool Own(PUI_Inventory inventory)
    {
        if (Ids.Count == 0) return false;
        var own = GuiManager.PlayerLayer?.Inventory;
        return own != null && own.Pointer == inventory.Pointer;
    }

    private static bool Hidden(BackpackItem? item) => item != null && Ids.Contains(item.ItemID);

    /// <summary>The hidden item in the local backpack's <paramref name="slot"/>.</summary>
    private static bool HiddenIn(InventorySlot slot) =>
        ForgeCore.Players.Local != null && PlayerBackpackManager.LocalBackpack is { } backpack && backpack.TryGetBackpackItem(slot, out var item) && Hidden(item);

    /// <summary>Any slot of the list holds a hidden item.</summary>
    private static bool Holding(PUI_Inventory inventory)
    {
        var order = inventory.m_slotGUIOrder;
        if (order == null) return false;
        for (var i = 0; i < order.Length; i++)
            if (HiddenIn(order[i])) return true;
        return false;
    }

    [PatchGroup(PatchGroup.WeaponListHidden)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAllSlots))]
    private static class AllSlots
    {
        private static bool Prefix(PUI_Inventory __instance)
        {
            try
            {
                if (!Own(__instance)) return true;
                return !Holding(__instance);
            }
            catch (Exception error)
            {
                Plugin.Error("ui-holding-weapon-list-hidden-item-failed", "Holding the weapon list for a hidden item failed: " + error);
                return true;
            }
        }
    }

    // PUI_Inventory.UpdateItemUI needs no patch: its native body only calls UpdateInfoForItem and UpdateAmmoForItem for the same
    // item, and both are held below.

    [PatchGroup(PatchGroup.WeaponListHidden)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateInfoForItem))]
    private static class ItemInfo
    {
        private static bool Prefix(PUI_Inventory __instance, BackpackItem bpItem) => !(Own(__instance) && Hidden(bpItem));
    }

    [PatchGroup(PatchGroup.WeaponListHidden)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAmmoForItem))]
    private static class ItemAmmo
    {
        private static bool Prefix(PUI_Inventory __instance, BackpackItem bpItem) => !(Own(__instance) && Hidden(bpItem));
    }

    [PatchGroup(PatchGroup.WeaponListHidden)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.SetSlotAmmo))]
    private static class SlotAmmo
    {
        private static bool Prefix(PUI_Inventory __instance, InventorySlot slot)
        {
            try { return !(Own(__instance) && HiddenIn(slot)); }
            catch (Exception error)
            {
                Plugin.Error("ui-holding-weapon-lists-counts-hidden-item", "Holding the weapon list's counts for a hidden item failed: " + error);
                return true;
            }
        }
    }
}
