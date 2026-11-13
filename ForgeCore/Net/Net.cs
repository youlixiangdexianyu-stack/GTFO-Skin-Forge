using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ForgeCore;

/// <summary>The network every Forge mod shares, built on GTFO-API's NetworkAPI. One handshake per pair of players lists every Forge mod
/// (<see cref="NetChannel"/>); one member list, one readiness and one epoch (<see cref="World"/>) serve them all. Messages run their
/// handlers on the main thread as the game delivers them.</summary>
public static class Net
{
    /// <summary>This process's node, set by ForgeCore's plugin; offline tests build their own nodes on a hub.</summary>
    internal static NetNode? Node { get; set; }

    private static NetNode Process => Node ?? throw new InvalidOperationException("ForgeCore's network is not ready (GTFO-API missing?).");

    /// <summary>Opens <paramref name="modId"/>'s channel; its message formats are fingerprinted as the mod registers them. A mod opens
    /// one channel; opening a second is refused.</summary>
    public static NetChannel Open(string modId) => Process.Open(modId);

    /// <summary>The session's epoch as this player holds it. A message is stamped with it when sent and dropped on arrival when the
    /// receiver's differs (which part is compared is fixed per kind of message, see <see cref="NetChannel"/>).</summary>
    public static NetWorld World => Node?.World ?? default;

    /// <summary>Raised on the main thread when <see cref="World"/> changed, after ForgeCore dropped what belonged to the earlier one and
    /// (on the host) told the other players, so what a handler sends is stamped with the new world and arrives after it.</summary>
    public static event Action<NetWorld>? WorldChanged
    {
        add => Process.WorldChanged += value;
        remove => Process.WorldChanged -= value;
    }

    private static long _sentMessages, _sentBytes, _lossyMessages, _lossyBytes;

    /// <summary>What every Forge package sent since the process started: each packet handed to the game is one message (a send to
    /// three players is three), at its size without GTFO-API's event header. A reader takes two readings and divides by the time
    /// between them. Reading and counting allocate nothing.</summary>
    public static NetTraffic Traffic => new(Interlocked.Read(ref _sentMessages), Interlocked.Read(ref _sentBytes),
        Interlocked.Read(ref _lossyMessages), Interlocked.Read(ref _lossyBytes));

    internal static void CountSent(int bytes, NetDelivery delivery)
    {
        Interlocked.Increment(ref _sentMessages);
        Interlocked.Add(ref _sentBytes, bytes);
        if (delivery != NetDelivery.Lossy) return;
        Interlocked.Increment(ref _lossyMessages);
        Interlocked.Add(ref _lossyBytes, bytes);
    }

    private static readonly Dictionary<string, long> Drops = new(StringComparer.Ordinal);
    private static readonly HashSet<(string ModId, string Code)> Noticed = new();

    /// <summary>Where <see cref="NoteDrop"/> writes its one line per mod and code (ForgeCore's own log, as information).</summary>
    internal static Action<string> Notice { get; set; } = static _ => { };

    /// <summary>Counts one received message dropped under its refusal <paramref name="code"/> (malformed, oversized, from a refused or
    /// unknown sender, from another world, out of order). Every Forge package counts its network drops here, so one place answers how
    /// many of each kind arrived.</summary>
    public static void Drop(string code)
    {
        lock (Drops) Drops[code] = Drops.TryGetValue(code, out var count) ? count + 1 : 1;
    }

    /// <summary>How many received messages were dropped under <paramref name="code"/> since the process started.</summary>
    public static long Dropped(string code)
    {
        lock (Drops) return Drops.TryGetValue(code, out var count) ? count : 0;
    }

    /// <summary>Counts a drop; true the first time <paramref name="modId"/> drops under <paramref name="code"/>, when the caller writes its
    /// one line with <see cref="NoteDrop"/>. The line is built only then, so a wave of drops (stale messages after a world change) allocates nothing.</summary>
    internal static bool FirstDrop(string modId, string code)
    {
        Drop(code);
        lock (Noticed) return Noticed.Add((modId, code));
    }

    internal static void NoteDrop(string modId, string code, string detail)
        => Notice("net.drop mod=" + modId + " code=" + code + " " + detail + " (later ones are only counted)");

