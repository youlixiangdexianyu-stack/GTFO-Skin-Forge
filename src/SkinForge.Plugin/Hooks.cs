using Gear;
using HarmonyLib;

namespace SkinForge.Plugin;

// dump.cs 614836: runs once every part of a gear is spawned and its GearMaterialFeeder is set up.
[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
static class PartsSpawnedHook
{
    static void Postfix(GearPartHolder __instance) => SkinnedGear.Spawned(__instance);
}

// dump.cs 562404: the feeder re-scans its renderers; our material instances are put back afterwards.
[HarmonyPatch(typeof(GearMaterialFeeder), nameof(GearMaterialFeeder.ScanAndSetupParts))]
static class FeederScanHook
{
    static void Postfix(GearMaterialFeeder __instance) => SkinnedGear.FeederScanned(__instance.Pointer);
}
