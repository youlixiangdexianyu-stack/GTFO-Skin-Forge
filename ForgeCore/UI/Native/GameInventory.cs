using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeUI;

/// <summary>One filled slot of the game's weapon list. See <see cref="GameInventory"/>.</summary>
public readonly struct InventoryRow
{
    internal InventoryRow(InventorySlot slot, string key, string kind, string name, bool selected, int clip, int reserve, float reserveRel,
        bool infinite, float stamina, Sprite? icon, int clipSize, int reserveMax)
    {
        ReserveMax = reserveMax;
        Slot = slot;
        Key = key;
        Kind = kind;
        Name = name;
        Selected = selected;
        Clip = clip;
        Reserve = reserve;
        ReserveRel = reserveRel;
        Infinite = infinite;
        Stamina = stamina;
        Icon = icon;
        ClipSize = clipSize;
    }

    public InventorySlot Slot { get; }
    /// <summary>The player's binding that selects the slot, as the game names it; empty for a slot with no selecting key.</summary>
    public string Key { get; }
    /// <summary>The game's archetype line of the item (for example the weapon class).</summary>
    public string Kind { get; }
    /// <summary>The game's name line of the item; a text a mod put there with <see cref="UIKit.ClaimWeaponListLine"/> replaces it.</summary>
    public string Name { get; }
    /// <summary>The slot in hand.</summary>
    public bool Selected { get; }
    /// <summary>Rounds in the magazine, or −1 where the game's list shows none.</summary>
    public int Clip { get; }
    /// <summary>Rounds or uses in reserve, or −1 where the game's list shows none.</summary>
    public int Reserve { get; }
    /// <summary>The reserve as the game's list shows it relative to its cap (0..1), or −1 where it shows none.</summary>
    public float ReserveRel { get; }
    public bool Infinite { get; }
    /// <summary>The melee weapon's stamina 0..1 as the game last set it (read live on every access); −1 on every other slot.</summary>
    public float Stamina { get; }
    public Sprite? Icon { get; }
    /// <summary>The magazine size, or −1 when the game's ammo storage has none for the slot.</summary>
    public int ClipSize { get; }

    /// <summary>The most rounds the reserve holds, or −1 when the game's ammo storage has none for the slot.</summary>
    public int ReserveMax { get; }

    /// <summary>All the rounds the slot has (magazine and reserve) relative to all it can hold (a full magazine and a full reserve),
    /// 0..1, or −1 where either is unknown.</summary>
    public float TotalRel => Clip >= 0 && Reserve >= 0 && ClipSize > 0 && ReserveMax >= 0
        ? Math.Clamp((Clip + Reserve) / (float)(ClipSize + ReserveMax), 0f, 1f) : -1f;

    internal InventoryRow WithStamina(float stamina) =>
        new(Slot, Key, Kind, Name, Selected, Clip, Reserve, ReserveRel, Infinite, stamina, Icon, ClipSize, ReserveMax);

    /// <summary>The row with another gun's rounds in place of the game's figures (<see cref="LoadoutAmmo"/>).</summary>
    internal InventoryRow WithAmmo(in LoadoutAmmo ammo) =>
        new(Slot, Key, Kind, Name, Selected, ammo.Clip, ammo.Reserve, ammo.ReserveMax > 0 ? Math.Clamp(ammo.Reserve / (float)ammo.ReserveMax, 0f, 1f) : ReserveRel,
            Infinite, Stamina, Icon, ammo.ClipSize > 0 ? ammo.ClipSize : ClipSize, ammo.ReserveMax > 0 ? ammo.ReserveMax : ReserveMax);
}

/// <summary>
/// The local player's weapon list as the game's own HUD shows it (the filled slots in the game's order, what is in hand and the carried
/// item), captured from the game's own calls on its weapon list. Rows are rebuilt only after the game
/// changed one (names and icons also only then), so a quiet frame allocates nothing; <see cref="Version"/> moves and
/// <see cref="Changed"/> is raised after a rebuild that changed something. See <see cref="UIKit.Inventory"/>.
/// </summary>
public sealed class GameInventory
{
    private const int Max = 12;
    private const float CarryEvery = 0.1f, KeysEvery = 1f;
    private readonly InventoryRow[] _rows = new InventoryRow[Max];
    private readonly IntPtr[] _items = new IntPtr[Max];
    private readonly string[] _keys = new string[Max + 1];
    private int _count;
    private bool _numbers, _names, _picturePending;
    private float _stamina = -1f, _nextCarry, _nextKeys;
    private IntPtr _carried;
    private readonly SlotFacts[] _facts = new SlotFacts[Max];
    private string _heldText = "∞";
    private int _heldTextFor = -2;

