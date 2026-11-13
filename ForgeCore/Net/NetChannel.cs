using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ForgeCore;

/// <summary>One mod's part of the Forge network (<see cref="Net.Open"/>). Message events are named <c>modId/name</c>. Every registered
/// message adds its name, delivery, kind and field layout (or hand-given version) to the mod's format fingerprint; ForgeCore's one
/// handshake carries that fingerprint and the mod's content hash for every mod at once, each mod reads only its own cell, and when
/// they differ the two players refuse each other for this mod alone (<see cref="Refused"/>, and an error in the mod's log naming the
/// messages that differ, from the format list the two sides then exchange). Nothing of the mod travels to or is taken from a player
/// who is not <see cref="PeerState.Compatible"/>.
/// <para>Seven ways to send, each with its epoch rule fixed here (<see cref="Net.World"/>):
/// <see cref="Event{T}"/> reliable (number and checkpoint generation compared, a message from another one is dropped and counted);
/// <see cref="Event{T}"/> <see cref="NetDelivery.Lossy"/> (the same, and one older than the last received is dropped);
/// <see cref="Request"/> (the same, answered once, run exactly once);
/// <see cref="Table{TKey, TRow}"/> (the host's rows, changed rows only, the whole table to a player who joins and to everyone after a checkpoint reload);
/// <see cref="Lobby"/>, <see cref="Manifest"/> and <see cref="PlayerState{T}"/> (each player's own row);
/// <see cref="CheckpointCopy"/> (number only: it outlives a checkpoint reload and a host change);
/// <see cref="Bytes"/> reliable (a body of any size to one player or all, split and sent within the send budget).</para>
/// Handlers run on the main thread as the game delivers each message. Senders are not authenticated by the network: a handler
/// checks the sender itself (<see cref="FromHost"/>, <see cref="IsPlayer"/>).</summary>
public sealed class NetChannel
{
    internal const byte PartWhole = 0, PartFirst = 1, PartNext = 2;

    /// <summary>The largest byte body a message takes unless its registration says otherwise.</summary>
    public const int DefaultMaxBytes = 64 * 1024;

    /// <summary>A registered message.</summary>
    internal sealed class Route
    {
        internal string Name = "", Event = "";
        internal NetDelivery Delivery;
        internal NetScope Scope;
        /// <summary>A reliable byte body: it starts with a part byte and may be split (<see cref="NetNode.ChunkBytes"/>).</summary>
        internal bool Chunked;
        internal int MaxBytes;
        /// <summary>Bodies of this message waiting for the send budget; while there are any, later ones wait behind them.</summary>
        internal int Queued;
        internal uint Sequence;
        internal Dictionary<ulong, uint>? Last;
        internal Dictionary<ulong, (byte[] Data, int Filled)>? Partial;
        internal Action<ulong, byte[], int> Handle = null!;
        /// <summary>The state this message carries (a table, a value, a checkpoint copy), told to send a player what it is owed
        /// (<see cref="IPart.Sync"/>) once a body of it was dropped because the send queue was full; null: a plain message or a request
        /// (which then times out), whose dropped body is only counted and reported.</summary>
        internal IPart? Owner;
        internal string ModId = "";
    }

    /// <summary>State a channel keeps per message kind (rows, tables, requests, copies), told what the channel is told.</summary>
    internal interface IPart
    {
        void EndSession();
        void Forget(ulong player);
        void OnWorld(NetWorld old, NetWorld next);
        /// <summary><paramref name="player"/> matches and holds nothing of this mod: send it what it is owed.</summary>
        void Sync(ulong player);
        void Tick();
    }

    private sealed class PeerInfo
    {
        internal PeerState State;
        internal bool Silent, ContentDiffers, Reported;
        internal ulong RefusedFormat, RefusedContent;
        internal List<NetFormat.Entry>? There;
        internal byte[]? ThereDetail;
    }

    private readonly NetNode _node;
    private readonly ModLog _log;
    private readonly Dictionary<ulong, PeerInfo> _peers = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly List<Route> _routes = new();
    private readonly List<NetFormat.Entry> _entries = new();
    private readonly List<IPart> _parts = new();
    private readonly NetLobbyValue _readyRow;
    private static readonly byte[] One = { 1 };
    private ulong _format;
    private bool _formatDirty = true, _held, _ready;
    private string _content = "";

