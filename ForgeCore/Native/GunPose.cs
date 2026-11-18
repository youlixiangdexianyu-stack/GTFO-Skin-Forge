using UnityEngine;

using System;
namespace ForgeCore;

/// <summary>Where a held gun's muzzle is once the gun is up, and a point kept in the shooter's yaw frame. Pure arithmetic; <see cref="Combat.GunLowered"/> reads the game's state.</summary>
public static class GunPose
{
    /// <summary>The raised hip muzzle in the look camera's frame: right, up and forward in metres.</summary>
    public static readonly Vector3 RaisedMuzzleOffset = new(0.08f, -0.08f, 0.6f);

    /// <summary>The muzzle of a raised gun, from the look camera's position and axes. A lowered gun's own muzzle hangs at the hip or lower;
    /// a shot or effect that begins while the gun is still coming up starts here instead.</summary>
    public static Vector3 RaisedMuzzle(Vector3 position, Vector3 right, Vector3 up, Vector3 forward)
        => position + right * RaisedMuzzleOffset.x + up * RaisedMuzzleOffset.y + forward * RaisedMuzzleOffset.z;

    /// <summary>A world offset from a shooter as right, up, forward in the yaw frame of <paramref name="facing"/> (its horizontal part).</summary>
    public static Vector3 ToYawFrame(Vector3 offset, Vector3 facing)
    {
        Axes(facing, out var right, out var forward);
        return new Vector3(offset.x * right.x + offset.z * right.z, offset.y, offset.x * forward.x + offset.z * forward.z);
    }

    /// <summary>The world offset of a point stored by <see cref="ToYawFrame"/>, for the shooter's current <paramref name="facing"/>.</summary>
    public static Vector3 FromYawFrame(Vector3 local, Vector3 facing)
    {
        Axes(facing, out var right, out var forward);
        return right * local.x + new Vector3(0f, local.y, 0f) + forward * local.z;
    }

    private static void Axes(Vector3 facing, out Vector3 right, out Vector3 forward)
    {
        var flat = new Vector3(facing.x, 0f, facing.z);
        var length = MathF.Sqrt(flat.x * flat.x + flat.z * flat.z);
        forward = length < 0.001f ? new Vector3(0f, 0f, 1f) : new Vector3(flat.x / length, 0f, flat.z / length);
        right = new Vector3(forward.z, 0f, -forward.x);
    }
}
