using System;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using GameData;
using UnityEngine;

namespace ForgeUI;

/// <summary>Readouts of what the game's own HUD shows, so a mod that hides it (<see cref="UIKitMod.HideGameHud"/>) can draw the same
/// information in its own style. Each is read on the main thread; values are refreshed once per frame on first access, or when the game
/// changes them; texts follow the game's language because they are the game's own.</summary>
public static partial class UIKit
{
    /// <summary>The local player's health and state.</summary>
    public static GameVitals Vitals { get { PatchGroups.Ensure(PatchGroup.Vitals); return VitalsReading; } }
    private static readonly GameVitals VitalsReading = new();

    /// <summary>The other players in the level.</summary>
    public static GameTeammates Teammates { get; } = new();

    /// <summary>The objectives the game shows.</summary>
    public static GameObjectives Objectives { get { PatchGroups.Ensure(PatchGroup.Objectives); return ObjectivesReading; } }
    internal static readonly GameObjectives ObjectivesReading = new();

    /// <summary>The game's interaction prompt and the message line under it.</summary>
    public static GameInteraction Interaction { get { PatchGroups.Ensure(PatchGroup.Interaction); return InteractionReading; } }
    internal static readonly GameInteraction InteractionReading = new();

    /// <summary>The scan nearest the local player: who is inside and how many it wants.</summary>
    public static GameScan Scan { get { GameScan.Install(); return ScanReading; } }
    private static readonly GameScan ScanReading = new();

    /// <summary>The game's subtitles.</summary>
    public static GameSubtitles Subtitles { get { PatchGroups.Ensure(PatchGroup.Subtitles); return SubtitlesReading; } }
    private static readonly GameSubtitles SubtitlesReading = new();

    /// <summary>The weapon list: the filled slots, what is in hand and the carried item.</summary>
    public static GameInventory Inventory { get { PatchGroups.Ensure(PatchGroup.Inventory); return InventoryReading; } }
    internal static readonly GameInventory InventoryReading = new();

    /// <summary>The warden intel and sub-objective messages the game flashes.</summary>
    public static GameWardenIntel Intel { get { PatchGroups.Ensure(PatchGroup.Intel); return IntelReading; } }
    private static readonly GameWardenIntel IntelReading = new();

    /// <summary>The pick-up messages the game lists (what was just picked up).</summary>
    public static GamePickups Pickups { get { PatchGroups.Ensure(PatchGroup.Pickups); return PickupsReading; } }
    internal static readonly GamePickups PickupsReading = new();

    /// <summary>The booster implants the game shows as active.</summary>
    public static GameBoosters Boosters { get { PatchGroups.Ensure(PatchGroup.Boosters); return BoostersReading; } }
    private static readonly GameBoosters BoostersReading = new();

    /// <summary>Where the local player is: the zone, the view's heading and the height.</summary>
    public static GameLocation Location { get; } = new();

    /// <summary>The azimuth of <paramref name="world"/> from where the player looks (0 ahead, 90 right, ±180 behind, −90 left); NaN
    /// without a camera.</summary>
    public static float Bearing(Vector3 world)
    {
        var camera = HudScreen.WorldCamera();
        if (camera == null) return float.NaN;
        var t = camera.transform;
        var forward = t.forward;
        var to = world - t.position;
        return ForgeUI.Bearing.Relative(forward.x, forward.z, to.x, to.z);
    }
}

/// <summary>Where the local player is, as three short texts: the zone as the game's map names it ("ZONE 49"), the view's horizontal
/// heading ("090° E") and the height in whole metres ("-212 M"). A value the game does not give (no zone, no camera, outside a level)
/// reads empty. Read from the game once per frame (the zone twice a second); a text is made once per distinct value, so reading them
/// every frame allocates nothing after the first time each value shows. See <see cref="UIKit.Location"/>.</summary>
public sealed class GameLocation
{
    private const float ZoneEvery = 0.5f;
    private readonly LocationReadout _readout = new();
    private int _frame = -1;
    private float _zoneAt = -1f;

    internal GameLocation() { }

    /// <summary>"ZONE 49"; empty without a zone.</summary>
    public string Zone { get { Read(); return _readout.Zone; } }

    /// <summary>"090° E": the view's heading, 0 to 359 degrees clockwise from north, and the nearest of N, E, S and W; empty without a camera.</summary>
    public string Heading { get { Read(); return _readout.Heading; } }

    /// <summary>"-212 M": the player's world height in whole metres; empty outside a level.</summary>
    public string Altitude { get { Read(); return _readout.Altitude; } }

    /// <summary>Bumped whenever a text above changes, so a label can redraw only then.</summary>
    public int Version { get { Read(); return _readout.Version; } }

    private void Read()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        try
        {
            var agent = ForgeCore.Players.Local;
            if (agent == null || agent.WasCollected)
            {
                _readout.Clear();
                _zoneAt = -1f;
                return;
            }
            var now = ForgeCore.Frame.GameTime;
            if (now >= _zoneAt)
            {
                _zoneAt = now + ZoneEvery;
                var node = agent.CourseNode;
                _readout.SetZone(node != null && node.m_zone != null ? node.m_zone.NavInfo.Number : (int?)null);
            }
            var forward = agent.FPSCamera != null ? agent.FPSCamera.Forward : agent.transform.forward;
            _readout.SetHeading(forward.x, forward.z);
            _readout.SetAltitude(agent.transform.position.y);
        }
        catch (Exception error)
        {
            Plugin.Error("ui-location-readout-could-not-be-read", "The location readout could not be read: " + error);
        }
    }
}

/// <summary>The local player's health, read from the game once per frame. See <see cref="UIKit.Vitals"/>.</summary>
public sealed class GameVitals
{
    private int _frame = -1;
    private float _health, _infection;
    private bool _alive, _downed;
    private PlayerHold _held;

    internal GameVitals() { }

    /// <summary>0..1; 0 outside a level.</summary>
    public float Health { get { Read(); return _health; } }

