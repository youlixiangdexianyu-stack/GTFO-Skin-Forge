using System;
using GTFO.API;
using AssetShards;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ForgeCore;

/// <summary>What the game's shard tables say about one shard at one moment: whether a manifest is registered, whether the
/// dependency table has a count, and the identity of the tables and the manifest (so two readings can tell a stable
/// registration from a table or manifest that was replaced in between).</summary>
public readonly struct GameShardState
{
    public GameShardState(IntPtr manifests, IntPtr counts, IntPtr manifestPointer, bool hasManifest, bool hasCount, int count)
    {
        Manifests = manifests; Counts = counts; ManifestPointer = manifestPointer;
        HasManifest = hasManifest; HasCount = hasCount; Count = count;
    }

    public IntPtr Manifests { get; }
    public IntPtr Counts { get; }
    public IntPtr ManifestPointer { get; }
    /// <summary>The manifest table has the shard's key.</summary>
    public bool HasManifest { get; }
    /// <summary>The manifest table has the key and its manifest object is alive: the shard is loaded.</summary>
    public bool Registered => HasManifest && ManifestPointer != IntPtr.Zero;
    public bool HasCount { get; }
    /// <summary>The dependency count, 0 when the table has no key.</summary>
    public int Count { get; }
    public bool SameTables(GameShardState other) => Manifests == other.Manifests && Counts == other.Counts;
    public bool SameRegistration(GameShardState other) => Registered && other.Registered
        && SameTables(other) && ManifestPointer == other.ManifestPointer;
}

/// <summary>
/// What GTFO-API's <c>AssetAPI</c> does not give: a loaded asset that is null, not an exception, when its shard is not loaded,
/// and the game's shard state and loader. Whether an asset is loaded is <c>AssetAPI.ContainsAsset</c>.
/// A shard is loaded only through the game's loader, which resets the count of an unregistered key to 1: callers decide
/// from <see cref="ReadShard"/> whether somebody else's load is in flight before they start one.
/// </summary>
public static class GameAssets
{
    /// <summary>The loaded asset at <paramref name="path"/>, or null when its shard is not loaded or the object was collected.</summary>
    public static T? Get<T>(string path) where T : UnityEngine.Object
    {
        if (!AssetAPI.ContainsAsset(path)) return null;
        var asset = AssetAPI.GetLoadedAsset<T>(path);
        return asset == null || asset.WasCollected ? null : asset;
    }

    /// <summary>The shard's manifest and dependency-count state.</summary>
    public static GameShardState ReadShard(string shard)
    {
        var manifests = AssetShardManager.s_loadedManifests
            ?? throw new InvalidOperationException("The native shard manifest table is unavailable.");
        var counts = AssetShardManager.s_loadedShardDependencyCount
            ?? throw new InvalidOperationException("The native shard dependency table is unavailable.");
        // The interop Dictionary.TryGetValue wrapper marshals out TValue as an Il2Cpp object, even for int; the indexer does not.
        bool hasManifest = manifests.ContainsKey(shard);
        var manifest = hasManifest ? manifests[shard] : null;
        bool hasCount = counts.ContainsKey(shard);
        return new(manifests.Pointer, counts.Pointer, manifest?.Pointer ?? IntPtr.Zero, hasManifest, hasCount, hasCount ? counts[shard] : 0);
    }

    public static string ShardName(AssetBundleName bundle, AssetBundleShard shard) => AssetShardManager.GetShardName(bundle, shard);

    /// <summary>The game's loader for a shard. True: a scene load started and <paramref name="done"/> fires when it ends. False:
    /// the manifest was already registered, the game incremented the dependency count itself and queued the callback.</summary>
    public static bool LoadShard(string shard, Action<AsyncOperation> done)
        => AssetShardManager.LoadShardAsync(shard, done, LoadSceneMode.Additive);

    public static bool LoadShard(AssetBundleName bundle, AssetBundleShard shard, Action<AsyncOperation> done)
        => AssetShardManager.LoadShardAsync(bundle, shard, done, LoadSceneMode.Additive);

    /// <summary>One more dependency on a registered shard; balance it with <see cref="UnloadShard"/>.</summary>
    public static void IncreaseShardDependency(string shard) => AssetShardManager.IncreaseShardDependencyCount(shard);

    /// <summary>Drops one dependency (the game decrements and unloads at zero); <paramref name="reason"/> names the caller in the game's log.</summary>
    public static void UnloadShard(string shard, string reason) => AssetShardManager.UnloadShardAsync(shard, null, reason);
}
