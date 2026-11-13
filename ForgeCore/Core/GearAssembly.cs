using System;

namespace ForgeCore;

/// <summary>The game assembles a gun's parts in a coroutine that spawns one part asset and then waits a frame
/// (<c>GearPartSpawner.SpawnAllParts</c>), so a gun of a dozen parts takes a dozen frames before it can be loaded and held. A swap in
/// the special slot (<see cref="LoanedGun"/>) finishes that coroutine at once: every remaining step runs now, in order, through the game's own enumerator.</summary>
internal static class GearAssembly
{
    /// <summary>More steps than any gear has parts: a coroutine still running after this many is not the part spawner's and is left alone.</summary>
    public const int MaxSteps = 256;

    /// <summary>Runs <paramref name="moveNext"/> until the coroutine reports it is done. Returns the steps it took (0: it was already
    /// done), or -1 when it was still running after <see cref="MaxSteps"/>.</summary>
    public static int Finish(Func<bool> moveNext) => Run(moveNext, MaxSteps);

    /// <summary>Runs <paramref name="moveNext"/> for at most <paramref name="maxSteps"/> steps (a part spawn each): the steps taken when
    /// the coroutine reported it is done, or -1 when it is still running, to be continued by a later call.</summary>
    public static int Run(Func<bool> moveNext, int maxSteps)
    {
        for (var steps = 0; steps < maxSteps; steps++)
            if (!moveNext()) return steps;
        return -1;
    }
}
