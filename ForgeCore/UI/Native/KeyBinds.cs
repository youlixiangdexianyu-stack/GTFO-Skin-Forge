using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UInput = UnityEngine.Input;

namespace ForgeUI;

/// <summary>
/// One registered key action (see <see cref="UIKitMod.AddKeyAction"/>). Read <see cref="Pressed"/>, <see cref="Held"/>,
/// <see cref="Released"/> or <see cref="On"/> every frame: the keys are read once per frame for all actions together, and reading
/// costs no lookup and no allocation. The player picks the key and the trigger mode (<see cref="Mode"/>) in the game's settings
/// (Controls); <see cref="Name"/> is what to show on a key prompt. While the settings read a new key no action is down.
/// </summary>
public sealed class KeyAction
{
    internal KeyAction(UIKitMod mod, int index)
    {
        Mod = mod;
        Index = index;
    }

    internal readonly UIKitMod Mod;
    internal readonly int Index;
    internal KeyGesture Pulse;
    internal int Code, OwnMod;
    internal float TapWait;
    private int _driven; // 0 = the real key, 1 = driven down, 2 = driven up for one more read
    internal string? ShownName;
    internal int ShownVersion = -1;
    internal string ShownLanguage = "";

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id => KeyBinds.Book[Index].FullId;

    /// <summary>The trigger mode the action has now (the player's, else the default).</summary>
    public KeyMode Mode => KeyBinds.Book[Index].Mode;

    /// <summary>The key the action has now (the player's, else the default); <see cref="KeyChord.None"/> when unbound.</summary>
    public KeyChord Chord => KeyBinds.Book[Index].Effective;

    /// <summary>True on the frame the action starts (see <see cref="KeyMode"/>).</summary>
    public bool Pressed { get { KeyBinds.Sync(); return Pulse.Pressed; } }

    /// <summary>True while the action lasts (for a mode that fires once, on that frame only).</summary>
    public bool Held { get { KeyBinds.Sync(); return Pulse.Held; } }

    /// <summary>True on the frame the action ends (for a mode that fires once, the frame it fires).</summary>
    public bool Released { get { KeyBinds.Sync(); return Pulse.Released; } }

    /// <summary>Flipped every time the action starts, whatever the mode: a toggle; set it to switch it yourself (for example off when
    /// the level ends).</summary>
    public bool On
    {
        get { KeyBinds.Sync(); return Pulse.On; }
        set => Pulse.On = value;
    }

    /// <summary>The key as the game names keys (<c>Ctrl+G</c>, the game's own mouse button names), or the "unbound" text. Cached: it
    /// changes only when the key or the language does.</summary>
    public string Name => KeyBinds.NameOf(this);

    /// <summary>For drivers and tests: reads the action as down, with a fresh press edge, until <see cref="ReleaseDriven"/>, without
    /// any key event. Works whatever key the action has, also none.</summary>
    public void HoldDriven() => _driven = 1;

    /// <summary>Ends <see cref="HoldDriven"/>: the action reads one released edge, then follows the real key again.</summary>
    public void ReleaseDriven() { if (_driven == 1) _driven = 2; }

    internal bool Driven => _driven != 0;

    internal bool DrivenDown()
    {
        var down = _driven == 1;
        if (_driven == 2) _driven = 0;
        return down;
    }

    /// <summary>Changes whenever any action's key changes, for redrawing key names you keep.</summary>
    public static int Version => KeyBinds.Book.Version;
}

/// <summary>Every key action of every mod: the book (defaults and the player's keys, saved in
/// <c>BepInEx/config/NAinfini.ForgeUI.keys.cfg</c>), the once-per-frame key read and the key capture the settings use.</summary>
internal static class KeyBinds
{
    internal static readonly KeyBindBook Book = new(Known);
    internal static readonly List<KeyAction> Actions = new();
    internal static readonly KeyCapture Capture = new();
    private static string? _path;
    private static int _frame = -1, _codesVersion = -1;
    private static KeyCode[] _candidates = Array.Empty<KeyCode>();
    private static readonly KeyCode[] Modifiers =
        { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr };

