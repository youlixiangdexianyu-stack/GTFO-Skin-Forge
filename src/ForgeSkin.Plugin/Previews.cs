using ForgeUI;
using Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// The picker's pictures of a gear in each of its skins (and in none). A picture is made only when the picker asks for it (the card in
// focus): the skin is loaded on demand like a worn one, then a tracked gear of that target that is not in anyone's hands (the lobby
// model, or the gear the game's icon renderer builds; ForgeCore GearIcons.Redraw has it build one, photographed the moment it is
// dressed, Built) puts on that skin for one frame, is photographed (ForgeCore ItemModelIcons.Photograph) and goes back to what it
// wears. A picture that cannot be made keeps its reason, which the picker says instead of waiting. Pictures are kept per target and
// skin until the skin's pack or colours, or the global skins, change; at most Keep of them.
static class Previews
{
    const int Keep = 24;
    const float GiveUp = 20f, RedrawAgain = 5f;

    sealed class Picture
    {
        public Sprite? Sprite; // null: it could not be made, for the reason Why (a text key)
        public string Why = "";
        public string Look = "";
        public float Used;
    }

    sealed class Want
    {
        public string Key = "";
        public Skin? Skin;
        public GearIDRange Range = null!;
        public float Since, Redrawn = -1;
        public int Redraws;
    }

    static readonly Dictionary<string, Picture> Made = new();
    static readonly Dictionary<string, Want> Wanted = new();

    static string Id(string key, Skin? skin) => key + "|" + (skin?.Doc.Id ?? "");
    static string Look(Skin? skin) => Skins.GlobalsLook() + "#" + (skin == null ? "" : Skins.LookOf(skin.Doc.Id));

    // The picture already made of the gear of `key` in `skin` (null: no skin of its own), or null; asks for nothing.
    public static Sprite? Of(string key, Skin? skin) =>
        Made.TryGetValue(Id(key, skin), out var p) && p.Look == Look(skin) ? p.Sprite : null;

    // The picture of the gear of `key` in `skin`, or null with `coming` true while it is being made (Picker.Changed follows when it is),
    // or null with `why` (a text key) when it could not be made.
    public static Sprite? Ask(string key, Skin? skin, out bool coming, out string why)
    {
        coming = false;
        why = "";
        var id = Id(key, skin);
        var look = Look(skin);
        if (Made.TryGetValue(id, out var p) && p.Look == look)
        {
            p.Used = Time.unscaledTime;
            why = p.Why;
            return p.Sprite;
        }
        if (SkinnedGear.RangeOf(key) is not { } range) return null; // only gear has a model to photograph
        coming = true;
        if (!Wanted.ContainsKey(id)) Wanted[id] = new Want { Key = key, Skin = skin, Range = range, Since = Time.unscaledTime };
        return null;
    }

    // The picker closed: pictures not made yet are no longer wanted.
    public static void Cancel() => Wanted.Clear();

    // The skins a picture of `want` wears: the global ones and its own.
    static List<Skin> SkinsOf(Want want)
    {
        var skins = new List<Skin>();
        foreach (var g in Skins.Globals)
            if (Skins.ById.TryGetValue(g, out var global)) skins.Add(global);
        if (want.Skin != null) skins.Add(want.Skin);
        return skins;
    }

    // Main thread, every frame after the gear were refreshed.
    public static void Tick(float now)
    {
        if (Wanted.Count == 0) return;
        foreach (var (id, want) in Wanted.ToList())
        {
            // A pack rebuilt or removed meanwhile: its card asks again for the new one.
            if (want.Skin != null && want.Skin.Retired) { Wanted.Remove(id); continue; }
            var ready = Skins.Ready(SkinsOf(want), now); // loads them on demand; the picture waits
            if (now - want.Since > GiveUp)
            {
                Wanted.Remove(id);
                var why = ready ? "picker.previewNoModel" : "picker.previewNoSkin";
                ForgeSkinPlugin.Warn($"picture of {id}: not made in {GiveUp} s ({(ready ? $"the icon renderer built no model of the gear in {want.Redraws} redraws" : "its skins did not load")})");
                Store(id, null, why, want.Skin, now);
                continue;
            }
            if (!ready) continue;
            if (SkinnedGear.Model(want.Key, now) is { } gear) { Shoot(id, want, gear, now); continue; }
            // No model of this gear around: the game's icon renderer builds one, and it is photographed the moment its parts are on (Built).
            if (want.Redraws < 2 && (want.Redrawn < 0 || now - want.Redrawn > RedrawAgain) && !RedrawnNow(want.Key, now) && GearIcons.Redraw(want.Range))
            {
                want.Redrawn = now;
                want.Redraws++;
                Redrawn[want.Key] = now;
            }
        }
    }

