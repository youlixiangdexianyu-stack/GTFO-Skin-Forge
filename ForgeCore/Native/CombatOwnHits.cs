using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Agents;
using Enemies;
using Player;

namespace ForgeCore;

/// <summary>A settled hit of the local player on an enemy, for ForgeCore's own readers (the hit feedback and the enemy health bars):
/// the enemy (null when it could not be found on this machine), the health it lost, the health it has left and its most, whether
/// it died of it and, for a kill, its <c>EnemyDataBlock</c> persistentID; then what the settlement changed against the
/// predictions it closed (<see cref="PredictionClosure"/>, under the enemy's key in the prediction book).</summary>
internal readonly record struct OwnHit(Dam_EnemyDamageBase? Enemy, float Damage, float Health, float HealthMax, bool Killed, uint EnemyType,
    long Key = 0, float DamageDelta = 0f, int KillDelta = 0, bool ShowKill = false, int Unmatched = 0);

/// <summary>What a hit request of the local player on an enemy is expected to do, before the host settled it: the health it
/// should take (never more than the enemy is thought to have left), whether it should kill and, for a kill, the enemy's
/// <c>EnemyDataBlock</c> persistentID. <see cref="Confirmable"/>: the host will settle it, so <see cref="Combat.OwnHitCorrected"/>
/// brings the difference; false on a client whose host runs no matching ForgeCore, where nothing follows and no kill is predicted.</summary>
public readonly record struct OwnHitPrediction(float Damage, bool Kill, uint EnemyType, bool Confirmable);

/// <summary>The difference between what <see cref="Combat.OwnHitPredicted"/> said and what happened: the damage to add (negative:
/// less landed), a kill to add (+1) or take back (-1), whether an added kill was never shown as predicted (<see cref="ShowKill"/>:
/// show it now), for an added kill the enemy's <c>EnemyDataBlock</c> persistentID, and whether the damage added is of a hit that
/// no open prediction stood for (<see cref="ShowHit"/>: an explosion, fire, a request that was not predicted or had run out of
/// time; show the hit now).</summary>
public readonly record struct OwnHitCorrection(float DamageDelta, int KillDelta, bool ShowKill, uint EnemyType, bool ShowHit = false);

/// <summary><see cref="OwnHitPrediction"/> for ForgeCore's own readers, with the enemy, its key in the prediction book, the hit
/// point and the health it was thought to have before the hit.</summary>
internal readonly record struct OwnPrediction(Dam_EnemyDamageBase Enemy, long Key, UnityEngine.Vector3 Position, float Damage, float Before, bool Kill, uint EnemyType, bool Confirmable);

/// <summary>A correction that came without a settlement (a request ran out of time, or the world ended), for ForgeCore's own
/// readers, with the enemy's key (0: every open prediction at once, the world ended). A settlement's own correction is in its
/// <see cref="OwnHit"/>.</summary>
internal readonly record struct OwnCorrection(long Key, float DamageDelta, int KillDelta);

public static partial class Combat
{
    private static Action<float, bool, uint>? _ownHit;
    private static Action<OwnHit>? _ownHitOnEnemy;
    private static Action<OwnHitPrediction>? _predicted;
    private static Action<OwnPrediction>? _predictedOnEnemy;
    private static Action<OwnHitCorrection>? _corrected;
    private static Action<OwnCorrection>? _correctedOnEnemy;
    private static readonly PredictionBook Book = new();

    /// <summary>Raised on the shooter's machine in the frame of the local player's hit on an enemy, host and client alike, with
    /// what the hit does: for what must show without waiting for the host (a number, a marker, a kill mark, a counter on screen).
    /// On the host it is the settlement itself (same frame, never corrected). On a client it is a prediction, made as the hit
    /// request goes out (<c>Dam_EnemyDamageBase.BulletDamage</c> / <c>MeleeDamage</c>, <see cref="GameHooks.EnemyHitSent"/>): the
    /// damage is the request's (after the limb's multipliers), capped by the health the enemy is thought to have left: the host's
    /// last word on it (each settlement of this player's hits and ForgeCore's health reports) less this player's requests still
    /// open, or this machine's own reading for an enemy the host has not reported. A request that takes the last of it predicts
    /// the kill, once per enemy. On a client an immortal enemy, a request past the book's capacity
    /// (<c>core-prediction-capacity</c>) and hits made any other way (explosions, fire) predict nothing: their settlement arrives
    /// as a plain <see cref="OwnHitCorrected"/>. Saved state follows <see cref="OwnHitSettled"/>, never this. Main thread only.</summary>
    public static event Action<OwnHitPrediction>? OwnHitPredicted
    {
        add { OwnHits.Watch(); _predicted += value; }
        remove => _predicted -= value;
    }