    /// <summary>The canonical spelling of a key code name, or null. Joystick buttons are not keys here (keyboard and mouse only).</summary>
    internal static string? Known(string name)
    {
        if (!Enum.TryParse<KeyCode>(name, true, out var key) || !Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.None) return null;
        var text = key.ToString();
        return text.StartsWith("Joystick", StringComparison.Ordinal) ? null : text;
    }

    private static BepInEx.Configuration.ConfigEntry<float>? _longPress;

    /// <summary>The long-press time of an action without its own, in seconds: a press shorter than this is a tap, a longer one a hold.</summary>
    private static float LongPress => Math.Clamp(_longPress?.Value ?? 0.25f, KeyBindBook.MinHold, KeyBindBook.MaxHold);

    private static BepInEx.Configuration.ConfigEntry<float>? _doublePress;

    /// <summary>The double-press time of an action without its own, in seconds.</summary>
    private static float DoublePress => Math.Clamp(_doublePress?.Value ?? 0.3f, KeyBindBook.MinWindow, KeyBindBook.MaxWindow);

    /// <summary>Reads the settings file once, when ForgeUI loads.</summary>
    internal static void Load(string configFolder)
    {
        _longPress = ForgeCore.Settings.Bind(Plugin.Id, "Keys", "LongPressSeconds", 0.25f,
            "How long a key must be held to count as a long press (Hold), in seconds, 0.1 to 1, for every action without its own time in the key settings; a shorter press is a tap (Tap) and goes to the game's own action on that key when the Forge action does not take taps.");
        Book.DefaultHold = LongPress;
        _doublePress = ForgeCore.Settings.Bind(Plugin.Id, "Keys", "DoublePressSeconds", 0.3f,
            "How far apart the two presses of a double press (Double) may be, in seconds, 0.1 to 0.6, for every action without its own time in the key settings.");
        Book.DefaultWindow = DoublePress;
        _path = Path.Combine(configFolder, Plugin.Id + ".keys.cfg");
        if (!File.Exists(_path)) return;
        try
        {
            var problems = new List<string>();
            Book.Load(File.ReadAllText(_path), problems);
            foreach (var problem in problems) Plugin.Error("ui-key-bindings-file-that-line-ignored", "Key bindings file " + _path + ": " + problem + "; that line is ignored.");
        }
        catch (Exception error) { Plugin.Error("ui-key-bindings-file", "Key bindings file " + _path + " could not be read; every action uses its default key: " + error); }
    }

    private static void Save()
    {
        if (_path == null) return;
        try { ForgeCore.Store.WriteText(_path, Book.Save()); }
        catch (Exception error) { Plugin.Error("ui-key-bindings-could-not-be-saved", "Key bindings could not be saved to " + _path + ": " + error); }
    }

    internal static KeyAction? Add(UIKitMod mod, string name, string nameKey, string groupKey, string defaultKey, KeyMode mode, string context, KeyModes allowed)
    {
        PatchGroups.Ensure(PatchGroup.Settings);
        var index = Book.Register(mod.Id, name, nameKey, groupKey, defaultKey, mode, allowed, context, out var problem);
        if (index < 0)
        {
            Plugin.Error("ui-key-action-not-added", mod.Id + ": key action not added: " + problem + ".");
            return null;
        }
        var action = new KeyAction(mod, index);
        Actions.Add(action);
        return action;
    }

    internal static void Set(int index, KeyChord chord)
    {
        var entry = Book[index];
        if (chord == entry.Effective) return;
        Book.Set(index, chord);
        Save();
    }

    /// <summary>The player picked the next allowed trigger mode for the action.</summary>
    internal static void CycleMode(int index)
    {
        var entry = Book[index];
        var next = KeyModeSet.Next(entry.Allowed, entry.Mode);
        if (next == entry.Mode || !Book.SetMode(index, next)) return;
        Save();
    }

