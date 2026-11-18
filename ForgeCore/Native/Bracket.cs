using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The patch kinds of one bracket's patch class: what its subscriptions need on the game method right now.</summary>
[Flags]
internal enum PatchKinds { None = 0, Prefix = 1, Postfix = 2, Finalizer = 4 }

/// <summary>One game method patched once by ForgeCore, handed to every package that wants to see it. The patch calls
/// <see cref="RunBefore"/> from a prefix at <c>Priority.First</c>, <see cref="RunAfter"/> from a postfix at <c>Priority.Last</c> and
/// <see cref="RunFaulted"/> from a finalizer at <c>Priority.Last</c>, so a subscriber reads the call around every other patch of the
/// method. A subscriber's state travels from its before to its after through ForgeCore (a value, never boxed); a subscriber that only
/// listens at one end gives nothing for the other. A handler that throws is one <c>ErrorOnce</c> line and the others still run.
/// Only the patch kinds the current subscriptions need are on the method (<see cref="Patches.Set"/>): a <c>Before</c> or a
/// <c>Skip</c> needs the prefix, an <c>After</c> the postfix, an <c>Around</c> whose state reaches an after or a faulted all three (a
/// body that throws still takes its state back); disposing the last subscription of a kind takes that kind off. Main thread only.</summary>
public sealed class Bracket<TCall>
{
    private interface ITap
    {
        PatchKinds Kinds { get; }
        void Before(TCall call);
        void After(TCall call, bool ran);
        void Faulted(TCall call, Exception error);
    }

    private sealed class Tap<TState> : ITap
    {
        private readonly Bracket<TCall> _owner;
        private readonly Func<TCall, TState>? _before;
        private readonly Action<TCall, TState, bool>? _after;
        private readonly Action<TCall, TState, Exception>? _faulted;
        private readonly Stack<TState>? _states;

        internal Tap(Bracket<TCall> owner, Func<TCall, TState>? before, Action<TCall, TState, bool>? after, Action<TCall, TState, Exception>? faulted)
        {
            _owner = owner;
            _before = before;
            _after = after;
            _faulted = faulted;
            // A state is kept only for an end that reads it; it is taken back at either end, so both are patched while it is kept.
            if (before != null && (after != null || faulted != null)) _states = new();
            Kinds = (before != null ? PatchKinds.Prefix : 0) | (after != null || _states != null ? PatchKinds.Postfix : 0)
                | (faulted != null || _states != null ? PatchKinds.Finalizer : 0);
        }

        public PatchKinds Kinds { get; }

        public void Before(TCall call)
        {
            if (_before == null) return;
            var state = default(TState)!;
            try { state = _before(call); }
            catch (Exception error) { _owner.Failed("before", error); }
            _states?.Push(state);
        }

        private bool Take(out TState state)
        {
            state = default!;
            return _states == null || _states.TryPop(out state!);
        }

        public void After(TCall call, bool ran)
        {
            if (!Take(out var state)) return;
            try { _after?.Invoke(call, state, ran); }
            catch (Exception error) { _owner.Failed("after", error); }
        }

        public void Faulted(TCall call, Exception fault)
        {
            if (!Take(out var state)) return;
            try { _faulted?.Invoke(call, state, fault); }
            catch (Exception error) { _owner.Failed("faulted", error); }
        }
    }

    private readonly string _module;
    private readonly Type _patches;
    private ITap[] _taps = [];
    private Func<TCall, bool>[] _skips = [];
    private PatchKinds _on;
    private long _calls;

    internal Bracket(string module, Type patches)
    {
        _module = module;
        _patches = patches;
    }

    /// <summary>The module name, as ForgeCore logs it (<c>GameHooks.WeakDoorState</c>).</summary>
    public string Module => _module;

    /// <summary>Calls of the game method seen since the process started, counted at the first end that is on (the prefix, else the
    /// postfix); a bracket with nothing on counts nothing.</summary>
    public long Calls => _calls;

    /// <summary>True once something subscribed; the patch skips building the call otherwise.</summary>
    internal bool Active => _taps.Length != 0 || _skips.Length != 0;

