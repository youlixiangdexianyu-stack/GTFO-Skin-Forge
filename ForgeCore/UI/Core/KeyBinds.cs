using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForgeUI;

/// <summary>Modifier keys a chord needs held. Left and right keys count the same.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// When an action fires, which the player picks per action in the key settings (from the modes the mod allows). The action is
/// <c>Pressed</c> when it starts, <c>Held</c> while it lasts and <c>Released</c> when it ends:
/// <see cref="Press"/> starts when the key goes down and lasts while it is held; <see cref="Release"/> fires once when the key comes
/// up; <see cref="Tap"/> fires once when the key comes up within the long-press time; <see cref="Hold"/> starts once the key has been
/// held for the long-press time and lasts until it comes up; <see cref="Double"/> starts when the key goes down a second time within
/// the double-press time of the first press and lasts until it comes up. The player sets the long-press and double-press times per action. A press that does not
/// become a <see cref="Tap"/> or a <see cref="Hold"/> goes to the game's own action on the same key (a short R still reloads), unless
/// the player set the action to fire together with the game's. For <see cref="Double"/> the game gets the first press (it cannot be
/// known to be a first press when it happens) and not the second.
/// </summary>
public enum KeyMode
{
    Press,
    Release,
    Tap,
    Hold,
    Double,
}

/// <summary>The trigger modes a mod allows for one action (<see cref="KeyMode"/>); the player chooses among them.</summary>
[Flags]
public enum KeyModes
{
    None = 0,
    Press = 1,
    Release = 2,
    Tap = 4,
    Hold = 8,
    Double = 16,
    All = Press | Release | Tap | Hold | Double,
}

internal static class KeyModeSet
{
    internal static KeyModes Of(KeyMode mode) => (KeyModes)(1 << (int)mode);

    internal static bool Allows(KeyModes set, KeyMode mode) => (set & Of(mode)) != 0;

    /// <summary>A short or long press is decided only after the key comes up or the long-press time passes; the press that is
    /// not the action's goes to the game.</summary>
    internal static bool Defers(KeyMode mode) => mode is KeyMode.Tap or KeyMode.Hold;

    /// <summary>The action can take the key's other presses away from the game's buttons on it (<see cref="Defers"/>, or the second press of a
    /// <see cref="KeyMode.Double"/>).</summary>
    internal static bool HandsBack(KeyMode mode) => Defers(mode) || mode == KeyMode.Double;

    /// <summary>The allowed mode after <paramref name="mode"/>, wrapping around.</summary>
    internal static KeyMode Next(KeyModes set, KeyMode mode)
    {
        for (var step = 1; step <= 5; step++)
        {
            var next = (KeyMode)(((int)mode + step) % 5);
            if (Allows(set, next)) return next;
        }
        return mode;
    }

    internal static bool TryParse(string text, out KeyMode mode) => Enum.TryParse(text.Trim(), true, out mode) && Enum.IsDefined(typeof(KeyMode), mode);
}

