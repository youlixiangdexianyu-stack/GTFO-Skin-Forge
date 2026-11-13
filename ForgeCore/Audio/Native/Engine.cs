using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeAudio;

internal enum ControlKind { Stop, Gain }
internal enum EmitterKind { TwoD, Positioned, Attached }

/// <summary>Everything one play uses, picked once (<see cref="Engine.Pick"/>): the caller's options and the variant, pitch and cue
/// volume chosen for it.</summary>
internal struct PlayRequest
{
    internal float Gain, CueGain, Pitch, StartSeconds;
    internal int Variant;
    internal bool IsSelf;
    /// <summary>The world sound's dimension when <see cref="FixedDimension"/>; otherwise it is found from the position.</summary>
    internal int Dimension;
    internal bool FixedDimension;
    internal object? Emitter;
    /// <summary>A silent start-up play (<see cref="AudioLibrary.Prewarm"/>): it takes no voice place, throttle, seat or category place from real sounds and evicts none of them.</summary>
    internal bool Prewarm;
}

/// <summary>Where a sound plays. Key groups plays for maxPerEmitter; null makes the play its own emitter for maxPerEmitter, while bus seats count every null-key play as one bucket.</summary>
internal sealed class Emitter
{
    /// <summary>The one emitter every 2D play shares; it names itself in the log (an anonymous object printed as System.Object).</summary>
    internal static readonly object TwoDKey = new TwoD();
    private sealed class TwoD { public override string ToString() => "2D (shared by every 2D sound)"; }
    internal readonly EmitterKind Kind;
    internal readonly object? Key;
    internal readonly Vector3 Position;
    internal readonly Transform? Target;
    /// <summary>The game dimension the sound is in. A followed sound whose caller gave no dimension is placed again when its
    /// object jumps (a teleport or a dimension warp), see <see cref="Engine"/>'s tick.</summary>
    internal int Dimension;
    internal readonly bool FixedDimension;
    internal Emitter(EmitterKind kind, object? key, Vector3 position, Transform? target, int dimension, bool fixedDimension)
    { Kind = kind; Key = key; Position = position; Target = target; Dimension = dimension; FixedDimension = fixedDimension; }

}

internal static class AudioClock
{
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

internal sealed class Playback
{
    internal readonly AudioHandle Handle;
    internal readonly CueHandle Cue;
    internal readonly Emitter Emitter;
    internal readonly AudioClip Clip;
    internal readonly bool Spatial;
    internal readonly int LogicalSlot;
    internal PlaybackState State;
    internal OneShotProgress Progress;
    internal Vector3 Position;
    internal float Gain, Pitch, Attenuation, Audibility, Occlusion = 1, OcclusionTarget = 1, Duck = 1, DuckTarget = 1;
    /// <summary>The cue's volume this play picked from the cue's range (<see cref="CueDefinition.GainMin"/> to <see cref="CueDefinition.Gain"/>).</summary>
    internal float CueGain = 1;
    /// <summary>Started by <see cref="PlayRequest.Prewarm"/>: not counted against any limit of real sounds.</summary>
    internal bool Prewarm;
    /// <summary>The followed transform is gone: the sound stays where it was and fades out.</summary>
    internal bool TargetLost;
    /// <summary>Where a followed sound was when its dimension was last placed.</summary>
    internal Vector3 PlacedAt;
    /// <summary>Pitch factor from relative motion (the cue's doppler setting), and the state that measures it.</summary>
    internal float Doppler = 1;
    internal Vector3 PrevPosition;
    internal bool HasPrev;
    internal double LastDoppler;
    internal EndReason FadeReason = EndReason.Stopped;
    internal double Phase, LastTick, Started, FadeStart, FadeEnd;
    internal int RealSlot = -1;
    internal Playback(AudioHandle handle, CueHandle cue, Emitter emitter, AudioClip clip, bool spatial, int logicalSlot,
        float gain, float pitch, float phase, double now)
    {
        Handle = handle; Cue = cue; Emitter = emitter; Clip = clip; Spatial = spatial; LogicalSlot = logicalSlot;
        Gain = gain; Pitch = pitch; Phase = phase; LastTick = Started = now;
    }
    internal AudioCategory Category => Cue.Bus.Category;
    internal VoiceRank Rank => new(Cue.Definition.Priority, Audibility, Started, VoicePolicy.Group(Category, Cue.Definition.Loop, Cue.Definition.Priority));
}

internal sealed class NativeVoice
{
    internal readonly GameObject Root;
    internal readonly AudioSource Source;
    internal Playback? Owner;
    /// <summary>What the engine last wrote to the source, so an unchanged value is not written again every frame.</summary>
    internal float WrittenVolume, WrittenPitch, WrittenPan;
    internal bool Alive => Root != null && !Root.WasCollected && Source != null && !Source.WasCollected;
    internal NativeVoice(string name)
    {
        Root = new GameObject(name);
        try
        {
            Object.DontDestroyOnLoad(Root);
            // Non-generic AddComponent: the generic GameObject.AddComponent<T> needs an Il2CppInterop generic method
            // instantiation that is unavailable for several component types in this game build.
            Source = Root.AddComponent(Il2CppType.Of<AudioSource>()).Cast<AudioSource>();
            Source.playOnAwake = false;
            Reset();
        }
        catch { Object.Destroy(Root); throw; }
    }
    internal void Reset()
    {
        Owner = null;
        if (!Alive) return;
        Source.Stop(); Source.clip = null; Source.volume = 0; Source.pitch = 1; Source.panStereo = 0;
        WrittenVolume = 0; WrittenPitch = 1; WrittenPan = 0;
        Source.spatialBlend = 0; Source.dopplerLevel = 0; Source.loop = false;
    }
    /// <summary>Starts and stops a silent clip through every AudioSource member the engine uses. Throws if Unity refuses one.</summary>
    internal string Probe(AudioClip clip)
    {
        Source.clip = clip; Source.loop = false; Source.pitch = 1; Source.volume = 0; Source.panStereo = 0;
        Source.time = 0;
        Source.Play();
        var playing = Source.isPlaying;
        var time = Source.time;
        Source.Stop();
        Reset();
        return "isPlaying=" + playing + ", time=" + time;
    }
}

internal static class Engine
{
    private static readonly Playback?[] Logical = new Playback?[VoicePolicy.LogicalCapacity];
    /// <summary>How many of <see cref="Logical"/> are in use; a frame with none skips every pass over them.</summary>
    private static int _active;
    private static readonly NativeVoice?[] Voices = new NativeVoice?[VoicePolicy.Capacity];
    private static readonly bool[] Eligible = new bool[VoicePolicy.Capacity];
    private static readonly VoiceRank?[] Ranks = new VoiceRank?[VoicePolicy.Capacity];
    private static readonly OcclusionSchedule Occlusion = new();
    private static readonly System.Random Random = new();
    private static readonly Queue<AudioHandle> EndNotifications = new();
    private static bool _clearing;
    private static long _generation;
    private static bool _selfTested;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<AudioLibrary> PendingDispose = new();
    internal static void QueueDispose(AudioLibrary library) => PendingDispose.Enqueue(library);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(AudioHandle Handle, float Value, ControlKind Kind)> PendingControl = new();
    internal static volatile bool DeviceChanged;

