using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeCore;

/// <summary>Every directory under one root, indexed by name (case-insensitive) in a single walk.</summary>
internal sealed class ContentFolderIndex
{
    private readonly Dictionary<string, SortedSet<string>> byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders that could not be listed during the walk.</summary>
    internal List<string> Errors { get; } = new();

    internal static ContentFolderIndex Build(string root)
    {
        var index = new ContentFolderIndex();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var parent = pending.Pop();
            string[] children;
            try { children = Directory.GetDirectories(parent); }
            catch (Exception error) { index.Errors.Add(parent + ": could not enumerate folders: " + error); continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!index.byName.TryGetValue(name, out var set)) index.byName[name] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(Path.GetFullPath(child));
                pending.Push(child);
            }
        }
        return index;
    }

    internal IReadOnlyList<string> Find(string folderName) =>
        byName.TryGetValue(folderName, out var set) ? new List<string>(set) : Array.Empty<string>();
}