    // ForgeCore's refusal codes for dropped messages.
    internal const string DropOversized = "net-body-oversized", DropMalformed = "net-body-malformed", DropFormatsUnreadable = "net-formats-unreadable",
        DropLobbyMalformed = "net-lobby-malformed", DropStateSize = "net-state-size", DropStateInvalid = "net-state-invalid",
        DropPeerRefused = "net-peer-refused", DropPeerUnverified = "net-peer-unverified", DropManifestMalformed = "net-manifest-malformed",
        DropStaleWorld = "net-stale-world", DropOutOfOrder = "net-out-of-order", DropNotHost = "net-not-host", DropHelloVersion = "net-hello-version",
        DropChunk = "net-chunk-orphan", DropBulkVoid = "net-bulk-void", DropRequestStale = "net-request-stale",
        DropRequestDuplicate = "net-request-duplicate", DropAnswerUnknown = "net-answer-unknown", DropTableGap = "net-table-gap";
}

/// <summary>One player's side of the Forge network: the member list, the handshake, the world and the send budget, shared by every
/// channel opened on it. The game has one (<see cref="Net.Node"/>).</summary>
internal sealed class NetNode
{
    /// <summary>How long a greeted player may stay silent before it is greeted again, <see cref="GreetingRetries"/> times; then it
    /// counts as silent: a joining player drops a greeting from a player it does not list yet, and loading can take longer than one window.</summary>
    internal const double AnswerSeconds = 10.0;
    internal const int GreetingRetries = 2;
    private const double RosterRetrySeconds = 5, AskSeconds = 2;
    /// <summary>A reliable byte body above this travels in parts of this size, after the other reliable messages, within the send budget:
    /// <see cref="RateBytes"/> a second whatever the frame rate (16 KiB a frame at 60 fps), of which a frame spends at most
    /// <see cref="BurstBytes"/>. Bodies waiting for it hold at most <see cref="QueueBytes"/>; one more is dropped and counted
    /// (<c>net-bulk-void</c>), and the state it carried is sent again once the queue is half empty (<see cref="NetChannel.Route.Owner"/>).</summary>
    internal const int ChunkBytes = 1024, RateBytes = 960 * 1024, BurstBytes = 32 * 1024, QueueBytes = 4 * 1024 * 1024;
    private const int MaxControlBytes = 512 * 1024;
    private const string HelloEvent = "Forge/hello", WorldEvent = "Forge/world", FormatsEvent = "Forge/formats";
    private const byte HelloVersion = 1;

    private sealed class Peer
    {
        internal bool Greeted, Answered, ReportedSilent;
        internal double GreetedAt;
        internal int Retries;
    }

    private sealed class Bulk
    {
        internal NetChannel.Route Route = null!;
        internal ulong To;
        internal byte[] Head = null!, Body = null!;
        internal int Offset;
        internal NetWorld Stamp;
    }

    internal readonly INetTransport Transport;
    /// <summary>The clock a <see cref="PlayerStateSlot{T}"/> counts on (the game's <c>Time.time</c>).</summary>
    internal Func<float> Clock = static () => 0f;
    private readonly ModLog _log;
    private readonly List<NetChannel> _channels = new();
    private readonly Dictionary<string, NetChannel> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly ulong[] _members = new ulong[8], _roster = new ulong[8], _added = new ulong[8], _removed = new ulong[8];
    private readonly List<ulong> _gone = new(4);
    private readonly Queue<Bulk> _bulk = new();
    private readonly List<(NetChannel.IPart Part, ulong To)> _resync = new(), _resyncing = new();
    private int _memberCount, _addedCount, _removedCount;
    private long _queuedBytes;
    private ulong _host, _local, _unlisted;
    private double _now, _nextSilent = double.PositiveInfinity, _unlistedSince, _askedAt, _tokens = BurstBytes, _budgetAt = double.NaN;
    private bool _sessionChanged = true, _greetAgain, _observed, _predicted;
    private NetWorld _world;
    private uint _highest;

