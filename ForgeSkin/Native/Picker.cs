using CellMenu;
using ForgeSkin.Format;
using ForgeUI;
using GameData;
using Gear;
using UnityEngine;

namespace ForgeSkin.Plugin;

// The skin picker, drawn by ForgeCore's UI in the lobby's loadout pop-up: the SKINS lobby button or the picker key opens it.
// One slot per gear that has a loaded skin; the SKINS tab picks its skin (or the original look), the COLOURS tab the
// player's colours of the skin it wears (the author's suggestions, a row of hues, and back to the skin's own colour).
sealed class Picker : PickerSource
{
    static readonly Picker Instance = new();
    static UIKitMod _ui = null!;
    static KeyAction? _key;
    const int Hues = 12;

    readonly List<string> _keys = new(); // the slots: target keys with a loaded skin, read when the picker opens
    readonly List<string> _tabs = new();
    // The colours tab of the chosen slot: (player colour, its colour or null for the skin's own).
    readonly List<(PlayerColor Colour, float[]? Rgb)> _colours = new();

    // `defaultKey`: the PickerKey of ForgeSkin.cfg, the key action's default (the player can rebind it in the game's Controls).
    public static void Register(string defaultKey)
    {
        _ui = UIKit.Mod(ForgeSkinPlugin.Guid);
        _ui.AddLobbyButton("Skins", UIText.Key("lobby.button"), Instance.Toggle);
        _key = _ui.AddKeyAction("Picker", "keys.picker", "keys.group", defaultKey, KeyMode.Press);
    }

    // Per frame: the key opens or closes the picker on the local player's lobby bar while the loadout page shows it.
    public static void Poll()
    {
        if (_key is not { Pressed: true }) return;
        var bars = CM_PageLoadout.Current?.m_playerLobbyBars;
        if (bars == null) return;
        for (int i = 0; i < bars.Length; i++)
            if (bars[i] is { } bar && bar.m_player != null && bar.m_player.IsLocal && bar.gameObject.activeInHierarchy) { Instance.Toggle(bar); return; }
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
            return _tabs;
        }
    }

    // The game's own name of the gear behind a target key (its gear or item block), else the key.
    static string Name(string key)
    {
        if (key.StartsWith("gear:", StringComparison.Ordinal) && uint.TryParse(key.AsSpan(5), out var gear))
            foreach (var slot in SkinnedGear.GearSlots)
                if (GearManager.GetAllGearForSlot(slot) is { } all)
                    foreach (var range in all)
                        if (range != null && SkinnedGear.Offline(range.PlayfabItemInstanceId) == gear) return range.PublicGearName;
        if (key.StartsWith("item:", StringComparison.Ordinal) && uint.TryParse(key.AsSpan(5), out var item) && ItemDataBlock.GetBlock(item) is { } block)
            return block.publicName;
        return key;
    }

    string? Key(int slot) => slot >= 0 && slot < _keys.Count ? _keys[slot] : null;
    static List<Skin> SkinsOf(string key) => Skins.Candidates.GetValueOrDefault(key) ?? new();
    static Skin? Worn(string key) => Skins.Targets.TryGetValue(key, out var id) ? Skins.ById.GetValueOrDefault(id) : null;
    static string Title(Skin skin) => skin.Doc.Name.Length > 0 ? skin.Doc.Name : skin.Doc.Id;

    public override PickerCard SlotCard(int slot, bool chosen) => new() { Title = Key(slot) is { } key ? Name(key) : "" };

    public override IReadOnlyList<PickerCard> Entries(int tab) => Array.Empty<PickerCard>();

    public override IReadOnlyList<PickerCard> Entries(int slot, int tab)
    {
        if (Key(slot) is not { } key) return Array.Empty<PickerCard>();
        var worn = Worn(key);
        if (tab == 0)
        {
            var cards = new List<PickerCard> { new() { Title = T("picker.original"), Equipped = worn == null ? 1 : 0 } };
            foreach (var skin in SkinsOf(key)) cards.Add(new PickerCard { Title = Title(skin), Subtitle = skin.Doc.Author, Equipped = skin == worn ? 1 : 0 });
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

    public override PickerInfo Info(int slot, int tab, int entry)
    {
        if (Key(slot) is not { } key) return new PickerInfo { Body = T("picker.none") };
        var worn = Worn(key);
        if (tab == 0)
        {
            var skins = SkinsOf(key);
            var skin = entry > 0 && entry <= skins.Count ? skins[entry - 1] : entry == 0 ? null : worn;
            if (skin == null) return new PickerInfo { Title = T("picker.original"), Subtitle = Name(key), Body = T("picker.originalBody") };
            var colours = skin.Doc.PlayerColors?.Count ?? 0;
            return new PickerInfo
            {
                Title = Title(skin),
                Subtitle = Name(key),
                Body = string.Join("\n", new[] { skin.Doc.Author.Length > 0 ? T("picker.by", skin.Doc.Author) : "", colours > 0 ? T("picker.colourCount", colours) : "" }.Where(l => l.Length > 0)),
            };
        }
        if (worn == null) return new PickerInfo { Subtitle = Name(key), Body = T("picker.pickSkinFirst") };
        Colours(worn);
        if (entry < 0 || entry >= _colours.Count)
            return new PickerInfo { Title = Title(worn), Subtitle = Name(key), Body = _colours.Count == 0 ? T("picker.noColours") : T("picker.colourCount", worn.Doc.PlayerColors!.Count) };
        var (colour, rgb) = _colours[entry];
        return new PickerInfo { Title = colour.Name, Subtitle = Title(worn), Accent = Display(rgb ?? colour.Default), Body = T("picker.colourBody", colour.Name) };
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
        Colours(worn);
        if (entry < 0 || entry >= _colours.Count) return null;
        var (colour, rgb) = _colours[entry];
        Skins.Colour(worn.Doc.Id, colour.Id, rgb);
        return null;
    }
}
