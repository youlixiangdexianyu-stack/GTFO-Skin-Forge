using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>A field receipt, not a snapshot of its containing object. Once it observes another writer changing the field,
/// this receipt cannot acquire it again, even if a later value happens to equal our last write.</summary>
public struct FieldReceipt<T> where T : struct
{
    public readonly T Original;
    private T _last;
    private readonly Func<T, T, bool>? _equal;
    private bool _written, _lost;

    public FieldReceipt(T original, Func<T, T, bool>? equal = null)
    { Original = _last = original; _equal = equal; _written = _lost = false; }

    /// <summary>True while the field still holds what this receipt last wrote (or its original, before the first write).</summary>
    public bool Accepts(T current)
    {
        if (!(_equal?.Invoke(current, _last) ?? EqualityComparer<T>.Default.Equals(current, _last))) _lost = true;
        return !_lost;
    }

    public void Written(T current) { _last = current; _written = true; }

    /// <summary>True when the field is still ours and we wrote it: the original may be put back.</summary>
    public bool Release(T current) => Accepts(current) && _written;
}

/// <summary>
/// One field that several holders borrow in turn: the latest holder's value shows, a holder letting go gives the field to the one before
/// it, and the last one gives back the value the field had before any of them. Changes run as a straight transition over the seconds the
/// call names (<see cref="Tick"/> steps them). The field is read and written through the two delegates, so this class holds no game type.
/// A writer outside the holders that changes the field takes it: the transition and every hold end there (<see cref="Entry.Alive"/> goes
/// false) and the original is never written back over the other writer's value; the next <see cref="Hold"/> starts over from whatever
/// the field shows then. Main thread only.
/// </summary>
internal sealed class FieldLeases<T> where T : struct
{
    /// <summary>One holder's claim on the field.</summary>
    internal sealed class Entry
    {
        internal T Value;
        public bool Alive { get; internal set; }
    }

    private readonly Func<T> _read;
    private readonly Action<T> _write;
    private readonly Func<T, T, float, T> _lerp;
    private readonly Func<T, T, bool>? _equal;
    private readonly List<Entry> _stack = new();
    private FieldReceipt<T> _receipt;
    private T _original, _from, _to;
    private float _seconds, _elapsed;
    private bool _live, _moving;

    public FieldLeases(Func<T> read, Action<T> write, Func<T, T, float, T> lerp, Func<T, T, bool>? equal = null)
    { _read = read; _write = write; _lerp = lerp; _equal = equal; }

    /// <summary>True from the first hold until the original is back (or the field was taken by another writer).</summary>
    public bool Live => _live;

    /// <summary>True while a transition is still running.</summary>
    public bool Moving => _moving;

    /// <summary>The value the field has without any holder: the original while held, else what it shows now.</summary>
    public T Native => _live ? _original : _read();

    /// <summary>True when the field is still held and nobody else changed it since the last write.</summary>
    public bool Owns(Entry entry) => entry.Alive && _live && _receipt.Release(_read());

    /// <summary>Takes the field for <paramref name="entry"/> (null: a new claim) at <paramref name="value"/>, moving there over <paramref name="seconds"/> (0: at once).
    /// A claim already held moves to the top of the stack with its new value.</summary>
    public Entry Hold(Entry? entry, T value, float seconds)
    {
        if (entry is { Alive: true }) _stack.Remove(entry); else entry = new Entry();
        if (!_live) { _original = _read(); _receipt = new FieldReceipt<T>(_original, _equal); _live = true; }
        entry.Value = value;
        entry.Alive = true;
        _stack.Add(entry);
        Go(seconds);
        return entry;
    }

    /// <summary>Lets <paramref name="entry"/> go: the field moves to the next holder's value, or back to the original, over <paramref name="seconds"/> (0: at once).</summary>
    public void Drop(Entry entry, float seconds)
    {
        if (!entry.Alive) return;
        entry.Alive = false;
        _stack.Remove(entry);
        if (!_live) return;
        if (_stack.Count == 0 && seconds <= 0f)
        {
            if (_receipt.Release(_read())) _write(_original);
            _live = _moving = false;
            return;
        }
        Go(seconds);
    }

    /// <summary>One frame of the running transition.</summary>
    public void Tick(float deltaSeconds)
    {
        if (!_moving) return;
        _elapsed += Math.Max(0f, deltaSeconds);
        var done = _elapsed >= _seconds;
        Apply(done ? _to : _lerp(_from, _to, _elapsed / _seconds));
        if (!done || !_live) return;
        _moving = false;
        if (_stack.Count == 0) _live = false;
    }

    /// <summary>Ends every hold without writing (the object the field belongs to is gone).</summary>
    public void Abandon()
    {
        foreach (var entry in _stack) entry.Alive = false;
        _stack.Clear();
        _live = _moving = false;
    }

    private void Go(float seconds)
    {
        _from = _read();
        _to = _stack.Count > 0 ? _stack[^1].Value : _original;
        _seconds = seconds;
        _elapsed = 0f;
        _moving = seconds > 0f;
        if (_moving) return;
        Apply(_to);
        if (_live && _stack.Count == 0) _live = false;
    }

    private void Apply(T value)
    {
        if (!_receipt.Accepts(_read())) { Abandon(); return; }
        _write(value);
        _receipt.Written(_read());
    }
}