    internal NetNode(INetTransport transport, string logId)
    {
        Transport = transport;
        _log = Log.For(logId);
        transport.Register(HelloEvent, (sender, data) => Control(() => ReceiveHello(sender, data), data));
        transport.Register(WorldEvent, (sender, data) => Control(() => ReceiveWorld(sender, data), data));
        transport.Register(FormatsEvent, (sender, data) => Control(() => ReceiveFormats(sender, data), data));
    }

    private void Control(Action work, byte[] data)
    {
        if (data == null || data.Length > MaxControlBytes) { if (Net.FirstDrop(_log.ModId, Net.DropOversized)) Net.NoteDrop(_log.ModId, Net.DropOversized, "a handshake message was missing or too large"); return; }
        try { Settle(); work(); }
        catch (Exception error) { _log.ErrorOnce("core-handshake-failed", "ForgeCore's handshake failed: " + error); }
    }

    internal NetChannel Open(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        if (_byName.ContainsKey(modId)) throw new InvalidOperationException(modId + " already opened its channel.");
        var channel = new NetChannel(this, modId);
        _byName.Add(modId, channel);
        _channels.Add(channel);
        GreetAgain();
        return channel;
    }

    internal ulong Local => _local;
    internal ulong Host => _host;
    internal bool IsHost => _host != 0 && _host == _local;
    internal double Now => _now;
    internal int MemberCount => _memberCount;
    internal ulong Member(int index) => _members[index];
    /// <summary><paramref name="player"/> is another player of the current session.</summary>
    internal bool IsMember(ulong player) => _host != 0 && Array.IndexOf(_members, player, 0, _memberCount) >= 0;

    // ---- session -------------------------------------------------------------------------------------------------------------

    /// <summary>The game said the session may have changed (a player joined or left, the host changed, the session was reset): it is
    /// read at the next message or frame, once for every channel.</summary>
    internal void NoteSession() => _sessionChanged = true;

    /// <summary>The session added another player (<c>SNet_SessionHub.AddPlayerToSession</c>' argument), whatever its list shows now.</summary>
    internal void NotePlayerAdded(ulong player)
    {
        if (player == 0) return;
        Take(_removed, ref _removedCount, player);
        if (Array.IndexOf(_added, player, 0, _addedCount) < 0 && _addedCount < _added.Length) _added[_addedCount++] = player;
        _sessionChanged = true;
    }

    /// <summary>The session removed another player (<c>SNet_SessionHub.RemovePlayerFromSession</c>' argument), whatever its list shows now.</summary>
    internal void NotePlayerRemoved(ulong player)
    {
        if (player == 0) return;
        Take(_added, ref _addedCount, player);
        if (Array.IndexOf(_removed, player, 0, _removedCount) < 0 && _removedCount < _removed.Length) _removed[_removedCount++] = player;
        _sessionChanged = true;
    }

    private static void Take(ulong[] list, ref int count, ulong player)
    {
        var at = Array.IndexOf(list, player, 0, count);
        if (at >= 0) list[at] = list[--count];
    }

    /// <summary>The session's member list with the named adds and removes applied; a name the list already agrees with is dropped.</summary>
    private int Apply(int count)
    {
        for (var i = 0; i < _removedCount;)
        {
            var at = Array.IndexOf(_roster, _removed[i], 0, count);
            if (at < 0) { _removed[i] = _removed[--_removedCount]; continue; }
            _roster[at] = _roster[--count];
            i++;
        }
        for (var i = 0; i < _addedCount;)
        {
            if (Array.IndexOf(_roster, _added[i], 0, count) >= 0) { _added[i] = _added[--_addedCount]; continue; }
            if (count < _roster.Length) _roster[count++] = _added[i];
            i++;
        }
        return count;
    }

