using CellMenu;
using ForgeSkin.Format;
using ForgeUI;
using ForgeVisual;
using GameData;
using UnityEngine;

namespace ForgeSkin.Plugin;

// The skin picker, drawn by ForgeCore's UI in the lobby's loadout pop-up: the SKINS lobby button opens it.
// One slot per gear that has a loaded skin (a gear with several installed skins lists them all; the choice is kept per gear, and the
// skins for every gear stay on under it); the SKINS tab picks its skin (or the original look), with a picture of the gear in the
// card in focus (Previews), the COLOURS tab the
// player's colours of the skin it wears (the author's suggestions, a row of hues, and back to the skin's own colour), the ADJUST tab
// the numbers and colours the skin's code effects let the player change.
sealed class Picker : PickerSource
{
    static readonly Picker Instance = new();
    static UIKitMod _ui = null!;
    const int Hues = 12;

    readonly List<string> _keys = new(); // the slots: target keys with a loaded skin, read when the picker opens
    readonly List<string> _tabs = new();
    // The colours tab of the chosen slot: (player colour, its colour or null for the skin's own).
    readonly List<(PlayerColor Colour, float[]? Rgb)> _colours = new();

    public static void Register()
    {
        _ui = UIKit.Mod(ForgeSkinPlugin.Guid);
        _ui.AddLobbyButton("Skins", LobbyArea.Below, LobbyWidth.Half, UIText.Key("lobby.button"), Instance.Toggle);
    }

    // A pick or reload changed what the open picker shows.
    public static void Changed()
    {
        if (UIKit.IsShowing(Instance)) UIKit.RefreshPicker();
    }

    void Toggle(CM_PlayerLobbyBar bar)
    {
        if (!UIKit.IsShowing(this))
        {
            _keys.Clear();
            _keys.AddRange(Skins.Candidates.Keys.OrderBy(Name, StringComparer.CurrentCultureIgnoreCase));
        }
        UIKit.TogglePicker(bar, this);
    }

    static string T(string key, params object[] args) => _ui.Text(key, args);

    public override void Closed() => Previews.Cancel();

    public override string SlotsHeader => T("picker.slots");
    public override int SlotCount => _keys.Count;
    public override PickerCard? ClearCard => null;

    public override IReadOnlyList<string> Tabs
    {
        get
        {
            _tabs.Clear();
            _tabs.Add(T("picker.skins"));
            _tabs.Add(T("picker.colours"));
            _tabs.Add(T("picker.adjust"));
            return _tabs;
        }
    }

    // The game's own name of the gear behind a target key (its gear or item block), else the key.
    static string Name(string key)
    {
        if (SkinnedGear.RangeOf(key) is { } range) return range.PublicGearName;
        if (key.StartsWith("item:", StringComparison.Ordinal) && uint.TryParse(key.AsSpan(5), out var item) && ItemDataBlock.GetBlock(item) is { } block)
            return block.publicName;
        return key;
    }

    string? Key(int slot) => slot >= 0 && slot < _keys.Count ? _keys[slot] : null;
    static List<Skin> SkinsOf(string key) => Skins.Candidates.GetValueOrDefault(key) ?? new();
    static Skin? Worn(string key) => Skins.Targets.TryGetValue(key, out var id) ? Skins.ById.GetValueOrDefault(id) : null;
    static string Title(Skin skin) => skin.Doc.Name.Length > 0 ? skin.Doc.Name : skin.Doc.Id;

    // How many code-effect values the skin's author lets the player change (the ADJUST tab's rows).
    static int Tweaks(Skin skin) => (skin.Doc.CodeEffects ?? new()).Sum(e => e.PlayerTweaks is { } tw ? tw.Params.Count + tw.Colors.Count + tw.Transform.Count : 0);

    // The category line of a skin's card: its author, and that it can be adjusted.
    static string Subtitle(Skin skin) =>
        string.Join(" · ", new[] { skin.Doc.Author, Tweaks(skin) > 0 ? T("picker.adjustable") : "" }.Where(s => s.Length > 0));

