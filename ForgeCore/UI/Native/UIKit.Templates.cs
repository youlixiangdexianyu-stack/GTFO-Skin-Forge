using System;
using TMPro;
using UnityEngine;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>How far HUD regions at the screen edge stay from it, in HUD units (the player's <c>Hud / SafeMargin</c> setting).</summary>
    public static float SafeMargin => Plugin.HudSafeMargin;
}

/// <summary>Ready-made HUD templates and pages. Each template call registers a HUD element (see <see cref="AddHud"/>) in the slot given,
/// or its default slot, and returns the template to drive; null (logged) when the name cannot be used or is taken.</summary>
public sealed partial class UIKitMod
{
    /// <summary>An ability slot (see <see cref="HudSkillSlot"/>). <paramref name="remaining"/> is read every frame and returns the seconds
    /// left (0 when ready); <paramref name="cooldown"/> is the full length. Default slot: bottom left.</summary>
    /// <example><c>ui.AddSkillSlot("Dash", () =&gt; dash.Left, 8f, UIKitKeys.Name(KeyCode.Q)).Label = "DASH";</c></example>
    public HudSkillSlot? AddSkillSlot(string name, Func<float> remaining, float cooldown, UIText? key = null, HudSlot? slot = null)
    {
        if (remaining == null) throw new ArgumentNullException(nameof(remaining));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudSkillSlot(remaining, cooldown, key ?? UIText.Empty), slot ?? new HudSlot(HudRegion.BottomLeft, 20, 98f, 136f));
    }

    /// <summary>A key prompt for a keyboard or mouse key (named the way the game names it). Default slot: under the weapon list.</summary>
    /// <example><c>var revive = ui.AddKeyHint("Revive", KeyCode.F, "REVIVE"); revive.HoldSeconds = 1.2f;</c></example>
    public HudKeyHint? AddKeyHint(string name, KeyCode key, UIText label, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudKeyHint(() => UIKitKeys.Name(key), label ?? UIText.Empty), slot ?? new HudSlot(HudRegion.WeaponList, 0, 220f, 30f));
    }

    /// <summary>A key prompt for a game action, showing whatever the player has bound to it. Default slot: under the weapon list.</summary>
    /// <example><c>ui.AddKeyHint("Reload", InputAction.Reload, "RELOAD");</c></example>
    public HudKeyHint? AddKeyHint(string name, InputAction action, UIText label, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudKeyHint(() => UIKitKeys.Binding(action, action.ToString()), label ?? UIText.Empty), slot ?? new HudSlot(HudRegion.WeaponList, 0, 220f, 30f));
    }

    /// <summary>A key prompt for one of your key actions (<see cref="AddKeyAction"/>), showing the key the player gave it. Default slot:
    /// under the weapon list.</summary>
    public HudKeyHint? AddKeyHint(string name, KeyAction action, UIText label, HudSlot? slot = null)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudKeyHint(() => action.Name, label ?? UIText.Empty), slot ?? new HudSlot(HudRegion.WeaponList, 0, 220f, 30f));
    }

    /// <summary>A line of mixed words and key boxes ("Release [F] to cancel"); give the parts with <see cref="HudHintLine.Set"/>.
    /// Default slot: under the weapon list. To put the same line inside an element of your own, use <see cref="HintRow"/>.</summary>
    /// <example><c>ui.AddHintLine("Hint").Set(HintPart.Word("Hold"), HintPart.Key("F"), HintPart.Word("to revive"));</c></example>
    public HudHintLine? AddHintLine(string name, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudHintLine(), slot ?? new HudSlot(HudRegion.WeaponList, 0, 300f, 30f));
    }

    /// <summary>A readout at a place you choose (see <see cref="HudPlaced"/>): text, a thin bar, a row of cells or a clock in the game's
    /// HUD look. It takes no room in any region: it may overlap the game's HUD or other elements.</summary>
    /// <example><c>var air = ui.AddPlaced("Air", new HudPlacement(HudAnchor.Bottom, 0, -128, 660, 30)); air.Kind = HudPlacedKind.Bar; air.Text = "AIR {percent}"; air.Set(62, 100);</c></example>
    public HudPlaced? AddPlaced(string name, HudPlacement placement)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudPlaced(placement), new HudSlot(HudRegion.World));
    }

    /// <summary>The list of commands called by typing an arrow code (see <see cref="HudCommandList"/>). Hidden until you call
    /// <see cref="HudCommandList.Show"/>. It stands at the right edge of the screen, its middle on the middle of the screen, clear of the
    /// rest of the HUD (<see cref="CommandDock"/>); while it shows, the game's glass reflection is held off (a hide claim named
    /// <paramref name="name"/> + <c>Glass</c>).</summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>CallIns</c>.</param>
    /// <param name="title">The header while a code is typed.</param>
    /// <param name="heldTitle">The header while a command is armed.</param>
    /// <param name="heldLabel">The text in the armed command's value cell.</param>
    /// <param name="maxItems">How many commands it can show.</param>
    /// <param name="maxSteps">How many arrows the longest code has.</param>
    public HudCommandList? AddCommandList(string name, UIText title, UIText heldTitle, UIText heldLabel, int maxItems, int maxSteps)
    {
        if (maxItems < 1 || maxSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxItems), "A command list needs at least one command with one step.");
        var id = Full(name, "HUD element");
        if (id == null) return null;
        var list = Hud.Add(id, new HudCommandList(title ?? UIText.Empty, heldTitle ?? UIText.Empty, heldLabel ?? UIText.Empty, maxItems, maxSteps), new HudSlot(HudRegion.World));
        if (list == null) return null;
        list.Visible = false;
        list.Glass = HideGameHud(name + "Glass", GameHudPart.All & ~GameHudPart.GlassReflection);
        return list;
    }

    /// <summary>A claim on the shared banner (big text a little above the middle of the screen). Banners of every mod share one slot:
    /// <paramref name="priority"/> decides against other mods' banners (higher takes the slot, equal or lower waits).</summary>
    /// <example><c>ui.AddBanner("Banner", 10).Show("WAVE 3", NoticeTone.Warn);</c></example>
    public HudBanner? AddBanner(string name, int priority = 0) => BannerView.Register(this, name, priority);

    /// <summary>A claim on the shared notice list in the top left corner (under where the game draws its objective).</summary>
    /// <example><c>ui.AddNotices("Notices").Push("PICKED UP KEY");</c></example>
    public HudNotices? AddNotices(string name) => NoticesView.Register(this, name);

    /// <summary>World points this mod places (see <see cref="HudWaypoints"/>): up to <paramref name="capacity"/> (1..64) marks with a label,
    /// distance and an arrow on the screen's edge while off screen, and, for points that ask, a mark on every bearing ring that shows the
    /// compass. Region <see cref="HudRegion.World"/>.</summary>
    /// <example><c>var points = ui.AddWaypoints("Caches", 8); var id = points.Add(new Waypoint(cache.position, "CACHE", CompassPointKind.Container) { Bearing = true });</c></example>
    public HudWaypoints? AddWaypoints(string name, int capacity = 16)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudWaypoints(Math.Clamp(capacity, 1, 64)), new HudSlot(HudRegion.World));
    }
}
public sealed partial class MenuBarPage
{
    private bool _overflowLogged;

