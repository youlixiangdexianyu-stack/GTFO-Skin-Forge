using System.Text.Json;
using System.Text.Json.Nodes;
using SkinForge.Format;

namespace SkinForge.Backend;

public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    static readonly string[] PluginFiles = ["SkinForge.Plugin.dll", "SkinForge.Format.dll"];

    static string Skins(string profile) => Path.Combine(Game.PluginDir(profile), "skins");
    static string ActiveFile(string profile) => Path.Combine(Skins(profile), "active.json");

    static JsonObject ReadActive(string profile)
    {
        var path = ActiveFile(profile);
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        json ??= new JsonObject();
        if (json["weapons"] is not JsonObject) json["weapons"] = new JsonObject();
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
        var weapons = json["weapons"]!.AsObject();
        var global = json["global"]!.AsArray();
        foreach (var key in weapons.Where(kv => (string?)kv.Value == doc.Id).Select(kv => kv.Key).ToList()) weapons.Remove(key);
        foreach (var node in global.Where(n => (string?)n == doc.Id).ToList()) global.Remove(node);
        if (active)
        {
            if (doc.Target.ArchetypeId is { } archetype) weapons[archetype.ToString()] = doc.Id;
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
        var active = json["weapons"]!.AsObject().Select(kv => (string?)kv.Value).Concat(json["global"]!.AsArray().Select(n => (string?)n)).ToHashSet();
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

    // The plugin build shipped next to the backend (plugin/), or in a development checkout the plugin project's output.
    static string PluginSource()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "plugin");
        if (File.Exists(Path.Combine(bundled, PluginFiles[0]))) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            foreach (var config in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "SkinForge.Plugin", "bin", config, "net6.0");
                if (File.Exists(Path.Combine(candidate, PluginFiles[0]))) return candidate;
            }
        throw new BackendException("not-found", "Plugin build not found next to the backend (plugin/SkinForge.Plugin.dll)");
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

    // Removes the plugin DLLs; installed skins stay.
    public static object PluginUninstall(string profile)
    {
        var dir = Game.PluginDir(profile);
        foreach (var f in PluginFiles) RequireUnlocked(Path.Combine(dir, f));
        foreach (var f in PluginFiles) File.Delete(Path.Combine(dir, f));
        return new { removed = dir };
    }
}
