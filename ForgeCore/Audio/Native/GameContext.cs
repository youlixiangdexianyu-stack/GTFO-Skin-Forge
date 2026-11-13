using System;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeAudio;

internal static class GameContext
{
    internal static bool HasListener, Reloading, InLevel;
    internal static Vector3 Ear, Right;
    /// <summary>The listener's velocity in metres per second, from its movement since the previous refresh (for doppler).</summary>
    internal static Vector3 EarVelocity;
    private static Vector3 _previousEar;
    private static double _previousEarTime;
    private static bool _hadEar;
    internal static int Dimension;
    internal static double Now;
    internal static float Master, Sfx, Music, Dialogue;
    /// <summary>The kit's own master and per-category volumes (its config), updated when a value changes (see <see cref="Plugin.ReadVolumes"/>).</summary>
    internal static float KitMaster = 1;
    internal static readonly float[] KitVolume = Enumerable.Repeat(1f, AudioCategories.Count).ToArray();
    internal static bool MuteBackground, Focused;
    /// <summary>The game's volume sliders were read this frame (<see cref="Music"/> holds the player's setting).</summary>
    internal static bool SlidersRead;
    private static bool _hadLevel, _settingsError, _wasReloading, _levelEnded, _checkpointReloaded;
    private static double _nextSliders;
    private static bool _idleSettled;
    private static readonly Il2CppStructArray<RaycastHit> OcclusionHits = new(16);

    /// <summary>The game says the level ended or a checkpoint reloaded; the next <see cref="Refresh"/> acts on it.</summary>
    internal static void Subscribe()
    {
        ForgeCore.GameEvents.LevelEnded += OnLevelEnded;
        ForgeCore.GameEvents.CheckpointReloaded += OnCheckpointReloaded;
    }

    private static void OnLevelEnded() => _levelEnded = true;
    private static void OnCheckpointReloaded() => _checkpointReloaded = true;

    /// <summary>The game's volume sliders, four times a second (the game has no event for a slider moving).</summary>
    private static void ReadSliders()
    {
        try
        {
            var audio = CellSettingsManager.SettingsData?.Audio;
            if (audio?.MasterVolume == null || audio.SFXVolume == null || audio.MusicVolume == null || audio.DialogVolume == null || audio.MuteInBackground == null)
                throw new InvalidOperationException("Game audio settings are not loaded.");
            Master = audio.MasterVolume.Value; Sfx = audio.SFXVolume.Value; Music = audio.MusicVolume.Value; Dialogue = audio.DialogVolume.Value;
            MuteBackground = audio.MuteInBackground.Value;
            if (!float.IsFinite(Master) || !float.IsFinite(Sfx) || !float.IsFinite(Music) || !float.IsFinite(Dialogue))
                throw new InvalidOperationException("Game audio settings are non-finite.");
            _settingsError = false;
            SlidersRead = true;
        }
        catch (Exception e)
        {
            SlidersRead = false;
            Master = Sfx = Music = Dialogue = 0;
            if (!_settingsError) AudioLog.Report(AudioError.NotReady, "Game volume unreadable; kit muted: " + e);
            _settingsError = true;
        }
    }

    internal static void Refresh()
    {
        Now = AudioClock.Now;
        Focused = Application.isFocused;
        if (Now >= _nextSliders) { _nextSliders = Now + .25; ReadSliders(); }
        InLevel = GameStateManager.CurrentStateName == eGameStateName.InLevel;
        Reloading = InLevel && ForgeCore.Checkpoint.Recalling;
        var levelEnded = _levelEnded; _levelEnded = false;
        var checkpointChanged = _checkpointReloaded; _checkpointReloaded = false;
        if (_hadLevel && (!InLevel || levelEnded))
        {
            Engine.Clear(EndReason.LevelExit);
            foreach (var library in Assets.Loaded) library.ResetPrewarm();
            BodySounds.Clear();
            WeaponActions.Clear();
        }
        if (checkpointChanged || Reloading && !_wasReloading) Engine.Clear(EndReason.CheckpointReload);
        var levelChanged = _hadLevel != InLevel || levelEnded;
        _wasReloading = Reloading;
        _hadLevel = InLevel;
        // Outside a level, after one full pass, nothing here changes until the game state turns to InLevel (read above every frame):
        // no camera, player or listener lookup in the menus.
        if (!InLevel && !levelChanged && _idleSettled)
        {
            HasListener = false; _hadEar = false; EarVelocity = Vector3.zero;
            return;
        }
        _idleSettled = !InLevel;
        var player = ForgeCore.Players.Local;
        var camera = ForgeCore.Players.WorldCamera();
        ListenerOutput.Refresh(camera, Focused, levelChanged);
        // A level camera with an active listener is enough; the local player object only refines the dimension.
        HasListener = ListenerOutput.Present && ListenerOutput.Active(camera) && InLevel && !Reloading;
        if (!HasListener) { _hadEar = false; EarVelocity = Vector3.zero; }
        if (HasListener)
        {
            var ear = camera!.transform;
            Ear = ear.position; Right = ear.right;
            var elapsed = Now - _previousEarTime;
            EarVelocity = _hadEar && elapsed > .001 && elapsed < .5 ? (Ear - _previousEar) / (float)elapsed : Vector3.zero;
            _previousEar = Ear; _previousEarTime = Now; _hadEar = true;
            ReadDimension(player, camera);
        }
    }

