using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeCore;

/// <summary>Logging for one mod, keyed by its BepInEx plugin GUID. Only errors are written, to that mod's own log source: a failed
/// feature, or data or configuration that leaves something unusable. A state the game or a player can legitimately be in is not
/// an error and is not written.</summary>
public sealed class ModLog
{
    private readonly Action<string> _sink;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    internal ModLog(string modId, Action<string> sink)
    {
        ModId = modId;
        _sink = sink;
    }

    public string ModId { get; }

    public void Error(string message) => Write(message);

    /// <summary>An error with its registered diagnostic code (<c>catalog/authoring/diagnostic-codes.json</c>, named package-event): written as <c>code: detail</c>.</summary>
    public void Error(string code, string detail) => Write(code + ": " + detail);

    /// <summary><see cref="ErrorOnce(string)"/> for an error with its registered diagnostic code, written as <c>code: detail</c>.</summary>
    public void ErrorOnce(string code, string detail) => ErrorOnce(code + ": " + detail);

    /// <summary>An error that can repeat: written the first time this exact <paramref name="message"/> occurs and never again, so a
    /// repeating failure stays visible once without flooding the log. Different failures from one place have different messages and
    /// are each written; put the exception (<c>error.ToString()</c>) in the message, not just its text.</summary>
    public void ErrorOnce(string message)
    {
        lock (_seen)
            if (!_seen.Add(message)) return;
        Write(message);
    }

    /// <summary>Calls every handler subscribed to an event in turn: one that throws is written once (<see cref="ErrorOnce(string, string)"/>
    /// with <paramref name="code"/>, as "A <paramref name="what"/> handler failed") and does not stop the others.</summary>
    public void RaiseEach(Action? handlers, string code, string what)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
            try { ((Action)handler)(); }
            catch (Exception error) { Failed(code, what, error); }
    }

    /// <inheritdoc cref="RaiseEach(Action?, string, string)"/>
    public void RaiseEach<T>(Action<T>? handlers, T arg, string code, string what)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
            try { ((Action<T>)handler)(arg); }
            catch (Exception error) { Failed(code, what, error); }
    }

    /// <inheritdoc cref="RaiseEach(Action?, string, string)"/>
    public void RaiseEach<T1, T2>(Action<T1, T2>? handlers, T1 arg1, T2 arg2, string code, string what)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
            try { ((Action<T1, T2>)handler)(arg1, arg2); }
            catch (Exception error) { Failed(code, what, error); }
    }

    /// <inheritdoc cref="RaiseEach(Action?, string, string)"/>
    public void RaiseEach<T1, T2, T3>(Action<T1, T2, T3>? handlers, T1 arg1, T2 arg2, T3 arg3, string code, string what)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
            try { ((Action<T1, T2, T3>)handler)(arg1, arg2, arg3); }
            catch (Exception error) { Failed(code, what, error); }
    }

    private void Failed(string code, string what, Exception error) => ErrorOnce(code, "A " + what + " handler failed: " + error);

    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>True the first time <paramref name="key"/> is asked: a failing place keyed this way is written once even when its text
    /// changes (a count, a path), and the text is built only when it is written.</summary>
    internal bool FirstReport(string key)
    {
        lock (_keys) return _keys.Add(key);
    }

    private void Write(string message)
    {
        try { _sink(message); }
        catch (Exception error) { Console.Error.WriteLine(ModId + ": " + message + " (log sink failed: " + error + ")"); }
    }
}
/// <summary>Per-mod logs (<see cref="For"/>) and the one way to write a number or a position into a message.</summary>
public static partial class Log
{
    /// <summary>A number for a message: at most two decimals, no trailing zeros, always a dot.</summary>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A position for a message: <c>(x, y, z)</c>, two decimals each, always a dot.</summary>
    public static string Pos(float x, float y, float z)
        => "(" + x.ToString("0.00", CultureInfo.InvariantCulture) + ", " + y.ToString("0.00", CultureInfo.InvariantCulture)
           + ", " + z.ToString("0.00", CultureInfo.InvariantCulture) + ")";

    private static readonly Dictionary<string, ModLog> Logs = new(StringComparer.Ordinal);

    /// <summary>The host (ForgeCore's plugin) turns a mod ID into its log source; offline it writes to the console.</summary>
    internal static Func<string, Action<string>> Sinks { get; set; } =
        id => message => Console.WriteLine("[Error: " + id + "] " + message);

    public static ModLog For(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        lock (Logs)
        {
            if (Logs.TryGetValue(modId, out var log)) return log;
            log = new ModLog(modId, Sinks(modId));
            Logs.Add(modId, log);
            return log;
        }
    }
}