    internal NetChannel(NetNode node, string modId)
    {
        _node = node;
        ModId = modId;
        _log = ForgeCore.Log.For(modId);
        _readyRow = Row("~ready", NetScope.World);
    }

    public string ModId { get; }
    internal NetNode Node => _node;
    internal ModLog Logger => _log;

    // ---- identity and verdicts ---------------------------------------------------------------------------------------------

    /// <summary>The fingerprint of every registered message, in hex; what the handshake compares. It changes with each
    /// registration, so read it after the mod has registered its messages.</summary>
    public string Format => Fingerprint().ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>An optional fingerprint of the content both sides must share besides the message formats (for example a hash of the
    /// definitions); changing it asks every player again. Setting it ends <see cref="HoldContent"/>.</summary>
    public string Content
    {
        get => _content;
        set
        {
            if (_content == (value ?? "") && !_held) return;
            _content = value ?? "";
            _held = false;
            AskAgain();
        }
    }

    /// <summary>This player does not know its content yet (its definitions are still loading, or it left the session's mod state):
    /// until <see cref="Content"/> is set no player is judged either way, so nobody is refused for a difference that is only a
    /// matter of time, and setting it asks every player again.</summary>
    public void HoldContent()
    {
        if (_held) return;
        _held = true;
        AskAgain();
    }

    /// <summary>What this player tells a player whose content differs, so each side can say what differs in its own words; sent once
    /// per refusal with the format list and read with <see cref="ContentDetailOf"/>. With a detail set, <see cref="Refused"/> waits for
    /// the other side's.</summary>
    public byte[]? ContentDetail { get; set; }

    /// <summary>The <see cref="ContentDetail"/> of a player this channel refused, or null while it has not arrived.</summary>
    public byte[]? ContentDetailOf(ulong player) => _peers.TryGetValue(player, out var peer) ? peer.ThereDetail : null;

    /// <summary>A refused player's message formats differ from this one's, which outranks any difference of content.</summary>
    public bool FormatDiffers(ulong player) => _peers.TryGetValue(player, out var peer) && peer.State == PeerState.Refused && peer.RefusedFormat != Fingerprint();

    /// <summary>Raised when a player's copy is refused: the player's lookup and the reason (for logs; a mod shows the player its own words).</summary>
    public event Action<ulong, string>? Refused;

    /// <summary>Raised when a player's copy matches this one and holds none of this mod's state: it answered for the first time,
    /// joined again, or the content changed on either side. ForgeCore has already sent that player this mod's rows, tables,
    /// readiness and checkpoint copies; the handler sends whatever else the mod keeps for a newcomer. It can fire more than once for
    /// one player, so what it sends must be safe to repeat.</summary>
    public event Action<ulong>? PeerSynced;

    /// <summary>Raised once per player who will not answer for this mod: its handshake does not list the mod, or it answered none of
    /// ForgeCore's greetings (ForgeCore missing or another build).</summary>
    public event Action<ulong>? PeerSilent;

    public PeerState State(ulong player) => _peers.TryGetValue(player, out var peer) ? peer.State : PeerState.Unknown;

    public bool IsHost => _node.IsHost;

    /// <summary>True while at least one player in the session runs a matching copy of this mod: nobody reads what is sent otherwise,
    /// so a mod that builds its messages ahead of sending them skips the work.</summary>
    public bool AnyPeer
    {
        get
        {
            foreach (var player in _peers.Keys)
                if (Compatible(player)) return true;
            return false;
        }
    }

    /// <summary>True when this peer is a client and <paramref name="sender"/> is the host: the check a handler of a host-to-client message
    /// makes on the sender the network gives it. False on the host itself and when there is no host.</summary>
    public bool FromHost(ulong sender) => !_node.IsHost && _node.Host != 0 && _node.Host == sender;

    /// <summary>True when <paramref name="lookup"/> is a human player this peer knows (the local player included), not a bot: the check
    /// a handler of a player-to-host message makes before acting for that player.</summary>
    public bool IsPlayer(ulong lookup) => _node.Transport.IsHuman(lookup);

    internal ulong Fingerprint()
    {
        if (_formatDirty) { _format = NetFormat.Fingerprint(_entries); _formatDirty = false; }
        return _format;
    }

    internal ulong ContentHash => Fnv.Hash64(_content);
    internal bool Held => _held;
    internal bool Compatible(ulong player) => _peers.TryGetValue(player, out var peer) && peer.State == PeerState.Compatible && _node.IsMember(player);

    /// <summary>Forgets every verdict and has every player greeted again: what this mod shares changed, so every player's copy is
    /// judged afresh (the other mods' verdicts stand) and <see cref="PeerSynced"/> follows for each that matches.</summary>
    private void AskAgain()
    {
        foreach (var peer in _peers.Values)
        {
            peer.State = PeerState.Unknown;
            peer.Silent = peer.Reported = false;
            peer.RefusedFormat = peer.RefusedContent = 0;
            peer.There = null;
            peer.ThereDetail = null;
        }
        _node.GreetAgain();
    }

    private PeerInfo Info(ulong player)
    {
        if (!_peers.TryGetValue(player, out var peer)) _peers[player] = peer = new PeerInfo();
        return peer;
    }

    /// <summary>This mod's cell of <paramref name="sender"/>'s handshake. A player is sent this mod's state when its verdict becomes
    /// compatible, and only then: a verdict is forgotten when the player leaves, when either side's content or formats change and
    /// while either side holds its content, so a compatible player always holds what it was sent.</summary>
    internal void Judge(ulong sender, ulong format, ulong content, bool held)
    {
        var peer = Info(sender);
        // Either side's content is still unknown: neither is judged. The side that holds greets again when it knows.
        if (_held || held)
        {
            if (peer.State == PeerState.Compatible) ForgetState(sender);
            peer.State = PeerState.Unknown;
            // The sender restarted what it holds: the verdict remembered is void, so the next judgement sends the format list again.
            if (held) { peer.RefusedFormat = peer.RefusedContent = 0; peer.Reported = false; peer.There = null; peer.ThereDetail = null; }
            return;
        }
        var formatMatches = format == Fingerprint();
        var contentMatches = content == ContentHash;
        if (formatMatches && contentMatches)
        {
            if (peer.State == PeerState.Compatible) return;
            peer.State = PeerState.Compatible;
            foreach (var part in _parts) part.Sync(sender);
            Raise(PeerSynced, sender);
            return;
        }
        if (peer.State == PeerState.Compatible) ForgetState(sender);
        peer.State = PeerState.Refused;
        if (peer.RefusedFormat == format && peer.RefusedContent == content) return;
        peer.RefusedFormat = format;
        peer.RefusedContent = content;
        peer.ContentDiffers = !contentMatches;
        peer.Reported = false;
        peer.There = null;
        peer.ThereDetail = null;
        if (formatMatches && ContentDetail == null) { peer.Reported = true; Refuse(sender, "same message formats but different content"); }
        else _node.SendFormats(sender, NetFormat.Write(ModId, _entries, ContentDetail));
    }

    internal void ReceiveFormats(ulong sender, List<NetFormat.Entry> there, byte[] detail)
    {
        var peer = Info(sender);
        peer.There = there;
        peer.ThereDetail = detail;
        // Once a player is refused and its list is here, say what differs, once: the messages by name.
        if (peer.Reported || peer.State != PeerState.Refused) return;
        peer.Reported = true;
        if (peer.RefusedFormat == Fingerprint()) { Refuse(sender, "same message formats but different content"); return; }
        var differences = NetFormat.Differences(_entries, there);
        Refuse(sender, "message formats differ (" + (differences.Length == 0 ? "no per-message difference found" : differences) + ")" + (peer.ContentDiffers ? " and content differs" : ""));
    }

    private void Refuse(ulong sender, string reason)
    {
        _log.Error("core-player-refused-messages-from-that-player", "Player " + sender + " refused for " + ModId + ": " + reason + ". Nothing of " + ModId + " travels between the two; the other Forge mods are judged on their own.");
        try { Refused?.Invoke(sender, reason); }
        catch (Exception error) { _log.Error("core-refusal-handler-failed", "A " + ModId + " refusal handler failed: " + error); }
    }

    internal void Silent(ulong player)
    {
        var peer = Info(player);
        if (peer.Silent) return;
        peer.Silent = true;
        Raise(PeerSilent, player);
    }

    private void Raise(Action<ulong>? handlers, ulong player)
    {
        try { handlers?.Invoke(player); }
        catch (Exception error) { _log.ErrorOnce("core-peer-handler-failed", "A " + ModId + " peer handler failed: " + error); }
    }

    // ---- what the node tells the channel -----------------------------------------------------------------------------------

    internal void EndSession()
    {
        _peers.Clear();
        _ready = false;
        foreach (var route in _routes) { route.Last?.Clear(); route.Partial?.Clear(); route.Queued = 0; }
        foreach (var part in _parts) part.EndSession();
    }

    /// <summary><paramref name="left"/>: the player is gone from the session; otherwise only its state for this mod goes.</summary>
    internal void Forget(ulong player, bool left)
    {
        ForgetState(player);
        if (left) _peers.Remove(player);
    }

    private void ForgetState(ulong player)
    {
        foreach (var route in _routes) { route.Last?.Remove(player); route.Partial?.Remove(player); }
        foreach (var part in _parts) part.Forget(player);
    }

    internal void OnWorld(NetWorld old, NetWorld next)
    {
        if (next.Number != old.Number && next.Reason != NetWorldReason.Host) _ready = false;
        foreach (var route in _routes) if (route.Scope != NetScope.Session) route.Partial?.Clear();
        foreach (var part in _parts) part.OnWorld(old, next);
    }

    internal void Tick()
    {
        foreach (var part in _parts) part.Tick();
    }

    internal void Add(IPart part) => _parts.Add(part);

    // ---- readiness ---------------------------------------------------------------------------------------------------------

    /// <summary>Whether this player's mod is ready in the current world (assets loaded, presentation up). Every compatible player is
    /// told when it changes and when they join; a new world number (other than a host change) puts it back to false.</summary>
    public bool Ready
    {
        get => _ready;
        set
        {
            if (_ready == value) return;
            _ready = value;
            _readyRow.Set(value ? One : null);
        }
    }

    /// <summary>The host said it is ready in the current world (on the host: <see cref="Ready"/>).</summary>
    public bool HostReady => _node.IsHost ? _ready : _readyRow.TryGet(_node.Host, out _);

    /// <summary><paramref name="player"/> is compatible and said it is ready in the current world.</summary>
    public bool PeerReady(ulong player) => State(player) == PeerState.Compatible && _readyRow.TryGet(player, out _);

    // ---- registration ------------------------------------------------------------------------------------------------------

    internal Route Claim(string name, NetDelivery delivery, NetScope scope, bool chunked, int maxBytes, string layout, Action<ulong, byte[], int> handle, IPart? owner = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name the message.", nameof(name));
        if (!_names.Add(name)) throw new InvalidOperationException(ModId + " already registered message " + name + ".");
        var route = new Route { Name = name, Event = ModId + "/" + name, Delivery = delivery, Scope = scope, Chunked = chunked, MaxBytes = maxBytes, Handle = handle, Owner = owner, ModId = ModId };
        _routes.Add(route);
        _entries.Add(new NetFormat.Entry(name, delivery, Fnv.Hash64(layout)));
        // A message registered after players answered changes what they must share: ask again.
        _formatDirty = true;
        AskAgain();
        _node.Transport.Register(route.Event, (sender, data) => Arrive(route, sender, data));
        return route;
    }

    /// <summary>A fixed-size message. <paramref name="handler"/> gets the sender and the message; the returned handle sends it. Every
    /// player registers it the same way. Stamped with the world number and checkpoint generation: one from another world or
    /// generation is dropped and counted (<c>net-stale-world</c>). <see cref="NetDelivery.Lossy"/>: also sequenced per sender, one that
    /// arrives after a newer one is dropped (<c>net-out-of-order</c>).</summary>
    public NetEvent<T> Event<T>(string name, Action<ulong, T> handler, NetDelivery delivery = NetDelivery.Reliable) where T : unmanaged
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var size = Unsafe.SizeOf<T>();
        var route = Claim(name, delivery, NetScope.Checkpoint, false, size, "event:" + NetFormat.LayoutOf<T>(), (sender, data, at) =>
        {
            if (data.Length - at != size) { if (Net.FirstDrop(ModId, Net.DropMalformed)) Net.NoteDrop(ModId, Net.DropMalformed, "message '" + name + "' came with " + (data.Length - at) + " bytes instead of " + size); return; }
            handler(sender, MemoryMarshal.Read<T>(data.AsSpan(at)));
        });
        return new NetEvent<T>(this, route);
    }