    /// <summary>0..1.</summary>
    public float Infection { get { Read(); return _infection; } }

    public bool Alive { get { Read(); return _alive; } }

    public bool Downed { get { Read(); return _downed; } }

    /// <summary>Whether something holds the local player: grabbed (a big enemy's strangle, a snatcher, a trap) or stunned; read from
    /// the game's locomotion state.</summary>
    public PlayerHold Held { get { Read(); return _held; } }

    /// <summary>The game's melee buff is active (the argument of its <c>UpdateHealth</c>).</summary>
    public bool MeleeBuff { get; private set; }

    [PatchGroup(PatchGroup.Vitals)]
    [HarmonyPatch(typeof(PlayerGuiLayer), nameof(PlayerGuiLayer.UpdateHealth))]
    private static class HealthShown
    {
        private static void Postfix(bool meleeBuffActive) => UIKit.Vitals.MeleeBuff = meleeBuffActive;
    }

    private void Read()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _health = _infection = 0f;
        _alive = _downed = false;
        _held = PlayerHold.None;
        try
        {
            var agent = ForgeCore.Players.Local;
            if (agent == null || agent.WasCollected) return;
            var damage = agent.Damage;
            if (damage != null)
            {
                _health = damage.HealthMax > 0f ? Ease.Clamp01(damage.Health / damage.HealthMax) : 0f;
                _infection = Ease.Clamp01(damage.Infection);
            }
            _alive = agent.Alive;
            _downed = GameTeammates.IsDowned(agent);
            _held = GameTeammates.HoldOf(agent);
        }
        catch (Exception error) { Plugin.Error("ui-reading-local-players-health-failed", "Reading the local player's health failed: " + error); }
    }
}

/// <summary>What holds the local player (<see cref="GameVitals.Held"/>).</summary>
public enum PlayerHold : byte
{
    None,
    /// <summary>Grabbed: a big enemy's strangle, a snatcher's pull or a trap.</summary>
    Grabbed,
    Stunned
}

/// <summary>One other player in the level.</summary>
public readonly struct TeammateState
{
    internal TeammateState(SNet_Player player, string name, float health, bool downed, PlayerHold held, bool alive, int slot, float infection, float ammo, bool speaking)
    {
        Player = player;
        Name = name;
        Health = health;
        Downed = downed;
        Held = held;
        Alive = alive;
        Slot = slot;
        Infection = infection;
        Ammo = ammo;
        Speaking = speaking;
    }

    /// <summary>0..1, or −1 when the game gives none for this player.</summary>
    public float Infection { get; }
    /// <summary>(standard + special ammo) / (their caps), 0..1, from the player's synced ammo storage (read four times a second); −1 when unreadable.</summary>
    public float Ammo { get; }
    /// <summary>The player is the game's current voice speaker (read four times a second).</summary>
    public bool Speaking { get; }
    public SNet_Player Player { get; }
    public string Name { get; }
    /// <summary>0..1.</summary>
    public float Health { get; }
    public bool Downed { get; }
    /// <summary>What holds this player, from its synced locomotion state.</summary>
    public PlayerHold Held { get; }
    public bool Alive { get; }
    /// <summary>The game's character slot, 0..3.</summary>
    public int Slot { get; }
}

/// <summary>The other players in the level, in slot order, read once per frame; names are read again only once a second, ammunition and
/// the speaker four times a second.</summary>
public sealed class GameTeammates
{
    private const int Max = 8;
    private readonly TeammateState[] _states = new TeammateState[Max];
    private readonly Cache[] _cache = new Cache[Max];
    private int _count, _frame = -1;
    private float _nextNames, _nextSlow;
    private ulong _speaker;
    /// <summary>What was read for one teammate on an earlier frame, found again by the agent's pointer whatever the order.</summary>
    private struct Cache
    {
        internal IntPtr Agent;
        internal int Frame;
        internal string? Name;
        internal float Ammo;
        internal bool Speaking;
    }

    internal GameTeammates()
    {
    }

    public int Count { get { Read(); return _count; } }

    public TeammateState Get(int index)
    {
        Read();
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return _states[index];
    }

    internal static bool IsDowned(PlayerAgent agent)
    {
        return ForgeCore.Players.Motion(agent) == ForgeCore.PlayerMotion.Downed;
    }

    internal static PlayerHold HoldOf(PlayerAgent agent)
    {
        return ForgeCore.Players.Motion(agent) switch
        {
            ForgeCore.PlayerMotion.GrabbedByTank or ForgeCore.PlayerMotion.GrabbedByPouncer or ForgeCore.PlayerMotion.GrabbedByTrap => PlayerHold.Grabbed,
            ForgeCore.PlayerMotion.Stunned => PlayerHold.Stunned,
            _ => PlayerHold.None
        };
    }

    private int CacheOf(IntPtr agent)
    {
        var free = -1;
        for (var i = 0; i < Max; i++)
        {
            if (_cache[i].Agent == agent) return i;
            if (free < 0 && (_cache[i].Agent == IntPtr.Zero || _cache[i].Frame != _frame)) free = i;
        }
        free = Math.Max(free, 0);
        _cache[free] = new Cache { Agent = agent, Frame = _frame, Name = null, Ammo = -1f };
        return free;
    }

