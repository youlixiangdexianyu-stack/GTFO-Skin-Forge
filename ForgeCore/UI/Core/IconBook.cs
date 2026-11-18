using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>One picture of the game's own icon catalog: <see cref="Item"/> is the game's sprite name, called as <c>Game:Item</c>.</summary>
public sealed record GameIcon(string Item, string Summary)
{
    /// <summary>The full name, <c>Game:Item</c>.</summary>
    public string Name => IconNames.Join(IconNames.Game, Item);
}

/// <summary>
/// The game's icons by name (<c>Game:symbol_hostile</c>). They are in every player's game already and are taken from it at run time
/// (the loaded <c>Sprite</c> of that name); nothing is redistributed. Only sprites whose names were read from the game and are in
/// use are listed; the website manifest is built from this table.
/// </summary>
public static class GameIcons
{
    /// <summary>The catalog, in the order it is listed.</summary>
    public static readonly IReadOnlyList<GameIcon> All = new[]
    {
        new GameIcon("symbol_hostile", "The hollow triangle on an enemy's tag marker."),
        new GameIcon("cellUI_SmallTriangleFill", "A small filled triangle."),
        new GameIcon("CircleBigMiddleSize_Fill", "A filled circle."),
        new GameIcon("GUIX_Detail_BoxFilled_AA_Centered", "A filled square."),
    };

    /// <summary>True when <paramref name="item"/> is in the catalog.</summary>
    public static bool Has(string item)
    {
        foreach (var icon in All)
            if (string.Equals(icon.Item, item, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>Icon names: <c>Pack:item</c>. <c>Game:</c> is the game's own catalog (<see cref="GameIcons"/>); no mod registers under it.</summary>
internal static class IconNames
{
    /// <summary>The reserved pack of the game's own icons.</summary>
    public const string Game = "Game";

    /// <summary>Null when <paramref name="pack"/> can be a pack name (<see cref="ForgeCore.PackNames"/>) and is not <c>Game</c>, else why not.</summary>
    public static string? CheckPack(string? pack)
        => ForgeCore.PackNames.CheckPack(pack)
            ?? (string.Equals(pack, Game, StringComparison.OrdinalIgnoreCase) ? "'Game' is the game's own icon catalog" : null);

    /// <summary><c>pack:item</c>.</summary>
    public static string Join(string pack, string item) => pack + ":" + item;
}

/// <summary>One registered icon, for listings.</summary>
public sealed record IconEntry(string Name, string Pack, string Owner, string Path);

/// <summary>
/// Every pack's registered icons and the game's catalog, by <c>Pack:item</c> name, without Unity: pictures come in through the
/// loader and the game resolver the caller gives. A pack belongs to the first mod that claims it. Every refusal (a bad or taken name, a
/// pack someone else owns, a file that cannot be read, a name nobody registered, a pack whose mod is not installed, a game sprite
/// that is not loaded) is reported through <c>report</c>, each distinct message once, and never replaced by another picture.
/// </summary>
internal sealed class IconBook<T> where T : class
{
    private readonly Func<string, T?> _load;
    private readonly Func<string, T?> _game;
    private readonly Action<string> _report;
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, T> _icons = new(StringComparer.Ordinal);
    private readonly List<IconEntry> _entries = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <param name="load">Reads the picture at a path; null when it cannot (the loader may log its own detail).</param>
    /// <param name="game">The game's picture for a catalog item; null when the game has not loaded it.</param>
    /// <param name="report">Receives each distinct failure message once.</param>
    public IconBook(Func<string, T?> load, Func<string, T?> game, Action<string> report)
    {
        _load = load;
        _game = game;
        _report = report;
    }

    /// <summary>The registered icons, in registration order.</summary>
    public IReadOnlyList<IconEntry> Entries => _entries;

    /// <summary>Makes <paramref name="pack"/> <paramref name="owner"/>'s; false (reported) when the name is unusable or another mod has it.</summary>
    public bool ClaimPack(string pack, string owner)
    {
        var problem = IconNames.CheckPack(pack);
        if (problem != null) return Fail("Icon pack not claimed by " + owner + ": " + problem + ".");
        if (_owners.TryGetValue(pack, out var current))
            return current == owner || Fail("Icon pack '" + pack + "' is already owned by " + current + "; " + owner + " cannot use it.");
        _owners.Add(pack, owner);
        return true;
    }

    /// <summary>Registers the picture at <paramref name="path"/> as <c>pack:item</c> of a pack <paramref name="owner"/> has claimed; false
    /// (reported) for a bad name, a pack that is not the owner's, a name already taken or a file that cannot be read.</summary>
    public bool Register(string owner, string pack, string item, string path)
    {
        var problem = ForgeCore.PackNames.CheckItem(item, "icon");
        if (problem != null) return Fail("Icon not registered in '" + pack + "': " + problem + ".");
        var name = IconNames.Join(pack, item);
        if (!_owners.TryGetValue(pack, out var current) || current != owner) return Fail("Icon '" + name + "' not registered: " + owner + " does not own pack '" + pack + "'.");
        if (_icons.ContainsKey(name)) return Fail("Icon '" + name + "' is already registered.");
        var picture = _load(path);
        if (picture == null) return Fail("Icon '" + name + "' not registered: " + path + " could not be loaded.");
        _icons.Add(name, picture);
        _entries.Add(new IconEntry(name, pack, owner, path));
        return true;
    }

    /// <summary>The picture called <paramref name="name"/> (<c>Pack:item</c>, or <c>Game:item</c> from the catalog); null with the reason
    /// reported once when there is none.</summary>
    public T? Find(string? name)
    {
        if (!ForgeCore.PackNames.TrySplit(name, "icon", out var pack, out var item, out var why)) { Fail("Icon not found: " + why + "."); return null; }
        if (string.Equals(pack, IconNames.Game, StringComparison.Ordinal))
        {
            if (!GameIcons.Has(item)) { Fail("Icon '" + name + "' is not in the game icon catalog."); return null; }
            var sprite = _game(item);
            if (sprite == null) Fail("Icon '" + name + "': the game has no loaded picture of that name.");
            return sprite;
        }
        if (_icons.TryGetValue(name!, out var picture)) return picture;
        Fail(_owners.ContainsKey(pack)
            ? "Icon '" + name + "' is not registered: pack '" + pack + "' has no such icon."
            : "Icon '" + name + "' is not registered: no pack '" + pack + "' is loaded (is the mod that owns it installed?).");
        return null;
    }

    private bool Fail(string message)
    {
        if (_reported.Add(message)) _report(message);
        return false;
    }
}
