using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>One value per player of a mod's channel (<see cref="NetChannel.Lobby"/>): each player sets only their own, as bytes.
/// ForgeCore's world-scoped rows (readiness, <see cref="PlayerStateSlot{T}"/>) are the same row stamped with the world: such a value
/// from another world waits for this player's world to become that one, a new world drops every value (the local player's too),
/// and a host change keeps the local player's and sends it again. Main thread only.</summary>
public sealed class NetLobbyValue : NetChannel.IPart
{
    private const int HeadBytes = 7;
    private readonly NetChannel _channel;
    private readonly NetScope _scope;
    private readonly NetChannel.Route _route;
    private readonly Dictionary<ulong, (byte[] Value, int Version, float Arrived)> _values = new();
    private readonly Dictionary<ulong, (byte[] Framed, float Arrived)> _waiting = new();
    private readonly List<ulong> _players = new(4);
    private int _nextVersion;
    // Other players may hold a value of this player's (one was sent in this world), so a player owed this row also gets a clear.
    private bool _shown;

    internal NetLobbyValue(NetChannel channel, string name, NetScope scope, string layout)
    {
        _channel = channel;
        _scope = scope;
        Name = name;
        _route = channel.Claim(name, NetDelivery.Reliable, NetScope.Session, true, NetChannel.DefaultMaxBytes + HeadBytes, "row:" + scope + ":" + layout, Receive, this);
        channel.Add(this);
    }

    public string Name { get; }

    /// <summary>This player's own value, or null when it has none.</summary>
    public byte[]? Local { get; private set; }

    /// <summary>Changes whenever <see cref="Local"/> changes; 0 while there is none.</summary>
    public int LocalVersion { get; private set; }

    /// <summary>Raised with the player whose value arrived, changed or went (it left, was refused, or the world ended). The local
    /// player's own never raises it.</summary>
    public event Action<ulong>? Changed;

    /// <summary>What is sent for <see cref="Local"/> when it has to be written at the moment of sending (a value that carries "seconds ago").</summary>
    internal Func<byte[]>? Fresh;

    /// <summary>Sets this player's value (null clears it) and sends it to every compatible player; the same bytes again send nothing.</summary>
    public void Set(byte[]? value)
    {
        if (value == null ? Local == null : Local != null && value.AsSpan().SequenceEqual(Local)) return;
        Local = value == null ? null : (byte[])value.Clone();
        LocalVersion = Local == null ? 0 : ++_nextVersion;
        Send(0);
    }

    /// <summary>Another player's value as last received.</summary>
    public bool TryGet(ulong player, out byte[] value)
    {
        if (_values.TryGetValue(player, out var entry)) { value = entry.Value; return true; }
        value = Array.Empty<byte>();
        return false;
    }

    /// <summary>Changes whenever <paramref name="player"/>'s value changes; 0 while there is none.</summary>
    public int VersionOf(ulong player) => _values.TryGetValue(player, out var entry) ? entry.Version : 0;

    /// <summary>When <paramref name="player"/>'s value arrived, on this player's clock.</summary>
    internal float ArrivedAt(ulong player) => _values.TryGetValue(player, out var entry) ? entry.Arrived : 0f;

    /// <summary>On the wire: the sender's world, then 1 and the value, or 0 for none.</summary>
    private void Send(ulong to)
    {
        var value = Local == null ? null : Fresh != null ? Fresh() : Local;
        var world = _channel.Node.World;
        var framed = new byte[HeadBytes + (value?.Length ?? 0)];
        BitConverter.TryWriteBytes(framed.AsSpan(0), world.Number);
        BitConverter.TryWriteBytes(framed.AsSpan(4), world.Checkpoint);
        framed[6] = value == null ? (byte)0 : (byte)1;
        value?.CopyTo(framed, HeadBytes);
        if (value != null) _shown = true;
        _channel.Send(_route, framed, to);
    }

    private void Receive(ulong sender, byte[] data, int at)
    {
        var framed = data.AsSpan(at);
        if (framed.Length < HeadBytes || framed[6] > 1 || framed[6] == 0 && framed.Length != HeadBytes)
        {
            Net.Drop(Net.DropLobbyMalformed);
            _channel.Logger.ErrorOnce(Net.DropLobbyMalformed, "A malformed " + _channel.ModId + " value '" + Name + "' from player " + sender + " was ignored.");
            return;
        }
        var arrived = _channel.Node.Clock();
        // Another world's value: it is this world's once this player gets there, and it is sent only once.
        if (!Current(framed)) { _waiting[sender] = (framed.ToArray(), arrived); return; }
        _waiting.Remove(sender);
        Apply(sender, framed, arrived);
    }

    private bool Current(ReadOnlySpan<byte> framed)
        => _channel.Node.Accepts(_scope, BitConverter.ToUInt32(framed), BitConverter.ToUInt16(framed.Slice(4)));

