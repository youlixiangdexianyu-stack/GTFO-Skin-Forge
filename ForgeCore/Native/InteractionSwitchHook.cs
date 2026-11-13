using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ForgeCore;

/// <summary>The three <c>PlayerInteraction</c> switch setters, patched only once a lease first holds a switch off
/// (<see cref="InputLease.BlockInteractions"/>): every write while a switch is held off is remembered and the game keeps reading false.</summary>
[HarmonyPatch]
internal static class InteractionSwitchHook
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(typeof(PlayerInteraction), nameof(PlayerInteraction.CameraRayInteractionEnabled));
        yield return AccessTools.PropertySetter(typeof(PlayerInteraction), nameof(PlayerInteraction.SphereCheckInteractionEnabled));
        yield return AccessTools.PropertySetter(typeof(PlayerInteraction), nameof(PlayerInteraction.LadderInteractionEnabled));
    }

    [HarmonyPrefix]
    private static void Before(MethodBase __originalMethod, ref bool value)
    {
        var index = __originalMethod.Name switch
        {
            "set_CameraRayInteractionEnabled" => 0,
            "set_SphereCheckInteractionEnabled" => 1,
            _ => 2
        };
        value = InputLease.InteractionWritten(index, value);
    }
}
