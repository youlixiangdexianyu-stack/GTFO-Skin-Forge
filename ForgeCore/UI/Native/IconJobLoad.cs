using UnityEngine;

namespace ForgeUI;

/// <summary>
/// What the game's <c>GearIconRendering.LoadGearForIconJob</c> leaves on a job, for a job whose model is supplied by a mod instead of
/// being assembled by the game. The native method makes one result per requested picture, each naming the gear, marks the job as loading
/// and starts the assembly, whose callback sets the loaded object and marks the job loaded. The renderer reads every one of these: a job
/// without its results throws in <c>RenderIconJob</c> on every frame and, since the renderer works one job at a time, no later icon of
/// any gear is ever drawn.
/// </summary>
internal static class IconJobLoad
{
    /// <summary>Everything the native load writes before the assembly: the results and the loading mark.</summary>
    internal static void Begin(GearIconRendering.IconRenderJob job)
    {
        var count = job.datas.Length;
        var results = new IconRenderJobResult[count];
        for (var i = 0; i < count; i++) results[i] = new IconRenderJobResult { gearID = job.gearToRender };
        job.results = results;
        job.asyncLoadingStarted = true;
    }

    /// <summary>What the native assembly callback writes: the object to photograph and the loaded mark.</summary>
    internal static void Complete(GearIconRendering.IconRenderJob job, GameObject model)
    {
        job.loadedGearGO = model;
        job.asyncLoadingDone = true;
    }
}