    /// <summary>The magazine of the item in hand (no magazine reads as unlimited), as the game's list shows it; moves with the list.</summary>
    public HeldMagazine Held { get; private set; } = HeldMagazine.Unlimited;

    /// <summary>The rounds in the held magazine as text, or ∞ without a magazine; the same string
    /// while nothing changes.</summary>
    public string HeldText => _heldText;

    /// <summary>The aim button is held on the item in hand (read from the game every frame); false with nothing in hand.</summary>
    public bool Aiming { get; private set; }

    internal GameInventory()
    {
        for (var i = 0; i < _keys.Length; i++) _keys[i] = "";
        CarryName = "";
    }

    public int Version { get; private set; }

    /// <summary>Raised on the main thread after a rebuild changed something.</summary>
    public event Action? Changed;

    /// <summary>The filled slots (empty slots are not counted), in the game's own order.</summary>
    public int Count => _count;

    public InventoryRow Row(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        var row = _rows[index];
        return row.Slot == InventorySlot.GearMelee ? row.WithStamina(_stamina) : row;
    }

    /// <summary>An in-level item (a cell, a bulkhead key and so on) is in hand.</summary>
    public bool Carrying { get; private set; }

    public string CarryName { get; private set; }

    /// <summary>The game's flashlight icon is lit (the argument of its <c>SetFlashLightIcon</c>).</summary>
    public bool Flashlight { get; internal set; }

    internal void MarkNamesDirty() => _names = true;

    /// <summary>The first read: its game hooks went on only now, so the list is read in full once instead of waiting for the game to change a row.</summary>
    internal void ReadInFull() => _numbers = _names = true;

