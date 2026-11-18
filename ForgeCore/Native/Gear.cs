using System;
using AIGraph;
using Player;
using UnityEngine;
using BulletWeapon = Gear.BulletWeapon;
using GearIDRange = Gear.GearIDRange;

namespace ForgeCore;

/// <summary>Changing the local player's gear the way the game does it, with the assets the game reads while it assembles a gun
/// brought in first (a gun whose muzzle flash shard a level did not load would otherwise throw inside the game's assembly and
/// leave a half-built gun in the slot), writing ammunition back with the game's own updates, and the one arbiter of the local
/// player's special weapon slot for every mod that lends a gun into it (<see cref="SpecialSlot"/>).</summary>
public static partial class Gear
{
    private static bool _slotPatched;

    /// <summary>The local player's special weapon slot, shared by every mod that lends a gun into it: who holds it (stacked
    /// leases), the player's own special weapon while another gun stands in it, and where special ammunition pickups go. The first
    /// use puts on the pickup redirect; checkpoints and the level end are kept and cleared by ForgeCore. Main thread only.</summary>
    public static SpecialSlot SpecialSlot
    {
        get
        {
            if (!_slotPatched)
            {
                Patches.Ensure(Modules.SpecialSlot, typeof(SpecialSlotHooks));
                _slotPatched = true;
            }
            return SpecialSlot.Local;
        }
    }

    /// <summary>The archetype block ID of a gun: the one its category (<c>GearCategoryDataBlock</c>) names for the gear's fire mode
    /// component (semi, burst, auto or semi-burst); 0 when the category is missing or the fire mode is none of those.</summary>
    public static uint ArchetypeId(GearIDRange gear)
    {
        var category = GameData.GearCategoryDataBlock.GetBlock(gear.GetCompID(global::Gear.eGearComponent.Category));
        if (category == null) return 0u;
        return (eWeaponFireMode)gear.GetCompID(global::Gear.eGearComponent.FireMode) switch
        {
            eWeaponFireMode.Semi => category.SemiArchetype,
            eWeaponFireMode.Burst => category.BurstArchetype,
            eWeaponFireMode.Auto => category.AutoArchetype,
            eWeaponFireMode.SemiBurst => category.SemiBurstArchetype,
            _ => 0u,
        };
    }

    /// <summary>Brings in what the game reads while it assembles <paramref name="gear"/>; call again until it is not Loading.</summary>
    public static GearAssetReading Prepare(GearIDRange gear) => GearEffectAssets.Prepare(gear);

    /// <summary>What a sentry gun's firing setup reads (it is never loaded here: missing means Failed).</summary>
    public static GearAssetReading PrepareSentry() => GearEffectAssets.PrepareSentry();

    /// <summary>Whether the class-ammunition pool a sentry is filled from can be read: the game spends the owner's class pool
    /// (<c>ItemEquippable.GetClassAmmoMaxCap</c> on the item in the owner's class slot) as a sentry's ammunition. False while the
    /// backpack has not built that item yet (ask again later); a built item that answers no pool is not ready either.</summary>
    public static bool SentryAmmoReady(PlayerAgent owner) => TryClassPool(owner, out _);

    /// <summary>Spawns a sentry through the one entry a deployed sentry is spawned through, <c>ItemReplicationManager.SpawnGear</c>,
    /// in <c>ItemMode.Instance</c>, owned by <paramref name="owner"/> (the machine placing it; the spawn is replicated, so only the
    /// host calls this). <paramref name="initialAmmo"/> and <paramref name="ammoCap"/> are fractions of the owner's class pool, which is
    /// the unit the sentry spends; returns the absolute ammunition passed to the game. Call only after <see cref="SentryAmmoReady"/>.</summary>
    public static (float Ammo, float Cap) SpawnSentry(string gearJson, ItemReplicationManager.delItemCallback onSpawned, Vector3 position,
        Quaternion rotation, AIG_CourseNode? node, PlayerAgent owner, float initialAmmo, float ammoCap)
        => SpawnSentry(new GearIDRange(gearJson), onSpawned, position, rotation, node, owner, initialAmmo, ammoCap);

    /// <summary><see cref="SpawnSentry(string, ItemReplicationManager.delItemCallback, Vector3, Quaternion, AIG_CourseNode?, PlayerAgent, float, float)"/>
    /// for a gear the caller has already built from its JSON, for one that carries something of its own on it (its <c>PublicGearName</c>
    /// marker, which the spawn replicates to every peer).</summary>
    public static (float Ammo, float Cap) SpawnSentry(GearIDRange gear, ItemReplicationManager.delItemCallback onSpawned, Vector3 position,
        Quaternion rotation, AIG_CourseNode? node, PlayerAgent owner, float initialAmmo, float ammoCap)
    {
        if (!TryClassPool(owner, out var pool)) throw new InvalidOperationException("The class-ammunition pool is not readable yet; ask SentryAmmoReady first.");
        ItemReplicationManager.SpawnGear(gear, onSpawned, ItemMode.Instance, position, rotation, node, owner, initialAmmo * pool, ammoCap * pool);
        return (initialAmmo * pool, ammoCap * pool);
    }

