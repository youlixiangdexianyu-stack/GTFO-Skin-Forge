using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeCore.Tests;

/// <summary>An in-memory session: every endpoint is one player with its own <see cref="NetNode"/>. A send waits on the wire until
/// <see cref="Flush"/>, as the game delivers a packet after the frame that sent it.</summary>
internal sealed class Hub
{
    internal sealed record Packet(ulong From, ulong To, string Name, byte[] Data);

    internal readonly List<Endpoint> Players = new();
    internal readonly List<Packet> Delivered = new();
    internal List<Packet> Wire = new();
    internal ulong HostLookup;
    internal double Now;

    internal Endpoint Join(ulong lookup)
    {
        var player = new Endpoint(this, lookup);
        Players.Add(player);
        if (HostLookup == 0) HostLookup = lookup;
        foreach (var each in Players) each.Node.NoteSession();
        return player;
    }

    /// <summary><paramref name="player"/> leaves; when it hosted, <paramref name="host"/> takes over.</summary>
    internal void Leave(Endpoint player, ulong host = 0)
    {
        Players.Remove(player);
        if (HostLookup == player.Local) HostLookup = host;
        foreach (var each in Players) each.Node.NoteSession();
    }

    internal void Deliver(Packet packet)
    {
        var target = Players.Find(p => p.Local == packet.To);
        if (target == null || !target.Handlers.TryGetValue(packet.Name, out var handler)) return;
        Delivered.Add(packet);
        handler(packet.From, (byte[])packet.Data.Clone());
    }

    /// <summary>Delivers everything on the wire, and what those deliveries send. <paramref name="reverse"/>: the packets now on the wire arrive newest first.</summary>
    internal void Flush(bool reverse = false)
    {
        while (Wire.Count != 0)
        {
            var batch = Wire;
            Wire = new List<Packet>();
            if (reverse) batch.Reverse();
            reverse = false;
            foreach (var packet in batch) Deliver(packet);
        }
    }

    /// <summary>One frame for every player, then the wire.</summary>
    internal void Step(double seconds = 0.1)
    {
        Now += seconds;
        foreach (var player in Players.ToArray()) player.Node.Tick(Now);
        Flush();
    }

    internal void Settle() { for (var i = 0; i < 4; i++) Step(); }

    internal int Count(string name, ulong to) => Delivered.Count(p => p.Name == name && p.To == to);

    internal sealed class Endpoint : INetTransport
    {
        private readonly Hub _hub;
        internal readonly Dictionary<string, Action<ulong, byte[]>> Handlers = new();
        internal readonly NetNode Node;
        internal Endpoint(Hub hub, ulong lookup)
        {
            _hub = hub;
            Local = lookup;
            Node = new NetNode(this, "core");
        }
        public ulong Local { get; }
        public ulong Host => _hub.Players.Contains(this) ? _hub.HostLookup : 0;
        public void Register(string name, Action<ulong, byte[]> receive) => Handlers.Add(name, receive);
        public void Send(string name, byte[] data, ulong to, NetDelivery delivery) => _hub.Wire.Add(new Packet(Local, to, name, (byte[])data.Clone()));
        public bool IsHuman(ulong lookup) => _hub.Players.Exists(p => p.Local == lookup);
        public int Others(ulong[] into)
        {
            var n = 0;
            foreach (var p in _hub.Players) if (p != this) into[n++] = p.Local;
            return n;
        }
    }
}
