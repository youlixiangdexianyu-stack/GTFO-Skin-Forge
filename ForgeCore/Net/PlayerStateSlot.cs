using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ForgeCore;

/// <summary>
/// One value per player with the moment it began (<see cref="NetChannel.PlayerState{T}(string)"/>): who is doing what, since when. Each player
/// writes only their own; the host does not arbitrate. On the wire the moment is "seconds ago", turned back into this peer's own clock on
/// arrival, so clocks are never synchronised and the error is one trip's latency. A player whose copy answers later (a late joiner, a
/// reconnect) gets the current value with the seconds counted to then. It is a row stamped with the world number and checkpoint
/// generation (<see cref="NetLobbyValue"/>): a player who leaves, the session or level ending and a checkpoint reload clear it, a
/// host change keeps the local player's and sends it again, and a value from a world this peer has not reached yet waits for it.
/// Main thread only.
/// </summary>
public sealed class PlayerStateSlot<T> where T : unmanaged
{
    private readonly NetLobbyValue _row;
    private readonly NetNode _node;
    private readonly Dictionary<ulong, (T Value, float Start)> _values = new();
    private readonly ModLog _log;
    private T _own;
    private float _ownStart;

    internal PlayerStateSlot(NetLobbyValue row, NetNode node, ModLog log)
    {
        _row = row;
        _node = node;
        _log = log;
        row.Fresh = Frame;
        row.Changed += Received;
    }

    /// <summary>A player's value arrived (true) or went (false). The local player's own never raises it.</summary>
    public event Action<ulong, bool>? Changed;

    /// <summary>Sets the local player's value, begun at <paramref name="start"/> (this peer's clock), and sends it to the others. The
    /// value it already holds, begun at the same moment, is no change and sends nothing.</summary>
    public void Publish(T value, float start)
    {
        if (_row.Local != null && start == _ownStart && BytesOf(ref value).SequenceEqual(BytesOf(ref _own))) return;
        _own = value;
        _ownStart = start;
        _row.Keep(Frame());
    }

    private static ReadOnlySpan<byte> BytesOf(ref T value) => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

    /// <summary>Takes the local player's value back; nothing happens when there is none.</summary>
    public void Clear() => _row.Set(null);

    /// <summary>The current value of <paramref name="player"/> (the local player's too) and when it began on this peer's clock.</summary>
    public bool TryGet(ulong player, out T value, out float start)
    {
        if (player == _node.Local)
        {
            value = _own;
            start = _ownStart;
            return _row.Local != null;
        }
        if (_values.TryGetValue(player, out var held))
        {
            value = held.Value;
            start = held.Start;
            return true;
        }
        value = default;
        start = 0f;
        return false;
    }

    /// <summary>The seconds since it began, then the value; written at the moment of sending.</summary>
    private byte[] Frame()
    {
        var data = new byte[4 + Unsafe.SizeOf<T>()];
        BitConverter.TryWriteBytes(data.AsSpan(0), _node.Clock() - _ownStart);
        MemoryMarshal.Write(data.AsSpan(4), ref _own);
        return data;
    }

    private void Received(ulong sender)
    {
        if (!_row.TryGet(sender, out var data))
        {
            if (_values.Remove(sender)) Changed?.Invoke(sender, false);
            return;
        }
        if (data.Length != 4 + Unsafe.SizeOf<T>())
        {
            Net.Drop(Net.DropStateSize);
            _log.ErrorOnce(Net.DropStateSize, "A player state of " + _log.ModId + " came with " + data.Length + " bytes; it was dropped.");
            return;
        }
        var ago = BitConverter.ToSingle(data, 0);
        var value = MemoryMarshal.Read<T>(data.AsSpan(4));
        // A packet the sender could not have written (seconds that are not a finite non-negative number, a top-level float field that is NaN
        // or infinite) is counted and dropped; the sender's earlier value stays.
        if (!float.IsFinite(ago) || ago < 0f || !Finite(value))
        {
            Net.Drop(Net.DropStateInvalid);
            _log.ErrorOnce(Net.DropStateInvalid, "A player state of " + _log.ModId + " carried an impossible time or number; it was dropped.");
            return;
        }
        _values[sender] = (value, _row.ArrivedAt(sender) - ago);
        Changed?.Invoke(sender, true);
    }

    /// <summary>Where each top-level float (size 4) and double (size 8) field of <typeparamref name="T"/> lies in its bytes, found once:
    /// a default value with only that field set to a pattern whose every byte is non-zero shows the field's first byte. Floats inside a
    /// nested struct (a Vector3 field, say) are not checked.</summary>
    private static readonly (int At, int Size)[] FloatFields = FloatsOf();

    private static (int At, int Size)[] FloatsOf()
    {
        var found = new List<(int, int)>();
        foreach (var field in typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            var single = field.FieldType == typeof(float);
            if (!single && field.FieldType != typeof(double)) continue;
            object probe = default(T);
            field.SetValue(probe, single ? (object)BitConverter.Int32BitsToSingle(0x01010101) : BitConverter.Int64BitsToDouble(0x0101010101010101));
            var value = (T)probe;
            var bytes = BytesOf(ref value);
            var at = 0;
            while (bytes[at] == 0) at++;
            found.Add((at, single ? 4 : 8));
        }
        return found.ToArray();
    }

    private static bool Finite(T value)
    {
        var bytes = BytesOf(ref value);
        foreach (var (at, size) in FloatFields)
            if (size == 4 ? !float.IsFinite(BitConverter.ToSingle(bytes.Slice(at))) : !double.IsFinite(BitConverter.ToDouble(bytes.Slice(at)))) return false;
        return true;
    }
}
