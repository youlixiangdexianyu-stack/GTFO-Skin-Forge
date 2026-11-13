using System;
using System.Linq;
using System.Threading;

namespace ForgeCore;

/// <summary>One subscription to a hook's calls, bound to the world it was taken in: it ends by itself ("world-epoch-ended") the
/// first time it is asked about after <c>world()</c> moved on, so a subscriber of an old world is never called in a new one.
/// A release that throws leaves the lease active so its owner can release it again.</summary>
public class HookLease : IDisposable
{
    private Action? _release;
    private int _ending;
    private readonly Func<long> _world;
    private readonly Action<HookLease>? _ended;
    private long _callbacks;

    /// <summary>A lease of <paramref name="consumer"/> taken in world <paramref name="epoch"/>; <paramref name="world"/> answers the
    /// current world (a lease that spans worlds passes a constant), <paramref name="release"/> unsubscribes and <paramref name="ended"/>
    /// hears that it ended.</summary>
    public HookLease(string consumer, long epoch, Func<long> world, Action release, Action<HookLease>? ended = null)
    {
        Consumer = consumer; WorldEpoch = epoch;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _ended = ended;
    }

    public string Consumer { get; }
    public long WorldEpoch { get; }
    /// <summary>"active", or why it ended: "released", "world-epoch-ended", "observer-failed", "replaced" ...</summary>
    public string Status { get; private set; } = "active";
    /// <summary>Calls delivered to this lease's callback.</summary>
    public long Callbacks => Interlocked.Read(ref _callbacks);
    public void CallbackEntered() => Interlocked.Increment(ref _callbacks);

    public bool IsActive
    {
        get
        {
            if (Status == "active" && _world() != WorldEpoch) End("world-epoch-ended");
            return Status == "active";
        }
    }

    public void End(string status)
    {
        if (Interlocked.CompareExchange(ref _ending, 1, 0) != 0) return;
        try
        {
            if (_release == null) return;
            _release.Invoke();
            _release = null;
            Status = status;
            _ended?.Invoke(this);
        }
        finally { Volatile.Write(ref _ending, 0); }
    }

    public void Dispose() => End("released");
}

/// <summary>What <see cref="HookFanout{T}.Publish"/> did with one call.</summary>
public enum HookDispatch { Delivered, NoSubscribers, WorldEpochStale }

/// <summary>One hook's calls handed to every subscriber of the current world, in subscription order. A subscriber that throws is
/// ended ("observer-failed") and reported once through <c>failed</c>; the others still run. Main thread only.</summary>
public sealed class HookFanout<T>
{
    private sealed record Observer(HookLease Lease, Action<T> Callback);
    private readonly Func<long> _world;
    private readonly Action<string, Exception> _failed;
    private readonly int _budget;
    private readonly Func<string, long, Action, Action<HookLease>?, HookLease> _lease;
    private Observer[] _observers = Array.Empty<Observer>();
    private long _calls;

    /// <summary>A fan-out over <paramref name="world"/> that reports a failing subscriber through <paramref name="failed"/>, takes at
    /// most <paramref name="budget"/> subscribers and makes each lease with <paramref name="lease"/> (consumer, world, release, ended)
    /// when a caller hands out its own lease type.</summary>
    public HookFanout(Func<long> world, Action<string, Exception> failed, int budget = 256,
        Func<string, long, Action, Action<HookLease>?, HookLease>? lease = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _budget = budget;
        _lease = lease ?? ((consumer, epoch, release, ended) => new HookLease(consumer, epoch, _world, release, ended));
    }

    /// <summary>Every call published, with or without subscribers.</summary>
    public long Calls => Interlocked.Read(ref _calls);
    public HookLease[] Leases => _observers.Select(observer => observer.Lease).ToArray();

    /// <summary>Subscribes <paramref name="consumer"/> for the world <paramref name="worldEpoch"/>. Refuses (InvalidOperationException
    /// with the native-hook-* reason) a world that is not the current one, a consumer already subscribed and a full fan-out.</summary>
    public HookLease Subscribe(string consumer, long worldEpoch, Action<T> callback, Action<HookLease>? ended = null)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 128) throw new ArgumentException("native-hook-consumer-invalid");
        if (worldEpoch < 0 || _world() != worldEpoch) throw new InvalidOperationException("native-hook-world-epoch-stale");
        foreach (var observer in _observers) _ = observer.Lease.IsActive;
        if (_observers.Any(observer => observer.Lease.Consumer == consumer)) throw new InvalidOperationException("native-hook-consumer-duplicate");
        if (_observers.Length >= _budget) throw new InvalidOperationException("native-hook-subscriber-budget");
        HookLease? lease = null;
        lease = _lease(consumer, worldEpoch, () => _observers = _observers.Where(observer => !ReferenceEquals(observer.Lease, lease)).ToArray(), ended);
        _observers = _observers.Append(new Observer(lease, callback)).ToArray();
        return lease;
    }

    /// <summary>Hands <paramref name="observation"/> of world <paramref name="epoch"/> to the active subscribers. Only the subscriber
    /// array is snapshotted; a world change inside a callback stops delivery to the rest.</summary>
    public HookDispatch Publish(T observation, long epoch)
    {
        if (epoch < 0 || _world() != epoch) return HookDispatch.WorldEpochStale;
        Interlocked.Increment(ref _calls);
        var delivered = false;
        foreach (var observer in _observers)
        {
            if (_world() != epoch) return HookDispatch.WorldEpochStale;
            if (!observer.Lease.IsActive) continue;
            try { observer.Lease.CallbackEntered(); observer.Callback(observation); delivered = true; }
            catch (Exception error)
            {
                observer.Lease.End("observer-failed");
                _failed(observer.Lease.Consumer, error);
            }
        }
        return _world() != epoch ? HookDispatch.WorldEpochStale : delivered ? HookDispatch.Delivered : HookDispatch.NoSubscribers;
    }
}
