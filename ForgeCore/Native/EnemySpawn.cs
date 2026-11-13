using AIGraph;
using Agents;
using Enemies;
using UnityEngine;

namespace ForgeCore;

/// <summary>Spawning an enemy through the game's own allocator (<c>EnemyAllocator.SpawnEnemy</c>): the game builds the enemy, sets it
/// up, registers it for replication and starts it in the given mode. Main thread, host only (the game refuses on a client).</summary>
public static partial class EnemySpawn
{
    /// <summary>The game's allocator exists, so a request can be made.</summary>
    public static bool Ready => EnemyAllocator.Current is { WasCollected: false };

    /// <summary>One spawn request: enemy <paramref name="enemyId"/> (its data block id) at <paramref name="position"/> in <paramref name="node"/>,
    /// started in <paramref name="mode"/>, with no group. Returns what the game returns: the enemy, or null when it refused. Throws the
    /// game's own exception when its setup throws; null as well when <see cref="Ready"/> is false.</summary>
    public static EnemyAgent? Request(uint enemyId, AIG_CourseNode node, AgentMode mode, Vector3 position, Quaternion rotation)
        => Ready ? EnemyAllocator.Current!.SpawnEnemy(enemyId, node, mode, position, rotation, null, -1) : null;
}
