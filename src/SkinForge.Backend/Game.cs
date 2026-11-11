using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SkinForge.Backend;

public sealed record GameInfo(string? GameDir, string? DataDir, bool Found, bool Running);
public sealed record ProfileInfo(string Name, string Path, bool HasBepInEx, string? PluginVersion);

// Finds the GTFO install (saved choice, then Steam registry + libraryfolders.vdf + common dirs) and BepInEx profiles.
public static class Game
{
    public const string PluginFolder = "SkinForge";

    static string? _chosen = ReadSetting();

    public static bool Running => Process.GetProcessesByName("GTFO").Length > 0;

    public static GameInfo Locate()
    {
        var dir = Valid(_chosen) ? _chosen : Candidates().FirstOrDefault(Valid);
        return new GameInfo(dir, dir == null ? null : Path.Combine(dir, "GTFO_Data"), dir != null, Running);
    }

    public static GameInfo SetDir(string dir)
    {
        dir = Path.GetFullPath(dir);
        if (Path.GetFileName(dir).Equals("GTFO_Data", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
        if (!Valid(dir)) throw new BackendException("game-not-found", $"{dir} is not a GTFO install (GTFO.exe and GTFO_Data expected)");
        _chosen = dir;
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(new { gameDir = dir }));
        return Locate();
    }

    public static string Dir => Locate().GameDir ?? throw new BackendException("game-not-found", "GTFO install not found; choose its folder");

    static bool Valid(string? dir) =>
        dir != null && File.Exists(Path.Combine(dir, "GTFO.exe")) && File.Exists(Path.Combine(dir, "GTFO_Data", "resources.assets"));

    static string? ReadSetting()
    {
        try { return JsonDocument.Parse(File.ReadAllText(Paths.Settings)).RootElement.GetProperty("gameDir").GetString(); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return null; }
    }

    static IEnumerable<string> Candidates()
    {
        var steamRoots = new List<string>();
        foreach (var (hive, key) in new[] {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam") })
        {
            using var k = hive.OpenSubKey(key);
            foreach (var name in new[] { "SteamPath", "InstallPath" })
                if (k?.GetValue(name) is string v && v.Length > 0) steamRoots.Add(v);
        }
        steamRoots.AddRange(new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" });
        foreach (var drive in "CDEFGH") steamRoots.AddRange(new[] { $@"{drive}:\Steam", $@"{drive}:\SteamLibrary" });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            var libraries = new List<string> { root };
            foreach (var vdf in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
                if (File.Exists(vdf))
                    libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)
                        .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
            foreach (var lib in libraries)
            {
                var dir = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", "GTFO"));
                if (seen.Add(dir)) yield return dir;
            }
        }
    }

    // r2modman and Thunderstore Mod Manager profiles of GTFO, plus the game dir itself.
    public static List<ProfileInfo> Profiles()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<ProfileInfo>();
        foreach (var root in new[] {
            Path.Combine(appData, "r2modmanPlus-local", "GTFO", "profiles"),
            Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder", "GTFO", "profiles") })
            if (Directory.Exists(root))
                list.AddRange(Directory.GetDirectories(root).Select(Profile));
        if (Locate().GameDir is { } game) list.Add(Profile(game) with { Name = "GTFO (game folder)" });
        return list;
    }

    static ProfileInfo Profile(string dir)
    {
        var dll = Path.Combine(PluginDir(dir), "SkinForge.Plugin.dll");
        return new ProfileInfo(Path.GetFileName(dir), dir, Directory.Exists(Path.Combine(dir, "BepInEx", "core")),
            File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).FileVersion : null);
    }

    public static string PluginDir(string profile) => Path.Combine(profile, "BepInEx", "plugins", PluginFolder);
}