    /// <summary>The player picked the next long-press time for the action.</summary>
    internal static void CycleHold(int index)
    {
        Book.SetHold(index, Book.NextHold(index));
        Save();
    }

    /// <summary>The player picked the next double-press time for the action.</summary>
    internal static void CycleWindow(int index)
    {
        Book.SetWindow(index, Book.NextWindow(index));
        Save();
    }

    /// <summary>The player switched whether the action fires together with the game's button on its key.</summary>
    internal static void ToggleWithGame(int index)
    {
        Book.SetWithGame(index, !Book[index].WithGame);
        Save();
    }

    internal static void ResetAll()
    {
        var changed = false;
        for (var i = 0; i < Book.Count; i++) changed |= Book[i].Custom != null || Book[i].CustomMode != null || Book[i].CustomHold != null || Book[i].CustomWindow != null || Book[i].WithGame;
        if (!changed) return;
        Book.ResetAll();
        Save();
    }

    /// <summary>Once per frame, on the first read: every action's key state.</summary>
    internal static void Sync()
    {
        var frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        var window = DoublePress;
        if (window != Book.DefaultWindow) { Book.DefaultWindow = window; _codesVersion = -1; }
        if (_codesVersion != Book.Version) Codes();
        if (Actions.Count == 0) return;
        var blocked = Capture.Active;
        var held = blocked ? KeyMods.None : HeldMods();
        var now = ForgeCore.Frame.UnscaledTime;
        Book.DefaultHold = LongPress;
        for (var i = 0; i < Actions.Count; i++)
        {
            var action = Actions[i];
            var mode = Book[action.Index].Mode;
            var seconds = Book.TimeOf(action.Index);
            if (action.Driven) { action.Pulse.Step(action.DrivenDown(), mode, now, seconds, action.TapWait); continue; }
            if (blocked || action.Code == 0) { action.Pulse.Step(false, mode, now, seconds, action.TapWait); continue; }
            var down = UInput.GetKey((KeyCode)action.Code) && (held & ~(KeyMods)action.OwnMod) == Book[action.Index].Effective.Mods;
            action.Pulse.Step(down, mode, now, seconds, action.TapWait);
        }
    }

    /// <summary>Per frame from the driver, so held keys are tracked even when no mod reads them this frame, and the presses that are
    /// not an action's go to the game (<see cref="KeyHandBack"/>).</summary>
    internal static void Update()
    {
        Sync();
        KeyHandBack.Update();
    }

    private static KeyMods HeldMods()
    {
        var mods = KeyMods.None;
        if (UInput.GetKey(KeyCode.LeftControl) || UInput.GetKey(KeyCode.RightControl)) mods |= KeyMods.Ctrl;
        if (UInput.GetKey(KeyCode.LeftShift) || UInput.GetKey(KeyCode.RightShift)) mods |= KeyMods.Shift;
        if (UInput.GetKey(KeyCode.LeftAlt) || UInput.GetKey(KeyCode.RightAlt) || UInput.GetKey(KeyCode.AltGr)) mods |= KeyMods.Alt;
        return mods;
    }

    /// <summary>The key code of every action, worked out again when a key changes (not per frame).</summary>
    private static void Codes()
    {
        _codesVersion = Book.Version;
        foreach (var action in Actions)
        {
            var chord = Book[action.Index].Effective;
            action.Code = chord.IsNone || !Enum.TryParse<KeyCode>(chord.Key, out var key) ? 0 : (int)key;
            action.OwnMod = (int)KeyChords.ModOf(chord.Key);
            action.TapWait = Book.TapWait(action.Index);
            action.Pulse.Drop();
        }
    }

    internal static string NameOf(KeyAction action)
    {
        var language = UITexts.Language;
        if (action.ShownName != null && action.ShownVersion == Book.Version && action.ShownLanguage == language) return action.ShownName;
        action.ShownVersion = Book.Version;
        action.ShownLanguage = language;
        return action.ShownName = Display(Book[action.Index].Effective);
    }

