using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Unity.IL2CPP;

namespace ForgeCore;

/// <summary>One loaded BepInEx plugin: its GUID, version and the SHA-256 of its DLL (empty when the file cannot be read).</summary>
public readonly record struct PluginPrint(string Guid, string Version, string DllHash);

/// <summary>The content one installed package ships: the package's directory name and the fingerprint of its <c>forge</c> folder.</summary>
public readonly record struct ContentPrint(string Package, string Hash);

/// <summary>The BepInEx plugins this process loaded.</summary>
public static class InstalledPlugins
{
    /// <summary>Every loaded plugin, ordered by GUID.</summary>
    public static List<PluginPrint> List()
    {
        var plugins = new List<PluginPrint>();
        foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
        {
            var hash = "";
            try
            {
                using var stream = File.OpenRead(info.Location);
                hash = Hash.Sha256Hex(stream);
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-plugin-could-not-be-hashed-dll", "Plugin " + info.Metadata.GUID + " could not be hashed (" + info.Location + "); its DLL hash is empty: " + error); }
            plugins.Add(new PluginPrint(info.Metadata.GUID, info.Metadata.Version.ToString(), hash));
        }
        plugins.Sort((a, b) => string.CompareOrdinal(a.Guid, b.Guid));
        return plugins;
    }

    /// <summary>The content every installed package ships: one entry per directory of BepInEx's plugins folder that has a <c>forge</c>
    /// folder (content packs, models, sounds), named by the directory, with <see cref="ForgeCore.Fingerprint.OfFolder"/> of that folder; ordered by name.
    /// Reads the files, so call it off the game's frame.</summary>
    public static List<ContentPrint> ContentPrints()
    {
        var prints = new List<ContentPrint>();
        string[] packages;
        try { packages = Directory.GetDirectories(BepInEx.Paths.PluginPath); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-plugins-folder-could-not-be-listed", "The plugins folder could not be listed for content fingerprints: " + error.Message); return prints; }
        Array.Sort(packages, StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var forge = Path.Combine(package, "forge");
            if (!Directory.Exists(forge)) continue;
            try { prints.Add(new ContentPrint(Path.GetFileName(package), ForgeCore.Fingerprint.OfFolder(forge))); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-content-could-not-be-fingerprinted", "The content of " + package + " could not be fingerprinted: " + error.Message); }
        }
        return prints;
    }

    /// <summary>One hash over <see cref="List"/>: equal exactly when the same plugins of the same versions and DLL contents are loaded.</summary>
    public static string Fingerprint()
    {
        var fingerprint = new ForgeCore.Fingerprint();
        foreach (var plugin in List()) fingerprint.Add(plugin.Guid, plugin.Version + "|" + plugin.DllHash);
        return fingerprint.Hex();
    }
}
