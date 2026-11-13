using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>Names of everything a mod can call by name: <c>Pack:item</c>. <c>Game:</c> is the game's own catalog and
/// <c>Forge:</c> this library's base pieces; no mod registers under either.</summary>
internal static class VisualNames
{
    internal const string Game = "Game", Forge = "Forge";

    /// <summary>Null when <paramref name="pack"/> is a usable pack name, else why not. <paramref name="builtIn"/> lets this library
    /// register under <c>Forge:</c>.</summary>
    internal static string? CheckPack(string? pack, bool builtIn = false)
    {
        if (string.IsNullOrEmpty(pack)) return "the pack name is empty";
        foreach (var c in pack)
            if (!IsNameChar(c) || c == '.') return "the pack name '" + pack + "' may only hold letters, digits, '_' and '-'";
        if (string.Equals(pack, Game, StringComparison.OrdinalIgnoreCase)) return "'Game' is the game's own catalog";
        if (!builtIn && string.Equals(pack, Forge, StringComparison.OrdinalIgnoreCase)) return "'Forge' is this library's own namespace";
        return null;
    }

    /// <summary>Null when <paramref name="item"/> is a usable item name, else why not.</summary>
    internal static string? CheckItem(string? item)
    {
        if (string.IsNullOrEmpty(item)) return "the item name is empty";
        foreach (var c in item)
            if (!IsNameChar(c)) return "the item name '" + item + "' may only hold letters, digits, '_', '-' and '.'";
        return null;
    }

    internal static string Join(string pack, string item) => pack + ":" + item;

    /// <summary>Splits <c>Pack:item</c>; false (with the reason) when it is not one.</summary>
    internal static bool TrySplit(string? name, out string pack, out string item, out string? why)
    {
        pack = item = "";
        why = null;
        if (string.IsNullOrEmpty(name)) { why = "the name is empty"; return false; }
        var colon = name.IndexOf(':');
        if (colon <= 0 || colon == name.Length - 1 || name.IndexOf(':', colon + 1) >= 0) { why = "'" + name + "' is not Pack:item"; return false; }
        pack = name.Substring(0, colon);
        item = name.Substring(colon + 1);
        why = CheckItem(item);
        foreach (var c in pack)
            if (!IsNameChar(c) || c == '.') why ??= "the pack name '" + pack + "' may only hold letters, digits, '_' and '-'";
        return why == null;
    }

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.';
}

/// <summary>One kind of named visual (models, sprite sheets, effects, particle pools). A name is resolved once into its handle;
/// afterwards nothing is looked up by name.</summary>
internal sealed class NameTable<T> where T : class
{
    private readonly Dictionary<string, T> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();

    internal int Count => _byName.Count;
    internal IReadOnlyList<string> Names => _names;

    /// <summary>Null when added, else why not (a bad name or one already taken: the first registration keeps it).</summary>
    internal string? Add(string name, T value)
    {
        if (!VisualNames.TrySplit(name, out _, out _, out var why)) return why;
        if (_byName.ContainsKey(name)) return "'" + name + "' is already registered";
        _byName.Add(name, value);
        _names.Add(name);
        return null;
    }

    internal bool TryGet(string? name, out T value)
    {
        value = null!;
        return name != null && _byName.TryGetValue(name, out value!);
    }

    internal bool Remove(string name)
    {
        if (!_byName.Remove(name)) return false;
        _names.Remove(name);
        return true;
    }
}

/// <summary>How a named lookup ended, and why when it did not find anything.</summary>
internal static class Missing
{
    private static readonly HashSet<(string Kind, string? Name)> Reported = new();

    /// <summary>True the first time a lookup of <paramref name="name"/> as <paramref name="kind"/> failed, so the reason (a string build) is made once, not on every miss.</summary>
    internal static bool First(string kind, string? name) => Reported.Add((kind, name));

    /// <summary>The reason a name was not found: not a name at all, a pack that is not installed, or an item its pack lacks.</summary>
    internal static string Why(string? name, Func<string, bool> packKnown, string kind)
    {
        if (!VisualNames.TrySplit(name, out var pack, out _, out var why)) return why!;
        return packKnown(pack) ? "pack " + pack + " has no " + kind + " '" + name + "'" : "no pack " + pack + " is registered (is the mod that owns it installed?)";
    }
}
