using System;
using System.Collections.Generic;
using System.Reflection;
using CellMenu;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Player;
using TMPro;
using Object = UnityEngine.Object;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>Shows a gear under another text on every screen that draws it: wherever the game's gear screens (the lobby's gear slots
    /// and weapon window, the in-level weapon list) draw a text holding <paramref name="find"/>, this player sees the text of
    /// <paramref name="key"/> from <paramref name="texts"/> in their language instead, also after a language change. Use it for a
    /// text that is not the gear's name, such as a default description; use <see cref="GearName(UITexts, string, string)"/> for the name of a gear.
    /// A text another mod registered is refused (logged).</summary>
    /// <example><c>UIKit.GearText(ui.Texts, "A prototype energy weapon.", "weapon.description");</c></example>
    public static void GearText(UITexts texts, string find, string key) => GearNames.Register(texts, find, key, false);

    /// <summary>Shows the gear whose gear-packet public name is <paramref name="packetName"/> under the text of <paramref name="key"/> from
    /// <paramref name="texts"/> in this player's language, on every screen that draws it. The packet name is what replicates and what
    /// every peer recognises the gear by, so it stays as it is; only this player's own screens change. Like <see cref="GearText"/>
    /// (every text holding the packet name is translated), and the local player's weapon list also shows the name on the name lines
    /// of the slot whose item has this packet name, where the game draws the stock gear block's own texts instead of the packet name.</summary>
    /// <example><c>UIKit.GearName(ui.Texts, "Arsenal HMG", "arsenal.weapon.machinegun");</c></example>
    public static void GearName(UITexts texts, string packetName, string key) => GearNames.Register(texts, packetName, key, true);

    /// <summary>As <see cref="GearName(UITexts, string, string)"/> for a name that is not in a language file of yours: <paramref name="name"/>
    /// returns it in the current language, and runs again after every language change. The same mod registering the packet name again
    /// replaces its rule; another mod's is refused (logged).</summary>
    public static void GearName(string packetName, Func<string> name) => GearNames.Register(packetName, name);
}

