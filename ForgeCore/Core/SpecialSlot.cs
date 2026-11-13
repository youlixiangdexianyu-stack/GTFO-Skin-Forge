using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The local player's own special weapon as <see cref="SpecialSlot"/> keeps it: its gear (the game's gear JSON and checksum,
/// to put it back through the game's own gear path) and its stock while another gun stands in the slot (clip in rounds, pool in the
/// game's ammunition units).</summary>
public sealed record OwnSpecialWeapon(string GearJson, uint Checksum, int Clip, float Pool);

/// <summary>One mod's hold on the local player's special weapon slot, from <see cref="SpecialSlot.Take"/> until disposed. Only the
/// newest lease may change the slot, answer the special weapon's select key or touch the slot's ammunition; an older one is
/// <see cref="Suspended"/> and keeps its own gun aside meanwhile.</summary>
public sealed class SpecialSlotLease : IDisposable
{
    private readonly SpecialSlot _slot;
    private readonly Func<string> _refusal;

    internal SpecialSlotLease(SpecialSlot slot, string owner, Func<string> refusal)
    {
        _slot = slot;
        Owner = owner;
        _refusal = refusal;
    }

    /// <summary>The mod that took the lease (for logs and <see cref="SpecialSlot.CheckpointHolder"/>).</summary>
    public string Owner { get; }

    /// <summary>Player-facing words, in the player's language, for why the slot cannot be lent to something else now (for example
    /// "Piloting an exosuit"); a mod that refuses its own loan while the slot is taken shows them.</summary>
    public string Refusal => _refusal();

    /// <summary>The lease is still held (not disposed, not dropped by the level ending).</summary>
    public bool Held => _slot.Holds(this);

    /// <summary>A newer lease holds the slot: leave the slot, the select key and the slot's ammunition alone until this is false again.</summary>
    public bool Suspended => _slot.IsSuspended(this);

    /// <summary>Every older lease has <see cref="Parked"/>: this lease may change the slot now.</summary>
    public bool Ready => _slot.IsReady(this);

    /// <summary>Set by a suspended lease once it has settled (its gun's stock put aside, no swap of its own on its way) and will not
    /// change the slot until it is on top again; cleared by it when it resumes.</summary>
    public bool Parked { get; set; }

    public void Dispose() => _slot.Release(this);
}

/// <summary>Who holds the local player's special weapon slot, the player's own special weapon while something else stands in it, and
/// where special ammunition pickups go. Mods that lend a gun into the slot (a heavy weapon, an exosuit's gun) take a lease and
/// remember the own weapon here instead of keeping their own copies, so two of them never fight over the slot, the select key or the
/// ammunition pool. Leases stack: a newer one suspends the older one, which puts its gun aside and parks; the newer one waits for that
/// (<see cref="SpecialSlotLease.Ready"/>), and when it is disposed the older one resumes. While the own weapon is away every special
/// ammunition pickup of the local player goes into its kept pool (the game's own pickup, run against that pool), never to a lent gun.
/// ForgeCore keeps this across a checkpoint the way the game keeps the slot (what was away at the checkpoint is away again after
/// a reload) and clears it when the level ends. Main thread only.</summary>
public sealed class SpecialSlot
{
    private readonly List<SpecialSlotLease> _leases = new();
    private OwnSpecialWeapon? _checkpoint;

    /// <summary>The local player's slot, the one every mod shares (<c>Gear.SpecialSlot</c>).</summary>
    internal static SpecialSlot Local { get; } = new();

    internal SpecialSlot() { }

    /// <summary>Some lease holds the slot.</summary>
    public bool Taken => _leases.Count != 0;

    /// <summary>The newest lease: the one that may change the slot now.</summary>
    public SpecialSlotLease? Top => _leases.Count == 0 ? null : _leases[_leases.Count - 1];

    /// <summary>The player's own special weapon, while a lease holds the slot or it is away.</summary>
    public OwnSpecialWeapon? Own { get; private set; }

