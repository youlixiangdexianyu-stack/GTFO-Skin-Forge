using System;
using AIGraph;
using HarmonyLib;
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
/// <summary>The one patch on <c>AIG_GeomorphNodeVolume.TryGetGeomorphVolume</c>: a position inside a registered room's extent is answered
/// with that room's volume (<see cref="StackedVolumes"/>) and the game's body is skipped. It is a prefix because the game's own body
/// logs "Borken cell ... grouping or root" for every cell it cannot match, and a postfix would only fix the answer after that log.</summary>
internal static class GeomorphVolumeLookupHook
{
    [HarmonyPatch(typeof(AIG_GeomorphNodeVolume), nameof(AIG_GeomorphNodeVolume.TryGetGeomorphVolume))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(eDimensionIndex dimensionIndex, Vector3 pos, ref AIG_GeomorphNodeVolume resultingGeoVolume, ref bool __result)
    {
        if (!StackedVolumes.TryPick(dimensionIndex, pos, out var volume)) return true;
        resultingGeoVolume = volume!;
        __result = true;
        return false;
    }
}