    // A slot shows the gear's own icon as the game drew it, in the skin it wears (ForgeSkin redraws it when that changes).
    public override PickerCard SlotCard(int slot, bool chosen)
    {
        if (Key(slot) is not { } key) return new PickerCard();
        UnityEngine.Sprite? icon = null;
        if (SkinnedGear.RangeOf(key) is { } range && GearIconRendering.TryGetGearIconSprite(range.GetChecksum(), out var sprite)) icon = sprite;
        return new PickerCard { Title = Name(key), Icon = icon };
    }

    public override IReadOnlyList<PickerCard> Entries(int tab) => Array.Empty<PickerCard>();

    public override IReadOnlyList<PickerCard> Entries(int slot, int tab)
    {
        if (Key(slot) is not { } key) return Array.Empty<PickerCard>();
        var worn = Worn(key);
        if (tab == Adjust) return Array.Empty<PickerCard>();
        if (tab == 0)
        {
            // Every card asks for its picture (Previews makes them one gear model at a time; the picker is refreshed as each arrives).
            var cards = new List<PickerCard> { new() { Title = T("picker.original"), Equipped = worn == null ? 1 : 0, Icon = Previews.Ask(key, null, out _, out _) } };
            foreach (var skin in SkinsOf(key))
                cards.Add(new PickerCard { Title = Title(skin), Subtitle = Subtitle(skin), Equipped = skin == worn ? 1 : 0, Icon = Previews.Ask(key, skin, out _, out _) });
            return cards;
        }
        Colours(worn);
        var picks = worn == null ? null : Skins.ColoursOf(worn.Doc.Id);
        return _colours.Select(c =>
        {
            var current = picks?.GetValueOrDefault(c.Colour.Id) ?? c.Colour.Default;
            var shown = c.Rgb ?? c.Colour.Default;
            return new PickerCard
            {
                Title = c.Colour.Name,
                Subtitle = c.Rgb == null ? T("picker.default") : c.Colour.Palette?.Any(p => Same(p, c.Rgb)) == true ? T("picker.suggested") : T("picker.more"),
                Accent = Display(shown),
                Swatch = Display(shown),
                Equipped = (c.Rgb == null ? picks?.ContainsKey(c.Colour.Id) != true : Same(current, c.Rgb)) ? 1 : 0,
            };
        }).ToList();
    }

    // The colours tab of `worn`: per player colour its default, the author's palette, then a row of hues at the default's
    // saturation and brightness when the author offered no palette.
    void Colours(Skin? worn)
    {
        _colours.Clear();
        foreach (var c in worn?.Doc.PlayerColors ?? new())
        {
            _colours.Add((c, null));
            if (c.Palette is { Count: > 0 } palette) { foreach (var p in palette) _colours.Add((c, p)); continue; }
            Color.RGBToHSV(Display(c.Default), out _, out var s, out var v);
            for (int h = 0; h < Hues; h++)
            {
                var hue = Color.HSVToRGB(h / (float)Hues, Math.Max(s, 0.6f), Math.Max(v, 0.5f)).linear;
                _colours.Add((c, new[] { hue.r, hue.g, hue.b }));
            }
        }
    }

    static bool Same(float[] a, float[] b) => Math.Abs(a[0] - b[0]) < 1e-4f && Math.Abs(a[1] - b[1]) < 1e-4f && Math.Abs(a[2] - b[2]) < 1e-4f;

    // Linear RGB as the screen shows it.
    static Color Display(float[] rgb) => new Color(rgb[0], rgb[1], rgb[2]).gamma;

    // What the player should know about all skins, under the skins tab's details: the game's texture quality below full draws every
    // mipmapped skin texture at half or a quarter, and the packs the last reload could not read.
    static string WithNotes(string body)
    {
        var notes = new List<string>();
        if (body.Length > 0) notes.Add(body);
        if (QualitySettings.masterTextureLimit > 0)
            notes.Add(MenuText.Tint(T("picker.quality", 1 << QualitySettings.masterTextureLimit), MenuTone.Warn));
        foreach (var (file, reason) in Skins.Failed) notes.Add(MenuText.Tint(T("picker.failed", file, reason), MenuTone.Alert));
        return string.Join("\n\n", notes);
    }

