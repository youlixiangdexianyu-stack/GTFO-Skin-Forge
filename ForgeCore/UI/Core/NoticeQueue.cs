using System;

namespace ForgeUI;

/// <summary>What a notice is about, which sets its colour.</summary>
public enum NoticeTone : byte
{
    Info,
    Good,
    Warn,
    Alert
}

/// <summary>The newest-first list behind the shared corner notices (every mod pushes into the same one, so their notices stack instead of
/// overlapping): a fixed number of slots, no allocation after construction. A notice
/// slides in, stays for its time and fades out; pushing the text that is already showing refreshes it instead of stacking a copy;
/// pushing into a full list drops the oldest. <see cref="Version"/> changes whenever a line is added or removed, so a view lays
/// its text out only then and per frame only follows <see cref="Alpha"/> and <see cref="Slide"/>.</summary>
public sealed class NoticeQueue
{
    public const float FadeIn = 0.2f, FadeOut = 0.4f;

    private readonly string?[] _text;
    private readonly object?[] _owner;
    private readonly NoticeTone[] _tone;
    private readonly float[] _born, _seconds;

    public NoticeQueue(int capacity = 4)
    {
        Capacity = Math.Max(1, capacity);
        _text = new string?[Capacity];
        _owner = new object?[Capacity];
        _tone = new NoticeTone[Capacity];
        _born = new float[Capacity];
        _seconds = new float[Capacity];
    }

    public int Capacity { get; }

    /// <summary>How many notices are showing.</summary>
    public int Count { get; private set; }

    /// <summary>Changes whenever a notice is added or removed.</summary>
    public int Version { get; private set; }

    /// <summary>Shows <paramref name="text"/> at the top of the list from <paramref name="now"/> for <paramref name="seconds"/>
    /// (not counting the slide-in and fade-out), on behalf of <paramref name="owner"/> (see <see cref="Clear(object)"/>).</summary>
    public void Push(string text, NoticeTone tone, float seconds, float now, object? owner = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (var i = 0; i < Count; i++)
        {
            if (!string.Equals(_text[i], text, StringComparison.Ordinal)) continue;
            Remove(i);
            break;
        }
        if (Count == Capacity) Count--;
        for (var i = Count; i > 0; i--) Copy(i - 1, i);
        _text[0] = text;
        _owner[0] = owner;
        _tone[0] = tone;
        _born[0] = now;
        _seconds[0] = Math.Max(0.1f, seconds);
        Count++;
        Version++;
    }

    /// <summary>Takes away the notices whose time is up.</summary>
    public void Tick(float now)
    {
        for (var i = Count - 1; i >= 0; i--)
            if (now - _born[i] >= _seconds[i] + FadeIn + FadeOut) Remove(i);
    }

    public void Clear()
    {
        if (Count == 0) return;
        for (var i = 0; i < Count; i++)
        {
            _text[i] = null;
            _owner[i] = null;
        }
        Count = 0;
        Version++;
    }

    /// <summary>Takes away the notices <paramref name="owner"/> pushed.</summary>
    public void Clear(object owner)
    {
        for (var i = Count - 1; i >= 0; i--)
            if (ReferenceEquals(_owner[i], owner)) Remove(i);
    }

    public string Text(int index) => _text[index]!;

    public NoticeTone Tone(int index) => _tone[index];

    /// <summary>The notice's opacity, 0..1: rising while it slides in, falling at the end.</summary>
    public float Alpha(int index, float now)
    {
        var age = now - _born[index];
        var left = _seconds[index] + FadeIn + FadeOut - age;
        return Math.Min(Ease.Clamp01(age / FadeIn), Ease.Clamp01(left / FadeOut));
    }

    /// <summary>How far the notice has slid in, 0 (just started, offset sideways) to 1 (in place).</summary>
    public float Slide(int index, float now) => Ease.OutCubic((now - _born[index]) / FadeIn);

    private void Remove(int index)
    {
        for (var i = index; i < Count - 1; i++) Copy(i + 1, i);
        Count--;
        _text[Count] = null;
        _owner[Count] = null;
        Version++;
    }

    private void Copy(int from, int to)
    {
        _text[to] = _text[from];
        _owner[to] = _owner[from];
        _tone[to] = _tone[from];
        _born[to] = _born[from];
        _seconds[to] = _seconds[from];
    }
}