/// <summary>
/// A key with the modifiers that must be held with it: <c>G</c>, <c>Ctrl+Shift+G</c>, <c>Mouse3</c>. The key is a Unity key code name
/// (<c>KeyCode</c>); <see cref="None"/> is "not bound". A chord matches only when exactly its modifiers are held, so <c>G</c> and
/// <c>Ctrl+G</c> are different keys. A modifier key on its own (<c>LeftShift</c>) is a key like any other.
/// </summary>
public readonly struct KeyChord : IEquatable<KeyChord>
{
    private const string NoneText = "None";

    public KeyChord(string key, KeyMods mods = KeyMods.None)
    {
        _key = key ?? "";
        Mods = _key.Length == 0 ? KeyMods.None : mods & ~KeyChords.ModOf(_key);
    }

    /// <summary>The key code name, or empty when unbound.</summary>
    public string Key => _key ?? "";
    private readonly string? _key;

    public KeyMods Mods { get; }

    public static KeyChord None => default;

    public bool IsNone => Key.Length == 0;

    /// <summary>The settings-file form: <c>Ctrl+Shift+G</c>, or <c>None</c>.</summary>
    public override string ToString()
    {
        if (IsNone) return NoneText;
        var text = new StringBuilder();
        if ((Mods & KeyMods.Ctrl) != 0) text.Append("Ctrl+");
        if ((Mods & KeyMods.Shift) != 0) text.Append("Shift+");
        if ((Mods & KeyMods.Alt) != 0) text.Append("Alt+");
        return text.Append(Key).ToString();
    }

    /// <summary>Reads <c>Ctrl+Shift+G</c>, <c>Mouse3</c> or <c>None</c> (case is ignored). <paramref name="known"/> turns a key name into its
    /// canonical spelling, or null for a name that is not a key; the parse fails with the reason in <paramref name="problem"/>.</summary>
    public static bool TryParse(string? text, Func<string, string?> known, out KeyChord chord, out string? problem)
    {
        chord = None;
        problem = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Equals(NoneText, StringComparison.OrdinalIgnoreCase)) return true;
        var parts = value.Split('+');
        var mods = KeyMods.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var mod = KeyChords.ParseMod(parts[i].Trim());
            if (mod == KeyMods.None) { problem = "'" + parts[i].Trim() + "' is not Ctrl, Shift or Alt"; return false; }
            if ((mods & mod) != 0) { problem = "'" + parts[i].Trim() + "' is given twice"; return false; }
            mods |= mod;
        }
        var name = parts[^1].Trim();
        if (name.Length == 0) { problem = "no key after '+'"; return false; }
        var key = known(name);
        if (key == null) { problem = "'" + name + "' is not a key"; return false; }
        chord = new KeyChord(key, mods);
        return true;
    }

    public bool Equals(KeyChord other) => Mods == other.Mods && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Key) * 8 + (int)Mods;
    public static bool operator ==(KeyChord a, KeyChord b) => a.Equals(b);
    public static bool operator !=(KeyChord a, KeyChord b) => !a.Equals(b);
}

/// <summary>Modifier key names (Unity key code names).</summary>
internal static class KeyChords
{
    /// <summary>The modifier <paramref name="key"/> is, or None.</summary>
    internal static KeyMods ModOf(string key) => key switch
    {
        "LeftControl" or "RightControl" => KeyMods.Ctrl,
        "LeftShift" or "RightShift" => KeyMods.Shift,
        "LeftAlt" or "RightAlt" or "AltGr" => KeyMods.Alt,
        _ => KeyMods.None,
    };

    internal static KeyMods ParseMod(string text)
    {
        if (text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || text.Equals("Control", StringComparison.OrdinalIgnoreCase)) return KeyMods.Ctrl;
        if (text.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return KeyMods.Shift;
        if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return KeyMods.Alt;
        return KeyMods.None;
    }
}

/// <summary>
/// Every registered action, its default key and trigger mode and the player's own, with the player's long-press time and whether the
/// action fires together with the game's button on its key. Only what the player changed is stored: an action the player never rebound
/// follows its default, so a new default in a later version reaches it; setting a value equal to the default stores nothing. Entries in
/// the settings file for actions not registered this session (a mod that is not installed) are kept as they are.
/// </summary>
internal sealed class KeyBindBook
{
    internal const string FileHeader = "# Forge key bindings: <mod>/<action> = <key> (for example Ctrl+Shift+G, Mouse3 or None), <mod>/<action>#mode = Press, Release, Tap, Hold or Double, <mod>/<action>#hold = long-press seconds (0.1 to 1), <mod>/<action>#double = double-press seconds (0.1 to 0.6), <mod>/<action>#game = Both (fires together with the game's button on that key). Only changes are listed.";
    private const string ModeSuffix = "#mode", HoldSuffix = "#hold", GameSuffix = "#game", DoubleSuffix = "#double", BothText = "Both";
    internal const float MinHold = 0.1f, MaxHold = 1f, MinWindow = 0.1f, MaxWindow = 0.6f;
    private static readonly float[] HoldSteps = { 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f, 0.75f, 1f };
    private static readonly float[] WindowSteps = { 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f, 0.6f };

