using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ForgeCore;

/// <summary>A table only the host writes and every compatible player holds: state that must be the same for everyone, such as a
/// countdown, a switch or a document kept in parts. Its rows are byte strings: of any length
/// (<see cref="NetChannel.Table{TKey}"/>), or one struct each through <see cref="NetTable{TKey, TRow}"/>, which is this table with
/// rows of one size. The host's writes of one frame leave together as one message holding only the rows whose bytes changed; a
/// player who joins, and every player after a checkpoint reload or a host change, gets the whole table (only that player). Rows are
/// stamped with the world number and checkpoint generation. A new world number empties the table on every player; a host change
/// does not: the new host goes on from the rows it held. A player that finds a hole in the changes (<c>net-table-gap</c>) or waits
/// for a table that does not come asks the host for the whole table again. <see cref="Changed"/> is raised on every player, the
/// host included, so one handler serves both sides.</summary>
public sealed class NetByteTable<TKey> : NetChannel.IPart where TKey : unmanaged, IEquatable<TKey>
{
    private const byte Delta = 0, Snapshot = 1, Resync = 2;
    private const double AskSeconds = 2;
    private static readonly int KeyBytes = Unsafe.SizeOf<TKey>();

    private readonly NetChannel _channel;
    private readonly NetChannel.Route _route;
    private readonly int _rowBytes;
    private readonly Dictionary<TKey, byte[]> _rows = new();
    private readonly HashSet<TKey> _dirty = new();
    private readonly HashSet<ulong> _owed = new();
    private readonly List<TKey> _keys = new(), _sending = new();
    private uint _revision;
    private bool _have;
    private double _askedAt;

    /// <summary><paramref name="rowBytes"/> is the size every row has, or -1 for rows of any length, each sent behind its length.</summary>
    internal NetByteTable(NetChannel channel, string name, int rowBytes, int maxBytes, string rowLayout)
    {
        _channel = channel;
        _rowBytes = rowBytes;
        _route = channel.Claim(name, NetDelivery.Reliable, NetScope.Checkpoint, true, maxBytes, "table:" + NetFormat.LayoutOf<TKey>() + rowLayout, Receive, this);
        channel.Add(this);
    }

    /// <summary>A row was set (its bytes) or removed (<c>null</c>) on this player's copy of the table.</summary>
    public event Action<TKey, byte[]?>? Changed;

    /// <summary>The rows this player holds. The arrays are the table's own: read them, do not write into them, and copy one that is kept
    /// past the <see cref="Changed"/> handler or the frame (on the host a row of the same length is written over its old array).</summary>
    public IReadOnlyDictionary<TKey, byte[]> Rows => _rows;

    public bool TryGet(TKey key, out byte[] row) => _rows.TryGetValue(key, out row!);

    /// <summary>The host sets a row; the same bytes again change nothing.</summary>
    public void Set(TKey key, ReadOnlySpan<byte> row)
    {
        RequireHost();
        if (_rowBytes >= 0 && row.Length != _rowBytes) throw new ArgumentException("A row of " + _channel.ModId + "'s table " + _route.Name + " is " + _rowBytes + " bytes, not " + row.Length + ".", nameof(row));
        byte[] copy;
        if (_rows.TryGetValue(key, out var old))
        {
            if (row.SequenceEqual(old)) return;
            if (old.Length == row.Length) { row.CopyTo(old); copy = old; }
            else copy = row.ToArray();
        }
        else copy = row.ToArray();
        _rows[key] = copy;
        _dirty.Add(key);
        Raise(key, copy);
    }

    /// <summary>The host removes a row.</summary>
    public bool Remove(TKey key)
    {
        RequireHost();
        if (!_rows.Remove(key)) return false;
        _dirty.Add(key);
        Raise(key, null);
        return true;
    }

    /// <summary>The host removes every row.</summary>
    public void Clear()
    {
        RequireHost();
        foreach (var key in new List<TKey>(_rows.Keys)) Remove(key);
    }

    private void RequireHost()
    {
        if (!_channel.IsHost) throw new InvalidOperationException("Only the host writes " + _channel.ModId + "'s table " + _route.Name + ".");
    }

    private void Raise(TKey key, byte[]? row)
    {
        try { Changed?.Invoke(key, row); }
        catch (Exception error) { _channel.Logger.ErrorOnce("core-message-handler-failed", "A " + _channel.ModId + " table handler failed: " + error); }
    }

