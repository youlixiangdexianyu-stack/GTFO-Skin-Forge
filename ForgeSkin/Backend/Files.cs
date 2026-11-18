using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ForgeSkin.Backend;

// A durable file written whole or not at all: the content goes to <target>.tmp (a stale one from an aborted write is
// deleted first), Commit moves it over the target, and a temp that was never committed is deleted on Dispose.
public sealed class AtomicFile : IDisposable
{
    readonly string _target;
    bool _committed;
    public string Temp { get; }

    public AtomicFile(string target)
    {
        _target = target;
        Temp = target + ".tmp";
        if (File.Exists(Temp)) File.Delete(Temp);
    }

    // `move` replaces the default overwrite (game files are swapped with the shared reader closed).
    public void Commit(Action<string, string>? move = null)
    {
        if (move != null) move(Temp, _target);
        else File.Move(Temp, _target, true);
        _committed = true;
    }

    public void Dispose()
    {
        if (!_committed && File.Exists(Temp)) File.Delete(Temp);
    }

    public static void WriteText(string path, string text)
    {
        using var file = new AtomicFile(path);
        File.WriteAllText(file.Temp, text);
        file.Commit();
    }
}

public static class FileHash
{
    static readonly ConcurrentDictionary<string, (long Size, DateTime Time, string Sha)> Known = new(StringComparer.OrdinalIgnoreCase);

    // SHA-256 as upper-case hex. `cached`: reuse the hash while the file keeps its size and write time (status reports only;
    // anything that decides whether to overwrite a file hashes it afresh).
    public static string Sha256(string path, bool cached = false)
    {
        var info = new FileInfo(path);
        if (cached && Known.TryGetValue(path, out var k) && k.Size == info.Length && k.Time == info.LastWriteTimeUtc) return k.Sha;
        string sha;
        using (var s = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(s));
        Known[path] = (info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }
}
