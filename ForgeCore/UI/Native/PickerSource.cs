using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>One card in the picker: a slot tile in the column on the left (its number, picture and title) or a row in the list (its
/// picture, title and category).</summary>
public sealed class PickerCard
{
    /// <summary>Large text, upper case to match the game. Card texts are shown as written (no rich text).</summary>
    public string Title = "";

    /// <summary>The category line under a list row's title; slots do not show it.</summary>
    public string Subtitle = "";

    /// <summary>The colour of the category mark and text on a row, and of a slot's edge. Pictures are always drawn in their own colours.</summary>
    public Color? Accent;

    /// <summary>On a list row: the slot (from 1) that holds this entry, shown as a numbered tag; 0 for none. On a grid card (and on a slot
    /// of a source without <see cref="PickerSource.NumberSlots"/>) any value above 0 marks it as in use: an amber bar down its left edge and
    /// a tick in its top right corner.</summary>
    public int Equipped;

    /// <summary>The card's picture; null shows none.</summary>
    public Sprite? Icon;

    /// <summary>A colour the card stands for (a colour choice), shown as a filled tile in the picture's place when there is no
    /// <see cref="Icon"/>; null for none.</summary>
    public Color? Swatch;
}

/// <summary>How the list shows its cards.</summary>
public enum PickerLayout
{
    /// <summary>One row per card: the picture at the left, the title and category beside it.</summary>
    Rows,

    /// <summary>Two cards side by side, each twice a row's height: the picture large on top, the title and category under it, each one
    /// line, cut with an ellipsis.</summary>
    Grid
}

/// <summary>The side panel under the details: what the thing in the chosen slot is wearing can be changed here, and a card under the
/// cursor shows its own (<see cref="PickerSource.Panel"/>). A section with nothing in it is not shown, title included.</summary>
public sealed class PickerPanel
{
    /// <summary>The heading over <see cref="Schemes"/>.</summary>
    public string SchemesTitle = "";

    /// <summary>A row of scheme cards (more than fit are paged with arrows beside the heading); a press calls
    /// <see cref="PickerSource.PickScheme"/>.</summary>
    public IReadOnlyList<PickerScheme> Schemes = Array.Empty<PickerScheme>();

    /// <summary>Controls in the schemes section, one row each right under the cards and not paged (such as one row of swatches per colour
    /// of a scheme the player makes); shown only with <see cref="Schemes"/>.</summary>
    public IReadOnlyList<UIControl> SchemeControls = Array.Empty<UIControl>();

    /// <summary>The heading over <see cref="Controls"/>.</summary>
    public string ControlsTitle = "";

    /// <summary>Controls, one row each (more than fit are paged with arrows beside the heading); the picker reads and writes their values
    /// itself, as on an adjust page.</summary>
    public IReadOnlyList<UIControl> Controls = Array.Empty<UIControl>();

    /// <summary>The scheme section's controls that are shown (none without schemes).</summary>
    internal int SchemeControlCount => Schemes.Count > 0 ? SchemeControls.Count : 0;

    /// <summary>Every control shown, numbered as presses and focus number them: the scheme section's first, then <see cref="Controls"/>.</summary>
    internal int ControlCount => SchemeControlCount + Controls.Count;

    internal UIControl Control(int i) => i < SchemeControlCount ? SchemeControls[i] : Controls[i - SchemeControlCount];
}

/// <summary>One card of <see cref="PickerPanel.Schemes"/>: its colours side by side over its name.</summary>
public sealed class PickerScheme
{
    public string Name = "";

    /// <summary>At most eight are shown.</summary>
    public IReadOnlyList<Color> Colours = Array.Empty<Color>();

    /// <summary>The scheme in use: framed in amber with a tick.</summary>
    public bool Chosen;
}

/// <summary>What was pressed, for <see cref="PickerSource.Clicked"/>.</summary>
public enum PickerClick
{
    /// <summary>A slot in the column on the left.</summary>
    Slot,

    /// <summary>A card in the list, including the clear card.</summary>
    Entry
}

/// <summary>What the details box on the right shows: the picture large, the category (in <see cref="Accent"/>), the title, the
/// badges as key caps, the body, then the readouts as a row of blocks along the bottom.</summary>
public sealed class PickerInfo
{
    public string Title = "";
    public string Subtitle = "";

    /// <summary>The colour of the category line and of the glow behind the picture.</summary>
    public Color? Accent;

    /// <summary>The description; TextMeshPro rich text is allowed.</summary>
    public string Body = "";

    public Sprite? Icon;

    /// <summary>A colour the entry stands for, shown large: filling the picture box when there is no <see cref="Icon"/>, else as a band
    /// along the bottom of the picture box under the picture; null for none.</summary>
    public Color? Swatch;

    /// <summary>Sprites drawn on key caps in a row under the title (for example a key sequence made of
    /// <see cref="UIKitSprites.ArrowGlyph"/>). At most 12 are shown.</summary>
    public IReadOnlyList<Sprite> Badges = Array.Empty<Sprite>();

    /// <summary>Rows of a label and <see cref="PickerStat.Max"/> pips, the first <see cref="PickerStat.Value"/> lit, above the description.</summary>
    public IReadOnlyList<PickerStat> Stats = Array.Empty<PickerStat>();

    /// <summary>Key numbers as blocks along the bottom of the box (at most four): a small label, a large value and its unit, and a bar.</summary>
    public IReadOnlyList<PickerReadout> Readouts = Array.Empty<PickerReadout>();
}

/// <summary>One row of <see cref="PickerInfo.Stats"/>: a label (already in the player's language, like the other picker texts) and a
/// rating of <see cref="Value"/> out of <see cref="Max"/>.</summary>
public readonly struct PickerStat
{
    public PickerStat(string label, int value, int max = 5)
    {
        Label = label ?? "";
        Max = Math.Clamp(max, 1, 10);
        Value = Math.Clamp(value, 0, Max);
    }

    public string Label { get; }
    public int Value { get; }
    public int Max { get; }
}

/// <summary>One block of <see cref="PickerInfo.Readouts"/>: a label, a value with its unit, and a bar filled to <see cref="Fill"/>
/// (0 to 1, for example the value against the largest one in the list). Texts are already in the player's language.</summary>
public readonly struct PickerReadout
{
    public PickerReadout(string label, string value, string unit = "", float fill = 0f)
    {
        Label = label ?? "";
        Value = value ?? "";
        Unit = unit ?? "";
        Fill = Math.Clamp(fill, 0f, 1f);
        Endless = false;
    }

    private PickerReadout(string label)
    {
        Label = label ?? "";
        Value = Unit = "";
        Fill = 1f;
        Endless = true;
    }

    /// <summary>A readout without limit: the infinity sign and a full bar.</summary>
    public static PickerReadout Unlimited(string label) => new(label);

    public string Label { get; }
    public string Value { get; }
    public string Unit { get; }
    public float Fill { get; }

    /// <summary>True for <see cref="Unlimited"/>.</summary>
    public bool Endless { get; }
}

/// <summary>
/// The data behind a picker: a column of slots on the left, a list of entries in tabs, and a details box. Subclass it, then
/// call <see cref="UIKit.TogglePicker"/>. The picker asks again for everything it shows
/// after every pick, so return current values.
/// <para>Indices: <c>slot</c> counts from 0 to <see cref="SlotCount"/> − 1; <c>tab</c> indexes <see cref="Tabs"/>; <c>entry</c>
/// indexes <see cref="Entries(int, int)"/> of that tab, and −1 means the clear card (or nothing in the list is focused).</para>
/// </summary>
public abstract class PickerSource
{
    private readonly string[] _allTab = { "" };

    /// <summary>The heading over the slot column; by default the game's word for loadout in the current language.</summary>
    public virtual string SlotsHeader => UIKit.Kit.Text("picker.slots");

    public abstract int SlotCount { get; }

    /// <summary>What slot <paramref name="slot"/> holds; <paramref name="chosen"/> is true for the slot being filled (the picker marks it).</summary>
    public abstract PickerCard SlotCard(int slot, bool chosen);

    /// <summary>How the list shows its cards; by default one row per card.</summary>
    public virtual PickerLayout Layout => PickerLayout.Rows;

    /// <summary>Whether slot tiles show their number; without it a slot whose card has <see cref="PickerCard.Equipped"/> above 0 shows a tick.</summary>
    public virtual bool NumberSlots => true;

    /// <summary>Settings that apply to every slot, opened by a gear button in the title bar: while open they take the side panel's place.
    /// Null (the default) shows no gear.</summary>
    public virtual IReadOnlyList<UIControl>? Settings => null;

    /// <summary>The details over the side panel while <see cref="Settings"/> are open (a title, and what the settings do as the description).</summary>
    public virtual PickerInfo SettingsInfo => new();

    /// <summary>The side panel drawn under the details of <paramref name="entry"/> while <paramref name="slot"/> is chosen, or with
    /// <paramref name="entry"/> −1 of what the slot holds: it follows the details, so a card under the cursor shows its own panel. Null (the
    /// default) shows none.</summary>
    public virtual PickerPanel? Panel(int slot, int entry) => null;

    /// <summary>A press on scheme <paramref name="scheme"/> of the panel last drawn (<see cref="Panel"/>); return null when accepted, else the
    /// reason (shown in red).</summary>
    public virtual string? PickScheme(int slot, int scheme) => null;

    /// <summary>Tab names along the top of the list, at most five; by default one tab, ALL in the current language. With one tab there is no
    /// tab strip: its name heads a title bar instead.</summary>
    public virtual IReadOnlyList<string> Tabs
    {
        get
        {
            _allTab[0] = UIKit.Kit.Text("picker.all");
            return _allTab;
        }
    }

    public abstract IReadOnlyList<PickerCard> Entries(int tab);

    /// <summary>The cards of <paramref name="tab"/> while <paramref name="slot"/> is chosen, for a picker whose list depends on the slot (each
    /// slot a different thing with its own choices); by default <see cref="Entries(int)"/>, the same list for every slot. The picker asks
    /// this one.</summary>
    public virtual IReadOnlyList<PickerCard> Entries(int slot, int tab) => Entries(tab);

    /// <summary>An adjust page: the controls <paramref name="tab"/> shows while <paramref name="slot"/> is chosen, one row each, in place of cards
    /// (no clear card, <see cref="Pick"/> is not called for them; the picker reads and writes each control's value itself, and
    /// <see cref="Info"/> is still asked with the row's index as the entry). Null (the default) makes the tab an ordinary list of
    /// <see cref="Entries(int, int)"/>.</summary>
    public virtual IReadOnlyList<UIControl>? Controls(int slot, int tab) => null;

    /// <summary>The first card of every tab, which empties the chosen slot; null leaves it out.</summary>
    public virtual PickerCard? ClearCard => new() { Title = UIKit.Kit.Text("picker.clear") };

    /// <summary>The details for <paramref name="entry"/> of <paramref name="tab"/>, or with <paramref name="entry"/> −1 for what
    /// slot <paramref name="slot"/> holds. By default: the entry card's title, subtitle, accent and picture, and for −1 the slot card's.</summary>
    public virtual PickerInfo Info(int slot, int tab, int entry)
    {
        var entries = Entries(slot, tab);
        var card = entry >= 0 && entry < entries.Count ? entries[entry] : entry < 0 ? SlotCard(slot, true) : null;
        return card == null ? new PickerInfo() : new PickerInfo { Title = card.Title, Subtitle = card.Subtitle, Accent = card.Accent, Icon = card.Icon, Swatch = card.Swatch };
    }

    /// <summary>Puts <paramref name="entry"/> (−1: nothing) into <paramref name="slot"/>. Return null when it was accepted, or
    /// the reason it was refused; the reason is shown in red in the details box.</summary>
    public abstract string? Pick(int slot, int tab, int entry);

    /// <summary>The slot chosen when the picker opens.</summary>
    public virtual int FirstSlot() => 0;

    /// <summary>The slot chosen after an accepted pick into <paramref name="slot"/>.</summary>
    public virtual int SlotAfterPick(int slot) => slot;

    /// <summary>Called on every press of one of your slots or list cards, before the slot is chosen or <see cref="Pick"/> runs.
    /// The game plays no click sound for them; play your own here if you want one (the library has no audio).</summary>
    public virtual void Clicked(PickerClick click) { }

    /// <summary>Called once when the picker closes, however it closed.</summary>
    public virtual void Closed() { }
}
