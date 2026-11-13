using HarmonyLib;

namespace ForgeUI;

// The icon renderer loads a job's gear when the job's turn comes; a supplied category's job is loaded by GearIcons instead of the donor gun.
[PatchGroup(PatchGroup.GearIcons)]
[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.LoadGearForIconJob))]
internal static class GearIconJob
{
    [HarmonyPrefix]
    private static bool Prefix(GearIconRendering.IconRenderJob __0)
    {
        try { return !GearIcons.Load(__0); }
        catch (System.Exception error)
        {
            Plugin.Error("ui-gear-icon-job", "Gear icon job could not be loaded; the game loads it: " + error);
            return true;
        }
    }
}
