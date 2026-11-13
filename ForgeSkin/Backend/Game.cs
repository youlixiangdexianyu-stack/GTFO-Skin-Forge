using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForgeSkin.Backend;

public sealed record GameInfo(string? GameDir, string? DataDir, bool Found, bool Running);
// Reason: why HasBepInEx is false. Modified: last time the profile changed or the game ran from it (BepInEx log).
// Development: the plugin's hot-reload switch in its config. PluginCurrent: Install.PluginCurrent (the shipped plugin DLL, nothing retired beside it, ForgeCore at least the shipped one).
public sealed record ProfileInfo(string Name, string Path, bool HasBepInEx, string? PluginVersion, bool PluginCurrent, bool Development, string? Reason, bool Custom, DateTime Modified);

// Finds the GTFO install (saved choice, then Steam registry + libraryfolders.vdf + common dirs) and BepInEx profiles.
public static class Game
{
    public const string PluginFolder = "ForgeSkin";

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
        var settings = ReadSettings();
        settings["gameDir"] = dir;
        WriteSettings(settings);
        return Locate();
    }

    public static string Dir => Locate().GameDir ?? throw new BackendException("game-not-found", "GTFO install not found; choose its folder");

    static bool Valid(string? dir) =>
        dir != null && File.Exists(Path.Combine(dir, "GTFO.exe")) && File.Exists(Path.Combine(dir, "GTFO_Data", "resources.assets"));

    static string? ReadSetting() => (string?)ReadSettings()["gameDir"];

    static JsonObject ReadSettings()
    {
        try { return JsonNode.Parse(File.ReadAllText(Paths.Settings)) as JsonObject ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    static void WriteSettings(JsonObject settings)
    {
        Directory.CreateDirectory(Paths.Data);
        AtomicFile.WriteText(Paths.Settings, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    // Mod manager data folders: the defaults plus any custom `dataDirectory` r2modman or Thunderstore Mod Manager keep in
    // their settings (IndexedDB LevelDB files, searched as text), and Gale's data dir from its prefs.
    static IEnumerable<string> ProfileRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDirs = new List<string> { Path.Combine(appData, "r2modmanPlus-local"), Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder") };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
        foreach (var store in new[] { Path.Combine(appData, "r2modman"), Path.Combine(appData, "Thunderstore Mod Manager"), Path.Combine(local, "Overwolf"), Path.Combine(appData, "Overwolf") }
                     .Where(Directory.Exists))
            foreach (var db in Directory.EnumerateDirectories(store, "*indexeddb.leveldb", options))
                foreach (var file in Directory.EnumerateFiles(db).Where(f => f.EndsWith(".ldb") || f.EndsWith(".log")))
                {
                    string text;
                    try { text = File.ReadAllText(file, System.Text.Encoding.Latin1); }
                    catch (IOException) { continue; }
                    foreach (Match m in Regex.Matches(text, @"""dataDirectory"":""((?:[^""\\]|\\.)+)"""))
                        dataDirs.Add(Regex.Unescape(m.Groups[1].Value));
                }
        var gale = Path.Combine(appData, "com.kesomannen.gale");
        yield return Path.Combine(gale, "gtfo", "profiles");
        if (File.Exists(Path.Combine(gale, "prefs.json"))
            && Regex.Match(File.ReadAllText(Path.Combine(gale, "prefs.json")), @"""data_?[dD]ir""\s*:\s*""((?:[^""\\]|\\.)+)""") is { Success: true } g)
            yield return Path.Combine(Regex.Unescape(g.Groups[1].Value), "gtfo", "profiles");
        foreach (var dir in dataDirs) yield return Path.Combine(dir, "GTFO", "profiles");
    }

    // Mod manager profiles of GTFO, the game folder and the profiles the user added.
    public static List<ProfileInfo> Profiles()
    {
        var list = ProfileRoots().Where(Directory.Exists).SelectMany(Directory.GetDirectories).Select(d => Profile(d, false)).ToList();
        if (Locate().GameDir is { } game) list.Add(Profile(game, false) with { Name = "GTFO (game folder)" });
        foreach (var path in CustomProfiles()) list.Add(Profile(path, true));
        return list.DistinctBy(p => Path.GetFullPath(p.Path).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<string> CustomProfiles() => ReadSettings()["profiles"] is JsonArray a ? a.Select(n => (string)n!).ToList() : new();

    // Why a folder is not a BepInEx 6 profile, or null when it is.
    static string? NotProfile(string dir)
    {
        var core = Path.Combine(dir, "BepInEx", "core");
        if (!Directory.Exists(Path.Combine(dir, "BepInEx"))) return "BepInEx folder missing";
        if (!Directory.Exists(core)) return "BepInEx/core missing";
        if (!File.Exists(Path.Combine(core, "BepInEx.Core.dll")) && !File.Exists(Path.Combine(core, "BepInEx.Unity.IL2CPP.dll")))
            return "BepInEx/core has neither BepInEx.Core.dll nor BepInEx.Unity.IL2CPP.dll";
        return null;
    }

    // major.minor.build: the file version always carries a fourth part (1.0.0.0), the plugin is versioned 1.0.0.
    static string ShortVersion(FileVersionInfo v) => $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";

    static ProfileInfo Profile(string dir, bool custom)
    {
        var dll = Install.InstalledPlugin(dir);
        var reason = NotProfile(dir);
        var modified = new[] { dir, Path.Combine(dir, "BepInEx"), Path.Combine(dir, "BepInEx", "plugins"), Path.Combine(dir, "BepInEx", "LogOutput.log") }
            .Where(p => Directory.Exists(p) || File.Exists(p)).Select(File.GetLastWriteTimeUtc).Max();
        return new ProfileInfo(Path.GetFileName(dir.TrimEnd('\\', '/')), dir, reason == null,
            dll != null ? ShortVersion(FileVersionInfo.GetVersionInfo(dll)) : null, dll != null && Install.PluginCurrent(dir), Install.Development(dir), reason, custom, modified);
    }

    // Accepts a profile root, its BepInEx folder or a game folder holding BepInEx.
    public static ProfileInfo AddProfile(string path)
    {
        var dir = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (Path.GetFileName(dir).Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
        if (!Directory.Exists(dir)) throw new BackendException("not-a-profile", $"{dir} does not exist");
        if (NotProfile(dir) is { } reason) throw new BackendException("not-a-profile", $"{dir}: {reason}");
        var settings = ReadSettings();
        var list = CustomProfiles();
        if (!list.Contains(dir, StringComparer.OrdinalIgnoreCase)) list.Add(dir);
        settings["profiles"] = new JsonArray(list.Select(p => (JsonNode)p).ToArray());
        WriteSettings(settings);
        return Profile(dir, true);
    }

    public static object RemoveProfile(string path)
    {
        var dir = Path.GetFullPath(path).TrimEnd('\\', '/');
        var list = CustomProfiles();
        if (list.RemoveAll(p => p.Equals(dir, StringComparison.OrdinalIgnoreCase)) == 0)
            throw new BackendException("not-found", $"{dir} is not an added profile");
        var settings = ReadSettings();
        settings["profiles"] = new JsonArray(list.Select(p => (JsonNode)p).ToArray());
        WriteSettings(settings);
        return new { removed = dir };
    }

    // Lines the plugin wrote to its own log from byte `from` on (0: the start) and after `since` (ISO time, UTC): {time, level,
    // message}, with `next`, where the following call goes on, and `head`, the start of the file's first line (its time names this
    // launch's log). A `head` the file no longer starts with (the game was started again and cleared the log) reads from the start:
    // `reset`. Only the bytes after `from` are read.
    public static object PluginLog(string profile, DateTime? since, long from, string? head)
    {
        var file = Path.Combine(PluginDir(profile), "forgeskin.log");
        if (!File.Exists(file)) return new { lines = Array.Empty<object>(), next = 0L, head = "", reset = from > 0 };
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = s.Length;
        var start = new byte[Math.Min(length, 64)];
        s.ReadExactly(start);
        var startEnd = Array.IndexOf(start, (byte)'\n');
        var first = System.Text.Encoding.UTF8.GetString(start, 0, startEnd < 0 ? start.Length : startEnd).TrimEnd('\r');
        var reset = from > 0 && (from > length || first != head);
        if (reset || from < 0) from = 0;
        var bytes = new byte[length - from];
        s.Position = from;
        s.ReadExactly(bytes);
        // Only whole lines: one the plugin is still writing is read by the next call.
        var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var lines = new List<object>();
        foreach (var raw in System.Text.Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split(' ', 3);
            if (parts.Length < 3 || !DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) continue;
            if (since == null || t.ToUniversalTime() > since.Value.ToUniversalTime()) lines.Add(new { time = parts[0], level = parts[1], message = parts[2] });
        }
        return new { lines, next = from + end, head = first, reset };
    }

    public static string PluginDir(string profile) => Path.Combine(profile, "BepInEx", "plugins", PluginFolder);
}
