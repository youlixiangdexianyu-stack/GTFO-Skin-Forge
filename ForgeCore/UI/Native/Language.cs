using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace ForgeUI;

/// <summary>
/// The current language: the player's <c>Language / Language</c> setting, or with <c>Auto</c> the game's own language
/// (<c>Localization.Text.TextLocalizationService.CurrentLanguage</c>), re-read when the game sets its language
/// (<see cref="ForgeCore.GameEvents.LanguageChanged"/>) and when the setting changes. Until the game's text service exists at start-up
/// the read is tried four times a second, then never again. A change is applied to every mod at once (<see cref="UITexts.Version"/>), and
/// what is already shown is set again (<see cref="UIKit.LanguageSwitched"/>).
/// </summary>
internal static class Language
{
    internal const string Auto = "Auto";
    private const float GameInterval = 0.25f;
    private static ConfigEntry<string>? _setting;
    private static float _nextGameRead;
    private static bool _gameUnreadable;
    private static bool _waitingForGame;

    internal static void Bind()
    {
        _setting = ForgeCore.Settings.Bind(Plugin.Id, "Language", "Language", Auto,
            "The language of the texts mods show through ForgeUI: Auto follows the game's language; or a language code such as zh or en. A language a mod has no file for shows its English texts.");
        _setting.SettingChanged += (_, _) => Apply();
        ForgeCore.GameEvents.LanguageChanged += Apply;
        UITexts.Changed += UIKit.LanguageSwitched;
        _waitingForGame = true;
    }

    /// <summary>Per frame: only while the game's text service has not answered yet (start-up); then it does nothing.</summary>
    internal static void Update()
    {
        if (!_waitingForGame) return;
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _nextGameRead) return;
        _nextGameRead = now + GameInterval;
        Apply();
    }

    private static void Apply()
    {
        if (_setting == null) return;
        _waitingForGame = false;
        var chosen = UITexts.Normalize(_setting.Value);
        string? code;
        if (chosen.Length != 0 && chosen != "auto")
        {
            code = chosen;
        }
        else
        {
            if (!TryGameLanguage(out var name))
            {
                _waitingForGame = !_gameUnreadable;
                return;
            }
            code = UITexts.GameLanguageCode(name);
            if (code == null) return;
        }
        UITexts.SetLanguage(code);
    }
    /// <summary>The game's language, once its text service exists (it is set up with the game data).</summary>
    private static bool TryGameLanguage(out string name)
    {
        name = "";
        try
        {
            var service = Localization.Text.TextLocalizationService;
            if (service == null) return false;
            name = service.CurrentLanguage.ToString();
            return true;
        }
        catch (Exception error)
        {
            if (!_gameUnreadable) Plugin.Error("ui-games-language-could-not-be-read", "The game's language could not be read; texts stay " + UITexts.Language + ": " + error);
            _gameUnreadable = true;
            return false;
        }
    }

    /// <summary>The language files in <c>lang</c> beside the plugin <paramref name="modId"/>'s DLL (the kit's own: beside ForgeCore.dll).</summary>
    internal static UITexts ForPlugin(string modId)
    {
        string? folder = null;
        if (modId == Plugin.Id)
            folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "lang");
        else if (IL2CPPChainloader.Instance?.Plugins.TryGetValue(modId, out var info) == true && !string.IsNullOrEmpty(info.Location))
            folder = Path.Combine(Path.GetDirectoryName(info.Location)!, "lang");
        if (folder == null)
            return new UITexts(modId, () =>
            {
                Plugin.Error("ui-no-loaded-plugin-has-this-id", modId + ": no loaded plugin has this ID, so its language files cannot be found; texts show their keys. Pass your plugin GUID to UIKit.Mod.");
                return Array.Empty<KeyValuePair<string, string>>();
            }, message => Plugin.Error("ui-language-file-problem", message));
        return InFolder(modId + " (" + Shown(folder) + ")", folder);
    }

    /// <summary>The language files in <paramref name="folder"/>.</summary>
    internal static UITexts InFolder(string owner, string folder) => new(owner, () =>
    {
        var files = new List<KeyValuePair<string, string>>();
        if (!Directory.Exists(folder)) return files;
        var paths = Directory.GetFiles(folder, "*.forge");
        Array.Sort(paths, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            try { files.Add(new KeyValuePair<string, string>(Path.GetFileName(path), File.ReadAllText(path))); }
            catch (Exception error) { Plugin.Error("ui-could-not-be-read", owner + ": " + Path.GetFileName(path) + " could not be read: " + error); }
        }
        return files;
    }, message => Plugin.Error("ui-language-file-problem", message));

    /// <summary>A path relative to the BepInEx folder when inside it.</summary>
    private static string Shown(string path)
    {
        var relative = Path.GetRelativePath(BepInEx.Paths.BepInExRootPath, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : ForgeCore.PosixPath.ToPosix(relative);
    }
}
