using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>How a confirmation ended.</summary>
public enum ConfirmAnswer : byte
{
    /// <summary>The player confirmed.</summary>
    Confirm,
    /// <summary>The player cancelled (the cancel key or button, or a click outside the window).</summary>
    Cancel,
    /// <summary>Nobody answered: the asking mod withdrew it, the queue was full, or the screen it was asked on went away.</summary>
    Dismissed
}

/// <summary>
/// The confirmation queue as a state machine, without Unity: one question shows at a time, at most <see cref="Pending"/> wait behind it
/// in the order asked, and a question asked while that many wait is refused at once (<see cref="ConfirmAnswer.Dismissed"/>, never
/// dropped silently). Every question ends exactly once through the <c>answered</c> callback given to the constructor: answered,
/// withdrawn by its ticket or by its owner, or dismissed with all others.
/// </summary>
internal sealed class ConfirmQueue<T>
{
    /// <summary>Questions that may wait behind the one shown.</summary>
    public const int Pending = 4;

    private readonly struct Entry
    {
        internal Entry(int ticket, string owner, T question) { Ticket = ticket; Owner = owner; Question = question; }
        internal readonly int Ticket;
        internal readonly string Owner;
        internal readonly T Question;
    }

    private readonly Action<T, ConfirmAnswer> _answered;
    private readonly List<Entry> _entries = new(Pending + 1);
    private int _nextTicket;

    public ConfirmQueue(Action<T, ConfirmAnswer> answered) => _answered = answered ?? throw new ArgumentNullException(nameof(answered));

    /// <summary>Whether a question shows now.</summary>
    public bool Showing => _entries.Count > 0;

    /// <summary>The question shown now (check <see cref="Showing"/> first).</summary>
    public T Current => _entries.Count > 0 ? _entries[0].Question : throw new InvalidOperationException("no question is showing");

    /// <summary>Questions shown or waiting.</summary>
    public int Count => _entries.Count;

    /// <summary>Changes every time the question shown changes (for redrawing).</summary>
    public int Version { get; private set; }

    /// <summary>Asks <paramref name="question"/> for <paramref name="owner"/>. Returns its ticket (above 0); 0 when the queue is full,
    /// in which case the question was already answered <see cref="ConfirmAnswer.Dismissed"/>.</summary>
    public int Ask(string owner, T question)
    {
        if (_entries.Count > Pending)
        {
            _answered(question, ConfirmAnswer.Dismissed);
            return 0;
        }
        _nextTicket = _nextTicket == int.MaxValue ? 1 : _nextTicket + 1;
        _entries.Add(new Entry(_nextTicket, owner, question));
        if (_entries.Count == 1) Version++;
        return _nextTicket;
    }

    /// <summary>The shown question is answered; the next one shows. False when none showed.</summary>
    public bool Answer(ConfirmAnswer answer)
    {
        if (_entries.Count == 0) return false;
        End(0, answer);
        return true;
    }

    /// <summary>Withdraws one question, shown or waiting (<see cref="ConfirmAnswer.Dismissed"/>). False when it already ended.</summary>
    public bool Withdraw(int ticket)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Ticket == ticket) { End(i, ConfirmAnswer.Dismissed); return true; }
        return false;
    }

    /// <summary>Withdraws every question of <paramref name="owner"/>; each is answered as dismissed.</summary>
    public void WithdrawOwner(string owner)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
            if (string.Equals(_entries[i].Owner, owner, StringComparison.Ordinal)) End(i, ConfirmAnswer.Dismissed);
    }

    private void End(int index, ConfirmAnswer answer)
    {
        var entry = _entries[index];
        _entries.RemoveAt(index);
        if (index == 0) Version++;
        // Removed before the callback, so a callback that asks again queues behind the rest.
        _answered(entry.Question, answer);
    }
}

/// <summary>Where a tooltip box goes, without Unity: centred over its anchor, above it when there is room inside the bounds, else below,
/// and moved sideways to stay inside. All rectangles are centre and size in one space with y up.</summary>
internal static class TooltipLayout
{
    /// <summary>The box's centre for a <paramref name="width"/> x <paramref name="height"/> tooltip over an anchor at
    /// (<paramref name="anchorX"/>, <paramref name="anchorY"/>) of half height <paramref name="anchorHalfHeight"/>, <paramref name="gap"/>
    /// clear of it, kept inside the bounds (centre <paramref name="boundsX"/>, <paramref name="boundsY"/>, half size
    /// <paramref name="boundsHalfWidth"/> x <paramref name="boundsHalfHeight"/>). <paramref name="below"/> says it flipped under the anchor.</summary>
    public static (float X, float Y) Place(float anchorX, float anchorY, float anchorHalfHeight, float width, float height, float gap,
        float boundsX, float boundsY, float boundsHalfWidth, float boundsHalfHeight, out bool below)
    {
        var top = boundsY + boundsHalfHeight;
        var bottom = boundsY - boundsHalfHeight;
        var above = anchorY + anchorHalfHeight + gap + height / 2f;
        below = above + height / 2f > top && anchorY - anchorHalfHeight - gap - height >= bottom;
        var y = below ? anchorY - anchorHalfHeight - gap - height / 2f : above;
        var left = boundsX - boundsHalfWidth + width / 2f;
        var right = boundsX + boundsHalfWidth - width / 2f;
        var x = left > right ? boundsX : Math.Clamp(anchorX, left, right);
        return (x, y);
    }
}