    internal sealed class Entry
    {
        internal string FullId = "", Mod = "", Name = "", NameKey = "", GroupKey = "", Context = "";
        internal KeyChord Default;
        internal KeyMode DefaultMode;
        internal KeyModes Allowed;
        /// <summary>The player's key; null follows <see cref="Default"/>.</summary>
        internal KeyChord? Custom;
        /// <summary>The player's trigger mode; null follows <see cref="DefaultMode"/>.</summary>
        internal KeyMode? CustomMode;
        /// <summary>The player's long-press time; null follows <see cref="DefaultHold"/>.</summary>
        internal float? CustomHold;
        /// <summary>The player's double-press time; null follows <see cref="DefaultWindow"/>.</summary>
        internal float? CustomWindow;
        /// <summary>The game's buttons on this key keep reading it and the action fires as well: no hand-back.</summary>
        internal bool WithGame;
        internal KeyChord Effective => Custom ?? Default;
        internal KeyMode Mode => CustomMode ?? DefaultMode;
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _stored = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _known;

    /// <param name="known">Turns a key name into its canonical spelling, or null (see <see cref="KeyChord.TryParse"/>).</param>
    internal KeyBindBook(Func<string, string?> known) => _known = known;

    internal int Count => _entries.Count;
    internal Entry this[int index] => _entries[index];

    /// <summary>Counts every change to a key or mode, so callers can redraw key names.</summary>
    internal int Version { get; private set; }

    /// <summary>The long-press time of every action without its own (ForgeUI's <c>[Keys] LongPressSeconds</c>).</summary>
    internal float DefaultHold { get; set; } = 0.25f;

    /// <summary>The action's long-press time in seconds.</summary>
    internal float HoldOf(int index) => HoldOf(_entries[index]);

    private float HoldOf(Entry entry) => entry.CustomHold ?? DefaultHold;

    /// <summary>The double-press time of every action without its own (ForgeUI's <c>[Keys] DoublePressSeconds</c>).</summary>
    internal float DefaultWindow { get; set; } = 0.3f;

    /// <summary>The action's double-press time in seconds: the longest the two presses may be apart.</summary>
    internal float WindowOf(int index) => _entries[index].CustomWindow ?? DefaultWindow;

    /// <summary>The time the mode's gesture works with: the double-press time for <see cref="KeyMode.Double"/>, else the long-press time.</summary>
    internal float TimeOf(int index) => _entries[index].Mode == KeyMode.Double ? WindowOf(index) : HoldOf(index);

    /// <summary>How long a <see cref="KeyMode.Tap"/> action must wait after a short press to know no second press follows: the longest
    /// double-press time of a <see cref="KeyMode.Double"/> action on the same key (same chord, overlapping context); 0 when there is none.</summary>
    internal float TapWait(int index)
    {
        var entry = _entries[index];
        if (entry.Mode != KeyMode.Tap) return 0;
        var wait = 0f;
        for (var i = 0; i < _entries.Count; i++)
            if (i != index && _entries[i].Mode == KeyMode.Double && _entries[i].Effective == entry.Effective && Overlap(entry.Context, _entries[i].Context))
                wait = Math.Max(wait, WindowOf(i));
        return wait;
    }

    private static bool TryParseWindow(string text, out float seconds)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= MinWindow && seconds <= MaxWindow;

    private static bool TryParseHold(string text, out float seconds)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= MinHold && seconds <= MaxHold;

