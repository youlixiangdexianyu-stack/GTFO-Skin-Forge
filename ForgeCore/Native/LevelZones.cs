using GameData;
using LevelGeneration;
using UnityEngine;

namespace ForgeCore;

public static partial class Level
{
    /// <summary>The level is built: its dimensions are read once from the game's own list (<c>LG_Floor.m_dimensions</c>), never by asking
    /// for an index the level may not have (the game logs an error with a stack trace for each one).</summary>
    internal static void ReadDimensions()
    {
        ulong present = 0;
        var floor = Builder.CurrentFloor;
        var list = floor == null || floor.WasCollected ? null : floor.m_dimensions;
        if (list != null)
            foreach (var dimension in list)
            {
                if (dimension == null || dimension.WasCollected) continue;
                var index = (int)dimension.DimensionIndex;
                if (index >= 0 && index < MaxDimensions) present |= 1UL << index;
            }
        SetDimensions(present);
        if (present == 0) Plugin.Logger.Error("core-built-level-lists-no-dimension-lg", "The built level lists no dimension (LG_Floor.m_dimensions); course node lookups find nothing in this level.");
    }

    /// <summary>The zone a level event names with <c>DimensionIndex</c>, <c>Layer</c> and <c>LocalIndex</c> (the game's
    /// <c>LG_Floor.TryGetZoneByLocalIndex</c>). False, with a null zone, when the built level has no such zone.</summary>
    public static bool TryZone(int dimension, int layer, int localIndex, out LG_Zone? zone)
    {
        zone = null;
        var floor = Builder.CurrentFloor;
        if (floor == null || floor.WasCollected || dimension < 0 || layer < 0 || localIndex < 0) return false;
        if (!floor.TryGetZoneByLocalIndex((eDimensionIndex)dimension, (LG_LayerType)layer, (eLocalZoneIndex)localIndex, out var found) || found == null || found.WasCollected) return false;
        zone = found;
        return true;
    }

    /// <summary>How many areas <paramref name="zone"/> has, in the zone's own order (<c>LG_Zone.m_areas</c>).</summary>
    public static int AreaCount(LG_Zone zone) => zone.m_areas?.Count ?? 0;

    /// <summary>The centre of the zone's area <paramref name="index"/> (0-based): its course node's position. False for an index out
    /// of range or an area without a course node.</summary>
    public static bool TryAreaCentre(LG_Zone zone, int index, out Vector3 centre)
    {
        centre = default;
        var areas = zone.m_areas;
        if (areas == null || index < 0 || index >= areas.Count) return false;
        var node = areas[index]?.m_courseNode;
        if (node == null || node.WasCollected) return false;
        centre = node.Position;
        return true;
    }
}
