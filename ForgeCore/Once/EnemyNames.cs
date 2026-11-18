using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>What players call an enemy, from its data block name: the game's own blocks (build 34873, the names the Forge catalog lists)
/// map to a family whose language key in ForgeCore's language files is <c>enemy.</c> and the family; a block the game does not ship
/// (a custom rundown's) has no key and is shown by its block name. Pure code: tools compile this file straight in.</summary>
public static class EnemyNames
{
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Striker_Wave"] = "striker",
        ["Striker_Hibernate"] = "striker",
        ["Striker_Patrol"] = "striker",
        ["Striker_Wave_Fast"] = "fast-striker",
        ["Shooter_Wave"] = "shooter",
        ["Shooter_Hibernate"] = "shooter",
        ["Striker_Big_Wave"] = "giant",
        ["Striker_Big_Hibernate"] = "giant",
        ["Striker_Boss"] = "giant",
        ["Shooter_Big"] = "big-shooter",
        ["Shooter_Big_Infection"] = "big-shooter",
        ["Shooter_Big_RapidFire"] = "hybrid",
        ["Scout"] = "scout",
        ["Scout_Shadow"] = "shadow-scout",
        ["Scout_Bullrush"] = "charger-scout",
        ["Scout_zoomer"] = "zoomer-scout",
        ["Scout_nightmare"] = "nightmare-scout",
        ["Shadow"] = "shadow",
        ["Striker_Big_Shadow"] = "big-shadow",
        ["Striker_Bullrush"] = "charger",
        ["Striker_Big_Bullrush"] = "big-charger",
        ["Striker_Child"] = "baby",
        ["Striker_Child_Nightmare"] = "nightmare-baby",
        ["Striker_Berserk"] = "nightmare-striker",
        ["Shooter_Spread"] = "nightmare-shooter",
        ["Striker_Big_nightmare"] = "nightmare-giant",
        ["Birther"] = "mother",
        ["Birther_Boss"] = "big-mother",
        ["MegaMother"] = "mega-mother",
        ["Tank"] = "tank",
        ["Tank_Boss"] = "immortal",
        ["Flyer"] = "flyer",
        ["Flyer_Big"] = "big-flyer",
        ["Pouncer"] = "snatcher",
        ["Squidward"] = "kraken",
        ["SquidBoss_Big"] = "kraken",
        ["SquidBoss_Big_Complex"] = "kraken",
        ["SquidBoss_VS"] = "kraken",
        ["Cocoon"] = "cocoon",
    };

    /// <summary>The language key of the enemy named <paramref name="blockName"/>, or null for a block the game does not ship.</summary>
    public static string? Key(string? blockName) => blockName != null && Families.TryGetValue(blockName, out var family) ? "enemy." + family : null;
}