    /// <summary>Reads the settings file text; bad lines are skipped and described in <paramref name="problems"/>. A key or mode the
    /// player had for an action registered earlier applies at once.</summary>
    internal void Load(string text, List<string> problems)
    {
        _stored.Clear();
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var at = line.IndexOf('=');
            if (at <= 0) { problems.Add("line " + (i + 1) + " has no '='"); continue; }
            var id = line.Substring(0, at).Trim();
            var hash = id.IndexOf('#');
            var suffix = hash < 0 ? "" : id.Substring(hash);
            var (mod, name) = ModIds.Split(hash < 0 ? id : id.Substring(0, hash));
            if (mod.Length == 0 || ModIds.Problem(mod, "mod ID") != null || ModIds.Problem(name, "action name") != null
                || suffix.Length > 0 && suffix != ModeSuffix && suffix != HoldSuffix && suffix != GameSuffix && suffix != DoubleSuffix)
            {
                problems.Add("line " + (i + 1) + ": '" + id + "' is not <mod>/<action>, or that with " + ModeSuffix + ", " + HoldSuffix + ", " + DoubleSuffix + " or " + GameSuffix);
                continue;
            }
            var value = line.Substring(at + 1).Trim();
            if (suffix == ModeSuffix)
            {
                if (!KeyModeSet.TryParse(value, out _)) { problems.Add(id + ": '" + value + "' is not Press, Release, Tap, Hold or Double"); continue; }
            }
            else if (suffix == HoldSuffix)
            {
                if (!TryParseHold(value, out _)) { problems.Add(id + ": '" + value + "' is not a number of seconds from " + MinHold.ToString(CultureInfo.InvariantCulture) + " to " + MaxHold.ToString(CultureInfo.InvariantCulture)); continue; }
            }
            else if (suffix == DoubleSuffix)
            {
                if (!TryParseWindow(value, out _)) { problems.Add(id + ": '" + value + "' is not a number of seconds from " + MinWindow.ToString(CultureInfo.InvariantCulture) + " to " + MaxWindow.ToString(CultureInfo.InvariantCulture)); continue; }
            }
            else if (suffix == GameSuffix)
            {
                if (value != BothText) { problems.Add(id + ": '" + value + "' is not " + BothText); continue; }
            }
            else if (!KeyChord.TryParse(value, _known, out _, out var problem)) { problems.Add(id + ": " + problem); continue; }
            if (_stored.ContainsKey(id)) problems.Add(id + " is listed twice; the last one is used");
            _stored[id] = value;
        }
        foreach (var entry in _entries) Apply(entry);
        Version++;
    }

    /// <summary>The settings file text: the header and one line per change, sorted.</summary>
    internal string Save()
    {
        var text = new StringBuilder(FileHeader).Append('\n');
        foreach (var (id, value) in _stored) text.Append(id).Append(" = ").Append(value).Append('\n');
        return text.ToString();
    }

    /// <summary>Adds an action; returns its index, or -1 with the reason in <paramref name="problem"/>.</summary>
    internal int Register(string mod, string name, string nameKey, string groupKey, string defaultKey, KeyMode mode, KeyModes allowed, string context, out string? problem)
    {
        problem = ModIds.Problem(mod, "mod ID") ?? ModIds.Problem(name, "action name");
        if (problem != null) return -1;
        var id = ModIds.Join(mod, name);
        if (_index.ContainsKey(id)) { problem = "the action '" + id + "' is already registered"; return -1; }
        if (string.IsNullOrWhiteSpace(nameKey)) { problem = "the action '" + id + "' has no name key"; return -1; }
        if (!KeyChord.TryParse(defaultKey, _known, out var chord, out var bad)) { problem = "the default key of '" + id + "' is wrong: " + bad; return -1; }
        if (!Enum.IsDefined(typeof(KeyMode), mode)) { problem = "the default mode of '" + id + "' is not a trigger mode"; return -1; }
        allowed &= KeyModes.All;
        if (!KeyModeSet.Allows(allowed, mode)) { problem = "the default mode " + mode + " of '" + id + "' is not among its allowed modes"; return -1; }
        var entry = new Entry
        {
            FullId = id,
            Mod = mod,
            Name = name,
            NameKey = nameKey,
            GroupKey = groupKey ?? "",
            Context = context ?? "",
            Default = chord,
            DefaultMode = mode,
            Allowed = allowed,
        };
        Apply(entry);
        _index.Add(id, _entries.Count);
        _entries.Add(entry);
        Version++;
        return _entries.Count - 1;
    }

