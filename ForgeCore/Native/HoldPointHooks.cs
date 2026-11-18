using HarmonyLib;

namespace ForgeCore;

/// <summary>Put on the first time a point is shut (<see cref="HoldPoint.Disable"/>): the game's switch-on of a shut point becomes a
/// switch-off. A prefix that only changes its argument, so the game's body, replication and callbacks run as for any switch-off.</summary>
internal static class HoldPointShutHook
{
    [HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.SetActive))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Keep(Interact_Timed __instance, ref bool __0)
    {
        if (__0 && HoldPoint.IsShut(__instance)) __0 = false;
    }
}
