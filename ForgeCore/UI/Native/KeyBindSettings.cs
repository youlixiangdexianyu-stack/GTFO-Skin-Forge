using System;
using System.Collections.Generic;
using System.Globalization;
using CellMenu;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// Puts every registered key action into the game's own keyboard and mouse key page. The rows join the game's own build of that page:
/// while <c>CM_PageSettings.CreateKeyMappingSubMenu</c> builds the key page, the list the game hands to its window
/// (<c>CM_ScrollWindow.SetContentItems</c>) gets, after the game's rows, one heading row per mod group and one row per action, so the
/// game lays out, scrolls and hovers them in its single call exactly as its own rows. The window is never given a list again: the game's
/// <c>SetContentItems</c> destroys every child of the content container before it lays out the new list, so a second call destroyed the
/// game's rows with ours (an empty key page and a NullReferenceException in <c>UpdateContentItemVisibility</c> on every hover).
/// Each row is a copy of one of the game's key rows (<c>CM_SettingsItem</c>) made while inactive, with the copy's key field
/// (<c>CM_SettingKeyBindingField</c>) and its colliders removed before it ever runs: the copy keeps the key box's look, and no game key
/// field code (reading keys, writing the game's bindings, menu ids) runs for it, so no key field method is patched. A press on the row
/// starts reading a key, Esc cancels, Backspace or Delete unbinds; the game's reset of the key page also puts every action back on its
/// default. Every Forge row sits under one FORGE heading, then one heading per mod group. An action with more than one allowed trigger mode has a row under it with its trigger mode in the key box; a press there moves to the next allowed mode. An action that may be Double has a row with its double-press time (a press moves to the next time). An action that may be Tap, Hold or Double also has a row with what happens on a key the game also uses (a press switches between handing the other presses back and firing together); each reads "-" while it does not apply. A key another action whose same press would fire (in an overlapping context) or the game uses is named after the action's title in the warning colour.
/// Actions are registered in the packages' Load, before the game builds its settings; one added after that is reported as an error and
/// keeps its saved key.
/// </summary>
internal static class KeyBindSettings
{
    /// <summary>The key actions' rows on the game's keyboard and mouse key page, registered with <see cref="SettingsPages"/>.</summary>
    internal static readonly PageRows Group = new KeyRows();

