using System;
using AIGraph;
using UnityEngine;

namespace ForgeCore;

/// <summary>The level as the game's navigation holds it: which dimensions it has, which course node and which dimension a point belongs
/// to, and where a standing player can be put down (<see cref="FindStandSpot"/>). Main thread only.</summary>
public static partial class Level
{
    // The dimensions the built level has, bit i for dimension index i: set when the level is built, cleared at cleanup. Only these are
    // asked for a course node; the game logs an error with a full stack trace for every dimension index the level does not have.
    private static ulong _dimensions;
    private const int MaxDimensions = 64;

    /// <summary>The level now has exactly the dimensions in <paramref name="present"/> (bit i: dimension index i); 0 when no level is built.</summary>
    internal static void SetDimensions(ulong present) => _dimensions = present;

    /// <summary>The expedition the game has active: the rundown block the process loaded (<c>Global.RundownIdToLoad</c>), the tier (the
    /// game's <c>eRundownTier</c> value) and the zero-based index inside the tier (<c>RundownManager.GetActiveExpeditionData</c>). False
    /// when the game cannot say, for example between levels or with no rundown loaded; the outputs are then 0.</summary>
    public static bool TryActiveExpedition(out uint rundownId, out int tier, out int index)
    {
        rundownId = 0; tier = 0; index = 0;
        try
        {
            var active = RundownManager.GetActiveExpeditionData();
            rundownId = Globals.Global.RundownIdToLoad; tier = (int)active.tier; index = active.expeditionIndex;
            return true;
        }
        catch (Exception error)
        {
            ReportExpedition(error);
            rundownId = 0; tier = 0; index = 0; return false;
        }
    }

    /// <summary>The game's key string of the active expedition (<c>RundownManager.ActiveExpeditionUniqueKey</c>, such as <c>Local_31_TierA_0</c>);
    /// null when the game cannot say. The game builds a new string on every read, so a caller that asks often compares the numbers of
    /// <see cref="TryActiveExpedition"/> and reads this only when they change.</summary>
    public static string? ActiveKey
    {
        get
        {
            try { return RundownManager.ActiveExpeditionUniqueKey; }
            catch (Exception error)
            {
                ReportExpedition(error);
                return null;
            }
        }
    }

    // Both reads run often while no expedition is active; the first failure is written, the rest cost nothing.
    private static bool _expeditionReported;

    private static void ReportExpedition(Exception error)
    {
        if (_expeditionReported) return;
        _expeditionReported = true;
        Plugin.Logger.ErrorOnce("core-game-could-not-name-active-expedition", "The game could not name the active expedition: " + error);
    }

    /// <summary>True when the built level has dimension <paramref name="dimension"/> (0 is reality).</summary>
    public static bool HasDimension(int dimension) => dimension >= 0 && dimension < MaxDimensions && (_dimensions & (1UL << dimension)) != 0;

    /// <summary>The course node that holds <paramref name="position"/> (the game's <c>AIG_CourseNode.TryGetCourseNode</c> with
    /// <paramref name="sample"/> as the search distance) and the index of its dimension. <paramref name="preferredDimension"/> is
    /// asked first (pass -1 for none), then every other dimension the level has, in index order. False, with a null node and dimension
    /// -1, when no dimension of the level has a live node there (a point off every navigation mesh, or no level built).</summary>
    public static bool CourseNodeAt(Vector3 position, float sample, out AIG_CourseNode? node, out int dimension, int preferredDimension = -1)
    {
        if (HasDimension(preferredDimension) && Try(preferredDimension, position, sample, out node)) { dimension = preferredDimension; return true; }
        var count = Math.Min((int)eDimensionIndex.MAX_COUNT, MaxDimensions);
        for (var d = 0; d < count; d++)
        {
            if (d == preferredDimension || !HasDimension(d) || !Try(d, position, sample, out node)) continue;
            dimension = d;
            return true;
        }
        node = null;
        dimension = -1;
        return false;
    }

    /// <summary>The live course node of dimension <paramref name="dimension"/> that holds <paramref name="position"/> (<paramref name="sample"/> is the search distance); false, with a null node, when that dimension has none there. Unlike <see cref="CourseNodeAt"/> no other dimension is tried.</summary>
    public static bool CourseNodeIn(int dimension, Vector3 position, float sample, out AIG_CourseNode? node) => Try(dimension, position, sample, out node);

    /// <summary>The solid level floor straight below <paramref name="from"/> within <paramref name="distance"/> metres (the level's own
    /// layers, trigger volumes ignored and not counted). False, with a default hit, when there is none.</summary>
    public static bool FloorBelow(Vector3 from, float distance, out RaycastHit hit) =>
        Physics.Raycast(from, Vec.Down, out hit, distance, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)
        && hit.collider != null && !hit.collider.isTrigger;

    /// <summary>A plain ray straight down from <paramref name="from"/> against <paramref name="mask"/> (-1 for every layer), the global
    /// trigger setting applying: the game's own ground ray, not the level-floor query.</summary>
    public static bool GroundRay(Vector3 from, float distance, int mask, out RaycastHit hit) =>
        Physics.Raycast(from, Vec.Down, out hit, distance, mask);
    private static bool Try(int dimension, Vector3 position, float sample, out AIG_CourseNode? node)
    {
        if (AIG_CourseNode.TryGetCourseNode((eDimensionIndex)dimension, position, sample, out var found) && found != null && !found.WasCollected)
        {
            node = found;
            return true;
        }
        node = null;
        return false;
    }
}