    public override PickerInfo Info(int slot, int tab, int entry)
    {
        if (Key(slot) is not { } key) return new PickerInfo { Body = WithNotes(T("picker.none")) };
        var worn = Worn(key);
        if (tab == 0)
        {
            var skins = SkinsOf(key);
            var skin = entry > 0 && entry <= skins.Count ? skins[entry - 1] : entry == 0 ? null : worn;
            // Until the picture is made the box says so (the picker is refreshed when it is); one that could not be made says why.
            var picture = Previews.Ask(key, skin, out var coming, out var why);
            var waiting = coming ? T("picker.previewLoading") : why.Length > 0 ? MenuText.Tint(T("picker.previewFailed", T(why)), MenuTone.Warn) : "";
            if (skin == null)
            {
                // Skins for every gear (not chosen here) stay on under the original look.
                var globals = Skins.Globals.Count(Skins.ById.ContainsKey);
                return new PickerInfo
                {
                    Title = T("picker.original"),
                    Subtitle = Name(key),
                    Icon = picture,
                    Body = WithNotes(string.Join("\n", new[] { T("picker.originalBody"), globals > 0 ? T("picker.globalsStay", globals) : "", waiting }.Where(l => l.Length > 0))),
                };
            }
            var colours = skin.Doc.PlayerColors?.Count ?? 0;
            var tweaks = Tweaks(skin);
            return new PickerInfo
            {
                Title = Title(skin),
                Subtitle = Name(key),
                Icon = picture,
                Body = WithNotes(string.Join("\n", new[]
                {
                    skin.Doc.Author.Length > 0 ? T("picker.by", skin.Doc.Author) : "", colours > 0 ? T("picker.colourCount", colours) : "",
                    tweaks > 0 ? T("picker.tweakCount", tweaks) : "", waiting,
                }.Where(l => l.Length > 0))),
            };
        }
        if (worn == null) return new PickerInfo { Subtitle = Name(key), Body = T("picker.pickSkinFirst") };
        if (tab == Adjust) return AdjustInfo(worn, key, entry);
        Colours(worn);
        if (entry < 0 || entry >= _colours.Count)
            return new PickerInfo { Title = Title(worn), Subtitle = Name(key), Body = _colours.Count == 0 ? T("picker.noColours") : T("picker.colourCount", worn.Doc.PlayerColors!.Count) };
        var (colour, rgb) = _colours[entry];
        // The colour large, under the picture of the gear in the skin it wears when one was made.
        return new PickerInfo
        {
            Title = colour.Name,
            Subtitle = Title(worn),
            Accent = Display(rgb ?? colour.Default),
            Swatch = Display(rgb ?? colour.Default),
            Icon = Previews.Of(key, worn),
            Body = T("picker.colourBody", colour.Name),
        };
    }

    public override string? Pick(int slot, int tab, int entry)
    {
        if (Key(slot) is not { } key) return null;
        if (tab == 0)
        {
            var skins = SkinsOf(key);
            Skins.Choose(key, entry > 0 && entry <= skins.Count ? skins[entry - 1].Doc.Id : "");
            return null;
        }
        if (Worn(key) is not { } worn) return T("picker.pickSkinFirst");
        if (tab == Adjust) return null;
        Colours(worn);
        if (entry < 0 || entry >= _colours.Count) return null;
        var (colour, rgb) = _colours[entry];
        Skins.Colour(worn.Doc.Id, colour.Id, rgb);
        return null;
    }

    // ---- the ADJUST tab: what the skin's author lets the player change on its code effects (CodeEffect.PlayerTweaks) ----

    const int Adjust = 2;
    readonly List<UIControl> _controls = new();
    readonly CodeEffectValues _values = new();

    // The setting first, then its effect: a row too narrow for both cuts the effect's name, not what the row changes.
    static string Label(CodeEffect e, ProceduralLabel l) => $"{Setting(l)} · {Effect(e)}";
    static string Setting(ProceduralLabel l) => UIKit.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? l.Zh : l.En;
    static string Effect(CodeEffect e) => e.Name.Length > 0 ? e.Name : e.Id;