    /// <summary>A play by cue name: the name is looked up here, then the play continues as <see cref="Play(CueHandle, EmitterKind, Vector3, Transform?, PlayOptions?)"/>.</summary>
    internal static PlayResult Play(AudioLibrary library, string cueName, EmitterKind kind, Vector3 position, Transform? target, PlayOptions? options)
    {
        if (Refuse(library, cueName) is { } refused) return refused;
        if (cueName == null || !library.Cues.TryGetValue(cueName, out var cue)) return Missing(library, cueName);
        return Play(cue, kind, position, target, options);
    }

    /// <summary>Why <paramref name="library"/> cannot play now (the kit, the thread, the library itself), or null.</summary>
    internal static PlayResult? Refuse(AudioLibrary library, string? cueName)
    {
        if (!AudioKit.Ready) return new PlayResult(PlayStatus.Failed, null, new AudioFailure(AudioError.NotReady, "ForgeAudio is not loaded."));
        if (!AudioKit.OnMainThread)
        {
            var wrong = new AudioFailure(AudioError.WrongThread, library.Identity + ": Play must run on the Unity main thread.");
            AudioLog.Report(wrong);
            return new PlayResult(PlayStatus.Failed, null, wrong);
        }
        if (library.IsDisposed) return Fail(library, cueName, AudioError.LibraryDisposed, "library was disposed.");
        if (library.Error is { } broken) return Fail(library, cueName, broken.Code, broken.Detail);
        return null;
    }

    /// <summary>The result for a cue name the library has no playable cue of.</summary>
    internal static PlayResult Missing(AudioLibrary library, string? cueName)
    {
        if (cueName != null && library.Catalog.Failures.TryGetValue(cueName, out var unavailable)) return Fail(library, cueName, unavailable.Code, unavailable.Detail);
        if (!library.IsLoaded) return Fail(library, cueName, AudioError.NotReady, "still loading; play after library.IsLoaded.");
        return Fail(library, cueName, AudioError.CueNotFound, "does not exist in " + (library.Files != null ? "its file list" : library.Folder) + " (cues: "
            + string.Join(", ", System.Linq.Enumerable.Take(library.Cues.Keys, 12)) + (library.Cues.Count > 12 ? ", ..." : "") + ").");
    }

