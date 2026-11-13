using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore;

public static partial class Level
{
    // How far from a point the navigation mesh may be sampled to snap a route's ends onto it.
    private const float RouteSampleRadius = 2f;
    // Height above the floor of the line that checks a move; a move through a wall or closed door crosses it.
    private const float MoveLineHeight = 1f;
    // Height above the floor a line of sight starts from, lowered under a low ceiling.
    private const float LookHeight = 2f;
    private const int RouteBuffer = 64;

    private static NavMeshPath? _path;
    private static readonly Il2CppStructArray<Vector3> PathCorners = new(RouteBuffer);

    /// <summary>The nearest walkable ground on the game's navigation mesh (<c>NavMesh.SamplePosition</c>, every area type) within
    /// <paramref name="reach"/> metres of <paramref name="position"/>, written to <paramref name="snapped"/>. False, with <paramref name="snapped"/>
    /// equal to <paramref name="position"/>, when the mesh has none there (off the floor, another room beyond reach, no level built).</summary>
    public static bool TrySnap(Vector3 position, float reach, out Vector3 snapped)
    {
        var found = NavMesh.SamplePosition(position, out var hit, reach, -1);
        snapped = found ? hit.position : position;
        return found;
    }

    /// <summary>A complete path on the game's navigation mesh (<c>NavMesh.CalculatePath</c>, the surface the enemies walk on, closed doors
    /// included) from <paramref name="from"/> to <paramref name="to"/>, no longer than <paramref name="maxLength"/> metres, with every
    /// corner after the start written to <paramref name="corners"/> and their number to <paramref name="count"/>. Both ends are first
    /// snapped onto the mesh within 2 m. False, with a count of 0, when either end is off the mesh, the path is partial or blocked (a
    /// closed door, another room), it is longer than allowed, or it has more corners than <paramref name="corners"/> holds (a path is
    /// never cut short). Pure queries; the path object and corner buffer are reused, nothing is allocated.</summary>
    public static bool TryRoute(Vector3 from, Vector3 to, float maxLength, Span<Vector3> corners, out int count)
    {
        count = 0;
        if (!TrySnap(from, RouteSampleRadius, out var start) || !TrySnap(to, RouteSampleRadius, out var end)) return false;
        _path ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(start, end, -1, _path) || _path.status != NavMeshPathStatus.PathComplete) return false;
        var found = _path.GetCornersNonAlloc(PathCorners);
        if (found >= PathCorners.Length) return false;
        var length = 0f;
        for (var i = 1; i < found; i++)
        {
            length += Vec.Length(PathCorners[i] - PathCorners[i - 1]);
            if (length > maxLength || i > corners.Length) return false;
            corners[i - 1] = PathCorners[i];
        }
        count = Math.Max(0, found - 1);
        return true;
    }

    /// <summary>How far above <paramref name="point"/> a line may start: <paramref name="lift"/> metres, or just under the ceiling
    /// when one is lower than that (never below <paramref name="min"/>), so a line from an indoor floor point does not start in the
    /// slab above (<c>LayerManager.MASK_WORLD</c>, trigger volumes ignored).</summary>
    public static float LiftUnderRoof(Vector3 point, float lift, float min)
        => Physics.Raycast(point + Vec.Up * 0.1f, Vec.Up, out var roof, lift, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)
            ? Mathf.Max(min, roof.point.y - point.y - 0.15f) : lift;
    /// <summary>Whether nothing of the level (<c>LayerManager.MASK_WORLD</c>, trigger volumes ignored) lies between a point above
    /// <paramref name="floorPoint"/> and <paramref name="aim"/>. The line starts 2 m above the floor, or just under the ceiling when a
    /// ceiling is lower than that.</summary>
    public static bool HasSight(Vector3 floorPoint, Vector3 aim)
    {
        var lift = LiftUnderRoof(floorPoint, LookHeight, 0.2f);
        return !Physics.Linecast(floorPoint + Vec.Up * lift, aim, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore);
    }

    /// <summary>Whether the move from one floor point to another runs into the level: a line 1 m above both against
    /// <c>LayerManager.MASK_WORLD</c>, trigger volumes ignored.</summary>
    public static bool Blocked(Vector3 from, Vector3 to)
        => Physics.Linecast(from + Vec.Up * MoveLineHeight, to + Vec.Up * MoveLineHeight, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore);
}
