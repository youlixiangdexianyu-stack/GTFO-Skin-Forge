using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>What happened to a banner request.</summary>
internal enum BannerOutcome : byte
{
    /// <summary>Shown at once (nothing was showing, or the same owner's banner was replaced).</summary>
    Shown,
    /// <summary>Shown at once and the lower priority banner of another owner that was showing was taken away.</summary>
    Preempted,
    /// <summary>Waits behind a banner of equal or higher priority.</summary>
    Queued,
    /// <summary>Not shown: the queue is full of banners that outrank it.</summary>
    Dropped,
}

/// <summary>
/// The one banner slot every mod shares (the middle of the screen). A request from an owner replaces that owner's own banner, showing
/// or waiting. Otherwise a higher priority request takes the slot from the banner showing (which is dropped: a banner is a moment and
/// would be stale later); an equal or lower one waits behind it. Waiting banners show by priority, then in the order they were asked,
/// and are dropped when they waited longer than <see cref="MaxWait"/>. Two banners never show at once.
/// </summary>
internal sealed class BannerQueue
{
    public const int Capacity = 4;
    public const float MaxWait = 6f, FadeSeconds = 0.5f;

    internal sealed class Request
    {
        internal Request(object owner, string ownerId, int priority, string text, NoticeTone tone, float seconds, string sub, float asked, long order)
        {
            Owner = owner;
            OwnerId = ownerId;
            Priority = priority;
            Text = text;
            Tone = tone;
            Seconds = seconds;
            Sub = sub;
            Asked = asked;
            Order = order;
        }

        public object Owner { get; }
        public string OwnerId { get; }
        public int Priority { get; }
        public string Text { get; }
        public NoticeTone Tone { get; }
        public float Seconds { get; }
        public string Sub { get; }
        public float Asked { get; }
        /// <summary>Counts requests, so of two asked in the same frame the earlier one ranks first.</summary>
        public long Order { get; }
    }

    private readonly List<Request> _waiting = new();
    private long _order;

    /// <summary>The banner showing, or null.</summary>
    public Request? Current { get; private set; }

    /// <summary>When <see cref="Current"/> started.</summary>
    public float Started { get; private set; }

    /// <summary>Changes whenever another banner starts or the slot empties.</summary>
    public int Version { get; private set; }

    public int Waiting => _waiting.Count;

    /// <summary>The banner the last call took away or dropped (for the log), or null.</summary>
    public Request? Displaced { get; private set; }

    public BannerOutcome Show(object owner, string ownerId, int priority, string text, NoticeTone tone, float seconds, string sub, float now)
    {
        Displaced = null;
        var request = new Request(owner, ownerId, priority, text ?? "", tone, Math.Max(0.3f, seconds), sub ?? "", now, ++_order);
        for (var i = _waiting.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_waiting[i].Owner, owner)) _waiting.RemoveAt(i);
        if (Current == null || ReferenceEquals(Current.Owner, owner))
        {
            Start(request, now);
            return BannerOutcome.Shown;
        }
        if (priority > Current.Priority)
        {
            Displaced = Current;
            Start(request, now);
            return BannerOutcome.Preempted;
        }
        if (_waiting.Count == Capacity)
        {
            var weakest = Weakest();
            if (Outranks(_waiting[weakest], request))
            {
                Displaced = request;
                return BannerOutcome.Dropped;
            }
            Displaced = _waiting[weakest];
            _waiting.RemoveAt(weakest);
        }
        _waiting.Add(request);
        return BannerOutcome.Queued;
    }

    /// <summary>Takes <paramref name="owner"/>'s banner away, showing or waiting.</summary>
    public void Hide(object owner, float now)
    {
        for (var i = _waiting.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_waiting[i].Owner, owner)) _waiting.RemoveAt(i);
        if (Current != null && ReferenceEquals(Current.Owner, owner)) Next(now);
    }

    /// <summary>Ends the banner whose time and fade are over and starts the next one waiting; returns the requests dropped because
    /// they waited too long (empty list most frames, never allocated then).</summary>
    public void Tick(float now, List<Request>? expired = null)
    {
        for (var i = _waiting.Count - 1; i >= 0; i--)
        {
            if (now - _waiting[i].Asked <= MaxWait) continue;
            expired?.Add(_waiting[i]);
            _waiting.RemoveAt(i);
        }
        if (Current != null && now - Started >= Current.Seconds + FadeSeconds) Next(now);
    }

    private void Next(float now)
    {
        Current = null;
        Version++;
        if (_waiting.Count == 0) return;
        var best = 0;
        for (var i = 1; i < _waiting.Count; i++)
            if (Outranks(_waiting[i], _waiting[best])) best = i;
        var next = _waiting[best];
        _waiting.RemoveAt(best);
        Start(next, now);
    }

    private void Start(Request request, float now)
    {
        Current = request;
        Started = now;
        Version++;
    }

    private int Weakest()
    {
        var weakest = 0;
        for (var i = 1; i < _waiting.Count; i++)
            if (Outranks(_waiting[weakest], _waiting[i])) weakest = i;
        return weakest;
    }

    /// <summary>Higher priority first, then the one asked earlier.</summary>
    private static bool Outranks(Request a, Request b) => a.Priority != b.Priority ? a.Priority > b.Priority : a.Order < b.Order;
}