    /// <summary>A hand-coded byte body of any length up to <paramref name="maxBytes"/>. Its <paramref name="version"/> is part of the
    /// format fingerprint: raise it whenever what the bytes mean changes. Epoch rule as <see cref="Event{T}"/>. Reliable: a body above
    /// 1 KiB is split by ForgeCore, sent within the send budget behind the frame's other messages and handed to the handler
    /// whole; this is the way to send a large block to one player. Lossy: the body must fit one packet (1 KiB).</summary>
    public NetBytes Bytes(string name, int version, Action<ulong, byte[]> handler, NetDelivery delivery = NetDelivery.Reliable, int maxBytes = DefaultMaxBytes)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var lossy = delivery == NetDelivery.Lossy;
        var route = Claim(name, delivery, NetScope.Checkpoint, !lossy, lossy ? Math.Min(maxBytes, NetNode.ChunkBytes) : maxBytes, "bytes:" + NetFormat.Bytes(version),
            (sender, data, at) => handler(sender, at == 0 ? data : data.AsSpan(at).ToArray()));
        return new NetBytes(this, route);
    }

    /// <summary>A request the receiver answers once and runs exactly once: see <see cref="NetRequest"/>.</summary>
    public NetRequest Request(string name, int version, Action<NetAsk> serve, int maxBytes = DefaultMaxBytes) => new(this, name, version, serve, maxBytes);

    /// <summary>A table only the host writes and every player reads: see <see cref="NetTable{TKey, TRow}"/>.</summary>
    public NetTable<TKey, TRow> Table<TKey, TRow>(string name) where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged => new(this, name);

    /// <summary>The same table with rows of bytes of any length (a document kept in parts, a state per object): see
    /// <see cref="NetByteTable{TKey}"/>. <paramref name="maxBytes"/> bounds one message, which is the whole table for a player who joins.</summary>
    public NetByteTable<TKey> Table<TKey>(string name, int version, int maxBytes = 1024 * 1024) where TKey : unmanaged, IEquatable<TKey>
        => new(this, name, -1, maxBytes, NetFormat.Bytes(version));

    /// <summary>A value every player of the session keeps for this mod under <paramref name="name"/> (lobby picks, the host's rules):
    /// each player sets only their own, and ForgeCore delivers it to every compatible player, again to each player whose copy of the
    /// mod newly matches (a player who joins later, a reconnect, a content change), and forgets the value of a player who left or
    /// was refused. It belongs to the session, not to a world.</summary>
    public NetLobbyValue Lobby(string name) => Row("lobby/" + name, NetScope.Session);

    /// <summary>A (content number, content hash) list per player for this mod under <paramref name="name"/>, matched entry by entry: see <see cref="NetManifest"/>.</summary>
    public NetManifest Manifest(string name) => new(Lobby("manifest/" + name), ModId, _log);

    /// <summary>A value per player with the moment it began, for this mod under <paramref name="name"/>: see <see cref="PlayerStateSlot{T}"/>.</summary>
    public PlayerStateSlot<T> PlayerState<T>(string name) where T : unmanaged
        => new(Row("state/" + name, NetScope.Checkpoint, "state:" + NetFormat.LayoutOf<T>()), _node, _log);

    /// <summary>The host's copy of what a checkpoint reload restores, held by every player: see <see cref="NetCheckpointCopy"/>.</summary>
    public NetCheckpointCopy CheckpointCopy(string name, int version, int maxBytes = 1024 * 1024) => new(this, name, version, maxBytes);

    private NetLobbyValue Row(string name, NetScope scope, string layout = "row") => new(this, name, scope, layout);

    // ---- sending and receiving ---------------------------------------------------------------------------------------------

    private static int HeadSize(Route route) => (route.Scope == NetScope.Session ? 0 : route.Scope == NetScope.World ? 4 : 6) + (route.Delivery == NetDelivery.Lossy ? 4 : 0);

    private void WriteHead(Route route, Span<byte> head)
    {
        var at = 0;
        var world = _node.World;
        if (route.Scope != NetScope.Session) { BitConverter.TryWriteBytes(head, world.Number); at = 4; }
        if (route.Scope == NetScope.Checkpoint) { BitConverter.TryWriteBytes(head.Slice(4), world.Checkpoint); at = 6; }
        if (route.Delivery == NetDelivery.Lossy) BitConverter.TryWriteBytes(head.Slice(at), ++route.Sequence);
    }

    /// <summary>Sends <paramref name="body"/> as <paramref name="route"/>'s message to <paramref name="to"/>, or to every compatible
    /// member when it is 0. Nothing leaves outside a session or toward a player who is not compatible.</summary>
    internal void Send(Route route, ReadOnlySpan<byte> body, ulong to) => Send(route, body, null, to);

    /// <summary><see cref="Send(Route, ReadOnlySpan{byte}, ulong)"/> of an array the caller hands over and never changes: a body that
    /// waits for the budget keeps it instead of a copy, so one array sent to several players is held once.</summary>
    internal void SendOwned(Route route, byte[] body, ulong to) => Send(route, body, body, to);

    private void Send(Route route, ReadOnlySpan<byte> body, byte[]? owned, ulong to)
    {
        if (_node.Host == 0) return;
        if (body.Length > route.MaxBytes) throw new ArgumentException(ModId + " message " + route.Name + " takes at most " + route.MaxBytes + " bytes, not " + body.Length + ".");
        var size = HeadSize(route);
        if (route.Chunked && (body.Length > NetNode.ChunkBytes || route.Queued != 0))
        {
            var head = new byte[size];
            WriteHead(route, head);
            var copy = owned ?? body.ToArray();
            if (to != 0) { if (Compatible(to)) _node.Enqueue(route, to, head, copy); return; }
            for (var i = 0; i < _node.MemberCount; i++)
                if (Compatible(_node.Member(i))) _node.Enqueue(route, _node.Member(i), head, copy);
            return;
        }
        if (to != 0 && !Compatible(to)) return;
        var packet = new byte[size + (route.Chunked ? 1 : 0) + body.Length];
        WriteHead(route, packet);
        if (route.Chunked) packet[size++] = PartWhole;
        body.CopyTo(packet.AsSpan(size));
        if (to != 0) { _node.Send(route.Event, packet, to, route.Delivery); return; }
        for (var i = 0; i < _node.MemberCount; i++)
            if (Compatible(_node.Member(i))) _node.Send(route.Event, packet, _node.Member(i), route.Delivery);
    }

    /// <summary>The host as a recipient, or 0 when this player is the host, there is none or it is not compatible.</summary>
    internal ulong HostPeer => !_node.IsHost && Compatible(_node.Host) ? _node.Host : 0;

    private void Arrive(Route route, ulong sender, byte[] data)
    {
        try
        {
            _node.Settle();
            if (sender == 0 || sender == _node.Local || data == null) return;
            if (!_node.IsMember(sender)) { _node.NoteUnlisted(sender); Net.Drop(Net.DropPeerUnverified); return; }
            var state = State(sender);
            if (state != PeerState.Compatible) { Net.Drop(state == PeerState.Refused ? Net.DropPeerRefused : Net.DropPeerUnverified); return; }
            var at = HeadSize(route);
            if (data.Length < at + (route.Chunked ? 1 : 0)) { Malformed(route, sender, data.Length); return; }
            if (route.Scope != NetScope.Session)
            {
                var number = BitConverter.ToUInt32(data, 0);
                var checkpoint = route.Scope == NetScope.Checkpoint ? BitConverter.ToUInt16(data, 4) : (ushort)0;
                if (!_node.Accepts(route.Scope, number, checkpoint))
                {
                    route.Partial?.Remove(sender);
                    var world = _node.World;
                    if (Net.FirstDrop(ModId, Net.DropStaleWorld)) Net.NoteDrop(ModId, Net.DropStaleWorld, "message '" + route.Name + "' from player " + sender + " was stamped world " + number + "." + checkpoint + " while this player is in " + world.Number + "." + world.Checkpoint);
                    return;
                }
            }
            if (route.Delivery == NetDelivery.Lossy)
            {
                var sequence = BitConverter.ToUInt32(data, at - 4);
                route.Last ??= new Dictionary<ulong, uint>();
                if (route.Last.TryGetValue(sender, out var last) && sequence <= last)
                {
                    if (Net.FirstDrop(ModId, Net.DropOutOfOrder)) Net.NoteDrop(ModId, Net.DropOutOfOrder, "message '" + route.Name + "' from player " + sender + " arrived after a newer one");
                    return;
                }
                route.Last[sender] = sequence;
            }
            if (route.Chunked)
            {
                var kind = data[at++];
                if (kind != PartWhole)
                {
                    var whole = Assemble(route, sender, data, at, kind);
                    if (whole == null) return;
                    data = whole;
                    at = 0;
                }
            }
            if (data.Length - at > route.MaxBytes) { Oversized(route, sender); return; }
            route.Handle(sender, data, at);
        }
        catch (Exception error) { _log.ErrorOnce("core-message-handler-failed", "A " + ModId + " message handler failed: " + error); }
    }

    private byte[]? Assemble(Route route, ulong sender, byte[] data, int at, byte kind)
    {
        route.Partial ??= new Dictionary<ulong, (byte[] Data, int Filled)>();
        if (kind == PartFirst)
        {
            if (data.Length < at + 4) { Malformed(route, sender, data.Length); return null; }
            var total = BitConverter.ToInt32(data, at);
            at += 4;
            if (total <= 0 || total > route.MaxBytes) { Oversized(route, sender); return null; }
            route.Partial[sender] = (new byte[total], 0);
        }
        else if (kind != PartNext || !route.Partial.ContainsKey(sender))
        {
            if (Net.FirstDrop(ModId, Net.DropChunk)) Net.NoteDrop(ModId, Net.DropChunk, "a part of message '" + route.Name + "' from player " + sender + " had no beginning");
            return null;
        }
        var (buffer, filled) = route.Partial[sender];
        var size = data.Length - at;
        if (size > buffer.Length - filled) { route.Partial.Remove(sender); Malformed(route, sender, data.Length); return null; }
        Array.Copy(data, at, buffer, filled, size);
        filled += size;
        if (filled < buffer.Length) { route.Partial[sender] = (buffer, filled); return null; }
        route.Partial.Remove(sender);
        return buffer;
    }

    internal void Malformed(Route route, ulong sender, int bytes)
    {
        if (Net.FirstDrop(ModId, Net.DropMalformed)) Net.NoteDrop(ModId, Net.DropMalformed, "message '" + route.Name + "' from player " + sender + " came with " + bytes + " bytes that do not fit it");
    }

    private void Oversized(Route route, ulong sender)
    {
        if (Net.FirstDrop(ModId, Net.DropOversized)) Net.NoteDrop(ModId, Net.DropOversized, "message '" + route.Name + "' from player " + sender + " was larger than the " + route.MaxBytes + " bytes it takes");
    }
}