    /// <summary>A chord as the player reads it: modifiers, then the key as the game names it.</summary>
    internal static string Display(KeyChord chord)
    {
        if (chord.IsNone) return UIKit.Mod(Plugin.Id).Text("keys.none");
        var text = "";
        if ((chord.Mods & KeyMods.Ctrl) != 0) text += "Ctrl+";
        if ((chord.Mods & KeyMods.Shift) != 0) text += "Shift+";
        if ((chord.Mods & KeyMods.Alt) != 0) text += "Alt+";
        return text + (Enum.TryParse<KeyCode>(chord.Key, out var key) ? UIKitKeys.Name(key) : chord.Key);
    }

    // ---- capture --------------------------------------------------------------------------------------------------

    internal static void BeginCapture()
    {
        if (_candidates.Length == 0) _candidates = Candidates();
        Capture.Begin();
        for (var i = 0; i < Actions.Count; i++) Actions[i].Pulse.Drop();
    }

    /// <summary>One frame of a running capture; the result says when it is over.</summary>
    internal static KeyCapture.Result StepCapture()
    {
        string? down = null, modDown = null, modUp = null;
        if (UInput.anyKeyDown)
            foreach (var key in _candidates)
                if (UInput.GetKeyDown(key)) { down = key.ToString(); break; }
        foreach (var key in Modifiers)
        {
            if (modDown == null && UInput.GetKeyDown(key)) modDown = key.ToString();
            if (modUp == null && UInput.GetKeyUp(key)) modUp = key.ToString();
        }
        return Capture.Step(UInput.anyKey, down, modDown, modUp, HeldMods());
    }

    /// <summary>Every keyboard key and mouse button except modifiers and joystick buttons, in key code order.</summary>
    private static KeyCode[] Candidates()
    {
        var keys = new List<KeyCode>();
        foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
        {
            if (key == KeyCode.None || Array.IndexOf(Modifiers, key) >= 0) continue;
            if (key.ToString().StartsWith("Joystick", StringComparison.Ordinal) || keys.Contains(key)) continue;
            keys.Add(key);
        }
        return keys.ToArray();
    }
}

/// <summary>The game's own keyboard and mouse bindings, read from its input map at most once a second.</summary>
internal static class GameKeys
{
    internal readonly struct Binding
    {
        internal Binding(InputAction action, bool negative, KeyCode key, bool button)
        {
            Action = action;
            Negative = negative;
            Key = key;
            Button = button;
        }

        internal readonly InputAction Action;
        internal readonly bool Negative;
        internal readonly KeyCode Key;
        /// <summary>A button (pressed or not), not one side of an axis.</summary>
        internal readonly bool Button;
    }

    private static readonly List<Binding> Read = new();
    private static float _readAt = float.NegativeInfinity;

    /// <summary>The bindings, re-read when a second has passed (the player may have changed one in the settings).</summary>
    internal static List<Binding> All()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (now - _readAt < 1f && Read.Count > 0) return Read;
        _readAt = now;
        Read.Clear();
        Version = 17;
        try
        {
            var map = InputMapper.Current?.m_currentKeyMouseMap;
            if (map == null) return Read;
            for (var i = 0; i < map.Length; i++)
            {
                var data = map[i];
                if (data == null) continue;
                if (data.type == InputType.AxisTwoInput)
                {
                    Add(data.action, false, data.axisPosIsMouseButton ? Mouse(data.axisPosMouseButtonIndex) : data.axisPosKeycode, false);
                    Add(data.action, true, data.axisNegIsMouseButton ? Mouse(data.axisNegMouseButtonIndex) : data.axisNegKeycode, false);
                }
                else if (data.device == InputMapperDevice.Keyboard) Add(data.action, false, data.keycode, data.type == InputType.Button);
                else if (data.device == InputMapperDevice.Mouse) Add(data.action, false, Mouse(data.mouseButtonIndex), data.type == InputType.Button);
            }
        }
        catch (Exception error) { Plugin.Error("ui-games-key-bindings", "The game's key bindings could not be read: " + error); }
        return Read;
    }

    private static KeyCode Mouse(int index) => index is >= 0 and <= 6 ? KeyCode.Mouse0 + index : KeyCode.None;

    private static void Add(InputAction action, bool negative, KeyCode key, bool button)
    {
        if (key != KeyCode.None) Read.Add(new Binding(action, negative, key, button));
        Version = Version * 31 + (int)action * 7 + (int)key * 3 + (button ? 1 : 0);
    }

    /// <summary>Changes when the bindings read change (a hash of them).</summary>
    internal static int Version { get; private set; }

    /// <summary>The key of a game action (an axis' negative side with <paramref name="negative"/>), or None.</summary>
    internal static KeyCode Of(InputAction action, bool negative)
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
            if (all[i].Action == action && all[i].Negative == negative) return all[i].Key;
        return KeyCode.None;
    }
}

