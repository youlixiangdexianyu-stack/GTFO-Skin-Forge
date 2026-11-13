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
        // A file that cannot be read is logged by Store and left alone: this session counts from 0 and Save refuses to replace it.
        return _kills ??= ForgeCore.Store.Read<Dictionary<string, int>>(ForgeSkinPlugin.Guid, File).Value ?? new();
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
        // Store logs a refused or failed write; the next change tries again.
        ForgeCore.Store.Write(ForgeSkinPlugin.Guid, File, _kills);
        (_dirty, _savedAt) = (false, now);
    }
}
