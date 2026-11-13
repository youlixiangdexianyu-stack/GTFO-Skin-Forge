using HarmonyLib;

namespace ForgeCore;

/// <summary>The one patch on <c>ItemReplicationManager.OnItemSpawn</c>: after every other postfix, <see cref="Items.Spawned"/> reports the
/// spawn, so no other package patches that method. The game's own keys are left alone: a pickup spawned in play gets its state replicator
/// from the game's self managed counter (the game logs its "self amanged replicators during play" line), because a key of the dynamic
/// range on a self managed replicator is lost by <c>SNet_Replication.ReconstructIds</c> after a checkpoint reload.</summary>
internal static class ItemSpawnHook
{
    internal static void Start() => Patches.Ensure(Modules.ItemSpawned, typeof(ItemSpawnHook));

    [HarmonyPatch(typeof(ItemReplicationManager), nameof(ItemReplicationManager.OnItemSpawn))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void SpawnPostfix(pItemSpawnData __0, ItemReplicator __1) => Items.RaiseSpawned(__0, __1);
}