/// <summary>
/// Gives the game the presses that are not a Forge action's. While the local player is in a level with the game's first-person
/// focus, every game button that shares its key with a <see cref="KeyMode.Tap"/>, <see cref="KeyMode.Hold"/> or <see cref="KeyMode.Double"/> action (the only
/// action on that key, <see cref="KeyBindBook.HandsBack"/>) is held off through one ForgeCore <c>InputLease</c>, so the game never
/// reads that key itself; ForgeUI then presses and lets go of those buttons through the lease for every press that is not the
/// action's: a short press of a hold action (on the frame the key comes up), a long press of a tap action (once the long-press time
/// passes, held until the key comes up), the first press of a double-press action (at once: it cannot be known to be a first press; the
/// second press, which fires the action, is not passed on) and a press with other modifiers than the action's chord (at once). Outside first-person
/// play (a menu, the map, a terminal, chat) the lease is let go and the game reads its keys as always. An action the player set to fire
/// together with the game takes no part: the game reads that key untouched.
/// </summary>
internal static class KeyHandBack
{
    private sealed class Share
    {
        internal KeyAction Action = null!;
        internal KeyCode Key;
        internal InputAction[] Game = Array.Empty<InputAction>();
        internal bool RawHeld, Passing;
    }

    private static readonly List<Share> Shares = new();
    private static readonly List<InputAction> Blocked = new();
    private static ForgeCore.InputLease? _lease;
    private static int _bookVersion = -1, _gameVersion = int.MinValue;

    internal static void Update()
    {
        if (KeyBinds.Actions.Count == 0) return;
        if (!Live()) { End(); return; }
        var game = GameKeys.All();
        if (_bookVersion != KeyBinds.Book.Version || _gameVersion != GameKeys.Version) Rebuild(game);
        if (Shares.Count == 0) { End(); return; }
        if (_lease == null || !_lease.IsHeld)
        {
            Reset();
            _lease = ForgeCore.InputLease.Take(Plugin.Id);
            _lease.Block(Blocked.ToArray());
        }
        foreach (var share in Shares)
        {
            var raw = UInput.GetKey(share.Key);
            ref var pulse = ref share.Action.Pulse;
            if (raw && !share.RawHeld && !pulse.Busy) { share.Passing = true; Give(share, true); }
            else if (!raw && share.Passing) { share.Passing = false; Give(share, false); }
            if (pulse.GiveDown) Give(share, true);
            if (pulse.GiveUp) Give(share, false);
            share.RawHeld = raw;
        }
    }

    private static void Give(Share share, bool down)
    {
        if (_lease == null) return;
        foreach (var action in share.Game)
        {
            if (down) _lease.Press(action);
            else _lease.Release(action, requireConsumed: false);
        }
    }

    private static bool Live()
    {
        return !KeyBinds.Capture.Active && UIKitFrame.InPlay();
    }