    private void Apply(Entry entry)
    {
        entry.Custom = null;
        entry.CustomMode = null;
        if (_stored.TryGetValue(entry.FullId, out var value) && KeyChord.TryParse(value, _known, out var chord, out _) && chord != entry.Default)
            entry.Custom = chord;
        // A stored mode the mod no longer allows is ignored (the default applies) but kept in the file.
        if (_stored.TryGetValue(entry.FullId + ModeSuffix, out var text) && KeyModeSet.TryParse(text, out var mode)
            && mode != entry.DefaultMode && KeyModeSet.Allows(entry.Allowed, mode))
            entry.CustomMode = mode;
        entry.CustomHold = _stored.TryGetValue(entry.FullId + HoldSuffix, out var hold) && TryParseHold(hold, out var seconds) ? seconds : null;
        entry.CustomWindow = _stored.TryGetValue(entry.FullId + DoubleSuffix, out var window) && TryParseWindow(window, out var gap) ? gap : null;
        entry.WithGame = _stored.ContainsKey(entry.FullId + GameSuffix);
    }

    internal int IndexOf(string fullId) => _index.TryGetValue(fullId, out var i) ? i : -1;

    /// <summary>Gives the action <paramref name="chord"/> (<see cref="KeyChord.None"/> unbinds it). The default itself is stored as "not
    /// changed". Other actions are never touched, also when they have the same key.</summary>
    internal void Set(int index, KeyChord chord)
    {
        var entry = _entries[index];
        if (chord == entry.Default)
        {
            entry.Custom = null;
            _stored.Remove(entry.FullId);
        }
        else
        {
            entry.Custom = chord;
            _stored[entry.FullId] = chord.ToString();
        }
        Version++;
    }

    /// <summary>Gives the action trigger <paramref name="mode"/>; false (nothing changed) when the mod does not allow it.</summary>
    internal bool SetMode(int index, KeyMode mode)
    {
        var entry = _entries[index];
        if (!KeyModeSet.Allows(entry.Allowed, mode)) return false;
        if (mode == entry.DefaultMode)
        {
            entry.CustomMode = null;
            _stored.Remove(entry.FullId + ModeSuffix);
        }
        else
        {
            entry.CustomMode = mode;
            _stored[entry.FullId + ModeSuffix] = mode.ToString();
        }
        Version++;
        return true;
    }