    private void Read()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _count = 0;
        try
        {
            var now = ForgeCore.Frame.UnscaledTime;
            _namesDue = now >= _nextNames;
            if (_namesDue) _nextNames = now + 1f;
            _slowDue = now >= _nextSlow;
            if (_slowDue)
            {
                _nextSlow = now + 0.25f;
                _speaker = Speaker();
            }
            ForgeCore.Players.Each(this, static (self, info) => self.ReadTeammate(info));
            // Slot order, insertion sort on at most a handful.
            for (var i = 1; i < _count; i++)
                for (var j = i; j > 0 && _states[j].Slot < _states[j - 1].Slot; j--)
                    (_states[j], _states[j - 1]) = (_states[j - 1], _states[j]);
        }
        catch (Exception error) { Plugin.Error("ui-reading-teammates-failed", "Reading the teammates failed: " + error); }
    }

    private bool _namesDue, _slowDue;

    private void ReadTeammate(ForgeCore.PlayerInfo info)
    {
        var agent = info.Agent;
        if (_count >= Max || info.IsLocal) return;
        var at = CacheOf(agent.Pointer);
        var fresh = _cache[at].Name == null;
        _cache[at].Frame = _frame;
        if (fresh || _namesDue) _cache[at].Name = agent.PlayerName ?? "";
        var owner = agent.Owner;
        if (fresh || _slowDue)
        {
            _cache[at].Ammo = AmmoOf(owner);
            _cache[at].Speaking = _speaker != 0UL && owner != null && owner.Lookup == _speaker;
        }
        var name = _cache[at].Name ?? "";
        var damage = agent.Damage;
        var health = damage != null && damage.HealthMax > 0f ? Ease.Clamp01(damage.Health / damage.HealthMax) : 0f;
        var infection = damage != null ? Ease.Clamp01(damage.Infection) : -1f;
        _states[_count++] = new TeammateState(agent.Owner, name, health, IsDowned(agent), HoldOf(agent), agent.Alive, info.Slot,
            infection, _cache[at].Ammo, _cache[at].Speaking);
    }

    /// <summary>The player the game's voice chat currently singles out as speaking (its lookup), or 0.</summary>
    private static ulong Speaker()
    {
        var chat = PlayerChatManager.Current;
        var speaker = chat != null && !chat.WasCollected ? chat.CurrentSNetSpeaker : null;
        return speaker != null ? speaker.Lookup : 0UL;
    }

    /// <summary>The teammate's ammunition from the ammo storage the game syncs for every player's backpack; −1 when unreadable.</summary>
    private static float AmmoOf(SNet_Player? owner)
    {
        if (owner == null || !PlayerBackpackManager.TryGetBackpack(owner, out var pack) || pack == null) return -1f;
        var storage = pack.AmmoStorage;
        var standard = storage?.StandardAmmo;
        if (standard == null) return -1f;
        var special = storage!.SpecialAmmo;
        return TeammateAmmo.Rel(standard.AmmoInPack, standard.AmmoMaxCap, special?.AmmoInPack ?? 0f, special?.AmmoMaxCap ?? 0f);
    }
}

/// <summary>The objective tier of a layer.</summary>
public enum ObjectiveTier : byte
{
    Main,
    Secondary,
    Overload,
}

/// <summary>The game's sub-objective step of a layer (the same names as the game's own steps).</summary>
public enum ObjectiveStatus : byte
{
    FindLocationInfo,
    FindLocationInfoHelp,
    GoToZone,
    GoToZoneHelp,
    InZoneFindItem,
    InZoneFindItemHelp,
    SolveItem,
    SolveItemHelp,
    GoToWinCondition,
    GoToWinConditionHelp,
}

/// <summary>One objective layer as the game shows it.</summary>
public readonly struct ObjectiveLayer
{
    internal ObjectiveLayer(ObjectiveTier tier, string tag, string main, string sub, ObjectiveStatus status, bool visible)
    {
        Tier = tier;
        Tag = tag;
        Main = main;
        Sub = sub;
        Status = status;
        Visible = visible;
    }

    public ObjectiveTier Tier { get; }
    /// <summary>The game's own label of the layer, in the game's language.</summary>
    public string Tag { get; }
    public string Main { get; }
    public string Sub { get; }
    public ObjectiveStatus Status { get; }
    public bool Visible { get; }
}

/// <summary>A progression objective line (for example powering generators).</summary>
public readonly struct ObjectiveLine
{
    internal ObjectiveLine(string header, string text)
    {
        Header = header;
        Text = text;
    }

    public string Header { get; }
    public string Text { get; }
}

/// <summary>
/// The objectives the game shows, captured from the game's own calls (its text functions) and read again twice a second; only changes
/// move <see cref="Version"/> and raise <see cref="Changed"/>. See <see cref="UIKit.Objectives"/>.
/// </summary>
public sealed class GameObjectives
{
    private const int Lines = 8;
    private const float Every = 0.5f;
    private readonly Il2CppSystem.Func<string>?[] _main = new Il2CppSystem.Func<string>?[3];
    private readonly WardenObjectiveDataBlock?[] _data = new WardenObjectiveDataBlock?[3];
    private readonly eWardenSubObjectiveStatus[] _step = new eWardenSubObjectiveStatus[3];
    private readonly bool[] _visible = new bool[3];
    private readonly ObjectiveLayer[] _layers = new ObjectiveLayer[3];
    private readonly int[] _progressionId = new int[Lines];
    private readonly Il2CppSystem.Func<string>?[] _progressionHeader = new Il2CppSystem.Func<string>?[Lines], _progressionText = new Il2CppSystem.Func<string>?[Lines];
    private readonly ObjectiveLine[] _progression = new ObjectiveLine[Lines];
    private Il2CppSystem.Func<string>? _header;
    private string _headerText = "";
    private int _layerCount, _progressionCount;
    private float _next;
    private bool _dirty;

    /// <summary>The first read: the panels are read in full once instead of waiting for the game to change one.</summary>
    internal void ReadInFull() => _dirty = true;

    internal GameObjectives()
    {
        for (var i = 0; i < 3; i++) _layers[i] = new ObjectiveLayer((ObjectiveTier)i, "", "", "", default, false);
    }

    public int Version { get; private set; }

    /// <summary>Raised on the main thread after something changed.</summary>
    public event Action? Changed;

    public string Header { get { Tick(); return _headerText; } }

    /// <summary>How many teammates the game's map prompt shows as looking at the map; −1 when the game has no such icons.</summary>
    public int MapViewers { get; private set; } = -1;

    /// <summary>The game's map prompt text (the line that names the key that opens the map), as the game shows it.</summary>
    public string MapPrompt { get; private set; } = "";

