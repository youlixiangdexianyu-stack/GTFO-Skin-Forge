using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeCore;

/// <summary>What a channel knows about another player's copy of the same mod.</summary>
public enum PeerState : byte
{
    /// <summary>No answer yet (just joined, the mod is not installed there, or one side still holds its content).</summary>
    Unknown,
    /// <summary>Same message formats and content: messages are exchanged.</summary>
    Compatible,
    /// <summary>Different message formats or content: both sides logged it and nothing of this mod travels between the two.</summary>
    Refused,
}

/// <summary>How a message travels. It is fixed where the message is registered and every send of it follows it.</summary>
public enum NetDelivery : byte
{
    /// <summary>Reliable and ordered: requests, answers, state.</summary>
    Reliable,
    /// <summary>Unreliable (the game's non-critical channel): the network may lose it; one that arrives after a newer one of the same
    /// message from the same player is dropped. For frequent presentation messages such as shots and positions.</summary>
    Lossy,
}

/// <summary>Why <see cref="Net.World"/> moved on.</summary>
public enum NetWorldReason : byte
{
    None,
    /// <summary>A session began.</summary>
    Session,
    /// <summary>The game started building a level.</summary>
    Build,
    /// <summary>The level was cleaned up.</summary>
    Cleanup,
    /// <summary>The host changed.</summary>
    Host,
    /// <summary>A checkpoint was reloaded: the number stays and the checkpoint generation went up.</summary>
    Checkpoint,
}

/// <summary>The session's epoch: two numbers the host mints and every player holds. <see cref="Number"/> changes when a session begins, a
/// level starts building, a level is cleaned up and the host changes; <see cref="Checkpoint"/> counts the checkpoint reloads inside one
/// number. Number 0: no world (outside a session, or a client the host has not told yet).</summary>
public readonly record struct NetWorld(uint Number, ushort Checkpoint, NetWorldReason Reason);

/// <summary>Messages and payload bytes sent since the process started (<see cref="Net.Traffic"/>): all of them, and the
/// <see cref="NetDelivery.Lossy"/> share.</summary>
public readonly record struct NetTraffic(long Messages, long Bytes, long LossyMessages, long LossyBytes);

/// <summary>The network underneath: GTFO-API events in the game, an in-memory hub offline. Every message is a byte body sent to one player.</summary>
internal interface INetTransport
{
    void Register(string name, Action<ulong, byte[]> receive);
    void Send(string name, byte[] data, ulong to, NetDelivery delivery);
    ulong Local { get; }
    /// <summary>0 when there is no host (not in a session).</summary>
    ulong Host { get; }
    /// <summary>The other human players in the session, written into <paramref name="into"/>; returns how many.</summary>
    int Others(ulong[] into);
    /// <summary>True when <paramref name="lookup"/> is a human player this peer knows (the local player included), not a bot.</summary>
    bool IsHuman(ulong lookup);
}

/// <summary>Which part of <see cref="NetWorld"/> a message kind is stamped with and compared against on arrival.</summary>
internal enum NetScope : byte { Session, World, Checkpoint }

/// <summary>A struct as a message body and back, for byte-bodied messages (<see cref="NetChannel.Bytes"/>, requests, checkpoint copies).</summary>
public static class NetBody
{
    public static byte[] Of<T>(in T value) where T : unmanaged
    {
        var data = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(data, ref Unsafe.AsRef(in value));
        return data;
    }

    /// <summary>False when <paramref name="data"/> is not exactly one <typeparamref name="T"/>.</summary>
    public static bool TryRead<T>(ReadOnlySpan<byte> data, out T value) where T : unmanaged
    {
        if (data.Length != Unsafe.SizeOf<T>()) { value = default; return false; }
        value = MemoryMarshal.Read<T>(data);
        return true;
    }
}

/// <summary>What a channel's registered messages look like on the wire, as plain text that is the same on every machine: the
/// message name, how it travels, its kind and its field layout (each field's name and type in declaration order, plus its offset in
/// explicit-layout structs, nested structs expanded, fixed buffers with their length, plus the struct's byte size; a hand-coded byte
/// body gives its integer version instead). The fingerprint is FNV-1a over that text, so any change to a name, a delivery, a kind, a
/// layout or a version changes it.</summary>
internal static class NetFormat
{
    internal static string Bytes(int version) => "bytes#v" + version.ToString(CultureInfo.InvariantCulture);