    /// <summary>Reads the session now if the game said it changed: before a received message is judged, before a game event that
    /// follows a host change is handled, and every frame.</summary>
    internal void Settle()
    {
        if (!_sessionChanged) return;
        _sessionChanged = false;
        var host = Transport.Host;
        var local = Transport.Local;
        var count = 0;
        if (host == 0) _addedCount = _removedCount = 0;
        else count = Apply(Transport.Others(_roster));
        var previous = _host;
        var same = host != 0 && previous != 0 && local == _local;
        _host = host;
        _local = local;
        for (var i = 0; i < _memberCount; i++)
            if (Array.IndexOf(_roster, _members[i], 0, count) < 0) _gone.Add(_members[i]);
        Array.Copy(_roster, _members, count);
        _memberCount = count;
        _greetAgain = true;
        if (!same)
        {
            _gone.Clear();
            _peers.Clear();
            _bulk.Clear();
            _queuedBytes = 0;
            _resync.Clear();
            _predicted = false;
            foreach (var channel in _channels) Guard(channel, static c => c.EndSession());
            if (IsHost) Mint(NetWorldReason.Session);
            else if (_world.Number != 0 || host != previous) SetWorld(default, false);
            return;
        }
        // The host changed inside the session: the world moves first, so a request to the host that left is asked of the new one
        // instead of ending with the player who left.
        if (host != previous) NoteWorld(NetWorldReason.Host);
        foreach (var player in _gone)
        {
            _peers.Remove(player);
            foreach (var channel in _channels) Guard(channel, c => c.Forget(player, true));
        }
        _gone.Clear();
    }

    private void Guard(NetChannel channel, Action<NetChannel> work)
    {
        try { work(channel); }
        catch (Exception error) { Log.For(channel.ModId).ErrorOnce("core-handshake-failed", "ForgeCore's network state of " + channel.ModId + " failed: " + error); }
    }

    // ---- world ---------------------------------------------------------------------------------------------------------------

    internal NetWorld World => _world;
    internal event Action<NetWorld>? WorldChanged;

    /// <summary>The game event behind <paramref name="reason"/> happened on this machine. The host mints the next world and tells
    /// everyone. A client that already holds the host's world for this very event does nothing; otherwise it moves to the world the
    /// host is about to announce (<see cref="Next"/>) without waiting, and asks the host if no
    /// announcement confirms it within <see cref="AskSeconds"/>.</summary>
    internal void NoteWorld(NetWorldReason reason)
    {
        Settle();
        if (_host == 0) return;
        if (IsHost) { Mint(reason); return; }
        if (_world.Number == 0) return;
        if (_world.Reason == reason && !_observed) { _observed = true; return; }
        _predicted = true;
        _askedAt = _now;
        SetWorld(Next(reason), true);
    }

    /// <summary>One above every number this process has seen (its own and each greeted player's), so a number never repeats for
    /// anyone in the session, whoever hosted before.</summary>
    private void Mint(NetWorldReason reason)
    {
        _predicted = false;
        SetWorld(Next(reason), true);
    }

    /// <summary>The world after this one: the same on the host, which mints it, and on a client that moves before the host's
    /// announcement arrives.</summary>
    private NetWorld Next(NetWorldReason reason)
        => reason == NetWorldReason.Checkpoint && _world.Number != 0
            ? new NetWorld(_world.Number, (ushort)(_world.Checkpoint + 1), reason)
            : new NetWorld(Math.Max(_highest, _world.Number) + 1, 0, reason);

    private void SetWorld(NetWorld next, bool observed)
    {
        var old = _world;
        _world = next;
        _observed = observed;
        _highest = Math.Max(_highest, next.Number);
        if (IsHost)
            for (var i = 0; i < _memberCount; i++)
                if (_peers.TryGetValue(_members[i], out var peer) && peer.Answered) SendWorld(_members[i], next.Reason);
        foreach (var channel in _channels) Guard(channel, c => c.OnWorld(old, next));
        try { WorldChanged?.Invoke(next); }
        catch (Exception error) { _log.ErrorOnce("core-world-handler-failed", "A world-change handler failed: " + error); }
    }

    private void SendWorld(ulong to, NetWorldReason reason)
    {
        var data = new byte[7];
        BitConverter.TryWriteBytes(data.AsSpan(0), _world.Number);
        BitConverter.TryWriteBytes(data.AsSpan(4), _world.Checkpoint);
        data[6] = (byte)reason;
        Send(WorldEvent, data, to, NetDelivery.Reliable);
    }

