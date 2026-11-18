using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>Where a <c>Game:</c> effect comes from, which is also how it is played.</summary>
internal enum GameSource : byte
{
    /// <summary>A prefab of the game's preload bundle, taken with <c>FX_Manager.GetPreloadedEffectPool(name)</c>; when the level has
    /// no such pool, the same prefab from the loaded asset shards (<see cref="GameEffect.AssetPath"/>) through
    /// <c>FX_Manager.GetEffectPool(prefab)</c>.</summary>
    PreloadedPool,
    /// <summary>A pooled effect prefab of the game's Resources table: <c>Resources.Load(path)</c> and <c>FX_Manager.GetEffectPool</c>.</summary>
    ResourcePrefab,
    /// <summary>A third-person muzzle flash from the startup shards (<c>AssetShardManager.GetLoadedAsset</c>), an
    /// <c>EX_SpriteMuzzleFlash</c> copy played with its own <c>Play</c>.</summary>
    MuzzleFlash,
}

/// <summary>One effect of the game's own catalog. <see cref="Line"/>: drawn from a point to an end (the tracer reads its far end from
/// <c>FX_Manager.EffectTargetPosition</c>).</summary>
internal sealed record GameEffect(string Item, GameSource Source, string Native, string? AssetPath, bool Line)
{
    internal string Name => VisualNames.Join(VisualNames.Game, Item);
}

/// <summary>The game's effects by name (<c>Game:FX_Tripmine</c>). They are in every player's game already and are taken from it at
/// run time; nothing is redistributed. The item is the game's own prefab name, with a space written as '_'.</summary>
internal static class GameCatalog
{
    private const string FxEffects = "Assets/AssetPrefabs/FX_Effects/";
    private const string Muzzle = "Assets/AssetPrefabs/Effects/Weapon/MuzzleFlash/";

    internal static readonly IReadOnlyList<GameEffect> All = new[]
    {
        Pooled("FX_Tripmine", FxEffects + "FX_Tripmine.prefab"),
        Pooled("FX_TripmineGlue", FxEffects + "FX_TripmineGlue.prefab"),
        Pooled("FX_Tracer", FxEffects + "FX_Tracer.prefab") with { Line = true },
        Pooled("FX_BulletImpact_Concrete"),
        Pooled("FX_BulletImpact_Metal"),
        Pooled("FX_BulletImpact_SheetMetal"),
        Pooled("FX_BulletImpact_Glass"),
        Pooled("FX_BulletImpact_Wood"),
        Pooled("FX_BulletImpact_Plastic"),
        Pooled("FX_BulletImpact_Mud"),
        Pooled("FX_BulletImpact_Gravel"),
        Pooled("FX_3D Blood Impact"),
        Pooled("FXC_MeshBloodBomb"),
        Pooled("FX_TorchCutting"),
        Pooled("FX_ElevatorSparks"),
        Pooled("FX_LockMelter"),
        Pooled("FX_InfectionSpit"),
        Resource("fxc_flyerdeath"),
        Resource("fxc_flyerbigdeath"),
        Resource("fx_flyer_teleport"),
        Resource("fx_flyer_big_teleport"),
        Resource("fxc_fleshbulb_pop"),
        Resource("fxc_fleshbulb_pop_boss"),
        Flash("AssaultRifle_MuzzleFlash_3RD"),
        Flash("Pistol_MuzzleFlash_3RD"),
        Flash("Shotgun_MuzzleFlash_3RD"),
        Flash("SubMachine_MuzzleFlash_3RD"),
        Flash("Sentrygun_MuzzleFlash_3RD"),
    };

    private static readonly Dictionary<string, GameEffect> ByName = Index();

    internal static bool TryGet(string? name, out GameEffect effect)
    {
        effect = null!;
        return name != null && ByName.TryGetValue(name, out effect!);
    }

    private static Dictionary<string, GameEffect> Index()
    {
        var table = new Dictionary<string, GameEffect>(StringComparer.Ordinal);
        foreach (var e in All)
        {
            if (VisualNames.CheckItem(e.Item) is { } why) throw new InvalidOperationException("Game catalog entry " + e.Native + ": " + why);
            if (!table.TryAdd(e.Name, e)) throw new InvalidOperationException("Game catalog lists " + e.Name + " twice.");
        }
        return table;
    }

    private static string Item(string native) => native.Replace(' ', '_');

    private static GameEffect Pooled(string prefab, string? path = null) => new(Item(prefab), GameSource.PreloadedPool, prefab, path, false);
    private static GameEffect Resource(string path) => new(Item(path), GameSource.ResourcePrefab, path, null, false);
    private static GameEffect Flash(string stem) => new(Item(stem), GameSource.MuzzleFlash, Muzzle + stem + ".prefab", Muzzle + stem + ".prefab", false);
}