    private void Apply(ulong sender, ReadOnlySpan<byte> framed, float arrived)
    {
        if (framed[6] == 0) { Drop(sender); return; }
        var value = framed.Slice(HeadBytes);
        if (_values.TryGetValue(sender, out var old) && value.SequenceEqual(old.Value)) return;
        _values[sender] = (value.ToArray(), ++_nextVersion, arrived);
        Raise(sender);
    }

    private void Drop(ulong player)
    {
        if (_values.Remove(player)) Raise(player);
    }

    private void DropOthers()
    {
        if (_values.Count == 0) return;
        _players.AddRange(_values.Keys);
        _values.Clear();
        foreach (var player in _players) Raise(player);
        _players.Clear();
    }

    private void Raise(ulong player)
    {
        try { Changed?.Invoke(player); }
        catch (Exception error) { _channel.Logger.ErrorOnce("core-lobby-value-handler-failed", "A " + _channel.ModId + " value '" + Name + "' handler failed: " + error); }
    }

    void NetChannel.IPart.EndSession()
    {
        _waiting.Clear();
        _shown = false;
        DropOthers();
    }

    void NetChannel.IPart.Forget(ulong player)
    {
        _waiting.Remove(player);
        Drop(player);
    }

    void NetChannel.IPart.OnWorld(NetWorld old, NetWorld next)
    {
        if (_scope == NetScope.Session || next.Number == old.Number && (_scope == NetScope.World || next.Checkpoint == old.Checkpoint)) return;
        DropOthers();
        // Every other player dropped this player's value too.
        _shown = false;
        // The host changed and this player's own is still going: the others get it again under the new world.
        if (next.Reason == NetWorldReason.Host) { if (Local != null) Send(0); }
        else { Local = null; LocalVersion = 0; }
        if (_waiting.Count == 0) return;
        _players.AddRange(_waiting.Keys);
        foreach (var player in _players)
        {
            var (framed, arrived) = _waiting[player];
            if (!Current(framed)) continue;
            _waiting.Remove(player);
            Apply(player, framed, arrived);
        }
        _players.Clear();
    }

    // A clear whose send was dropped on a full queue is owed like a value: the player may still hold the old one.
    void NetChannel.IPart.Sync(ulong player)
    {
        if (Local != null || _shown) Send(player);
    }

    void NetChannel.IPart.Tick() { }
}

/// <summary>The host's copy of what a checkpoint reload restores (<see cref="NetChannel.CheckpointCopy"/>), held by every player so
/// that whoever hosts at the reload has it. The host publishes it when the checkpoint is reached; ForgeCore delivers it (split and
/// budgeted like any large body), sends it to a player who joins later, keeps it through checkpoint reloads and host changes (the new
/// host sends its copy again) and drops it when the level is built or cleaned up or the session ends. Stamped with the world number
/// only. The mod reads <see cref="Value"/> when the checkpoint is reloaded.</summary>
public sealed class NetCheckpointCopy : NetChannel.IPart
{
    private readonly NetChannel _channel;
    private readonly NetChannel.Route _route;

    internal NetCheckpointCopy(NetChannel channel, string name, int version, int maxBytes)
    {
        _channel = channel;
        _route = channel.Claim(name, NetDelivery.Reliable, NetScope.World, true, maxBytes, "copy:" + NetFormat.Bytes(version), Receive, this);
        channel.Add(this);
    }

    /// <summary>The copy this player holds (the host's own, or the one it received), or null when there is none.</summary>
    public byte[]? Value { get; private set; }

    /// <summary>Raised on a client when the host's copy arrived.</summary>
    public event Action? Changed;

    /// <summary>The host stores <paramref name="data"/> and sends it to every compatible player.</summary>
    public void Publish(byte[] data)
    {
        if (!_channel.IsHost) throw new InvalidOperationException("Only the host publishes " + _channel.ModId + "'s checkpoint copy.");
        Value = (byte[])data.Clone();
        _channel.Send(_route, Value, 0);
    }

    private void Receive(ulong sender, byte[] data, int at)
    {
        if (!_channel.FromHost(sender)) { if (Net.FirstDrop(_channel.ModId, Net.DropNotHost)) Net.NoteDrop(_channel.ModId, Net.DropNotHost, "a checkpoint copy from player " + sender + ", who is not the host"); return; }
        Value = at == 0 ? data : data.AsSpan(at).ToArray();
        Changed?.Invoke();
    }

    void NetChannel.IPart.EndSession() => Value = null;

    void NetChannel.IPart.Forget(ulong player) { }

    void NetChannel.IPart.OnWorld(NetWorld old, NetWorld next)
    {
        if (next.Number == old.Number) return;
        if (next.Reason != NetWorldReason.Host) Value = null;
        else if (_channel.IsHost && Value != null) _channel.Send(_route, Value, 0);
    }

    void NetChannel.IPart.Sync(ulong player)
    {
        if (_channel.IsHost && Value != null) _channel.Send(_route, Value, player);
    }

    void NetChannel.IPart.Tick() { }
}
