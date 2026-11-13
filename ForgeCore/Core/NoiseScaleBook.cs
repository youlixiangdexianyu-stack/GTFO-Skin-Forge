using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>
/// The multipliers behind <c>PlayerNoise.Scale</c>: for each player (the game's global ID), the leases held on it. Several leases on one
/// player never compound: the largest multiplier wins. The factor read on the hot path is one array element, so a detection pass
/// does no lookup, no string compare and no allocation. Movement noises are scaled; any other noise (a shot, a melee hit, a decoy) is
/// answered 1. Pure arithmetic, no game types.
/// </summary>
internal sealed class NoiseScaleBook
{
    private readonly float[] _factor;
    private readonly Dictionary<int, List<(int Id, float Multiplier)>> _leases = new();
    private int _nextId;

    public NoiseScaleBook(int playerIdCapacity = ushort.MaxValue + 1) => _factor = new float[playerIdCapacity];

    /// <summary>How many players currently carry a multiplier; 0 means every detection pass can return at once.</summary>
    public int Count => _leases.Count;

    /// <summary>True while lease <paramref name="id"/> of <paramref name="player"/> is held.</summary>
    public bool Has(int player, int id)
    {
        if (!_leases.TryGetValue(player, out var list)) return false;
        foreach (var lease in list) if (lease.Id == id) return true;
        return false;
    }

    /// <summary>Takes a lease multiplying <paramref name="player"/>'s movement noise by <paramref name="multiplier"/> (above 0);
    /// <paramref name="id"/> is the lease (always above 0). Returns the multiplier now in force for the player.</summary>
    public float Add(int player, float multiplier, out int id)
    {
        if ((uint)player >= (uint)_factor.Length) throw new ArgumentOutOfRangeException(nameof(player), "No such player ID.");
        if (!(multiplier > 0f) || float.IsInfinity(multiplier)) throw new ArgumentOutOfRangeException(nameof(multiplier), "The multiplier must be above 0.");
        if (!_leases.TryGetValue(player, out var list)) _leases[player] = list = new();
        id = ++_nextId;
        list.Add((id, multiplier));
        return _factor[player] = Largest(list);
    }

    /// <summary>Ends lease <paramref name="id"/>; false when it was not held.</summary>
    public bool Remove(int player, int id)
    {
        if (!Has(player, id)) return false;
        var list = _leases[player];
        list.RemoveAll(l => l.Id == id);
        if (list.Count == 0) { _leases.Remove(player); _factor[player] = 0f; }
        else _factor[player] = Largest(list);
        return true;
    }

    /// <summary>Forgets every lease (level left).</summary>
    public void Clear()
    {
        foreach (var player in _leases.Keys) _factor[player] = 0f;
        _leases.Clear();
    }

    /// <summary>The multiplier for a noise of <paramref name="player"/>: the player's multiplier for a movement noise, else 1.</summary>
    public float Factor(int player, bool movementNoise)
    {
        if (!movementNoise || (uint)player >= (uint)_factor.Length) return 1f;
        var factor = _factor[player];
        return factor == 0f ? 1f : factor;
    }

    private static float Largest(List<(int Id, float Multiplier)> list)
    {
        var largest = 0f;
        foreach (var lease in list) largest = Math.Max(largest, lease.Multiplier);
        return largest;
    }
}