    /// <summary>Raised on a client when what <see cref="OwnHitPredicted"/> said turns out different: with a settlement of the host (just before
    /// its <see cref="OwnHitSettled"/>; a settlement closes that many of the enemy's oldest open requests, and a kill every one),
    /// when a request stayed open 3 s (lost on the way, or the enemy died of another hit first: its damage and its kill are taken
    /// back; a settlement that still comes later adds them again without <see cref="OwnHitCorrection.ShowKill"/>), and when the
    /// world ends (level, checkpoint reload, host change: everything open is taken back at once). A settled hit that was never
    /// predicted comes as its whole damage and kill. What was shown from predictions plus every correction equals what
    /// <see cref="OwnHitSettled"/> reported. Main thread only.</summary>
    public static event Action<OwnHitCorrection>? OwnHitCorrected
    {
        add { OwnHits.Watch(); _corrected += value; }
        remove => _corrected -= value;
    }

    internal static void HearOwnPredictions(Action<OwnPrediction> reader)
    {
        OwnHits.Watch();
        _predictedOnEnemy += reader;
    }

    internal static void HearOwnCorrections(Action<OwnCorrection> reader)
    {
        OwnHits.Watch();
        _correctedOnEnemy += reader;
    }

    /// <summary>The local player's hit request on <paramref name="receiver"/> went out to the host (this machine is a client).</summary>
    internal static void Predict(Dam_EnemyDamageBase receiver, UnityEngine.Vector3 at, float damage)
    {
        // The host's receivers drop every hit on an immortal enemy.
        if (receiver.IsImortal || !OwnHits.Handle(receiver, out var handle)) return;
        var seed = OwnHits.Health(receiver);
        var key = OwnHits.Key(handle);
        if (!OwnHits.HostAnswers)
        {
            RaisePrediction(new OwnPrediction(receiver, key, at, Math.Min(Math.Max(0f, damage), Math.Max(0f, seed)), seed, false, 0u, false));
            return;
        }
        if (!Book.Predict(key, damage, seed, Frame.UnscaledTime, out var before, out var predicted, out var kill))
        {
            Plugin.Logger.ErrorOnce("core-prediction-capacity", "More than " + PredictionBook.Entries + " hit requests (or " + PredictionBook.Subjects + " enemies) of this player wait for the host's settlement: the hits past that are not predicted and show when the host settles them.");
            return;
        }
        RaisePrediction(new OwnPrediction(receiver, key, at, predicted, before, kill, kill ? OwnHits.TypeOf(receiver) : 0u, true));
    }

    private static void RaisePrediction(in OwnPrediction hit)
    {
        Plugin.Logger.RaiseEach(_predictedOnEnemy, hit, "core-own-hit-handler-failed", "Combat.HearOwnPredictions");
        Plugin.Logger.RaiseEach(_predicted, new OwnHitPrediction(hit.Damage, hit.Kill, hit.EnemyType, hit.Confirmable), "core-own-hit-handler-failed", "Combat.OwnHitPredicted");
    }

    private static void RaiseCorrection(in OwnHitCorrection change)
        => Plugin.Logger.RaiseEach(_corrected, change, "core-own-hit-handler-failed", "Combat.OwnHitCorrected");

    // A correction no settlement brought.
    private static void TakeBack(long key, in PredictionClosure closure)
    {
        if (!closure.Changed) return;
        Plugin.Logger.RaiseEach(_correctedOnEnemy, new OwnCorrection(key, closure.AmountDelta, closure.KillDelta), "core-own-hit-handler-failed", "Combat.HearOwnCorrections");
        RaiseCorrection(new OwnHitCorrection(closure.AmountDelta, closure.KillDelta, false, 0u));
    }

