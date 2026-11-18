using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeCore;

/// <summary>The SHA-256 of a large file that rarely changes (the game's GameAssembly.dll), kept across launches in one small text file
/// (<see cref="CacheFile"/>, <c>BepInEx/cache/ForgeCore/file-sha256.txt</c>): a file whose full path, length, last write time and creation
/// time are the ones recorded is not read again. No cache file, any line that is not five well-formed fields, any other key, or a file
/// that changed while it was read means the whole file is read and hashed. The cache only skips re-reading an unchanged file; the caller
/// still compares the result with its expected hash every launch. The cache is written to a temporary file and moved over the old one;
/// a cache that cannot be written only costs the next launch a read. ForgePerformance keeps its own copy (it depends on no Forge package).</summary>
public static class FileHashes
{
    /// <summary>The cache file (set by ForgeCore's plugin); null keeps nothing across launches.</summary>
    internal static string? CacheFile { get; set; }
    private static readonly object Gate = new();
    private static Dictionary<string, (string Key, string Hash)>? _known;

    /// <summary>The file's SHA-256 as 64 lowercase hex characters (<see cref="Hash.Sha256Hex(Stream)"/>). Throws the
    /// <see cref="IOException"/> family when the file cannot be read.</summary>
    public static string Sha256Hex(string path)
    {
        var info = new FileInfo(path);
        var key = Key(info);
        lock (Gate)
        {
            _known ??= Read();
            if (_known.TryGetValue(info.FullName, out var kept) && kept.Key == key) return kept.Hash;
        }
        string hash;
        using (var stream = info.OpenRead()) hash = Hash.Sha256Hex(stream);
        info.Refresh();
        // A file that changed while it was read keeps no record: the next launch reads it again.
        if (Key(info) == key) lock (Gate) { _known[info.FullName] = (key, hash); Write(); }
        return hash;
    }

    private static string Key(FileInfo info) => info.Length + "\t" + info.LastWriteTimeUtc.Ticks + "\t" + info.CreationTimeUtc.Ticks;

    // One line per file: full path, length, last write ticks, creation ticks, hash. One bad line discards the whole file.
    private static Dictionary<string, (string Key, string Hash)> Read()
    {
        var known = new Dictionary<string, (string Key, string Hash)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (CacheFile == null || !File.Exists(CacheFile)) return known;
            foreach (var line in File.ReadAllLines(CacheFile))
            {
                var parts = line.Split('\t');
                if (parts.Length != 5 || parts[0].Length == 0 || !long.TryParse(parts[1], out _) || !long.TryParse(parts[2], out _)
                    || !long.TryParse(parts[3], out _) || parts[4].Length != 64 || !LowerHex(parts[4])) { known.Clear(); return known; }
                known[parts[0]] = (parts[1] + "\t" + parts[2] + "\t" + parts[3], parts[4]);
            }
        }
        catch (Exception) { known.Clear(); } // An unreadable cache is no cache.
        return known;
    }

    private static bool LowerHex(string text)
    {
        foreach (var c in text) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static void Write()
    {
        if (CacheFile == null) return;
        var text = new System.Text.StringBuilder();
        foreach (var (file, value) in _known!) text.Append(file).Append('\t').Append(value.Key).Append('\t').Append(value.Hash).Append('\n');
        // Any failure only costs the next launch a read; it never reaches the caller.
        try { Store.WriteText(CacheFile, text.ToString()); }
        catch (Exception) { }
    }
}
