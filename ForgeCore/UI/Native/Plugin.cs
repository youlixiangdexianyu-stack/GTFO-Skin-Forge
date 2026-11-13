using System;
using BepInEx;

namespace ForgeUI;

/// <summary>The UI part of ForgeCore: started by ForgeCore's plugin (<see cref="Load"/>), stepped by its driver (<see cref="UIKitFrame"/>).
/// <see cref="Id"/> names its configuration file, log source, Harmony patches and HUD elements.</summary>
internal static class Plugin
{
    internal const string Id = "NAinfini.ForgeUI";

    /// <summary>This library's ForgeCore log.</summary>
    internal static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);
    /// <summary>The player's <c>Screen / Visor</c> setting.</summary>
    internal static bool VisorEnabled { get; private set; } = true;
    /// <summary>The <c>Screen</c> strength caps every overlay is drawn under (0..1, or seconds).</summary>
    internal static float VisorFrame { get; private set; } = 0.13f;
    internal static float CrackMax { get; private set; } = 0.3f;
    internal static float HitFlashPeak { get; private set; } = 0.35f;
    internal static float HitFlashSeconds { get; private set; } = 0.3f;
    /// <summary>The player's <c>Screen / Glow</c>: multiplies the soft glow every HUD piece is drawn with (0..2).</summary>
    internal static float Glow { get; private set; } = 1f;
    /// <summary>The player's <c>Screen / Fringe</c>: whether HUD pieces get their 1 px colour fringe.</summary>
    internal static bool Fringe { get; private set; } = true;
    internal static float OffsetX { get; private set; }
    internal static float OffsetY { get; private set; }
    internal static float HudOffsetX { get; private set; }
    internal static float HudOffsetY { get; private set; }
    /// <summary>How far screen-anchored HUD elements stay from the screen edge, in HUD units.</summary>
    internal static float HudSafeMargin { get; private set; } = 48f;

    /// <summary>Binds a setting and hands its value to <paramref name="set"/> now and whenever it changes.</summary>
    private static void Watch<T>(string section, string key, T value, string description, Action<T> set)
        => set(ForgeCore.Settings.Bind(Id, section, key, value, description, set).Value);

    /// <summary>An offset setting, 0 (logged) when it is not finite.</summary>
    private static void Offset(string section, string key, float value, string description, Action<float> set)
        => Watch(section, key, value, description, v =>
        {
            if (!float.IsFinite(v)) { Error("ui-must-be-finite-using-0-instead", section + " / " + key + " must be finite; using 0 instead."); v = 0f; }
            set(v);
        });

    /// <summary>A <c>Screen</c> strength setting clamped to 0..<paramref name="max"/> (its default when it is not finite).</summary>
    private static void Strength(string key, float value, string description, Action<float> set, float max)
        => Watch("Screen", key, value, description, v => set(Math.Clamp(float.IsFinite(v) ? v : value, 0f, max)));

    /// <summary>An error with its registered diagnostic code, written once per exact text.</summary>
    internal static void Error(string code, string detail) => Logger.ErrorOnce(code, detail);

    internal static void Load()
    {
        Offset("Lobby", "ButtonOffsetX", 0f,
            "Moves every lobby button added by mods right (positive) or left, in menu units (about pixels at 1080p). Use it when another mod draws over them.", v => OffsetX = v);
        Offset("Lobby", "ButtonOffsetY", 0f,
            "Moves every lobby button added by mods up (positive) or down, in menu units (about pixels at 1080p).", v => OffsetY = v);
        Offset("Hud", "OffsetX", 0f,
            "Moves every in-level HUD element added by mods right (positive) or left, in HUD units (about pixels at 1080p).", v => HudOffsetX = v);
        Offset("Hud", "OffsetY", 0f,
            "Moves every in-level HUD element added by mods up (positive) or down, in HUD units (about pixels at 1080p).", v => HudOffsetY = v);
        Offset("Hud", "SafeMargin", 48f,
            "How far HUD elements that sit on a screen corner or edge stay from the screen edge, in HUD units (about pixels at 1080p). Raise it on a TV that crops the picture.", v => HudSafeMargin = Math.Max(0f, v));
        ForgeUI.Language.Bind();
        ForgeCore.GameEvents.LevelEnded += GameHud.LevelEnded;
        ForgeCore.GameEvents.LevelEnded += LocalHits.LevelEnded;
        ForgeCore.GameEvents.LevelEnded += ScreenStack.LevelEnded;
        // A checkpoint reload rebuilds the enemies: bars kept for the old ones are dropped, like Audio and Visual drop theirs.
        ForgeCore.GameEvents.CheckpointReloaded += EnemyHealthBars.Clear;
        KeyBinds.Load(Paths.ConfigPath);
        Watch("Screen", "Visor", true,
            "Draws the helmet visor overlay some mods show (its frame, shade, cracks and heat glow). Turn it off for a clear screen.", v => VisorEnabled = v);
        Strength("VisorFrame", 0.13f, "Opacity of the visor's frame lines, 0 to 1.", v => VisorFrame = v, 1f);
        Strength("CracksMax", 0.3f, "Highest opacity of visor cracks, 0 to 1 (they keep clear of the middle of the screen).", v => CrackMax = v, 1f);
        Strength("HitFlashPeak", 0.35f, "Peak opacity of the hit-direction flash on the screen edge, 0 to 1.", v => HitFlashPeak = v, 1f);
        Strength("Glow", 1f, "Multiplies the soft glow round HUD pieces mods draw, 0 to 2; 0 turns it off.", v => Glow = v, 2f);
        Watch("Screen", "Fringe", true, "Draws a faint 1 px red and blue colour fringe on HUD pieces mods draw, like a display seen through a visor. Turn it off for clean edges.", v => Fringe = v);
        Strength("HitFlashSeconds", 0.3f, "How long a full hit-direction flash lasts, 0.1 to 2 seconds.", v => HitFlashSeconds = Math.Max(0.1f, v), 2f);
        // No game method is patched here: each feature puts its own hooks on the first time it is used (PatchGroups).
        // The confirmation keys are listed under Controls from the start, so they can be rebound before the first question.
        ConfirmDialogs.RegisterKeys();
    }
}

