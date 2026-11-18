using System;

namespace ForgeCore;

/// <summary>The one backslash-to-slash conversion and the one relative-path rule: Windows paths and prefab names become
/// forward-slash text, and every path that comes from a package, a work or a document is checked here. Mirrors
/// <c>toPosix</c> in the website's <c>posix-path.ts</c>. No game or Unity type, so any package may use it.</summary>
public static class PosixPath
{
    /// <summary>Returns <paramref name="value"/> with every backslash replaced by a forward slash.</summary>
    public static string ToPosix(string value) => value.Replace('\\', '/');

    /// <summary>A `/`-separated path below some root: not empty, no backslash, and every segment non-empty and neither
    /// `.` nor `..`, so no leading, trailing or doubled separator and no step out of the root. Unity hierarchy and asset
    /// paths use this as is, since an object name may end in a space; a path that names a file goes through
    /// <see cref="IsRelativeFile"/>.</summary>
    public static bool IsRelative(string path)
    {
        if (path.Length == 0 || path.IndexOf('\\') >= 0) return false;
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or "..") return false;
        return true;
    }

    /// <summary>A relative path that names one file on every system: <see cref="IsRelative"/>, no control character,
    /// none of the characters Windows refuses in a name (`:` also rules out a drive, a drive-relative path and a stream),
    /// no segment ending in a space or dot (Windows drops them, so two spellings would name one file) and no reserved
    /// device name.</summary>
    public static bool IsRelativeFile(string path)
    {
        if (!IsRelative(path)) return false;
        foreach (var character in path)
            if (character < ' ' || character is ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        foreach (var segment in path.Split('/'))
            if (segment[segment.Length - 1] is ' ' or '.' || IsDeviceName(segment)) return false;
        return true;
    }

    /// <summary>CON, PRN, AUX, NUL, COM1-9 or LPT1-9 in any case, alone or before an extension.</summary>
    private static bool IsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment.Substring(0, dot);
        if (stem.Length == 3)
            return stem.Equals("con", StringComparison.OrdinalIgnoreCase) || stem.Equals("prn", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("aux", StringComparison.OrdinalIgnoreCase) || stem.Equals("nul", StringComparison.OrdinalIgnoreCase);
        return stem.Length == 4 && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase));
    }
}