    /// <summary>After the game's frame, from the driver: rebuilds what the game changed and looks at the carried item ten times a second.</summary>
    internal void Tick()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        var changed = false;
        try
        {
            ReadAim();
            if (now >= _nextCarry)
            {
                _nextCarry = now + CarryEvery;
                changed |= ReadCarry();
            }
            var keys = now >= _nextKeys;
            if (keys) _nextKeys = now + KeysEvery;
            if (_numbers || _names || keys)
            {
                var retry = keys && _picturePending;
                _picturePending = false;
                changed |= Rebuild(_names || retry, keys);
            }
        }
        catch (Exception error) { Plugin.Error("ui-reading-weapon-list-failed", "Reading the weapon list failed: " + error); }
        _numbers = _names = false;
        if (changed) Bump();
    }

    internal void Bump()
    {
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-inventory-listener-failed", "An inventory listener failed: " + error); }
    }

    private void ReadAim()
    {
        var agent = ForgeCore.Players.Local;
        var inventory = agent != null && !agent.WasCollected ? agent.Inventory : null;
        var item = inventory != null && !inventory.WasCollected ? inventory.WieldedItem : null;
        Aiming = item != null && !item.WasCollected && item.AimButtonHeld;
    }

    /// <summary>The held magazine from the rows just rebuilt; the text is built again only when the rounds change.</summary>
    private void RefreshHeld()
    {
        var held = HeldMagazine.Pick(_facts.AsSpan(0, _count));
        Held = held;
        var rounds = held.Limited ? held.Clip : -1;
        if (rounds == _heldTextFor) return;
        _heldTextFor = rounds;
        _heldText = held.Limited ? rounds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "∞";
    }

    private bool ReadCarry()
    {
        var agent = ForgeCore.Players.Local;
        var inventory = agent != null && !agent.WasCollected ? agent.Inventory : null;
        // The carry slot, not the hand: a heavy object that rides on a suit's back is still carried.
        Item? item = null;
        if (inventory != null && inventory.WieldedSlot == InventorySlot.InLevelCarry) item = inventory.WieldedItem;
        else if (agent != null && PlayerBackpackManager.LocalBackpack is { } backpack
            && backpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var held) && held != null && held.IsLoaded) item = held.Instance;
        if (item != null && item.WasCollected) item = null;
        var carrying = item != null;
        var pointer = item != null ? item.Pointer : IntPtr.Zero;
        if (carrying == Carrying && pointer == _carried) return false;
        Carrying = carrying;
        _carried = pointer;
        CarryName = item != null ? item.PublicName ?? "" : "";
        return true;
    }

    private bool Rebuild(bool names, bool keys)
    {
        var changed = false;
        var inventory = GuiManager.PlayerLayer?.Inventory;
        var order = inventory?.m_slotGUIOrder;
        var items = inventory?.m_inventorySlots;
        var count = 0;
        if (inventory != null && !inventory.WasCollected && order != null && items != null)
        {
            if (keys) RefreshKeys(ref names);
            // The game's LocalBackpack throws while the level loads and there is no local player yet.
            var storage = ForgeCore.Players.Local != null ? PlayerBackpackManager.LocalBackpack?.AmmoStorage : null;
            for (var i = 0; i < order.Length && count < Max; i++)
            {
                var slot = order[i];
                // Not TryGetValue: its out value is built through a shared generic constructor the runtime cannot find (MissingMethodException).
                if (!items.ContainsKey(slot)) continue;
                var item = items[slot];
                if (item == null || item.WasCollected) continue;
                if (item.CurrentState == ePUI_InventortyItemState.Empty) continue;
                var old = _rows[count];
                var refresh = names || _items[count] != item.Pointer || old.Slot != slot;
                var kind = old.Kind;
                var name = old.Name;
                var icon = old.Icon;
                var clipSize = old.ClipSize;
                // The magazine size and the reserve's cap are the game's (an ammo modifier can move the cap), so they are read on every rebuild.
                var slotAmmo = storage?.GetInventorySlotAmmo(slot);
                var reserveMax = slotAmmo is { BulletsMaxCap: > 0 } cap ? cap.BulletsMaxCap : -1;
                if (refresh)
                {
                    kind = item.m_selected_archetypeName?.text ?? "";
                    var line = item.m_selected_detailedName;
                    name = line != null ? WeaponListLine.RenamedTo(line) ?? line.text ?? "" : "";
                    icon = ModelPictured(slot) ? ModelIcon(slot) : item.m_selected_icon?.sprite;
                    clipSize = slotAmmo is { BulletClipSize: > 0 } ammo ? ammo.BulletClipSize : -1;
                }
                var row = new InventoryRow(slot, _keys[(int)slot], kind, name, item.CurrentState == ePUI_InventortyItemState.Selected,
                    InventoryAmmo.Shown(item.m_showAmmoClip, item.m_ammoClipAbsVal),
                    InventoryAmmo.Shown(item.m_showAmmoPack, item.m_ammoTotalAbsVal),
                    InventoryAmmo.Shown(item.m_showAmmoTotalRel, item.m_ammoTotalRelVal),
                    item.m_showAmmoInfinite, slot == InventorySlot.GearMelee ? _stamina : -1f, icon, clipSize, reserveMax);
                if (!Same(old, row)) changed = true;
                _rows[count] = row;
                _facts[count] = new SlotFacts(row.Selected, row.Clip, row.ClipSize, row.Infinite);
                _items[count] = item.Pointer;
                count++;
            }
        }
        if (count != _count)
        {
            changed = true;
            for (var i = count; i < _count; i++) { _rows[i] = default; _items[i] = IntPtr.Zero; }
        }
        _count = count;
        RefreshHeld();
        return changed;
    }

    /// <summary>Slots whose picture is the item's own model: the game's list has no picture for a consumable and a stock glyph for a
    /// resource pack.</summary>
    private static bool ModelPictured(InventorySlot slot) =>
        slot is InventorySlot.ResourcePack or InventorySlot.Consumable or InventorySlot.ConsumableHeavy or InventorySlot.Pickup;

    /// <summary>The slot's item model picture; while the item is not built yet it is null and the next quiet rebuild asks again.</summary>
    private Sprite? ModelIcon(InventorySlot slot)
    {
        var backpack = ForgeCore.Players.Local != null ? PlayerBackpackManager.LocalBackpack : null;
        var instance = backpack != null && backpack.TryGetBackpackItem(slot, out var held) && held != null && held.IsLoaded ? held.Instance : null;
        var picture = ItemModelIcons.Of(instance);
        // The instance may exist before the game has built its model: that is not a failure, and the next quiet rebuild asks again.
        if (picture == null && (instance == null || ItemModelIcons.Waiting(instance))) _picturePending = true;
        return picture;
    }

    /// <summary>Reads the selecting keys again; a changed binding asks for the rows' texts to be built again.</summary>
    private void RefreshKeys(ref bool names)
    {
        for (var slot = 0; slot <= Max; slot++)
        {
            var action = ActionOf((InventorySlot)slot);
            var key = action == InputAction.None ? "" : UIKitKeys.Binding(action);
            if (key == _keys[slot]) continue;
            _keys[slot] = key;
            names = true;
        }
    }

    private static InputAction ActionOf(InventorySlot slot) => slot switch
    {
        InventorySlot.GearMelee => InputAction.SelectMelee,
        InventorySlot.GearStandard => InputAction.SelectStandard,
        InventorySlot.GearSpecial => InputAction.SelectSpecial,
        InventorySlot.GearClass => InputAction.SelectTool,
        InventorySlot.ResourcePack => InputAction.SelectResourcePack,
        InventorySlot.Consumable => InputAction.SelectConsumable,
        InventorySlot.HackingTool => InputAction.SelectHackingTool,
        _ => InputAction.None,
    };

    private static bool Same(in InventoryRow a, in InventoryRow b) =>
        a.Slot == b.Slot && a.Selected == b.Selected && a.Clip == b.Clip && a.Reserve == b.Reserve && a.ReserveRel == b.ReserveRel
        && a.Infinite == b.Infinite && a.ClipSize == b.ClipSize && a.ReserveMax == b.ReserveMax && a.Icon == b.Icon
        && string.Equals(a.Key, b.Key, StringComparison.Ordinal) && string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>The level ends: nothing is shown any more.</summary>
    internal void Clear()
    {
        Array.Clear(_rows, 0, Max);
        Array.Clear(_items, 0, Max);
        var changed = _count != 0 || Carrying || Flashlight;
        _count = 0;
        Array.Clear(_facts, 0, Max);
        RefreshHeld();
        Aiming = false;
        Carrying = false;
        CarryName = "";
        _carried = IntPtr.Zero;
        Flashlight = false;
        _stamina = -1f;
        _nextCarry = _nextKeys = 0f;
        _numbers = _names = false;
        if (changed) Bump();
    }

    // ---- The game's calls -----------------------------------------------------------------------------------------------------

    /// <summary>Calls that change what a slot's texts, picture or state show.</summary>
    [PatchGroup(PatchGroup.Inventory)]
    [HarmonyPatch]
    private static class Names
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetState));
            // SetArchetypeName and SetDetailedName: the one patch class SlotNameCalls (GearNames.cs), shared with the gear names.
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetAllShowFlags));
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetIcon));
        }

        private static void Postfix() => UIKit.Inventory._names = true;
    }

    /// <summary>The calls that set a slot's counts: the whole row's (<c>SetAll</c>) and, while shooting, reloading and picking up, the
    /// ammunition's own (<c>OnAmmoUpdated</c> on the item, <c>UpdateAmmoForItem</c> and <c>SetSlotAmmo</c> on the list). Each only marks
    /// the rows for a read at the end of the frame; a read that finds the same figures changes nothing.</summary>
    [PatchGroup(PatchGroup.Inventory)]
    [HarmonyPatch]
    private static class Numbers
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetAll));
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.OnAmmoUpdated));
            yield return AccessTools.Method(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAmmoForItem));
            yield return AccessTools.Method(typeof(PUI_Inventory), nameof(PUI_Inventory.SetSlotAmmo));
        }

        private static void Postfix() => UIKit.Inventory._numbers = true;
    }

    /// <summary>The melee weapon's stamina; it changes while swinging and recovering, so it is read live and moves no version.</summary>
    [PatchGroup(PatchGroup.Inventory)]
    [HarmonyPatch]
    private static class Stamina
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetMeleeStamina));
            yield return AccessTools.Method(typeof(PUI_Inventory), nameof(PUI_Inventory.SetMeleeStamina));
        }

        private static void Postfix(float stamina) => UIKit.Inventory._stamina = Ease.Clamp01(stamina);
    }

    /// <summary>The flashlight icon of the local player's weapon list (not the lobby page's copy of it).</summary>
    [PatchGroup(PatchGroup.Inventory)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.SetFlashLightIcon))]
    private static class FlashlightIcon
    {
        private static void Postfix(PUI_Inventory __instance, bool enabled)
        {
            var own = GuiManager.PlayerLayer?.Inventory;
            var inventory = UIKit.Inventory;
            if (own == null || own.Pointer != __instance.Pointer || inventory.Flashlight == enabled) return;
            inventory.Flashlight = enabled;
            inventory.Bump();
        }
    }

    [PatchGroup(PatchGroup.Inventory)]
    [HarmonyPatch(typeof(PlayerGuiLayer), nameof(PlayerGuiLayer.OnLevelCleanup))]
    private static class Cleanup
    {
        private static void Postfix() => UIKit.Inventory.Clear();
    }
}
