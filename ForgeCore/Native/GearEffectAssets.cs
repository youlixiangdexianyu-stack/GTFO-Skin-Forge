using System;
using System.Collections.Generic;
using AssetShards;
using GameData;
using GTFO.API;
using GearIDRange = Gear.GearIDRange;
using eGearComponent = Gear.eGearComponent;
using BulletWeapon = Gear.BulletWeapon;

namespace ForgeCore;

/// <summary>Whether the assets the game reads while it assembles a gun are in the loaded-asset table.</summary>
public enum GearAssetStatus { Ready, Loading, Failed }

/// <summary>One look at a gun's assembly assets. <see cref="Detail"/> names what was read; on <see cref="GearAssetStatus.Failed"/> it names the asset or native fact.</summary>
public readonly record struct GearAssetReading(GearAssetStatus Status, string Detail = "");

/// <summary>
/// The one entry every path that makes the game build a gun goes through before it asks for the gun: the equip row, the test
/// Loadout fixture and its give-back, a level sentry, and the lobby host weapon. It brings in the assets the game indexes
/// while it assembles the gun, through the game's own shard loader, and reads the answer from the loaded-asset table itself
/// (a callback alone proves nothing).
///
/// What the game reads (Build 20403457, disassembly of the gear spawn callbacks; every <c>AssetShardManager.GetLoadedAsset</c>
/// indexes the table and throws KeyNotFoundException for a missing key, inside the spawn callback, so the equip never completes
/// and the slot keeps a half-built gun):
/// - <c>BulletWeapon.SetupEffects</c> reads <c>ItemEquippable.MuzzleFlashData.muzzleFlashFPS</c> (the block the gear's
///   <c>eGearComponent.MuzzleFlash</c> id names, set by <c>GearBuilder.AssembleGearAsync</c>), and, only while the static
///   <c>BulletWeapon.s_tracerPool</c> is still null, the constant path <see cref="TracerPath"/>.
/// - <c>BulletWeaponSynced.OnGearSpawnComplete</c> (a gun on another player or a bot) runs that setup first, then reads the same
///   block's <c>muzzleFlash3RD</c>.
/// - <c>SentryGunInstance_Firing_Bullets.Setup</c> reads the constant <see cref="SentryFlashPath"/> and, while its own static
///   tracer pool is null, <see cref="TracerPath"/>.
/// The shell casing block names only an enum, so it is not read from the table. The flashlight prefab is read when a gun is
/// wielded (<c>PlayerInventoryBase.OnItemEquippableFlashlightWielded</c>), not while it is assembled, and is not part of this entry.
///
/// A muzzle flash prefab lives in the shard its block names, which the game loads only for the gear a level started with, so a
/// missing one is loaded here once per shard and kept for the level, as the game keeps the shards of the gear it started with.
/// A shard that has a count or a manifest but not the prefab is somebody else's load in progress, so it is waited for, never
/// restarted: native <c>LoadShardAsync</c> resets the count of an unregistered key. The two constant effect prefabs belong to no
/// shard this code can name, so a missing one is reported as <see cref="GearAssetStatus.Failed"/> with its path, never loaded blind.
/// Every call is idempotent and keeps no state for the caller.
/// </summary>
internal static class GearEffectAssets
{
    public const string TracerPath = "Assets/AssetPrefabs/FX_Effects/FX_Tracer.prefab";
    public const string SentryFlashPath = "Assets/AssetPrefabs/Effects/Weapon/MuzzleFlash/Sentrygun_MuzzleFlash_3RD.prefab";

    private sealed class ShardLoad
    {
        internal bool Done;
        internal long StartedMs = Environment.TickCount64;
    }

    /// <summary>A load whose callback never came (the world it belonged to ended) is forgotten after this long.</summary>
    private const long AbandonedMs = 60_000;

    private static readonly Dictionary<string, ShardLoad> Loads = new(StringComparer.Ordinal);

