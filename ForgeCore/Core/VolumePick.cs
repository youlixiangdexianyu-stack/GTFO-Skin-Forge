using System;

namespace ForgeCore;

/// <summary>The extent of one registered room volume in one dimension: a box in the room's own plan frame (X and Z measured from the
/// room's origin, turned by its yaw) and a height range in world Y. A room turned off the grid keeps its true footprint, so a point
/// 2 m past its doorway is not claimed by the room's axis-aligned bounds.</summary>
internal readonly struct VolumeBox
{
    /// <summary>An axis-aligned world box (no turn, origin at the world origin).</summary>
    public VolumeBox(int dimension, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        : this(dimension, 0f, 0f, 0f, minX, minY, minZ, maxX, maxY, maxZ) { }

    /// <summary>A box measured in the plan frame whose origin is (<paramref name="originX"/>, <paramref name="originZ"/>) and which is
    /// turned by <paramref name="yaw"/> radians about the height axis (the game's convention: local +Z at yaw 0 is world +Z, a positive yaw turns it toward +X);
    /// X and Z are local, Y is world.</summary>
    public VolumeBox(int dimension, float originX, float originZ, float yaw, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        Dimension = dimension;
        OriginX = originX; OriginZ = originZ; Cos = MathF.Cos(yaw); Sin = MathF.Sin(yaw);
        MinX = minX; MinY = minY; MinZ = minZ;
        MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
    }

    public int Dimension { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float Cos { get; }
    public float Sin { get; }
    public float MinX { get; }
    public float MinY { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxY { get; }
    public float MaxZ { get; }

    internal float Size => (MaxX - MinX) * (MaxY - MinY) * (MaxZ - MinZ);

    internal bool ContainsXz(float x, float z)
    {
        float dx = x - OriginX, dz = z - OriginZ;
        float localX = dx * Cos - dz * Sin, localZ = dx * Sin + dz * Cos;
        return localX >= MinX && localX <= MaxX && localZ >= MinZ && localZ <= MaxZ;
    }

    internal bool ContainsY(float y) => y >= MinY && y <= MaxY;

    /// <summary>Distance in height from <paramref name="y"/> to the box (0 inside).</summary>
    internal float GapY(float y) => y < MinY ? MinY - y : y > MaxY ? y - MaxY : 0f;
}
/// <summary>Which of several room volumes that share a plan position a point belongs to. The game finds a volume by the grid cell alone
/// and never looks at height, so rooms stacked above one another, or reaching into a neighbour's cell, are answered with the wrong volume.</summary>
internal static class VolumePick
{
    /// <summary>The index in <paramref name="boxes"/> of the volume that holds the point, or -1 when the game's own answer stands.
    /// A box that contains the point wins (the smallest when several do, the one nearest in height centre among equals); with no containing box, of two or more
    /// boxes over the same plan position the nearest in height; a single box over the position that does not contain the point leaves
    /// the game's answer. Allocation-free.</summary>
    public static int Choose(ReadOnlySpan<VolumeBox> boxes, int dimension, float x, float y, float z)
    {
        var inside = -1; var insideSize = float.MaxValue; var insideCentre = float.MaxValue;
        var near = -1; var nearGap = float.MaxValue; var overPlan = 0;
        for (var i = 0; i < boxes.Length; i++)
        {
            ref readonly var box = ref boxes[i];
            if (box.Dimension != dimension || !box.ContainsXz(x, z)) continue;
            overPlan++;
            if (box.ContainsY(y))
            {
                var centre = Math.Abs(y - (box.MinY + box.MaxY) * .5f);
                var size = box.Size;
                if (size < insideSize || (size == insideSize && centre < insideCentre)) { insideSize = size; insideCentre = centre; inside = i; }
            }
            var gap = box.GapY(y);
            if (gap < nearGap) { nearGap = gap; near = i; }
        }
        if (inside >= 0) return inside;
        return overPlan >= 2 ? near : -1;
    }
}

/// <summary>The registered boxes of rooms whose volume may not exist yet: a box takes part in a choice only once its volume exists
/// (asked on every choice until it does), so a room registered early is skipped, never refused, until the game has made its volume.</summary>
internal sealed class VolumeBoxes
{
    private VolumeBox[] _real = [];
    private VolumeBox[] _live = [];
    private bool[] _ready = [];

    public int Count => _real.Length;

    public int Add(VolumeBox box)
    {
        _real = [.. _real, box];
        _live = [.. _live, new VolumeBox(-1, 0, 0, 0, 0, 0, 0)];
        _ready = [.. _ready, false];
        return _real.Length - 1;
    }

    /// <summary>Replaces the box at <paramref name="index"/> (a room that moved after it was registered). A box already live is
    /// replaced at once; one still waiting for its volume takes the new box when the volume exists.</summary>
    public void Replace(int index, VolumeBox box)
    {
        _real[index] = box;
        if (_ready[index]) _live[index] = box;
    }

    /// <summary>The index of the box that holds the point among those whose volume exists (<paramref name="exists"/> is asked of
    /// the still waiting ones), or -1 for the game's own answer.</summary>
    public int Choose<TState>(TState state, Func<TState, int, bool> exists, int dimension, float x, float y, float z)
    {
        for (var i = 0; i < _ready.Length; i++)
            if (!_ready[i] && exists(state, i)) { _ready[i] = true; _live[i] = _real[i]; }
        return VolumePick.Choose(_live, dimension, x, y, z);
    }
}