/// <summary>A fixed-size message of a channel (<see cref="NetChannel.Event{T}"/>): how it is sent. The local player's own handler is not run.</summary>
public sealed class NetEvent<T> where T : unmanaged
{
    private readonly NetChannel _channel;
    private readonly NetChannel.Route _route;

    internal NetEvent(NetChannel channel, NetChannel.Route route)
    {
        _channel = channel;
        _route = route;
    }

    /// <summary>To the host; false (nothing sent) when this player is the host, there is no host or the host is not compatible.</summary>
    public bool ToHost(T message)
    {
        var host = _channel.HostPeer;
        if (host != 0) To(host, message);
        return host != 0;
    }

    /// <summary>To one player; nothing is sent to a player who is not compatible.</summary>
    public void To(ulong player, T message)
    {
        if (player == 0) throw new ArgumentException("Name the player.", nameof(player));
        Span<byte> body = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(body, ref message);
        _channel.Send(_route, body, player);
    }

    /// <summary>To every other compatible player.</summary>
    public void Broadcast(T message)
    {
        Span<byte> body = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(body, ref message);
        _channel.Send(_route, body, 0);
    }
}

/// <summary>A byte-bodied message of a channel (<see cref="NetChannel.Bytes"/>): how it is sent. The local player's own handler is not run.</summary>
public sealed class NetBytes
{
    private readonly NetChannel _channel;
    private readonly NetChannel.Route _route;

    internal NetBytes(NetChannel channel, NetChannel.Route route)
    {
        _channel = channel;
        _route = route;
    }

    /// <summary>To the host; false (nothing sent) when this player is the host, there is no host or the host is not compatible.</summary>
    public bool ToHost(byte[] body)
    {
        var host = _channel.HostPeer;
        if (host != 0) _channel.Send(_route, body, host);
        return host != 0;
    }

    /// <summary>To one player; nothing is sent to a player who is not compatible.</summary>
    public void To(ulong player, byte[] body) => _channel.Send(_route, body, player != 0 ? player : throw new ArgumentException("Name the player.", nameof(player)));

    /// <summary>To every other compatible player.</summary>
    public void Broadcast(byte[] body) => _channel.Send(_route, body, 0);
}
