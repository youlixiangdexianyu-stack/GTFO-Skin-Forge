using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// TEMPORARY, removed at ForgeSkin 1.4.0 together with Core/OldSkinFolder.cs (Docs/forge-skin/PLAN.md §6): delete this file
// and its one-line calls in Install.List and Install.Pack.
public static class OldSkinMove
{
    // ForgeSkin 1.1.0 and earlier kept the skins in BepInEx/plugins/ForgeSkin/skins; the plugin now reads only
    // BepInEx/ForgeSkin/skins. Runs whenever the editor lists or installs skins: each file (packs, active.json, .pick) is
    // moved over unless the new folder already has one by that name; a pack already there with the same content and the
    // old author log are deleted, and the old skins folder too once it is empty. A same-named file with other content and
    // anything that cannot be moved stay where they are, written to the log, and the plugin keeps saying skins are left there
    // (Core/OldSkinFolder.HasSkins).
    // active.json entries the new one lacks are added to it. The move stays inside the profile: a rename, never half a copy.
    public static void Run(string profile)
    {
        var bepInEx = Game.BepInEx(profile);
        var plugin = SkinFolders.Plugin(bepInEx);
        var old = OldSkinFolder.Of(bepInEx);
        try { File.Delete(Path.Combine(plugin, "forgeskin.log")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a running old plugin holds it: next time
        if (!Directory.Exists(old)) return;
        var dir = Install.Skins(profile);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(old))
            try
            {
                var to = Path.Combine(dir, Path.GetFileName(file));
                if (!File.Exists(to)) File.Move(file, to);
                else if (FileHash.Sha256(to) == FileHash.Sha256(file)) File.Delete(file);
                else if (Path.GetFileName(file).Equals("active.json", StringComparison.OrdinalIgnoreCase))
                {
                    var json = Install.ReadActive(profile);
                    var older = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                    if (older?["targets"] is JsonObject targets)
                        foreach (var (key, id) in targets)
                            if (!json["targets"]!.AsObject().ContainsKey(key)) json["targets"]![key] = id?.DeepClone();
                    if (older?["global"] is JsonArray global)
                        foreach (var id in global.Select(n => (string?)n).Where(id => id != null && !json["global"]!.AsArray().Any(n => (string?)n == id)).ToList())
                            json["global"]!.AsArray().Add(id);
                    Install.WriteActive(profile, json);
                    File.Delete(file);
                }
                else Log.Write($"{file} kept in the old skins folder: {to} already exists with other content");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Write($"{file} kept in the old skins folder: {e.Message}");
            }
        if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
        if (Directory.GetFileSystemEntries(plugin).Length == 0) Directory.Delete(plugin);
    }
}
