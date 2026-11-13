using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>A button the lease presses itself (see <c>InputLease.Press</c>): held until released, with one press edge and one
/// release edge, each answered to the first query frame of the game's input layer and to every further query in that same frame.</summary>
public struct InputButtonState
{
    public bool Held, DownPending, UpPending;
    public int PressFrame, ReleaseFrame, DownQueryFrame, UpQueryFrame;
}

/// <summary>The stacked input leases behind <c>InputLease</c>: the blocked actions are every held lease's together, steering
/// comes from the newest lease that steers, and nothing is blocked once every lease is released. Actions are the game's
/// <c>InputAction</c> values. Main thread only; reading allocates nothing.</summary>
internal sealed class InputLeaseBook
{
    internal const int Actions = 128;
    /// <summary>The world-interaction switches a lease can hold off (<c>InputLease.BlockInteractions</c>).</summary>
    internal const int Interactions = 3;

    internal sealed class Entry
    {
        internal readonly string Owner;
        internal readonly bool[] Blocked = new bool[Actions];
        internal bool Steers;
        internal float Sideways, Forward;
        internal readonly InputButtonState[] Buttons = new InputButtonState[Actions];
        internal readonly bool[] Pressed = new bool[Actions];
        internal readonly bool[] HeldOff = new bool[Interactions];
        internal int AxisAction = -1;
        internal float AxisValue;
        internal Func<string?>? Guard;
        internal Action<string>? Invalidated;
        internal Entry(string owner) => Owner = owner;
    }

    private readonly List<Entry> _held = new();
    private readonly bool[] _blocked = new bool[Actions];
    private readonly bool[] _pressed = new bool[Actions], _axes = new bool[Actions];
    private readonly bool[] _interactionsOff = new bool[Interactions];

    internal int Count => _held.Count;

    /// <summary>Some lease blocks some action or steers: the game's input must be wrapped.</summary>
    internal bool Engaged { get; private set; }

