using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeCore;

/// <summary>Reports a repeating condition without flooding: the same key is written the 1st, 10th, 100th, 1000th ... time it
/// occurs, with the running count after the first. The output target is the caller's (an Engine log, a mod log, a journal);
/// this type binds to none. No game or Unity type, so game-independent Core code can use it.</summary>
public sealed class OnceReporter
{
    private readonly Action<string>? _sink;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>A reporter that writes to <paramref name="sink"/>.</summary>
    public OnceReporter(Action<string> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>A counting gate with no output target: only <see cref="ShouldReport"/> and <see cref="Count"/> apply.</summary>
    public OnceReporter() { }

    /// <summary>Counts one occurrence of <paramref name="key"/>; writes <paramref name="message"/> to the sink when it is the 1st,
    /// 10th, 100th ... Returns whether it was written.</summary>
    public bool Report(string key, string message)
    {
        var count = Tick(key);
        if (!Reports(count)) return false;
        Write(count, message);
        return true;
    }

    /// <summary>Like <see cref="Report(string, string)"/>, but builds the message only when it is written. Pass a static lambda and
    /// the values it needs as <paramref name="state"/>, so a repeating condition on a hot path allocates nothing.</summary>
    public bool Report<TState>(string key, TState state, Func<TState, string> message)
    {
        var count = Tick(key);
        if (!Reports(count)) return false;
        Write(count, message(state));
        return true;
    }

    /// <summary>Counts one occurrence of <paramref name="key"/> and returns whether it is the 1st, 10th, 100th ... Writes nothing;
    /// for callers that log by their own means.</summary>
    public bool ShouldReport(string key) => Reports(Tick(key));

    private long Tick(string key)
    {
        lock (_gate)
        {
            _counts.TryGetValue(key, out var count);
            _counts[key] = ++count;
            return count;
        }
    }

    private void Write(long count, string message)
    {
        if (_sink is null) throw new InvalidOperationException("This OnceReporter has no output target; use ShouldReport.");
        _sink(count == 1 ? message : message + " (" + count.ToString(CultureInfo.InvariantCulture) + " times)");
    }

    /// <summary>How many times <paramref name="key"/> has been reported since the last <see cref="Reset"/>.</summary>
    public long Count(string key)
    {
        lock (_gate) return _counts.TryGetValue(key, out var count) ? count : 0;
    }

    /// <summary>Forgets every count; the next occurrence of any key is the 1st again. Called when a level starts or ends.</summary>
    public void Reset()
    {
        lock (_gate) _counts.Clear();
    }

    /// <summary>1, 10, 100, 1000 ...</summary>
    private static bool Reports(long count)
    {
        if (count <= 0) return false;
        while (count % 10 == 0) count /= 10;
        return count == 1;
    }
}
