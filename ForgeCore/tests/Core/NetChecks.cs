using System.Runtime.InteropServices;

namespace ForgeCore.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Ping { public uint Sequence; public float Value; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Wide { public uint Sequence; public float Value; public ulong Extra; }

/// <summary>The network's rules without the game: the one handshake and its refusal, the epoch rules per kind of message, the
/// exactly-once request ledger, splitting under the frame budget, and the host's table.</summary>
internal static class NetChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        var notices = new List<string>();
        Net.Notice = notices.Add;
        Handshake(check, errors);
        Epoch(check, notices);
        Rows(check);
        Ledger(check);
        Budget(check);
        Table(check);
    }

    private static void Handshake(Action<bool, string> check, List<string> errors)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        var got = 0;
        var hostAlpha = host.Node.Open("alpha");
        var clientAlpha = client.Node.Open("alpha");
        var ping = hostAlpha.Event<Ping>("ping", (_, _) => { });
        clientAlpha.Event<Ping>("ping", (_, _) => got++);
        var hostBeta = host.Node.Open("beta");
        var clientBeta = client.Node.Open("beta");
        var refusedBy = hostBeta.Event<Ping>("x", (_, _) => { });
        clientBeta.Event<Wide>("x", (_, _) => { });
        var hostGamma = host.Node.Open("gamma");
        string? reason = null;
        var silent = 0;
        var synced = 0;
        hostBeta.Refused += (_, why) => reason = why;
        hostGamma.PeerSilent += _ => silent++;
        hostAlpha.PeerSynced += _ => synced++;
        hub.Settle();
        check(hostAlpha.State(2) == PeerState.Compatible && clientAlpha.State(1) == PeerState.Compatible && synced == 1, "handshake: a matching mod is compatible on both sides and synced once");
        check(hostBeta.State(2) == PeerState.Refused && clientBeta.State(1) == PeerState.Refused, "handshake: a mod whose message layout differs is refused on both sides");
        check(reason != null && reason.Contains("'x'") && errors.Exists(e => e.Contains("refused for beta") && e.Contains("'x'")), "handshake: the refusal names the mod and the message that differs");
        check(silent == 1 && hostGamma.State(2) == PeerState.Unknown, "handshake: a mod the other player does not list is reported silent once");
        check(hub.Count("Forge/hello", 2) == 2 && !hub.Delivered.Exists(p => p.Name.Contains("handshake")), "handshake: one greeting and one answer carry every mod");
        refusedBy.Broadcast(new Ping());
        ping.Broadcast(new Ping { Sequence = 7 });
        check(!hub.Wire.Exists(p => p.Name == "beta/x"), "handshake: nothing of a refused mod leaves");
        hub.Flush();
        check(got == 1, "handshake: the other mods keep talking");
    }

    private static void Epoch(Action<bool, string> check, List<string> notices)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        var hostMod = host.Node.Open("epoch");
        var clientMod = client.Node.Open("epoch");
        var hostGot = 0;
        var clientGot = 0;
        var positions = new List<uint>();
        var hostPing = hostMod.Event<Ping>("ping", (_, _) => hostGot++);
        var clientPing = clientMod.Event<Ping>("ping", (_, _) => clientGot++);
        var hostPos = hostMod.Event<Ping>("pos", (_, _) => { }, NetDelivery.Lossy);
        clientMod.Event<Ping>("pos", (_, m) => positions.Add(m.Sequence), NetDelivery.Lossy);
        var hostCopy = hostMod.CheckpointCopy("copy", 1);
        var clientCopy = clientMod.CheckpointCopy("copy", 1);
        hub.Settle();
        check(host.Node.World.Number == 1 && client.Node.World == host.Node.World, "epoch: the host mints the session's world and the client holds the same");

        clientMod.Ready = true;
        hub.Flush();
        check(hostMod.PeerReady(2), "epoch: readiness reaches the host");

        // The host moves on while a client's message stamped with the earlier world is on its way.
        var stale = Net.Dropped(Net.DropStaleWorld);
        host.Node.NoteWorld(NetWorldReason.Build);
        clientPing.ToHost(new Ping());
        hub.Flush();
        check(hostGot == 0 && Net.Dropped(Net.DropStaleWorld) == stale + 1 && client.Node.World.Number == 2, "epoch: a message from an earlier world is dropped and counted");
        check(!hostMod.PeerReady(2) && !clientMod.Ready, "epoch: a new world number resets readiness");

        hostCopy.Publish(new byte[] { 5 });
        hub.Flush();
        host.Node.NoteWorld(NetWorldReason.Checkpoint);
        hostPing.Broadcast(new Ping());
        hub.Flush();
        check(client.Node.World.Number == 2 && client.Node.World.Checkpoint == 1 && clientGot == 1, "epoch: a checkpoint reload keeps the number and raises the generation");
        check(clientCopy.Value is { Length: 1 } copy && copy[0] == 5, "epoch: the checkpoint copy outlives the reload");
        clientPing.ToHost(new Ping());
        host.Node.NoteWorld(NetWorldReason.Checkpoint);
        hub.Flush();
        check(hostGot == 0 && Net.Dropped(Net.DropStaleWorld) == stale + 2, "epoch: a one-shot message of an earlier checkpoint generation is dropped");
        check(notices.Count(n => n.Contains("mod=epoch") && n.Contains(Net.DropStaleWorld)) == 1, "epoch: drops are logged once per mod and code, then only counted");

        // The same game event reaches the client before the host's announcement, then after it.
        var changes = 0;
        client.Node.WorldChanged += _ => changes++;
        client.Node.NoteWorld(NetWorldReason.Cleanup);
        host.Node.NoteWorld(NetWorldReason.Cleanup);
        hub.Flush();
        check(changes == 1 && client.Node.World == host.Node.World && host.Node.World.Number == 3, "epoch: a client ahead of the host's announcement lands on the same world");
        check(clientCopy.Value == null, "epoch: the checkpoint copy goes with the level");
        host.Node.NoteWorld(NetWorldReason.Build);
        hub.Flush();
        client.Node.NoteWorld(NetWorldReason.Build);
        check(changes == 2 && client.Node.World == host.Node.World && host.Node.World.Number == 4, "epoch: a client behind the host's announcement does not move twice");

        // A client that reached a higher number in a session of its own before it joined this host: its guess is still the number the host mints.
        var later = new Hub();
        var veteran = later.Join(2);
        veteran.Node.Open("later");
        later.Settle();
        veteran.Node.NoteWorld(NetWorldReason.Build);
        veteran.Node.NoteWorld(NetWorldReason.Build);
        later.Leave(veteran);
        veteran.Node.NoteSession();
        veteran.Node.Tick(later.Now);
        var fresh = later.Join(1);
        fresh.Node.Open("later");
        later.Settle();
        later.Players.Add(veteran);
        foreach (var each in later.Players) each.Node.NoteSession();
        later.Settle();
        var behind = fresh.Node.World.Number;
        veteran.Node.NoteWorld(NetWorldReason.Build);
        var guess = veteran.Node.World;
        fresh.Node.NoteWorld(NetWorldReason.Build);
        later.Flush();
        check(behind < 3 && veteran.Node.World == guess && fresh.Node.World == guess && guess.Number > 3, "epoch: a client that has seen higher numbers than the host's world guesses the number the host mints");

        var late = Net.Dropped(Net.DropOutOfOrder);
        hostPos.Broadcast(new Ping { Sequence = 1 });
        hostPos.Broadcast(new Ping { Sequence = 2 });
        hub.Flush(reverse: true);
        check(positions.Count == 1 && positions[0] == 2 && Net.Dropped(Net.DropOutOfOrder) == late + 1, "epoch: an unreliable message older than the last one received is dropped");
    }

    private static void Rows(Action<bool, string> check)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        float hostClock = 100f, clientClock = 500f;
        host.Node.Clock = () => hostClock;
        client.Node.Clock = () => clientClock;
        var hostMod = host.Node.Open("rows");
        var clientMod = client.Node.Open("rows");
        var hostPick = hostMod.Lobby("pick");
        var clientPick = clientMod.Lobby("pick");
        var hostState = hostMod.PlayerState<Ping>("act");
        var clientState = clientMod.PlayerState<Ping>("act");
        hostPick.Set(new byte[] { 7 });
        hub.Settle();
        check(clientPick.TryGet(1, out var pick) && pick[0] == 7, "rows: a value set before the other player answered reaches it");
        hostState.Publish(new Ping { Sequence = 4 }, 97f);
        hub.Flush();
        check(clientState.TryGet(1, out var state, out var start) && state.Sequence == 4 && Math.Abs(start - 497f) < 0.01f, "rows: a player state arrives with its start on the receiver's clock");
        // The state of the next world arrives before the world does.
        host.Node.NoteWorld(NetWorldReason.Build);
        hostState.Publish(new Ping { Sequence = 5 }, 100f);
        hub.Flush(reverse: true);
        check(clientState.TryGet(1, out state, out _) && state.Sequence == 5, "rows: a state from a world not reached yet waits for it");
        host.Node.NoteWorld(NetWorldReason.Cleanup);
        hub.Flush();
        check(!clientState.TryGet(1, out _, out _) && !hostState.TryGet(1, out _, out _) && clientPick.TryGet(1, out _), "rows: a new world clears player states and keeps session values");
        var joiner = hub.Join(3);
        var joinerMod = joiner.Node.Open("rows");
        var joinerPick = joinerMod.Lobby("pick");
        joinerMod.PlayerState<Ping>("act");
        hub.Settle();
        check(joinerPick.TryGet(1, out pick) && pick[0] == 7, "rows: a player who joins later gets the value");
        hub.Leave(host, 2);
        hub.Settle();
        check(!joinerPick.TryGet(1, out _) && !clientPick.TryGet(1, out _), "rows: the value of a player who left is forgotten");
    }

    private static void Ledger(Action<bool, string> check)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        var third = hub.Join(3);
        var served = 0;
        var thirdServed = 0;
        NetAsk? held = null;
        var hostRequest = host.Node.Open("ledger").Request("do", 1, ask => { served++; held = ask; });
        var clientRequest = client.Node.Open("ledger").Request("do", 1, _ => { });
        var thirdRequest = third.Node.Open("ledger").Request("do", 1, _ => thirdServed++);
        hub.Settle();

        NetAnswer? answer = null;
        clientRequest.AskHost(new byte[] { 1, 2, 3 }, a => answer = a);
        hub.Flush();
        check(served == 1 && held != null && held.Body.Length == 3 && held.From == 2, "ledger: the receiver runs the request");
        var packet = hub.Delivered.Last(p => p.Name == "ledger/do");
        var duplicate = Net.Dropped(Net.DropRequestDuplicate);
        hub.Deliver(packet);
        check(served == 1 && Net.Dropped(Net.DropRequestDuplicate) == duplicate + 1, "ledger: the same request arriving again while it runs is not run again");
        held!.Answer(new byte[] { 9 });
        hub.Flush();
        check(answer is { Status: NetAnswerStatus.Answered } && answer.Value.Body[0] == 9, "ledger: the asker gets the answer once");
        var unknown = Net.Dropped(Net.DropAnswerUnknown);
        hub.Deliver(packet);
        hub.Flush();
        check(served == 1 && Net.Dropped(Net.DropAnswerUnknown) == unknown + 1, "ledger: a request that already ran is answered as already run, not run again");

        answer = null;
        clientRequest.AskHost(new byte[] { 4 }, a => answer = a, 1);
        hub.Flush();
        hub.Step(2);
        check(served == 2 && answer is { Status: NetAnswerStatus.TimedOut }, "ledger: an unanswered request ends with a timeout");

        // The host leaves with a request running; the player who takes over holds the ledger the mod mirrored.
        answer = null;
        clientRequest.AskHost(new byte[] { 5 }, a => answer = a, 60);
        hub.Flush();
        var staleBefore = Net.Dropped(Net.DropRequestStale);
        hub.Deliver(packet);
        check(served == 3 && Net.Dropped(Net.DropRequestStale) == staleBefore + 1, "ledger: a number below the asker's settled floor is dropped");
        thirdRequest.RestoreLedger(hostRequest.CaptureLedger());
        hub.Leave(host, 3);
        hub.Settle();
        check(client.Node.World == third.Node.World && third.Node.World.Reason == NetWorldReason.Host, "ledger: a host change moves every player to the new host's world");
        check(thirdServed == 0 && answer is { Status: NetAnswerStatus.AlreadyRan, From: 3 }, "ledger: the new host is asked the same number and does not run what the old host ran");
        answer = null;
        clientRequest.AskHost(new byte[] { 6 }, a => answer = a);
        hub.Flush();
        check(thirdServed == 1, "ledger: the new host runs a new request");
    }

    private static void Budget(Action<bool, string> check)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        byte[]? got = null;
        var small = 0;
        var hostMod = host.Node.Open("budget");
        var clientMod = client.Node.Open("budget");
        var blob = hostMod.Bytes("blob", 1, (_, _) => { }, maxBytes: 100_000);
        clientMod.Bytes("blob", 1, (_, data) => got = data, maxBytes: 100_000);
        var ping = hostMod.Event<Ping>("ping", (_, _) => { });
        clientMod.Event<Ping>("ping", (_, _) => small++);
        var lossy = hostMod.Bytes("burst", 1, (_, _) => { }, NetDelivery.Lossy);
        clientMod.Bytes("burst", 1, (_, _) => { }, NetDelivery.Lossy);
        hub.Settle();
        var body = new byte[40_000];
        for (var i = 0; i < body.Length; i++) body[i] = (byte)(i * 31);
        var before = Net.Traffic.Bytes;
        blob.To(2, body);
        ping.To(2, new Ping());
        hub.Step();
        var frame = Net.Traffic.Bytes - before;
        check(got == null && small == 1 && frame <= NetNode.BurstBytes + NetNode.ChunkBytes + 64, "budget: a large body is split and one frame sends no more than the burst, ordinary messages first");
        hub.Step();
        hub.Step();
        check(got != null && got.AsSpan().SequenceEqual(body), "budget: the receiver gets the body whole");

        // The budget is a rate, not a share per frame: at 240 frames a second a frame sends a 240th of a second's worth.
        got = null;
        blob.To(2, body);
        hub.Step();
        before = Net.Traffic.Bytes;
        hub.Step(1.0 / 240);
        frame = Net.Traffic.Bytes - before;
        check(frame > 0 && frame <= NetNode.RateBytes / 240 + NetNode.ChunkBytes + 64, "budget: a short frame sends what its time earns, so the rate does not grow with the frame rate");
        while (got == null) hub.Step();

        // A full queue drops the next body and counts it, never silently; the table whose change was dropped is sent whole again.
        var big = hostMod.Bytes("big", 1, (_, _) => { }, maxBytes: NetNode.QueueBytes);
        clientMod.Bytes("big", 1, (_, _) => { }, maxBytes: NetNode.QueueBytes);
        var hostRows = hostMod.Table<int>("rows", 1, NetNode.QueueBytes);
        var clientRows = clientMod.Table<int>("rows", 1, NetNode.QueueBytes);
        hub.Settle();
        hostRows.Set(1, new byte[2 * NetNode.ChunkBytes]);
        hub.Step();
        var voided = Net.Dropped(Net.DropBulkVoid);
        big.To(2, new byte[NetNode.QueueBytes - NetNode.ChunkBytes]);
        hostRows.Set(1, new byte[3 * NetNode.ChunkBytes]);
        hub.Step();
        check(Net.Dropped(Net.DropBulkVoid) == voided + 1, "budget: a body that does not fit the full send queue is dropped and counted");
        for (var i = 0; i < 400 && !(clientRows.TryGet(1, out var row) && row.Length == 3 * NetNode.ChunkBytes); i++) hub.Step();
        check(clientRows.TryGet(1, out var kept) && kept.Length == 3 * NetNode.ChunkBytes, "budget: a table change dropped on a full queue reaches the player once the queue empties");
        // A lobby value's clear dropped on a full queue (it waits behind the value's own body) is sent again too.
        for (var i = 0; i < 200; i++) hub.Step();
        var hostSpot = hostMod.Lobby("spot");
        var clientSpot = clientMod.Lobby("spot");
        hub.Settle();
        voided = Net.Dropped(Net.DropBulkVoid);
        hostSpot.Set(new byte[2 * NetNode.ChunkBytes]);
        big.To(2, new byte[NetNode.QueueBytes - 2 * NetNode.ChunkBytes - 10]);
        hostSpot.Set(null);
        check(Net.Dropped(Net.DropBulkVoid) == voided + 1, "budget: a lobby value's clear that does not fit the full send queue is dropped and counted");
        for (var i = 0; i < 400; i++) hub.Step();
        check(!clientSpot.TryGet(1, out _), "budget: a lobby value's clear dropped on a full queue reaches the player once the queue empties");
        var refused = false;
        try { lossy.To(2, new byte[NetNode.ChunkBytes + 1]); }
        catch (ArgumentException) { refused = true; }
        check(refused, "budget: an unreliable body that does not fit one packet is refused where it is sent");
    }

    private static void Table(Action<bool, string> check)
    {
        var hub = new Hub();
        var host = hub.Join(1);
        var client = hub.Join(2);
        var hostMod = host.Node.Open("table");
        var clientMod = client.Node.Open("table");
        var hostTable = hostMod.Table<int, Ping>("rows");
        var clientTable = clientMod.Table<int, Ping>("rows");
        var hostParts = hostMod.Table<int>("parts", 1);
        var clientParts = clientMod.Table<int>("parts", 1);
        var hostSaw = 0;
        hostTable.Changed += (_, _, _) => hostSaw++;
        hub.Settle();
        hostTable.Set(1, new Ping { Sequence = 1 });
        hostTable.Set(2, new Ping { Sequence = 2 });
        hostTable.Set(3, new Ping { Sequence = 3 });
        hub.Step();
        var sent = hub.Count("table/rows", 2);
        check(clientTable.Rows.Count == 3 && hostSaw == 3, "table: the host's rows reach the client, and the host's own handler sees them too");
        hostTable.Set(2, new Ping { Sequence = 20 });
        hostTable.Set(3, new Ping { Sequence = 3 });
        hub.Step();
        var last = hub.Delivered.Last(p => p.Name == "table/rows");
        // Stamp 6, part 1, kind 1, revision 4, count 4, then one change: flag 1, key 4, row 8.
        check(hub.Count("table/rows", 2) == sent + 1 && last.Data.Length == 29 && clientTable.TryGet(2, out var row) && row.Sequence == 20, "table: only the changed row is sent");

        // Rows of bytes of any length: a document kept in parts, of which only the part that changed travels.
        hostParts.Set(1, new byte[300]);
        hostParts.Set(2, new byte[] { 7 });
        hub.Step();
        var parts = hub.Count("table/parts", 2);
        hostParts.Set(1, new byte[300]);
        hostParts.Set(2, new byte[] { 7, 8, 9 });
        hub.Step();
        last = hub.Delivered.Last(p => p.Name == "table/parts");
        check(hub.Count("table/parts", 2) == parts + 1 && last.Data.Length < 40 && clientParts.TryGet(2, out var part) && part.Length == 3 && part[2] == 9 && clientParts.Rows[1].Length == 300,
            "table: rows of bytes change length, and a part whose bytes did not change is not sent again");

        var joiner = hub.Join(3);
        var joinerMod = joiner.Node.Open("table");
        var joinerTable = joinerMod.Table<int, Ping>("rows");
        joinerMod.Table<int>("parts", 1);
        sent = hub.Count("table/rows", 2);
        hub.Settle();
        check(joinerTable.Rows.Count == 3 && hub.Count("table/rows", 2) == sent && hub.Count("table/rows", 3) == 1, "table: a player who joins gets the whole table, and only that player");

        host.Node.NoteWorld(NetWorldReason.Checkpoint);
        hostTable.Remove(1);
        hub.Step();
        check(hub.Count("table/rows", 2) == sent + 1 && clientTable.Rows.Count == 2 && joinerTable.Rows.Count == 2, "table: after a checkpoint reload every player gets the whole table again");
        host.Node.NoteWorld(NetWorldReason.Build);
        hub.Step();
        check(clientTable.Rows.Count == 0 && hostTable.Rows.Count == 0, "table: a new world empties the table everywhere");
        var refused = false;
        try { clientTable.Set(9, new Ping()); }
        catch (InvalidOperationException) { refused = true; }
        check(refused, "table: only the host writes");
    }
}
