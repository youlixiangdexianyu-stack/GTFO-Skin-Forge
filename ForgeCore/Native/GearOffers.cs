using System;
using System.Collections.Generic;
using GameData;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using GearIDRange = Gear.GearIDRange;
using GearManager = Gear.GearManager;
using eGearComponent = Gear.eGearComponent;

namespace ForgeCore;

public static partial class Gear
{
    /// <summary>Narrows or reorders what the game offers a slot: the gears the game's own getter returned, in its order. Return null to leave
    /// the offer as it is. Runs on the main thread for every read of the offer (the lobby window and the in-level inventory both read it),
    /// so keep it cheap.</summary>
    public delegate IReadOnlyList<GearIDRange>? OfferProjection(InventorySlot slot, IReadOnlyList<GearIDRange> offered);

    /// <summary>What happens to a gear the local player is about to equip without the picker (a saved favourite, the last equipped gear, a
    /// predefined loadout): return <paramref name="requested"/> to let it through, another gear to equip that instead, or null to refuse.</summary>
    public delegate GearIDRange? LocalAdmission(GearIDRange requested);

    private sealed class OfferOwner
    {
        internal OfferOwner(OfferProjection offers, LocalAdmission? admit) { Offers = offers; Admit = admit; }
        internal readonly OfferProjection Offers;
        internal readonly LocalAdmission? Admit;
    }

    private static readonly List<OfferOwner> OfferOwners = new();