    /// <summary>Sees the call before the game's body (and before any other patch of it). Disposing the result ends the subscription.</summary>
    public IDisposable Before(Action<TCall> before)
    {
        if (before == null) throw new ArgumentNullException(nameof(before));
        return Add(new Tap<bool>(this, call => { before(call); return true; }, null, null));
    }

    /// <summary>Sees the call after the game's body returned and every other postfix ran; <c>ran</c> is false when a prefix skipped the body.</summary>
    public IDisposable After(Action<TCall, bool> after)
    {
        if (after == null) throw new ArgumentNullException(nameof(after));
        return Add(new Tap<bool>(this, null, (call, _, ran) => after(call, ran), null));
    }

    /// <summary>Sees the call at both ends and keeps its own state between them: <paramref name="before"/> answers the state,
    /// <paramref name="after"/> gets it back when the body returned and <paramref name="faulted"/> when the body threw (the exception is
    /// still the game's to throw).</summary>
    public IDisposable Around<TState>(Func<TCall, TState> before, Action<TCall, TState, bool>? after, Action<TCall, TState, Exception>? faulted = null)
        => Add(new Tap<TState>(this, before ?? throw new ArgumentNullException(nameof(before)), after, faulted));

    /// <summary>Answers true to keep the game's body from running for this call (a method patched with <see cref="Skipped"/> in its prefix;
    /// only those methods honour it). Every other subscriber still sees the call, and its <c>after</c> gets <c>ran</c> false. Asked before the other patches, so the first one that answers true wins.</summary>
    public IDisposable Skip(Func<TCall, bool> skip)
    {
        if (skip == null) throw new ArgumentNullException(nameof(skip));
        Apply(_taps, [.. _skips, skip]);
        return new Subscription(() => Apply(_taps, Array.FindAll(_skips, other => !ReferenceEquals(other, skip))));
    }

    private IDisposable Add(ITap tap)
    {
        Apply([.. _taps, tap], _skips);
        return new Subscription(() => Apply(Array.FindAll(_taps, other => !ReferenceEquals(other, tap)), _skips));
    }

    // The kinds go on before a new subscription is seen and come off after an ended one is gone, so no subscriber misses an end it needs.
    private void Apply(ITap[] taps, Func<TCall, bool>[] skips)
    {
        var wanted = skips.Length != 0 ? PatchKinds.Prefix : PatchKinds.None;
        foreach (var tap in taps) wanted |= tap.Kinds;
        if ((wanted & ~_on) != 0) _on = Patches.Set(_module, _patches, _on | wanted);
        _taps = taps;
        _skips = skips;
        if ((_on & ~wanted) != 0) _on = Patches.Set(_module, _patches, wanted);
    }

    internal void RunBefore(TCall call)
    {
        _calls++;
        foreach (var tap in _taps) tap.Before(call);
    }

    /// <summary>True when a <see cref="Skip"/> subscriber wants the body not to run; a subscriber that throws does not skip it.</summary>
    internal bool Skipped(TCall call)
    {
        foreach (var skip in _skips)
        {
            try { if (skip(call)) return true; }
            catch (Exception error) { Failed("skip", error); }
        }
        return false;
    }

    internal void RunAfter(TCall call, bool ran)
    {
        if ((_on & PatchKinds.Prefix) == 0) _calls++;
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].After(call, ran);
    }

    internal void RunFaulted(TCall call, Exception error)
    {
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].Faulted(call, error);
    }

    private void Failed(string end, Exception error)
        => Plugin.Logger.ErrorOnce("core-bracket-handler-failed", "A " + _module + " handler failed (" + end + "): " + error);
}

/// <summary>One subscription to a ForgeCore hook; disposing it ends the subscription (once).</summary>
internal sealed class Subscription : IDisposable
{
    private Action? _end;
    internal Subscription(Action end) => _end = end;
    public void Dispose()
    {
        var end = _end;
        _end = null;
        end?.Invoke();
    }
}
