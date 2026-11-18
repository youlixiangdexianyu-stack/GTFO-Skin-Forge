namespace ForgeSkin.Format;

/// <summary>Where ForgeSkin keeps its data, from a BepInEx folder: the running game's (plugin) or a profile's (editor). Everything
/// lives in BepInEx/ForgeSkin/, outside plugins/, so a mod manager updating or removing the plugin never touches it.</summary>
public static class SkinFolders
{
    public static string Root(string bepInEx) => Path.Combine(bepInEx, "ForgeSkin");

    /// <summary>The installed packs, active.json and the editor's one-shot .pick files.</summary>
    public static string Skins(string bepInEx) => Path.Combine(Root(bepInEx), "skins");

    /// <summary>The plugin's log for the editor (what loaded, what failed; every detail with Development on), read after 应用到游戏.</summary>
    public static string AuthorLog(string bepInEx) => Path.Combine(Root(bepInEx), "forgeskin.log");

    /// <summary>The file a skin mod (a mod folder directly under BepInEx/plugins) lists its packs in, one path per line.</summary>
    public const string ModMarker = "forgeskin.forge";

    /// <summary>Every pack the skin mods under `plugins` list: the mod folder and the pack's full path, or why the line names no pack.</summary>
    public static IEnumerable<(string Mod, string Pack, string? Problem)> ModPacks(string plugins)
    {
        if (!Directory.Exists(plugins)) yield break;
        foreach (var mod in Directory.GetDirectories(plugins))
        {
            var marker = Path.Combine(mod, ModMarker);
            if (!File.Exists(marker)) continue;
            foreach (var raw in File.ReadAllLines(marker))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var pack = Path.GetFullPath(Path.Combine(mod, line));
                var problem = !pack.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase) ? $"{Path.GetFileName(mod)}/{ModMarker}: {line} is not a {SkinPack.Extension} pack"
                    : !File.Exists(pack) ? $"{Path.GetFileName(mod)}/{ModMarker}: {line} not found" : null;
                yield return (mod, pack, problem);
            }
        }
    }

    public static string Readback(string bepInEx) => Path.Combine(Root(bepInEx), "readback");

    /// <summary>The folder the editor installs its own copy of the plugin DLL into (a mod manager puts it elsewhere).</summary>
    public static string Plugin(string bepInEx) => Path.Combine(bepInEx, "plugins", "ForgeSkin");
}
