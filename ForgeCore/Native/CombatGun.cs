using System;
using Gear;
using Player;
using UnityEngine;

namespace ForgeCore;

public static partial class Combat
{
    /// <summary>Whether the player's own gun is lowered at idle (the relaxed pose). A shot or
    /// effect anchored on the muzzle must wait for it, or start from <see cref="GunPose.RaisedMuzzle"/>.</summary>
    public static bool GunLowered(PlayerAgent owner) => HolderLowered(owner.FPItemHolder);

    private static bool HolderLowered(FirstPersonItemHolder? holder)
    {
        if (holder == null) return false;
        return holder.ItemRelaxedTrigger ||
            holder.CurrentItemState?.TryCast<FirstPersonItem.FPIS_Relaxed>() != null;
    }

    /// <summary>Where the player's held gun shows its muzzle in the world, for a shot or an effect that starts there. The first-person model has its own
    /// field of view: its muzzle is put at the same screen point in the world camera's space; a gun still coming up from idle has its muzzle at the hip, so the
    /// raised muzzle is used; and the point is pulled in front of any level surface between <paramref name="eye"/> and it. Local player only (it reads the player's item cameras).</summary>
    public static Vector3 ShownMuzzle(BulletWeapon weapon, Vector3 eye)
    {
        var holder = weapon.Owner.FPItemHolder;
        var camera = holder.m_LookCamera;
        var muzzle = weapon.MuzzleAlign;
        if (camera == null || muzzle == null) throw new InvalidOperationException("The held gun has no camera or muzzle.");
        return ShownMuzzle(holder, camera, muzzle, eye);
    }

    /// <summary><see cref="ShownMuzzle(BulletWeapon, Vector3)"/> with the holder, its look camera and the muzzle already read and checked
    /// (a shot reads them once). Vector math stays managed: Il2CppInterop's Vector3 members are native calls.</summary>
    internal static Vector3 ShownMuzzle(FirstPersonItemHolder holder, Camera camera, Transform muzzle, Vector3 eye)
    {
        var itemCamera = holder.m_ItemLayerCamera;
        Vector3 start;
        if (HolderLowered(holder))
        {
            var look = camera.transform;
            start = GunPose.RaisedMuzzle(look.position, look.right, look.up, look.forward);
        }
        else start = itemCamera == null ? muzzle.position : camera.ViewportToWorldPoint(itemCamera.WorldToViewportPoint(muzzle.position));
        var toMuzzle = start - eye;
        var squared = toMuzzle.x * toMuzzle.x + toMuzzle.y * toMuzzle.y + toMuzzle.z * toMuzzle.z;
        if (squared <= 0.0001f) return start;
        var distance = MathF.Sqrt(squared);
        var along = toMuzzle / distance;
        if (Physics.Raycast(eye, along, out var obstruction, distance, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            start = obstruction.point - along * 0.025f;
        return start;
    }

    /// <summary>The player's fire input asks for the gun: the game's own idle-lowering timer restarts, which brings a relaxed gun back up.</summary>
    public static void RaiseGun(PlayerAgent owner) => owner.FPItemHolder?.DontRelax();
}
