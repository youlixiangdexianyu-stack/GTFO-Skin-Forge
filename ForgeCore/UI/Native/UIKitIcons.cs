using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeUI;

/// <summary>The icon names of every pack and the game's catalog; the rules and the failure messages are in <see cref="IconBook{T}"/>.</summary>
internal static class UIKitIcons
{
    internal static readonly IconBook<Sprite> Book = new(UIKit.LoadIcon, UIKit.GameSprite, message => Plugin.Error("ui-icon-book-problem", message));
}
public static partial class UIKit
{
    /// <summary>How long a game picture that was not found is not searched for again.</summary>
    private const float GameRetrySeconds = 2f;
    private static readonly Dictionary<string, Sprite> GameFound = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> GameMissed = new(StringComparer.Ordinal);

    /// <summary>The loaded <c>Sprite</c> of the game with this name (<c>symbol_hostile</c>), found among the loaded sprites once and kept;
    /// null while it is not loaded, and a miss is searched again after two seconds (the level may not have loaded it yet).</summary>
    public static Sprite? GameSprite(string name)
    {
        if (GameFound.TryGetValue(name, out var known) && known != null) return known;
        if (GameMissed.TryGetValue(name, out var until) && ForgeCore.Frame.UnscaledTime < until) return null;
        foreach (var asset in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>())) // Resources.FindObjectsOfTypeAll(Type)
        {
            var sprite = asset?.TryCast<Sprite>();
            if (sprite == null || sprite.name != name) continue;
            GameFound[name] = sprite;
            GameMissed.Remove(name);
            return sprite;
        }
        GameMissed[name] = ForgeCore.Frame.UnscaledTime + GameRetrySeconds;
        return null;
    }

    /// <summary>The icon called <paramref name="name"/>: <c>Pack:item</c> for one a mod registered (<c>Frames:skill-charge</c>) or
    /// <c>Game:item</c> for one of the game's own (<see cref="GameIcons"/>). Null, with the reason logged once, when the name is not a name,
    /// no such pack is loaded (its mod is not installed), the pack has no such icon or the game has not loaded that picture. Never another
    /// icon in its place. Look it up once and keep it; a game icon is searched for among the loaded sprites.</summary>
    public static Sprite? Icon(string name) => UIKitIcons.Book.Find(name);
}

/// <summary>A mod's icon pack: the icons it registers are named <c>Pack:item</c>. Take it with <see cref="UIKitMod.Icons"/>.</summary>
public sealed class IconPack
{
    private readonly UIKitMod _owner;

    internal IconPack(UIKitMod owner, string name)
    {
        _owner = owner;
        Name = name;
    }

    /// <summary>The pack name, the part before the colon.</summary>
    public string Name { get; }

    /// <summary>Registers the PNG at <paramref name="pngPath"/> as <c>Pack:name</c>; false (logged) when the name cannot be used, is
    /// taken or the file cannot be read.</summary>
    public bool Register(string name, string pngPath)
    {
        if (string.IsNullOrEmpty(pngPath)) throw new ArgumentException("An icon needs a file path.", nameof(pngPath));
        return UIKitIcons.Book.Register(_owner.Id, Name, name, pngPath);
    }

    /// <summary>Registers many icons from <paramref name="folder"/> at once: each of <paramref name="names"/> is a file name without
    /// <c>.png</c> (registered under that name) or a prefix ending in <c>*</c> (<c>mark-*</c>: every PNG there starting so). The
    /// <c>@2x</c> file is read on screens taller than 1440 lines, else the plain one (either when only one exists). Names without a
    /// file and files that cannot be read are logged (one line for the folder); read the results with <see cref="Icon"/> (null for those).</summary>
    /// <example><c>pack.RegisterFolder(Path.Combine(dir, "hud"), "skill-charge", "skill-siege", "mark-*");</c></example>
    public void RegisterFolder(string folder, params string[] names)
    {
        if (string.IsNullOrEmpty(folder)) throw new ArgumentException("An icon folder needs a path.", nameof(folder));
        var files = new List<string>();
        if (Directory.Exists(folder))
            foreach (var path in Directory.GetFiles(folder, "*.png")) files.Add(Path.GetFileName(path));
        var wantDouble = IconFolder.UseDouble(ForgeCore.Frame.ScreenHeight);
        var failed = new List<string>();
        foreach (var name in IconFolder.Names(files, names ?? Array.Empty<string>()))
        {
            var file = IconFolder.Pick(files, name, wantDouble);
            if (file == null) { failed.Add(name); continue; }
            if (!Register(name, Path.Combine(folder, file))) failed.Add(name);
        }
        if (failed.Count > 0)
            Plugin.Error("ui-icon-folder-pack-not-registered", "Icon folder " + folder + " (pack " + Name + "): not registered " + string.Join(", ", failed) + ".");
    }

    /// <summary>This pack's icon <paramref name="name"/>, or null (logged once, see <see cref="UIKit.Icon"/>).</summary>
    public Sprite? Icon(string name) => UIKit.Icon(IconNames.Join(Name, name ?? ""));
}

public sealed partial class UIKitMod
{
    /// <summary>This mod's icon pack <paramref name="pack"/>: the icons registered through it are named <c>pack:name</c> and any mod can
    /// ask for them with <see cref="UIKit.Icon"/>. Pass the short name of your package (<c>Frames</c>), letters, digits, '_' and '-',
    /// not <c>Game</c>. Calling it again returns the same pack; null (logged) when another mod owns the name.</summary>
    /// <exception cref="ArgumentException">The name cannot be a pack name.</exception>
    public IconPack? Icons(string pack)
    {
        var problem = IconNames.CheckPack(pack);
        if (problem != null) throw new ArgumentException("ForgeUI: " + problem + ".", nameof(pack));
        return UIKitIcons.Book.ClaimPack(pack, Id) ? new IconPack(this, pack) : null;
    }
}
