using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeCore;

/// <summary>How a request ended for the player who asked.</summary>
public enum NetAnswerStatus : byte
{
    /// <summary>The receiver ran it and this is its answer.</summary>
    Answered,
    /// <summary>The receiver had already run this very request (it was asked again after a host change): it did not run it a second
    /// time, and the first run's answer is not known here.</summary>
    AlreadyRan,
    /// <summary>No answer came in time; whether it ran is not known.</summary>
    TimedOut,
    /// <summary>The receiver left the session, was refused or is not compatible; whether it ran is not known.</summary>
    Left,
    /// <summary>The world number or checkpoint generation changed before the answer came; whether it ran is not known.</summary>
    WorldChanged,
}

/// <summary>What the asking player gets back, once per request.</summary>
public readonly struct NetAnswer
{
    internal NetAnswer(uint id, ulong from, NetAnswerStatus status, byte[] body)
    {
        Id = id;
        From = from;
        Status = status;
        Body = body;
    }

    public uint Id { get; }
    public ulong From { get; }
    public NetAnswerStatus Status { get; }
    /// <summary>The receiver's answer; empty unless <see cref="Status"/> is <see cref="NetAnswerStatus.Answered"/>.</summary>
    public byte[] Body { get; }
}

/// <summary>One request on the receiving side: who asked, what, and the one answer it takes (now or later).</summary>
public sealed class NetAsk
{
    private readonly NetRequest _request;

    internal NetAsk(NetRequest request, ulong from, uint id, byte[] body)
    {
        _request = request;
        From = from;
        Id = id;
        Body = body;
    }

    public ulong From { get; }
    public uint Id { get; }
    public byte[] Body { get; }
    public bool Answered { get; private set; }

    /// <summary>Answers the request; a second answer is refused.</summary>
    public void Answer(byte[] body)
    {
        if (Answered) throw new InvalidOperationException("This request was already answered.");
        Answered = true;
        _request.Reply(From, Id, body);
    }
}

/// <summary>A request with a receipt (<see cref="NetChannel.Request"/>). The asking player gets exactly one <see cref="NetAnswer"/>
/// per request: the receiver's answer, or why none came. The receiver runs each request exactly once: every request carries its
/// sender's number for it, and the receiver keeps a ledger per sender of the numbers it has started, so one that arrives again is
/// not run again (<c>net-request-duplicate</c> while it is still running, <see cref="NetAnswerStatus.AlreadyRan"/> once it is done)
/// and one below the sender's settled floor is dropped (<c>net-request-stale</c>). Requests and answers are stamped with the world
/// number and checkpoint generation; when either changes, a waiting request ends with <see cref="NetAnswerStatus.WorldChanged"/>,
/// except that a request to the host that left is asked of the new host under the same number. The ledger is the receiver's own;
/// a mod that mirrors host state to the players carries it in that mirror (<see cref="CaptureLedger"/>, <see cref="RestoreLedger"/>),
/// so the new host does not run what the old one already ran. Bodies are bytes of any size up to the registered maximum.</summary>
public sealed class NetRequest : NetChannel.IPart
{
    private const int MaxSeen = 4096;

    private sealed class Pending
    {
        internal ulong To;
        internal byte[] Body = null!;
        internal Action<NetAnswer> Done = null!;
        internal double Deadline;
        internal bool ToHost, Waiting;
    }

    private sealed class Window
    {
        /// <summary>Every number below it is settled on the sender's side.</summary>
        internal uint Floor;
        /// <summary>The numbers at or above the floor this player started, and whether each is done.</summary>
        internal readonly Dictionary<uint, bool> Seen = new();
    }

    private readonly NetChannel _channel;
    private readonly NetChannel.Route _ask, _answer;
    private readonly Action<NetAsk> _serve;
    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly Dictionary<ulong, Window> _ledger = new();
    private readonly List<uint> _ids = new(4);
    private uint _nextId;

    internal NetRequest(NetChannel channel, string name, int version, Action<NetAsk> serve, int maxBytes)
    {
        _channel = channel;
        _serve = serve ?? throw new ArgumentNullException(nameof(serve));
        _ask = channel.Claim(name, NetDelivery.Reliable, NetScope.Checkpoint, true, maxBytes + 8, "request:" + NetFormat.Bytes(version), ReceiveAsk);
        _answer = channel.Claim(name + "/answer", NetDelivery.Reliable, NetScope.Checkpoint, true, maxBytes + 5, "answer:" + NetFormat.Bytes(version), ReceiveAnswer);
        channel.Add(this);
    }