    /// <summary>Picks what the caller left open (variant, pitch, the cue's volume in its range) and plays.</summary>
    internal static PlayResult Play(CueHandle cue, EmitterKind kind, Vector3 position, Transform? target, PlayOptions? options)
    {
        options ??= DefaultOptions;
        return Play(cue, kind, position, target, Pick(cue, options));
    }

    /// <summary>The values one play uses: the caller's options with the variant, pitch and the cue volume picked. A play every player
    /// hears picks them once on the sender, so each copy sounds the same.</summary>
    internal static PlayRequest Pick(CueHandle cue, PlayOptions options)
    {
        var definition = cue.Definition;
        return new PlayRequest
        {
            Gain = options.Gain,
            StartSeconds = options.StartSeconds,
            IsSelf = options.IsSelf,
            Emitter = options.Emitter,
            Prewarm = options.Prewarm,
            Dimension = options.Dimension ?? -1,
            FixedDimension = options.Dimension != null,
            Variant = options.Variant ?? VariantPick.Next(cue.Variants, ref cue.LastVariant, Random),
            Pitch = options.Pitch ?? definition.PitchMin + (float)Random.NextDouble() * (definition.PitchMax - definition.PitchMin),
            CueGain = GainPick.At(definition.GainMin, definition.Gain, Random.NextDouble()),
        };
    }

