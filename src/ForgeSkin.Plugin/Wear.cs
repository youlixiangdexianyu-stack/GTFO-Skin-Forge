using System.Text.Json;
using BepInEx;
using ForgeSkin.Format;

namespace ForgeSkin.Plugin;

// Kills the local player made with each skin on each gear ("<skin id>|<gear key>"), kept across sessions in
// BepInEx/ForgeSkin/wear.json, for skins whose livery wears with kills (MaterialOverride.Wear). Saved at most every ten
// seconds while it changes, and when a level starts generating.
static class Wear
{
    static readonly string File = Path.Combine(Paths.BepInExRootPath, "ForgeSkin", "wear.json");
    static Dictionary<string, int>? _kills;
    static bool _dirty;
    static float _savedAt;

    static Dictionary<string, int> Kills()
    {
        if (_kills != null) return _kills;
        try { _kills = System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(File)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { ForgeSkinPlugin.Fail($"wear: {File} unreadable ({e.Message}); counting from 0"); }
        return _kills ??= new();
    }

    public static int Count(string key) => Kills().GetValueOrDefault(key);

    public static void Add(string key)
    {
        Kills()[key] = Count(key) + 1;
        _dirty = true;
    }

    /// <summary>The stage the kills reached: the last whose kill count is at most `kills` (stages lowest first).</summary>
    public static WearStage Stage(List<WearStage> stages, int kills) => stages.LastOrDefault(s => s.Kills <= kills) ?? stages[0];

    public static void Save(float now, bool force = false)
    {
        if (!_dirty || !force && now - _savedAt < 10f) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(_kills));
            System.IO.File.Move(temp, File, true);
            (_dirty, _savedAt) = (false, now);
        }
        catch (IOException e) { ForgeSkinPlugin.Fail($"wear: could not save {File}: {e.Message}"); }
    }
}
