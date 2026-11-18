using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ForgeCore;

public enum StoreStatus { Missing, Read, Corrupt }

/// <summary>What <see cref="Store.Read{T}(string)"/> found: nothing yet, the value, or a file it could not read (<see cref="Detail"/> says why).</summary>
public readonly record struct StoreRead<T>(StoreStatus Status, T? Value, string Detail);

/// <summary>Local progress, one JSON file per mod. Only read and write: no structure migration. A file that cannot be read is
/// reported and never overwritten.</summary>
public static class Store
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    /// <summary>Paths whose file this process has read whole or written itself, so <see cref="Write{T}(string, string, T)"/> need not read them again.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths whose temporary files left by an earlier process were cleared.</summary>
    private static readonly HashSet<string> Swept = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The newest text waiting for the background writer, per path (an older one for the same path is dropped unwritten).</summary>
    private static readonly Dictionary<string, (string ModId, string Text)> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static bool _writing;
    private static readonly object WriteGate = new();

    /// <summary>The folder of the store files, set by ForgeCore's plugin to <c>BepInEx/ForgeCore/Store</c> when it loads; empty before.</summary>
    internal static string Root { get; set; } = "";

    public static string PathOf(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A mod ID is a plugin GUID usable as a file name.", nameof(modId));
        if (Root.Length == 0) throw new InvalidOperationException("The store folder is set when ForgeCore loads; a mod reads or writes its store after that.");
        return Path.Combine(Root, modId + ".forge");
    }

    public static StoreRead<T> Read<T>(string modId) => Read<T>(modId, PathOf(modId));

    /// <summary><see cref="Read{T}(string)"/> for a file the mod keeps at its own <paramref name="path"/>; <paramref name="modId"/> names the log.
    /// A value <see cref="Write{T}(string, string, T)"/> has not finished writing yet is read as already written.</summary>
    public static StoreRead<T> Read<T>(string modId, string path) => Read<T>(modId, path, report: true);

    /// <summary><paramref name="report"/> false leaves the log to a caller that reports an unreadable file itself (local clear progress moves it aside).</summary>
    internal static StoreRead<T> Read<T>(string modId, string path, bool report)
    {
        string? pending = null;
        lock (Pending) if (Pending.TryGetValue(path, out var job)) pending = job.Text;
        if (pending == null && !File.Exists(path)) return new(StoreStatus.Missing, default, path);
        try
        {
            var value = JsonSerializer.Deserialize<T>(pending ?? File.ReadAllText(path), Json);
            if (value == null) return new(StoreStatus.Corrupt, default, path + ": empty");
            lock (Pending) Known.Add(path);
            return new(StoreStatus.Read, value, path);
        }
        catch (Exception error) when (error is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            if (report) Log.For(modId).Error("core-store-file-cannot-be-read-left", "Store file " + path + " cannot be read; it is left as it is: " + error);
            return new(StoreStatus.Corrupt, default, path + ": " + error);
        }
    }

    /// <summary>Writes <paramref name="text"/> (UTF-8, no byte order mark) as the whole content of <paramref name="path"/>; see <see cref="WriteBytes"/>.</summary>
    public static void WriteText(string path, string text) => WriteBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    /// <summary>Writes <paramref name="bytes"/> as the whole content of <paramref name="path"/>: to a temporary file beside it first
    /// (<c>.&lt;name&gt;.&lt;random&gt;.tmp</c>, flushed to the disk), then over the old one (retried briefly while a reader holds it), so a crash or a full disk never leaves half a file.
    /// Creates the folder; the first write of a path in a process removes the temporary files an earlier process left behind. Throws the
    /// <see cref="IOException"/> family when it cannot write; the caller decides what that costs.</summary>
    public static void WriteBytes(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        Sweep(path, folder, name);
        // A name of its own, so two writers of one path never share a temporary file.
        var temporary = Path.Combine(folder, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            // A Windows reader or virus scanner can deny the replace for a moment: retried for at most 450 ms, the last failure is thrown.
            for (var attempt = 0; ; attempt++)
                try { File.Move(temporary, path, true); break; }
                catch (IOException error) when (attempt < 9 && (error.HResult & 0xffff) is 5 or 32 or 33) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(50); }
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } // Cleanup only: the write failure itself is rethrown.
            throw;
        }
    }

    /// <summary>A process that died between creating a temporary file and moving it leaves it behind; the first write of the path in the
    /// next process removes those older than the process itself (one still being written by a live writer is newer).</summary>
    private static void Sweep(string path, string folder, string name)
    {
        lock (Swept) if (!Swept.Add(path)) return;
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        foreach (var stale in Directory.EnumerateFiles(folder, "." + name + ".*.tmp"))
            try { if (File.GetLastWriteTimeUtc(stale) < started) File.Delete(stale); }
            catch (IOException) { } // Left for the next process: a stale temporary file is never read.
            catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes the whole value (to a temporary file first, then over the old one). Refuses, with an error in the mod's log,
    /// when the existing file cannot be read as <typeparamref name="T"/> (the same test as <see cref="Read{T}(string)"/>), so a damaged save is never replaced unseen.</summary>
    public static bool Write<T>(string modId, T value) => Write(modId, PathOf(modId), value);

    /// <summary><see cref="Write{T}(string, T)"/> for a file the mod keeps at its own <paramref name="path"/>; <paramref name="modId"/> names the log.
    /// The value is serialized at once and written to the disk on a background thread, so a save never stalls the game; a newer write of the
    /// same path replaces one not yet written. The existing file is checked only the first time a path is read or written in a process.
    /// False means refused or not serializable (logged); a failed disk write is logged by the writer, and the next write tries again.</summary>
    public static bool Write<T>(string modId, string path, T value)
    {
        try
        {
            bool known;
            lock (Pending) known = Known.Contains(path);
            if (!known && File.Exists(path))
            {
                try { if (JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) == null) throw new JsonException("empty"); }
                catch (JsonException error)
                {
                    Log.For(modId).ErrorOnce("core-store-file-damaged-not-overwritten", "Store file " + path + " is damaged; not overwritten: " + error);
                    return false;
                }
            }
            var text = JsonSerializer.Serialize(value, Json);
            lock (Pending)
            {
                Known.Add(path);
                Pending[path] = (modId, text);
                if (_writing) return true;
                _writing = true;
            }
            ThreadPool.QueueUserWorkItem(_ => WritePending());
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.For(modId).ErrorOnce("core-store-file-could-not-be-written", "Store file " + path + " could not be written: " + error);
            return false;
        }
    }

    /// <summary>Writes every value <see cref="Write{T}(string, string, T)"/> has not written yet, on the calling thread, and returns when the disk has them.
    /// ForgeCore calls it when the game shuts down, after every mod's quit callback; a mod may call it too.</summary>
    public static void Flush()
    {
        while (WriteNext()) { }
    }

    private static void WritePending()
    {
        while (true)
        {
            lock (Pending) if (Pending.Count == 0) { _writing = false; return; }
            WriteNext();
        }
    }

    /// <summary>Writes one pending value, whichever the dictionary yields first (each path holds only its newest value, so the order
    /// between paths does not matter); false when nothing was pending. One writer at a time (the background thread or <see cref="Flush"/>),
    /// so a newer value of a path never reaches the disk before an older one.</summary>
    private static bool WriteNext()
    {
        lock (WriteGate)
        {
            string path;
            (string ModId, string Text) job;
            lock (Pending)
            {
                using var next = Pending.GetEnumerator();
                if (!next.MoveNext()) return false;
                (path, job) = (next.Current.Key, next.Current.Value);
            }
            // Any failure is logged and the value dropped, so the writer never stops and the next write of the path tries again.
            try { WriteText(path, job.Text); }
            catch (Exception error)
            {
                try { Log.For(job.ModId).ErrorOnce("core-store-file-could-not-be-written", "Store file " + path + " could not be written: " + error); } catch { } // The log itself failed: nowhere left to report, and the writer thread must not stop.
            }
            // Removed only now, and only if no newer text arrived meanwhile, so a read in between still sees the value.
            lock (Pending) if (Pending.TryGetValue(path, out var current) && ReferenceEquals(current.Text, job.Text)) Pending.Remove(path);
            return true;
        }
    }
}