    // When the gear of a key was last sent to the icon renderer: the cards of one gear share one redraw.
    static readonly Dictionary<string, float> Redrawn = new();
    static bool RedrawnNow(string key, float now) => Redrawn.TryGetValue(key, out var at) && now - at < RedrawAgain;

    // The icon renderer's gear of `gear`'s key has just been built and dressed (SkinnedGear.Track): every picture of that gear waiting
    // for a model is taken now. The renderer keeps its gear only while it draws the icon, too short to wait for the next Tick.
    public static void Built(SkinnedGear gear, float now)
    {
        if (Wanted.Count == 0 || !gear.Photographable()) return;
        foreach (var (id, want) in Wanted.ToList())
            if (want.Key == gear.Key && !(want.Skin?.Retired ?? false) && Skins.Ready(SkinsOf(want), now)) Shoot(id, want, gear, now);
    }

    static void Shoot(string id, Want want, SkinnedGear gear, float now)
    {
        Wanted.Remove(id);
        Sprite? sprite = null;
        try { sprite = gear.Photograph(want.Skin); }
        catch (Exception e) { ForgeSkinPlugin.Warn($"picture of {id}: {e.Message}"); }
        Store(id, sprite, sprite == null ? "picker.previewError" : "", want.Skin, now);
    }

    static void Store(string id, Sprite? sprite, string why, Skin? skin, float now)
    {
        if (Made.Remove(id, out var old)) Free(old);
        Made[id] = new Picture { Sprite = sprite, Why = why, Look = Look(skin), Used = now };
        while (Made.Count > Keep)
        {
            var oldest = Made.MinBy(p => p.Value.Used);
            Made.Remove(oldest.Key);
            Free(oldest.Value);
        }
        Picker.Changed();
    }

    static void Free(Picture p)
    {
        if (p.Sprite == null) return;
        Object.Destroy(p.Sprite.texture);
        Object.Destroy(p.Sprite);
    }
}

sealed partial class SkinnedGear
{
    Skin? _try; // while _trying: the weapon skin Apply puts on instead of the chosen one (null: none)
    bool _trying;

    // A gear of `key` to photograph: one nobody holds (the lobby model, or the icon renderer's), whose own look is settled.
    public static SkinnedGear? Model(string key, float now)
    {
        foreach (var g in Live)
            if (g._key == key && g.Photographable() && Skins.Ready(Skins.Wanted(key), now)) return g;
        return null;
    }

    internal string Key => _key;

    internal bool Photographable() =>
        _root != null && _holder != null && (_item == null || _icon) && !_waiting && ForgeVisual.GunMount.Of(_holder) == null;

    // This gear in `skin` (null: no skin of its own; the global skins as always), photographed, then back to what it wears.
    public Sprite Photograph(Skin? skin)
    {
        (_try, _trying) = (skin, true);
        try
        {
            Revert();
            Apply();
            return ItemModelIcons.Photograph(_root.transform, $"ForgeSkin {_key} {skin?.Doc.Id ?? "original"}");
        }
        finally
        {
            (_try, _trying) = (null, false);
            Revert();
            TryApply();
        }
    }

    // The game's gear behind a gear target key, from its own gear lists; null for an item key or gear not in them.
    public static GearIDRange? RangeOf(string key)
    {
        if (!key.StartsWith("gear:", StringComparison.Ordinal) || !uint.TryParse(key.AsSpan(5), out var gear)) return null;
        foreach (var slot in GearSlots)
            if (GearManager.GetAllGearForSlot(slot) is { } all)
                foreach (var range in all)
                    if (range != null && Offline(range.PlayfabItemInstanceId) == gear) return range;
        return null;
    }
}
