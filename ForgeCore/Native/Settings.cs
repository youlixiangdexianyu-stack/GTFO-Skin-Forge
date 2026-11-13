using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace ForgeCore;

/// <summary>Configuration by mod ID: one file per mod, <c>BepInEx/config/&lt;mod ID&gt;.cfg</c> (the file BepInEx gives the plugin
/// itself, so a mod either <see cref="Adopt"/>s its own <c>Config</c> or lets ForgeCore open the file).</summary>
public static class Settings
{
    private static readonly Dictionary<string, ConfigFile> Files = new(StringComparer.Ordinal);

    /// <summary>A plugin's own <c>Config</c> (already <c>BepInEx/config/&lt;GUID&gt;.cfg</c>) becomes its file here, so the same file is
    /// never open twice. Call it before the first <see cref="For"/> or <see cref="Bind{T}"/> of that mod.</summary>
    public static void Adopt(string modId, ConfigFile pluginConfig)
    {
        if (pluginConfig == null) throw new ArgumentNullException(nameof(pluginConfig));
        var expected = Path.GetFullPath(PathOf(modId));
        if (!string.Equals(Path.GetFullPath(pluginConfig.ConfigFilePath), expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configuration file of " + modId + " must be " + expected + ".", nameof(pluginConfig));
        lock (Files)
        {
            if (Files.TryGetValue(modId, out var open) && !ReferenceEquals(open, pluginConfig))
                throw new InvalidOperationException("The configuration file of " + modId + " is already open.");
            Files[modId] = pluginConfig;
        }
    }

    /// <summary>The mod's configuration file.</summary>
    public static ConfigFile For(string modId)
    {
        var path = PathOf(modId);
        lock (Files)
        {
            if (!Files.TryGetValue(modId, out var file))
                Files[modId] = file = new ConfigFile(path, true);
            return file;
        }
    }

    private static string PathOf(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A mod ID is a plugin GUID usable as a file name.", nameof(modId));
        return Path.Combine(Paths.ConfigPath, modId + ".cfg");
    }

    /// <summary>Binds one entry; <paramref name="changed"/> runs with the new value whenever it changes while the game runs (an
    /// edit of the file or a configuration manager). A failing handler is logged under the mod's name.</summary>
    public static ConfigEntry<T> Bind<T>(string modId, string section, string key, T value, string description, Action<T>? changed = null)
    {
        var entry = For(modId).Bind(section, key, value, description);
        if (changed != null)
            entry.SettingChanged += (_, _) =>
            {
                try { changed(entry.Value); }
                catch (Exception error) { Log.For(modId).Error("core-setting-change-handler-failed", "Setting " + section + "/" + key + " change handler failed: " + error); }
            };
        return entry;
    }

    /// <summary>Binds several entries and saves the file once at the end instead of after each one.</summary>
    public static void Batch(string modId, Action<ConfigFile> bind)
    {
        if (bind == null) throw new ArgumentNullException(nameof(bind));
        var file = For(modId);
        var save = file.SaveOnConfigSet;
        file.SaveOnConfigSet = false;
        try { bind(file); }
        finally { file.SaveOnConfigSet = save; }
        if (save) file.Save();
    }
}
