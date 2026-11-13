using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>One line of the game's communication menu: its category's title, and its own text.</summary>
public readonly record struct CommsEntry(uint Category, uint Text, uint Dialog);

/// <summary>The game's own communication menu: the voice through its dialog manager and the text through the menu's broadcast, as its buttons do, and every player gets them as the game does. Main thread only.</summary>
public static class Comms
{
    private static PUI_CommunicationMenu? _menu;

    /// <summary>Every line of the game's communication menu, for choosing one. Empty when the menu data is not there yet.</summary>
    public static IReadOnlyList<CommsEntry> Entries()
    {
        var entries = new List<CommsEntry>();
        var data = CommunicationMenuData.ProvideInstance();
        if (data == null || data.m_categories == null) return entries;
        foreach (var category in data.m_categories)
        {
            if (category == null || category.Dialogs == null) continue;
            foreach (var dialog in category.Dialogs)
                if (dialog != null) entries.Add(new CommsEntry(category.Title, dialog.Title, dialog.DialogUID));
        }
        return entries;
    }

    /// <summary>The local player says line <paramref name="text"/> (<see cref="CommsEntry.Text"/>), to <paramref name="target"/> if named.
    /// False when <paramref name="speaker"/> is not the local player or the game has no such line (nothing said), or the menu is not up (the voice went, the text did not).</summary>
    public static bool Say(PlayerAgent speaker, uint text, PlayerAgent? target = null)
    {
        if (speaker == null || speaker.WasCollected || speaker.Pointer != Players.Local?.Pointer) return false;
        uint dialog = 0;
        foreach (var entry in Entries()) if (entry.Text == text) { dialog = entry.Dialog; break; }
        if (dialog == 0 || !PlayerDialogManager.IsDialogValid(dialog)) return false;
        // The two calls of the game's own PUI_CommunicationMenu.ExecuteDialogue: the voice, then the text in the chat.
        PlayerDialogManager.WantToStartDialogForced(dialog, speaker);
        if (_menu == null || _menu.WasCollected)
        {
            var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<PUI_CommunicationMenu>());
            _menu = found != null && found.Length > 0 ? found[0].TryCast<PUI_CommunicationMenu>() : null;
        }
        var menu = _menu == null ? null : _menu.m_menu;
        if (menu == null) return false;
        menu.BroadcastDialog(speaker, text, target);
        return true;
    }
}
