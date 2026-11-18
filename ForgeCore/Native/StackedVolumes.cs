using System;
using System.Collections.Generic;
using AIGraph;
using GTFO.API;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeCore;

/// <summary>Room volumes that share a plan position or reach into a neighbour's grid cell. The game finds the geomorph volume of a
/// position by the 64 m grid cell alone (<c>AIG_GeomorphNodeVolume.TryGetGeomorphVolume</c>), so a room stacked above another, or one
/// spilling into the next cell, is answered with another room's volume. A package that places such rooms registers each one's world
/// extent and volume here; the one patch on that lookup then answers the volume whose extent holds the position (height included),
/// and every cell nothing was registered for keeps the game's answer at no cost beyond an empty-array test. Main thread only.</summary>
public static class StackedVolumes
{
    private static VolumeBoxes _boxes = new();
    private static LG_Geomorph[] _rooms = [];

    /// <summary>How many rooms are registered.</summary>
    public static int Count => _boxes.Count;

    /// <summary>Registers <paramref name="room"/> as the owner of the box from <paramref name="localMin"/> to <paramref name="localMax"/>
    /// measured in the room's own frame (turned by its yaw, so a room off the grid keeps its true footprint) in <paramref name="dimension"/>,
    /// and puts the lookup patch on (first call). The box is taken at the room's pose of this call; a room moved or turned after it was
    /// registered calls again, and the new box replaces the room's earlier one. The room's volume is read at lookup time: until the game
    /// has made it, the room takes no part in a choice.</summary>
    public static void Register(eDimensionIndex dimension, Vector3 localMin, Vector3 localMax, LG_Geomorph room)
    {
        if (room == null) throw new ArgumentNullException(nameof(room));
        Patches.Ensure(Modules.StackedVolumes, typeof(GeomorphVolumeLookupHook));
        var pose = room.transform; var origin = pose.position;
        var box = new VolumeBox((int)dimension, origin.x, origin.z, pose.eulerAngles.y * Mathf.Deg2Rad,
            localMin.x, origin.y + localMin.y, localMin.z, localMax.x, origin.y + localMax.y, localMax.z);
        var index = Array.FindIndex(_rooms, registered => registered.Pointer == room.Pointer);
        if (index >= 0) { _boxes.Replace(index, box); return; }
        _boxes.Add(box);
        _rooms = [.. _rooms, room];
    }

    /// <summary>Forgets every registration (level cleanup, or before a new generation).</summary>
    public static void Clear()
    {
        _boxes = new();
        _rooms = [];
    }

    /// <summary>The game's volume lookup of <paramref name="position"/> (<c>AIG_GeomorphNodeVolume.TryGetGeomorphVolume</c>, first floor)
    /// with the registrations applied; the one entry other packages call instead of the game's.</summary>
    public static bool Find(eDimensionIndex dimension, Vector3 position, out AIG_GeomorphNodeVolume? volume)
    {
        var found = AIG_GeomorphNodeVolume.TryGetGeomorphVolume(0, dimension, position, out var native);
        volume = found && native != null && !native.WasCollected ? native : null;
        return volume != null;
    }

    private static AIG_GeomorphNodeVolume? VolumeOf(int index)
    {
        var room = _rooms[index];
        if (room == null || room.WasCollected) return null;
        var volume = room.m_nodeVolume?.TryCast<AIG_GeomorphNodeVolume>();
        return volume == null || volume.WasCollected ? null : volume;
    }

    internal static bool TryPick(eDimensionIndex dimension, Vector3 position, out AIG_GeomorphNodeVolume? volume)
    {
        volume = null;
        if (_boxes.Count == 0) return false;
        var index = _boxes.Choose(0, static (_, i) => VolumeOf(i) != null, (int)dimension, position.x, position.y, position.z);
        if (index < 0) return false;
        volume = VolumeOf(index);
        return volume != null;
    }
}
/// <summary>The game's volume lookup writes "ERROR : Borken cell ... grouping or root" (a formatted line with a stack trace) on every call
/// for a position whose grid cell holds no room; the player's node update and the camera culler call it every frame, so a player outside
/// the map writes it every frame. On by default (<c>NAinfini.ForgeCore.cfg</c>, [Logs] BorkenCellOnce); off, nothing is patched for it.
/// On, from build done each such cell is written once per level: the game's body runs and writes the line, and later calls for that cell,
/// while it still holds no room, get the body's own answer for it (no volume, false) without the body. Every other call runs the body.</summary>
internal static class BorkenCells
{
    private static readonly Dictionary<eDimensionIndex, LG_Grid> Grids = new();
    private static readonly HashSet<IntPtr> Written = new();
    private static bool _live;

