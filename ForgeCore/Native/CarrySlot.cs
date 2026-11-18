using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>Moves the heavy object of the game's in-level carry slot between the local player's hands and back. The object stays in the
/// slot, so the game's pick-up state, its checkpoint capture, its drop for a player who leaves and everything the object does while carried
/// are unchanged, and the only thing sent is the game's own synced wield request (<c>PlayerSync.WantsToWieldSlot</c>), so every peer reads
/// the same answer from the slot and the wield. A package that uses this must also lift the game's two blocks for its carrier
/// (<see cref="GameHooks.WieldedSlotBlocked"/> and <see cref="GameHooks.LocalItemHeld"/>). The game only drops a carried object when it is in
/// hand, so a package calls <see cref="DropIfFallen"/> while the object may be on the back. Main thread.</summary>
public static class CarrySlot
{
    private static readonly InventorySlot[] Hands = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    private const float AskEvery = 0.5f;
    private static InventorySlot _hand = InventorySlot.GearStandard;
    private static float _askAgainAt, _dropAgainAt;

    /// <summary>After <see cref="Track"/>: null nothing is carried, false the object is on the back, true it is in the hands.</summary>
    public static bool? InHands { get; private set; }

    /// <summary>The object is still to be put back in the hands (<see cref="GiveBack"/>); <see cref="Settle"/> keeps asking until the game wields it.</summary>
    public static bool Pending { get; private set; }

    /// <summary>Every frame the local player may carry on the back: remembers the weapon in hand and reads where the object is.</summary>
    public static void Track(PlayerAgent agent)
    {
        var inventory = agent.Inventory;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (inventory == null || backpack == null) { InHands = null; return; }
        var wielded = inventory.WieldedSlot;
        if (wielded != InventorySlot.InLevelCarry) _hand = wielded;
        InHands = HasItem(backpack, InventorySlot.InLevelCarry) ? wielded == InventorySlot.InLevelCarry : null;
    }

    /// <summary>Hands to back (the weapon last held comes out), or back to hands. Repeats within half a second are ignored, so two packages
    /// answering one key press make one move.</summary>
    public static void Toggle(PlayerAgent agent, float now)
    {
        var inventory = agent.Inventory;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (inventory == null || backpack == null || now < _askAgainAt || !HasItem(backpack, InventorySlot.InLevelCarry)) return;
        if (inventory.WieldedSlot != InventorySlot.InLevelCarry) { Wield(agent, InventorySlot.InLevelCarry, now); return; }
        if (HasItem(backpack, _hand)) { Wield(agent, _hand, now); return; }
        foreach (var slot in Hands)
            if (HasItem(backpack, slot)) { Wield(agent, slot, now); return; }
    }

    /// <summary>True when the local player holds something in the carry slot and has another slot in hand.</summary>
    public static bool OnBack(PlayerAgent? agent)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        return agent != null && backpack != null && agent.Inventory is { } inventory
            && inventory.WieldedSlot != InventorySlot.InLevelCarry && HasItem(backpack, InventorySlot.InLevelCarry);
    }

    /// <summary>True when <paramref name="inventory"/> is the local player's (the question a <see cref="GameHooks.WieldedSlotBlocked"/> release asks first).</summary>
    public static bool IsLocal(PlayerInventoryBase inventory) => Players.Local?.Inventory is { } mine && mine.Pointer == inventory.Pointer;

    /// <summary>Whatever let the player carry on the back has ended: the object goes back in the hands. The game ignores a wield request made
    /// while the leaving still holds the inventory, so <see cref="Settle"/> repeats it until it holds.</summary>
    public static void GiveBack()
    {
        InHands = null;
        Pending = true;
        _askAgainAt = 0f;
    }

    /// <summary>Every frame while <see cref="Pending"/>: done once the game wields it, nothing is carried or the player is gone. A fallen
    /// player's object drops instead.</summary>
    public static void Settle(float now)
    {
        if (!Pending) return;
        var agent = Players.Local;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (agent == null || agent.WasCollected || backpack == null || agent.Inventory == null) { Pending = false; return; }
        if (DropIfFallen(agent, now) || !agent.Alive || !HasItem(backpack, InventorySlot.InLevelCarry)
            || agent.Inventory.WieldedSlot == InventorySlot.InLevelCarry) { Pending = false; return; }
        if (now >= _askAgainAt) Wield(agent, InventorySlot.InLevelCarry, now);
    }

    /// <summary>When the local player is down or pinned with the object on the back, it is dropped where they are, through the same request
    /// the game makes for one in hand (the game's own <c>PLOC_Downed</c> and <c>PLOC_GrabbedByPouncer</c> test the wield and do nothing for
    /// one on the back). True when a drop was asked for; asked again every second until the slot is empty.</summary>
    public static bool DropIfFallen(PlayerAgent agent, float now)
    {
        if (now < _dropAgainAt || !Checkpoint.Settled || !OnBack(agent)) return false;
        if (agent.Alive && Players.Motion(agent) != PlayerMotion.GrabbedByPouncer) return false;
        if (!PlayerBackpackManager.LocalBackpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var item) || item?.Instance == null) return false;
        _dropAgainAt = now + 1f;
        PlayerBackpackManager.WantToDropItem_Local(item.Instance.Get_pItemData(), agent.Position, agent.transform.rotation, true);
        return true;
    }

    /// <summary>The <c>ItemDataBlock</c> id of the object in this player's carry slot, on the back or in the hands; 0 when there is none.</summary>
    public static uint ItemId(PlayerAgent agent)
    {
        if (agent.Owner == null || !PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack) || backpack == null) return 0;
        return backpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var item) && item != null && item.IsLoaded ? item.ItemID : 0;
    }

    /// <summary>Shows another player's object at the game's own carry slot place on their body: the game switches an object off when it
    /// leaves the hand and places backpack items only at equip time, so this asks for that placing again once it is seen switched off
    /// (<c>PlayerInventorySynced.PlaceAllBackpackItems</c>). It reads only the replicated slot and wield, so a player who joined late sees the same.</summary>
    public static void ShowOnBack(PlayerAgent agent)
    {
        if (agent.IsLocallyOwned || agent.Owner == null || agent.Inventory == null) return;
        var inventory = agent.Inventory.TryCast<PlayerInventorySynced>();
        if (inventory == null || inventory.WieldedSlot == InventorySlot.InLevelCarry) return;
        if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack) || backpack == null
            || !backpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var item) || item?.Instance == null || !item.IsLoaded) return;
        if (!item.Instance.gameObject.activeSelf) inventory.PlaceAllBackpackItems();
    }

    /// <summary>Sets how long a heavy object's pick-up prompt is held (0 is a press). False when the item has no timed pick-up, and it stays as it was.</summary>
    public static bool SetPickupHold(Item item, float seconds)
    {
        var timed = item.TryCast<CarryItemPickup_Core>()?.m_interactComp?.TryCast<Interact_Timed>();
        if (timed == null) return false;
        timed.InteractDuration = Mathf.Max(seconds, 0.01f);
        return true;
    }

    private static bool HasItem(PlayerBackpack backpack, InventorySlot slot)
        => backpack.TryGetBackpackItem(slot, out var item) && item != null && item.IsLoaded;

    private static void Wield(PlayerAgent agent, InventorySlot slot, float now)
    {
        _askAgainAt = now + AskEvery;
        agent.Sync.WantsToWieldSlot(slot);
    }
}
