using System;

namespace ForgeCore;

/// <summary>What closing predictions changed against what was shown when they were made: the amount to add to what was shown
/// (negative: less landed than predicted), a kill to add (+1: one settled that was not predicted) or take back (-1: a predicted
/// one did not happen), and whether that added kill was never shown (<see cref="ShowKill"/>; false when its prediction had only
/// run out of time).</summary>
internal struct PredictionClosure
{
    public float AmountDelta;
    public int KillDelta;
    public bool ShowKill;
    /// <summary>Requests a settlement named that no open prediction was found for (they ran out of time, or were never held).</summary>
    public int Unmatched;
    public readonly bool Changed => AmountDelta != 0f || KillDelta != 0;
}

/// <summary>
/// The predictions this machine made of what an authority will settle, without the game: an amount taken from a subject (an
/// enemy, by a key both machines share) by each request sent, held until the authority's settlement closes it, it runs out of
/// time (<see cref="TimeoutSeconds"/>) or the world ends. Every prediction is closed exactly once. A settlement names how many
/// requests it settles and closes that many of the subject's oldest; one that comes after they ran out of time finds none and is
/// returned as a plain difference, so the amount and the kill are still counted and nothing is shown again.
/// <para>The subject's level as the authority last told it (<see cref="Known"/>) less the open predictions that level does not include yet is
/// what is left (<see cref="Remaining"/>; the authority counts the requests it settled, so a level told between a settlement
/// and its arrival is not reduced twice); a request that takes the last of it predicts a kill, once per subject. A subject the authority has
/// not told of starts from the level this machine reads (the seed of <see cref="Predict"/>), which any told value
/// replaces.</para>
/// Every array is made once: predicting, settling, expiring and reading allocate nothing.
/// </summary>
internal sealed class PredictionBook
{
    public const int Entries = 256, Subjects = 128, ShownKills = 16;
    /// <summary>A prediction no settlement closed within this is closed as not landed.</summary>
    public const float TimeoutSeconds = 3f;
    /// <summary>How long a kill whose prediction ran out of time is remembered as already shown.</summary>
    public const float ShownKillSeconds = 30f;

    private struct Entry { public long Key; public float Amount, Born; public bool Open; }

    private struct Subject
    {
        public long Key;
        public bool Used, HasKnown, Seeded, KillPredicted;
        public float Known, Pending, Touched;
        public ushort Stamp;
        public int Count;
        /// <summary>The authority's wrapping count of this machine's requests it had settled on the subject: as of the level it
        /// told, and as of its latest settlement that came. Their difference is how many of the oldest open predictions the told
        /// level already includes.</summary>
        public byte KnownCount, SettledCount;
        /// <summary>Requests of the subject that ran out of time here with no settlement yet. They are older than everything
        /// open, so the authority's counts and settlements are taken against them first.</summary>
        public int Expired;
    }

    private struct Shown { public long Key; public float At; public bool Used; }

    private readonly Entry[] _entries = new Entry[Entries];
    private readonly Subject[] _subjects = new Subject[Subjects];
    private readonly Shown[] _shown = new Shown[ShownKills];
    // The ring holds _count slots from _head in the order they were predicted; a closed one inside it waits for the head to pass.
    private int _head, _count, _nextShown;

    /// <summary>Open predictions.</summary>
    public int Open { get; private set; }

    private int Find(long key)
    {
        for (var i = 0; i < Subjects; i++)
            if (_subjects[i].Used && _subjects[i].Key == key) return i;
        return -1;
    }

    // A free row, else the row touched longest ago that holds nothing open; -1 when every row holds open predictions.
    private int Row(long key, float now)
    {
        var slot = Find(key);
        if (slot >= 0) { _subjects[slot].Touched = now; return slot; }
        var oldest = -1;
        for (var i = 0; i < Subjects; i++)
        {
            ref var s = ref _subjects[i];
            if (!s.Used) { slot = i; break; }
            if (s.Count == 0 && !s.KillPredicted && (oldest < 0 || s.Touched < _subjects[oldest].Touched)) oldest = i;
        }
        if (slot < 0) slot = oldest;
        if (slot < 0) return -1;
        _subjects[slot] = new Subject { Key = key, Used = true, Touched = now };
        return slot;
    }

    private bool WasShown(long key, float now, bool take)
    {
        for (var i = 0; i < ShownKills; i++)
        {
            ref var k = ref _shown[i];
            if (!k.Used || k.Key != key) continue;
            var live = now - k.At <= ShownKillSeconds;
            if (take || !live) k = default;
            if (live) return true;
        }
        return false;
    }