    private void ReceiveWorld(ulong sender, byte[] data)
    {
        if (data.Length != 7 || sender == 0 || sender == _local) { if (Net.FirstDrop(_log.ModId, Net.DropMalformed)) Net.NoteDrop(_log.ModId, Net.DropMalformed, "a world message of " + data.Length + " bytes"); return; }
        var reason = (NetWorldReason)data[6];
        // A client asks which world the host is in (its own guess was never confirmed).
        if (reason == NetWorldReason.None) { if (IsHost && IsMember(sender)) SendWorld(sender, _world.Reason); return; }
        if (sender != _host || IsHost) { if (Net.FirstDrop(_log.ModId, Net.DropNotHost)) Net.NoteDrop(_log.ModId, Net.DropNotHost, "a world announced by player " + sender + ", who is not the host"); return; }
        var next = new NetWorld(BitConverter.ToUInt32(data, 0), BitConverter.ToUInt16(data, 4), reason);
        _predicted = false;
        if (next.Number == _world.Number && next.Checkpoint == _world.Checkpoint)
        {
            if (_world.Reason != reason) { _world = next; _observed = false; }
            return;
        }
        SetWorld(next, false);
    }

    /// <summary>Whether a message stamped (<paramref name="number"/>, <paramref name="checkpoint"/>) belongs to this player's world.</summary>
    internal bool Accepts(NetScope scope, uint number, ushort checkpoint)
        => scope == NetScope.Session || _world.Number != 0 && number == _world.Number && (scope == NetScope.World || checkpoint == _world.Checkpoint);

    // ---- handshake -----------------------------------------------------------------------------------------------------------

    /// <summary>What this player lists changed (a mod opened, registered a message or changed its content): greet every member again.</summary>
    internal void GreetAgain()
    {
        foreach (var peer in _peers.Values)
        {
            peer.Greeted = false;
            peer.ReportedSilent = false;
            peer.Answered = false;
            peer.Retries = 0;
        }
        _greetAgain = true;
    }

    private void Greet(double now)
    {
        _greetAgain = false;
        for (var i = 0; i < _memberCount; i++)
        {
            var lookup = _members[i];
            if (!_peers.TryGetValue(lookup, out var peer)) _peers[lookup] = peer = new Peer();
            if (peer.Greeted) continue;
            peer.Greeted = true;
            peer.GreetedAt = now;
            _nextSilent = Math.Min(_nextSilent, now + AnswerSeconds);
            SendHello(lookup, false);
        }
    }