/// <summary>What the arbiter said to a full-screen layer that wants to draw.</summary>
internal enum ScreenGrant : byte
{
    /// <summary>It holds the screen (it already did, or the screen was free).</summary>
    Granted,
    /// <summary>It holds the screen now; the lower priority holder was told to put itself away.</summary>
    Preempted,
    /// <summary>Another layer holds the screen, or waits with a better claim; ask again next frame.</summary>
    Waiting,
}

/// <summary>
/// One exclusive full-screen layer at a time (a power-on, a fade to black: anything that covers the whole picture). A layer asks
/// every frame it wants to draw and releases when it stops. A request of higher priority than the holder takes the screen at once and
/// the holder is told to put itself away (it may ask again and then waits). Otherwise the request waits; when the screen frees, the
/// waiter with the best claim (highest priority, then the one waiting longest) that asked within <see cref="WaitWindow"/> gets it.
/// Overlay layers (edge frames, vignettes, flashes) do not take part: any number of them draw together with each other and with the
/// exclusive layer.
/// </summary>
internal sealed class ScreenArbiter
{
    /// <summary>A waiter that has not asked for this long has given up.</summary>
    public const float WaitWindow = 0.5f;

    private sealed class Claim
    {
        internal object Key = null!;
        internal string Id = "";
        internal int Priority;
        internal float Since, Last;
        internal Action? Preempted;
    }

    private readonly List<Claim> _waiting = new();
    private Claim? _holder;

    /// <summary>The ID of the layer holding the screen, or null.</summary>
    public string? Holder => _holder?.Id;

    /// <summary>For <see cref="ScreenGrant.Waiting"/>: who holds or has the better claim; for <see cref="ScreenGrant.Preempted"/>: who was put away.</summary>
    public string? Other { get; private set; }

    public ScreenGrant Request(object key, string id, int priority, float now, Action? preempted)
    {
        Other = null;
        if (_holder != null && ReferenceEquals(_holder.Key, key))
        {
            _holder.Last = now;
            return ScreenGrant.Granted;
        }
        var claim = Waiter(key) ?? new Claim { Key = key, Id = id, Since = now };
        claim.Priority = priority;
        claim.Last = now;
        claim.Preempted = preempted;
        if (_holder != null)
        {
            if (priority > _holder.Priority)
            {
                var old = _holder;
                Other = old.Id;
                Take(claim);
                old.Preempted?.Invoke();
                return ScreenGrant.Preempted;
            }
            Wait(claim);
            Other = _holder.Id;
            return ScreenGrant.Waiting;
        }
        for (var i = 0; i < _waiting.Count; i++)
        {
            var other = _waiting[i];
            if (ReferenceEquals(other, claim) || now - other.Last > WaitWindow) continue;
            if (other.Priority > priority || other.Priority == priority && other.Since < claim.Since)
            {
                Wait(claim);
                Other = other.Id;
                return ScreenGrant.Waiting;
            }
        }
        Take(claim);
        return ScreenGrant.Granted;
    }

    /// <summary>The layer stopped drawing: it no longer holds or waits for the screen.</summary>
    public void Release(object key)
    {
        if (_holder != null && ReferenceEquals(_holder.Key, key)) _holder = null;
        for (var i = _waiting.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_waiting[i].Key, key)) _waiting.RemoveAt(i);
    }

    private Claim? Waiter(object key)
    {
        foreach (var claim in _waiting)
            if (ReferenceEquals(claim.Key, key)) return claim;
        return null;
    }

    private void Wait(Claim claim)
    {
        if (!_waiting.Contains(claim)) _waiting.Add(claim);
    }

    private void Take(Claim claim)
    {
        _waiting.Remove(claim);
        _holder = claim;
    }
}
