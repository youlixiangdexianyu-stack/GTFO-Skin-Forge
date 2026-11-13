using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeUI;

/// <summary>The registered entries of one kind (lobby buttons, menu bar buttons), kept in the fixed order they are laid out in:
/// by ID, ordinal. Two mods that register buttons therefore get the same order on every machine, whatever their load order.</summary>
internal sealed class Registry<T> where T : class
{
    private readonly SortedDictionary<string, T> _entries = new(StringComparer.Ordinal);

    /// <summary>The entries in layout order.</summary>
    internal IReadOnlyList<T> Ordered { get; private set; } = Array.Empty<T>();

    /// <summary>Adds <paramref name="entry"/> under <paramref name="id"/>; returns why it was refused, or null.</summary>
    internal string? Add(string id, T entry)
    {
        var problem = Validate(id);
        if (problem != null) return problem;
        if (_entries.ContainsKey(id)) return "the ID '" + id + "' is already registered";
        _entries.Add(id, entry);
        Ordered = _entries.Values.ToArray();
        return null;
    }

    internal bool Remove(string id, T entry)
    {
        if (!Contains(id, entry)) return false;
        _entries.Remove(id);
        Ordered = _entries.Values.ToArray();
        return true;
    }

    internal bool Contains(string id, T entry) => _entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry);

    internal int IndexOf(string id)
    {
        var index = 0;
        foreach (var key in _entries.Keys)
        {
            if (key == id) return index;
            index++;
        }
        return -1;
    }

    /// <summary>IDs are non-empty and printable without spaces, so they read well in logs; <c>Author.Mod.Button</c> is the
    /// suggested form.</summary>
    internal static string? Validate(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "the ID is empty";
        if (id.Length > 128) return "the ID '" + id + "' is longer than 128 characters";
        foreach (var c in id)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return "the ID '" + id + "' contains a space or a control character";
        return null;
    }
}

/// <summary>Namespaced IDs: every registration is <c>mod/element</c>, the mod's own ID (its plugin GUID, for example
/// <c>NAinfini.ForgeOrdnance</c>) and the element's name inside it, so two mods can never take each other's names.</summary>
internal static class ModIds
{
    public const char Separator = '/';

    /// <summary>Why <paramref name="part"/> cannot be a mod ID or an element name, or null: non-empty, at most 64 characters, no
    /// space, control character or <see cref="Separator"/>.</summary>
    public static string? Problem(string? part, string what)
    {
        if (string.IsNullOrEmpty(part)) return "the " + what + " is empty";
        if (part.Length > 64) return "the " + what + " '" + part + "' is longer than 64 characters";
        foreach (var c in part)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == Separator) return "the " + what + " '" + part + "' contains a space, a control character or '/'";
        return null;
    }

    public static string Join(string mod, string name) => mod + Separator + name;

    /// <summary>The mod and element parts of a namespaced ID.</summary>
    public static (string Mod, string Name) Split(string id)
    {
        var at = id.IndexOf(Separator);
        return at < 0 ? ("", id) : (id.Substring(0, at), id.Substring(at + 1));
    }
}

/// <summary>Where lobby buttons go: one under another, starting one step below the game's APPAREL button.</summary>
internal static class LobbyLayout
{
    /// <summary>The APPAREL box is 55 units tall; each button sits one box and an 18-unit gap below the one above.</summary>
    internal const float Step = 73f;

    /// <summary>Local offset from the APPAREL button of the button at <paramref name="index"/> in layout order, shifted by the
    /// player's offset setting.</summary>
    internal static (float X, float Y) Offset(int index, float offsetX, float offsetY) => (offsetX, -Step * (index + 1) + offsetY);
}
