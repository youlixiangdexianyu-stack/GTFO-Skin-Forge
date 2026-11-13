using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    const string PluginDll = "ForgeSkin.Plugin.dll", CoreDll = "ForgeCore.dll", CoreFolder = "NAinfini-ForgeCore";
    // Left in the plugin folder by 1.0.3 and earlier: the format library is compiled into the plugin now and NVorbis ships
    // with ForgeCore, whose copy a second NVorbis.dll would clash with.
    static readonly string[] RetiredFiles = ["ForgeSkin.Format.dll", "NVorbis.dll"];

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

    // `pick` (the editor's 应用到游戏): the skin also goes before the player's in-game picker choice for its target. Written
    // as a one-shot `<ticks>.pick` file beside active.json ({"target", "skin"}); the plugin applies it to its own choices at
    // its next reload (at once in a running game with Development on, else at the next launch) and deletes it. Only the
    // plugin writes ForgeSkin.choices.json, so a choice the player makes meanwhile is never written over.
    public static object Pack(string pack, string profile, bool activate, bool pick = false)
    {
        RequireProfile(profile);
        SkinDocument doc;
        using (var p = SkinPack.Open(pack))
        {
            PackBuilder.CheckSounds(p);
            doc = p.Manifest;
        }
        var dir = Skins(profile);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Preview.Safe(doc.Id) + SkinPack.Extension);
        using (var file = new AtomicFile(target))
        {
            File.Copy(pack, file.Temp, true);
            file.Commit();
        }
        if (activate) SetActive(profile, doc, true);
        if (pick && doc.Target.Key() is { } key)
            AtomicFile.WriteText(Path.Combine(dir, $"{DateTime.UtcNow.Ticks}{SkinPack.PickExtension}"), JsonSerializer.Serialize(new { target = key, skin = doc.Id }));
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

    // The plugin the app ships, next to the backend: plugin/ForgeSkin/ForgeSkin.Plugin.dll and plugin/ForgeCore/, the unpacked
    // NAinfini-ForgeCore package it depends on. In a checkout the same layout that Mods/ForgeSkin/tools/build-editor-release.ps1
    // stages under artifacts/forge-skin-release/publish/plugin.
    static string PluginSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            foreach (var plugin in new[] { Path.Combine(dir.FullName, "plugin"), Path.Combine(dir.FullName, "artifacts", "forge-skin-release", "publish", "plugin") })
                if (File.Exists(Path.Combine(plugin, "ForgeSkin", PluginDll)) && File.Exists(Path.Combine(plugin, "ForgeCore", CoreDll))) return plugin;
        throw new BackendException("no-plugin", "Plugin build not found: neither plugin/ next to the backend nor artifacts/forge-skin-release in the checkout");
    }

    static Version DllVersion(string dll)
    {
        var v = FileVersionInfo.GetVersionInfo(dll);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    // The newest ForgeCore.dll anywhere under BepInEx/plugins (a mod manager puts it in NAinfini-ForgeCore/), or null.
    static Version? InstalledCore(string profile)
    {
        var plugins = Path.Combine(profile, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) return null;
        return Directory.EnumerateFiles(plugins, CoreDll, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(DllVersion).Max();
    }

    // The newest ForgeSkin.Plugin.dll a mod manager installed (anywhere under BepInEx/plugins but the editor's own
    // plugins/ForgeSkin/, usually NAinfini-ForgeSkin/), or null. The skins stay in plugins/ForgeSkin/skins either way.
    static string? ManagedPlugin(string profile)
    {
        var plugins = Path.Combine(profile, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) return null;
        var own = Path.GetFullPath(Game.PluginDir(profile)) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(plugins, PluginDll, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => !Path.GetFullPath(f).StartsWith(own, StringComparison.OrdinalIgnoreCase)).MaxBy(DllVersion);
    }

    /// <summary>The plugin DLL the game loads from this profile: the mod manager's, else the editor's, else null.</summary>
    public static string? InstalledPlugin(string profile)
    {
        var own = Path.Combine(Game.PluginDir(profile), PluginDll);
        return ManagedPlugin(profile) ?? (File.Exists(own) ? own : null);
    }

    // Current: one plugin DLL (a mod manager's at least the shipped version, or the editor's own copy of the shipped build),
    // nothing an older version left beside it, and ForgeCore at least the shipped one.
    public static bool PluginCurrent(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return false; }
        var dir = Game.PluginDir(profile);
        var own = Path.Combine(dir, PluginDll);
        var shipped = Path.Combine(source, "ForgeSkin", PluginDll);
        var plugin = ManagedPlugin(profile) is { } managed
            ? DllVersion(managed) >= DllVersion(shipped) && !File.Exists(own)
            : File.Exists(own) && FileHash.Sha256(own) == FileHash.Sha256(shipped)
              && Texts(Path.Combine(source, "ForgeSkin")).All(f => File.Exists(Path.Combine(dir, f)) && FileHash.Sha256(Path.Combine(dir, f)) == FileHash.Sha256(Path.Combine(source, "ForgeSkin", f)));
        return plugin && !RetiredFiles.Any(f => File.Exists(Path.Combine(dir, f)))
            && InstalledCore(profile) is { } core && core >= DllVersion(Path.Combine(source, "ForgeCore", CoreDll));
    }

    // The plugin's in-game texts (lang/*.forge, beside its DLL) under `folder`, as paths relative to it.
    static IEnumerable<string> Texts(string folder) =>
        Directory.Exists(Path.Combine(folder, "lang")) ? Directory.GetFiles(Path.Combine(folder, "lang"), "*.forge").Select(f => Path.Combine("lang", Path.GetFileName(f))) : [];

    // Refuses while GTFO runs and holds the DLL.
    static void RequireUnlocked(string dll)
    {
        if (!File.Exists(dll) || !Game.Running) return;
        try { using var _ = File.Open(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BackendException("game-running", "Close GTFO first: it has the plugin loaded"); }
    }

    // ForgeCore belongs to the mod manager: one already in the profile is never replaced, and one older than the shipped
    // copy is refused (forge-core-old) so the player updates it there. Only a profile without ForgeCore gets the shipped
    // package, in BepInEx/plugins/NAinfini-ForgeCore/ where the mod manager would put it. The same holds for a ForgeSkin
    // plugin the mod manager installed: it is the one the game loads (forge-skin-old when older than the shipped one), and
    // the editor's own copy in plugins/ForgeSkin/ is deleted so the profile never holds the same plugin twice.
    public static object Plugin(string profile)
    {
        RequireProfile(profile);
        var source = PluginSource();
        var core = InstalledCore(profile);
        var shipped = DllVersion(Path.Combine(source, "ForgeCore", CoreDll));
        if (core != null && core < shipped)
            throw new BackendException("forge-core-old", $"ForgeCore {core} in this profile is older than {shipped}, which ForgeSkin needs; update ForgeCore in the mod manager");
        var managed = ManagedPlugin(profile);
        var plugin = DllVersion(Path.Combine(source, "ForgeSkin", PluginDll));
        if (managed != null && DllVersion(managed) < plugin)
            throw new BackendException("forge-skin-old", $"ForgeSkin {DllVersion(managed)} at {managed} is older than {plugin}, which this editor needs; update ForgeSkin in the mod manager");
        MovePreRenameInstall(profile);
        var dir = Game.PluginDir(profile);
        Directory.CreateDirectory(Path.Combine(dir, "skins"));
        foreach (var f in RetiredFiles.Append(PluginDll)) RequireUnlocked(Path.Combine(dir, f));
        if (core == null)
        {
            var coreSource = Path.Combine(source, "ForgeCore");
            foreach (var file in Directory.GetFiles(coreSource, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(profile, "BepInEx", "plugins", CoreFolder, Path.GetRelativePath(coreSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        }
        if (managed == null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "lang"));
            foreach (var f in Texts(Path.Combine(source, "ForgeSkin")).Append(PluginDll)) File.Copy(Path.Combine(source, "ForgeSkin", f), Path.Combine(dir, f), true);
        }
        string[] stale = managed == null ? RetiredFiles : [.. RetiredFiles, PluginDll, .. Texts(dir)];
        foreach (var f in stale.Select(f => Path.Combine(dir, f)).Where(File.Exists)) File.Delete(f);
        return new { installed = managed == null ? dir : Path.GetDirectoryName(managed)!, forgeCoreInstalled = core == null, managedPlugin = managed != null, version = Game.Profiles().FirstOrDefault(p => Path.GetFullPath(p.Path) == Path.GetFullPath(profile))?.PluginVersion };
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

    // Removes the editor's plugin DLLs; installed skins stay, and so do ForgeCore and a ForgeSkin the mod manager installed
    // (the mod manager owns them).
    public static object PluginUninstall(string profile)
    {
        var dir = Game.PluginDir(profile);
        string[] files = [PluginDll, .. RetiredFiles, .. Texts(dir)];
        foreach (var f in files) RequireUnlocked(Path.Combine(dir, f));
        foreach (var f in files.Select(f => Path.Combine(dir, f)).Where(File.Exists)) File.Delete(f);
        return new { removed = dir };
    }
}