    /// <summary>Asks <paramref name="to"/>. <paramref name="done"/> runs once, on the main thread: with the answer, or after
    /// <paramref name="seconds"/> without one, or when the receiver or the world went. Returns the request's number.</summary>
    public uint Ask(ulong to, byte[] body, Action<NetAnswer> done, double seconds = 10)
    {
        if (done == null) throw new ArgumentNullException(nameof(done));
        var id = ++_nextId;
        var node = _channel.Node;
        var pending = new Pending { To = to, Body = body, Done = done, Deadline = node.Now + seconds, ToHost = to != 0 && to == node.Host };
        _pending[id] = pending;
        if (_channel.Compatible(to)) SendAsk(id, pending);
        else Complete(id, NetAnswerStatus.Left);
        return id;
    }

    /// <summary>Asks the host (<see cref="Ask"/>); on the host itself, or with no compatible host, the request ends at once with <see cref="NetAnswerStatus.Left"/>.</summary>
    public uint AskHost(byte[] body, Action<NetAnswer> done, double seconds = 10) => Ask(_channel.HostPeer, body, done, seconds);

    /// <summary>The request's number, the lowest number this player still waits on, then the body.</summary>
    private void SendAsk(uint id, Pending pending)
    {
        var floor = id;
        foreach (var other in _pending.Keys) if (other < floor) floor = other;
        var packet = new byte[8 + pending.Body.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0), id);
        BitConverter.TryWriteBytes(packet.AsSpan(4), floor);
        pending.Body.CopyTo(packet, 8);
        pending.Waiting = false;
        _channel.Send(_ask, packet, pending.To);
    }

    private void ReceiveAsk(ulong sender, byte[] data, int at)
    {
        if (data.Length - at < 8) { _channel.Malformed(_ask, sender, data.Length); return; }
        var id = BitConverter.ToUInt32(data, at);
        var floor = BitConverter.ToUInt32(data, at + 4);
        if (!_ledger.TryGetValue(sender, out var window)) _ledger[sender] = window = new Window();
        if (floor > window.Floor)
        {
            window.Floor = floor;
            foreach (var seen in window.Seen.Keys) if (seen < floor) _ids.Add(seen);
            foreach (var seen in _ids) window.Seen.Remove(seen);
            _ids.Clear();
        }
        if (id < window.Floor || window.Seen.Count >= MaxSeen)
        {
            if (Net.FirstDrop(_channel.ModId, Net.DropRequestStale)) Net.NoteDrop(_channel.ModId, Net.DropRequestStale, "request '" + _ask.Name + "' number " + id + " from player " + sender + " is below the " + window.Floor + " that player had settled, or that player has " + MaxSeen + " unsettled");
            return;
        }
        if (window.Seen.TryGetValue(id, out var done))
        {
            if (done) Send(sender, id, NetAnswerStatus.AlreadyRan, Array.Empty<byte>());
            else if (Net.FirstDrop(_channel.ModId, Net.DropRequestDuplicate)) Net.NoteDrop(_channel.ModId, Net.DropRequestDuplicate, "request '" + _ask.Name + "' number " + id + " from player " + sender + " arrived again while it runs");
            return;
        }
        window.Seen[id] = false;
        _serve(new NetAsk(this, sender, id, data.AsSpan(at + 8).ToArray()));
    }

    internal void Reply(ulong to, uint id, byte[] body)
    {
        if (_ledger.TryGetValue(to, out var window) && window.Seen.ContainsKey(id)) window.Seen[id] = true;
        Send(to, id, NetAnswerStatus.Answered, body);
    }

    private void Send(ulong to, uint id, NetAnswerStatus status, byte[] body)
    {
        var packet = new byte[5 + body.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0), id);
        packet[4] = (byte)status;
        body.CopyTo(packet, 5);
        _channel.Send(_answer, packet, to);
    }

    private void ReceiveAnswer(ulong sender, byte[] data, int at)
    {
        if (data.Length - at < 5 || data[at + 4] > (byte)NetAnswerStatus.AlreadyRan) { _channel.Malformed(_answer, sender, data.Length); return; }
        var id = BitConverter.ToUInt32(data, at);
        if (!_pending.TryGetValue(id, out var pending) || pending.To != sender)
        {
            if (Net.FirstDrop(_channel.ModId, Net.DropAnswerUnknown)) Net.NoteDrop(_channel.ModId, Net.DropAnswerUnknown, "an answer to request '" + _ask.Name + "' number " + id + " from player " + sender + " matches nothing this player waits on");
            return;
        }
        _pending.Remove(id);
        pending.Done(new NetAnswer(id, sender, (NetAnswerStatus)data[at + 4], data.AsSpan(at + 5).ToArray()));
    }

    private void Complete(uint id, NetAnswerStatus status)
    {
        if (!_pending.Remove(id, out var pending)) return;
        try { pending.Done(new NetAnswer(id, pending.To, status, Array.Empty<byte>())); }
        catch (Exception error) { _channel.Logger.ErrorOnce("core-message-handler-failed", "A " + _channel.ModId + " request handler failed: " + error); }
    }

    /// <summary>Ends every request <paramref name="which"/> picks; it is handed <paramref name="state"/>, so a static lambda allocates nothing.</summary>
    private void CompleteAll<TState>(TState state, Func<TState, Pending, bool> which, NetAnswerStatus status)
    {
        if (_pending.Count == 0) return;
        foreach (var pair in _pending) if (which(state, pair.Value)) _ids.Add(pair.Key);
        var ids = _ids.ToArray();
        _ids.Clear();
        foreach (var id in ids) Complete(id, status);
    }

    /// <summary>The whole ledger (every sender's floor and started numbers) as bytes, for the mirror of host state a mod keeps on
    /// every player.</summary>
    public byte[] CaptureLedger()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(_ledger.Count);
            foreach (var pair in _ledger)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value.Floor);
                writer.Write(pair.Value.Seen.Count);
                foreach (var seen in pair.Value.Seen) { writer.Write(seen.Key); writer.Write(seen.Value); }
            }
        }
        return stream.ToArray();
    }

    /// <summary>Replaces the ledger with one captured on the host that left. What was still running there counts as run: this player
    /// cannot finish it, and must not start it again.</summary>
    public void RestoreLedger(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        _ledger.Clear();
        var senders = reader.ReadInt32();
        for (var i = 0; i < senders; i++)
        {
            var window = new Window();
            _ledger[reader.ReadUInt64()] = window;
            window.Floor = reader.ReadUInt32();
            var count = reader.ReadInt32();
            for (var j = 0; j < count; j++) { window.Seen[reader.ReadUInt32()] = true; reader.ReadBoolean(); }
        }
    }

    void NetChannel.IPart.EndSession()
    {
        _ledger.Clear();
        CompleteAll(0, static (_, _) => true, NetAnswerStatus.Left);
    }

    void NetChannel.IPart.Forget(ulong player)
    {
        _ledger.Remove(player);
        CompleteAll(player, static (who, pending) => pending.To == who, NetAnswerStatus.Left);
    }

    void NetChannel.IPart.OnWorld(NetWorld old, NetWorld next)
    {
        if (_pending.Count == 0) return;
        var node = _channel.Node;
        // The host left with requests unanswered: the new host is asked the same numbers, and its ledger says which already ran.
        var carry = next.Reason == NetWorldReason.Host && !node.IsHost;
        // Only what is carried is retargeted: a handler completed here may ask again, and that request keeps its own recipient.
        if (carry) foreach (var pair in _pending) if (pair.Value.ToHost) _ids.Add(pair.Key);
        var carried = _ids.ToArray();
        _ids.Clear();
        CompleteAll(carry, static (moving, pending) => !(moving && pending.ToHost), NetAnswerStatus.WorldChanged);
        foreach (var id in carried)
        {
            if (!_pending.TryGetValue(id, out var pending)) continue;
            pending.To = node.Host;
            pending.Waiting = true;
        }
        Resend(node.Host);
    }

    void NetChannel.IPart.Sync(ulong player) => Resend(player);

    private void Resend(ulong player)
    {
        if (_pending.Count == 0 || !_channel.Compatible(player)) return;
        foreach (var pair in _pending)
            if (pair.Value.Waiting && pair.Value.To == player) SendAsk(pair.Key, pair.Value);
    }

    void NetChannel.IPart.Tick()
    {
        CompleteAll(_channel.Node.Now, static (now, pending) => now >= pending.Deadline, NetAnswerStatus.TimedOut);
    }
}
