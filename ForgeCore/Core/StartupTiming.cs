using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ForgeCore;

/// <summary>Startup measurement of one plugin's Load (or another named scope): the elapsed time of each phase it marks, written
/// as one <c>startup.timing</c> line through the caller's sink when disposed, with the hook work completed meanwhile
/// (<see cref="StartupOperationTiming"/>). Every Forge package, the libraries included, uses this one class.</summary>
public sealed class StartupTiming : IDisposable
{
    private readonly Action<string> _report;
    private readonly string _plugin, _scope;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _phases = new();
    private readonly StartupOperationTiming.Snapshot _operations = StartupOperationTiming.Read();
    private double _previous;
    private string _phase = "host", _result = "failed";
    private bool _disposed;

    public StartupTiming(string plugin, Action<string> report, string scope = "load")
    { _plugin = plugin; _report = report ?? throw new ArgumentNullException(nameof(report)); _scope = scope; }

    public void Mark(string phase)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _phases.Add(_phase + "Ms=" + (now - _previous).ToString("F3", CultureInfo.InvariantCulture));
        _previous = now; _phase = phase;
    }
    public void Complete(string result = "loaded") => _result = result;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Mark("end"); _clock.Stop();
        string line = "startup.timing plugin=" + _plugin + " result=" + _result + " scope=" + _scope
            + " startedUtc=" + _startedUtc.ToString("O", CultureInfo.InvariantCulture)
            + " endedUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + " elapsedMs=" + _previous.ToString("F3", CultureInfo.InvariantCulture) + " " + string.Join(" ", _phases)
            + StartupOperationTiming.Read().Since(_operations);
        // The caller's own log failed; the console is the only place left that keeps the measurement.
        try { _report(line); }
        catch (Exception error) { Console.Error.WriteLine(line + " (startup.timing sink failed: " + error + ")"); }
    }
}

/// <summary>Cumulative completed hook work, not another scheduler or log. Patch time includes wrapper generation; snapshots describe
/// operations completed during the enclosing startup scope and must not be added together. PatchInstallation and MethodResolution
/// nest inside Declaration, so they attribute the declaration time rather than extend it.</summary>
public static class StartupOperationTiming
{
    public enum Operation { MethodResolution, PatchInstallation, Declaration }
    private static readonly string[] Names = { "methodResolution", "patchInstallation", "declaration" };
    private static readonly long[] Ticks = new long[Names.Length], Calls = new long[Names.Length];
    public readonly struct Measurement : IDisposable
    {
        private readonly Operation _operation;
        private readonly long _started;
        internal Measurement(Operation operation) { _operation = operation; _started = Stopwatch.GetTimestamp(); }
        public void Dispose() => Finish(_operation, _started);
    }
    public static Measurement Measure(Operation operation) => new(operation);
    private static void Finish(Operation operation, long started)
    {
        Interlocked.Add(ref Ticks[(int)operation], Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref Calls[(int)operation]);
    }
    internal readonly struct Snapshot
    {
        private readonly long[] _ticks, _calls;
        internal Snapshot(long[] ticks, long[] calls) { _ticks = ticks; _calls = calls; }
        internal string Since(Snapshot before)
        {
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < Names.Length; i++)
                text.Append(' ').Append(Names[i]).Append("Ms=")
                    .Append(((_ticks[i] - before._ticks[i]) * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture))
                    .Append(' ').Append(Names[i]).Append("Calls=").Append((_calls[i] - before._calls[i]).ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }
    internal static Snapshot Read()
    {
        var ticks = new long[Names.Length]; var calls = new long[Names.Length];
        for (var i = 0; i < Names.Length; i++) { ticks[i] = Interlocked.Read(ref Ticks[i]); calls[i] = Interlocked.Read(ref Calls[i]); }
        return new Snapshot(ticks, calls);
    }
}
