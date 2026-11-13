using System;

namespace ForgeUI;

/// <summary>
/// Whether, in a level, the player has left first-person play for one of the game's own full screens: the map, a terminal, the
/// in-game menu (settings and the other menu pages), the console, a debug menu or a global pop-up (<c>FocusStateManager.CurrentState</c>).
/// While away the library steps aside completely, so the game's screen shows exactly as it does without mods: every hide of the
/// game's HUD is lifted (the game puts its own HUD away for these screens), the chat goes back to its place, HUD elements, crosshair
/// attachments and the screen layer are off (ForgeVisual steps its outlines aside the same way). Everything comes back on the return. Typing in chat, the communication
/// menu, hacking and the elevator ride are play, not away.
/// </summary>
internal static class HudFocus
{
    private static bool _logged;

    /// <summary>True while, in a level, the game shows one of its own full screens.</summary>
    internal static bool Away { get; private set; }

    /// <summary>Per frame: the focus state, then the HUD pointer that depends on it.</summary>
    internal static void Update()
    {
        Track();
        HudPointer.Update();
    }

    private static void Track()
    {
        bool away;
        eFocusState focus = default;
        try
        {
            focus = FocusStateManager.CurrentState;
            away = GameStateManager.CurrentStateName == eGameStateName.InLevel && IsAway(focus);
        }
        catch (Exception error)
        {
            if (!_logged) { _logged = true; Plugin.Error("ui-reading-games-focus-state-failed", "Reading the game's focus state failed: " + error); }
            away = false;
        }
        if (away == Away) return;
        Away = away;
        ScreenLayer.Refresh();
    }

    private static bool IsAway(eFocusState focus) => focus switch
    {
        eFocusState.Map or eFocusState.MainMenu or eFocusState.ComputerTerminal or eFocusState.Console or eFocusState.DebugMenu
            or eFocusState.GlobalPopupMessage => true,
        _ => false,
    };
}