    /// <summary>The game's objective timer (the countdown above the objectives) is running.</summary>
    public bool TimerActive { get; private set; }

    /// <summary>The timer's title as the game last set it.</summary>
    public string TimerTitle { get; private set; } = "";

    /// <summary>The timer's text as the game shows it; read again once per timer second.</summary>
    public string TimerText { get; private set; } = "";

    private int _timerSecond = int.MinValue;

    private void SetTimer(bool active, string title, string text)
    {
        if (TimerActive == active && TimerTitle == title && TimerText == text) return;
        TimerActive = active;
        TimerTitle = title;
        TimerText = text;
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-objectives-listener-failed", "An objectives listener failed: " + error); }
    }

    /// <summary>The visible layers, Main first.</summary>
    public int Count { get { Tick(); return _layerCount; } }

    public ObjectiveLayer Layer(int index)
    {
        Tick();
        if ((uint)index >= (uint)_layerCount) throw new ArgumentOutOfRangeException(nameof(index));
        var seen = -1;
        for (var i = 0; i < 3; i++)
            if (_layers[i].Visible && ++seen == index) return _layers[i];
        return default;
    }

    public int ProgressionCount { get { Tick(); return _progressionCount; } }

    public ObjectiveLine Progression(int index)
    {
        Tick();
        if ((uint)index >= (uint)_progressionCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _progression[index];
    }

    /// <summary>Per frame from the driver: reads the texts again twice a second (or at once after a game call).</summary>
    internal void Tick()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (!_dirty && now < _next) return;
        _dirty = false;
        _next = now + Every;
        var changed = false;
        try
        {
            changed |= Set(ref _headerText, Call(_header));
            var objectives = GuiManager.PlayerLayer?.m_wardenObjective;
            var viewers = CountViewers(objectives);
            if (viewers != MapViewers) { MapViewers = viewers; changed = true; }
            var prompt = objectives?.m_pressBtn_Text;
            if (Set(ref _mapPrompt, prompt != null && !prompt.WasCollected ? prompt.text ?? "" : "")) { MapPrompt = _mapPrompt; changed = true; }
            var count = 0;
            for (var i = 0; i < 3; i++)
            {
                var old = _layers[i];
                var visible = _visible[i] && _main[i] != null;
                var main = visible ? Call(_main[i]) : "";
                var sub = visible && objectives != null && _data[i] != null ? Safe(objectives.GetSubObjectiveText(LayerType(i), _data[i], _step[i], false)) : "";
                var tag = old.Tag.Length > 0 || objectives == null ? old.Tag : Safe(PUI_GameObjectives.GetGameObjectiveTagFromLayer_Localized(LayerType(i)));
                var status = (ObjectiveStatus)(byte)_step[i];
                if (old.Visible != visible || old.Main != main || old.Sub != sub || old.Tag != tag || old.Status != status)
                {
                    _layers[i] = new ObjectiveLayer((ObjectiveTier)i, tag, main, sub, status, visible);
                    changed = true;
                }
                if (visible) count++;
            }
            changed |= count != _layerCount;
            _layerCount = count;
            for (var i = 0; i < _progressionCount; i++)
            {
                var header = Call(_progressionHeader[i]);
                var text = Call(_progressionText[i]);
                if (header == _progression[i].Header && text == _progression[i].Text) continue;
                _progression[i] = new ObjectiveLine(header, text);
                changed = true;
            }
        }
        catch (Exception error) { Plugin.Error("ui-reading-objectives-failed", "Reading the objectives failed: " + error); }
        if (!changed) return;
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-objectives-listener-failed", "An objectives listener failed: " + error); }
    }

    private string _mapPrompt = "";

    private static int CountViewers(PUI_GameObjectives? objectives)
    {
        var icons = objectives?.m_playersActiveInMapIcons;
        if (icons == null) return -1;
        var n = 0;
        for (var i = 0; i < icons.Length; i++)
        {
            var icon = icons[i];
            if (icon != null && !icon.WasCollected && icon.enabled && icon.gameObject.activeSelf) n++;
        }
        return n;
    }

    private static LG_LayerType LayerType(int i) => (LG_LayerType)(byte)i;

    private static bool Set(ref string field, string value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }

    private static string Call(Il2CppSystem.Func<string>? text)
    {
        if (text == null) return "";
        try { return text.Invoke() ?? ""; }
        catch (Exception error)
        {
            Plugin.Error("ui-game-text-readout", "A game text readout could not be read: " + error);
            return "";
        }
    }

    private static string Safe(string? text) => text ?? "";

    internal void Clear()
    {
        for (var i = 0; i < 3; i++) { _main[i] = null; _data[i] = null; _visible[i] = false; }
        _header = null;
        _progressionCount = 0;
        _dirty = true;
        _timerSecond = int.MinValue;
        SetTimer(false, "", "");
    }

    [PatchGroup(PatchGroup.Objectives)]
    [HarmonyPatch(typeof(PUI_ObjectiveTimer), nameof(PUI_ObjectiveTimer.SetTimerActive))]
    private static class TimerActiveChanged
    {
        private static void Postfix(bool value)
        {
            var o = UIKit.Objectives;
            o._timerSecond = int.MinValue;
            o.SetTimer(value, o.TimerTitle, value ? o.TimerText : "");
        }
    }

    [PatchGroup(PatchGroup.Objectives)]
    [HarmonyPatch(typeof(PUI_ObjectiveTimer), nameof(PUI_ObjectiveTimer.UpdateTimerTitle))]
    private static class TimerTitleChanged
    {
        private static void Postfix(string timerTitle)
        {
            var o = UIKit.Objectives;
            o.SetTimer(o.TimerActive, timerTitle ?? "", o.TimerText);
        }
    }