    /// <summary>The host settled <paramref name="hits"/> requests of the local player on one enemy: the predictions they close are
    /// corrected first, then the settlement is raised as it is. <paramref name="count"/> is the host's count of this player's
    /// requests it has settled on the enemy, this settlement included.</summary>
    internal static void Settle(in OwnHit hit, long key, int hits, ushort stamp, byte count)
    {
        var closure = Book.Settle(key, hits, hit.Damage, hit.Killed, hit.Health, stamp, Frame.UnscaledTime, count);
        if (closure.Changed) RaiseCorrection(new OwnHitCorrection(closure.AmountDelta, closure.KillDelta, closure.ShowKill, hit.EnemyType, closure.Unmatched > 0 && closure.AmountDelta > 0f));
        RaiseOwnHit(hit with { Key = key, DamageDelta = closure.AmountDelta, KillDelta = closure.KillDelta, ShowKill = closure.ShowKill, Unmatched = closure.Unmatched });
    }

    /// <summary>The host settled its own player's hits on one enemy, in the hit's own frame: there the settlement is the
    /// prediction too, so the readers that show and the readers that save hear the same on host and client, and nothing is
    /// corrected.</summary>
    internal static void SettleHere(in OwnHit hit, long key, UnityEngine.Vector3 at)
    {
        if (hit.Enemy != null) RaisePrediction(new OwnPrediction(hit.Enemy, key, at, hit.Damage, hit.Health + hit.Damage, hit.Killed, hit.EnemyType, true));
        RaiseOwnHit(hit with { Key = key });
    }

    /// <summary>Once a frame: the requests no settlement closed in time are taken back.</summary>
    internal static void ExpirePredictions()
    {
        if (Book.Open == 0) return;
        var now = Frame.UnscaledTime;
        while (Book.Expire(now, out var key, out var closure)) TakeBack(key, closure);
    }

    /// <summary>The world ended: every open prediction is taken back in one correction.</summary>
    internal static void ClearPredictions() => TakeBack(0, Book.Clear());

    /// <summary>The enemy died or left the world: what the host said of its health goes with it (its key may name another later).</summary>
    internal static void ForgetEnemy(long key) => Book.Forget(key);

    /// <summary>The host told an enemy's health (a report, or the list a player gets on joining) and how many of this player's
    /// requests on it that health includes.</summary>
    internal static void EnemyHealthTold(long key, float health, ushort stamp, byte count) => Book.Known(key, health, stamp, Frame.UnscaledTime, count);

    /// <summary>The enemy's key for <see cref="EnemyHealth"/>; 0 for one without an agent.</summary>
    internal static long EnemyKey(Dam_EnemyDamageBase enemy) => OwnHits.Handle(enemy, out var handle) ? OwnHits.Key(handle) : 0;

    /// <summary>The health to show for an enemy. The game keeps an enemy's health on the host only (a client's copy stays where it
    /// spawned), so on a client this is the host's last word (its settlements of this player's hits and its health reports) less
    /// this player's requests still open that the word does not include yet, and the game's own value only for an enemy the host has said nothing of.</summary>
    internal static float EnemyHealth(long key, Dam_EnemyDamageBase enemy) => !Session.IsHost && Book.Remaining(key, out var left) ? left : OwnHits.Health(enemy);

    /// <summary>(damage, killed, enemy type) Raised on the machine of the player whose hit (bullet, melee, explosion, fire: every hit the game settles through
    /// <c>Dam_EnemyDamageBase.ProcessReceivedDamage</c>) the host settled on a living enemy. The game settles a client's hits on the host
    /// only (<c>Dam_SyncedDamageBase.SendLocally</c> answers <c>SNet.IsMaster</c>), so the host's ForgeCore sends each one to the client
    /// whose hit it was, and a client hears its own hits about one round trip late. A kill is raised on its own, as it settles, with
    /// the rest of that frame's damage to the same enemy; the other hits of one frame are raised once per enemy, summed, in that frame's
    /// late update. The arguments: the health the enemy lost, whether it
    /// died of the hit and, for a kill, the enemy's <c>EnemyDataBlock</c> persistentID (0 for a hit, or an enemy without data). A client
    /// whose host runs no matching ForgeCore hears none (one <c>core-own-hits-host-silent</c> line). Bots' hits are nobody's. Main thread only.</summary>
    public static event Action<float, bool, uint>? OwnHitSettled
    {
        add { OwnHits.Watch(); _ownHit += value; }
        remove => _ownHit -= value;
    }

