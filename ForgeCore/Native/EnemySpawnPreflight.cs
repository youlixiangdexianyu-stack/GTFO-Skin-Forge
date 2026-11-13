using System;
using GameData;
using UnityEngine;

namespace ForgeCore;

/// <summary>Why <see cref="EnemySpawn.Preflight"/> refuses an enemy before the allocator is asked.</summary>
public enum SpawnRefusal
{
    None,
    /// <summary>No enemy data block has that id.</summary>
    EnemyTypeMissing,
    /// <summary>A SquidBoss base prefab needs the arena objects (<c>StartArea</c>, <c>TopArea</c>, <c>MidArea</c>, <c>BotArea</c>) that only the native boss arena supplies.</summary>
    ArenaMissing,
}

public static partial class EnemySpawn
{
    /// <summary>Whether the allocator may be asked for <paramref name="enemyId"/> at all. <c>SquidBossBehaviour.Setup</c> (native RVA 0x15913f0)
    /// reads those <c>GameObject.Find</c> results unconditionally, so a boss without the native arena would be replicated half built.
    /// Every base prefab of the SquidBoss family counts (<c>SquidBossBase</c>, <c>SquidBossBase_Complex</c>, <c>SquidBossBase_VS</c>: each carries
    /// <c>SquidBossBehaviour</c> in the game files), whatever the block's <c>ArenaDimensions</c> list holds (it is empty for them).
    /// <paramref name="detail"/> names the missing object.</summary>
    public static SpawnRefusal Preflight(uint enemyId, out string? detail)
    {
        detail = null;
        var block = GameDataBlockBase<EnemyDataBlock>.GetBlock(enemyId);
        if (block == null) return SpawnRefusal.EnemyTypeMissing;
        if (block.BasePrefabs == null) return SpawnRefusal.None;
        foreach (var prefab in block.BasePrefabs)
            if (prefab != null && prefab.StartsWith(SquidBossBasePrefab, StringComparison.Ordinal))
                foreach (var anchor in SquidBossAnchors)
                    if (GameObject.Find(anchor) == null) { detail = anchor; return SpawnRefusal.ArenaMissing; }
        return SpawnRefusal.None;
    }

    private const string SquidBossBasePrefab = "Assets/AssetPrefabs/Characters/Enemies/Bases/SquidBossBase";
    private static readonly string[] SquidBossAnchors = { "StartArea", "TopArea", "MidArea", "BotArea" };
}
