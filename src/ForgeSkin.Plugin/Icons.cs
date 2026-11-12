using ForgeSkin.Format;
using Gear;
using UnityEngine;

namespace ForgeSkin.Plugin;

// Gear icons (lobby loadout, inventory) are rendered by the game from a gear it builds for the render job and kept in
// memory and on disk by the gear's checksum. The job's gear is skinned the moment its parts spawn (IsJob), and a gear
// whose active skin changed (another skin, a rebuilt pack, back to vanilla) has its cached icons dropped and re-rendered.
static class Icons
{
    static Dictionary<string, string> _shown = new(); // target key -> active skin id and pack time the icons show
    static readonly HashSet<string> Due = new(); // target keys whose icons are to be rendered again
    static bool _globalsChanged;
    static string _globals = "";

    /// <summary>True while the game's icon renderer is building this gear.</summary>
    public static bool IsJob(GearPartHolder holder)
    {
        var job = GearIconRendering.Current?.m_currentJob;
        var range = holder.GearIDRange;
        return job?.gearToRender != null && range != null && job.gearToRender.GetChecksum() == range.GetChecksum();
    }

    /// <summary>After a reload or a pick: notes every target whose shown skin changed.</summary>
    public static void SkinsChanged()
    {
        var now = new Dictionary<string, string>();
        foreach (var (key, id) in Skins.Targets)
            now[key] = Skins.ById.TryGetValue(id, out var skin) ? $"{id}@{skin.Stamp.Ticks}" : id;
        foreach (var key in now.Keys.Union(_shown.Keys))
            if (!now.TryGetValue(key, out var a) || !_shown.TryGetValue(key, out var b) || a != b) Due.Add(key);
        _shown = now;
        var globals = string.Join(",", Skins.Globals.Select(id => Skins.ById.TryGetValue(id, out var s) ? $"{id}@{s.Stamp.Ticks}" : id));
        if (globals != _globals) (_globals, _globalsChanged) = (globals, true);
    }

    /// <summary>Re-renders the icons noted, once the game has its gear and icon renderer.</summary>
    public static void Tick()
    {
        if (Due.Count == 0 && !_globalsChanged) return;
        if (GearManager.Current == null || GearIconRendering.Current == null) return;
        int rendered = 0;
        foreach (var slot in SkinnedGear.GearSlots)
        {
            var all = GearManager.GetAllGearForSlot(slot);
            if (all == null) continue;
            foreach (var range in all)
            {
                if (range == null || SkinnedGear.Offline(range.PlayfabItemInstanceId) is not { } id) continue;
                if (!_globalsChanged && !Due.Contains(SkinTarget.GearKey(id))) continue;
                Render(range);
                rendered++;
            }
        }
        Due.Clear();
        _globalsChanged = false;
        if (rendered > 0) ForgeSkinPlugin.Info($"icons: rendering {rendered} gear icons again for their skins");
    }

    // The game's caches of one gear's icons (posted jobs, sprites, render textures, the file) dropped and its jobs posted.
    static void Render(GearIDRange range)
    {
        uint checksum = range.GetChecksum();
        GearManager.m_allGearWithPostedIconJobs?.Remove(checksum);
        GearIconRendering.s_cachedSprites?.Remove(checksum);
        var perKey = GearManager.Current.m_allGearIconTexturesPerInstanceKey;
        if (perKey != null)
            foreach (var textures in perKey)
                textures?.Remove(checksum);
        string? path;
        if (GearIconRendering.TryGetGearIconFilePath(checksum, out path) && File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException e) { ForgeSkinPlugin.Info($"icons: could not delete {path}: {e.Message}"); }
        }
        GearManager.PostIconJobs(range);
    }
}