    internal static PlayResult Play(CueHandle cue, EmitterKind kind, Vector3 position, Transform? target, in PlayRequest request)
    {
        var library = cue.Library;
        var definition = cue.Definition;
        var cueName = definition.Name;
        if (Refuse(library, cueName) is { } refused) return refused;
        if (_clearing || GameContext.Reloading) return Fail(library, cueName, AudioError.NotReady, "refused during a level or checkpoint transition.");
        var now = AudioClock.Now;
        if (now < cue.NextAllowed) return new PlayResult(PlayStatus.Throttled);
        if (kind == EmitterKind.Attached && ForgeCore.NativeObjects.Gone(target)) return TargetGone(library, cueName);
        var variant = request.Variant;
        var pitch = request.Pitch;
        if (kind == EmitterKind.Attached) position = target!.position;
        if (!Finite(request.Gain, 0, 4) || !Finite(pitch, .1f, 3) || !Finite(request.StartSeconds, 0, float.MaxValue) || !Finite(request.CueGain, 0, 4)
            || variant < 0 || variant >= cue.Variants || !ForgeCore.Vec.Finite(position) || request.FixedDimension && request.Dimension < 0)
            return Fail(library, cueName, AudioError.InvalidOptions, "needs gain 0-4, pitch 0.1-3, StartSeconds >= 0, Variant below the variant count, a finite position and Dimension >= 0.");
        // Every valid play is reported, heard here or not (the listener of a replay stands elsewhere); a loop once it has its handle, so its end can name it.
        if (!definition.Loop && !request.Prewarm && ForgeCore.Recording.Watched) Report(cue, kind, position, in request, 0);
        // The cue's own placement for this side decides how the caller's position or transform is used.
        switch (request.IsSelf ? definition.SelfPlace : definition.OtherPlace)
        {
            case Place.TwoD: kind = EmitterKind.TwoD; target = null; break;
            case Place.World when kind == EmitterKind.Attached: kind = EmitterKind.Positioned; target = null; break;
        }
        var spatial = kind != EmitterKind.TwoD;
        if (!ListenerOutput.Present || spatial && !GameContext.HasListener)
            return Fail(library, cueName, AudioError.ListenerUnavailable, spatial ? "world sound skipped: no level camera with an audio listener is active (level loading, ending or reloading)." : "sound skipped: no active Unity audio listener yet.", quiet: true);
        var stretch = CueMix.ProfileAt(cue.Profiles, spatial ? Vector3.Distance(GameContext.Ear, position) : 0);
        if (cue.Clips[stretch] is not { } stretchClips) return new PlayResult(PlayStatus.DistanceCulled);
        var clip = stretchClips[variant].Clip;
        if (clip == null || clip.WasCollected) return Fail(library, cueName, AudioError.DecodeFailed, "lost its clip (destroyed by another mod).");
        if (!definition.Loop && request.StartSeconds >= clip.length) return Fail(library, cueName, AudioError.InvalidOptions, "StartSeconds is past the end of the clip.");
        var key = request.Emitter ?? kind switch { EmitterKind.TwoD => Emitter.TwoDKey, EmitterKind.Attached => target!.Pointer, _ => null };
        var free = -1; var count = 0; var emitterCount = 0; Playback? clash = null;
        for (var i = 0; i < Logical.Length; i++)
        {
            var p = Logical[i];
            if (p == null) { if (free < 0) free = i; continue; }
            if (p.Prewarm || !ReferenceEquals(p.Cue, cue)) continue;
            count++; if (key != null && key.Equals(p.Emitter.Key)) { emitterCount++; clash = p; }
        }
        if (free < 0 || count >= definition.MaxVoices || emitterCount >= definition.MaxPerEmitter)
            return Reject(cue, free < 0 ? "all 256 sound states are in use." : count >= definition.MaxVoices
                ? "maxVoices " + definition.MaxVoices + " reached." : "maxPerEmitter " + definition.MaxPerEmitter + " reached for emitter '" + key
                + "' (held by a play of the same cue " + (now - clash!.Started).ToString("0.00", Inv) + " s old, " + clash.State
                + (clash.FadeEnd > 0 ? ", fading out" : "") + ").",
                // The cue's own per-emitter and per-cue limits doing their job are not a fault; only a full engine is.
                quiet: free >= 0);
        var dimension = kind == EmitterKind.TwoD ? -1 : request.FixedDimension ? request.Dimension : GameContext.DimensionAt(position);
        var emitter = new Emitter(kind, key, position, target, dimension, request.FixedDimension);
        var handle = new AudioHandle(++_generation);
        var playback = new Playback(handle, cue, emitter, clip, spatial, free, request.Gain, pitch, request.StartSeconds % clip.length, now) { CueGain = request.CueGain, Prewarm = request.Prewarm };
        UpdatePosition(playback); Measure(playback);
        playback.PlacedAt = playback.Position;
        if (playback.Attenuation <= 0 && !definition.Loop) return new PlayResult(PlayStatus.DistanceCulled);
        var evictions = 0;
        var bus = cue.Bus;
        if (bus.Seats > 0)
        {
            var holders = 0;
            for (var i = 0; i < Logical.Length; i++)
            {
                var p = Logical[i];
                if (p == null || p.Prewarm || p.FadeEnd > 0 || p.Cue.Bus != bus || !Equals(key, p.Emitter.Key)) continue;
                BusPlays[holders] = p; BusMembers[holders++] = new BusMember(p.Cue.Definition.Priority, p.Audibility, p.Started);
            }
            evictions = BusPolicy.Evict(BusMembers.AsSpan(0, holders), bus.Seats, definition.Priority, playback.Audibility, Victims);
            if (evictions < 0) return Reject(cue, "bus '" + bus.Name + "' holds stronger sounds in all " + bus.Seats + " seats for emitter '" + key + "'.", quiet: true);
            if (evictions > 0 && request.Prewarm) return Reject(cue, "a silent prewarm never replaces a sound on bus '" + bus.Name + "'.", quiet: true);
        }
        // The global category limit, over every mod's sounds; the bus's own replacements already free their places.
        var category = bus.Category;
        var members = 0;
        for (var i = 0; i < Logical.Length; i++)
        {
            var p = Logical[i];
            if (p == null || p.Prewarm || p.FadeEnd > 0 || p.Category != category || Replacing(p, evictions)) continue;
            CategoryPlays[members] = p; CategoryMembers[members++] = new BusMember(p.Cue.Definition.Priority, p.Audibility, p.Started);
        }
        var admission = CategoryPolicy.Admit(CategoryMembers.AsSpan(0, members), AudioCategories.Limits[(int)category], definition.Priority, playback.Audibility, out var pushed);
        if (admission == CategoryAdmission.Refuse)
        {
            return Reject(cue, "category '" + AudioCategories.Name(category) + "' has all " + AudioCategories.Limits[(int)category]
                + " places held by stronger or nearer sounds of every mod.", quiet: true);
        }
        if (admission == CategoryAdmission.PushOut && request.Prewarm)
            return Reject(cue, "a silent prewarm never pushes out a sound of category '" + AudioCategories.Name(category) + "'.", quiet: true);
        var slot = playback.Audibility > 0 || !definition.Loop ? FindVoice(playback, now) : -1;
        if (slot < 0 && !definition.Loop) return Reject(cue, "all 64 voices are busy with sounds that may not be stolen.");
        for (var i = 0; i < evictions; i++) FadeOut(BusPlays[Victims[i]]!, BusPolicy.ReplaceFade, EndReason.Replaced);
        if (admission == CategoryAdmission.PushOut)
        {
            var victim = CategoryPlays[pushed]!;
            FadeOut(victim, BusPolicy.ReplaceFade, EndReason.PushedOut);
        }
        Logical[free] = playback; _active++; handle.Playback = playback;
        if (!request.Prewarm) cue.NextAllowed = now + definition.MinInterval;
        if (definition.Loop && ForgeCore.Recording.Watched) Report(cue, kind, position, in request, handle.Generation);
        try
        {
            if (slot >= 0) StartVoice(playback, slot, now);
            else playback.State = PlaybackState.Virtualized;
            return new PlayResult(slot >= 0 ? PlayStatus.Played : PlayStatus.Virtualized, handle);
        }
        catch (Exception e) { End(playback, EndReason.Failed); return Fail(library, cueName, AudioError.VoiceFailed, "Unity refused to start the voice: " + e); }
    }
    private static readonly PlayOptions DefaultOptions = new();

