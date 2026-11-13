using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeCore;

/// <summary>How one content entry of a <see cref="NetManifest"/> stands between this peer and another player.</summary>
public enum ContentMatch : byte
{
    /// <summary>That player's list has not arrived (just joined, refused, or the mod is missing there).</summary>
    Unknown,
    /// <summary>Both have it with the same hash: usable between the two.</summary>
    Shared,
    /// <summary>Both have the number but the hashes differ: refused, neither side may use it as the other's.</summary>
    Mismatched,
    /// <summary>Only this peer has it: that player lacks it.</summary>
    OnlyHere,
    /// <summary>Only that player has it: this peer lacks it.</summary>
    OnlyThere,
}

/// <summary>What differs between this peer's list and another player's, each sorted by number.</summary>
public sealed class ContentDifferences
{
    internal ContentDifferences(List<uint> onlyHere, List<uint> onlyThere, List<uint> mismatched)
    {
        OnlyHere = onlyHere;
        OnlyThere = onlyThere;
        Mismatched = mismatched;
    }

    /// <summary>Numbers this peer has and that player lacks.</summary>
    public IReadOnlyList<uint> OnlyHere { get; }
    /// <summary>Numbers that player has and this peer lacks.</summary>
    public IReadOnlyList<uint> OnlyThere { get; }
    /// <summary>Numbers both have with different hashes.</summary>
    public IReadOnlyList<uint> Mismatched { get; }
    public bool None => OnlyHere.Count == 0 && OnlyThere.Count == 0 && Mismatched.Count == 0;
}

/// <summary>
/// A content list per player (<see cref="NetChannel.Manifest"/>): each player sets its own (content number, content hash) entries, the
/// channel exchanges them like a lobby value (again to a player who joins later, forgotten for one who left or was refused), and each
/// peer reads, per other player, which entries both have with the same hash (<see cref="Match"/>, <see cref="Usable"/>) and what differs
/// (<see cref="Differences"/>). Content that only matches in part is not a reason to refuse the channel: a mod keeps the handshake's
/// <see cref="NetChannel.Content"/> for what must match whole and lists entry by entry what may differ. On the wire: 12 bytes per entry
/// (number, hash), sorted by number. Main thread only.
/// </summary>
public sealed class NetManifest
{
    private const int EntryBytes = 12;
    private readonly NetLobbyValue _value;
    private readonly ModLog _log;
    private readonly string _modId;
    private Dictionary<uint, ulong> _own = new();
    private readonly Dictionary<ulong, Dictionary<uint, ulong>> _theirs = new();

    internal NetManifest(NetLobbyValue value, string modId, ModLog log)
    {
        _value = value;
        _modId = modId;
        _log = log;
        value.Changed += Received;
    }

    /// <summary>A player's list arrived, changed or went, or this peer's own list changed (raised for every player whose list is held).</summary>
    public event Action<ulong>? Changed;

    /// <summary>Sets this peer's own entries and sends them; a number listed twice is refused.</summary>
    public void Set(IEnumerable<(uint Id, ulong Hash)> entries)
    {
        var own = new Dictionary<uint, ulong>();
        foreach (var (id, hash) in entries)
            if (!own.TryAdd(id, hash)) throw new ArgumentException(_modId + " lists content " + id + " twice.", nameof(entries));
        _own = own;
        var data = new byte[own.Count * EntryBytes];
        var at = 0;
        foreach (var pair in own.OrderBy(p => p.Key))
        {
            BitConverter.TryWriteBytes(data.AsSpan(at), pair.Key);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), pair.Value);
            at += EntryBytes;
        }
        _value.Set(data);
        foreach (var player in _theirs.Keys.ToList()) Raise(player);
    }

    /// <summary>True once <paramref name="player"/>'s list is here.</summary>
    public bool Known(ulong player) => _theirs.ContainsKey(player);

    public ContentMatch Match(ulong player, uint id)
    {
        if (!_theirs.TryGetValue(player, out var theirs)) return ContentMatch.Unknown;
        var here = _own.TryGetValue(id, out var ownHash);
        var there = theirs.TryGetValue(id, out var theirHash);
        if (here && there) return ownHash == theirHash ? ContentMatch.Shared : ContentMatch.Mismatched;
        return here ? ContentMatch.OnlyHere : there ? ContentMatch.OnlyThere : ContentMatch.Unknown;
    }

    /// <summary>Both this peer and <paramref name="player"/> have <paramref name="id"/> with the same hash.</summary>
    public bool Usable(ulong player, uint id) => Match(player, id) == ContentMatch.Shared;

    /// <summary>What differs from <paramref name="player"/>'s list; null while it has not arrived.</summary>
    public ContentDifferences? Differences(ulong player)
    {
        if (!_theirs.TryGetValue(player, out var theirs)) return null;
        var onlyHere = new List<uint>();
        var mismatched = new List<uint>();
        foreach (var pair in _own)
        {
            if (!theirs.TryGetValue(pair.Key, out var hash)) onlyHere.Add(pair.Key);
            else if (hash != pair.Value) mismatched.Add(pair.Key);
        }
        var onlyThere = theirs.Keys.Where(id => !_own.ContainsKey(id)).ToList();
        onlyHere.Sort();
        onlyThere.Sort();
        mismatched.Sort();
        return new ContentDifferences(onlyHere, onlyThere, mismatched);
    }

    private void Received(ulong player)
    {
        if (!_value.TryGet(player, out var data))
        {
            if (_theirs.Remove(player)) Raise(player);
            return;
        }
        var theirs = Parse(data);
        if (theirs == null)
        {
            Net.Drop(Net.DropManifestMalformed);
            _log.ErrorOnce(Net.DropManifestMalformed, "Player " + player + " sent a malformed " + _modId + " content list (" + data.Length + " bytes); it was ignored.");
            if (_theirs.Remove(player)) Raise(player);
            return;
        }
        _theirs[player] = theirs;
        Raise(player);
    }

    private static Dictionary<uint, ulong>? Parse(byte[] data)
    {
        if (data.Length % EntryBytes != 0) return null;
        var entries = new Dictionary<uint, ulong>(data.Length / EntryBytes);
        for (var at = 0; at < data.Length; at += EntryBytes)
            if (!entries.TryAdd(BitConverter.ToUInt32(data, at), BitConverter.ToUInt64(data, at + 4))) return null;
        return entries;
    }

    private void Raise(ulong player)
    {
        try { Changed?.Invoke(player); }
        catch (Exception error) { _log.ErrorOnce("core-content-list-handler-failed", "A " + _modId + " content list handler failed: " + error); }
    }
}