    // What is left of a subject: the told level less the open predictions it does not include yet. A level the authority told
    // after it settled some of the open requests, while those settlements are still on their way, already has them taken off.
    private float Left(int slot)
    {
        ref var s = ref _subjects[slot];
        var pending = s.Pending;
        // The requests the level includes past the last settlement are first the ones that ran out of time here.
        var included = Math.Min((sbyte)(s.KnownCount - s.SettledCount) - s.Expired, s.Count);
        for (int i = 0, at = _head; i < _count && included > 0; i++, at = (at + 1) % Entries)
            if (_entries[at].Open && _entries[at].Key == s.Key) { pending -= _entries[at].Amount; included--; }
        return Math.Max(0f, s.Known - Math.Max(0f, pending));
    }

    private void DropHead()
    {
        while (_count > 0 && !_entries[_head].Open) { _head = (_head + 1) % Entries; _count--; }
    }

    /// <summary>A request that asks <paramref name="amount"/> of <paramref name="key"/> went out. <paramref name="seed"/> is the
    /// subject's level as this machine reads it, used only while the authority has told none. <paramref name="before"/> is
    /// what was left before the request, <paramref name="predicted"/> what it is expected to take (never more than is left) and
    /// <paramref name="kill"/> whether it takes the last. False when the prediction could not be held (every row or the ring is
    /// in use): the answers still stand, but no settlement will be matched to it.</summary>
    public bool Predict(long key, float amount, float seed, float now, out float before, out float predicted, out bool kill)
    {
        amount = Math.Max(0f, amount);
        var slot = Row(key, now);
        if (slot < 0)
        {
            before = Math.Max(0f, seed);
            predicted = Math.Min(amount, before);
            kill = false;
            return false;
        }
        ref var s = ref _subjects[slot];
        if (!s.HasKnown) { s.Known = seed; s.HasKnown = true; s.Seeded = true; }
        before = Left(slot);
        predicted = Math.Min(amount, before);
        kill = before > 0f && amount >= before && !s.KillPredicted && !WasShown(key, now, false);
        DropHead();
        if (_count == Entries) { kill = false; return false; }
        if (kill) s.KillPredicted = true;
        _entries[(_head + _count) % Entries] = new Entry { Key = key, Amount = predicted, Born = now, Open = true };
        _count++;
        Open++;
        s.Pending += predicted;
        s.Count++;
        return true;
    }

    /// <summary>The authority's level of <paramref name="key"/>, stamped with its send counter: one older than the value held is
    /// ignored, and a value this machine only read for itself is always replaced. <paramref name="settled"/> is the authority's
    /// wrapping count of this machine's requests on the subject that the level includes (0 from an authority that counts none).
    /// With nothing open the count is taken as settled too, so a count this book never followed (another player's before this
    /// one took its place, requests that ran out of time) cannot stand for a later prediction.</summary>
    public void Known(long key, float level, ushort stamp, float now, byte settled = 0)
    {
        var slot = Row(key, now);
        if (slot < 0) return;
        ref var s = ref _subjects[slot];
        if (s.HasKnown && !s.Seeded && (short)(stamp - s.Stamp) <= 0) return;
        s.Known = level; s.Stamp = stamp; s.HasKnown = true; s.Seeded = false; s.KnownCount = settled;
        if (s.Count == 0) { s.SettledCount = settled; s.Expired = 0; }
    }

    /// <summary>What is left of <paramref name="key"/>: the level last known less the open predictions it does not include yet.
    /// False for a subject no level is known of.</summary>
    public bool Remaining(long key, out float level)
    {
        var slot = Find(key);
        if (slot < 0 || !_subjects[slot].HasKnown) { level = 0f; return false; }
        level = Left(slot);
        return true;
    }