    /// <summary>The game writes the timer's text every frame; it is read only when the whole second changed.</summary>
    [PatchGroup(PatchGroup.Objectives)]
    [HarmonyPatch(typeof(PUI_ObjectiveTimer), nameof(PUI_ObjectiveTimer.UpdateTimerText))]
    private static class TimerTextChanged
    {
        private static void Postfix(PUI_ObjectiveTimer __instance, float timer)
        {
            var o = UIKit.Objectives;
            var second = (int)Math.Floor(timer);
            if (second == o._timerSecond) return;
            o._timerSecond = second;
            var text = __instance.m_timerText;
            o.SetTimer(o.TimerActive, o.TimerTitle, text != null && !text.WasCollected ? text.text ?? "" : "");
        }
    }

    [PatchGroup(PatchGroup.Objectives)]
    [HarmonyPatch(typeof(PlayerGuiLayer), nameof(PlayerGuiLayer.UpdateObjectives))]
    private static class LayersPatch
    {
        private static void Postfix(LG_LayerType layer, Il2CppSystem.Func<string> mainObjective, WardenObjectiveDataBlock data, eWardenSubObjectiveStatus sub, bool visible)
        {
            var o = UIKit.Objectives;
            var i = (int)layer;
            if ((uint)i >= 3u) return;
            o._main[i] = mainObjective;
            o._data[i] = data;
            o._step[i] = sub;
            o._visible[i] = visible;
            o._dirty = true;
        }
    }

    [PatchGroup(PatchGroup.Objectives)]
    [HarmonyPatch(typeof(PlayerGuiLayer), nameof(PlayerGuiLayer.UpdateObjectiveHeader))]
    private static class HeaderPatch
    {
        private static void Postfix(Il2CppSystem.Func<string> header)
        {
            UIKit.Objectives._header = header;
            UIKit.Objectives._dirty = true;
        }
    }

    /// <summary>The objective panel's readbacks, taken from ForgeCore's one patch of each panel method.</summary>
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.ProgressionObjectiveSet.After((call, _) => SetProgression(call.Id, call.Header, call.Text));
        ForgeCore.GameHooks.ProgressionObjectiveRemoved.After((call, _) => RemoveProgression(call.Id));
        ForgeCore.GameHooks.ObjectivesCleaned.After((_, _) => UIKit.Objectives.Clear());
    }

    private static void SetProgression(int ID, Il2CppSystem.Func<string> getHeaderText, Il2CppSystem.Func<string> getSubObjtext)
    {
        var o = UIKit.Objectives;
        var at = -1;
        for (var i = 0; i < o._progressionCount; i++) if (o._progressionId[i] == ID) { at = i; break; }
        if (at < 0)
        {
            if (o._progressionCount >= Lines) return;
            at = o._progressionCount++;
            o._progression[at] = new ObjectiveLine("", "");
        }
        o._progressionId[at] = ID;
        o._progressionHeader[at] = getHeaderText;
        o._progressionText[at] = getSubObjtext;
        o._dirty = true;
    }

    private static void RemoveProgression(int ID)
    {
        var o = UIKit.Objectives;
        for (var i = 0; i < o._progressionCount; i++)
        {
            if (o._progressionId[i] != ID) continue;
            for (var j = i; j < o._progressionCount - 1; j++)
            {
                o._progressionId[j] = o._progressionId[j + 1];
                o._progressionHeader[j] = o._progressionHeader[j + 1];
                o._progressionText[j] = o._progressionText[j + 1];
                o._progression[j] = o._progression[j + 1];
            }
            o._progressionCount--;
            o.Version++;
            o._dirty = true;
            return;
        }
    }

}

/// <summary>
/// The game's interaction prompt and its message line, as the game shows them: the prompt (text, key, style, the hold bar) and the
/// message line under it (the text of a scan, a reactor or uplink step, a door's or a terminal's note, being downed; its style and its
/// progress bar). See <see cref="UIKit.Interaction"/>.
/// <para>Texts and styles are taken where the game writes them into its two prompt pieces (<c>PUI_InteractionPrompt.SetPrompt</c>,
/// <c>SetMessage</c>, <c>SetStyle</c>), which every interaction calls; what is shown and how far the bars are filled is read from those
/// pieces after every game frame. The interaction layer's own setters are not watched: the game's build has them inlined into most
/// interactions (pickups, terminals, doors, holds), so only a few of their calls ever reach a hook.</para>
/// </summary>
public sealed class GameInteraction
{
    private IntPtr _layerPointer, _promptPointer, _messagePointer;
    private PUI_InteractionPrompt? _prompt, _message;
    private GameObject? _promptObject, _messageObject, _promptBarObject, _messageBarObject;
    private SpriteRenderer? _promptBar, _messageBar;
    private float _promptBarFull, _messageBarFull;

    internal GameInteraction() { }

    /// <summary>The prompt is on screen.</summary>
    public bool Visible { get; private set; }
    public string Text { get; private set; } = "";
    public string Key { get; private set; } = "";
    public InteractionStyle Style { get; private set; }
    /// <summary>The prompt is shown in the game's usable style, not as a warning or a scan.</summary>
    public bool Usable => Style == InteractionStyle.Usable;
    /// <summary>The hold bar's fill 0..1, or −1 while the game shows none.</summary>
    public float Progress { get; private set; } = -1f;
    /// <summary>The extra line of the interaction in sight (what its <c>OnAdditionalInteractionText</c> gives); empty when none or once the prompt is down.</summary>
    public string AdditionalText { get; private set; } = "";
    /// <summary>The message line is on screen.</summary>
    public bool MessageVisible { get; private set; }
    public string Message { get; private set; } = "";
    public InteractionStyle MessageStyle { get; private set; } = InteractionStyle.Message;
    /// <summary>The message line's bar (a scan's or a reactor step's progress) 0..1, or −1 while the game shows none.</summary>
    public float MessageProgress { get; private set; } = -1f;
    public int Version { get; private set; }
    public event Action? Changed;

    private void Bump()
    {
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-interaction-listener-failed", "An interaction listener failed: " + error); }
    }