    private sealed class KeyRows : PageRows
    {
        internal override PageBuilder Builder => PageBuilder.KeyMapping;
        internal override eSettingsSubMenuId Page => eSettingsSubMenuId.KeyMap;
        internal override string Owner => "the mods' key actions";
        internal override void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content) => AddRows(window, content);
        internal override void PageReset() => KeyBinds.ResetAll();
        internal override void Update() => KeyBindSettings.Update();
    }
    private enum RowKind { Key, Mode, Hold, Window, Game }

    private sealed class Row
    {
        internal CM_SettingsItem Item = null!;
        internal KeyBox Box;
        internal int Action = -1;
        internal RowKind Kind;
        internal string Group = "", Mod = "";
        internal IntPtr Route;
    }

    /// <summary>The key box parts of a copied row, kept after its key field component is removed.</summary>
    private struct KeyBox
    {
        internal TMPro.TextMeshPro? Text;
        internal SpriteRenderer? Background;
        internal Color Active, Idle;
    }

    private const string NotApplicable = "-";

    private static readonly List<Row> Rows = new();
    private static readonly List<(CM_SettingsItem Row, CM_SettingKeyBindingField Field)> GameRows = new();
    private static readonly Dictionary<(InputAction, bool), string> GameNames = new();
    private static readonly List<(string Action, string Key, bool Button)> Game = new();
    private static readonly List<int> Others = new();
    private static readonly List<string> GameHits = new();
    private static CM_ScrollWindow? _window;
    private static Row? _capturing;
    private static bool _lateReported;
    private static int _builtCount = -1, _shownVersion = -1, _doneFrame = -1;
    private static string _shownLanguage = "";
    private static float _nextGameRead;

    /// <summary>Per frame: runs a key capture and keeps the rows' texts current.</summary>
    private static void Update()
    {
        if (_window == null || _window.WasCollected) return;
        try
        {
            if (_builtCount >= 0 && _builtCount != KeyBinds.Book.Count && !_lateReported)
            {
                _lateReported = true;
                Plugin.Error("ui-key-action-s-were-added", "Key action(s) were added after the game built its settings page and are not listed there (" + (KeyBinds.Book.Count - _builtCount)
                             + " of " + KeyBinds.Book.Count + "); register key actions in the package's Load. They keep their saved keys (" + Plugin.Id + ".keys.cfg).");
            }
            if (Rows.Count == 0) return;
            if (_capturing != null) Capture();
            var visible = _window.gameObject.activeInHierarchy;
            var now = ForgeCore.Frame.UnscaledTime;
            var gameRead = visible && now >= _nextGameRead;
            if (gameRead) _nextGameRead = now + 1f;
            if (gameRead || _shownVersion != KeyBinds.Book.Version || _shownLanguage != UITexts.Language) Refresh(gameRead);
        }
        catch (Exception error) { Plugin.Error("ui-key-settings-rows-failed-update", "The key settings rows failed to update: " + error); }
    }

    /// <summary>The game is about to lay out its keyboard key page: the mod rows are appended to the list it lays out.</summary>
    private static void AddRows(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content)
    {
        Reset();
        _window = window;
        _builtCount = KeyBinds.Book.Count;
        CM_SettingsItem? template = null;
        for (var i = 0; i < content.Count; i++)
        {
            var row = content[i]?.TryCast<CM_SettingsItem>();
            var field = row == null ? null : row.GetComponentInChildren<CM_SettingKeyBindingField>(true);
            if (row == null || field == null) continue;
            template ??= row;
            GameRows.Add((row, field));
        }
        if (_builtCount == 0) return;
        if (template == null)
        {
            Plugin.Error("ui-games-keyboard-key-page", "The game's keyboard key page has no key row to copy; the mods' key actions are not listed and keep their saved keys (" + Plugin.Id + ".keys.cfg).");
            return;
        }

        var order = new List<int>();
        for (var i = 0; i < KeyBinds.Book.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var x = KeyBinds.Book[a];
            var y = KeyBinds.Book[b];
            var c = string.CompareOrdinal(x.Mod, y.Mod);
            if (c == 0) c = string.CompareOrdinal(x.GroupKey, y.GroupKey);
            return c != 0 ? c : a.CompareTo(b);
        });

        SettingsPages.CopyRows(Group, template, "ForgeUI.KeyRows", content, holder =>
        {
            Rows.Add(Copy(template, holder, -1, "", "", RowKind.Key));
            string? mod = null, group = null;
            foreach (var index in order)
            {
                var entry = KeyBinds.Book[index];
                if (entry.Mod != mod || entry.GroupKey != group)
                {
                    mod = entry.Mod;
                    group = entry.GroupKey;
                    Rows.Add(Copy(template, holder, -1, mod, group, RowKind.Key));
                }
                Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey, RowKind.Key));
                if (KeyModeSet.Next(entry.Allowed, entry.DefaultMode) != entry.DefaultMode) Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey, RowKind.Mode));
                if ((entry.Allowed & (KeyModes.Tap | KeyModes.Hold)) != 0) Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey, RowKind.Hold));
                if ((entry.Allowed & KeyModes.Double) != 0) Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey, RowKind.Window));
                if ((entry.Allowed & (KeyModes.Tap | KeyModes.Hold | KeyModes.Double)) != 0) Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey, RowKind.Game));
            }
            return Rows.ConvertAll(row => row.Item);
        });
        _shownVersion = -1;
    }

    private static Row Copy(CM_SettingsItem template, Transform holder, int action, string mod, string group, RowKind kind)
    {
        var go = Object.Instantiate(template.gameObject, holder);
        go.name = action < 0 ? "ForgeUI.KeyGroup." + mod + "." + group : (kind == RowKind.Key ? "ForgeUI.Key." : "ForgeUI.Key" + kind + ".") + KeyBinds.Book[action].FullId;
        NativeUi.StripLocalization(go);
        var item = go.GetComponent<CM_SettingsItem>();
        var row = new Row { Item = item, Action = action, Kind = kind, Mod = mod, Group = group };
        var field = go.GetComponentInChildren<CM_SettingKeyBindingField>(true);
        var boxObject = field == null ? null : field.gameObject;
        if (field != null)
        {
            row.Box = new KeyBox { Text = field.m_valueText, Background = field.m_background, Active = field.m_bgActive, Idle = field.m_bgNotActive };
            foreach (var collider in field.GetComponents<Collider2D>()) Object.DestroyImmediate(collider);
            Object.DestroyImmediate(field);
        }
        if (action < 0)
        {
            if (item.m_inputAlign != null) item.m_inputAlign.gameObject.SetActive(false);
            else if (boxObject != null) boxObject.SetActive(false);
            row.Box = default;
        }
        row.Route = Input.Route(item, () => Press(row), null);
        return row;
    }

    private static void Reset()
    {
        if (_capturing != null) { KeyBinds.Capture.Stop(); _capturing = null; }
        foreach (var row in Rows) Input.Unroute(row.Route);
        Rows.Clear();
        GameRows.Clear();
        GameNames.Clear();
        Game.Clear();
        _window = null;
        _builtCount = -1;
        _lateReported = false;
    }

    private static void Press(Row row)
    {
        if (row.Action < 0 || Time.frameCount == _doneFrame) return;
        if (row.Kind != RowKind.Key)
        {
            if (_capturing != null) Stop(_capturing);
            if (row.Kind == RowKind.Mode) KeyBinds.CycleMode(row.Action);
            else if (row.Kind == RowKind.Hold && UsesHold(row.Action)) KeyBinds.CycleHold(row.Action);
            else if (row.Kind == RowKind.Window && KeyBinds.Book[row.Action].Mode == KeyMode.Double) KeyBinds.CycleWindow(row.Action);
            else if (row.Kind == RowKind.Game && SharesGameButton(row.Action)) KeyBinds.ToggleWithGame(row.Action);
            return;
        }
        if (_capturing != null && _capturing != row) Show(_capturing, false);
        _capturing = row;
        KeyBinds.BeginCapture();
        Show(row, true);
    }

    private static void Capture()
    {
        var row = _capturing!;
        if (row.Item == null || row.Item.WasCollected || !row.Item.gameObject.activeInHierarchy)
        {
            Stop(row);
            return;
        }
        switch (KeyBinds.StepCapture())
        {
            case KeyCapture.Result.Bound: KeyBinds.Set(row.Action, KeyBinds.Capture.Chord); Stop(row); break;
            case KeyCapture.Result.Cleared: KeyBinds.Set(row.Action, KeyChord.None); Stop(row); break;
            case KeyCapture.Result.Cancelled: Stop(row); break;
        }
    }

    private static void Stop(Row row)
    {
        KeyBinds.Capture.Stop();
        _capturing = null;
        _doneFrame = Time.frameCount;
        if (row.Item != null && !row.Item.WasCollected) Show(row, false);
    }

    /// <summary>The row's key box: the game's reading colour and "press a key" while capturing, else the key.</summary>
    private static void Show(Row row, bool reading)
    {
        var box = row.Box;
        if (box.Background != null && !box.Background.WasCollected) box.Background.color = reading ? box.Active : box.Idle;
        if (box.Text != null && !box.Text.WasCollected) NativeUi.SetText(box.Text, reading ? UIKit.Mod(Plugin.Id).Text("keys.press") : Value(row));
    }

    private static string Value(Row row)
    {
        var ui = UIKit.Mod(Plugin.Id);
        var entry = KeyBinds.Book[row.Action];
        return row.Kind switch
        {
            RowKind.Mode => ui.Text("keys.mode." + entry.Mode.ToString().ToLowerInvariant()),
            RowKind.Hold => UsesHold(row.Action) ? ui.Text("keys.seconds", KeyBinds.Book.HoldOf(row.Action).ToString("0.##", CultureInfo.InvariantCulture)) : NotApplicable,
            RowKind.Window => entry.Mode == KeyMode.Double ? ui.Text("keys.seconds", KeyBinds.Book.WindowOf(row.Action).ToString("0.##", CultureInfo.InvariantCulture)) : NotApplicable,
            RowKind.Game => !SharesGameButton(row.Action) ? NotApplicable : ui.Text(entry.WithGame ? "keys.game.both" : "keys.game.split"),
            _ => KeyBinds.Display(entry.Effective),
        };
    }

    /// <summary>The trigger mode the action has now is decided by the long-press time.</summary>
    private static bool UsesHold(int action) => KeyModeSet.Defers(KeyBinds.Book[action].Mode);

    /// <summary>The action could hand its other presses to a game button on its key, so the same-key choice means something.</summary>
    private static bool SharesGameButton(int action)
    {
        if (!KeyBinds.Book.CanHandBack(action)) return false;
        var key = KeyBinds.Book[action].Effective.Key;
        foreach (var (_, gameKey, button) in Game)
            if (button && string.Equals(gameKey, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What the same-key row says the game gets, in the row's title.</summary>
    private static string GameTitle(UIKitMod ui, int action)
    {
        var title = "    " + ui.Text("keys.game");
        if (!SharesGameButton(action)) return title;
        var entry = KeyBinds.Book[action];
        return title + "  " + ui.Text(entry.WithGame ? "keys.game.hint.both" : entry.Mode switch { KeyMode.Hold => "keys.game.hint.hold", KeyMode.Double => "keys.game.hint.double", _ => "keys.game.hint.tap" });
    }

    private static void Refresh(bool readGame)
    {
        _shownVersion = KeyBinds.Book.Version;
        _shownLanguage = UITexts.Language;
        if (readGame || Game.Count == 0)
        {
            GameNames.Clear();
            foreach (var (gameRow, field) in GameRows)
                if (!gameRow.WasCollected && !field.WasCollected && field.m_data != null && gameRow.m_title != null)
                    GameNames[(field.m_data.action, field.m_isNegCompOfTwoAxisValue)] = gameRow.m_title.text;
            Game.Clear();
            foreach (var binding in GameKeys.All())
                Game.Add((GameNames.TryGetValue((binding.Action, binding.Negative), out var name) ? name : binding.Action.ToString(), binding.Key.ToString(), binding.Button));
        }
        var ui = UIKit.Mod(Plugin.Id);
        foreach (var row in Rows)
        {
            if (row.Item == null || row.Item.WasCollected || row.Item.m_title == null) continue;
            if (row.Action < 0)
            {
                var heading = row.Mod.Length == 0 ? ui.Text("keys.forge") : row.Group.Length > 0 ? UIKit.Mod(row.Mod).Texts.Text(row.Group) : row.Mod;
                NativeUi.SetText(row.Item.m_title, heading.ToUpperInvariant());
                continue;
            }
            var entry = KeyBinds.Book[row.Action];
            if (row.Kind != RowKind.Key)
            {
                NativeUi.SetText(row.Item.m_title, row.Kind == RowKind.Game ? GameTitle(ui, row.Action) : "    " + ui.Text(row.Kind == RowKind.Mode ? "keys.trigger" : row.Kind == RowKind.Window ? "keys.window" : "keys.hold"));
                Show(row, false);
                continue;
            }
            var title = RichText.Plain(UIKit.Mod(row.Mod).Texts.Text(entry.NameKey));
            var clashes = new List<string>();
            if (KeyBinds.Book.Conflicts(row.Action, Others) > 0)
                foreach (var other in Others) clashes.Add(RichText.Plain(UIKit.Mod(KeyBinds.Book[other].Mod).Texts.Text(KeyBinds.Book[other].NameKey)));
            if (KeyBinds.Book.GameConflicts(row.Action, Game, GameHits) > 0)
                foreach (var hit in GameHits) clashes.Add(RichText.Plain(hit));
            if (clashes.Count > 0)
                title += "  <color=#" + ColorUtility.ToHtmlStringRGB(UIKitColors.Alert) + ">" + ui.Text("keys.conflict", string.Join(" / ", clashes)) + "</color>";
            NativeUi.SetText(row.Item.m_title, title);
            if (row != _capturing) Show(row, false);
        }
    }
}
