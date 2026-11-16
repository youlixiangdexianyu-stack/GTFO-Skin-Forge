using CellMenu;
using ForgeSkin.Format;
using ForgeUI;
using ForgeVisual;
using GameData;
using UnityEngine;

namespace ForgeSkin.Plugin;

// The skin picker, drawn by ForgeCore's UI in the lobby's loadout pop-up: the SKINS lobby button opens it, one screen with no tabs.
// Left, one slot per gear that has a loaded skin (and the character): which thing. Middle, a grid of its looks, the original look first,
// each card with a picture of the thing in it (Previews): a press wears it (the choice is kept per gear, and the skins for every gear stay
// on under it). Right, the details and panel of the card under the cursor, else of the look worn: its colour schemes (the author's
// colours, each scheme the author added, and the player's own, which opens a row of swatches per colour) and what its code effects let
// the player change. ForgeSkin's own switches sit behind the title bar's gear.
sealed class Picker : PickerSource
{
    static readonly Picker Instance = new();
    static UIKitMod _ui = null!;
    const int Hues = 12;

    readonly List<string> _keys = new(); // the slots: target keys with a loaded skin, read when the picker opens
    readonly List<string> _tabs = new();
    // The skin whose own colours are open (its MINE scheme was pressed): a row of swatches per colour. Closed by another scheme or skin.
    Skin? _mine;

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
            // The character first (only with a character pack installed), then the gear by name.
            _keys.AddRange(Skins.Candidates.Keys.OrderBy(k => k != CharacterSkin.Target).ThenBy(Name, StringComparer.CurrentCultureIgnoreCase));
        }
        UIKit.TogglePicker(bar, this);
    }

    static string T(string key, params object[] args) => _ui.Text(key, args);

    public override void Closed()
    {
        Previews.Drop();
        _mine = _shown = null;
    }

    public override string SlotsHeader => T("picker.slots");
    public override int SlotCount => _keys.Count;
    public override PickerCard? ClearCard => null;
    public override PickerLayout Layout => PickerLayout.Grid;
    public override bool NumberSlots => false;

    public override IReadOnlyList<string> Tabs
    {
        get
        {
            _tabs.Clear();
            _tabs.Add(T("picker.title"));
            return _tabs;
        }
    }

    // The game's own name of the gear behind a target key (its gear or item block), else the key.
    static string Name(string key)
    {
        if (key == CharacterSkin.Target) return T("picker.character");
        if (SkinnedGear.RangeOf(key) is { } range) return range.PublicGearName;
        if (key.StartsWith("item:", StringComparison.Ordinal) && uint.TryParse(key.AsSpan(5), out var item) && ItemDataBlock.GetBlock(item) is { } block)
            return block.publicName;
        return key;
    }

    string? Key(int slot) => slot >= 0 && slot < _keys.Count ? _keys[slot] : null;
    static List<Skin> SkinsOf(string key) => Skins.Candidates.GetValueOrDefault(key) ?? new();
    static Skin? Worn(string key) => Skins.Targets.TryGetValue(key, out var id) ? Skins.ById.GetValueOrDefault(id) : null;
    static string Title(Skin skin) => skin.Doc.Name.Length > 0 ? skin.Doc.Name : skin.Doc.Id;

    // A card's line under the name: the author.
    static string Author(Skin skin) => skin.Doc.Author.Length > 0 ? T("picker.by", skin.Doc.Author) : "";

    // A gear slot shows the game's own loadout icon of the gear (it wears what the gear wears); the CHARACTER slot shows the default
    // avatar icon. A tick while it wears a skin.
    public override PickerCard SlotCard(int slot, bool chosen)
    {
        if (Key(slot) is not { } key) return new PickerCard();
        var card = new PickerCard { Title = Name(key), Equipped = Worn(key) != null ? 1 : 0 };
        if (key == CharacterSkin.Target)
        {
            card.Icon = CharacterIcon();
            return card;
        }
        card.Icon = Previews.GameIcon(key);
        return card;
    }

    // The CHARACTER slot's icon, carried in the assembly (rendered from Native/character-slot.svg); read once.
    static byte[]? _characterIcon;

    static UnityEngine.Sprite? CharacterIcon()
    {
        if (_characterIcon == null)
        {
            using var stream = typeof(Picker).Assembly.GetManifestResourceStream("ForgeSkin.character-slot.png")!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            _characterIcon = bytes.ToArray();
        }
        return UIKit.LoadIcon(_characterIcon, "ForgeSkin.character-slot");
    }

    public override IReadOnlyList<PickerCard> Entries(int tab) => Array.Empty<PickerCard>();

    public override IReadOnlyList<PickerCard> Entries(int slot, int tab)
    {
        if (Key(slot) is not { } key) return Array.Empty<PickerCard>();
        var worn = Worn(key);
        // Each skin's card shows its pack's picture (read in the background; the picker is refreshed as each arrives).
        var cards = new List<PickerCard> { new() { Title = T("picker.original"), Equipped = worn == null ? 1 : 0, Icon = Picture(key, null) } };
        foreach (var skin in SkinsOf(key))
            cards.Add(new PickerCard { Title = Title(skin), Subtitle = Author(skin), Equipped = skin == worn ? 1 : 0, Icon = Picture(key, skin) });
        return cards;
    }

    // The picture of skin (null: the original look): its pack's own; a pack without one, and the original person, the avatar icon; the
    // original gear the game's own icon while it wears nothing of the picker's (else none: that icon shows the skin worn).
    static UnityEngine.Sprite? Picture(string key, Skin? skin)
    {
        if (skin != null && Previews.Of(skin, out _) is { } picture) return picture;
        if (key == CharacterSkin.Target) return CharacterIcon();
        return skin == null && Worn(key) == null ? Previews.GameIcon(key) : null;
    }

    // ---- the panel: colour schemes and what the skin's code effects let the player change ----

    readonly List<PickerScheme> _schemes = new();
    readonly List<UIControl> _colourRows = new(); // MINE open: one row of swatches per player colour, under the scheme cards
    readonly List<UIControl> _controls = new();
    readonly CodeEffectValues _values = new();

    // The colour of `c` in the skin as the player wears it.
    static float[] Current(PlayerColor c, IReadOnlyDictionary<string, float[]>? picks) => picks?.GetValueOrDefault(c.Id) ?? c.Default;

    // The colour schemes of `worn`, in order: the author's own colours (every default), each scheme the author added (a colour it leaves
    // out keeps its default), then MINE, the player's own: chosen when the colours worn match no other scheme. A character takes no
    // player colours, so it has none.
    List<ColorScheme> SchemesOf(Skin worn, string key)
    {
        var list = new List<ColorScheme>();
        if (key == CharacterSkin.Target || worn.Doc.PlayerColors is not { Count: > 0 }) return list;
        list.Add(new ColorScheme { Name = T("picker.schemeOriginal") });
        list.AddRange(worn.Doc.ColorSchemes ?? new());
        return list;
    }

    static float[] InScheme(ColorScheme scheme, PlayerColor c) => scheme.Colors.GetValueOrDefault(c.Id) is { Length: >= 3 } rgb ? rgb : c.Default;

    // The skin whose panel was drawn last (the card under the cursor, else the look worn): scheme presses colour it.
    Skin? _shown;

    // The skin of card `entry` (0 the original look: none), or with -1 the look worn.
    static Skin? Of(string key, int entry)
    {
        var skins = SkinsOf(key);
        return entry > 0 && entry <= skins.Count ? skins[entry - 1] : entry == 0 ? null : Worn(key);
    }

    // The panel follows the details: the colour schemes of the card under the cursor (none for the original look), else of the look worn.
    public override PickerPanel? Panel(int slot, int entry)
    {
        _shown = null;
        if (Key(slot) is not { } key) return null;
        _schemes.Clear();
        _colourRows.Clear();
        _controls.Clear();
        var panel = new PickerPanel
        {
            SchemesTitle = T("picker.colours"),
            ControlsTitle = T("picker.adjust"),
            Schemes = _schemes,
            SchemeControls = _colourRows,
            Controls = _controls,
        };
        if (Of(key, entry) is not { } skin) return panel;
        _shown = skin;
        var mine = _mine == skin;
        var id = skin.Doc.Id;
        var colours = skin.Doc.PlayerColors ?? new();
        var picks = Skins.ColoursOf(id);
        var any = false;
        foreach (var scheme in SchemesOf(skin, key))
        {
            var chosen = colours.TrueForAll(c => Same(Current(c, picks), InScheme(scheme, c)));
            any |= chosen;
            _schemes.Add(new PickerScheme { Name = scheme.Name, Colours = colours.Select(c => Display(InScheme(scheme, c))).ToList(), Chosen = chosen && !mine });
        }
        if (_schemes.Count > 0)
        {
            _schemes.Add(new PickerScheme { Name = T("picker.schemeMine"), Colours = colours.Select(c => Display(Current(c, picks))).ToList(), Chosen = mine || !any });
            if (mine) foreach (var c in colours) _colourRows.Add(ColourRow(id, c));
        }
        Tweaks(skin, _controls);
        return panel;
    }

    // One row of swatches for player colour `c`: its default, then hues at the default's saturation and brightness.
    static UIControl ColourRow(string id, PlayerColor c)
    {
        Color.RGBToHSV(Display(c.Default), out _, out var s, out var v);
        var swatches = new List<Color> { Display(c.Default) };
        for (int h = 0; h < Hues - 1; h++) swatches.Add(Color.HSVToRGB(h / (float)(Hues - 1), Math.Max(s, 0.6f), Math.Max(v, 0.5f)));
        return new UISwatches(c.Name, swatches, () =>
        {
            var now = Current(c, Skins.ColoursOf(id));
            for (int i = 0; i < swatches.Count; i++)
                if (Math.Abs(swatches[i].r - now[0]) < 1e-3f && Math.Abs(swatches[i].g - now[1]) < 1e-3f && Math.Abs(swatches[i].b - now[2]) < 1e-3f) return i;
            return -1;
        }, i => Skins.Colour(id, c.Id, i <= 0 ? null : new[] { swatches[i].r, swatches[i].g, swatches[i].b }));
    }

    public override string? PickScheme(int slot, int scheme)
    {
        if (!Skins.Enabled) return T("picker.off");
        if (Key(slot) is not { } key || _shown is not { } skin) return null;
        var schemes = SchemesOf(skin, key);
        if (scheme >= schemes.Count) { _mine = skin; return null; }
        if (scheme < 0) return null;
        _mine = null;
        foreach (var c in skin.Doc.PlayerColors ?? new())
        {
            var rgb = InScheme(schemes[scheme], c);
            Skins.Colour(skin.Doc.Id, c.Id, Same(rgb, c.Default) ? null : rgb);
        }
        return null;
    }

    static bool Same(float[] a, float[] b) => Math.Abs(a[0] - b[0]) < 1e-4f && Math.Abs(a[1] - b[1]) < 1e-4f && Math.Abs(a[2] - b[2]) < 1e-4f;

    // A skin colour (sRGB, as the screen shows it) as a UI colour.
    static Color Display(float[] rgb) => new Color(rgb[0], rgb[1], rgb[2]);

    // The old skins folder (ForgeSkin 1.1.0 and earlier) when skin packs are left in it, unloaded; set at launch. TEMPORARY with Core/OldSkinFolder.cs.
    public static string? OldFolder;

    // What the player should know about all skins, under the skins tab's details: the game's texture quality below full draws every
    // mipmapped skin texture at half or a quarter, and the packs the last reload could not read.
    static string WithNotes(string body)
    {
        var notes = new List<string>();
        if (body.Length > 0) notes.Add(body);
        if (QualitySettings.masterTextureLimit > 0)
            notes.Add(MenuText.Tint(T("picker.quality", 1 << QualitySettings.masterTextureLimit), MenuTone.Warn));
        if (!Skins.Enabled) notes.Add(MenuText.Tint(T("picker.off"), MenuTone.Alert));
        if (OldFolder != null) notes.Add(MenuText.Tint(T("picker.oldFolder", OldFolder), MenuTone.Warn));
        foreach (var (file, reason) in Skins.Failed) notes.Add(MenuText.Tint(T("picker.failed", file, T(reason)), MenuTone.Alert));
        // Teammates' gear shown in its original look here, and what to install to see it.
        foreach (var (player, gear, pack, skin) in Looks.Unshown())
        {
            var install = pack.Length > 0 ? T("picker.teammateInstall", pack) : "";
            notes.Add(MenuText.Tint(skin == null ? T("picker.teammateMissing", player, Name(gear), install) : T("picker.teammateMismatch", player, Name(gear), skin, install), MenuTone.Warn));
        }
        return string.Join("\n\n", notes);
    }

    // The details of the card under the cursor (or, with none, of the look worn): its picture, name and author over the panel, then what
    // the player should know (Notes).
    public override PickerInfo Info(int slot, int tab, int entry)
    {
        if (Key(slot) is not { } key) return new PickerInfo { Body = WithNotes(T("picker.none")) };
        var skin = Of(key, entry);
        var picture = Picture(key, skin);
        // A pack without a picture of its own was packed by an older editor. The picture is the author's: picked colours show in the lobby.
        var coming = false;
        var old = skin != null && Previews.Of(skin, out coming) == null && !coming;
        var waiting = string.Join("\n", new[]
        {
            coming ? T("picker.previewLoading") : old ? MenuText.Tint(T("picker.previewOld"), MenuTone.Warn) : "",
            skin?.Doc.PlayerColors?.Count > 0 ? T("picker.coloursInLobby") : "",
        }.Where(l => l.Length > 0));
        if (skin == null)
        {
            // Skins for every gear (not chosen here) stay on under the original look.
            var globals = Skins.Globals.Count(Skins.ById.ContainsKey);
            return new PickerInfo
            {
                Title = T("picker.original"),
                Subtitle = Name(key),
                Icon = picture,
                Body = WithNotes(string.Join("\n", new[] { T(key == CharacterSkin.Target ? "picker.originalPerson" : "picker.originalBody"), globals > 0 ? T("picker.globalsStay", globals) : "", waiting }.Where(l => l.Length > 0))),
            };
        }
        return new PickerInfo { Title = Title(skin), Subtitle = skin.Doc.Author.Length > 0 ? Author(skin) : Name(key), Icon = picture, Body = WithNotes(waiting) };
    }

    public override string? Pick(int slot, int tab, int entry)
    {
        if (Key(slot) is not { } key) return null;
        if (!Skins.Enabled) return T("picker.off");
        var skins = SkinsOf(key);
        _mine = null;
        Skins.Choose(key, entry > 0 && entry <= skins.Count ? skins[entry - 1].Doc.Id : "");
        return null;
    }

    // ---- what the skin's author lets the player change on its code effects (CodeEffect.PlayerTweaks) ----

    // The setting first, then its effect: a row too narrow for both cuts the effect's name, not what the row changes.
    static string Label(CodeEffect e, ProceduralLabel l) => $"{Setting(l)} · {Effect(e)}";
    static string Setting(ProceduralLabel l) => UIKit.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? l.Zh : l.En;
    static string Effect(CodeEffect e) => e.Name.Length > 0 ? e.Name : e.Id;

    // One slider per number and one row of swatches per colour of `worn`'s code effects, added to `controls`; every change goes to
    // Skins.Tweak (kept per skin in ForgeSkin.colours.json, never in the pack), and the effect in hand follows at once.
    void Tweaks(Skin worn, List<UIControl> controls)
    {
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
                    controls.Add(new UISlider(Label(e, p.Label), p.Min, p.Max, p.Step, () => Now(TweakKind.Param, p.Index),
                        v => Skins.Tweak(id, CodeEffects.TweakKey(e.Id, TweakKind.Param, p.Name), new[] { v })));
            foreach (var t in CodeEffects.Catalog.Transform)
                if (tw.Transform.Contains(t.Name))
                    controls.Add(new UISlider(Label(e, t.Label), t.Min, t.Max, t.Step, () => Now(TweakKind.Transform, t.Index),
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
                controls.Add(new UISwatches(Label(e, c.Label), swatches, () =>
                {
                    if (Skins.ColoursOf(id)?.GetValueOrDefault(tweakKey) is not { Length: 4 } now) return 0;
                    for (int i = 1; i < swatches.Count; i++)
                        if (Math.Abs(swatches[i].r - now[0]) < 1e-3f && Math.Abs(swatches[i].g - now[1]) < 1e-3f && Math.Abs(swatches[i].b - now[2]) < 1e-3f) return i;
                    return 0;
                }, i => Skins.Tweak(id, tweakKey, i <= 0 ? null : new[] { swatches[i].r, swatches[i].g, swatches[i].b, own[3] })));
            }
        }
    }

    // ---- ForgeSkin's own switches, behind the title bar's gear: (lang key, its entry in ForgeSkin.cfg); Development is shown on no
    // screen. Each change applies at once and is saved (ForgeSkinPlugin binds what it does). ----

    static readonly (string Key, Func<BepInEx.Configuration.ConfigEntry<bool>> Entry)[] PluginSettings =
    {
        ("settings.enabled", () => ForgeSkinPlugin.Enabled),
        ("settings.hologram", () => ForgeSkinPlugin.HologramShader),
    };

    readonly List<UIControl> _settings = new();

    public override IReadOnlyList<UIControl> Settings
    {
        get
        {
            _settings.Clear();
            foreach (var (label, entry) in PluginSettings)
                _settings.Add(new UIToggle(T(label), () => entry().Value, v => entry().Value = v));
            _settings.Add(new UIChoice(T("settings.toon"), new[] { T("settings.toonForward"), T("settings.toonDeferred"), T("settings.toonOff") },
                () => (int)ForgeSkinPlugin.Toon.Value, v => ForgeSkinPlugin.Toon.Value = (ToonMode)v));
            return _settings;
        }
    }

    // What each switch does, under the settings' title; no picture, so the text has the room.
    public override PickerInfo SettingsInfo => new()
    {
        Title = T("settings.all"),
        Body = string.Join("\n\n", PluginSettings.Select(s => T(s.Key + "Body")).Append(T("settings.toonBody"))),
    };
}
