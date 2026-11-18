using UnityEngine;

namespace ForgeUI;

/// <summary>Key names the way the game shows them, for key hints that follow what the player actually bound.</summary>
public static class UIKitKeys
{
    /// <summary>The name of <paramref name="key"/>: the game's own mouse button names (the settings menu's) for mouse keys, a digit
    /// for the number row, else the key code's name.</summary>
    public static string Name(KeyCode key)
    {
        if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
        {
            var name = CellSettingGlobals.GetMouseButtonName(key - KeyCode.Mouse0);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return key is >= KeyCode.Alpha0 and <= KeyCode.Alpha9 ? ((int)(key - KeyCode.Alpha0)).ToString() : key.ToString();
    }

    /// <summary>The player's current binding of a game action, as the game names it; <paramref name="fallback"/> when it has none.
    /// Allocates a string: read it once a second, not every frame.</summary>
    public static string Binding(InputAction action, string fallback = "")
    {
        var name = InputMapper.GetBindingName(action);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>True on the frame the player's own key for a game action goes down (for a two-key axis such as
    /// <see cref="InputAction.MoveVertical"/>, its positive key, or its negative one with <paramref name="negative"/>). It reads the key
    /// itself, so it works while the action is held off the game (ForgeCore's input lease). The bindings are re-read once a second.</summary>
    public static bool GameKeyDown(InputAction action, bool negative = false)
    {
        var key = GameKeys.Of(action, negative);
        return key != KeyCode.None && UnityEngine.Input.GetKeyDown(key);
    }
}