    /// <summary><see cref="SpawnSentry(GearIDRange, ItemReplicationManager.delItemCallback, Vector3, Quaternion, AIG_CourseNode?, PlayerAgent, float, float)"/>
    /// for a gear built from its JSON that carries <paramref name="publicGearName"/> as its marker, which the spawn replicates to
    /// every peer and <see cref="KeepToolSlot"/> reads back.</summary>
    public static (float Ammo, float Cap) SpawnSentry(string gearJson, string publicGearName, ItemReplicationManager.delItemCallback onSpawned,
        Vector3 position, Quaternion rotation, AIG_CourseNode? node, PlayerAgent owner, float initialAmmo, float ammoCap)
        => SpawnSentry(new GearIDRange(gearJson) { PublicGearName = publicGearName }, onSpawned, position, rotation, node, owner, initialAmmo, ammoCap);

    private static bool TryClassPool(PlayerAgent owner, out float pool)
    {
        pool = 0f;
        if (owner.Owner == null || !PlayerBackpackManager.TryGetBackpack(owner.Owner, out var backpack) || backpack?.Slots == null) return false;
        var slot = (int)InventorySlot.GearClass;
        var item = slot < backpack.Slots.Length ? backpack.Slots[slot]?.Instance?.TryCast<ItemEquippable>() : null;
        if (item == null || item.Pointer == IntPtr.Zero) return false;
        pool = item.GetClassAmmoMaxCap(owner.Owner);
        return pool > 0f;
    }

    /// <summary>Asks the game to put <paramref name="gear"/> into its slot of the local player's inventory, only once
    /// <see cref="Prepare"/> answers Ready; otherwise nothing changes and the reading says why (Loading: ask again later).</summary>
    public static GearAssetReading EquipLocal(GearIDRange gear)
    {
        var reading = GearEffectAssets.Prepare(gear);
        if (reading.Status == GearAssetStatus.Ready) PlayerBackpackManager.EquipLocalGear(gear);
        return reading;
    }

    /// <summary>Sets what a round of the local player's <paramref name="type"/> costs and the size of its magazine, through the game's own
    /// entry: the pool takes both from the game's gun when the item is made, so a gun lent with other numbers charges the game's price until
    /// this is called. The game sets them again for the next gun in the slot. The reserve itself is <see cref="WriteLocalAmmo"/>'s.</summary>
    public static void SetLocalAmmoType(AmmoType type, float costOfBullet, int magazine)
    {
        var storage = PlayerBackpackManager.LocalBackpack?.AmmoStorage ?? throw new InvalidOperationException("The local player has no ammunition storage.");
        storage.SetupAmmoType(type, costOfBullet, magazine);
    }

    /// <summary><see cref="EquipLocal"/> for a bot, through the game's own bot equip. Host only.</summary>
    public static GearAssetReading EquipBot(SNetwork.SNet_Player bot, GearIDRange gear)
    {
        var reading = GearEffectAssets.Prepare(gear);
        if (reading.Status == GearAssetStatus.Ready) PlayerBackpackManager.EquipBotGear(bot, gear);
        return reading;
    }

    /// <summary>Writes the local player's reserve of <paramref name="type"/> and <paramref name="weapon"/>'s magazine, refreshes the
    /// ammunition display and sends the storage to the other players, as the game does after a pickup. Returns whether both read
    /// back as written.</summary>
    public static bool WriteLocalAmmo(AmmoType type, BulletWeapon weapon, float reserve, int magazine)
    {
        if (weapon == null) throw new ArgumentNullException(nameof(weapon));
        var storage = PlayerBackpackManager.LocalBackpack?.AmmoStorage ?? throw new InvalidOperationException("The local player has no ammunition storage.");
        var pool = type switch
        {
            AmmoType.Standard => storage.StandardAmmo,
            AmmoType.Special => storage.SpecialAmmo,
            AmmoType.Class => storage.ClassAmmo,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only standard, special and class ammunition have a reserve."),
        };
        pool.AmmoInPack = reserve;
        weapon.SetCurrentClip(magazine);
        storage.UpdateAllAmmoUI();
        PlayerBackpackManager.ForceLocalAmmoStorageUpdate();
        return pool.AmmoInPack == reserve && weapon.GetCurrentClip() == magazine;
    }
}
