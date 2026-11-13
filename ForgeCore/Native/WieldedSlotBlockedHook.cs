using System;
using HarmonyLib;

namespace ForgeCore;

public static partial class GameHooks
{
    /// <summary><c>PlayerInventoryBase.get_WieldedSlotBlocked</c>: true while the in-level carry slot is in hand, which makes
    /// <c>PlayerSync.WantsToWieldSlot</c> (every synced wield request, the player's own included), the bots' equip and reload actions and
    /// <c>PlayerBackpack.ShowHideInstance</c> refuse. A package that lets a player wield something else while carrying vouches for that
    /// player here (<see cref="WieldedSlotGate.Release"/>). Key handling (<c>PlayerInventoryLocal.CheckInput</c>) tests the slot itself
    /// and is not affected.</summary>
    public static readonly WieldedSlotGate WieldedSlotBlocked = new("GameHooks.WieldedSlotBlocked", typeof(WieldedSlotBlockedHook));
}

/// <summary>The carry slot's wield block (<see cref="GameHooks.WieldedSlotBlocked"/>) and who is exempt from it.</summary>
public sealed class WieldedSlotGate
{
    private readonly string _module;
    private readonly Type _patches;
    private Func<PlayerInventoryBase, bool>[] _releases = [];

    internal WieldedSlotGate(string module, Type patches)
    {
        _module = module;
        _patches = patches;
    }

    /// <summary>Asked while the game reports the block: true lifts it for that inventory. The first subscription puts the patch on.</summary>
    public void Release(Func<PlayerInventoryBase, bool> release)
    {
        Patches.Ensure(_module, _patches);
        _releases = [.. _releases, release];
    }

    internal bool Released(PlayerInventoryBase inventory)
    {
        foreach (var release in _releases)
        {
            try { if (release(inventory)) return true; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-wielded-slot-blocked-hook-handler-failed", "A " + _module + " handler failed: " + error); }
        }
        return false;
    }
}

[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.WieldedSlotBlocked), MethodType.Getter)]
internal static class WieldedSlotBlockedHook
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(PlayerInventoryBase __instance, ref bool __result)
    {
        if (__result && GameHooks.WieldedSlotBlocked.Released(__instance)) __result = false;
    }
}
