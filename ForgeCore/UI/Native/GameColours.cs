using System;
using CellMenu;
using Player;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The game's own HUD and menu colours, read from the game's own pieces: the chat log's line prefab (<c>PUI_GameEventLog_Item</c>: the
/// HUD's cyan-white text, its red alert and its green), the local player's status (<c>PUI_LocalPlayerStatus</c>: health and infection),
/// a teammate status item (<c>PUI_TeammateStatusItem</c>: downed, grabbed and ammo-low notices), the player colours
/// (<c>PlayerManager.GetStaticPlayerColor</c>) and the settings page (<c>CM_Item</c> text, plain and under the cursor). Each source is read
/// once, the first time it exists (<see cref="Refresh"/>); until then, and if one cannot be read (logged once), the colours keep the values
/// sampled from a 1280x720 screenshot.
/// </summary>
public static partial class UIKitColors
{
    /// <summary>HUD text: the colour the game writes its event log in.</summary>
    public static Color GameText { get; private set; } = Hex(0xB8E2FA);
    /// <summary>The game's highlight orange: its interaction prompts' key colour (<c>PUI_InteractionPrompt.m_colorButtonPrompt</c>), the
    /// value sampled from a screenshot until that is read.</summary>
    public static Color Highlight { get; private set; } = Hex(0xE8A33C);
    /// <summary>The teammate notice for downed.</summary>
    public static Color Downed { get; private set; } = Hex(0xE24851);
    /// <summary>The teammate notice for grabbed.</summary>
    public static Color Grabbed { get; private set; } = new(0.98f, 0.78f, 0.25f, 1f);
    /// <summary>The local health bar, full.</summary>
    public static Color HealthGood { get; private set; } = Hex(0xB8E2FA);
    /// <summary>The local health bar, nearly empty.</summary>
    public static Color HealthBad { get; private set; } = Hex(0xE24851);
    /// <summary>How full something is at a glance: green when full, through yellow at half, to red when empty. Bright enough to read on a dark
    /// plate; say the number beside it too, so it reads without telling the colours apart.</summary>
    public static Color Level(float share)
    {
        share = Mathf.Clamp01(share);
        return share >= 0.5f ? Color.Lerp(LevelMid, LevelFull, (share - 0.5f) * 2f) : Color.Lerp(LevelEmpty, LevelMid, share * 2f);
    }

    private static readonly Color LevelFull = Hex(0x4FE36B), LevelMid = Hex(0xFFD23C), LevelEmpty = Hex(0xFF4D4D);

    /// <summary>The local infection bar.</summary>
    public static Color Infection { get; private set; } = new(0.62f, 0.42f, 0.95f, 1f);
    /// <summary>Menu item text of the game's settings page.</summary>
    public static Color MenuText { get; private set; } = Hex(0xB8E2FA);
    /// <summary>The same item under the cursor.</summary>
    public static Color MenuOver { get; private set; } = Hex(0xFFFFFF);

    private static readonly Color[] Players = { Hex(0xC23B3B), Hex(0x3BA35B), Hex(0x3B6BC2), Hex(0xC2A23B) };
    private static bool _log, _status, _mate, _players, _prompt, _menu;

    /// <summary>A colour as RRGGBBAA for a rich text tint: <c>&lt;color=#...&gt;text&lt;/color&gt;</c>.</summary>
    public static string Tint(string text, Color colour) => "<color=#" + ColorUtility.ToHtmlStringRGBA(colour) + ">" + text + "</color>";

    /// <summary>Reads every source that exists now and was not read yet; cheap once all are read. Call it where the colours are about to
    /// be drawn (a screen opening, a list being built).</summary>
    public static void Refresh()
    {
        if (!_log) _log = Read("the chat log line", () =>
        {
            var item = GuiManager.PlayerLayer?.m_gameEventLog?.m_logItemPrefab?.GetComponent<PUI_GameEventLog_Item>();
            if (item == null) return false;
            GameText = Opaque(item.m_colorGameEvent); Alert = Opaque(item.m_colorAlert); Good = Opaque(item.m_colorPositivePop);
            return true;
        });
        if (!_status) _status = Read("the player status", () =>
        {
            var status = GuiManager.PlayerLayer?.m_playerStatus;
            if (status == null) return false;
            HealthGood = Opaque(status.m_healthGood); HealthBad = Opaque(status.m_healthBad); Infection = Opaque(status.m_infectionHigh);
            return true;
        });
        if (!_mate) _mate = Read("a teammate status item", () =>
        {
            var root = GuiManager.PlayerLayer?.Root;
            var items = root == null ? null : root.GetComponentsInChildren<PUI_TeammateStatusItem>(true);
            var item = items != null && items.Length > 0 ? items[0] : PlayerGuiLayer.s_teammate;
            if (item == null) return false;
            Downed = Opaque(item.m_noticeDowned); Grabbed = Opaque(item.m_noticeGrabbed); Warn = Opaque(item.m_noticeAmmoLow);
            return true;
        });
        ReadPlayers();
        if (!_prompt) _prompt = Read("the interaction prompt", () =>
        {
            var prompt = GuiManager.InteractionLayer?.m_interactPrompt;
            if (prompt == null) return false;
            Highlight = Opaque(prompt.m_colorButtonPrompt);
            return true;
        });
        if (!_menu) _menu = Read("the settings page", () =>
        {
            var page = GuiManager.MainMenuLayer?.GetPage(eCM_MenuPage.CMP_SETTINGS);
            if (page == null) return false;
            CM_Item? text = null;
            foreach (var i in page.GetComponentsInChildren<CM_Item>(true))
                if (!i.gameObject.name.StartsWith("ForgeUI.", StringComparison.Ordinal) && i.m_textColorOrg != null && i.m_textColorOrg.Length > 0 && i.m_textColorOver != null && i.m_textColorOver.Length > 0) { text = i; break; }
            if (text == null) return false;
            MenuText = Opaque(text.m_textColorOrg[0]); MenuOver = Opaque(text.m_textColorOver[0]);
            return true;
        });
    }

    internal static Color Player(int slot)
    {
        ReadPlayers();
        return Players[slot & 3];
    }

    private static void ReadPlayers()
    {
        if (!_players) _players = Read("the player colours", () =>
        {
            if (PlayerManager.Current == null) return false;
            for (var slot = 0; slot < 4; slot++) Players[slot] = Opaque(PlayerManager.GetStaticPlayerColor(slot));
            return true;
        });
    }

    // A source that throws is logged once and not asked again; one that is not there yet is asked again on the next refresh.
    private static bool Read(string what, Func<bool> read)
    {
        try { return read(); }
        catch (Exception error)
        {
            Plugin.Error("ui-reading-games-colours-from-failed-screenshot", "Reading the game's colours from " + what + " failed; the screenshot values stay: " + error.Message);
            return true;
        }
    }

    private static Color Opaque(Color c) => new(c.r, c.g, c.b, 1f);

    private static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
}

public static partial class UIKit
{
    /// <summary>The game's colour of player slot <paramref name="slot"/> (0..3, the colour the game gives that player everywhere; slots
    /// repeat after 4), read from the game once it has players and the screenshot value until then.</summary>
    public static Color PlayerColour(int slot) => UIKitColors.Player(slot);
}