/// <summary>The game's gear screens drawing the registered names (<see cref="UIKit.GearName(UITexts, string, string)"/>, <see cref="UIKit.GearText"/>). One patch
/// per screen position, none of them per frame: the game's own calls that set a text run the registered replacement on it.</summary>
internal static class GearNames
{
    private static readonly GearNameBook Book = new();
    private static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearMelee, InventorySlot.GearClass };
    // Per gear slot: the gear packet last read and the rule it matched (null: none), so an unchanged item costs no text reads.
    private static readonly IntPtr[] SlotItem = new IntPtr[4], SlotRange = new IntPtr[4];
    private static readonly GearNameRule?[] SlotRule = new GearNameRule?[4];

    internal static void Register(UITexts texts, string find, string key, bool byPacket)
    {
        if (texts == null) throw new ArgumentNullException(nameof(texts));
        PatchGroups.Ensure(PatchGroup.GearNames);
        var problem = Book.Add(texts, find, texts, key, byPacket);
        if (problem != null) Plugin.Error("ui-gear-name-not-added", "Gear name not added: " + problem + ".");
    }

    internal static void Register(string packetName, Func<string> name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        PatchGroups.Ensure(PatchGroup.GearNames);
        var problem = Book.Add(name.Method.Module.Assembly, packetName, name, true);
        if (problem != null) Plugin.Error("ui-gear-name-not-added", "Gear name not added: " + problem + ".");
    }

    /// <summary>The language changed: the screens already drawn show the new language.</summary>
    internal static void LanguageChanged()
    {
        if (Book.IsEmpty) return;
        foreach (var item in Object.FindObjectsOfType(Il2CppType.Of<CM_InventorySlotItem>())) Retext(item.Cast<CM_InventorySlotItem>());
        foreach (var bar in Object.FindObjectsOfType(Il2CppType.Of<CM_PlayerLobbyBar>())) RetextChildren(bar.Cast<CM_PlayerLobbyBar>());
        foreach (var slot in Object.FindObjectsOfType(Il2CppType.Of<PUI_InventoryItem>())) RetextChildren(slot.Cast<PUI_InventoryItem>());
        NameSlots();
    }

    private static void Retext(TextMeshPro? text)
    {
        if (text == null || text.WasCollected) return;
        var current = text.text;
        var shown = Book.Display(current);
        if (!ReferenceEquals(shown, current)) text.text = shown;
    }

    private static void Retext(CM_InventorySlotItem item)
    {
        if (item == null || item.WasCollected) return;
        var name = item.GearPublicName;
        var shown = Book.Display(name);
        if (!ReferenceEquals(shown, name)) item.GearPublicName = shown;
        var archetype = item.GearArchetypeName;
        shown = Book.Display(archetype);
        if (!ReferenceEquals(shown, archetype)) item.GearArchetypeName = shown;
        var description = item.GearDescription;
        shown = Book.Display(description);
        if (!ReferenceEquals(shown, description)) item.GearDescription = shown;
        Retext(item.m_nameText); Retext(item.m_subTitleText); Retext(item.m_descText);
    }

    private static void RetextChildren(UnityEngine.Component? root)
    {
        if (root == null || root.WasCollected) return;
        foreach (var text in root.GetComponentsInChildren<TextMeshPro>(true)) Retext(text);
    }

    /// <summary>The name lines of the local weapon slots whose item has a registered packet name show that gear's name.</summary>
    private static void NameSlots()
    {
        if (!Book.HasPackets) return;
        var lines = GuiManager.PlayerLayer?.Inventory?.m_inventorySlots;
        if (lines == null || ForgeCore.Players.Local == null) return;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null) return;
        for (var i = 0; i < GearSlots.Length; i++)
        {
            var slot = GearSlots[i];
            // Not TryGetValue: its out value is built through a shared generic constructor the runtime cannot find (MissingMethodException).
            if (!lines.ContainsKey(slot)) continue;
            var line = lines[slot];
            if (line == null || line.WasCollected) continue;
            var rule = RuleOf(i, backpack, slot);
            if (rule == null) continue;
            // The game fills all three from the stock gear block's archetype and texts, never from the packet name: the slim line, and the
            // selected slot's large archetype line and name line.
            Draw(line.m_slim_archetypeName, rule.Shown);
            Draw(line.m_selected_archetypeName, rule.Shown);
            Draw(line.m_selected_detailedName, rule.Shown);
        }
    }

    private static GearNameRule? RuleOf(int index, PlayerBackpack backpack, InventorySlot slot)
    {
        if (!backpack.TryGetBackpackItem(slot, out var item) || item == null) { SlotItem[index] = SlotRange[index] = IntPtr.Zero; return SlotRule[index] = null; }
        var range = item.GearIDRange;
        var rangePointer = range != null ? range.Pointer : IntPtr.Zero;
        if (item.Pointer != SlotItem[index] || rangePointer != SlotRange[index])
        {
            SlotItem[index] = item.Pointer;
            SlotRange[index] = rangePointer;
            SlotRule[index] = Book.ForPacket(range?.PublicGearName);
        }
        else Book.Sync();
        return SlotRule[index];
    }

    private static void Draw(TextMeshPro? line, string name)
    {
        if (line != null && !line.WasCollected && line.text != name) line.text = name;
    }

    /// <summary>A weapon list name text the game is about to set takes the registered text (<see cref="SlotNameCalls"/>).</summary>
    internal static void TranslateListName(ref string name)
    {
        if (Book.IsEmpty) return;
        try { name = Book.Display(name); }
        catch (Exception error) { Plugin.Error("ui-gear-name-weapon-list", "A gear name in the weapon list could not be translated: " + error); }
    }

    /// <summary>The weapon list set a name: the slots registered by packet name show it again (<see cref="SlotNameCalls"/> and <see cref="ListSlotName"/>).</summary>
    internal static void ListSlotNamed()
    {
        try { NameSlots(); }
        catch (Exception error) { Plugin.Error("ui-gear-names-gear-name-weapon-list", "A gear name in the weapon list could not be set: " + error); }
    }

    /// <summary>The weapon list's other calls that fill the name lines, for a slot whose item is registered by packet name (its two name
    /// calls are <see cref="SlotNameCalls"/>).</summary>
    [PatchGroup(PatchGroup.GearNames)]
    [HarmonyPatch]
    private static class ListSlotName
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // UpdateItemUI needs no patch: its native body calls UpdateInfoForItem, then only UpdateAmmoForItem, which sets no name.
            yield return AccessTools.Method(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateInfoForItem));
            yield return AccessTools.Method(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAllSlots));
        }

        [HarmonyPostfix]
        private static void Postfix() => ListSlotNamed();
    }

    /// <summary>The loadout picker and every lobby gear slot (the local player's and other players') load and re-read their texts here.</summary>
    [PatchGroup(PatchGroup.GearNames)]
    [HarmonyPatch]
    private static class LobbySlotText
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CM_InventorySlotItem), nameof(CM_InventorySlotItem.LoadData));
            yield return AccessTools.Method(typeof(CM_InventorySlotItem), nameof(CM_InventorySlotItem.UpdateText));
        }

        [HarmonyPostfix]
        private static void Postfix(CM_InventorySlotItem __instance)
        {
            if (Book.IsEmpty) return;
            try { Retext(__instance); }
            catch (Exception error) { Plugin.Error("ui-gear-name-gear-slot", "A gear name in a gear slot could not be translated: " + error); }
        }
    }

    /// <summary>The lobby weapon window built when a gear slot is pointed at.</summary>
    [PatchGroup(PatchGroup.GearNames)]
    [HarmonyPatch(typeof(CM_PlayerLobbyBar), nameof(CM_PlayerLobbyBar.UpdateWeaponWindowInfo))]
    private static class LobbyWeaponWindow
    {
        [HarmonyPostfix]
        private static void Postfix(CM_PlayerLobbyBar __instance)
        {
            if (Book.IsEmpty) return;
            try { RetextChildren(__instance); }
            catch (Exception error) { Plugin.Error("ui-gear-name-lobby-weapon-window", "A gear name in the lobby weapon window could not be translated: " + error); }
        }
    }
}

/// <summary>The weapon list row's two name calls (<c>PUI_InventoryItem.SetArchetypeName</c>, <c>SetDetailedName</c>), which the inventory
/// readout and the gear names both read: one patch class (group <see cref="PatchGroup.SlotNames"/>, put on with either feature). In this
/// order: before the body the gear names translate the text the game sets; after it the inventory readout marks its rows for the read at
/// the end of the frame, then the gear names draw the registered names on the slot's lines (the mark is read later, so it never sees a
/// half-drawn row).</summary>
[PatchGroup(PatchGroup.SlotNames)]
[HarmonyPatch]
internal static class SlotNameCalls
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetArchetypeName));
        yield return AccessTools.Method(typeof(PUI_InventoryItem), nameof(PUI_InventoryItem.SetDetailedName));
    }

    [HarmonyPrefix]
    private static void Prefix(ref string name)
    {
        if (PatchGroups.Has(PatchGroup.GearNames)) GearNames.TranslateListName(ref name);
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (PatchGroups.Has(PatchGroup.Inventory)) UIKit.InventoryReading.MarkNamesDirty();
        if (PatchGroups.Has(PatchGroup.GearNames)) GearNames.ListSlotNamed();
    }
}