    /// <summary>Limits which gears players may choose, as one rule for every package: <paramref name="keep"/> says whether a gear stays
    /// offered in its slot. Gears it refuses disappear from the picker, and a saved or predefined choice it refuses is replaced by the
    /// slot's first offered gear (the slot's choice is refused when nothing is left; one error line says which slot). Several owners add up:
    /// a gear needs every owner's yes. The local machine only; the owner enforces nothing on other players. Dispose to lift the rule.
    /// The game's offer is replaced for a slot only while a rule drops something there.</summary>
    /// <param name="owner">Names the rule's owner in error lines (a plugin GUID).</param>
    /// <param name="keep">Whether a gear stays offered in its slot; called for every gear of a read of the offer, so keep it cheap.</param>
    public static IDisposable RestrictOffers(string owner, Func<InventorySlot, GearIDRange, bool> keep)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Name the owner (its plugin GUID).", nameof(owner));
        if (keep == null) throw new ArgumentNullException(nameof(keep));
        return ProjectOffers(owner, (slot, offered) =>
        {
            var kept = new List<GearIDRange>(offered.Count);
            foreach (var gear in offered) if (keep(slot, gear)) kept.Add(gear);
            return kept.Count == offered.Count ? null : kept;
        }, requested =>
        {
            var slot = SlotOf(requested);
            if (slot == null || keep(slot.Value, requested)) return requested;
            var offers = GearManager.GetAllGearForSlot(slot.Value);
            if (offers != null && offers.Length > 0) return offers[0];
            Plugin.Logger.ErrorOnce("core-gear-restriction-left-slot-empty-" + slot,
                owner + " allows no gear for the " + slot + " slot, so the choice for it was refused; the slot stays as the game has it.");
            return null;
        });
    }

    /// <summary>The general form of <see cref="RestrictOffers"/>: <paramref name="offers"/> projects the whole offer of a slot and
    /// <paramref name="admit"/> (optional) rules on a gear equipped without the picker. Owners run in registration order, each on what the
    /// one before left. A projection or admission that throws is reported once and treated as no change. Dispose to remove it.</summary>
    /// <param name="owner">Names the owner in error lines (a plugin GUID).</param>
    /// <param name="offers">Projects the whole offer of a slot; null leaves it as it is.</param>
    /// <param name="admit">Rules on a gear equipped without the picker; null leaves such choices alone.</param>
    public static IDisposable ProjectOffers(string owner, OfferProjection offers, LocalAdmission? admit = null)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Name the owner (its plugin GUID).", nameof(owner));
        if (offers == null) throw new ArgumentNullException(nameof(offers));
        Patches.Ensure(Modules.GearOffers, typeof(GearOfferHooks));
        var entry = new OfferOwner(Guard(owner, offers), admit == null ? null : Guard(owner, admit));
        OfferOwners.Add(entry);
        return new OfferRegistration(entry);
    }

    private static OfferProjection Guard(string owner, OfferProjection offers) => (slot, offered) =>
    {
        try { return offers(slot, offered); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("core-gear-offer-projection-" + owner, owner + " failed to project the gear offer; the game's offer stands: " + error);
            return null;
        }
    };

    private static LocalAdmission Guard(string owner, LocalAdmission admit) => requested =>
    {
        try { return admit(requested); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("core-gear-admission-" + owner, owner + " failed to rule on a gear choice; the choice stands: " + error);
            return requested;
        }
    };

    private sealed class OfferRegistration : IDisposable
    {
        private OfferOwner? _entry;
        internal OfferRegistration(OfferOwner entry) { _entry = entry; }
        public void Dispose()
        {
            if (_entry == null) return;
            OfferOwners.Remove(_entry);
            _entry = null;
        }
    }

    /// <summary>The slot the game would put <paramref name="gear"/> in, read from its base item block; null when the packet names none.</summary>
    private static InventorySlot? SlotOf(GearIDRange gear)
        => ItemDataBlock.GetBlock(gear.GetCompID(eGearComponent.BaseItem)) is { } item ? item.inventorySlot : null;

    /// <summary>Runs every projection over the game's offer of <paramref name="slot"/> and returns the array to hand back, or null when
    /// nothing changed (the game's own array stays).</summary>
    internal static Il2CppReferenceArray<GearIDRange>? Project(InventorySlot slot, Il2CppReferenceArray<GearIDRange> offered)
    {
        if (OfferOwners.Count == 0 || offered == null) return null;
        var live = new GearIDRange[offered.Count];
        for (var index = 0; index < live.Length; index++) live[index] = offered[index];
        IReadOnlyList<GearIDRange> current = live;
        var changed = false;
        foreach (var owner in OfferOwners.ToArray())
        {
            var next = owner.Offers(slot, current);
            if (next == null) continue;
            current = next;
            changed = true;
        }
        if (!changed) return null;
        var items = new GearIDRange[current.Count];
        for (var index = 0; index < items.Length; index++) items[index] = current[index];
        return items;
    }

    /// <summary>Runs every admission over a gear about to be equipped; null refuses the equip.</summary>
    internal static GearIDRange? Admit(GearIDRange requested)
    {
        GearIDRange? gear = requested;
        foreach (var owner in OfferOwners.ToArray())
        {
            if (owner.Admit == null || gear == null) continue;
            gear = owner.Admit(gear);
        }
        return gear;
    }
}

/// <summary>The game's slot offer and its local equip entry, patched once for every package that limits what players may choose
/// (<see cref="Gear.RestrictOffers"/>, <see cref="Gear.ProjectOffers"/>). Put on the first time a rule is registered. Both go last, so they
/// see what every other patch left.</summary>
internal static class GearOfferHooks
{
    [HarmonyPatch(typeof(GearManager), nameof(GearManager.GetAllGearForSlot))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Offered(InventorySlot slot, ref Il2CppReferenceArray<GearIDRange> __result)
    {
        try
        {
            if (Gear.Project(slot, __result) is { } projected) __result = projected;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-gear-offer-hook-failed", "Projecting the gear offer failed; the game's offer stands: " + error); }
    }

    [HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.EquipLocalGear))]
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static bool Equipping(ref GearIDRange gearSetup)
    {
        if (gearSetup == null) return true;
        try
        {
            var admitted = Gear.Admit(gearSetup);
            if (admitted == null) return false;
            gearSetup = admitted;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("core-gear-admission-hook-failed", "Ruling on a local gear choice failed; the choice stands: " + error);
            return true;
        }
    }
}