    /// <summary>The authority settled <paramref name="hits"/> requests on <paramref name="key"/>: they took <paramref name="amount"/>
    /// in all, left <paramref name="left"/>, and <paramref name="killed"/> says the subject died of them. Closes that many of the
    /// subject's oldest predictions, and on a kill every other one of it (the authority settles nothing more on the dead).
    /// <paramref name="settled"/> is the authority's wrapping count of this machine's requests on the subject after this
    /// settlement (see <see cref="Known"/>).</summary>
    public PredictionClosure Settle(long key, int hits, float amount, bool killed, float left, ushort stamp, float now, byte settled = 0)
    {
        var closure = new PredictionClosure { AmountDelta = amount };
        var slot = Find(key);
        // Settlements come in the order the requests went out: the ones that ran out of time here are settled first, and
        // close nothing that is still open. A kill ends the subject, and with it what was still expected of it.
        var late = 0;
        if (slot >= 0)
        {
            ref var s = ref _subjects[slot];
            late = Math.Min(Math.Max(0, hits), s.Expired);
            s.Expired = killed ? 0 : s.Expired - late;
            hits -= late;
        }
        for (int i = 0, at = _head; i < _count && (hits > 0 || killed); i++, at = (at + 1) % Entries)
        {
            ref var e = ref _entries[at];
            if (!e.Open || e.Key != key) continue;
            e.Open = false;
            Open--;
            hits--;
            closure.AmountDelta -= e.Amount;
        }
        closure.Unmatched = late + Math.Max(0, hits);
        DropHead();
        if (slot >= 0)
        {
            ref var s = ref _subjects[slot];
            // What stays open is what the loop left: recounted, so a settlement can never leave an amount behind.
            s.Pending = 0f; s.Count = 0;
            for (int i = 0, at = _head; i < _count; i++, at = (at + 1) % Entries)
                if (_entries[at].Open && _entries[at].Key == key) { s.Pending += _entries[at].Amount; s.Count++; }
            if (s.Seeded || !s.HasKnown || (short)(stamp - s.Stamp) > 0) { s.Known = left; s.Stamp = stamp; s.HasKnown = true; s.Seeded = false; s.KnownCount = settled; }
            // Settlements come in order; one that a told level already rebased past does not move the count back.
            if ((sbyte)(settled - s.SettledCount) > 0) s.SettledCount = settled;
            s.Touched = now;
        }
        var predicted = slot >= 0 && _subjects[slot].KillPredicted;
        if (killed)
        {
            var shown = WasShown(key, now, true);
            closure.KillDelta = predicted ? 0 : 1;
            closure.ShowKill = !predicted && !shown;
            if (slot >= 0) _subjects[slot].KillPredicted = false;
        }
        else if (predicted && _subjects[slot].Count == 0)
        {
            closure.KillDelta = -1;
            _subjects[slot].KillPredicted = false;
        }
        return closure;
    }

    /// <summary>Closes the oldest prediction that ran out of time, one per call: its amount is taken back, and a kill predicted for
    /// its subject too once nothing of the subject is open (remembered as shown, so a settlement that still comes shows nothing
    /// again). False when none is due.</summary>
    public bool Expire(float now, out long key, out PredictionClosure closure)
    {
        closure = default;
        key = 0;
        DropHead();
        if (_count == 0 || now - _entries[_head].Born < TimeoutSeconds) return false;
        ref var e = ref _entries[_head];
        key = e.Key;
        closure.AmountDelta = -e.Amount;
        e.Open = false;
        Open--;
        var slot = Find(key);
        if (slot >= 0)
        {
            ref var s = ref _subjects[slot];
            s.Pending = Math.Max(0f, s.Pending - e.Amount);
            s.Count--;
            s.Expired++;
            if (s.Count == 0)
            {
                s.Pending = 0f;
                if (s.KillPredicted)
                {
                    s.KillPredicted = false;
                    closure.KillDelta = -1;
                    _shown[_nextShown] = new Shown { Key = key, At = now, Used = true };
                    _nextShown = (_nextShown + 1) % ShownKills;
                }
            }
        }
        DropHead();
        return true;
    }

    /// <summary>The subject left the world: the level known of it goes (its key may name another later); what is open of it stays
    /// until settled or out of time.</summary>
    public void Forget(long key)
    {
        var slot = Find(key);
        if (slot < 0) return;
        ref var s = ref _subjects[slot];
        if (s.Count == 0 && !s.KillPredicted) { s = default; return; }
        s.HasKnown = s.Seeded = false;
        s.Known = 0f;
        s.KnownCount = s.SettledCount = 0;
        s.Expired = 0;
    }

    /// <summary>The world ended (a level, a checkpoint reload, another host): everything open is closed as not landed, in one
    /// closure, and nothing is remembered.</summary>
    public PredictionClosure Clear()
    {
        var closure = default(PredictionClosure);
        for (int i = 0, at = _head; i < _count; i++, at = (at + 1) % Entries)
            if (_entries[at].Open) closure.AmountDelta -= _entries[at].Amount;
        for (var i = 0; i < Subjects; i++)
            if (_subjects[i].Used && _subjects[i].KillPredicted) closure.KillDelta--;
        Array.Clear(_entries);
        Array.Clear(_subjects);
        Array.Clear(_shown);
        _head = _count = _nextShown = 0;
        Open = 0;
        return closure;
    }
}
