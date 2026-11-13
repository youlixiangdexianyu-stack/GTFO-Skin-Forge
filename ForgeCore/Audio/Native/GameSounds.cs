using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeAudio;

/// <summary>One game sound event playing, as <see cref="AudioKit.PlayGameEvent(uint, Vector3)"/> answers it: ask <see cref="IsPlaying"/>, end it with
/// <see cref="Stop"/>. It is the sound engine's own playing id, so stopping it ends that one play and nothing else (not another sound of the same event,
/// not the game's own plays of it).</summary>
public sealed class GameSoundHandle
{
    private readonly uint[] _ids;
    private bool _stopped;

    internal GameSoundHandle(uint[] ids) => _ids = ids;

    /// <summary>True until <see cref="Stop"/> was called and while the sound engine still knows the play (a one-shot that finished is not playing).</summary>
    public bool IsPlaying
    {
        get
        {
            if (_stopped) return false;
            foreach (var id in _ids) if (AkSoundEngine.GetEventIDFromPlayingID(id) != 0) return true;
            return false;
        }
    }

    /// <summary>Ends the sound at once; calling it again, or after the sound finished, does nothing.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        foreach (var id in _ids) AkSoundEngine.StopPlayingID(id);
    }
}

public static partial class AudioKit
{
    private static PointSoundPool? _points;
    private static PointSoundPool Points => _points ??= new PointSoundPool();

    /// <summary>Clears the point emitters when the level ends or a checkpoint reloads (the game's events), as the emitters do not outlive their level.</summary>
    internal static void SubscribeGameSounds()
    {
        ForgeCore.GameEvents.LevelEnded += ClearGameSoundPoints;
        ForgeCore.GameEvents.CheckpointReloaded += ClearGameSoundPoints;
    }

