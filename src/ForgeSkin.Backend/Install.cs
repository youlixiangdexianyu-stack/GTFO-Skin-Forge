using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    static readonly string[] PluginFiles = ["ForgeSkin.Plugin.dll", "ForgeSkin.Format.dll", "NVorbis.dll"];

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
        AtomicFile.WriteText(ActiveFile(profile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        using (var file = new AtomicFile(target))
        {
            File.Copy(pack, file.Temp, true);
            file.Commit();
        }
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
                candidates.Add(Path.Combine(dir.FullName, "src", "ForgeSkin.Plugin", "bin", config, "net6.0"));
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
        return PluginFiles.All(f => File.Exists(Path.Combine(dir, f)) && FileHash.Sha256(Path.Combine(dir, f)) == FileHash.Sha256(Path.Combine(source, f)));
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
        MovePreRenameInstall(profile);
        var dir = Game.PluginDir(profile);
        Directory.CreateDirectory(Path.Combine(dir, "skins"));
        foreach (var f in PluginFiles) RequireUnlocked(Path.Combine(dir, f));
        foreach (var f in PluginFiles) File.Copy(Path.Combine(source, f), Path.Combine(dir, f), true);
        return new { installed = dir, version = Game.Profiles().FirstOrDefault(p => Path.GetFullPath(p.Path) == Path.GetFullPath(profile))?.PluginVersion };
    }

    // Moves a pre-rename install (GTFO Skin Forge -> ForgeSkin) to the new names: skins/ of plugins/SkinForge (packs get the
    // new extension, active.json kept) into the new plugin folder, config/SkinForge.cfg and the BepInEx/SkinForge readback
    // folder to theirs; then the old DLLs and log are deleted and the old folder too once nothing else is left in it.
    static void MovePreRenameInstall(string profile)
    {
        var bepInEx = Path.Combine(profile, "BepInEx");
        var old = Path.Combine(bepInEx, "plugins", "SkinForge");
        if (Directory.Exists(old))
        {
            string[] oldFiles = ["SkinForge.Plugin.dll", "SkinForge.Format.dll", "NVorbis.dll", "skinforge.log"];
            foreach (var f in oldFiles) RequireUnlocked(Path.Combine(old, f));
            var oldSkins = Path.Combine(old, "skins");
            if (Directory.Exists(oldSkins))
            {
                Directory.CreateDirectory(Skins(profile));
                foreach (var file in Directory.GetFiles(oldSkins))
                {
                    var name = Path.GetExtension(file).Equals(".gtfskin", StringComparison.OrdinalIgnoreCase)
                        ? Path.ChangeExtension(Path.GetFileName(file), SkinPack.Extension) : Path.GetFileName(file);
                    var to = Path.Combine(Skins(profile), name);
                    if (!File.Exists(to)) File.Move(file, to);
                }
                if (Directory.GetFileSystemEntries(oldSkins).Length == 0) Directory.Delete(oldSkins);
            }
            foreach (var f in oldFiles.Select(f => Path.Combine(old, f)).Where(File.Exists)) File.Delete(f);
            if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
            else Log.Write($"pre-rename {old} kept: it still holds files that were not moved");
        }
        var oldConfig = Path.Combine(bepInEx, "config", "SkinForge.cfg");
        if (File.Exists(oldConfig) && !File.Exists(ConfigFile(profile))) File.Move(oldConfig, ConfigFile(profile));
        var oldReadback = Path.Combine(bepInEx, "SkinForge");
        // Readback files are debug output the plugin writes again; a folder held open elsewhere stays where it is.
        try { if (Directory.Exists(oldReadback) && !Directory.Exists(Path.Combine(bepInEx, "ForgeSkin"))) Directory.Move(oldReadback, Path.Combine(bepInEx, "ForgeSkin")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"pre-rename {oldReadback} kept: {e.Message}"); }
    }

    // The plugin's development switch (hot reload), the `Development` entry of BepInEx/config/ForgeSkin.cfg. The plugin
    // reads it when the game starts, so a change applies from the next launch.
    static string ConfigFile(string profile) => Path.Combine(profile, "BepInEx", "config", "ForgeSkin.cfg");
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
        AtomicFile.WriteText(file, text);
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