    /// <summary>The kind, the revision, the row count, then per row: (changes only: 1 set / 0 removed), the key and, unless removed,
    /// the row (behind its length when rows have no one size).</summary>
    /// <summary>The keys are copied into a list first (a collection's own copy, no boxed enumerator) and the list is emptied after.</summary>
    private byte[] Encode(byte kind, ICollection<TKey> source)
    {
        var keys = _sending;
        keys.AddRange(source);
        var count = keys.Count;
        var delta = kind == Delta;
        var size = 9 + count * ((delta ? 1 : 0) + KeyBytes + (_rowBytes < 0 ? 4 : 0));
        for (var i = 0; i < count; i++)
            if (_rows.TryGetValue(keys[i], out var held)) size += held.Length;
        var data = new byte[size];
        data[0] = kind;
        BitConverter.TryWriteBytes(data.AsSpan(1), _revision);
        BitConverter.TryWriteBytes(data.AsSpan(5), count);
        var at = 9;
        for (var i = 0; i < count; i++)
        {
            var key = keys[i];
            if (delta) data[at++] = _rows.ContainsKey(key) ? (byte)1 : (byte)0;
            MemoryMarshal.Write(data.AsSpan(at), ref key);
            at += KeyBytes;
            if (!_rows.TryGetValue(key, out var row)) continue;
            if (_rowBytes < 0) { BitConverter.TryWriteBytes(data.AsSpan(at), row.Length); at += 4; }
            row.CopyTo(data.AsSpan(at));
            at += row.Length;
        }
        keys.Clear();
        return data;
    }

    /// <summary>Takes one row off the front of a message; false when the bytes do not hold one.</summary>
    private bool Read(ref ReadOnlySpan<byte> body, bool delta, out TKey key, out byte[]? row)
    {
        key = default;
        row = null;
        var present = true;
        if (delta)
        {
            if (body.Length < 1) return false;
            present = body[0] == 1;
            body = body.Slice(1);
        }
        if (body.Length < KeyBytes) return false;
        key = MemoryMarshal.Read<TKey>(body);
        body = body.Slice(KeyBytes);
        if (!present) return true;
        var length = _rowBytes;
        if (length < 0)
        {
            if (body.Length < 4) return false;
            length = BitConverter.ToInt32(body);
            body = body.Slice(4);
        }
        if (length < 0 || body.Length < length) return false;
        row = body.Slice(0, length).ToArray();
        body = body.Slice(length);
        return true;
    }

    private void Receive(ulong sender, byte[] data, int at)
    {
        ReadOnlySpan<byte> body = data.AsSpan(at);
        if (body.Length == 1 && body[0] == Resync)
        {
            if (_channel.IsHost) _owed.Add(sender);
            return;
        }
        if (!_channel.FromHost(sender)) { if (Net.FirstDrop(_channel.ModId, Net.DropNotHost)) Net.NoteDrop(_channel.ModId, Net.DropNotHost, "table '" + _route.Name + "' rows from player " + sender + ", who is not the host"); return; }
        if (body.Length < 9 || body[0] > Snapshot) { _channel.Malformed(_route, sender, data.Length); return; }
        var revision = BitConverter.ToUInt32(body.Slice(1));
        var count = BitConverter.ToInt32(body.Slice(5));
        body = body.Slice(9);
        if (count < 0) { _channel.Malformed(_route, sender, data.Length); return; }
        if (data[at] == Snapshot)
        {
            var rows = new Dictionary<TKey, byte[]>();
            for (var i = 0; i < count; i++)
            {
                if (!Read(ref body, false, out var key, out var row)) { _channel.Malformed(_route, sender, data.Length); return; }
                rows[key] = row!;
            }
            if (body.Length != 0) { _channel.Malformed(_route, sender, data.Length); return; }
            foreach (var key in _rows.Keys) if (!rows.ContainsKey(key)) _keys.Add(key);
            foreach (var key in _keys) { _rows.Remove(key); Raise(key, null); }
            _keys.Clear();
            _have = true;
            _revision = revision;
            foreach (var pair in rows)
            {
                if (_rows.TryGetValue(pair.Key, out var old) && pair.Value.AsSpan().SequenceEqual(old)) continue;
                _rows[pair.Key] = pair.Value;
                Raise(pair.Key, pair.Value);
            }
            return;
        }
        if (!_have || revision != _revision + 1)
        {
            if (Net.FirstDrop(_channel.ModId, Net.DropTableGap)) Net.NoteDrop(_channel.ModId, Net.DropTableGap, "table '" + _route.Name + "' change " + revision + " does not follow the " + _revision + " this player holds; the whole table is asked for again");
            _have = false;
            _askedAt = double.NegativeInfinity;
            return;
        }
        _revision = revision;
        for (var i = 0; i < count; i++)
        {
            if (!Read(ref body, true, out var key, out var row)) { _channel.Malformed(_route, sender, data.Length); _have = false; _askedAt = double.NegativeInfinity; return; }
            if (row == null) { if (_rows.Remove(key)) Raise(key, null); continue; }
            _rows[key] = row;
            Raise(key, row);
        }
    }