    internal static string LayoutOf<T>() where T : unmanaged
    {
        var text = new StringBuilder();
        Describe(typeof(T), text);
        return text.Append('#').Append(Unsafe.SizeOf<T>()).ToString();
    }

    private static void Describe(Type type, StringBuilder text)
    {
        if (type.IsEnum) { text.Append("enum:").Append(Enum.GetUnderlyingType(type).Name); return; }
        if (type.IsPrimitive) { text.Append(type.Name); return; }
        text.Append('{');
        var first = true;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken))
        {
            if (!first) text.Append(',');
            first = false;
            text.Append(field.Name);
            if (type.IsExplicitLayout) text.Append('@').Append(field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0);
            text.Append(':');
            var buffer = field.GetCustomAttribute<FixedBufferAttribute>();
            if (buffer != null) { Describe(buffer.ElementType, text); text.Append('[').Append(buffer.Length).Append(']'); }
            else Describe(field.FieldType, text);
        }
        text.Append('}');
    }

    /// <summary>One message: its name, delivery and the hash of its kind and layout. The channel's fingerprint covers these in name order.</summary>
    internal readonly struct Entry
    {
        internal readonly string Name;
        internal readonly NetDelivery Delivery;
        internal readonly ulong Layout;
        internal Entry(string name, NetDelivery delivery, ulong layout) { Name = name; Delivery = delivery; Layout = layout; }
    }

    internal static ulong Fingerprint(List<Entry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            text.Append(entry.Name).Append('|').Append((int)entry.Delivery).Append('|').Append(entry.Layout.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
        return Fnv.Hash64(text.ToString());
    }

    /// <summary>The mod's name, its per-message list, then the channel's <see cref="NetChannel.ContentDetail"/> (empty when it has none).</summary>
    internal static byte[] Write(string modId, List<Entry> entries, byte[]? detail)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(modId);
            writer.Write(entries.Count);
            foreach (var entry in entries) { writer.Write(entry.Name); writer.Write((byte)entry.Delivery); writer.Write(entry.Layout); }
            writer.Write(detail?.Length ?? 0);
            if (detail != null) writer.Write(detail);
        }
        return stream.ToArray();
    }

    internal static List<Entry>? Read(byte[] data, out string modId, out byte[] detail)
    {
        detail = Array.Empty<byte>();
        modId = "";
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            modId = reader.ReadString();
            var count = reader.ReadInt32();
            if (count < 0 || count > 4096) return null;
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++) entries.Add(new Entry(reader.ReadString(), (NetDelivery)reader.ReadByte(), reader.ReadUInt64()));
            var length = reader.ReadInt32();
            if (length < 0 || length > data.Length) return null;
            detail = reader.ReadBytes(length);
            return detail.Length == length ? entries : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The messages that differ between this side ("here") and the other ("there"), by name; empty when they agree.</summary>
    internal static string Differences(List<Entry> here, List<Entry> there)
    {
        var theirs = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in there) theirs[entry.Name] = entry;
        var found = new List<string>();
        foreach (var entry in here.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (!theirs.TryGetValue(entry.Name, out var other)) { found.Add("message '" + entry.Name + "' is missing there"); continue; }
            if (other.Delivery != entry.Delivery) found.Add("message '" + entry.Name + "' travels " + entry.Delivery + " here but " + other.Delivery + " there");
            if (other.Layout != entry.Layout) found.Add("message '" + entry.Name + "' has a different kind, field layout or version");
        }
        var ours = new HashSet<string>(here.Select(e => e.Name), StringComparer.Ordinal);
        foreach (var entry in there.OrderBy(e => e.Name, StringComparer.Ordinal))
            if (!ours.Contains(entry.Name)) found.Add("message '" + entry.Name + "' exists only there");
        return string.Join("; ", found);
    }
}