    /// <summary>After the game's frame, from the driver: what the two pieces show now.</summary>
    internal void Tick()
    {
        try
        {
            Find();
            var visible = Shown(_promptObject);
            var progress = visible ? Bar(_promptBarObject, _promptBar, _promptBarFull) : -1f;
            var messageVisible = Shown(_messageObject);
            var messageProgress = messageVisible ? Bar(_messageBarObject, _messageBar, _messageBarFull) : -1f;
            var changed = visible != Visible || messageVisible != MessageVisible || progress != Progress || messageProgress != MessageProgress;
            if (!visible && AdditionalText.Length > 0) { AdditionalText = ""; changed = true; }
            if (!changed) return;
            Visible = visible;
            Progress = progress;
            MessageVisible = messageVisible;
            MessageProgress = messageProgress;
            Bump();
        }
        catch (Exception error) { Plugin.Error("ui-reading-interaction-prompt-failed", "Reading the interaction prompt failed: " + error); }
    }

    /// <summary>The game's interaction layer and its two pieces, looked up again only when the layer is another one (a new level).</summary>
    private void Find()
    {
        var layer = GuiManager.InteractionLayer;
        var pointer = layer != null && !layer.WasCollected ? layer.Pointer : IntPtr.Zero;
        if (pointer == _layerPointer && (_prompt == null || !_prompt.WasCollected) && (_message == null || !_message.WasCollected)) return;
        _layerPointer = pointer;
        _prompt = pointer != IntPtr.Zero ? layer!.m_interactPrompt : null;
        _message = pointer != IntPtr.Zero ? layer!.m_message : null;
        _promptPointer = Piece(_prompt, out _promptObject, out _promptBar, out _promptBarObject, out _promptBarFull);
        _messagePointer = Piece(_message, out _messageObject, out _messageBar, out _messageBarObject, out _messageBarFull);
    }

    private static IntPtr Piece(PUI_InteractionPrompt? piece, out GameObject? shown, out SpriteRenderer? bar, out GameObject? barShown, out float full)
    {
        shown = null; bar = null; barShown = null; full = 0f;
        if (piece == null || piece.WasCollected) return IntPtr.Zero;
        shown = piece.gameObject;
        bar = piece.m_timer;
        if (bar != null && !bar.WasCollected) barShown = bar.gameObject;
        full = piece.m_timerWidthMax;
        return piece.Pointer;
    }

    // The game shows each piece, and its bar, by switching its object on and off (InteractPromptVisible, MessageVisible, TimerVisible,
    // MessageTimerVisible), and fills a bar by widening its sprite to fill × m_timerWidthMax (SetTimerFill).
    private static bool Shown(GameObject? o) => o != null && !o.WasCollected && o.activeSelf;

    private static float Bar(GameObject? shown, SpriteRenderer? bar, float full) =>
        bar == null || bar.WasCollected ? -1f : InteractionReading.Fill(Shown(shown), bar.size.x, full);

    private void Prompt(PUI_InteractionPrompt piece, string header, string button)
    {
        Find();
        var p = piece.Pointer;
        if (p == _promptPointer)
        {
            if (Text == header && Key == button) return;
            Text = header;
            Key = button;
            Bump();
        }
        else if (p == _messagePointer) MessageText(button.Length > 0 ? header + " " + button : header);
    }

    private void MessageText(string text)
    {
        if (Message == text) return;
        Message = text;
        Bump();
    }

    /// <summary>A message the game wrote into its reusable character buffer (a scan's line, every frame): a string is made only when the
    /// characters differ from the line already held.</summary>
    private void MessageText(TenCC.Utils.CharBuffer? buffer)
    {
        var chars = buffer?.m_chars;
        var length = chars == null ? 0 : Math.Min(buffer!.m_length, chars.Length);
        if (length == Message.Length)
        {
            var same = true;
            for (var i = 0; i < length && same; i++) same = chars![i] == Message[i];
            if (same) return;
        }
        var text = new char[length];
        for (var i = 0; i < length; i++) text[i] = chars![i];
        Message = new string(text);
        Bump();
    }

    [PatchGroup(PatchGroup.Interaction)]
    [HarmonyPatch(typeof(PUI_InteractionPrompt), nameof(PUI_InteractionPrompt.SetPrompt), typeof(string), typeof(string))]
    private static class PromptText
    {
        private static void Postfix(PUI_InteractionPrompt __instance, string header, string button) =>
            UIKit.Interaction.Prompt(__instance, header ?? "", button ?? "");
    }

    [PatchGroup(PatchGroup.Interaction)]
    [HarmonyPatch(typeof(PUI_InteractionPrompt), nameof(PUI_InteractionPrompt.SetMessage), typeof(string))]
    private static class MessageString
    {
        private static void Postfix(PUI_InteractionPrompt __instance, string message)
        {
            var g = UIKit.Interaction;
            g.Find();
            if (__instance.Pointer == g._messagePointer) g.MessageText(message ?? "");
        }
    }

    [PatchGroup(PatchGroup.Interaction)]
    [HarmonyPatch(typeof(PUI_InteractionPrompt), nameof(PUI_InteractionPrompt.SetMessage), typeof(TenCC.Utils.CharBuffer))]
    private static class MessageBuffer
    {
        private static void Postfix(PUI_InteractionPrompt __instance, TenCC.Utils.CharBuffer buffer)
        {
            var g = UIKit.Interaction;
            g.Find();
            if (__instance.Pointer == g._messagePointer) g.MessageText(buffer);
        }
    }

    [PatchGroup(PatchGroup.Interaction)]
    [HarmonyPatch(typeof(PUI_InteractionPrompt), nameof(PUI_InteractionPrompt.SetStyle))]
    private static class StyleSet
    {
        private static void Postfix(PUI_InteractionPrompt __instance, ePUIMessageStyle style)
        {
            var g = UIKit.Interaction;
            g.Find();
            var s = InteractionReading.Style((int)style);
            var p = __instance.Pointer;
            if (p == g._promptPointer && g.Style != s) { g.Style = s; g.Bump(); }
            else if (p == g._messagePointer && g.MessageStyle != s) { g.MessageStyle = s; g.Bump(); }
        }
    }

