using System.Text.Json;
using System.Text.Json.Nodes;
using SkinForge.Format;

namespace SkinForge.Backend;

public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    static readonly string[] PluginFiles = ["SkinForge.Plugin.dll", "SkinForge.Format.dll", "NVorbis.dll"];

    static string Skins(string profile) => Path.Combine(Game.PluginDir(profile), "skins");
    static string ActiveFile(string profile) => Path.Combine(Skins(profile), "active.json");

    static JsonObject ReadActive(string profile)
    {
        var path = ActiveFile(profile);
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        json ??= new JsonObject();
        if (json["targets"] is not JsonObject) json["targets"] = new JsonObject();
        if (json["global"] is not JsonArray) json["global"] = new JsonArray();
        return json;
    }

    static void WriteActive(string profile, JsonObject json)
    {
        var path = ActiveFile(profile);
        File.WriteAllText(path + ".tmp", json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    static void SetActive(string profile, SkinDocument doc, bool active)
    {
        var json = ReadActive(profile);
        var targets = json["targets"]!.AsObject();
        var global = json["global"]!.AsArray();
        foreach (var key in targets.Where(kv => (string?)kv.Value == doc.Id).Select(kv => kv.Key).ToList()) targets.Remove(key);
        foreach (var node in global.Where(n => (string?)n == doc.Id).ToList()) global.Remove(node);
        if (active)
        {
            if (doc.Target.Key() is { } key) targets[key] = doc.Id;
            else global.Add(doc.Id);
        }
        WriteActive(profile, json);
    }

    static void RequireProfile(string profile)
    {
        if (!Directory.Exists(Path.Combine(profile, "BepInEx"))) throw new BackendException("not-found", $"{profile} has no BepInEx folder");
    }

    public static object Pack(string pack, string profile, bool activate)
    {
        RequireProfile(profile);
        SkinDocument doc;
        using (var p = SkinPack.Open(pack)) doc = p.Manifest;
        var dir = Skins(profile);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Preview.Safe(doc.Id) + SkinPack.Extension);
        File.Copy(pack, target + ".tmp", true);
        File.Move(target + ".tmp", target, true);
        if (activate) SetActive(profile, doc, true);
        return new { installed = target, active = activate };
    }

    public static List<InstalledSkin> List(string profile)
    {
        var dir = Skins(profile);
        if (!Directory.Exists(dir)) return new();
        var json = ReadActive(profile);
        var active = json["targets"]!.AsObject().Select(kv => (string?)kv.Value).Concat(json["global"]!.AsArray().Select(n => (string?)n)).ToHashSet();
        return Directory.GetFiles(dir, "*" + SkinPack.Extension).Order().Select(file =>
        {
            try
            {
                using var p = SkinPack.Open(file);
                var d = p.Manifest;
                return new InstalledSkin(d.Id, d.Name, file, d.Target.Weapon, active.Contains(d.Id), null);
            }
            catch (Exception e) when (e is SkinFormatException or IOException or JsonException or InvalidDataException)
            {
                return new InstalledSkin(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), file, null, false,
                    e is SkinFormatException f ? $"{f.Code}: {f.Message}" : e.Message);
            }
        }).ToList();
    }

    public static object Remove(string profile, string skinId)
    {
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId) ?? throw new BackendException("not-found", $"Skin {skinId} is not installed");
        SetActive(profile, new SkinDocument { Id = skinId }, false);
        File.Delete(skin.File);
        return new { removed = skin.File };
    }

    public static object Activate(string profile, string skinId, bool active)
    {
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId && s.Error == null) ?? throw new BackendException("not-found", $"Skin {skinId} is not installed");
        using var p = SkinPack.Open(skin.File);
        SetActive(profile, p.Manifest, active);
        return new { id = skinId, active };
    }

    // The plugin build shipped next to the backend (plugin/); in a checkout the newest of the plugin project's Release or
    // Debug output and the committed prebuilt copy (repo plugin/, refreshed by every Release build).
    static string PluginSource()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "plugin");
        if (File.Exists(Path.Combine(bundled, PluginFiles[0]))) return bundled;
        var candidates = new List<string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "plugin"));
            foreach (var config in new[] { "Release", "Debug" })
                candidates.Add(Path.Combine(dir.FullName, "src", "SkinForge.Plugin", "bin", config, "net6.0"));
        }
        return candidates.Where(c => PluginFiles.All(f => File.Exists(Path.Combine(c, f))))
                   .OrderByDescending(c => File.GetLastWriteTimeUtc(Path.Combine(c, PluginFiles[0]))).FirstOrDefault()
               ?? throw new BackendException("no-plugin", "Plugin build not found: neither plugin/ next to the backend nor in the checkout");
    }

    public static bool PluginCurrent(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return false; }
        var dir = Game.PluginDir(profile);
        return PluginFiles.All(f => File.Exists(Path.Combine(dir, f)) && Hash(Path.Combine(dir, f)) == Hash(Path.Combine(source, f)));
        static string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }
    }

    // Refuses while GTFO runs and holds the DLL.
    static void RequireUnlocked(string dll)
    {
        if (!File.Exists(dll) || !Game.Running) return;
        try { using var _ = File.Open(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BackendException("game-running", "Close GTFO first: it has the plugin loaded"); }
    }

    public static object Plugin(string profile)
    {
        RequireProfile(profile);
        var source = PluginSource();
        var dir = Game.PluginDir(profile);
        Directory.CreateDirectory(Path.Combine(dir, "skins"));
        foreach (var f in PluginFiles) RequireUnlocked(Path.Combine(dir, f));
        foreach (var f in PluginFiles) File.Copy(Path.Combine(source, f), Path.Combine(dir, f), true);
        return new { installed = dir, version = Game.Profiles().FirstOrDefault(p => Path.GetFullPath(p.Path) == Path.GetFullPath(profile))?.PluginVersion };
    }

    // The plugin's development switch (hot reload), the `Development` entry of BepInEx/config/SkinForge.cfg. The plugin
    // reads it when the game starts, so a change applies from the next launch.
    static string ConfigFile(string profile) => Path.Combine(profile, "BepInEx", "config", "SkinForge.cfg");
    static readonly System.Text.RegularExpressions.Regex DevLine = new(@"^Development[ \t]*=[ \t]*(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
    static readonly System.Text.RegularExpressions.Regex GeneralLine = new(@"^\[General\][ \t]*\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);

    public static bool Development(string profile) =>
        File.Exists(ConfigFile(profile)) && DevLine.Match(File.ReadAllText(ConfigFile(profile))) is { Success: true } m && m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static object SetDevelopment(string profile, bool on)
    {
        RequireProfile(profile);
        var file = ConfigFile(profile);
        var line = "Development = " + (on ? "true" : "false");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        text = DevLine.IsMatch(text) ? DevLine.Replace(text, line, 1)
            : GeneralLine.IsMatch(text) ? GeneralLine.Replace(text, "$0\n" + line, 1)
            : text + (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "") + "[General]\n" + line + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return new { development = on, restartNeeded = Game.Running };
    }

    // Removes the plugin DLLs; installed skins stay.
    public static object PluginUninstall(string profile)
    {
        var dir = Game.PluginDir(profile);
        foreach (var f in PluginFiles) RequireUnlocked(Path.Combine(dir, f));
        foreach (var f in PluginFiles.Select(f => Path.Combine(dir, f)).Where(File.Exists)) File.Delete(f);
        return new { removed = dir };
    }
}
