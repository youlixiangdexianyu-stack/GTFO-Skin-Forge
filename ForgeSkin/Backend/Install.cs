using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// `Dropped`: other copies of this skin the listing just moved to the Recycle Bin (file names), for the editor's log. `Source`:
// "editor" (the skins folder's `<id>.forgeskin`), "hand" (another name there: put in by hand or by another tool) or "mod" (listed
// by the skin mod `Mod` under BepInEx/plugins; the editor cannot remove those). `Modified`: the pack file's last write (ISO),
// which the editor compares with its project's to tell an edit not applied yet.
// `InUse`: the skin the game shows for its target, as the plugin decides it (Skins.Resolve): a pending 应用到游戏 pick, else the
// in-game picker's choice, else active.json, else the first by path.
public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error, string[]? Dropped = null, string Source = "editor", string? Mod = null, bool InUse = false, string? Modified = null);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    const string PluginDll = "ForgeSkin.Plugin.dll", CoreDll = "ForgeCore.dll", CoreFolder = "NAinfini-ForgeCore";
    // Left in the plugin folder by 1.0.3 and earlier: the format library is compiled into the plugin now and NVorbis ships
    // with ForgeCore, whose copy a second NVorbis.dll would clash with.
    static readonly string[] RetiredFiles = ["ForgeSkin.Format.dll", "NVorbis.dll"];

    internal static string Skins(string profile) => SkinFolders.Skins(Game.BepInEx(profile));
    static string ActiveFile(string profile) => Path.Combine(Skins(profile), "active.json");

    internal static JsonObject ReadActive(string profile)
    {
        var path = ActiveFile(profile);
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        json ??= new JsonObject();
        if (json["targets"] is not JsonObject) json["targets"] = new JsonObject();
        if (json["global"] is not JsonArray) json["global"] = new JsonArray();
        return json;
    }

    internal static void WriteActive(string profile, JsonObject json)
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
    // its next reload (at once in a running game, else at the next launch) and deletes it. Only the
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
        OldSkinMove.Run(profile);
        var dir = Skins(profile);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Preview.Safe(doc.Id) + SkinPack.Extension);
        using (var file = new AtomicFile(target))
        {
            File.Copy(pack, file.Temp, true);
            file.Commit();
        }
        if (activate) SetActive(profile, doc, true);
        if (pick) WritePick(dir, doc);
        var dropped = Dedupe(Read(dir).Where(p => p.Doc?.Id == doc.Id).ToList()).GetValueOrDefault(doc.Id) ?? [];
        return new { installed = target, active = activate, dropped };
    }

    // Every pack in the skins folder with its manifest, or why it cannot be read.
    static List<(string File, SkinDocument? Doc, string? Error)> Read(string dir) =>
        Directory.GetFiles(dir, "*" + SkinPack.Extension).Order().Select(file =>
        {
            try
            {
                using var p = SkinPack.Open(file);
                return (file, (SkinDocument?)p.Manifest, (string?)null);
            }
            catch (Exception e) when (e is SkinFormatException or IOException or JsonException or InvalidDataException)
            {
                return (file, null, e is SkinFormatException f ? $"{f.Code}: {f.Message}" : e.Message);
            }
        }).ToList();

    // One pack per skin id. The editor installs `<id>.forgeskin` and replaces it in place; a copy of the same skin under another
    // name (put in by hand or by another tool) makes the plugin load only one of the two. The editor's own copy stays, else the
    // newest; the others go to the Recycle Bin and leave `packs`. Returns the file names moved away per id; one that cannot be
    // moved (held open) stays and is logged, so the install itself never fails for it.
    static Dictionary<string, string[]> Dedupe(List<(string File, SkinDocument? Doc, string? Error)> packs)
    {
        var dropped = new Dictionary<string, string[]>();
        foreach (var group in packs.Where(p => p.Doc != null).GroupBy(p => p.Doc!.Id).Where(g => g.Count() > 1))
        {
            var keep = group.FirstOrDefault(p => Path.GetFileName(p.File).Equals(Preview.Safe(group.Key) + SkinPack.Extension, StringComparison.OrdinalIgnoreCase)).File
                ?? group.MaxBy(p => File.GetLastWriteTimeUtc(p.File)).File;
            var gone = new List<string>();
            foreach (var (file, _, _) in group.Where(p => p.File != keep).ToList())
                try
                {
                    RecycleBin.Send(file);
                    packs.RemoveAll(p => p.File == file);
                    gone.Add(Path.GetFileName(file));
                    Log.Write($"{file}: another copy of skin {group.Key} (kept {Path.GetFileName(keep)}), moved to the Recycle Bin");
                }
                catch (IOException e) { Log.Write($"{file}: another copy of skin {group.Key} not moved: {e.Message}"); }
            if (gone.Count > 0) dropped[group.Key] = [.. gone];
        }
        return dropped;
    }

    // Every skin the profile's game loads: the skins folder's packs, then the skin mods' (read only). `tidy` (the editor's
    // listing): copies of one skin under several names in the skins folder are cleared first (Dedupe).
    public static List<InstalledSkin> List(string profile, bool tidy = false)
    {
        OldSkinMove.Run(profile);
        var dir = Skins(profile);
        var json = Directory.Exists(dir) ? ReadActive(profile) : new JsonObject { ["targets"] = new JsonObject(), ["global"] = new JsonArray() };
        var targets = json["targets"]!.AsObject().Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        var active = targets.Values.Concat(json["global"]!.AsArray().Select(n => (string?)n)).ToHashSet();
        var packs = Directory.Exists(dir) ? Read(dir) : [];
        var dropped = tidy ? Dedupe(packs) : new();
        var mods = SkinFolders.ModPacks(Path.Combine(profile, "BepInEx", "plugins")).Where(m => m.Problem == null).Select(m => m.Pack).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = packs.Select(p => (p.File, p.Doc, p.Error, Mod: (string?)null))
            .Concat(mods.Select(file => { var (f, d, e) = ReadOne(file); return (File: f, Doc: d, Error: e, Mod: (string?)Path.GetFileName(Path.GetDirectoryName(file))); })).ToList();
        // What the game shows per target, in the plugin's order.
        var shown = new Dictionary<string, string>();
        foreach (var (key, id) in ReadChoices(profile)) shown[key] = id;
        foreach (var (key, id) in ReadPicks(dir)) shown[key] = id;
        foreach (var (key, id) in targets) if (id != null) shown.TryAdd(key, id);
        // A skin mod's pack is worn unless something above picks for its target; among them the first by path.
        foreach (var (_, d, _, _) in all.Where(p => p.Mod != null).OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase))
            if (d?.Target.Key() is { } key) shown.TryAdd(key, d.Id);
        return all.Select(p => p.Doc is { } d
            ? new InstalledSkin(d.Id, d.Name, p.File, d.Target.Weapon, active.Contains(d.Id), null, dropped.GetValueOrDefault(d.Id), SourceOf(p.File, d.Id, p.Mod), p.Mod,
                d.Target.Key() is { } key ? shown.GetValueOrDefault(key) == d.Id : active.Contains(d.Id), Stamp(p.File))
            : new InstalledSkin(Path.GetFileNameWithoutExtension(p.File), Path.GetFileName(p.File), p.File, null, false, p.Error, null, SourceOf(p.File, null, p.Mod), p.Mod, false, Stamp(p.File))).ToList();
    }

    static string SourceOf(string file, string? id, string? mod) =>
        mod != null ? "mod" : id != null && Path.GetFileName(file).Equals(Preview.Safe(id) + SkinPack.Extension, StringComparison.OrdinalIgnoreCase) ? "editor" : "hand";

    static string? Stamp(string file)
    {
        try { return File.GetLastWriteTimeUtc(file).ToString("O"); }
        catch (IOException) { return null; }
    }

    static (string File, SkinDocument? Doc, string? Error) ReadOne(string file)
    {
        try
        {
            using var p = SkinPack.Open(file);
            return (file, p.Manifest, null);
        }
        catch (Exception e) when (e is SkinFormatException or IOException or JsonException or InvalidDataException)
        {
            return (file, null, e is SkinFormatException f ? $"{f.Code}: {f.Message}" : e.Message);
        }
    }

    // The in-game picker's choices (target key -> skin id, "" = the original look), written by the plugin only.
    static Dictionary<string, string> ReadChoices(string profile)
    {
        var file = Path.Combine(profile, "BepInEx", "config", "ForgeSkin.choices.json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    // 应用到游戏 picks the plugin has not taken yet, oldest first (a later one wins, as in the plugin).
    static IEnumerable<(string Target, string Skin)> ReadPicks(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*" + SkinPack.PickExtension).Order())
        {
            JsonObject? pick;
            try { pick = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (Exception e) when (e is IOException or JsonException) { continue; }
            if (pick?["target"]?.GetValue<string>() is { } target && pick["skin"]?.GetValue<string>() is { } skin) yield return (target, skin);
        }
    }

    // A character pack's card picture (the one the game's skin page shows) as base64 PNG. A pack without one gives its converted
    // model instead, unpacked under `cache`, for the editor to draw the picture from (`character`: the model files and bone match).
    public static object Picture(string pack, string cache)
    {
        using var p = SkinPack.Open(pack);
        if (p.Manifest.Character?.Picture is { } picture) return new { png = Convert.ToBase64String(p.ReadBytes(picture)), character = (CharacterSkin?)null };
        var doc = SkinJson.Read(SkinJson.Write(p.Manifest));
        if (doc.Character != null) Projects.Unpack(p, doc, cache, new HashSet<string>());
        return new { png = (string?)null, character = doc.Character };
    }

    // Takes a skin out of the profile: its pack goes to the Recycle Bin (the player can put it back), and active.json forgets it.
    public static object Remove(string profile, string skinId)
    {
        // A skin mod's pack belongs to the mod manager: only the skins folder's copies are taken out.
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId && s.Source != "mod") ?? throw new BackendException("not-found", $"Skin {skinId} is not in the skins folder");
        SetActive(profile, new SkinDocument { Id = skinId }, false);
        RecycleBin.Send(skin.File);
        return new { removed = skin.File };
    }

    // Turning a skin on also goes before the player's in-game picker choice for its target (a pick, as 应用到游戏 does), so the
    // game shows what the editor says.
    public static object Activate(string profile, string skinId)
    {
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId && s.Error == null) ?? throw new BackendException("not-found", $"Skin {skinId} is not installed");
        SkinDocument doc;
        using (var p = SkinPack.Open(skin.File)) doc = p.Manifest;
        SetActive(profile, doc, true);
        WritePick(Skins(profile), doc);
        return new { id = skinId };
    }

    // An older pick for the same target that the plugin has not taken (it is not running) is dropped first, as the newer one
    // would win anyway; picks for other targets stay.
    static void WritePick(string dir, SkinDocument doc)
    {
        if (doc.Target.Key() is not { } key) return;
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(dir, "*" + SkinPack.PickExtension))
        {
            try
            {
                if ((JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["target"]?.GetValue<string>() == key) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { } // the plugin took it meanwhile
        }
        AtomicFile.WriteText(Path.Combine(dir, $"{DateTime.UtcNow.Ticks}{SkinPack.PickExtension}"), JsonSerializer.Serialize(new { target = key, skin = doc.Id }));
    }

    /// <summary>The ForgeSkin plugin version this editor ships (what a profile's plugin is held to), or null without a plugin build.</summary>
    public static string? ShippedVersion()
    {
        try { return DllVersion(Path.Combine(PluginSource(), "ForgeSkin", PluginDll)).ToString(3); }
        catch (BackendException) { return null; }
    }

    // The plugin the app ships, next to the backend: plugin/ForgeSkin/ForgeSkin.Plugin.dll and plugin/ForgeCore/, the plugins/ForgeCore/
    // folder of the NAinfini-ForgeCore release zip it depends on. In a checkout the same layout that Mods/ForgeSkin/tools/build-editor-release.ps1
    // stages under artifacts/forge-skin-release/publish/plugin.
    static string PluginSource()
    {
        // The folder to name when none is complete: the first that exists (a half-staged one), else the checkout's (the
        // folder holding .git), else plugin/ next to the backend.
        string? named = null, checkout = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var plugin in new[] { Path.Combine(dir.FullName, "plugin"), Path.Combine(dir.FullName, "artifacts", "forge-skin-release", "publish", "plugin") })
            {
                if (File.Exists(Path.Combine(plugin, "ForgeSkin", PluginDll)) && File.Exists(Path.Combine(plugin, "ForgeCore", CoreDll))) return plugin;
                if (Directory.Exists(plugin)) named ??= plugin;
            }
            if (checkout == null && Path.Exists(Path.Combine(dir.FullName, ".git"))) checkout = Path.Combine(dir.FullName, "artifacts", "forge-skin-release", "publish", "plugin");
        }
        var where = named ?? checkout ?? Path.Combine(AppContext.BaseDirectory, "plugin");
        var missing = new[] { Path.Combine(where, "ForgeSkin", PluginDll), Path.Combine(where, "ForgeCore", CoreDll) }.Where(f => !File.Exists(f));
        throw new BackendException("no-plugin", string.Join("; ", missing));
    }

    static Version DllVersion(string dll)
    {
        var v = FileVersionInfo.GetVersionInfo(dll);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    // Every folder under BepInEx/plugins holding a ForgeCore.dll (a mod manager puts it in NAinfini-ForgeCore/).
    static List<string> CoreFolders(string profile)
    {
        var plugins = Path.Combine(profile, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) return [];
        return [.. Directory.EnumerateFiles(plugins, CoreDll, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Select(f => Path.GetDirectoryName(f)!)];
    }

    // Whether every file of the shipped folder `source` is in `target` with the same content (`cached`: for a status report).
    static bool Same(string source, string target, bool cached = false) =>
        Directory.GetFiles(source, "*", SearchOption.AllDirectories).All(file =>
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            return File.Exists(to) && FileHash.Sha256(to, cached) == FileHash.Sha256(file, cached);
        });

    // Copies the shipped folder `source` over `target`; refuses while GTFO holds one of the files.
    static void CopyOver(string source, string target)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        foreach (var file in files) RequireUnlocked(Path.Combine(target, Path.GetRelativePath(source, file)));
        foreach (var file in files)
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
    }

    // The newest ForgeSkin.Plugin.dll a mod manager installed (anywhere under BepInEx/plugins but the editor's own
    // plugins/ForgeSkin/, usually NAinfini-ForgeSkin/), or null. The skins are in BepInEx/ForgeSkin/skins either way.
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

    // The copies the editor keeps in step with the shipped folders: the ForgeSkin the game loads (the mod manager's, else the
    // editor's own folder) and every ForgeCore; `Dll` is the copy's DLL, `Source` the shipped folder it follows.
    static IEnumerable<(string Name, string Source, string Target, string Dll)> Copies(string profile, string source)
    {
        var skin = ManagedPlugin(profile) is { } managed ? Path.GetDirectoryName(managed)! : Game.PluginDir(profile);
        yield return ("ForgeSkin", Path.Combine(source, "ForgeSkin"), skin, PluginDll);
        foreach (var core in CoreFolders(profile)) yield return ("ForgeCore", Path.Combine(source, "ForgeCore"), core, CoreDll);
    }

    // A copy the editor leaves alone: one a mod manager installed from the store, newer than the shipped one. From the store means
    // its package folder (the first folder under BepInEx/plugins) holds the Thunderstore manifest.json whose version_number is the
    // DLL's own version, so nothing wrote over it since. Store versions only go up, so a higher number there is a newer build. Any
    // other copy (the editor's own folder, one unpacked by hand, one an editor already wrote over, the pre-store 1.1.x ForgeSkin and
    // 1.0.2 ForgeCore of the GitHub editors) follows the shipped one by content, whatever its number.
    static bool NewerFromStore(string profile, string dll, string shippedDll)
    {
        if (!File.Exists(dll)) return false;
        var version = DllVersion(dll);
        if (version <= DllVersion(shippedDll)) return false;
        var plugins = Path.GetFullPath(Path.Combine(profile, "BepInEx", "plugins"));
        var top = Path.GetRelativePath(plugins, Path.GetFullPath(dll)).Split(Path.DirectorySeparatorChar);
        if (top.Length < 2 || top[0] == "..") return false;
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(plugins, top[0], "manifest.json")))?["version_number"]?.GetValue<string>() is { } number
                && Version.TryParse(number, out var store) && store == version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>The profile's store copies newer than the shipped ones, which the editor leaves alone ("ForgeCore 1.0.3"), for the editor to say so.</summary>
    public static string[] Newer(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return []; }
        return [.. Copies(profile, source).Where(c => !Same(c.Source, c.Target, true) && NewerFromStore(profile, Path.Combine(c.Target, c.Dll), Path.Combine(c.Source, c.Dll)))
            .Select(c => $"{c.Name} {DllVersion(Path.Combine(c.Target, c.Dll)).ToString(3)}")];
    }

    // Current: one plugin, nothing an older version left beside it, a ForgeCore in the profile, and each copy (Copies) either the
    // shipped build's files (DLL and lang/) or a newer one from the store (NewerFromStore).
    public static bool PluginCurrent(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return false; }
        var dir = Game.PluginDir(profile);
        var copies = Copies(profile, source).ToList();
        return (ManagedPlugin(profile) == null || !File.Exists(Path.Combine(dir, PluginDll))) && !RetiredFiles.Any(f => File.Exists(Path.Combine(dir, f)))
            && copies.Any(c => c.Name == "ForgeCore")
            && copies.All(c => Same(c.Source, c.Target, true) || NewerFromStore(profile, Path.Combine(c.Target, c.Dll), Path.Combine(c.Source, c.Dll)));
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

    // ForgeCore and ForgeSkin follow this editor: each copy in the profile (Copies) whose files differ from the shipped build is
    // overwritten in place with it (a mod manager's copy too, where the mod manager put it), unless it is a newer one from the
    // store (NewerFromStore), which stays and is named in `newer`; a profile without ForgeCore gets it in
    // BepInEx/plugins/NAinfini-ForgeCore/ForgeCore/ where the mod manager would put it. A ForgeSkin the mod manager installed is the
    // one the game loads, so the editor's own copy in plugins/ForgeSkin/ is deleted and the profile never holds the same plugin twice.
    public static object Plugin(string profile)
    {
        RequireProfile(profile);
        var source = PluginSource();
        var managed = ManagedPlugin(profile);
        MovePreRenameInstall(profile);
        var dir = Game.PluginDir(profile);
        Directory.CreateDirectory(Skins(profile));
        foreach (var f in RetiredFiles.Append(PluginDll)) RequireUnlocked(Path.Combine(dir, f));
        var copies = Copies(profile, source).ToList();
        if (!copies.Any(c => c.Name == "ForgeCore")) copies.Add(("ForgeCore", Path.Combine(source, "ForgeCore"), Path.Combine(profile, "BepInEx", "plugins", CoreFolder, "ForgeCore"), CoreDll));
        var newer = new List<string>();
        var replaced = new List<string>();
        foreach (var (name, from, to, dll) in copies.Where(c => !Same(c.Source, c.Target)))
            if (NewerFromStore(profile, Path.Combine(to, dll), Path.Combine(from, dll))) newer.Add($"{name} {DllVersion(Path.Combine(to, dll)).ToString(3)}");
            else
            {
                CopyOver(from, to);
                if (name == "ForgeCore") replaced.Add(to);
            }
        string[] stale = managed == null ? RetiredFiles : [.. RetiredFiles, PluginDll, .. Texts(dir)];
        foreach (var f in stale.Select(f => Path.Combine(dir, f)).Where(File.Exists)) File.Delete(f);
        return new { installed = copies[0].Target, forgeCore = replaced, newer, managedPlugin = managed != null, version = Game.Profiles().FirstOrDefault(p => Path.GetFullPath(p.Path) == Path.GetFullPath(profile))?.PluginVersion };
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
        try { if (Directory.Exists(oldReadback) && !Directory.Exists(SkinFolders.Root(bepInEx))) Directory.Move(oldReadback, SkinFolders.Root(bepInEx)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"pre-rename {oldReadback} kept: {e.Message}"); }
    }

    static string ConfigFile(string profile) => Path.Combine(profile, "BepInEx", "config", "ForgeSkin.cfg");

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
