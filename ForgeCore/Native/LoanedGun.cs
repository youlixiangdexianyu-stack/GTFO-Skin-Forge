using System;
using Player;
using BulletWeapon = Gear.BulletWeapon;
using GearIDRange = Gear.GearIDRange;

namespace ForgeCore;

/// <summary>What every mod that lends a gun into the local player's special slot (<see cref="Gear.SpecialSlot"/>) does the same way:
/// read the slot, tell whether the gun in it is assembled, record the stock the own weapon leaves with, and ask the game for a gun
/// (the lent one or the own weapon back) through the one gear entry that brings its assets in first. Which gun, how it looks, what
/// its stock is and when it goes back are the lender's. Main thread only.</summary>
public static partial class LoanedGun
{
    /// <summary>The local player's backpack and the item in its special slot.</summary>
    public static bool TryLocalSpecial(out PlayerBackpack backpack, out BackpackItem? item)
    {
        backpack = PlayerBackpackManager.LocalBackpack;
        item = null;
        return backpack != null && backpack.TryGetBackpackItem(InventorySlot.GearSpecial, out item) && item != null;
    }

    /// <summary>The slot's gun is assembled: loaded, a live weapon with its archetype, and its gear known. The game builds a gun's parts
    /// over several frames after it is asked for, so nothing is written into it before this holds.</summary>
    public static bool Settled(BackpackItem item, out BulletWeapon? weapon)
    {
        weapon = item.Instance?.TryCast<BulletWeapon>();
        return item.IsLoaded && weapon != null && !weapon.WasCollected && weapon.ArchetypeData != null && item.GearIDRange != null;
    }

    /// <summary>The own weapon is about to leave the slot (the caller already <see cref="SpecialSlot.Remember"/>ed it): its live magazine
    /// and reserve are kept in the shared slot.</summary>
    public static void OwnLeaves()
    {
        if (!TryLocalSpecial(out var backpack, out _)) throw new InvalidOperationException("The special slot is not readable.");
        var storage = backpack.AmmoStorage;
        Gear.SpecialSlot.OwnLeaves((int)storage.GetClipAmmoFromSlot(InventorySlot.GearSpecial), storage.SpecialAmmo.AmmoInPack);
    }

    /// <summary>Throws unless the assets the game reads while it assembles <paramref name="range"/> are ready (nothing changes yet).</summary>
    public static void Require(GearIDRange range)
    {
        var reading = Gear.Prepare(range);
        if (reading.Status != GearAssetStatus.Ready)
            throw new InvalidOperationException("Native weapon effects are not ready (" + reading.Status + "): " + reading.Detail);
    }

    /// <summary>Asks the game to put <paramref name="range"/> into the special slot; throws, with the slot untouched, unless its assets are ready.</summary>
    public static void Equip(GearIDRange range)
    {
        BeginSwap();
        var reading = Gear.EquipLocal(range);
        if (reading.Status != GearAssetStatus.Ready)
        {
            EndSwap();
            throw new InvalidOperationException("Native weapon effects are not ready (" + reading.Status + "): " + reading.Detail);
        }
    }

    /// <summary>Asks the game for the own weapon the shared slot keeps; false when none is kept.</summary>
    public static bool EquipOwn()
    {
        var own = Gear.SpecialSlot.Own;
        if (own == null) return false;
        Equip(new GearIDRange(own.GearJson));
        return true;
    }
}
