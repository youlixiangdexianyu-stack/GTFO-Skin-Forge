using System;
using Agents;
using Enemies;
using HarmonyLib;
using Player;

namespace ForgeCore;

/// <summary>A hold on a player's movement-noise multiplier taken with <see cref="PlayerNoise.Scale"/>: dispose it to end it.</summary>
public sealed class PlayerNoiseLease : IDisposable
{
    internal PlayerNoiseLease(int player, int id, float multiplier, string owner) { Player = player; Id = id; Multiplier = multiplier; Owner = owner; }
    internal int Player { get; }
    internal int Id { get; }
    public float Multiplier { get; }
    public string Owner { get; }
    /// <summary>True until the lease is disposed or the level is left.</summary>
    public bool Active => PlayerNoise.Held(this);
    public void Dispose() => PlayerNoise.End(this);
}

/// <summary>
/// How far sleeping enemies hear a player move. The game gives that no number of its own: an enemy hears a player's walk, run, jump
/// or landing inside its own <c>movementDetectionDistance</c> (<c>EnemyDetectionDataBlock</c>) and a crouched player inside a fixed
/// 1.9 m, each as the player's <c>Agent.NoiseType</c> says. <see cref="Scale"/> makes the enemies hear one player as if that distance
/// were <c>multiplier</c> times longer, for every one of those noises (<c>Sneak</c>, <c>Walk</c>, <c>Run</c>, <c>Jump</c>,
/// <c>LoudLanding</c>), by the game's own detection: the enemy still runs its whole native check, only the measured distance to that
/// player is divided for the length of that check. A shot, a melee hit and a decoy are not movement noise and are not scaled. Call it on
/// the host, where enemies detect. Main thread only; leases end with the level.
/// </summary>
public static class PlayerNoise
{
    private static readonly NoiseScaleBook Book = new();

    /// <summary>Multiplies <paramref name="player"/>'s movement-noise detection distance by <paramref name="multiplier"/> (above 0)
    /// until the lease is disposed. Several leases on one player never compound: the largest wins.</summary>
    public static PlayerNoiseLease Scale(PlayerAgent player, float multiplier, string owner)
    {
        if (player == null) throw new ArgumentNullException(nameof(player));
        Patches.Ensure(Modules.PlayerNoise, typeof(PlayerNoiseHooks));
        Book.Add(player.GlobalID, multiplier, out var id);
        return new PlayerNoiseLease(player.GlobalID, id, multiplier, owner);
    }

    internal static bool Held(PlayerNoiseLease lease) => Book.Has(lease.Player, lease.Id);
    internal static void End(PlayerNoiseLease lease) => Book.Remove(lease.Player, lease.Id);
    internal static void Clear() => Book.Clear();
    internal static bool Active => Book.Count != 0;
    internal static float Factor(int player, bool movementNoise) => Book.Factor(player, movementNoise);
}

/// <summary>Put on by <see cref="PlayerNoise.Scale"/> the first time it runs. The game measures a target's distance in
/// <c>EnemyDetection.UpdateDirectionDistanceToTarget_Hibernate</c> (build 20403457, no by-ref parameter) and the noise check that follows reads it
/// from <c>AgentTarget.m_distance</c>: this postfix divides that distance for a leased player's movement noise, so the game's own check
/// (and a mod reading the distance after it, ForgeEnemy's wake rules) sees the scaled value. The check method itself is not patched: its
/// <c>out float</c> parameter made Il2CppInterop's trampoline throw on every call (see <see cref="TrampolineSafety"/>). The distance is
/// measured again before every check, so nothing needs putting back.</summary>
internal static class PlayerNoiseHooks
{
    [HarmonyPatch(typeof(EnemyDetection), "UpdateDirectionDistanceToTarget_Hibernate")]
    [HarmonyPostfix]
    private static void Postfix(AgentTarget agentTarget)
    {
        if (!PlayerNoise.Active) return;
        try
        {
            var agent = agentTarget?.m_agent;
            if (agent == null) return;
            var noise = agent.Noise;
            var factor = PlayerNoise.Factor(agent.GlobalID, noise is Agent.NoiseType.Sneak or Agent.NoiseType.Walk or Agent.NoiseType.Run
                or Agent.NoiseType.Jump or Agent.NoiseType.LoudLanding);
            if (factor == 1f) return;
            var measured = agentTarget!.m_distance;
            agentTarget.m_distance = measured / factor;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-scaling-players-noise-distance-failed", "Scaling a player's noise distance failed: " + error); }
    }
}
