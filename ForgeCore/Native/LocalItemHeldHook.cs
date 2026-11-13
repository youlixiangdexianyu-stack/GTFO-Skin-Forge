using System;
using HarmonyLib;
using Player;

namespace ForgeCore;

public static partial class GameHooks
{
    /// <summary><c>PlayerBackpackManager.HasItem_Local</c>: what the game's interactions (supply and box pick-ups, terminals, doors) ask to
    /// learn whether the local player is carrying a heavy object. A package whose carrier keeps the object on the back (the item stays in
    /// the carry slot, another slot is in hand) vouches here that the slot reads as empty (<see cref="LocalItemGate.Hide"/>).</summary>
    public static readonly LocalItemGate LocalItemHeld = new("GameHooks.LocalItemHeld", typeof(LocalItemHeldHook));
}

/// <summary>The local player's held-item read (<see cref="GameHooks.LocalItemHeld"/>) and who may hide a slot from it.</summary>
public sealed class LocalItemGate
{
    private readonly string _module;
    private readonly Type _patches;
    private Func<InventorySlot, bool>[] _hides = [];

    internal LocalItemGate(string module, Type patches)
    {
        _module = module;
        _patches = patches;
    }

    /// <summary>Asked while the game reports a slot as held: true makes the game read it as empty. The first subscription puts the patch on.</summary>
    public void Hide(Func<InventorySlot, bool> hide)
    {
        Patches.Ensure(_module, _patches);
        _hides = [.. _hides, hide];
    }

    internal bool Hidden(InventorySlot slot)
    {
        foreach (var hide in _hides)
        {
            try { if (hide(slot)) return true; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-local-item-held-hook-handler-failed", "A " + _module + " handler failed: " + error); }
        }
        return false;
    }
}

[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.HasItem_Local))]
internal static class LocalItemHeldHook
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(InventorySlot slot, ref bool __result)
    {
        if (__result && GameHooks.LocalItemHeld.Hidden(slot)) __result = false;
    }
}
