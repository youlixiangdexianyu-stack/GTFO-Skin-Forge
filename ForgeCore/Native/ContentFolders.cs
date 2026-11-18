using System.Collections.Generic;
using BepInEx;

namespace ForgeCore;

/// <summary>Finds the content folders mods ship inside the BepInEx plugins tree.</summary>
public static class ContentFolders
{
    private static ContentFolderIndex? index;

    /// <summary>Every directory named <paramref name="folderName"/> (case-insensitive) under BepInEx's plugins folder, full paths, sorted and distinct. The tree is walked once per process; folders that could not be listed are added to <paramref name="errors"/> on every call.</summary>
    public static IReadOnlyList<string> Find(string folderName, List<string> errors)
    {
        index ??= ContentFolderIndex.Build(Paths.PluginPath);
        errors.AddRange(index.Errors);
        return index.Find(folderName);
    }
}