    // One slider per number and one row of swatches per colour; every change goes to Skins.Tweak (kept per skin in
    // ForgeSkin.colours.json, never in the pack), and the effect in hand follows at once.
    public override IReadOnlyList<UIControl>? Controls(int slot, int tab)
    {
        if (tab != Adjust || Key(slot) is not { } key || Worn(key) is not { } worn) return null;
        _controls.Clear();
        var id = worn.Doc.Id;
        foreach (var e in worn.Doc.CodeEffects ?? new())
        {
            if (e.PlayerTweaks is not { } tw || CodeEffects.Catalog.Find(e.Program) is not { } program) continue;
            float Now(TweakKind kind, int index)
            {
                CodeEffects.Resolve(e, Skins.ColoursOf(id), _values);
                return kind == TweakKind.Param ? _values.Params[index] : _values.Transform[index];
            }
            foreach (var p in program.Params)
                if (tw.Params.Contains(p.Name))
                    _controls.Add(new UISlider(Label(e, p.Label), p.Min, p.Max, p.Step, () => Now(TweakKind.Param, p.Index),
                        v => Skins.Tweak(id, CodeEffects.TweakKey(e.Id, TweakKind.Param, p.Name), new[] { v })));
            foreach (var t in CodeEffects.Catalog.Transform)
                if (tw.Transform.Contains(t.Name))
                    _controls.Add(new UISlider(Label(e, t.Label), t.Min, t.Max, t.Step, () => Now(TweakKind.Transform, t.Index),
                        v => Skins.Tweak(id, CodeEffects.TweakKey(e.Id, TweakKind.Transform, t.Name), new[] { v })));
            foreach (var c in program.Colours)
            {
                if (!tw.Colors.Contains(c.Name)) continue;
                // The skin's own colour first, then a row of hues at its saturation and brightness.
                var own = e.Colors.TryGetValue(c.Name, out var o) ? o : c.Default.ToArray();
                Color.RGBToHSV(new Color(own[0], own[1], own[2]), out _, out var s, out var v);
                var swatches = new List<Color> { new(own[0], own[1], own[2], 1) };
                for (int h = 0; h < Hues - 1; h++) swatches.Add(Color.HSVToRGB(h / (float)(Hues - 1), Math.Max(s, 0.6f), Math.Max(v, 0.5f)));
                var tweakKey = CodeEffects.TweakKey(e.Id, TweakKind.Color, c.Name);
                _controls.Add(new UISwatches(Label(e, c.Label), swatches, () =>
                {
                    if (Skins.ColoursOf(id)?.GetValueOrDefault(tweakKey) is not { Length: 4 } now) return 0;
                    for (int i = 1; i < swatches.Count; i++)
                        if (Math.Abs(swatches[i].r - now[0]) < 1e-3f && Math.Abs(swatches[i].g - now[1]) < 1e-3f && Math.Abs(swatches[i].b - now[2]) < 1e-3f) return i;
                    return 0;
                }, i => Skins.Tweak(id, tweakKey, i <= 0 ? null : new[] { swatches[i].r, swatches[i].g, swatches[i].b, own[3] })));
            }
        }
        return _controls.Count > 0 ? _controls : null;
    }

    PickerInfo AdjustInfo(Skin worn, string key, int entry)
    {
        // The rows Controls built last for this slot (the picker asks for them before their details).
        var rows = _controls;
        if (rows.Count == 0) return new PickerInfo { Title = Title(worn), Subtitle = Name(key), Body = T("picker.noTweaks") };
        if (entry < 0 || entry >= rows.Count) return new PickerInfo { Title = Title(worn), Subtitle = Name(key), Body = T("picker.tweakBody") };
        // The details title is the setting; the effect (and the skin) go on the category line above it (Label: "setting · effect").
        var label = rows[entry].Label;
        var cut = label.IndexOf(" · ", StringComparison.Ordinal);
        return cut < 0
            ? new PickerInfo { Title = label, Subtitle = Title(worn), Body = T("picker.tweakBody") }
            : new PickerInfo { Title = label[..cut], Subtitle = label[(cut + 3)..] + " · " + Title(worn), Body = T("picker.tweakBody") };
    }
}