    /// <summary>The same settlements as <see cref="OwnHitSettled"/>, with the enemy and the health it has left.</summary>
    internal static void HearOwnHits(Action<OwnHit> reader)
    {
        OwnHits.Watch();
        _ownHitOnEnemy += reader;
    }

    internal static void RaiseOwnHit(in OwnHit hit)
    {
        Plugin.Logger.RaiseEach(_ownHit, hit.Damage, hit.Killed, hit.EnemyType, "core-own-hit-handler-failed", "Combat.OwnHitSettled");
        Plugin.Logger.RaiseEach(_ownHitOnEnemy, hit, "core-own-hit-handler-failed", "Combat.HearOwnHits");
    }

    internal static bool OwnHitHeard => _ownHit != null || _ownHitOnEnemy != null || _predicted != null || _predictedOnEnemy != null || _corrected != null || _correctedOnEnemy != null;
}

/// <summary>The host's side of <see cref="Combat.OwnHitSettled"/>: reads every settled hit of a human player on a living enemy and
/// raises it here (the host's own) or sends it to that player over ForgeCore's channel.</summary>
internal static class OwnHits
{
    internal const string Channel = "NAinfini.ForgeCore.Combat";

    // The enemy goes as the game's own agent handle (its replicator key), which names the same enemy on every machine.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Settled
    {
        public float Damage;
        public float Health;
        public float HealthMax;
        public uint EnemyType;
        public pAgent Enemy;
        public byte Killed;
        /// <summary>How many of the player's requests on this enemy the message settles.</summary>
        public byte Hits;
        /// <summary>The host's count of the messages that told an enemy's health, so an older one never replaces a newer.</summary>
        public ushort Stamp;
        /// <summary>The host's wrapping count of the player's requests it has settled on this enemy, this message included
        /// (0 for a player outside the four slots, who is not counted).</summary>
        public byte Count;
    }

    private struct Pending
    {
        public ulong To;
        public Dam_EnemyDamageBase Receiver;
        public pAgent Enemy;
        public float Damage, Health, HealthMax;
        public byte Hits, Count;
        /// <summary>Where the latest of the hits landed.</summary>
        public UnityEngine.Vector3 At;
    }

    /// <summary>How often at most the host tells the clients the health of the enemies whose health changed.</summary>
    private const float HealthReportsPerSecond = 30f;

    private struct Told
    {
        public IntPtr Receiver;
        public pAgent Enemy;
        public float Health;
    }

    private static NetEvent<Settled>? _send;
    private static NetBytes? _health, _healthAll;
    private static NetChannel? _channel;
    private static bool _watching, _silentLogged;
    private static ushort _stamp;
    private static float _nextReport;
    private static readonly int HandleBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<pAgent>();
    // The enemies whose health changed on the host since the last report, each once with its latest health.
    private static readonly List<Told> Changed = new(32);
    /// <summary>The player slots whose requests the host counts per enemy; every health row carries one count for each.</summary>
    private const int CountedSlots = 4;
    // On the host, per enemy (its damage receiver): how many requests of the player in each slot were settled on it, one wrapping
    // byte a slot. A health row carries them, so a client knows which of its open requests the health already includes.
    private static readonly Dictionary<IntPtr, uint> Counts = new(64);

    private static byte Counted(IntPtr receiver, int slot, bool add)
    {
        if (slot is < 0 or >= CountedSlots) return 0;
        Counts.TryGetValue(receiver, out var counts);
        var count = (byte)(counts >> (slot * 8));
        if (!add) return count;
        count++;
        Counts[receiver] = counts & ~(0xFFu << (slot * 8)) | (uint)count << (slot * 8);
        return count;
    }

    /// <summary>This machine is the host and a player is there to hear what enemies have left.</summary>
    private static bool Reporting => _health != null && Session.IsHost && _channel!.AnyPeer;

    /// <summary>This machine is a client whose host settles its hits over ForgeCore's channel.</summary>
    internal static bool HostAnswers => _channel != null && _channel.HostPeer != 0;

    // The game's reads every part here shares, each made in one place.
    internal static float Health(Dam_EnemyDamageBase receiver) => receiver.Health;

    internal static uint TypeOf(Dam_EnemyDamageBase receiver) => receiver.Owner?.EnemyData is { } data ? data.persistentID : 0u;

