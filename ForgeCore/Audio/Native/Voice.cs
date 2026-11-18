using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ForgeAudio;

/// <summary>One sound slot riding on a transform: at most one cue plays on it, a new play replaces the old, and a loop is started once
/// and then only has its level changed. Make it with <see cref="AudioLibrary.Voice"/>; keep one per thing that sounds (a gun, a
/// projectile, a field) and let the slot answer "is it already playing".</summary>
public sealed class AudioVoice
{
    private readonly AudioLibrary _library;
    private AudioHandle? _handle;
    private AudioLibrary? _playingLibrary;
    private string? _cue;

    internal AudioVoice(AudioLibrary library, Transform anchor) { _library = library; Anchor = anchor; }

    /// <summary>The transform the sound rides on; move it to place the sound.</summary>
    public Transform Anchor { get; }

    /// <summary>True while the slot's sound plays (a virtualized sound still counts).</summary>
    public bool Playing => _handle is { State: PlaybackState.Playing or PlaybackState.Virtualized };

    /// <summary>True while <paramref name="cue"/> is the sound the slot plays.</summary>
    public bool IsPlaying(string cue) => Playing && _cue == cue && _playingLibrary == _library;

    /// <summary>True while <paramref name="cue"/>, of whichever library it belongs to, is the sound the slot plays.</summary>
    public bool IsPlaying(AudioCue cue) => Playing && _cue == cue.Name && _playingLibrary == cue.Library;

    /// <summary>Stops what plays on the slot and starts <paramref name="cue"/> at <paramref name="gain"/> (0 to 4, times the cue's own
    /// gain). False when the play was refused (ForgeAudio logged why); the slot is then silent.</summary>
    public bool Play(string cue, float gain = 1f) => PlayIn(_library, cue, gain);

    /// <summary>As <see cref="Play(string, float)"/> for a cue of any library (a slot made by one library can play another's cue).</summary>
    public bool Play(AudioCue cue, float gain = 1f) => PlayIn(cue.Library, cue.Name, gain);

    private bool PlayIn(AudioLibrary library, string cue, float gain)
    {
        Stop();
        _handle = library.Play(cue, Anchor, PlayOptions.OfGain(gain)).Handle;
        _cue = _handle != null ? cue : null;
        _playingLibrary = _handle != null ? library : null;
        return _handle != null;
    }

    /// <summary>Keeps a loop going at <paramref name="gain"/>: starts it unless it already plays, else only changes its level.</summary>
    public void Hold(string cue, float gain)
    {
        if (IsPlaying(cue)) _handle!.SetGain(gain);
        else Play(cue, gain);
    }

    /// <summary>As <see cref="Hold(string, float)"/> for a cue of any library.</summary>
    public void Hold(AudioCue cue, float gain)
    {
        if (IsPlaying(cue)) _handle!.SetGain(gain);
        else Play(cue, gain);
    }

    /// <summary>Changes the level of what plays; nothing happens when the slot is silent.</summary>
    public void SetGain(float gain)
    {
        if (Playing) _handle!.SetGain(gain);
    }

    /// <summary>Stops what plays on the slot now, or fading out over <paramref name="fadeSeconds"/>.</summary>
    public void Stop(float fadeSeconds = 0)
    {
        _handle?.Stop(fadeSeconds);
        _handle = null;
        _cue = null;
        _playingLibrary = null;
    }
}

public sealed partial class AudioLibrary
{
    private static readonly PlayOptions Silent = new() { Gain = 0f, Prewarm = true };
    private PrewarmProgress? _prewarm;

    /// <summary>A sound slot riding on <paramref name="anchor"/> (see <see cref="AudioVoice"/>).</summary>
    public AudioVoice Voice(Transform anchor) => new(this, anchor ?? throw new ArgumentNullException(nameof(anchor)));

    /// <summary>Throws unless the library loaded and has every cue in <paramref name="cues"/>, naming each missing one. Call it once the
    /// library <see cref="IsLoaded"/>, before the mod starts playing, so a cue missing from the audio folder or its cues.forge stops the
    /// mod at startup instead of staying silent in the middle of play.</summary>
    /// <exception cref="InvalidDataException">The library could not load, or cues are missing.</exception>
    /// <exception cref="InvalidOperationException">The library has not finished loading.</exception>
    public void Require(params string[] cues)
    {
        if (Error is { } error) throw new InvalidDataException(Identity + " sounds cannot load: " + error);
        if (!IsLoaded) throw new InvalidOperationException(Identity + " sounds have not finished loading.");
        if (CueRequirement.Missing(Has, cues) is { } missing)
            throw new InvalidDataException(Identity + " sounds missing from the audio folder or its cues.forge: " + missing);
    }

    /// <summary>Starts the next one-shot cue silently, so the first real play of a cue does not pay its first-start cost (a hitch of tens
    /// of milliseconds) in the middle of play. One cue per call, so call it once per frame; loops are not started. True once every cue was
    /// started, or when the library has nothing to start; false while cues remain, while the
    /// library is still loading, and while no level camera has an audio listener yet. The next level starts the run again.</summary>
    public bool Prewarm()
    {
        if (IsDisposed || Error != null) return true;
        if (!IsLoaded) return false;
        _prewarm ??= new PrewarmProgress(Identity, PrewarmProgress.OneShots(Cues.Values.Select(cue => cue.Definition)));
        if (_prewarm.Current is not { } name) return true;
        var result = Play(name, GameContext.Ear, Silent);
        // No listener yet is the wait this is for. A throttled cue was just played for real, so it is warm. A play that started no voice
        // otherwise (over budget, culled, failed) is a refusal: the cue is tried again.
        var step = result.Accepted || result.Status == PlayStatus.Throttled ? PrewarmStep.Started
            : result.Status == PlayStatus.Failed && result.Error.Code == AudioError.ListenerUnavailable ? PrewarmStep.Waiting : PrewarmStep.Refused;
        if (_prewarm.Report(step, result.Error.Detail ?? result.Status.ToString()) is { } refusal) AudioLog.Report(AudioError.NotReady, refusal);
        return _prewarm.Done;
    }

    internal void ResetPrewarm() => _prewarm = null;

    /// <summary>Plays <paramref name="cue"/> following <paramref name="follow"/>; a destroyed object is refused as TargetDestroyed
    /// (quietly: an owner that is gone is an ordinary end), so callers need no liveness check of their own.</summary>
    public PlayResult Play(string cue, GameObject follow, PlayOptions? options = null)
        => ForgeCore.NativeObjects.Gone(follow) ? Engine.TargetGone(this, cue) : Play(cue, follow.transform, options);

    /// <summary>Like <see cref="Sustain(string, Transform, PlayOptions?, Func{bool}?)"/> on a game object; an owner that is already
    /// destroyed gives a sustain that is not active, so callers need no liveness check of their own.</summary>
    public AudioSustain Sustain(string cue, GameObject owner, PlayOptions? options = null, Func<bool>? keepWhile = null)
        => ForgeCore.NativeObjects.Gone(owner) ? new AudioSustain(this, cue, null, options, keepWhile) { IsActive = false } : Sustain(cue, owner.transform, options, keepWhile);
}