    private void CheckSilent(double now)
    {
        _nextSilent = double.PositiveInfinity;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (!peer.Greeted || peer.ReportedSilent || peer.Answered) continue;
            if (now - peer.GreetedAt < AnswerSeconds) { _nextSilent = Math.Min(_nextSilent, peer.GreetedAt + AnswerSeconds); continue; }
            if (peer.Retries < GreetingRetries)
            {
                peer.Retries++;
                peer.GreetedAt = now;
                _nextSilent = Math.Min(_nextSilent, now + AnswerSeconds);
                SendHello(pair.Key, false);
                continue;
            }
            peer.ReportedSilent = true;
            _gone.Add(pair.Key);
        }
        foreach (var player in _gone)
        {
            _log.Error("core-player-silent", "Player " + player + " answered none of ForgeCore's greetings: ForgeCore is missing there or is a build that does not speak this handshake. No Forge mod talks to that player.");
            foreach (var channel in _channels) Guard(channel, c => c.Silent(player));
        }
        _gone.Clear();
    }

    /// <summary>One message for every mod: the handshake version, whether it answers a greeting, the highest world number
    /// the sender has seen, then one cell per mod: its name, format fingerprint, content hash and flags. The host sends its world
    /// first, so whoever judges it compatible already holds the world its state is stamped with.</summary>
    private void SendHello(ulong to, bool answer)
    {
        if (IsHost) SendWorld(to, _world.Reason);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(HelloVersion);
            writer.Write(answer);
            writer.Write(_highest);
            writer.Write((byte)_channels.Count);
            foreach (var channel in _channels)
            {
                writer.Write(channel.ModId);
                writer.Write(channel.Fingerprint());
                writer.Write(channel.ContentHash);
                writer.Write(channel.Held);
            }
        }
        Send(HelloEvent, stream.ToArray(), to, NetDelivery.Reliable);
    }

    private void ReceiveHello(ulong sender, byte[] data)
    {
        if (sender == 0 || sender == _local) return;
        // The game closes the connection of a player that sends to someone who does not list it, so a greeting from a player the
        // session does not list yet is not answered: once it lists them this side greets them and the answer follows.
        if (!IsMember(sender)) { NoteUnlisted(sender); return; }
        if (data.Length == 0 || data[0] != HelloVersion)
        {
            Net.Drop(Net.DropHelloVersion);
            _log.ErrorOnce(Net.DropHelloVersion, "Player " + sender + " speaks ForgeCore handshake version " + (data.Length == 0 ? "none" : data[0].ToString()) + ", this build speaks " + HelloVersion + ". No Forge mod talks to that player; both sides need the same ForgeCore.");
            return;
        }
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        reader.ReadByte();
        var answer = reader.ReadBoolean();
        _highest = Math.Max(_highest, reader.ReadUInt32());
        var count = reader.ReadByte();
        if (!_peers.TryGetValue(sender, out var peer)) _peers[sender] = peer = new Peer();
        // Any greeting or answer proves ForgeCore is there, whatever each mod decides next.
        peer.Answered = true;
        // Answer first, so the other side has judged this one before the state a matching mod sends below arrives. An answer is
        // never answered, so they cannot loop.
        if (!answer) SendHello(sender, true);
        var listed = new HashSet<NetChannel>();
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var format = reader.ReadUInt64();
            var content = reader.ReadUInt64();
            var held = reader.ReadBoolean();
            if (!_byName.TryGetValue(name, out var channel)) continue;
            listed.Add(channel);
            Guard(channel, c => c.Judge(sender, format, content, held));
        }
        foreach (var channel in _channels)
            if (!listed.Contains(channel)) Guard(channel, c => c.Silent(sender));
    }

    internal void SendFormats(ulong to, byte[] data) => Send(FormatsEvent, data, to, NetDelivery.Reliable);

    private void ReceiveFormats(ulong sender, byte[] data)
    {
        var there = NetFormat.Read(data, out var modId, out var detail);
        if (there == null || !_byName.TryGetValue(modId, out var channel))
        {
            Net.Drop(Net.DropFormatsUnreadable);
            _log.ErrorOnce(Net.DropFormatsUnreadable, "Player " + sender + " sent an unreadable message format list.");
            return;
        }
        channel.ReceiveFormats(sender, there, detail);
    }

    /// <summary>A message came from a player the member list does not show. Only recorded: the list is read again when the session
    /// lists a player, so the player is greeted then; if that has not happened after <see cref="RosterRetrySeconds"/> what it sent was
    /// lost for good and one error says so.</summary>
    internal void NoteUnlisted(ulong player)
    {
        if (_unlisted != 0) return;
        _unlisted = player;
        _unlistedSince = double.NaN;
    }

    private void CheckUnlisted(double now)
    {
        if (double.IsNaN(_unlistedSince)) _unlistedSince = now;
        if (IsMember(_unlisted)) { _unlisted = 0; return; }
        if (now - _unlistedSince < RosterRetrySeconds) return;
        _log.ErrorOnce("core-player-sent-data-but-session-never", "Player " + _unlisted + " sent Forge data but the session never listed them within " + RosterRetrySeconds + " s; it was dropped.");
        _unlisted = 0;
    }

    // ---- sending -------------------------------------------------------------------------------------------------------------

    /// <summary>One packet to one member. Reliable bytes are taken from the send budget (<see cref="RateBytes"/>), which may go into
    /// debt by up to one burst: ordinary messages are never held back, the waiting bodies wait longer.</summary>
    internal void Send(string name, byte[] data, ulong to, NetDelivery delivery)
    {
        Transport.Send(name, data, to, delivery);
        Net.CountSent(data.Length, delivery);
        if (delivery == NetDelivery.Reliable) _tokens = Math.Max(-BurstBytes, _tokens - data.Length);
    }

    /// <summary>A body that waits for the budget: sent in parts after the other reliable messages, oldest first. When the bodies already
    /// waiting hold <see cref="QueueBytes"/> it is dropped and counted instead, and the route's state is owed to the player.</summary>
    internal void Enqueue(NetChannel.Route route, ulong to, byte[] head, byte[] body)
    {
        if (_queuedBytes != 0 && _queuedBytes + body.Length > QueueBytes)
        {
            if (Net.FirstDrop(route.ModId, Net.DropBulkVoid))
                Net.NoteDrop(route.ModId, Net.DropBulkVoid, "message '" + route.Name + "' of " + body.Length + " bytes to player " + to + " found the send queue full (" + _queuedBytes + " bytes waiting)"
                    + (route.Owner != null ? "; its state is sent again once the queue empties" : "; it is lost"));
            if (route.Owner != null && !_resync.Contains((route.Owner, to))) _resync.Add((route.Owner, to));
            return;
        }
        route.Queued++;
        _queuedBytes += body.Length;
        _bulk.Enqueue(new Bulk { Route = route, To = to, Head = head, Body = body, Stamp = _world });
    }

    private void Done(Bulk item)
    {
        _bulk.Dequeue();
        item.Route.Queued--;
        _queuedBytes -= item.Body.Length;
    }

    private void DrainBulk()
    {
        while (_bulk.Count != 0 && _tokens > 0)
        {
            var item = _bulk.Peek();
            var scope = item.Route.Scope;
            if (!IsMember(item.To) || scope != NetScope.Session && (item.Stamp.Number != _world.Number || scope == NetScope.Checkpoint && item.Stamp.Checkpoint != _world.Checkpoint))
            {
                // Its player left or its world ended while it waited.
                Done(item);
                Net.Drop(Net.DropBulkVoid);
                continue;
            }
            var size = Math.Min(ChunkBytes, item.Body.Length - item.Offset);
            var start = item.Offset == 0;
            var whole = start && size == item.Body.Length;
            var packet = new byte[item.Head.Length + (start && !whole ? 5 : 1) + size];
            item.Head.CopyTo(packet, 0);
            var at = item.Head.Length;
            packet[at++] = whole ? NetChannel.PartWhole : start ? NetChannel.PartFirst : NetChannel.PartNext;
            if (start && !whole) { BitConverter.TryWriteBytes(packet.AsSpan(at), item.Body.Length); at += 4; }
            Array.Copy(item.Body, item.Offset, packet, at, size);
            Send(item.Route.Event, packet, item.To, NetDelivery.Reliable);
            item.Offset += size;
            if (item.Offset < item.Body.Length) continue;
            Done(item);
        }
        if (_resync.Count == 0 || _queuedBytes > QueueBytes / 2) return;
        // State whose body was dropped on a full queue: each part sends that player what it is owed (the whole table, the value, the copy).
        _resyncing.AddRange(_resync);
        _resync.Clear();
        foreach (var (part, to) in _resyncing)
        {
            if (!IsMember(to)) continue;
            try { part.Sync(to); }
            catch (Exception error) { _log.ErrorOnce("core-handshake-failed", "ForgeCore's network state failed to send again after a full send queue: " + error); }
        }
        _resyncing.Clear();
    }

    /// <summary>Fills the send budget for the time since the last frame, up to one burst.</summary>
    private void Refill(double now)
    {
        if (!double.IsNaN(_budgetAt) && now > _budgetAt) _tokens = Math.Min(BurstBytes, _tokens + (now - _budgetAt) * RateBytes);
        _budgetAt = now;
    }

    /// <summary>Once a frame: reads the session if it changed, greets members not greeted yet, repeats unanswered greetings, lets each
    /// channel flush its tables and time its requests out, then sends what the budget allows of the waiting bodies. It
    /// allocates nothing on a frame where nothing happens.</summary>
    internal void Tick(double now)
    {
        _now = now;
        Refill(now);
        if (_channels.Count == 0) return;
        Settle();
        if (_host == 0) return;
        if (_unlisted != 0) CheckUnlisted(now);
        if (_greetAgain) Greet(now);
        if (now >= _nextSilent) CheckSilent(now);
        if (_predicted && now - _askedAt >= AskSeconds)
        {
            _askedAt = now;
            Send(WorldEvent, new byte[7], _host, NetDelivery.Reliable);
        }
        foreach (var channel in _channels) Guard(channel, static c => c.Tick());
        DrainBulk();
    }
}