    /// <summary>Where the next <see cref="AddRow"/> goes: the y in <see cref="Root"/> units
    /// (0 at the top of the content, negative down).</summary>
    public float Cursor { get; set; }

    /// <summary>A row with <paramref name="label"/> at the left and <paramref name="value"/> at the right edge, under the previous
    /// one; the cursor moves down by the taller of the two and <paramref name="gap"/>.</summary>
    public void AddRow(UIText label, UIText value, float gap = 8f)
    {
        var left = AddText(label);
        var right = AddText(value);
        var split = Size.x * 0.6f;
        var heightLeft = Place(left, 0f, split - 12f);
        var heightRight = Place(right, split, Size.x - split);
        right.alignment = TextAlignmentOptions.TopRight;
        left.transform.localPosition = new Vector3(0f, Cursor, 0f);
        right.transform.localPosition = new Vector3(split, Cursor, 0f);
        Advance(Math.Max(heightLeft, heightRight) + gap);
    }

    /// <summary>Wraps <paramref name="label"/> to <paramref name="width"/> (<see cref="Root"/> units), fits its box to the text and
    /// returns the text's height in <see cref="Root"/> units.</summary>
    private float Place(TextMeshPro label, float x, float width)
    {
        var scale = Math.Max(1e-4f, label.transform.localScale.x);
        label.rectTransform.sizeDelta = new Vector2(width / scale, label.rectTransform.sizeDelta.y);
        var height = label.GetPreferredValues(label.text, width / scale, float.PositiveInfinity).y * scale;
        label.rectTransform.sizeDelta = new Vector2(width / scale, height / scale);
        return height;
    }

    private void Advance(float amount)
    {
        Cursor -= amount;
        if (-Cursor > Size.y && !_overflowLogged)
        {
            _overflowLogged = true;
            Plugin.Error("ui-menu-bar-page-has-more-text", "Menu bar page '" + Id + "' has more text than fits the window (" + NativeUi.F(Size.y) + " units tall); the rest is cut off.");
        }
    }
}
