using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeAudio;

/// <summary>A sound that should keep playing on a world object for as long as the object lives: the kit starts it and starts it
/// again whenever it ended for any other reason (every sound is stopped on level exit or checkpoint reload, the device changed,
/// the budget refused it), and ends it when the object is gone or the condition you gave turns false. Make it with
/// <see cref="AudioLibrary.Sustain(string, Transform, PlayOptions?, Func{bool}?)"/>.</summary>
public sealed class AudioSustain
{
    internal readonly AudioLibrary Library;
    internal readonly string Cue;
    /// <summary>Null only for a sustain made on an owner that was already destroyed, which is never active.</summary>
    internal readonly Transform? Owner;
    internal readonly PlayOptions? Options;
    internal readonly Func<bool>? KeepWhile;
    internal AudioHandle? Voice;

    internal AudioSustain(AudioLibrary library, string cue, Transform? owner, PlayOptions? options, Func<bool>? keepWhile)
    {
        Library = library; Cue = cue; Owner = owner; Options = options; KeepWhile = keepWhile;
    }

    /// <summary>True until the owner is gone, the condition turned false, the library was disposed or <see cref="Stop"/> was called.</summary>
    public bool IsActive { get; internal set; } = true;

    /// <summary>The sound playing right now, or null between a restart and the next check.</summary>
    public AudioHandle? Current => Voice;

    /// <summary>Ends the sustain and fades its sound out over <paramref name="fadeSeconds"/>. Safe to call twice.</summary>
    public void Stop(float fadeSeconds = 0.15f) => Sustains.End(this, fadeSeconds);
}

public sealed partial class AudioLibrary
{
    /// <summary>Plays <paramref name="cue"/> following <paramref name="owner"/> and keeps it playing (see <see cref="AudioSustain"/>).
    /// The first play happens now; later restarts are checked four times a second while a level is running and no checkpoint is
    /// reloading. The sustain ends when the owner is destroyed or inactive, when <paramref name="keepWhile"/> returns false, or
    /// when the library is disposed. A failed play is logged by <see cref="Play(string, Transform, PlayOptions?)"/> as usual and
    /// tried again at the next check.</summary>
    /// <param name="cue">A cue name of this library.</param>
    /// <param name="owner">The object the sound follows.</param>
    /// <param name="options">Per-play settings used for every (re)start, or null.</param>
    /// <param name="keepWhile">Optional; checked at every check, on the main thread.</param>
    public AudioSustain Sustain(string cue, Transform owner, PlayOptions? options = null, Func<bool>? keepWhile = null)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        var sustain = new AudioSustain(this, cue, owner, options, keepWhile);
        if (ForgeCore.NativeObjects.Gone(owner) || IsDisposed) { sustain.IsActive = false; return sustain; }
        sustain.Voice = Play(cue, owner, options).Handle;
        Sustains.Add(sustain);
        return sustain;
    }
}

public static partial class AudioKit
{
    /// <summary>The game dimension whose navigation mesh holds <paramref name="position"/> (the local player's dimension first), or the
    /// local player's dimension for a point off every mesh, such as one in the air. This is the dimension <see cref="PlayOptions.Dimension"/>
    /// defaults to for a sound at a point; ask it for a position other than the sound's own (a pod's landing point while it is still
    /// in the sky). With <paramref name="preferLocalPlayer"/> the local player's own dimension is asked first and stands in for a point off
    /// every mesh, even while the camera is somewhere else (a sound that is nearly always heard where the player is); without a local
    /// player the listener's dimension is used. Main thread only.</summary>
    public static int DimensionAt(Vector3 position, bool preferLocalPlayer = false) => GameContext.DimensionAt(position, preferLocalPlayer);
}

internal static class Sustains
{
    private const float DefaultFade = 0.15f;
    private const double Interval = 0.25;
    private static readonly List<AudioSustain> All = new();
    private static double _next;

    internal static void Add(AudioSustain sustain) => All.Add(sustain);

    internal static void End(AudioSustain sustain, float fadeSeconds)
    {
        if (!sustain.IsActive) return;
        sustain.IsActive = false;
        sustain.Voice?.Stop(fadeSeconds);
        sustain.Voice = null;
        All.Remove(sustain);
    }

    /// <summary>Per driver update: every quarter second, ends sustains whose owner or condition is gone and restarts those whose sound ended.</summary>
    internal static void Tick()
    {
        if (All.Count == 0 || !GameContext.InLevel || GameContext.Reloading || GameContext.Now < _next) return;
        _next = GameContext.Now + Interval;
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var sustain = All[i];
            if (sustain.Library.IsDisposed || Gone(sustain))
            {
                End(sustain, DefaultFade);
                continue;
            }
            if (sustain.Voice == null || sustain.Voice.State == PlaybackState.Ended)
                sustain.Voice = sustain.Library.Play(sustain.Cue, sustain.Owner!, sustain.Options).Handle;
        }
    }

    private static bool Gone(AudioSustain sustain)
    {
        var owner = sustain.Owner;
        // A destroyed owner (for example at level exit) keeps its wrapper; reading its game object would throw.
        if (ForgeCore.NativeObjects.Gone(owner) || !owner!.gameObject.activeInHierarchy) return true;
        if (sustain.KeepWhile == null) return false;
        try { return !sustain.KeepWhile(); }
        catch (Exception error)
        {
            AudioLog.Report(AudioError.CallbackFailed, sustain.Library.Identity + ": the keep-while check of '" + sustain.Cue + "' threw and ended it: " + error);
            return true;
        }
    }

    internal static void Clear() => All.Clear();
}