    /// <summary>The shared keys, worked out again when a key, a mode or the game's bindings change (not per frame).</summary>
    private static void Rebuild(List<GameKeys.Binding> game)
    {
        _bookVersion = KeyBinds.Book.Version;
        _gameVersion = GameKeys.Version;
        End();
        Shares.Clear();
        Blocked.Clear();
        var hits = new List<InputAction>();
        foreach (var action in KeyBinds.Actions)
        {
            if (action.Code == 0 || !KeyBinds.Book.HandsBack(action.Index)) continue;
            var key = (KeyCode)action.Code;
            hits.Clear();
            foreach (var binding in game)
                if (binding.Button && binding.Key == key && !hits.Contains(binding.Action)) hits.Add(binding.Action);
            if (hits.Count == 0) continue;
            Shares.Add(new Share { Action = action, Key = key, Game = hits.ToArray() });
            foreach (var hit in hits) if (!Blocked.Contains(hit)) Blocked.Add(hit);
        }
    }

    private static void End()
    {
        // Nothing was taken (outside play, every frame): nothing to hand back, and Reset runs when the next lease is taken.
        if (_lease == null) return;
        _lease.Dispose();
        _lease = null;
        Reset();
    }

    private static void Reset()
    {
        foreach (var share in Shares) { share.Passing = false; share.RawHeld = UInput.GetKey(share.Key); }
    }
}

public sealed partial class UIKitMod
{
    /// <summary>
    /// Registers a key action the player can rebind in the game's settings (Controls, under your group's heading). Read it every frame
    /// through the handle. Keys the player never changed follow <paramref name="defaultKey"/>, also when you change the default in a
    /// later version; the same goes for the trigger mode, which the player picks from <paramref name="allowed"/> in the same settings
    /// row group, together with the action's long-press and double-press times. Two actions whose same press would both fire (same key,
    /// overlapping contexts, any modes but a Tap with a Hold of at least its time, or a Tap with a Double), or an action on a key the game itself uses, are shown in the settings and logged;
    /// ForgeUI never moves anyone's key. A Tap, Hold or Double action alone on a game button's key hands every press that is not its own to that
    /// button (<see cref="KeyMode"/>; a Double action hands back the first press, the second is its own), so it shares the key without a conflict, unless the player sets it to fire together with the game
    /// (the settings' same-key row). All key reading and press timing lives here: a mod only reads the handle. Use the game's own
    /// bindings (<see cref="InputAction"/>) for what the game already has a key for, rather than a second action.
    /// </summary>
    /// <param name="name">Unique and stable inside your mod (the saved key is stored under it), for example <c>Exit</c>.</param>
    /// <param name="nameKey">Your language file key for the action's name in the settings.</param>
    /// <param name="groupKey">Your language file key for the heading the action is listed under.</param>
    /// <param name="defaultKey">A Unity key code name with optional modifiers: <c>G</c>, <c>Mouse3</c>, <c>Ctrl+Shift+G</c>, or <c>None</c>.</param>
    /// <param name="mode">The default trigger mode; it must be among <paramref name="allowed"/>.</param>
    /// <param name="context">When the action is live, for example <c>exosuit</c>; actions in different contexts may share a key. Empty
    /// means everywhere.</param>
    /// <param name="allowed">The trigger modes the player may choose; with one mode the settings show no mode row.</param>
    public KeyAction? AddKeyAction(string name, string nameKey, string groupKey, string defaultKey, KeyMode mode = KeyMode.Press, string context = "", KeyModes allowed = KeyModes.All)
        => KeyBinds.Add(this, name, nameKey, groupKey, defaultKey, mode, context, allowed);

    /// <summary>
    /// Says that while your actions of <paramref name="context"/> are live you hold the game's own input off yourself (ForgeCore
    /// <c>InputLease</c>), as a replay or a vehicle does: those actions then show no conflict with the game's keys in the settings and
    /// hand no press back to the game. Conflicts between Forge actions are still shown.
    /// </summary>
    public void TakeGameInput(string context) => KeyBinds.Book.TakeGameInput(context);
}