/// <summary>Runs the per-frame work, called by ForgeCore's driver: keeps the lobby and menu bar buttons and the HUD elements in step, destroys closed picker
/// pieces and holds an open picker still on screen.</summary>
internal static class UIKitFrame
{
    private static bool _playing;

    /// <summary>In a level with the player in control (no menu, map or chat open).</summary>
    internal static bool InPlay()
    {
        try { return GameStateManager.CurrentStateName == eGameStateName.InLevel && FocusStateManager.CurrentState == eFocusState.FPS; }
        catch (Exception error) { Plugin.Error("ui-forge-ui-could-not-read-game", "Forge UI could not read the game state: " + error); return false; }
    }

    internal static void Update()
    {
        ForgeUI.Language.Update();
        KeyBinds.Update(); SettingsPages.Update();
        Input.PruneDestroyed();
        // In play no lobby or menu bar is on screen: skip their per-frame upkeep and close the bar pages once on the way in.
        var playing = InPlay();
        if (!playing) { LobbyButtons.Update(); MenuBar.Update(); }
        else if (!_playing) MenuBar.Hidden();
        _playing = playing;
        ConfirmDialogs.Update();
        HudFocus.Update();
        Hud.Update();
        LobbyStrips.Update();
        if (PatchGroups.Has(PatchGroup.Objectives)) UIKit.ObjectivesReading.Tick();
        Picker.Tick();
    }

    /// <summary>After the page has panned its content for the cursor.</summary>
    internal static void LateUpdate()
    {
        Picker.Pin();
        GameHud.LateUpdate();
        CrosshairAttachments.LateUpdate();
        // Supplied gear icon jobs wait for their mod's model; with nothing waiting this is one comparison.
        try { GearIcons.Update(); }
        catch (Exception error) { Plugin.Error("ui-gear-icon-update-failed", "Gear icon update failed: " + error); }
        // After every mod's Update, so an effect pushed or a visor shown this frame shows this frame.
        try { ScreenStack.Tick(); }
        catch (Exception error) { Plugin.Error("ui-screen-effect-stack-update-failed", "Screen effect stack update failed: " + error); }
    }
}
