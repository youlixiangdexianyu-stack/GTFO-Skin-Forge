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

// The renderer frames a job's gear by the bounds of every renderer under it; additions a mod left out of framing are taken out again.
[PatchGroup(PatchGroup.GearIcons)]
[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.RenderIconPrep))]
internal static class GearIconFraming
{
    [HarmonyPostfix]
    private static void Postfix(GearIconRendering.IconRenderJob __0, ref UnityEngine.Bounds __result)
    {
        try { GearIcons.Frame(__0, ref __result); }
        catch (System.Exception error) { Plugin.Error("ui-gear-icon-framing", "Gear icon framing could not leave additions out; the game's framing stays: " + error); }
    }
}