    /// <summary>A held or deployed gun that is assembled as a <c>BulletWeapon</c> (or its synced form): its muzzle flash block's
    /// first- and third-person prefabs, and the tracer while the game has not yet made its pool. A gear whose block resolves to
    /// nothing (a melee weapon, a tool) reads none of these and is ready.</summary>
    public static GearAssetReading Prepare(GearIDRange gear)
    {
        ArgumentNullException.ThrowIfNull(gear);
        var flash = WeaponMuzzleFlashDataBlock.GetBlock(gear.GetCompID(eGearComponent.MuzzleFlash));
        // A Ready answer names what it looked at, so a gun that still throws in SetupEffects can be told apart.
        if (flash == null) return new(GearAssetStatus.Ready, "no-muzzle-flash-block id=" + gear.GetCompID(eGearComponent.MuzzleFlash));
        var shard = GameAssets.ShardName(flash.AssetBundleName, flash.AssetBundleShard);
        var status = GearAssetStatus.Ready;
        foreach (var path in new[] { flash.muzzleFlashFPS, flash.muzzleFlash3RD })
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (AssetAPI.ContainsAsset(path)) continue;
            var request = Request(flash, shard, path);
            if (request.Status == GearAssetStatus.Failed) return request;
            status = GearAssetStatus.Loading;
        }
        if (status == GearAssetStatus.Ready) Loads.Remove(shard);
        var detail = "muzzle-flash fps=" + flash.muzzleFlashFPS + " 3rd=" + flash.muzzleFlash3RD;
        if (status != GearAssetStatus.Ready) return new(status, detail);
        if (BulletWeapon.s_tracerPool == null && !AssetAPI.ContainsAsset(TracerPath)) return Missing(TracerPath);
        return new(status, detail + " tracer=" + TracerPath);
    }

    /// <summary>A level or player sentry gun: the constant third-person flash and the tracer its firing setup reads.</summary>
    public static GearAssetReading PrepareSentry()
    {
        if (!AssetAPI.ContainsAsset(SentryFlashPath)) return Missing(SentryFlashPath);
        if (!AssetAPI.ContainsAsset(TracerPath)) return Missing(TracerPath);
        return new(GearAssetStatus.Ready, "sentry-flash=" + SentryFlashPath + " tracer=" + TracerPath);
    }

    private static GearAssetReading Missing(string path)
        => new(GearAssetStatus.Failed, "effect-asset-not-loaded: asset=" + path + " startupAssetsLoaded=" + AssetShardManager.StartupAssetsIsLoaded);

    private static GearAssetReading Request(WeaponMuzzleFlashDataBlock flash, string shard, string path)
    {
        if (Loads.TryGetValue(shard, out var load) && !load.Done && Environment.TickCount64 - load.StartedMs > AbandonedMs)
        {
            Loads.Remove(shard);
            load = null;
        }
        if (load != null)
        {
            if (!load.Done) return new(GearAssetStatus.Loading);
            // The shard finished loading and the prefab is still not in the table: the load itself failed.
            Loads.Remove(shard);
            return new(GearAssetStatus.Failed, "shard-loaded-without-asset: shard=" + shard + " asset=" + path);
        }

        var state = GameAssets.ReadShard(shard);
        if (state.HasManifest || state.HasCount && state.Count > 0)
            return new(GearAssetStatus.Loading);

        var started = new ShardLoad();
        Loads[shard] = started;
        Action<UnityEngine.AsyncOperation> loaded = _ => started.Done = true;
        try { GameAssets.LoadShard(flash.AssetBundleName, flash.AssetBundleShard, loaded); }
        catch (Exception error)
        {
            Loads.Remove(shard);
            return new(GearAssetStatus.Failed, "load-threw: shard=" + shard + " " + error.GetType().Name);
        }
        return new(GearAssetStatus.Loading);
    }
}