    internal static void Start()
    {
        if (!Settings.Bind(Plugin.Id, "Logs", "BorkenCellOnce", true,
                "Writes the game's \"Borken cell\" error once per grid cell and level instead of every frame while a player stands outside the rooms.").Value) return;
        LevelAPI.OnBuildStart += Forget;
        LevelAPI.OnLevelCleanup += Forget;
        LevelAPI.OnBuildDone += static () =>
        {
            try { Patches.Ensure(Modules.StackedVolumes, typeof(GeomorphVolumeLookupHook)); _live = true; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-borken-cell-hook-failed", "The Borken cell line stays every frame: " + error); }
        };
    }

    private static void Forget()
    {
        _live = false;
        Written.Clear();
        Grids.Clear();
    }

    /// <summary>The game has written this position's cell already and the cell still holds no room.</summary>
    internal static bool AlreadyWritten(eDimensionIndex dimension, Vector3 pos)
        => Written.Count != 0 && Grids.TryGetValue(dimension, out var grid) && CellAt(grid, pos) is { } cell
           && Written.Contains(cell.Pointer) && Roomless(cell);

    /// <summary>The game's body answered false: when that was a cell with no room (its line was just written), remember the cell.</summary>
    internal static void Refused(eDimensionIndex dimension, Vector3 pos)
    {
        if (!_live || Builder.CurrentFloor is not { } floor || !floor.GetDimension(dimension, out var native) || native?.Grid is not { } grid) return;
        if (CellAt(grid, pos) is not { } cell || !Roomless(cell)) return;
        Written.Add(cell.Pointer);
        Grids[dimension] = grid;
    }

    // The game's CellGridBase.GetCell(Vector3, out C) worked out by hand: its interop wrapper builds the out cell as `new C(pointer)` on the type
    // parameter, which throws MissingMethodException. GetCell(int, int) returns through the generic pointer conversion and answers null off the grid.
    private static LG_Cell? CellAt(LG_Grid grid, Vector3 pos)
    {
        var local = pos - grid.m_gridPosition;
        float dim = grid.m_cellDim, half = grid.m_cellDimHalf;
        return grid.GetCell(Mathf.RoundToInt((local.x - half) / dim), Mathf.RoundToInt((local.z - half) / dim));
    }

    // The body's own test: no grouping, or a grouping without a live root geomorph.
    private static bool Roomless(LG_Cell cell) => cell.m_grouping is not { } tile || tile.m_geoRoot == null;
}

/// <summary>The one patch on <c>AIG_GeomorphNodeVolume.TryGetGeomorphVolume</c>: a position inside a registered room's extent is answered
/// with that room's volume (<see cref="StackedVolumes"/>) and the game's body is skipped; a cell whose "Borken cell" line was already
/// written is answered as the body answers it (<see cref="BorkenCells"/>). It is a prefix because the game's own body logs "Borken cell ...
/// grouping or root" for every cell it cannot match, and a postfix would only fix the answer after that log.</summary>
internal static class GeomorphVolumeLookupHook
{
    [HarmonyPatch(typeof(AIG_GeomorphNodeVolume), nameof(AIG_GeomorphNodeVolume.TryGetGeomorphVolume))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(eDimensionIndex dimensionIndex, Vector3 pos, ref AIG_GeomorphNodeVolume resultingGeoVolume, ref bool __result, out bool __state)
    {
        __state = true;
        if (StackedVolumes.TryPick(dimensionIndex, pos, out var volume))
        {
            resultingGeoVolume = volume!;
            __result = true;
            return false;
        }
        try
        {
            if (BorkenCells.AlreadyWritten(dimensionIndex, pos))
            {
                resultingGeoVolume = null!;
                __result = false;
                return false;
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-borken-cell-check-failed", "Checking a Borken cell failed: " + error); }
        __state = false;
        return true;
    }

    [HarmonyPatch(typeof(AIG_GeomorphNodeVolume), nameof(AIG_GeomorphNodeVolume.TryGetGeomorphVolume))]
    [HarmonyPostfix]
    private static void Postfix(eDimensionIndex dimensionIndex, Vector3 pos, bool __result, bool __state, bool __runOriginal)
    {
        if (__state || __result || !__runOriginal) return;
        try { BorkenCells.Refused(dimensionIndex, pos); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-borken-cell-check-failed", "Checking a Borken cell failed: " + error); }
    }
}
