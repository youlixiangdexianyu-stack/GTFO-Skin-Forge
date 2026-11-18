using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>Which icon files of a folder to register, without Unity or the file system: the names asked for (a name, or a prefix ending
/// in <c>*</c>) against the PNG names the folder holds, and which file each one reads (the <c>@2x</c> picture on a tall screen, else
/// the plain one, else the other).</summary>
internal static class IconFolder
{
    /// <summary>Screens taller than this many lines use the <c>@2x</c> file.</summary>
    public const int LargeScreenLines = 1440;

    private const string Double = "@2x";

    /// <summary>True when <paramref name="screenHeight"/> asks for the <c>@2x</c> pictures.</summary>
    public static bool UseDouble(int screenHeight) => screenHeight > LargeScreenLines;

    /// <summary>The icon names to register: every plain name asked for (in order, once), then for each prefix pattern (<c>mark-*</c>) the
    /// names found in <paramref name="files"/> (file names without folder, with or without <c>.png</c>, <c>@2x</c> files folded into the
    /// plain name), sorted. Names that are not a file are still listed, so the caller can report them missing.</summary>
    public static List<string> Names(IReadOnlyCollection<string> files, IReadOnlyList<string> asked)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in asked)
            if (!name.EndsWith("*", StringComparison.Ordinal) && seen.Add(name)) names.Add(name);
        foreach (var pattern in asked)
        {
            if (!pattern.EndsWith("*", StringComparison.Ordinal)) continue;
            var prefix = pattern.Substring(0, pattern.Length - 1);
            var found = new List<string>();
            foreach (var file in files)
            {
                var name = BaseName(file);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(name)) found.Add(name);
            }
            found.Sort(StringComparer.Ordinal);
            names.AddRange(found);
        }
        return names;
    }

    /// <summary>The file (as it appears in <paramref name="files"/>) icon <paramref name="name"/> reads on a screen that wants the double
    /// pictures or not, or null when the folder holds neither.</summary>
    public static string? Pick(IReadOnlyCollection<string> files, string name, bool wantDouble)
    {
        string? plain = null, big = null;
        foreach (var file in files)
        {
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = file.Substring(0, file.Length - 4);
            if (string.Equals(stem, name, StringComparison.Ordinal)) plain = file;
            else if (string.Equals(stem, name + Double, StringComparison.Ordinal)) big = file;
        }
        return wantDouble ? big ?? plain : plain ?? big;
    }

    private static string BaseName(string file)
    {
        var name = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
        return name.EndsWith(Double, StringComparison.Ordinal) ? name.Substring(0, name.Length - Double.Length) : name;
    }
}