    private void Empty()
    {
        _dirty.Clear();
        _revision = 0;
        if (_rows.Count == 0) return;
        _keys.AddRange(_rows.Keys);
        _rows.Clear();
        foreach (var key in _keys) Raise(key, null);
        _keys.Clear();
    }

    void NetChannel.IPart.EndSession()
    {
        _owed.Clear();
        _have = false;
        Empty();
    }

    void NetChannel.IPart.Forget(ulong player) => _owed.Remove(player);

    void NetChannel.IPart.OnWorld(NetWorld old, NetWorld next)
    {
        if (next.Number != old.Number && next.Reason != NetWorldReason.Host) Empty();
        // Every player's copy is now of another world or generation: each is owed the whole table, and a client takes no change before it.
        var node = _channel.Node;
        _have = false;
        _askedAt = node.Now;
        _owed.Clear();
        if (node.IsHost)
            for (var i = 0; i < node.MemberCount; i++) _owed.Add(node.Member(i));
    }

    void NetChannel.IPart.Sync(ulong player)
    {
        if (_channel.IsHost) _owed.Add(player);
    }

    void NetChannel.IPart.Tick()
    {
        var node = _channel.Node;
        if (!node.IsHost)
        {
            if (_have || node.World.Number == 0 || node.Now - _askedAt < AskSeconds || _channel.HostPeer == 0) return;
            _askedAt = node.Now;
            _channel.Send(_route, stackalloc byte[] { Resync }, node.Host);
            return;
        }
        if (_dirty.Count != 0)
        {
            _revision++;
            var delta = Encode(Delta, _dirty);
            _dirty.Clear();
            // One message for every member: the channel keeps one copy of it, however many wait for the budget.
            if (_owed.Count == 0) _channel.SendOwned(_route, delta, 0);
            else
                for (var i = 0; i < node.MemberCount; i++)
                    if (!_owed.Contains(node.Member(i))) _channel.SendOwned(_route, delta, node.Member(i));
        }
        if (_owed.Count == 0) return;
        var snapshot = Encode(Snapshot, _rows.Keys);
        foreach (var player in _owed) _channel.SendOwned(_route, snapshot, player);
        _owed.Clear();
    }
}

/// <summary>A host table with one struct per row (<see cref="NetChannel.Table{TKey, TRow}"/>): <see cref="NetByteTable{TKey}"/>
/// with rows of one size, read and written as <typeparamref name="TRow"/>.</summary>
public sealed class NetTable<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly NetByteTable<TKey> _table;
    private readonly Dictionary<TKey, TRow> _rows = new();

    internal NetTable(NetChannel channel, string name)
    {
        _table = new NetByteTable<TKey>(channel, name, Unsafe.SizeOf<TRow>(), 1024 * 1024, NetFormat.LayoutOf<TRow>());
        _table.Changed += (key, bytes) =>
        {
            var row = bytes == null ? default : MemoryMarshal.Read<TRow>(bytes);
            if (bytes == null) _rows.Remove(key);
            else _rows[key] = row;
            Changed?.Invoke(key, row, bytes != null);
        };
    }

    /// <summary>A row was set (<c>true</c>, with its value) or removed (<c>false</c>) on this player's copy of the table.</summary>
    public event Action<TKey, TRow, bool>? Changed;

    /// <summary>The rows this player holds.</summary>
    public IReadOnlyDictionary<TKey, TRow> Rows => _rows;

    public bool TryGet(TKey key, out TRow row) => _rows.TryGetValue(key, out row);

    /// <summary>The host sets a row; the same value again changes nothing.</summary>
    public void Set(TKey key, TRow row) => _table.Set(key, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref row, 1)));

    /// <summary>The host removes a row.</summary>
    public bool Remove(TKey key) => _table.Remove(key);

    /// <summary>The host removes every row.</summary>
    public void Clear() => _table.Clear();
}