    private static void Report(CueHandle cue, EmitterKind kind, Vector3 position, in PlayRequest request, long loop)
    {
        var place = kind switch { EmitterKind.TwoD => ForgeCore.ShownPlace.TwoD, EmitterKind.Attached => ForgeCore.ShownPlace.Attached, _ => ForgeCore.ShownPlace.World };
        ForgeCore.Recording.Sound(new ForgeCore.ShownSound(cue.Seen, place, position, request.Variant, request.Pitch, request.Gain, request.IsSelf, loop));
    }

    private static readonly Playback?[] BusPlays = new Playback?[VoicePolicy.LogicalCapacity];
    private static readonly BusMember[] BusMembers = new BusMember[VoicePolicy.LogicalCapacity];
    private static readonly int[] Victims = new int[VoicePolicy.LogicalCapacity];
    private static readonly Playback?[] CategoryPlays = new Playback?[VoicePolicy.LogicalCapacity];
    private static readonly BusMember[] CategoryMembers = new BusMember[VoicePolicy.LogicalCapacity];
    private static readonly DuckSource[] Duckers = new DuckSource[VoicePolicy.LogicalCapacity];
    private static bool Replacing(Playback p, int evictions)
    {
        for (var i = 0; i < evictions; i++) if (ReferenceEquals(BusPlays[Victims[i]], p)) return true;
        return false;
    }
    private static void FadeOut(Playback p, float seconds, EndReason reason)
    {
        if (p.FadeEnd > 0) return;
        p.FadeStart = AudioClock.Now; p.FadeEnd = p.FadeStart + seconds; p.State = PlaybackState.Fading; p.FadeReason = reason;
    }
    private static PlayResult Fail(AudioLibrary library, string? cue, AudioError code, string detail, PlayStatus status = PlayStatus.Failed, bool quiet = false)
    {
        var failure = new AudioFailure(code, library.Identity + "/" + cue + ": " + detail);
        var key = library.Identity + "|" + cue + "|" + code;
        if (!quiet) AudioLog.Report(key, failure);
        return new PlayResult(status, null, failure);
    }
    /// <summary>A sound asked to follow an object that is destroyed: an owner that is gone is an ordinary end, so it is logged quietly.</summary>
    internal static PlayResult TargetGone(AudioLibrary library, string? cue) => Fail(library, cue, AudioError.TargetDestroyed, "cannot follow a destroyed object.", quiet: true);
    private static PlayResult Reject(CueHandle cue, string detail, bool quiet = false)
    {
        return Fail(cue.Library, cue.Definition.Name, AudioError.VoiceLimit, "rejected: " + detail, PlayStatus.BudgetRejected, quiet);
    }
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private static bool Finite(float n, float min, float max) => float.IsFinite(n) && n >= min && n <= max;
    /// <summary>A followed object that moves this far between two ticks was teleported (or warped to another dimension).</summary>
    private const float JumpMetres = 10f;

    /// <summary>A followed sound jumped: its dimension is read again where it is now, so a sound on something that warped with the
    /// listener stays audible and one left behind is silent.</summary>
    private static void Replace(Playback p)
    {
        p.Emitter.Dimension = GameContext.DimensionAt(p.Position);
        p.PlacedAt = p.Position;
    }