    private static bool CanPlayGameEvent(string call, uint eventId)
    {
        if (eventId == 0) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a non-zero event ID."); return false; }
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, call + " must be called on the Unity main thread after the kit started."); return false; }
        return true;
    }

    private static GameSoundHandle? Played(uint eventId, params uint[] ids)
    {
        if (Array.IndexOf(ids, 0u) >= 0)
        {
            // One refused post of several: the others are stopped, as no handle will ever name them.
            foreach (var id in ids) if (id != 0) AkSoundEngine.StopPlayingID(id);
            AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " posted nothing (the game refused the event).");
            return null;
        }
        return new GameSoundHandle(ids);
    }

    /// <summary>Plays one of the game's own sound events at a world position and answers a handle to stop it or ask whether it still plays: the
    /// way <see cref="PostGameEvent(uint, Vector3)"/> plays, through a point emitter ForgeAudio keeps (an emitter within a metre of the point is shared, the
    /// oldest of 32 is recycled, all are given back when the level ends). The same rules apply: the game's own mix and occlusion, an event that
    /// starts no voice when posted so stays silent. Null (logged) when the event ID is 0, off the main thread, or the game posted nothing. Never throws.</summary>
    public static GameSoundHandle? PlayGameEvent(uint eventId, Vector3 position)
    {
        if (!CanPlayGameEvent("PlayGameEvent", eventId)) return null;
        try
        {
            PostingOwn = true;
            return Played(eventId, Points.Play(eventId, position));
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be played: " + error); return null; }
        finally { PostingOwn = false; }
    }

    // Emitters that carry a switch of their own (a character's voice): the oldest is given back when a ninth is needed, all when the level ends.
    private static readonly Queue<CellSoundPlayer> Switched = new();
    private const int SwitchedCapacity = 8;

    /// <summary>Plays one of the game's sound events at a world position as <see cref="PostGameEvent(uint, Vector3)"/> does, with the sound engine's switch
    /// <paramref name="switchGroup"/> set to <paramref name="switchValue"/> on its emitter first: what picks a character's voice for one line (the game's
    /// <c>AK.SWITCHES.CHARACTER</c> group), so a voice line plays as another character than the local player. The emitter is not shared with other plays. Main
    /// thread only; false (logged) when the event ID is 0, off the main thread or the game refuses; never throws.</summary>
    public static bool PostGameEvent(uint eventId, Vector3 position, uint switchGroup, uint switchValue)
    {
        if (!CanPlayGameEvent("PostGameEvent", eventId)) return false;
        try
        {
            PostingOwn = true;
            if (Switched.Count >= SwitchedCapacity) Switched.Dequeue().Recycle();
            var emitter = new CellSoundPlayer(position);
            Switched.Enqueue(emitter);
            emitter.SetSwitch(switchGroup, switchValue);
            return emitter.Post(eventId, position) != 0;
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be posted with a switch: " + error); return false; }
        finally { PostingOwn = false; }
    }

    /// <summary>As <see cref="PlayGameEvent(uint, Vector3)"/> at every one of <paramref name="positions"/>; one handle stops them all.</summary>
    public static GameSoundHandle? PlayGameEvent(uint eventId, IReadOnlyList<Vector3> positions)
    {
        if (!CanPlayGameEvent("PlayGameEvent", eventId)) return null;
        if (positions == null || positions.Count == 0) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event played at points needs at least one point."); return null; }
        var ids = new uint[positions.Count];
        try
        {
            PostingOwn = true;
            for (var i = 0; i < ids.Length; i++) ids[i] = Points.Play(eventId, positions[i]);
            return Played(eventId, ids);
        }
        catch (Exception error)
        {
            foreach (var id in ids) if (id != 0) AkSoundEngine.StopPlayingID(id);
            AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be played: " + error);
            return null;
        }
        finally { PostingOwn = false; }
    }

    /// <summary>As <see cref="PlayGameEvent(uint, Vector3)"/> with no place: the sound plays as a level-wide one.</summary>
    public static GameSoundHandle? PlayGameEvent(uint eventId)
    {
        if (!CanPlayGameEvent("PlayGameEvent", eventId)) return null;
        try
        {
            PostingOwn = true;
            return Played(eventId, Points.PlayGlobal(eventId));
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be played: " + error); return null; }
        finally { PostingOwn = false; }
    }

    /// <summary>Plays one of the game's sound events on <paramref name="target"/> so it follows the object: the target gets an <c>AkGameObj</c> if it has none and
    /// the event is posted on it; Wwise's own integration moves the sound with the object every frame and ends it when the object is destroyed. The handle
    /// stops it earlier. Null (logged) when the event ID is 0, off the main thread, or the engine posted nothing; the sound is never played at a fixed point instead. Never throws.</summary>
    public static GameSoundHandle? PlayGameEvent(uint eventId, GameObject target)
    {
        if (!CanPlayGameEvent("PlayGameEvent", eventId)) return null;
        if (target == null) { AudioLog.Report(AudioError.InvalidOptions, "A followed game sound event needs a target object."); return null; }
        try
        {
            PostingOwn = true;
            if (target.GetComponent<AkGameObj>() == null) target.AddComponent<AkGameObj>();
            return Played(eventId, AkSoundEngine.PostEvent(eventId, target));
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be played on " + target.name + ": " + error); return null; }
        finally { PostingOwn = false; }
    }

    /// <summary>As <see cref="PlayGameEvent(uint, Vector3)"/>, naming the event as <see cref="PostGameEvent(string, Vector3)"/> does.</summary>
    public static GameSoundHandle? PlayGameEvent(string eventName, Vector3 position)
        => OnMainThread && TryGameEventId(eventName, out var id) ? PlayGameEvent(id, position) : null;

    /// <summary>As <see cref="PlayGameEvent(uint, GameObject)"/>, naming the event as <see cref="PostGameEvent(string, Vector3)"/> does.</summary>
    public static GameSoundHandle? PlayGameEvent(string eventName, GameObject target)
        => OnMainThread && TryGameEventId(eventName, out var id) ? PlayGameEvent(id, target) : null;

    /// <summary>Posts the stop event <paramref name="stopEventId"/> on every point emitter <see cref="PlayGameEvent(uint, Vector3)"/> keeps, for a loop the game stops with
    /// the stop event its bank pairs with it (a stop event acts on the sound object it is posted to). Answers how many emitters it reached, or -1 (logged) when the
    /// event ID is 0, off the main thread or the game refused. A handle is the way to stop one play.</summary>
    public static int StopGameEventAtPoints(uint stopEventId)
    {
        if (!CanPlayGameEvent("StopGameEventAtPoints", stopEventId)) return -1;
        try
        {
            PostingOwn = true;
            return Points.Stop(stopEventId);
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "Game sound stop event " + stopEventId + " could not be posted at the points: " + error); return -1; }
        finally { PostingOwn = false; }
    }

    /// <summary>Gives back every point emitter now (the level ended); the library does it itself on the game's level and checkpoint events. Never throws.</summary>
    public static void ClearGameSoundPoints()
    {
        try
        {
            _points?.Clear();
            while (Switched.Count > 0) Switched.Dequeue().Recycle();
        }
        catch (Exception error) { AudioLog.Report(AudioError.VoiceFailed, "The game sound point emitters could not be given back: " + error); }
    }
}