    /// <summary>Gives the action its own long-press time, within <see cref="MinHold"/> and <see cref="MaxHold"/>; the time every action
    /// has by default stores nothing.</summary>
    internal void SetHold(int index, float seconds)
    {
        var entry = _entries[index];
        seconds = Math.Clamp(seconds, MinHold, MaxHold);
        if (Math.Abs(seconds - DefaultHold) < 0.001f)
        {
            entry.CustomHold = null;
            _stored.Remove(entry.FullId + HoldSuffix);
        }
        else
        {
            entry.CustomHold = seconds;
            _stored[entry.FullId + HoldSuffix] = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
        Version++;
    }

    /// <summary>Gives the action its own double-press time, within <see cref="MinWindow"/> and <see cref="MaxWindow"/>; the time every
    /// action has by default stores nothing.</summary>
    internal void SetWindow(int index, float seconds)
    {
        var entry = _entries[index];
        seconds = Math.Clamp(seconds, MinWindow, MaxWindow);
        if (Math.Abs(seconds - DefaultWindow) < 0.001f)
        {
            entry.CustomWindow = null;
            _stored.Remove(entry.FullId + DoubleSuffix);
        }
        else
        {
            entry.CustomWindow = seconds;
            _stored[entry.FullId + DoubleSuffix] = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
        Version++;
    }

    /// <summary>The next double-press time the settings offer after the action's own, wrapping around.</summary>
    internal float NextWindow(int index)
    {
        var now = WindowOf(index);
        foreach (var step in WindowSteps)
            if (step > now + 0.001f) return step;
        return WindowSteps[0];
    }

    /// <summary>The next long-press time the settings offer after the action's own, wrapping around.</summary>
    internal float NextHold(int index)
    {
        var now = HoldOf(index);
        foreach (var step in HoldSteps)
            if (step > now + 0.001f) return step;
        return HoldSteps[0];
    }

    /// <summary>Whether the action fires together with the game's button on its key instead of handing presses back.</summary>
    internal void SetWithGame(int index, bool withGame)
    {
        var entry = _entries[index];
        entry.WithGame = withGame;
        if (withGame) _stored[entry.FullId + GameSuffix] = BothText;
        else _stored.Remove(entry.FullId + GameSuffix);
        Version++;
    }

    internal void Reset(int index)
    {
        Set(index, _entries[index].Default);
        SetMode(index, _entries[index].DefaultMode);
        SetHold(index, DefaultHold);
        SetWindow(index, DefaultWindow);
        SetWithGame(index, false);
    }

    internal void ResetAll()
    {
        foreach (var entry in _entries)
        {
            entry.Custom = null;
            entry.CustomMode = null;
            entry.CustomHold = null;
            entry.CustomWindow = null;
            entry.WithGame = false;
            _stored.Remove(entry.FullId);
            _stored.Remove(entry.FullId + ModeSuffix);
            _stored.Remove(entry.FullId + HoldSuffix);
            _stored.Remove(entry.FullId + DoubleSuffix);
            _stored.Remove(entry.FullId + GameSuffix);
        }
        Version++;
    }

    /// <summary>Whether actions in two contexts can be live at once: the same context, or either is empty (everywhere).</summary>
    internal static bool Overlap(string a, string b) => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>One press of a shared key goes to at most one of the two: a tap to the <see cref="KeyMode.Tap"/> action, a long press
    /// to the <see cref="KeyMode.Hold"/> one, as long as the tap's time does not run past the time the hold starts; a tap action also
    /// shares with a <see cref="KeyMode.Double"/> one (the tap waits out the double-press time, and a second press cancels it). Every other
    /// pair would both fire.</summary>
    private bool Split(Entry a, Entry b)
    {
        if (a.Mode == KeyMode.Double && b.Mode == KeyMode.Tap || a.Mode == KeyMode.Tap && b.Mode == KeyMode.Double) return true;
        if (a.Mode == KeyMode.Hold && b.Mode == KeyMode.Tap) (a, b) = (b, a);
        return a.Mode == KeyMode.Tap && b.Mode == KeyMode.Hold && HoldOf(a) <= HoldOf(b);
    }

    /// <summary>The other actions that share the key of <paramref name="index"/> where both can be live and the same press would fire
    /// both (a <see cref="KeyMode.Tap"/> and a <see cref="KeyMode.Hold"/> action share a key without conflict while the tap's time is
    /// not longer than the hold's); fills <paramref name="into"/> (cleared first) and returns how many.</summary>
    internal int Conflicts(int index, List<int> into)
    {
        into.Clear();
        var entry = _entries[index];
        var chord = entry.Effective;
        if (chord.IsNone) return 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i == index) continue;
            var other = _entries[i];
            if (other.Effective == chord && Overlap(entry.Context, other.Context) && !Split(entry, other)) into.Add(i);
        }
        return into.Count;
    }

    private readonly HashSet<string> _takesGameInput = new(StringComparer.Ordinal);

    /// <summary>While actions of <paramref name="context"/> are live their mod holds the game's own input off (a replay, a vehicle): they
    /// never share a key with a game button, so they show no conflict with the game's keys and hand nothing back.</summary>
    internal void TakeGameInput(string context)
    {
        if (!string.IsNullOrEmpty(context) && _takesGameInput.Add(context)) Version++;
    }

    private bool TakesGameInput(Entry entry) => entry.Context.Length > 0 && _takesGameInput.Contains(entry.Context);

    /// <summary>The action hands back (<see cref="CanHandBack"/>) and the player did not set it to fire together with the game.</summary>
    internal bool HandsBack(int index) => !_entries[index].WithGame && CanHandBack(index);