    /// <summary>Another gun stands in the slot (or is on its way into it); the own weapon's stock is <see cref="Own"/>.</summary>
    public bool OwnAway { get; private set; }

    /// <summary>Special ammunition pickups go into <see cref="Own"/>'s pool instead of the slot's.</summary>
    public bool RedirectsRefill => OwnAway && Own != null;

    /// <summary>The lease's owner whose gun stood in the slot at the last checkpoint, or null when the own weapon was in it.</summary>
    public string? CheckpointHolder { get; private set; }

    /// <summary>A new lease on top: the one below is suspended until this one is disposed. <paramref name="refusal"/> gives
    /// <see cref="SpecialSlotLease.Refusal"/>.</summary>
    public SpecialSlotLease Take(string owner, Func<string> refusal)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Name the owner.", nameof(owner));
        var lease = new SpecialSlotLease(this, owner, refusal ?? throw new ArgumentNullException(nameof(refusal)));
        _leases.Add(lease);
        return lease;
    }

    /// <summary>The gear in the slot now is the player's own (the caller reads it from the slot before lending anything). Ignored while
    /// the own weapon is away: the slot then holds another gun.</summary>
    public void Remember(string gearJson, uint checksum)
    {
        if (OwnAway) return;
        Own = Own != null && Own.Checksum == checksum && Own.GearJson == gearJson ? Own : new OwnSpecialWeapon(gearJson, checksum, 0, 0f);
    }

    /// <summary>The own weapon leaves the slot with this stock (read from the slot just before another gun is asked for).</summary>
    public void OwnLeaves(int clip, float pool)
    {
        if (Own == null) throw new InvalidOperationException("Remember the own weapon before it leaves the slot.");
        Own = Own with { Clip = clip, Pool = pool };
        OwnAway = true;
    }

    /// <summary>The own weapon is settled in the slot again (its stock written back): pickups go through the game again.</summary>
    public void OwnReturned()
    {
        OwnAway = false;
        if (_leases.Count == 0) Own = null;
    }

    /// <summary>The own weapon's kept pool after a pickup counted against it.</summary>
    internal void Refilled(float pool)
    {
        if (Own != null) Own = Own with { Pool = pool };
    }

    /// <summary>A checkpoint: what is away now is what a reload brings back.</summary>
    internal void CaptureCheckpoint()
    {
        _checkpoint = OwnAway ? Own : null;
        CheckpointHolder = OwnAway ? Top?.Owner : null;
    }

    /// <summary>A checkpoint reload: the game put the slot back as it was at the checkpoint, so the own weapon is away again with the
    /// stock it had then, or it is in the slot again.</summary>
    internal void RecallCheckpoint()
    {
        if (_checkpoint != null)
        {
            Own = _checkpoint;
            OwnAway = true;
            return;
        }
        OwnReturned();
    }

    /// <summary>Level end: the game rebuilds the inventory; every lease is dropped and nothing is kept.</summary>
    internal void Clear()
    {
        _leases.Clear();
        Own = _checkpoint = null;
        OwnAway = false;
        CheckpointHolder = null;
    }

    internal bool Holds(SpecialSlotLease lease) => _leases.Contains(lease);

    internal bool IsSuspended(SpecialSlotLease lease)
    {
        var at = _leases.IndexOf(lease);
        return at >= 0 && at < _leases.Count - 1;
    }

    internal bool IsReady(SpecialSlotLease lease)
    {
        var at = _leases.IndexOf(lease);
        if (at < 0) return false;
        for (var i = 0; i < at; i++)
            if (!_leases[i].Parked) return false;
        return true;
    }

    internal void Release(SpecialSlotLease lease)
    {
        if (!_leases.Remove(lease)) return;
        lease.Parked = false;
        if (_leases.Count == 0 && !OwnAway) Own = null;
    }
}