    /// <summary>The listener's dimension: the local player's while the kit hears through that player's own camera; otherwise (dead and
    /// watching a teammate, a scripted camera) the dimension holding the camera, read four times a second.</summary>
    private static void ReadDimension(PlayerAgent? player, Camera camera)
    {
        var own = false;
        if (player != null && !player.WasCollected && player.Alive)
        {
            var fps = player.FPSCamera;
            var mine = fps == null || fps.WasCollected ? null : fps.m_camera;
            own = mine != null && mine.Pointer == camera.Pointer;
        }
        if (own) Dimension = (int)player!.DimensionIndex;
        else if (Now >= _nextDimension)
        {
            _nextDimension = Now + .25;
            Dimension = Followed(Ear) ?? DimensionAt(Ear);
        }
    }

    /// <summary>The dimension of the player whose eyes the camera is at (watching a teammate), or null when it is at nobody's.</summary>
    private static int? Followed(Vector3 ear)
    {
        _followedEar = ear;
        _followedDimension = -1;
        ForgeCore.Players.Each(FollowCheck);
        return _followedDimension < 0 ? null : _followedDimension;
    }

    private static Vector3 _followedEar;
    private static int _followedDimension;
    private static readonly Action<ForgeCore.PlayerInfo> FollowCheck = static info =>
    {
        if (_followedDimension < 0 && (info.Agent.EyePosition - _followedEar).sqrMagnitude < 4f) _followedDimension = (int)info.Agent.DimensionIndex;
    };

    private static double _nextDimension;
    /// <summary>The dimension whose navigation mesh holds <paramref name="position"/>, the listener's first. Points off every
    /// mesh (a shell in the air, a pod above the map) are taken to be in the listener's dimension.</summary>
    internal static int DimensionAt(Vector3 position, bool preferLocalPlayer = false)
    {
        if (!InLevel) return Dimension;
        var near = Dimension;
        if (preferLocalPlayer && ForgeCore.Players.Local is { } player && !player.WasCollected) near = (int)player.DimensionIndex;
        return ForgeCore.Level.CourseNodeAt(position, 1f, out _, out var d, preferredDimension: near) ? d : near;
    }
    internal static float Volume(AudioCategory category)
        => CategoryVolume.Map(category, Master, Sfx, Music, Dialogue, MuteBackground, Focused, KitMaster, KitVolume[(int)category]);

    internal static bool Occluded(Vector3 source, Transform? ownTarget)
    {
        var delta = source - Ear;
        var distance = delta.magnitude;
        if (distance <= .15f) return false;
        var mask = (LayerManager.MASK_WORLD | LayerManager.MASK_EXPLOSION_BLOCKERS) & ~(LayerManager.MASK_AGENT | LayerManager.MASK_ENEMY);
        var count = Physics.RaycastNonAlloc(Ear, delta / distance, OcclusionHits, distance - .1f, mask, QueryTriggerInteraction.Ignore);
        for (var i = 0; i < count; i++)
        {
            var collider = OcclusionHits[i].collider;
            if (collider == null || collider.WasCollected) continue;
            var hit = collider.transform;
            if (ownTarget != null && !ownTarget.WasCollected && (hit.IsChildOf(ownTarget) || ownTarget.IsChildOf(hit))) continue;
            return true;
        }
        // A full hit buffer cannot be told from a blocked path: it counts as blocked.
        return count == OcclusionHits.Length;
    }
}