    /// <summary>The action is <see cref="KeyMode.Tap"/>, <see cref="KeyMode.Hold"/> or <see cref="KeyMode.Double"/> and the only action on its main key, so every
    /// press that is not its own can go to the game's buttons on that key.</summary>
    internal bool CanHandBack(int index)
    {
        var entry = _entries[index];
        var chord = entry.Effective;
        if (chord.IsNone || !KeyModeSet.HandsBack(entry.Mode) || TakesGameInput(entry)) return false;
        for (var i = 0; i < _entries.Count; i++)
            if (i != index && string.Equals(_entries[i].Effective.Key, chord.Key, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The game's own bindings that fire together with <paramref name="index"/>'s key: a game key equal to its main key fires
    /// whatever modifiers are held, except a game button the action hands its other presses back to (<see cref="HandsBack"/>).
    /// <paramref name="game"/> is the game's (action name, key name, is a button) list; matching names go into <paramref name="into"/>
    /// (cleared first), each once.</summary>
    internal int GameConflicts(int index, IReadOnlyList<(string Action, string Key, bool Button)> game, List<string> into)
    {
        into.Clear();
        var chord = _entries[index].Effective;
        if (chord.IsNone || TakesGameInput(_entries[index])) return 0;
        var handsBack = HandsBack(index);
        foreach (var (action, key, button) in game)
            if (string.Equals(key, chord.Key, StringComparison.OrdinalIgnoreCase) && !(handsBack && button) && !into.Contains(action)) into.Add(action);
        return into.Count;
    }
}

/// <summary>
/// One action's per-frame state from whether its key is down, by its trigger mode (<see cref="KeyMode"/>): <see cref="Pressed"/> when
/// the action starts, <see cref="Held"/> while it lasts, <see cref="Released"/> when it ends (an action that fires once is pressed,
/// held and released on that one frame), and <see cref="On"/> flipped at every start. For <see cref="KeyMode.Tap"/> and
/// <see cref="KeyMode.Hold"/>, <see cref="GiveDown"/> and <see cref="GiveUp"/> say when the press that was not the action's should be
/// pressed and let go on the game's own buttons: a short press of a hold action gives a press on the frame the key comes up and the
/// release on the next frame; a long press of a tap action gives the press when the long-press time passes and the release when the
/// key comes up. A <see cref="KeyMode.Double"/> action starts on the second press (the first only waits for the key to come up, it is not
/// <see cref="Busy"/>, so the game gets it) and lasts until that press comes up; a <see cref="KeyMode.Tap"/> action with a double-press
/// action on its key (<c>tapWait</c> above 0) holds a short press back until the double-press time has passed, and a second press
/// within it cancels the tap.
/// </summary>
internal struct KeyGesture
{
    private enum Phase : byte { Idle, Deciding, Active, Given, First, Pending, Swallow }

    internal bool Held, Pressed, Released, On, GiveDown, GiveUp;
    private Phase _phase;
    private float _downAt;
    private bool _giveUpNext, _hasFirst;

    /// <summary><paramref name="seconds"/> is the long-press time for <see cref="KeyMode.Tap"/> and <see cref="KeyMode.Hold"/>, the double-press time
    /// for <see cref="KeyMode.Double"/>; <paramref name="tapWait"/> is, for <see cref="KeyMode.Tap"/>, how long a short press waits for a possible
    /// second press (0 fires at once).</summary>
    internal void Step(bool down, KeyMode mode, float now, float seconds, float tapWait = 0)
    {
        var longPress = seconds;
        var wasInstant = Held && Released;
        Pressed = Released = GiveDown = GiveUp = false;
        if (_giveUpNext) { _giveUpNext = false; GiveUp = true; }
        if (wasInstant) Held = false;
        switch (_phase)
        {
            case Phase.Idle:
                if (!down) break;
                switch (mode)
                {
                    case KeyMode.Press: Start(); _phase = Phase.Active; break;
                    case KeyMode.Release: _phase = Phase.Deciding; break;
                    case KeyMode.Double:
                        if (_hasFirst && now - _downAt <= seconds) { _hasFirst = false; Start(); _phase = Phase.Active; }
                        else { _hasFirst = true; _downAt = now; _phase = Phase.First; }
                        break;
                    default: _phase = Phase.Deciding; _downAt = now; break;
                }
                break;
            case Phase.Deciding:
                if (mode == KeyMode.Release)
                {
                    if (!down) { Instant(); _phase = Phase.Idle; }
                    break;
                }
                var long_ = now - _downAt >= longPress;
                if (!down)
                {
                    if (mode == KeyMode.Tap && !long_ && tapWait > 0) _phase = Phase.Pending;
                    else if (mode == KeyMode.Tap && !long_) Instant();
                    else { GiveDown = true; _giveUpNext = true; }
                    if (_phase != Phase.Pending) _phase = Phase.Idle;
                }
                else if (long_)
                {
                    if (mode == KeyMode.Hold) { Start(); _phase = Phase.Active; }
                    else { GiveDown = true; _phase = Phase.Given; }
                }
                break;
            case Phase.Active:
                if (!down) { Held = false; Released = true; _phase = Phase.Idle; }
                break;
            case Phase.Given:
                if (!down) { GiveUp = true; _phase = Phase.Idle; }
                break;
            case Phase.First:
                if (!down) _phase = Phase.Idle;
                break;
            case Phase.Pending:
                if (now - _downAt > tapWait)
                {
                    Instant();
                    if (down) { _phase = Phase.Deciding; _downAt = now; }
                    else _phase = Phase.Idle;
                }
                else if (down) _phase = Phase.Swallow;
                break;
            case Phase.Swallow:
                if (!down) _phase = Phase.Idle;
                break;
        }
    }

    private void Start()
    {
        Pressed = Held = true;
        On = !On;
    }

    private void Instant()
    {
        Start();
        Released = true;
    }

    /// <summary>Lets go without a start or end (input taken away, the key or mode changed): nothing is pressed or held, a press given
    /// to the game is let go (<see cref="GiveUp"/> on the next step), <see cref="On"/> stays as it is.</summary>
    internal void Drop()
    {
        _giveUpNext = _phase == Phase.Given || _giveUpNext;
        _hasFirst = false;
        Held = Pressed = Released = GiveDown = GiveUp = false;
        _phase = Phase.Idle;
    }

    /// <summary>A press is being decided or the action is running: the key's current press is the action's, not the game's.</summary>
    internal bool Busy => _phase is not (Phase.Idle or Phase.First);
}

/// <summary>
/// Reads one new key in the settings: it waits until every key is up (so the click that started it is not taken), then takes the
/// first key that goes down with the modifiers held at that moment. Esc cancels, Backspace or Delete clears the binding. A modifier
/// pressed and let go on its own is bound by itself.
/// </summary>
internal sealed class KeyCapture
{
    internal enum Result { Waiting, Bound, Cleared, Cancelled }

    private bool _armed, _otherKey;
    private string? _modifier;

    internal bool Active { get; private set; }
    internal KeyChord Chord { get; private set; }

    internal void Begin()
    {
        Active = true;
        _armed = false;
        _otherKey = false;
        _modifier = null;
        Chord = KeyChord.None;
    }

    internal void Stop() => Active = false;

    /// <param name="anyHeld">Whether any key or mouse button is held this frame.</param>
    /// <param name="down">A key that went down this frame other than a modifier, or null.</param>
    /// <param name="modifierDown">A modifier key that went down this frame, or null.</param>
    /// <param name="modifierUp">A modifier key that came up this frame, or null.</param>
    /// <param name="mods">The modifiers held this frame.</param>
    internal Result Step(bool anyHeld, string? down, string? modifierDown, string? modifierUp, KeyMods mods)
    {
        if (!Active) return Result.Waiting;
        if (!_armed)
        {
            if (!anyHeld) _armed = true;
            return Result.Waiting;
        }
        if (down != null)
        {
            _otherKey = true;
            if (down == "Escape") return Finish(Result.Cancelled, KeyChord.None);
            if ((down == "Backspace" || down == "Delete") && mods == KeyMods.None) return Finish(Result.Cleared, KeyChord.None);
            return Finish(Result.Bound, new KeyChord(down, mods));
        }
        if (modifierDown != null && _modifier == null) { _modifier = modifierDown; _otherKey = false; }
        if (modifierUp != null && modifierUp == _modifier && !_otherKey) return Finish(Result.Bound, new KeyChord(modifierUp));
        return Result.Waiting;
    }

    private Result Finish(Result result, KeyChord chord)
    {
        Active = false;
        Chord = chord;
        return result;
    }
}
