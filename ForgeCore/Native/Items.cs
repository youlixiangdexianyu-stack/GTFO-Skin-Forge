using System;
using AIGraph;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeCore;

/// <summary>Why <see cref="Items.Spawn"/> put nothing into the level.</summary>
public enum ItemSpawnRefusal
{
    /// <summary>It spawned; the replicator is returned.</summary>
    None,
    /// <summary>The game's item spawning is not ready (no spawn manager data, no replication manager).</summary>
    NotReady,
    /// <summary>The item has no prefab for the requested mode.</summary>
    ModeMissing,
    /// <summary>The call went through but the game's replication manager left no spawned instance.</summary>
    ResultUnavailable,
}

/// <summary>Spawning an item the way the game does: <c>ItemReplicationManager.SpawnItem</c>, whose replication manager leaves the
/// instance it just made in <c>m_internalSpawnCallbackReturnReplicator</c> (assigned after OnSpawn for every mode, null on
/// failure). The slot is cleared before the call and read straight after it, so the answer is exactly the instance this call made,
/// never a search for a matching item. Host only for a level item; main thread only.
/// <para><b>Pickups in play.</b> The game gives every pickup a state replicator with the next "self managed" key and logs "Its not
/// allowed to create self amanged replicators during play" for one made in play; that is the game's own behavior for every pickup it
/// spawns in play, and the key and the replicator stay the game's: a key of another range on a self managed replicator is not counted by
/// <c>SNet_Replication.ReconstructIds</c> after a checkpoint reload, so a later allocation can take it over. The new pickup's culler gets
/// no cull bucket either ("ItemCuller ... tried to MoveToNode() but CullBucket was null"); the game's culling job needs the level's zone
/// objects above the pickup, so a pickup made in play is simply not culled.</para></summary>
public static class Items
{
    private static Action<pItemSpawnData, ItemReplicator>? _spawned;

    /// <summary>Raised after the game ran <c>ItemReplicationManager.OnItemSpawn</c> for any item, on every player: the spawn data and the
    /// item's replicator (whose <c>Item</c> can still be null if the game failed to build it). It runs after every other patch of that
    /// method, once per spawn. A handler that
    /// throws is one <c>ErrorOnce</c> line and the others still run. This is the one patch of that game method; other packages
    /// subscribe. Main thread only.</summary>
    public static event Action<pItemSpawnData, ItemReplicator>? Spawned { add => _spawned += value; remove => _spawned -= value; }

    internal static void RaiseSpawned(pItemSpawnData data, ItemReplicator replicator)
        => Plugin.Logger.RaiseEach(_spawned, data, replicator, "core-items-spawned-handler-failed", "Items.Spawned");

    /// <summary>Spawns item <paramref name="itemId"/> in <paramref name="slot"/> at a pose inside <paramref name="node"/> (the origin
    /// node the game assigns); <paramref name="custom"/> is the item's custom state, if it has one. Returns the spawned instance, or
    /// null with the reason in <paramref name="refusal"/>.</summary>
    public static ItemReplicator? Spawn(uint itemId, InventorySlot slot, pItemData_Custom? custom, ItemMode mode, Vector3 position,
        Quaternion rotation, AIG_CourseNode node, out ItemSpawnRefusal refusal)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        var replication = ItemReplicationManager.Current?.m_SNetReplicationManager_Item;
        if (!ItemSpawnManager.ReadyToSpawnItems || replication == null || replication.WasCollected)
        {
            refusal = ItemSpawnRefusal.NotReady;
            return null;
        }
        if (!ItemSpawnManager.ItemHasMode(itemId, mode))
        {
            refusal = ItemSpawnRefusal.ModeMissing;
            return null;
        }
        var origin = default(pCourseNode);
        origin.Set(node);
        var data = new pItemData { itemID_gearCRC = itemId, slot = slot, originLayer = node.LayerType, originCourseNode = origin };
        if (custom is { } state) data.custom = state;
        replication.m_internalSpawnCallbackReturnReplicator = null;
        ItemReplicationManager.SpawnItem(data, null, mode, position, rotation, node, null);
        var spawned = replication.m_internalSpawnCallbackReturnReplicator;
        refusal = spawned == null || spawned.WasCollected ? ItemSpawnRefusal.ResultUnavailable : ItemSpawnRefusal.None;
        return refusal == ItemSpawnRefusal.None ? spawned : null;
    }

    /// <summary>Removes a spawned item the way the game does, <c>ItemReplicationManager.DeSpawn</c>: the host runs it and every peer
    /// removes the instance. A void submission, not a readback; the caller checks the item is gone if it needs to know.
    /// Host only; main thread only.</summary>
    public static void Despawn(IReplicator replicator) => ItemReplicationManager.DeSpawn(replicator);
}