    /// <summary>The game asks an interaction for its extra line when it builds the prompt.</summary>
    [PatchGroup(PatchGroup.Interaction)]
    [HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.GetAdditionalInteractionInfo))]
    private static class Additional
    {
        private static void Postfix(string __result)
        {
            var g = UIKit.Interaction;
            __result ??= "";
            if (g.AdditionalText == __result) return;
            g.AdditionalText = __result;
            g.Bump();
        }
    }
}

/// <summary>
/// The scan nearest the local player while the game shows it (a bioscan or team scan's HUD): how many players are inside, how many it
/// counts, whether it wants all of them or one, whether the local player is inside and whether it sounds an alarm. Its text and progress
/// bar are the message line of <see cref="UIKit.Interaction"/>. Read from the scan's HUD after the game updates it each frame; the hook goes on when it is first asked for.
/// See <see cref="UIKit.Scan"/>.
/// </summary>
public sealed class GameScan
{
    private int _frame = -1;

    internal GameScan() { }

    /// <summary>A scan's HUD is up for the local player (updated this or the last frame).</summary>
    public bool Active => _frame >= Time.frameCount - 1;
    public int PlayersIn { get; private set; }
    /// <summary>The players the scan counts (its row of player marks).</summary>
    public int PlayersMax { get; private set; }
    public ScanRequirement Requirement { get; private set; }
    public bool LocalIn { get; private set; }
    public bool Alarm { get; private set; }
    public int Version { get; private set; }

    private static bool _installed;

    /// <summary>Puts the patch on the scan HUD's per-frame update the first time something asks for the scan (<see cref="UIKit.Scan"/>);
    /// a mod that never reads it pays nothing for the game's scan HUDs. It stays on once installed.</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        ForgeCore.Patches.Lazy(Plugin.Id + ".scan", harmony => harmony.Patch(AccessTools.Method(typeof(ChainedPuzzles.CP_Bioscan_Hud), nameof(ChainedPuzzles.CP_Bioscan_Hud.Update)),
            postfix: new HarmonyMethod(typeof(GameScan), nameof(Updated)))).Ensure();
    }

    private static void Updated(ChainedPuzzles.CP_Bioscan_Hud __instance)
    {
        if (!__instance.m_visible || !__instance.m_isClosestToPlayer) return;
        var s = UIKit.Scan;
        s._frame = Time.frameCount;
        var players = __instance.m_playersInScan;
        var max = __instance.m_playersMax;
        var requirement = InteractionReading.Requirement((int)__instance.m_playerRequirement);
        var local = __instance.m_localPlayerInScan;
        var alarm = __instance.m_hasAlarm;
        if (players == s.PlayersIn && max == s.PlayersMax && requirement == s.Requirement && local == s.LocalIn && alarm == s.Alarm) return;
        s.PlayersIn = players;
        s.PlayersMax = max;
        s.Requirement = requirement;
        s.LocalIn = local;
        s.Alarm = alarm;
        s.Version++;
    }
}
/// <summary>The game's subtitles, captured when the game shows one. See <see cref="UIKit.Subtitles"/>.</summary>
public sealed class GameSubtitles
{
    internal GameSubtitles() { }

    /// <summary>True while the current subtitle is within its showing time.</summary>
    public bool Visible => Text.Length > 0 && ForgeCore.Frame.GameTime < Until;
    public string Speaker { get; private set; } = "";
    public string Text { get; private set; } = "";
    /// <summary>When the current subtitle ends (<c>ForgeCore.Frame.GameTime</c>).</summary>
    public float Until { get; private set; }
    public int Version { get; private set; }
    public event Action? Changed;

    private void Show(PUI_Subtitles subtitles, string text, float seconds, int lines)
    {
        text ??= "";
        var speaker = "";
        try { speaker = subtitles.m_header != null ? subtitles.m_header.text ?? "" : ""; }
        catch (Exception error) { Plugin.Error("ui-subtitle-speaker-could-not-be-read", "The subtitle speaker could not be read: " + error); }
        Until = ForgeCore.Frame.GameTime + Math.Max(0.5f, seconds) * Math.Max(1, lines);
        if (Text == text && Speaker == speaker) return;
        Text = text;
        Speaker = speaker;
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-subtitles-listener-failed", "A subtitles listener failed: " + error); }
    }

    private static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var n = 1;
        foreach (var c in text) if (c == '\n') n++;
        return n;
    }

    [PatchGroup(PatchGroup.Subtitles)]
    [HarmonyPatch(typeof(PUI_Subtitles), nameof(PUI_Subtitles.ShowSingleLineSubtitle))]
    private static class Single
    {
        private static void Postfix(PUI_Subtitles __instance, string text, float duration) => UIKit.Subtitles.Show(__instance, text, duration, 1);
    }

    [PatchGroup(PatchGroup.Subtitles)]
    [HarmonyPatch(typeof(PUI_Subtitles), nameof(PUI_Subtitles.ShowMultiLineSubtitle))]
    private static class Multi
    {
        private static void Postfix(PUI_Subtitles __instance, string text, float duration) => UIKit.Subtitles.Show(__instance, text, duration, LineCount(text));
    }
}

/// <summary>The warden intel and sub-objective messages the game flashes on screen, captured when the game starts them (a message
/// the game queues behind another one is recorded when it is queued, not when it appears). See <see cref="UIKit.Intel"/>.</summary>
public sealed class GameWardenIntel
{
    private int _frame = -1;

    internal GameWardenIntel() { }

    public int Version { get; private set; }

    /// <summary>Raised on the main thread each time the game starts a message, even one with the text of the last.</summary>
    public event Action? Changed;

    /// <summary>The last message the game was asked to show.</summary>
    public string Text { get; private set; } = "";

    /// <summary>How long the game was asked to show it (seconds).</summary>
    public float Duration { get; private set; }