    internal Entry Take(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(owner));
        var entry = new Entry(owner);
        _held.Add(entry);
        return entry;
    }

    internal bool Holds(Entry entry) => _held.Contains(entry);

    internal void Block(Entry entry, ReadOnlySpan<int> actions)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        Array.Clear(entry.Blocked);
        foreach (var action in actions)
        {
            if (action < 0 || action >= Actions) throw new ArgumentOutOfRangeException(nameof(actions), "Input action " + action + " is outside 0.." + (Actions - 1) + ".");
            entry.Blocked[action] = true;
        }
        Recompute();
    }

    /// <summary>Holds off exactly the world-interaction switches <paramref name="switches"/> for this lease (replacing what it held off before).</summary>
    internal void HoldOff(Entry entry, ReadOnlySpan<int> switches)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        Array.Clear(entry.HeldOff);
        foreach (var index in switches)
        {
            if (index < 0 || index >= Interactions) throw new ArgumentOutOfRangeException(nameof(switches), "Interaction switch " + index + " is outside 0.." + (Interactions - 1) + ".");
            entry.HeldOff[index] = true;
        }
        Recompute();
    }

    /// <summary>Some held lease holds this interaction switch off.</summary>
    internal bool InteractionOff(int index) => _interactionsOff[index];

    internal void Steer(Entry entry, bool steers, float sideways, float forward)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        entry.Steers = steers;
        entry.Sideways = Math.Clamp(sideways, -1f, 1f);
        entry.Forward = Math.Clamp(forward, -1f, 1f);
        Recompute();
    }

    /// <summary>Holds the button and arms a fresh press edge, also when the same button was held or released before.</summary>
    internal void Press(Entry entry, int action, int frame)
    {
        Check(entry, action);
        entry.Pressed[action] = true;
        entry.Buttons[action] = new InputButtonState
        {
            Held = true,
            DownPending = true,
            PressFrame = frame,
            ReleaseFrame = -1,
            DownQueryFrame = -1,
            UpQueryFrame = -1
        };
        Recompute();
    }

    /// <summary>Lets go of a leased button; true when its press edge was never queried (the hold is dropped either way).</summary>
    internal bool ReleaseButton(Entry entry, int action, int frame)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        if (action < 0 || action >= Actions || !entry.Pressed[action]) return false;
        ref var button = ref entry.Buttons[action];
        if (!button.Held && button.UpPending) return false;
        var unconsumed = button.DownQueryFrame < 0;
        button.Held = false;
        button.ReleaseFrame = frame;
        button.UpPending = true;
        button.UpQueryFrame = -1;
        return unconsumed;
    }

    /// <summary>One axis per lease answers with <paramref name="value"/>; a second axis action on the same lease is refused.</summary>
    internal void SetAxis(Entry entry, int action, float value)
    {
        Check(entry, action);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (entry.AxisAction >= 0 && entry.AxisAction != action)
            throw new InvalidOperationException("input.axis-already-leased:" + entry.AxisAction);
        entry.AxisAction = action;
        entry.AxisValue = value;
        Recompute();
    }

    internal void Guarded(Entry entry, Func<string?> invalid, Action<string> invalidated)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        entry.Guard = invalid;
        entry.Invalidated = invalidated;
    }

    /// <summary>Some held lease has a lifetime guard to ask before it answers.</summary>
    internal bool HasGuard()
    {
        foreach (var entry in _held) if (entry.Guard != null) return true;
        return false;
    }

    internal List<Entry> Snapshot() => new(_held);

    private void Check(Entry entry, int action)
    {
        if (!Holds(entry)) throw new InvalidOperationException("The input lease of " + entry.Owner + " was already released.");
        if (action <= 0 || action >= Actions) throw new ArgumentOutOfRangeException(nameof(action), "Input action " + action + " is outside 1.." + (Actions - 1) + ".");
    }

    /// <summary>Some lease answers <paramref name="action"/>. A button released more than a frame ago has had its release edge answered and
    /// stops answering, so the player's own press of that key reads through again while the lease is still held.</summary>
    internal bool Presses(int action, int frame)
    {
        if (action < 0 || action >= Actions || !_pressed[action]) return false;
        var retired = false;
        foreach (var entry in _held)
        {
            if (!entry.Pressed[action] || entry.Buttons[action].Held || frame <= entry.Buttons[action].ReleaseFrame + 1) continue;
            entry.Pressed[action] = false;
            retired = true;
        }
        if (retired) Recompute();
        return _pressed[action];
    }
    internal bool Drives(int action) => action >= 0 && action < Actions && _axes[action];

    internal bool Held(int action) => Newest(action)?.Buttons[action].Held ?? false;

    internal bool Down(int action, int frame)
    {
        if (Newest(action) is not { } entry) return false;
        ref var button = ref entry.Buttons[action];
        if (button.DownQueryFrame >= 0) return frame == button.DownQueryFrame;
        if (!button.DownPending) return false;
        button.DownPending = false;
        button.DownQueryFrame = frame;
        return true;
    }

    internal bool Up(int action, int frame)
    {
        if (Newest(action) is not { } entry) return false;
        ref var button = ref entry.Buttons[action];
        if (button.UpQueryFrame >= 0) return frame == button.UpQueryFrame;
        if (!button.UpPending) return false;
        button.UpPending = false;
        button.UpQueryFrame = frame;
        return true;
    }

    internal float Axis(int action)
    {
        for (var i = _held.Count - 1; i >= 0; i--)
            if (_held[i].AxisAction == action) return _held[i].AxisValue;
        return 0f;
    }

    private Entry? Newest(int action)
    {
        for (var i = _held.Count - 1; i >= 0; i--)
            if (_held[i].Pressed[action]) return _held[i];
        return null;
    }

    internal bool Release(Entry entry)
    {
        if (!_held.Remove(entry)) return false;
        Recompute();
        return true;
    }

    internal int ReleaseAll()
    {
        var count = _held.Count;
        _held.Clear();
        Recompute();
        return count;
    }

    internal bool Blocks(int action) => action >= 0 && action < Actions && _blocked[action];

    /// <summary>The newest steering lease's value for a blocked movement axis, 0 when none steers.</summary>
    internal float Steered(bool sideways)
    {
        for (var i = _held.Count - 1; i >= 0; i--)
            if (_held[i].Steers) return sideways ? _held[i].Sideways : _held[i].Forward;
        return 0f;
    }

    private void Recompute()
    {
        Array.Clear(_blocked);
        Array.Clear(_pressed);
        Array.Clear(_axes);
        Array.Clear(_interactionsOff);
        var any = false;
        foreach (var entry in _held)
        {
            for (var i = 0; i < Interactions; i++)
                if (entry.HeldOff[i]) _interactionsOff[i] = true;
            any |= entry.Steers;
            if (entry.AxisAction >= 0) { _axes[entry.AxisAction] = true; any = true; }
            for (var i = 0; i < Actions; i++)
                if (entry.Pressed[i]) { _pressed[i] = true; any = true; }
            for (var i = 0; i < Actions; i++)
                if (entry.Blocked[i]) { _blocked[i] = true; any = true; }
        }
        Engaged = any;
    }
}