    internal static bool Handle(Dam_EnemyDamageBase receiver, out pAgent handle)
    {
        handle = default;
        var enemy = receiver.Owner;
        if (enemy == null) return false;
        handle.Set(enemy);
        return true;
    }

    /// <summary>The enemy's key in the prediction book: the bytes of its agent handle, the same on every machine.</summary>
    internal static long Key(pAgent handle)
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref handle, 1));
        if (bytes.Length > sizeof(long)) return (long)Fnv.Hash64(bytes);
        long key = 0;
        bytes.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref key, 1)));
        return key;
    }

    // Hits that did not kill, summed per player (0: this machine) and enemy until the frame's late update, which raises or sends one
    // for each: a shotgun's pellets settle many times a frame. A kill is raised or sent at once, with that enemy's pending damage.
    private static readonly List<Pending> Waiting = new(8);

    internal static void Open()
    {
        try
        {
            _channel = Net.Open(Channel);
            _send = _channel.Event<Settled>("own-hit", Receive);
            // The game never tells a client an enemy's health: the host reports every change (lossy, a newer report replaces a
            // lost one) and, to a player who joins, every enemy already hurt.
            _health = _channel.Bytes("health", 2, HealthTold, NetDelivery.Lossy);
            _healthAll = _channel.Bytes("health-all", 2, HealthTold);
            // Only the host settles hits, its own and its clients'; a client's own hits arrive by message.
            _channel.PeerSynced += static player => { if (Session.IsHost) { Watch(); ReportAll(player); } };
            _channel.PeerSilent += HostSilent;
            _channel.Refused += static (player, _) => HostSilent(player);
        }
        catch (Exception error)
        {
            _send = null;
            _health = _healthAll = null;
            Plugin.Logger.Error("core-own-hits-off", "Hits of clients are not reported to them (Combat.OwnHitSettled hears only the host's own) and no enemy health is told: ForgeCore's combat channel could not open: " + error);
        }
    }

    internal static void Watch()
    {
        if (_watching) return;
        _watching = true;
        GameHooks.EnemyDamageReceived.Around(Before, After);
        GameHooks.EnemyHitSent.Before(Sent);
        GameHooks.EnemyDead.Before(Gone);
        GameHooks.EnemyDespawned.Before(Gone);
        Net.WorldChanged += static _ => { Changed.Clear(); Counts.Clear(); Combat.ClearPredictions(); };
        Frame.OnLateUpdate(Plugin.Id, Flush);
    }

    private static void Flush()
    {
        for (int i = 0; i < Waiting.Count; i++) Settle(Waiting[i], false, 0);
        Waiting.Clear();
        Combat.ExpirePredictions();
        if (Changed.Count == 0 || Frame.UnscaledTime < _nextReport) return;
        _nextReport = Frame.UnscaledTime + 1f / HealthReportsPerSecond;
        if (Reporting) Report(_health!, 0, Changed);
        Changed.Clear();
    }

    // The rows as messages of one packet each: the stamp, then each enemy's handle, health and the settled requests counted for
    // each player slot. `to` 0: every compatible player.
    private static void Report(NetBytes message, ulong to, List<Told> rows)
    {
        var row = HandleBytes + sizeof(float) + CountedSlots;
        var most = (NetNode.ChunkBytes - sizeof(ushort)) / row;
        for (var from = 0; from < rows.Count; from += most)
        {
            var count = Math.Min(most, rows.Count - from);
            var body = new byte[sizeof(ushort) + count * row];
            var stamp = ++_stamp;
            MemoryMarshal.Write(body, ref stamp);
            for (var i = 0; i < count; i++)
            {
                var told = rows[from + i];
                var at = sizeof(ushort) + i * row;
                MemoryMarshal.Write(body.AsSpan(at), ref told.Enemy);
                MemoryMarshal.Write(body.AsSpan(at + HandleBytes), ref told.Health);
                // Read as the row is written: the frame's settlements went out before it, so the counts cover them.
                Counts.TryGetValue(told.Receiver, out var counts);
                MemoryMarshal.Write(body.AsSpan(at + HandleBytes + sizeof(float)), ref counts);
            }
            if (to == 0) message.Broadcast(body);
            else message.To(to, body);
        }
    }

    // A player joined the host's session: every living enemy that is not at full health, once and reliably.
    private static void ReportAll(ulong player)
    {
        if (_healthAll == null) return;
        try
        {
            var rows = new List<Told>();
            foreach (var enemy in Enemies.Live)
            {
                var damage = enemy == null ? null : enemy.Damage;
                if (damage == null) continue;
                var health = Health(damage);
                if (health <= 0f || health >= damage.HealthMax) continue;
                var told = new Told { Receiver = damage.Pointer, Health = health };
                told.Enemy.Set(enemy!);
                rows.Add(told);
            }
            Report(_healthAll, player, rows);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-enemy-health-report-failed", "Telling player " + player + " the health of the enemies already hurt failed; that player's bars and kill predictions start from full health for them: " + error); }
    }

    private static void HealthTold(ulong sender, byte[] body)
    {
        if (!_channel!.FromHost(sender))
        {
            if (Net.FirstDrop(Channel, Net.DropNotHost)) Net.NoteDrop(Channel, Net.DropNotHost, "enemy health from player " + sender + ", who is not the host");
            return;
        }
        var row = HandleBytes + sizeof(float) + CountedSlots;
        if (body.Length < sizeof(ushort) || (body.Length - sizeof(ushort)) % row != 0)
        {
            if (Net.FirstDrop(Channel, Net.DropMalformed)) Net.NoteDrop(Channel, Net.DropMalformed, "enemy health of " + body.Length + " bytes");
            return;
        }
        // Nothing on this machine reads enemy health or predicts hits: no book is kept.
        if (!_watching) return;
        var stamp = MemoryMarshal.Read<ushort>(body);
        // This player's own count of each row; a player outside the counted slots has none and reads 0, as the host sends it.
        var local = Players.Local;
        var slot = local == null ? -1 : local.PlayerSlotIndex;
        var mine = slot is >= 0 and < CountedSlots ? HandleBytes + sizeof(float) + slot : -1;
        for (var at = sizeof(ushort); at < body.Length; at += row)
            Combat.EnemyHealthTold(Key(MemoryMarshal.Read<pAgent>(body.AsSpan(at))), MemoryMarshal.Read<float>(body.AsSpan(at + HandleBytes)), stamp, mine < 0 ? (byte)0 : body[at + mine]);
    }

    private static void Note(Dam_EnemyDamageBase receiver, float health)
    {
        var pointer = receiver.Pointer;
        for (var i = 0; i < Changed.Count; i++)
        {
            if (Changed[i].Receiver != pointer) continue;
            var told = Changed[i];
            told.Health = health;
            Changed[i] = told;
            return;
        }
        var first = new Told { Receiver = pointer, Health = health };
        if (Handle(receiver, out first.Enemy)) Changed.Add(first);
    }

    private static void Settle(in Pending hit, bool killed, uint type)
    {
        if (hit.To == 0) Combat.SettleHere(new OwnHit(hit.Receiver, hit.Damage, hit.Health, hit.HealthMax, killed, type), Key(hit.Enemy), hit.At);
        else _send!.To(hit.To, new Settled { Damage = hit.Damage, Health = hit.Health, HealthMax = hit.HealthMax, EnemyType = type, Enemy = hit.Enemy, Killed = killed ? (byte)1 : (byte)0, Hits = hit.Hits, Stamp = ++_stamp, Count = hit.Count });
    }

    // A client's own hit request on an enemy: the local player's is predicted. The host settles its own in the same frame.
    private static void Sent((Dam_EnemyDamageBase Receiver, Agent? Source, UnityEngine.Vector3 Position, float Damage) call)
    {
        if (call.Source == null || Session.IsHost || !Combat.OwnHitHeard) return;
        try
        {
            var local = Players.Local;
            if (local != null && local.Pointer == call.Source.Pointer) Combat.Predict(call.Receiver, call.Position, call.Damage);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-own-hit-prediction-failed", "Predicting the local player's hit failed; it shows when the host settles it: " + error); }
    }

    private static void Gone(EnemyAgent enemy)
    {
        try
        {
            pAgent handle = default;
            handle.Set(enemy);
            Combat.ForgetEnemy(Key(handle));
            if (Counts.Count > 0 && enemy.Damage is { } damage) Counts.Remove(damage.Pointer);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-own-hit-prediction-failed", "Forgetting an enemy's reported health failed: " + error); }
    }

    // The receiver's health before the hit (0 or less: nothing to report), read when a player's machine hears the hit (Heard; To
    // says whose, 0: this one) or the host reports health changes to its clients.
    private static (float Health, ulong To, bool Heard, int Slot) Before(EnemyDamageCall call)
    {
        if (!NativeObjects.Is<Dam_EnemyDamageBase>(call.Receiver)) return default;
        var heard = Hearer(call.Source, out var to, out var slot);
        return heard || Reporting ? (Health(call.Receiver), to, heard, slot) : default;
    }

    // Whose machine hears a hit of this source: a human player's, and the slot the player holds. A bot's lookup is never a
    // compatible peer, so nothing is sent for it.
    private static bool Hearer(Agent? source, out ulong to, out int slot)
    {
        to = 0;
        slot = -1;
        if (source == null || !NativeObjects.Is<PlayerAgent>(source)) return false;
        var player = source.TryCast<PlayerAgent>();
        var owner = player?.Owner;
        if (owner == null) return false;
        slot = owner.PlayerSlotIndex();
        var lookup = owner.Lookup;
        to = lookup == Session.LocalLookup ? 0UL : lookup;
        return to == 0 ? Combat.OwnHitHeard : _send != null && Session.IsHost;
    }

    // A living enemy (health above 0) that has none left after the hit died of it.
    private static void After(EnemyDamageCall call, (float Health, ulong To, bool Heard, int Slot) before, bool ran)
    {
        if (before.Health <= 0f) return;
        var receiver = call.Receiver;
        var after = Math.Max(0f, Health(receiver));
        // Every change is reported, whoever or whatever made it.
        if (after != before.Health && Reporting) Note(receiver, after);
        if (!before.Heard) return;
        var damage = Math.Max(0f, before.Health - after);
        int at = -1;
        for (int i = 0; i < Waiting.Count; i++)
            if (Waiting[i].To == before.To && Waiting[i].Receiver.Pointer == receiver.Pointer) { at = i; break; }
        Pending hit;
        if (at >= 0)
        {
            hit = Waiting[at];
            hit.Damage += damage;
            hit.Health = after;
        }
        else
        {
            hit = new Pending { To = before.To, Receiver = receiver, Damage = damage, Health = after, HealthMax = receiver.HealthMax };
            Handle(receiver, out hit.Enemy);
        }
        hit.Hits++;
        // A client's request is counted as it settles; the host's own player keeps no book.
        if (before.To != 0) hit.Count = Counted(receiver.Pointer, before.Slot, true);
        hit.At = call.Position;
        // One message counts at most 255 requests: a full one goes now and the next request starts another.
        if (after > 0f && hit.Hits < byte.MaxValue)
        {
            if (at >= 0) Waiting[at] = hit;
            else Waiting.Add(hit);
            return;
        }
        if (at >= 0) Waiting.RemoveAt(at);
        Settle(hit, after <= 0f, after <= 0f ? TypeOf(receiver) : 0u);
    }

    private static void Receive(ulong sender, Settled message)
    {
        if (!_channel!.FromHost(sender))
        {
            if (Net.FirstDrop(Channel, Net.DropNotHost)) Net.NoteDrop(Channel, Net.DropNotHost, "a settled hit from player " + sender + ", who is not the host");
            return;
        }
        // An enemy that is already gone here still counts; only the readers that draw on it skip it.
        var enemy = message.Enemy.TryGet(out var agent) ? agent.TryCast<EnemyAgent>()?.Damage : null;
        Combat.Settle(new OwnHit(enemy, message.Damage, message.Health, message.HealthMax, message.Killed != 0, message.EnemyType), Key(message.Enemy), message.Hits, message.Stamp, message.Count);
    }

    // This machine is a client whose host will send it nothing: its own hits and kills are never heard here, and what
    // Combat.OwnHitPredicted says of them is never confirmed.
    private static void HostSilent(ulong player)
    {
        if (_silentLogged || !Combat.OwnHitHeard || Session.IsHost || player != GameEvents.Master) return;
        _silentLogged = true;
        Plugin.Logger.Error("core-own-hits-host-silent", "The host (player " + player + ") runs no ForgeCore that reports settled hits: this player's hits and kills on enemies are not heard here (Combat.OwnHitSettled stays silent and Combat.OwnHitPredicted is never confirmed; skins count no kills).");
    }
}