    /// <summary>The message is a sub-objective update (<c>ShowSubObjectiveMessage</c>), not warden intel (<c>ShowWardenIntel</c>).</summary>
    public bool IsSubObjective { get; private set; }

    private void Show(string? text, float duration, bool sub)
    {
        text ??= "";
        // The game's priority message calls the plain one: one message, one change.
        if (_frame == Time.frameCount && sub && IsSubObjective && Text == text) return;
        _frame = Time.frameCount;
        Text = text;
        Duration = duration;
        IsSubObjective = sub;
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-warden-intel-listener-failed", "A warden intel listener failed: " + error); }
    }

    [PatchGroup(PatchGroup.Intel)]
    [HarmonyPatch(typeof(PlayerGuiLayer), nameof(PlayerGuiLayer.ShowWardenIntel))]
    private static class Intel
    {
        private static void Postfix(string intel, float duration) => UIKit.Intel.Show(intel, duration, false);
    }

    /// <summary>The sub-objective messages, taken from ForgeCore's one patch of each message method.</summary>
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.SubObjectiveShown.After((message, _) => UIKit.Intel.Show(message.NewText, message.Duration, true));
        ForgeCore.GameHooks.PrioritySubObjectiveShown.After((message, _) => UIKit.Intel.Show(message.NewText, message.Duration, true));
    }
}

/// <summary>The booster implants the game's HUD shows for the local player, read after the game redraws its booster icons. Only
/// boosters the game has an implant for are listed. See <see cref="UIKit.Boosters"/>.</summary>
public sealed class GameBoosters
{
    private const int Max = 8;
    private readonly Sprite?[] _icons = new Sprite?[Max];
    private readonly string[] _names = new string[Max];

    internal GameBoosters()
    {
        for (var i = 0; i < Max; i++) _names[i] = "";
    }

    public int Version { get; private set; }

    /// <summary>Raised on the main thread after the listed boosters changed.</summary>
    public event Action? Changed;

    public int Count { get; private set; }

    /// <summary>The icon the game draws for the booster.</summary>
    public Sprite? Icon(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _icons[index];
    }

    /// <summary>The booster's public name in the game's language; empty when the game gives none.</summary>
    public string Name(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _names[index];
    }

    private void Read(PUI_BoosterIconActiveDisplay display)
    {
        var icons = display.m_boosterIcons;
        var instances = display.m_boosterImplantInstances;
        var count = 0;
        var changed = false;
        if (icons != null && instances != null)
            for (var i = 0; i < icons.Length && i < instances.Length && count < Max; i++)
            {
                var instance = instances[i];
                if (instance == null) continue;
                var sprite = icons[i]?.m_iconForegroundRenderer?.Sprite?.sprite;
                var name = NameOf(instance.BoosterImplantID, _names[count]);
                if (_icons[count] != sprite || _names[count] != name) changed = true;
                _icons[count] = sprite;
                _names[count] = name;
                count++;
            }
        if (count != Count) changed = true;
        for (var i = count; i < Count; i++) { _icons[i] = null; _names[i] = ""; }
        Count = count;
        if (!changed) return;
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception error) { Plugin.Error("ui-boosters-listener-failed", "A boosters listener failed: " + error); }
    }

    private static string NameOf(uint id, string known)
    {
        try { return BoosterImplantTemplateDataBlock.GetPublicName(id) ?? ""; }
        catch (Exception error)
        {
            Plugin.Error("ui-booster-name-could-not-be-read", "A booster name could not be read, so the name already known is kept: " + error);
            return known;
        }
    }

    [PatchGroup(PatchGroup.Boosters)]
    [HarmonyPatch(typeof(PUI_BoosterIconActiveDisplay), nameof(PUI_BoosterIconActiveDisplay.UpdateBoosterIconsActiveState))]
    private static class Updated
    {
        private static void Postfix(PUI_BoosterIconActiveDisplay __instance)
        {
            try { UIKit.Boosters.Read(__instance); }
            catch (Exception error) { Plugin.Error("ui-reading-boosters-failed", "Reading the boosters failed: " + error); }
        }
    }
}
/// <summary>The pick-up messages on the game's HUD (the lines that say what the player just picked up), read from the game's own list
/// ten times a second; only a change moves <see cref="Version"/>. See <see cref="UIKit.Pickups"/>.</summary>
public sealed class GamePickups
{
    private const int Max = 8;
    private const float Every = 0.1f;
    private readonly IntPtr[] _items = new IntPtr[Max];
    private readonly string[] _texts = new string[Max];
    private float _next;

    internal GamePickups()
    {
        for (var i = 0; i < Max; i++) _texts[i] = "";
    }

    public int Version { get; private set; }

    /// <summary>Raised on the main thread after the list changed.</summary>
    public event Action? Changed;

    public int Count { get; private set; }

    public string Text(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _texts[index];
    }

    /// <summary>After the game's frame, from the driver.</summary>
    internal void Tick()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _next) return;
        _next = now + Every;
        try
        {
            var list = GuiManager.PlayerLayer?.m_pickUpList;
            var count = list != null ? Math.Min(list.Count, Max) : 0;
            var changed = count != Count;
            for (var i = 0; i < count; i++)
            {
                var pick = list![i];
                var pointer = pick != null ? pick.Pointer : IntPtr.Zero;
                if (pointer == _items[i] && !changed) continue;
                if (pointer != _items[i]) changed = true;
                _items[i] = pointer;
            }
            if (!changed) return;
            for (var i = 0; i < count; i++) _texts[i] = list![i]?.pickUpText ?? "";
            for (var i = count; i < Count; i++) { _items[i] = IntPtr.Zero; _texts[i] = ""; }
            Count = count;
            Version++;
            try { Changed?.Invoke(); }
            catch (Exception error) { Plugin.Error("ui-pick-up-listener-failed", "A pick-up listener failed: " + error); }
        }
        catch (Exception error) { Plugin.Error("ui-reading-pick-up-messages-failed", "Reading the pick-up messages failed: " + error); }
    }
}
