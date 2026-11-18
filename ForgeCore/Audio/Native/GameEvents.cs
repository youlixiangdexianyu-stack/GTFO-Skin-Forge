using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ForgeAudio;

public static partial class AudioKit
{
    private static readonly Dictionary<string, uint> GameEventIds = new(StringComparer.OrdinalIgnoreCase);

    private static bool _gameSilent;

    /// <summary>Silences or restores every sound of the game at once, whoever posts it (the game's master volume parameter, written the way the
    /// settings do; the player's slider is not changed, and restoring reads it). Costs nothing while it stays as it is. Main thread only.</summary>
    public static void SilenceGame(bool silent)
    {
        if (silent == _gameSilent) return;
        var master = CellSettingsManager.SettingsData?.Audio?.MasterVolume;
        if (master == null) return;
        _gameSilent = silent;
        CellSettingsApply.ApplyMasterVolume(silent ? 0f : master.Value);
    }

    /// <summary>Plays one of the game's own sound events (a Wwise event ID such as <c>AK.EVENTS.DOOR_ALARM</c>) at a world
    /// position, the way the game's <c>CellSound</c> does: the game's own mix, sliders and occlusion apply and the kit's categories,
    /// voice limits and cue files do not. Use it only for a vanilla sound you want unchanged; your own sounds are cues of an
    /// <see cref="AudioLibrary"/>. Main thread only. Returns false (and logs why) when the event ID is 0, the kit is not running, the
    /// call is off the main thread or the game refuses the event; never throws. An accepted event can still stay silent: the game's
    /// grenade, mine and enemy explosion events (FRAGGRENADEEXPLODE, STICKYMINEEXPLODE, EXPLODEREXPLODE, ENEMYEXPLODE) start no voice
    /// when posted this way, so a mod's explosion is a cue of its own.</summary>
    /// <example><c>AudioKit.PostGameEvent(AK.EVENTS.DOOR_ALARM, point);</c></example>
    public static bool PostGameEvent(uint eventId, Vector3 position)
    {
        if (eventId == 0) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a non-zero event ID."); return false; }
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "PostGameEvent must be called on the Unity main thread after the kit started."); return false; }
        try
        {
            PostingOwn = true;
            CellSound.Post(eventId, position);
            return true;
        }
        catch (Exception error)
        {
            AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be posted: " + error);
            return false;
        }
        finally { PostingOwn = false; }
    }

    /// <summary>Stops every voice of one of the game's sound events, wherever it was posted from (a looping alarm that
    /// <see cref="PostGameEvent(uint, Vector3)"/> started, or the game itself). Main thread only. Returns false (and logs why) when the game refuses.</summary>
    public static bool StopGameEvent(uint eventId)
    {
        if (eventId == 0) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a non-zero event ID."); return false; }
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "StopGameEvent must be called on the Unity main thread after the kit started."); return false; }
        try
        {
            AkSoundEngine.ExecuteActionOnEvent(eventId, AkActionOnEventType.AkActionOnEventType_Stop);
            return true;
        }
        catch (Exception error)
        {
            AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be stopped: " + error);
            return false;
        }
    }

    private static Action<uint>[] _gameEventObservers = Array.Empty<Action<uint>>();

    /// <summary>True while <see cref="PostGameEvent(uint, Vector3)"/> is on its way to the game's player, so the observers are not told of the kit's own posts.</summary>
    internal static bool PostingOwn;

    /// <summary>Tells <paramref name="observer"/> the ID of every sound event the game's own scripts post through its point emitter
    /// (<c>CellSoundPlayer.Post</c>, the at-a-position and the global overload), so a mod can tell whether the game has just played an
    /// event before it plays the same one itself. Posts made by <see cref="PostGameEvent(uint, Vector3)"/> are not reported. Any number of
    /// mods can observe at once; dispose the returned handle to stop watching yours (it never affects the others, and disposing twice is
    /// harmless). The observer runs on the main thread inside the game's post and must not allocate or throw. The hook goes on the game
    /// the first time an observer is added and costs one empty-array check per post while none is. Main thread only; returns null (and logs why) off it.</summary>
    /// <example><c>using var watch = AudioKit.ObserveGameEvents(id =&gt; seen = id);</c></example>
    public static IDisposable? ObserveGameEvents(Action<uint> observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "ObserveGameEvents must be called on the Unity main thread after the kit started."); return null; }
        _gameEventObservers = [.. _gameEventObservers, observer];
        Plugin.InstallHooks(HookGroup.Reserved);
        return new GameEventWatch(observer);
    }

    private sealed class GameEventWatch(Action<uint> observer) : IDisposable
    {
        private Action<uint>? _observer = observer;

        public void Dispose()
        {
            if (_observer == null) return;
            var list = new List<Action<uint>>(_gameEventObservers);
            list.Remove(_observer);
            _gameEventObservers = list.ToArray();
            _observer = null;
        }
    }

    internal static void RaiseGameEvent(uint eventId)
    {
        if (PostingOwn) return;
        foreach (var observer in _gameEventObservers)
        {
            // This runs inside the game's own post: an observer that throws must neither break that call nor keep the others from hearing it.
            try { observer(eventId); }
            catch (Exception error) { AudioLog.Report(AudioError.CallbackFailed, "A game sound event observer failed: " + error); }
        }
    }

    /// <summary>As<see cref="PostGameEvent(uint, Vector3)"/>, naming the event as the game's event list does (<c>AK.EVENTS</c>, case
    /// ignored), so a mod needs no reference to the game's Wwise assembly. The name is looked up once and the ID kept. Returns false
    /// (and logs why) when the game has no event of that name.</summary>
    /// <example><c>AudioKit.PostGameEvent("DOOR_ALARM", point);</c></example>
    public static bool PostGameEvent(string eventName, Vector3 position)
    {
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "PostGameEvent must be called on the Unity main thread after the kit started."); return false; }
        return TryGameEventId(eventName, out var id) && PostGameEvent(id, position);
    }

    /// <summary>The ID of one of the game's sound events by its <c>AK.EVENTS</c> name (case ignored), the ID that <see cref="PostGameEvent(string, Vector3)"/>
    /// posts and that the game's own scripts post it under, so a mod can tell whether the game has just played the same event. Looked up once
    /// and kept. Returns false (and logs why) when the name is empty or the game has no event of that name.</summary>
    public static bool TryGameEventId(string eventName, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(eventName)) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a name."); return false; }
        if (GameEventIds.TryGetValue(eventName, out id)) return true;
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            var events = typeof(AK.EVENTS);
            var value = events.GetProperty(eventName, flags)?.GetValue(null) ?? events.GetField(eventName, flags)?.GetValue(null);
            if (value == null) { AudioLog.Report(AudioError.CueNotFound, "The game has no sound event named '" + eventName + "' (AK.EVENTS)."); return false; }
            id = Convert.ToUInt32(value);
        }
        catch (Exception error)
        {
            AudioLog.Report(AudioError.CueNotFound, "The game sound event '" + eventName + "' could not be read: " + error);
            return false;
        }
        GameEventIds[eventName] = id;
        return true;
    }
}
