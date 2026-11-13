using UnityEngine;

namespace ForgeCore;

public static partial class Level
{
    /// <summary>How far the game's navigation mesh may lie from a point that stands on the level's floor: the mesh floats a little above
    /// the geometry it was baked from, so ground within this reach of a place counts as the place's ground.</summary>
    public const float SampleReach = 0.6f;
    private const float FloorProbe = 1.0f, MaxStep = 1.2f, CapsuleRadius = 0.3f, CapsuleLow = 0.45f, CapsuleHigh = 1.45f, SightHeight = 1.0f;

    /// <summary>A place a standing player can be put down around <paramref name="origin"/> (a suit, a pod, a lift) facing <paramref name="yaw"/>
    /// degrees: the first of the places at <paramref name="clearance"/> metres (straight behind first, then the back
    /// diagonals, the sides, further behind, the front; the mirror when <paramref name="preferBehind"/> is false) that the game itself says
    /// a body can stand on. A place passes when the game's navigation mesh has walkable ground within reach of it (<c>NavMesh.SamplePosition</c>,
    /// what the enemies walk on) no more than a step above or below <paramref name="origin"/>, the floor under it is found with a ray against
    /// the level (<c>LayerManager.MASK_WORLD</c>), a standing player's capsule there touches no level geometry, and nothing of the level lies
    /// between <paramref name="origin"/> and the place (no exit through a wall). <paramref name="tried"/> is the number of the place that
    /// passed (0 is the first tried); when none does the result is false, <paramref name="spot"/> is <paramref name="origin"/> and
    /// <paramref name="tried"/> is the number of places tried. Pure queries; nothing is allocated.</summary>
    public static bool FindStandSpot(Vector3 origin, float yaw, float clearance, bool preferBehind, out Vector3 spot, out int tried)
    {
        var mask = LayerManager.MASK_WORLD;
        for (tried = 0; tried < StandSpots.Count; tried++)
        {
            StandSpots.Offset(tried, yaw, clearance, preferBehind, out var dx, out var dz);
            var wanted = origin + new Vector3(dx, 0f, dz);
            if (!TrySnap(wanted, SampleReach, out var ground)) continue;
            if (Mathf.Abs(ground.y - origin.y) > MaxStep) continue;
            if (Physics.Raycast(ground + Vec.Up * FloorProbe, Vec.Down, out var floor, FloorProbe * 2f, mask, QueryTriggerInteraction.Ignore)) ground = floor.point;
            if (Physics.CheckCapsule(ground + Vec.Up * CapsuleLow, ground + Vec.Up * CapsuleHigh, CapsuleRadius, mask, QueryTriggerInteraction.Ignore)) continue;
            if (Physics.Linecast(origin + Vec.Up * SightHeight, ground + Vec.Up * SightHeight, mask, QueryTriggerInteraction.Ignore)) continue;
            spot = ground;
            return true;
        }
        spot = origin;
        return false;
    }
}
