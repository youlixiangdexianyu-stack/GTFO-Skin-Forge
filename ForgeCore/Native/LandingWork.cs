using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>Work that makes something ready before its first use (a shader drawn once off screen, a large texture made) and has to wait for the
/// game's picture: it runs once the elevator has stopped or the level runs, one step on each frame, in the order the steps were added
/// (<see cref="Tick"/>, from ForgeCore's driver). The UI's and the visual part's warm-ups add their steps here, so they never land on the same frame.
/// Main thread only.</summary>
internal static class LandingWork
{
    private static readonly Queue<(string Owner, string Name, Action Run)> Steps = new();

    /// <summary>Queues <paramref name="step"/>; <paramref name="owner"/> is the part's ID and <paramref name="name"/> names the step in the error report. A step that throws is
    /// reported once under ForgeCore and the steps after it still run.</summary>
    internal static void Add(string owner, string name, Action step) => Steps.Enqueue((owner, name, step));

    internal static void Tick()
    {
        if (Steps.Count == 0) return;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.StopElevatorRide && state != eGameStateName.InLevel) return;
        var (owner, name, run) = Steps.Dequeue();
        try { run(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-landing-step-failed", "The landing step " + owner + "/" + name + " failed: " + error); }
    }
}