    private static void UpdatePosition(Playback p)
    {
        if (p.Emitter.Kind == EmitterKind.Attached) { if (!p.TargetLost) p.Position = p.Emitter.Target!.position; }
        else p.Position = p.Emitter.Position;
    }
    private static void Measure(Playback p)
    {
        float attenuation = 1;
        if (p.Spatial)
        {
            if (!GameContext.HasListener || p.Emitter.Dimension != GameContext.Dimension) attenuation = 0;
            else
            {
                attenuation = p.Cue.Definition.Space.Attenuation(Vector3.Distance(GameContext.Ear, p.Position));
            }
        }
        // Distance is distinct from game mute: one-shots at zero slider volume are accepted but silent.
        p.Attenuation = attenuation;
        p.Audibility = attenuation * p.Gain * p.CueGain * p.Cue.Bus.Gain;
    }
    private static string Name(Playback p) => p.Cue.Library.Identity + "/" + p.Cue.Definition.Name;
    private static int FindVoice(Playback incoming, double now)
    {
        for (var i = 0; i < Voices.Length; i++)
            Ranks[i] = Voices[i]?.Owner?.Rank;
        return VoicePolicy.Select(Ranks, incoming.Rank, now);
    }
    private static void StartVoice(Playback p, int slot, double now)
    {
        var v = Voices[slot];
        if (v != null && !v.Alive)
        {
            if (v.Owner != null) End(v.Owner, EndReason.Failed);
            if (v.Root != null && !v.Root.WasCollected) Object.Destroy(v.Root);
            Voices[slot] = v = null;
        }
        v ??= Voices[slot] = new NativeVoice("ForgeAudio.Voice." + slot);
        var previous = v.Owner;
        if (previous != null)
        {
            if (previous.Cue.Definition.Loop) Virtualize(previous);
            else End(previous, EndReason.Stolen);
        }
        v.Reset(); v.Owner = p; p.RealSlot = slot; p.Started = now;
        p.State = p.FadeEnd > 0 ? PlaybackState.Fading : PlaybackState.Playing;
        Occlusion.Reset(slot);
        v.Source.clip = p.Clip; v.Source.loop = p.Cue.Definition.Loop; v.Source.pitch = p.Pitch; v.WrittenPitch = p.Pitch;
        v.Source.time = (float)(p.Phase % p.Clip.length);
        Apply(p, v, 0);
        v.Source.Play();
        p.Progress = new OneShotProgress(p.Phase, AudioSettings.dspTime, now);
    }
    private static void Virtualize(Playback p)
    {
        if (p.RealSlot >= 0) { Voices[p.RealSlot]!.Reset(); }
        p.RealSlot = -1; p.State = PlaybackState.Virtualized;
    }
    /// <summary>Once, the first time sounds can play: creates a real voice and starts and stops a silent clip, so a Unity or
    /// Il2Cpp interop incompatibility shows in the startup log instead of as a failure of every later play.</summary>
    private static void SelfTest()
    {
        _selfTested = true;
        NativeVoice? voice = null; AudioClip? clip = null;
        try
        {
            clip = AudioClip.Create("ForgeAudio.SelfTest", 441, 1, 44100, false);
            var silence = new Il2CppStructArray<float>(441);
            if (!AudioClip.SetData(clip, silence, 441, 0)) throw new InvalidOperationException("Unity refused the silent test clip.");
            voice = new NativeVoice("ForgeAudio.SelfTest");
            voice.Probe(clip);
        }
        catch (Exception e)
        {
            AudioLog.Report(AudioError.VoiceFailed, "Voice self-test failed: Unity could not create or start an AudioSource, so no sound can play: " + e);
        }
        finally
        {
            if (voice != null && voice.Root != null && !voice.Root.WasCollected) Object.Destroy(voice.Root);
            if (clip != null && !clip.WasCollected) Object.Destroy(clip);
        }
    }
    /// <summary>A sound whose followed transform was destroyed fades out over this long where it was.</summary>
    private const float TargetLostFade = .15f;
    private const float SpeedOfSound = 343f;
    private static void UpdateDoppler(Playback p, double now)
    {
        var dt = now - p.LastDoppler;
        if (dt < .02) return;
        if (p.HasPrev && GameContext.HasListener)
        {
            var offset = p.Position - GameContext.Ear; var distance = offset.magnitude;
            if (distance > .5f)
            {
                var toward = offset / distance;
                var source = Vector3.Dot((p.Position - p.PrevPosition) / (float)dt, toward);
                var listener = Vector3.Dot(GameContext.EarVelocity, toward);
                var shift = (SpeedOfSound + listener) / Math.Max(SpeedOfSound * .2f, SpeedOfSound + source);
                var target = Math.Clamp(1 + (shift - 1) * p.Cue.Definition.Space.Doppler, .5f, 2f);
                p.Doppler += (target - p.Doppler) * .3f;
            }
        }
        p.PrevPosition = p.Position; p.HasPrev = true; p.LastDoppler = now;
    }
    /// <summary>Every audible, not fading sound of a bus that ducks lowers the sounds of the categories it ducks, whichever mod plays them.</summary>
    private static void UpdateDucking()
    {
        var count = 0;
        for (var i = 0; i < Logical.Length; i++)
        {
            var d = Logical[i];
            if (d == null || d.FadeEnd > 0 || d.Audibility <= 0 || d.Cue.Bus.DuckMask == 0) continue;
            Duckers[count++] = new DuckSource(i, d.Cue.Bus.DuckMask, DuckPolicy.Factor(d.Cue.Bus.DuckDb));
        }
        var duckers = Duckers.AsSpan(0, count);
        for (var i = 0; i < Logical.Length; i++)
            if (Logical[i] is { } p) p.DuckTarget = count == 0 ? 1 : DuckPolicy.Target(duckers, i, p.Category);
    }
    internal static void Tick()
    {
        if (DeviceChanged) { DeviceChanged = false; RecoverDevice(); }
        while (PendingDispose.TryDequeue(out var library)) library.Dispose();
        while (PendingControl.TryDequeue(out var control)) Control(control.Handle, control.Value, control.Kind);
        if (!_selfTested && ListenerOutput.Present) SelfTest();
        var now = GameContext.Now;
        if (_active == 0)
        {
            DispatchEnded();
            return;
        }
        var dsp = AudioSettings.dspTime;
        for (var i = 0; i < Logical.Length; i++)
        {
            var p = Logical[i]; if (p == null) continue;
            if (p.Emitter.Kind == EmitterKind.Attached && !p.TargetLost && ForgeCore.NativeObjects.Gone(p.Emitter.Target))
            { p.TargetLost = true; FadeOut(p, TargetLostFade, EndReason.TargetDestroyed); }
            if (p.Clip == null || p.Clip.WasCollected) { AudioLog.Report("clip-destroyed", AudioError.DecodeFailed, "Clip destroyed: " + Name(p)); End(p, EndReason.Failed); continue; }
            if (p.FadeEnd > 0 && now >= p.FadeEnd) { End(p, p.FadeReason); continue; }
            if (p.RealSlot >= 0 && !Voices[p.RealSlot]!.Alive)
            { AudioLog.Report("voice-destroyed", AudioError.NotReady, "Owned Unity voice was destroyed: " + Name(p)); End(p, EndReason.Failed); continue; }
            if (p.RealSlot >= 0)
            {
                var source = Voices[p.RealSlot]!.Source;
                if (p.Cue.Definition.Loop)
                {
                    if (!source.isPlaying && now - p.Started > .1)
                    {
                        // Unity stops sources on output resets that can precede the device callback. Restart from phase.
                        Virtualize(p);
                    }
                }
                else
                {
                    var progress = p.Progress.Update(source.time, source.isPlaying, p.Clip.length, source.pitch, dsp, now);
                    if (progress != OneShotState.Playing)
                    {
                        if (progress == OneShotState.Stalled) AudioLog.Report("one-shot-stalled", AudioError.NotReady, "Unity one-shot position and DSP clock stalled; voice reclaimed: " + Name(p));
                        End(p, progress == OneShotState.Completed ? EndReason.Completed : EndReason.Failed); continue;
                    }
                }
            }
            p.Phase += Math.Max(0, now - p.LastTick) * p.Pitch; p.LastTick = now;
            if (p.Cue.Definition.Loop) p.Phase %= p.Clip.length;
            UpdatePosition(p);
            if (p.Emitter.Kind == EmitterKind.Attached && !p.Emitter.FixedDimension && !p.TargetLost && (p.Position - p.PlacedAt).sqrMagnitude > JumpMetres * JumpMetres)
                Replace(p);
            if (p.Spatial && p.Cue.Definition.Space.Doppler > 0) UpdateDoppler(p, now);
            if (!ForgeCore.Vec.Finite(p.Position)) { AudioLog.Report("emitter-non-finite", AudioError.InvalidOptions, "Emitter position became non-finite: " + Name(p)); End(p, EndReason.Failed); continue; }
            Measure(p);
            if (p.RealSlot >= 0 && p.Audibility <= 0 && p.Cue.Definition.Loop) Virtualize(p);
        }
        // At most 256 lightweight loop states. Reacquisition uses the same rank/hold policy as initial admission.
        for (var i = 0; i < Logical.Length; i++)
        {
            var p = Logical[i];
            if (p == null || p.RealSlot >= 0 || p.Audibility <= 0) continue;
            var slot = FindVoice(p, now); if (slot >= 0) StartVoice(p, slot, now);
        }
        for (var i = 0; i < Voices.Length; i++)
        {
            var p = Voices[i]?.Owner;
            Eligible[i] = p != null && p.Spatial && p.Cue.Definition.Environment && p.Audibility > 0 && GameContext.HasListener;
        }
        int next;
        while ((next = Occlusion.Next(Time.frameCount, now, Eligible)) >= 0)
        {
            var p = Voices[next]!.Owner!;
            p.OcclusionTarget = GameContext.Occluded(p.Position, p.Emitter.Target) ? .35f : 1;
        }
        UpdateDucking();
        for (var i = 0; i < Voices.Length; i++)
        {
            var voice = Voices[i]; if (voice?.Owner == null) continue;
            Apply(voice.Owner, voice, Math.Clamp(Time.unscaledDeltaTime, 0, .1f));
        }
        DispatchEnded();
    }
    // Unity resets every AudioSource when the output device or configuration changes. One-shots end with DeviceChanged;
    // loops keep their logical state and phase and restart on the next reacquisition pass, so consumers need not restore them.
    private static void RecoverDevice()
    {
        for (var i = 0; i < Logical.Length; i++)
        {
            var p = Logical[i]; if (p == null) continue;
            if (p.Cue.Definition.Loop) Virtualize(p);
            else End(p, EndReason.DeviceChanged);
        }
    }
    private static void Apply(Playback p, NativeVoice v, float dt)
    {
        var smoothing = dt == 0 ? 1 : 1 - MathF.Exp(-dt * 12);
        p.Occlusion += (p.OcclusionTarget - p.Occlusion) * smoothing;
        p.Duck = dt == 0 ? p.DuckTarget : DuckPolicy.Step(p.Duck, p.DuckTarget, dt);
        var fade = p.FadeEnd > 0 ? (float)Math.Clamp((p.FadeEnd - GameContext.Now) / (p.FadeEnd - p.FadeStart), 0, 1) : 1;
        var volume = Math.Clamp(p.Audibility * GameContext.Volume(p.Category) * p.Occlusion * p.Duck * fade, 0, 1);
        if (volume != v.WrittenVolume) { v.Source.volume = volume; v.WrittenVolume = volume; }
        var pitch = Math.Clamp(p.Pitch * p.Doppler, .1f, 3f);
        if (pitch != v.WrittenPitch) { v.Source.pitch = pitch; v.WrittenPitch = pitch; }
        var pan = 0f;
        if (p.Spatial && GameContext.HasListener)
        {
            var offset = p.Position - GameContext.Ear; var distance = offset.magnitude;
            var space = p.Cue.Definition.Space;
            pan = distance < .01f ? 0 : Math.Clamp(Vector3.Dot(offset, GameContext.Right) / distance, -1, 1) * .75f * Math.Min(1, distance / space.Near)
                * space.Blend * (1 - space.Spread / 180);
        }
        if (pan != v.WrittenPan) { v.Source.panStereo = pan; v.WrittenPan = pan; }
    }
    internal static bool Control(AudioHandle handle, float value, ControlKind kind)
    {
        // On the main thread the request runs now. Off it (a finalizer, a callback thread) it is queued for the next driver update.
        // After the kit shut down (plugin unload, game exit) its sounds are already gone, so the request is dropped without a warning.
        if (!AudioKit.Ready) return false;
        if (Environment.CurrentManagedThreadId != AudioKit.MainThread) { PendingControl.Enqueue((handle, value, kind)); return true; }
        if (!Finite(value, 0, kind == ControlKind.Gain ? 4 : 3600))
        {
            AudioLog.Report(AudioError.InvalidOptions, "AudioHandle." + kind + ": " + value + " is out of range (gain 0-4, fade 0-3600 s).");
            return false;
        }
        var p = handle.Playback;
        if (p == null) return false;
        if (kind == ControlKind.Stop)
        {
            if (value == 0) End(p, EndReason.Stopped);
            else if (p.FadeEnd <= 0) { p.FadeStart = AudioClock.Now; p.FadeEnd = p.FadeStart + value; p.State = PlaybackState.Fading; }
        }
        else p.Gain = value;
        return true;
    }
    private static void End(Playback p, EndReason reason)
    {
        if (p.Handle.Gate.Ended) return;
        if (p.RealSlot >= 0) Voices[p.RealSlot]!.Reset();
        p.RealSlot = -1; if (Logical[p.LogicalSlot] != null) _active--; Logical[p.LogicalSlot] = null;
        p.Handle.Finish(reason);
    }
    internal static void StopLibrary(AudioLibrary library, float fadeSeconds)
    {
        for (var i = 0; i < Logical.Length; i++)
            if (Logical[i] is { } p && p.Cue.Library == library && !Control(p.Handle, fadeSeconds, ControlKind.Stop)) return;
    }
    internal static void DisposeLibrary(AudioLibrary library)
    {
        for (var i = 0; i < Logical.Length; i++)
        { var p = Logical[i]; if (p?.Cue.Library == library) End(p, EndReason.LibraryDisposed); }
        Assets.ReleaseLibrary(library);
    }
    internal static void Clear(EndReason reason)
    {
        if (_clearing) return;
        _clearing = true;
        try
        {
            for (var i = 0; i < Logical.Length; i++) if (Logical[i] is { } p) End(p, reason);
            for (var i = 0; i < Voices.Length; i++)
            { if (Voices[i] is { } v && v.Root != null) Object.Destroy(v.Root); Voices[i] = null; Eligible[i] = false; Occlusion.Reset(i); }
            DispatchEnded();
        }
        finally { _clearing = false; }
    }
    internal static void NotifyEnded(AudioHandle handle) => EndNotifications.Enqueue(handle);
    private static void DispatchEnded()
    {
        // Reentrant callbacks may play/stop/dispose. Deliver only the initial queue snapshot this tick.
        var count = EndNotifications.Count;
        while (count-- > 0) EndNotifications.Dequeue().DispatchEnded();
    }
}
