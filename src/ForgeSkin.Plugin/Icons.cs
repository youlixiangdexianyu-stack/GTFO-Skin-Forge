using ForgeSkin.Format;
using Gear;

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
        foreach (var (key, id) in Skins.Targets) now[key] = Skins.LookOf(id);
        foreach (var key in now.Keys.Union(_shown.Keys))
            if (!now.TryGetValue(key, out var a) || !_shown.TryGetValue(key, out var b) || a != b) Due.Add(key);
        _shown = now;
        var globals = Skins.GlobalsLook();
        if (globals != _globals) (_globals, _globalsChanged) = (globals, true);
    }

    static readonly HashSet<string> Early = new(); // target keys whose icon was rendered while their skins were still loading

    /// <summary>The icon of this target was rendered before its skins were loaded: it is rendered again once they are.</summary>
    public static void RenderedWithout(string key) => Early.Add(key);

    /// <summary>Re-renders the icons noted, once the game has its gear and icon renderer.</summary>
    public static void Tick()
    {
        if (Early.Count > 0)
            foreach (var key in Early.ToList())
                if (Skins.Ready(Skins.Wanted(key), ForgeCore.Frame.UnscaledTime)) { Early.Remove(key); Due.Add(key); }
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
                // Only the icons of a gear whose shown skin changed (any gear when a global skin did); a gear whose icons another
                // package supplies (ForgeUI GearIcons.Supply) is drawn again by that package's own model.
                if (!_globalsChanged && !Due.Contains(SkinTarget.GearKey(id))) continue;
                if (ForgeUI.GearIcons.Redraw(range)) rendered++;
            }
        }
        Due.Clear();
        _globalsChanged = false;
        if (rendered > 0) ForgeSkinPlugin.Info($"icons: rendering {rendered} gear icons again for their skins");
    }
}
