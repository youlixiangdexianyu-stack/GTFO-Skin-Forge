using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;

namespace ForgeUI;

/// <summary>
/// Presses on the menu items this library creates are routed here by the prefixes below and never reach the game's own handlers,
/// which would otherwise act on a copied APPAREL button or a picker card with no gear behind it. Hovers keep the game's own
/// handling (colours, sound, and the hover state the game needs to send the next hover in) and are reported afterwards. Every
/// other item goes to the game untouched.
/// </summary>
internal static class Input
{
    private static readonly Dictionary<IntPtr, (CM_Item Item, Action Click, Action<bool>? Hover, int Index)> Routes = new();
    private static readonly List<IntPtr> Keys = new();
    private const int SweepBudget = 8;
    private static int _sweep;

    /// <summary>Routes presses on <paramref name="item"/> to <paramref name="onClick"/>; <paramref name="onHover"/>, when given, is
    /// told after the game handled the cursor entering (true) or leaving (false) the item.</summary>
    internal static IntPtr Route(CM_Item item, Action onClick, Action<bool>? onHover)
    {
        PatchGroups.Ensure(PatchGroup.Clicks);
        var key = item.Pointer;
        var index = Routes.TryGetValue(key, out var previous) ? previous.Index : Keys.Count;
        if (index == Keys.Count) Keys.Add(key);
        Routes[key] = (item, onClick, onHover, index);
        return key;
    }

    internal static void Unroute(IntPtr key)
    {
        if (!Routes.Remove(key, out var removed)) return;
        var last = Keys[^1];
        Keys.RemoveAt(Keys.Count - 1);
        if (removed.Index < Keys.Count)
        {
            Keys[removed.Index] = last;
            var moved = Routes[last];
            Routes[last] = (moved.Item, moved.Click, moved.Hover, removed.Index);
        }
    }

    /// <summary>Parent teardown can destroy items without an explicit library removal. Cleanup uses saved keys and never
    /// dereferences a destroyed item to remove its callbacks. At most eight routes are probed per frame; explicit Unroute
    /// remains immediate.</summary>
    internal static void PruneDestroyed()
    {
        var count = Math.Min(SweepBudget, Keys.Count);
        for (var i = 0; i < count && Keys.Count > 0; i++)
        {
            if (_sweep >= Keys.Count) _sweep = 0;
            var key = Keys[_sweep];
            var route = Routes[key];
            if (route.Item == null || route.Item.WasCollected) Unroute(key);
            else _sweep++;
        }
    }

    private static bool Press(Il2CppSystem.Object instance)
    {
        if (!Routes.TryGetValue(instance.Pointer, out var route)) return true;
        try { route.Click(); }
        catch (Exception error) { Plugin.Error("ui-menu-click-failed", "Menu click failed: " + error); }
        return false;
    }

    private static void Hover(Il2CppSystem.Object instance, bool inside)
    {
        if (!Routes.TryGetValue(instance.Pointer, out var route) || route.Hover == null) return;
        try { route.Hover(inside); }
        catch (Exception error) { Plugin.Error("ui-menu-hover-failed", "Menu hover failed: " + error); }
    }

    [PatchGroup(PatchGroup.Clicks)]
    [HarmonyPatch(typeof(CM_Item), nameof(CM_Item.OnBtnPress))]
    private static class ItemPress
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_Item __instance) => Press(__instance);
    }

    [PatchGroup(PatchGroup.Clicks)]
    [HarmonyPatch(typeof(CM_LobbyScrollItem), nameof(CM_LobbyScrollItem.OnBtnPress))]
    private static class ScrollItemPress
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_LobbyScrollItem __instance) => Press(__instance);
    }

    [PatchGroup(PatchGroup.Clicks)]
    [HarmonyPatch(typeof(CM_InventorySlotItem), nameof(CM_InventorySlotItem.OnBtnPress))]
    private static class SlotItemPress
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_InventorySlotItem __instance) => Press(__instance);
    }

    [PatchGroup(PatchGroup.Clicks)]
    [HarmonyPatch(typeof(CM_LobbyScrollItem), nameof(CM_LobbyScrollItem.OnHoverIn))]
    private static class ScrollItemHoverIn
    {
        [HarmonyPostfix]
        private static void Postfix(CM_LobbyScrollItem __instance) => Hover(__instance, true);
    }

    [PatchGroup(PatchGroup.Clicks)]
    [HarmonyPatch(typeof(CM_LobbyScrollItem), nameof(CM_LobbyScrollItem.OnHoverOut))]
    private static class ScrollItemHoverOut
    {
        [HarmonyPostfix]
        private static void Postfix(CM_LobbyScrollItem __instance) => Hover(__instance, false);
    }
}
