using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ForgeAudio;

public enum PlayStatus { Failed = 0, Played = 1, Virtualized = 2, DistanceCulled = 3, BudgetRejected = 4, Throttled = 5 }
public enum PlaybackState { Playing, Virtualized, Fading, Ended }
public enum EndReason { Completed, Stopped, Stolen, TargetDestroyed, LevelExit, CheckpointReload, LibraryDisposed, DeviceChanged, Failed, PluginUnloaded, Replaced, PushedOut }

/// <summary>Optional per-play settings. Reuse one instance for repeated plays; it is only read.</summary>
public sealed class PlayOptions
{
    /// <summary>Multiplies the cue gain, 0 to 4.</summary>
    public float Gain { get; init; } = 1;
    /// <summary>Exact pitch 0.1 to 3; null picks a random pitch in the cue's range.</summary>
    public float? Pitch { get; init; }
    /// <summary>Zero-based clip index for a cue with several clips; null picks one at random.</summary>
    public int? Variant { get; init; }
    /// <summary>Start offset in seconds. Loops wrap it; a one-shot must start before its end.</summary>
    public float StartSeconds { get; init; }
    /// <summary>The sound belongs to the local player: the cue's <c>self</c> mix applies instead of <c>other</c>.</summary>
    public bool IsSelf { get; init; }
    /// <summary>Game dimension of a world sound. Null uses the dimension whose navigation mesh holds the point,
    /// or the local player's dimension for points off the mesh (in the air).</summary>
    public int? Dimension { get; init; }
    /// <summary>Groups plays for the cue's <c>maxPerEmitter</c> limit (compared with Equals). By default a followed
    /// transform is one emitter, all 2D plays are one emitter, and each positioned play is its own emitter for <c>maxPerEmitter</c>;
    /// for a bus's <c>seats</c> the positioned plays without an emitter share one bucket (give them an emitter to separate them).</summary>
    public object? Emitter { get; init; }
    /// <summary>A silent start-up play of <see cref="AudioLibrary.Prewarm"/> (see <see cref="PlayRequest.Prewarm"/>).</summary>
    internal bool Prewarm { get; init; }

    private static readonly System.Collections.Generic.Dictionary<float, PlayOptions> Gains = new();

    /// <summary>The shared options of a play at <paramref name="gain"/> (0 to 4) and nothing else, made once per gain. Use it for the
    /// few fixed levels of a mod; a gain that changes continuously belongs on <see cref="AudioHandle.SetGain"/>. Main thread only.</summary>
    public static PlayOptions OfGain(float gain)
    {
        if (!Gains.TryGetValue(gain, out var options)) Gains.Add(gain, options = new PlayOptions { Gain = gain });
        return options;
    }
}

public readonly struct PlayResult
{
    public PlayStatus Status { get; }
    /// <summary>Present when <see cref="Accepted"/>.</summary>
    public AudioHandle? Handle { get; }
    private readonly AudioFailure _error;
    /// <summary>Why the play was refused (Failed or BudgetRejected). Already written to the log.</summary>
    public AudioFailure Error => Status == PlayStatus.Failed && _error.Code == AudioError.None
        ? new AudioFailure(AudioError.NotReady, "Uninitialized play result.") : _error;
    public bool Accepted => Status is PlayStatus.Played or PlayStatus.Virtualized;
    internal PlayResult(PlayStatus status, AudioHandle? handle = null, AudioFailure error = default) { Status = status; Handle = handle; _error = error; }
}

/// <summary>A playing sound. Never reused: a handle of an ended sound never controls a newer one.</summary>
public sealed class AudioHandle
{
    internal readonly EndGate Gate = new();
    internal Playback? Playback;
    public long Generation { get; }
    public PlaybackState State => Playback?.State ?? PlaybackState.Ended;
    /// <summary>Why the sound ended; null while it plays.</summary>
    public EndReason? Reason { get; private set; }
    /// <summary>Runs once on the main thread when the sound ends. Subscribe right after Play.</summary>
    public event Action<EndReason>? Ended;
    internal AudioHandle(long generation) { Generation = generation; }
    /// <summary>Stops now, or fades out over <paramref name="fadeSeconds"/>. False when the sound had already ended.</summary>
    public bool Stop(float fadeSeconds = 0) => Engine.Control(this, fadeSeconds, ControlKind.Stop);
    /// <summary>Per-play gain 0 to 4, applied on the next update. False when ended or out of range (logged).</summary>
    public bool SetGain(float gain) => Engine.Control(this, gain, ControlKind.Gain);
    internal void Finish(EndReason reason)
    {
        if (!Gate.End()) return;
        Playback = null; Reason = reason;
        Engine.NotifyEnded(this);
    }
    internal void DispatchEnded()
    {
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.SoundEnded(Generation);
        var callbacks = Ended; Ended = null;
        if (callbacks == null) return;
        foreach (Action<EndReason> callback in callbacks.GetInvocationList())
            try { callback(Reason!.Value); } catch (Exception e) { AudioLog.Report(AudioError.CallbackFailed, e.ToString()); }
    }
}

/// <summary>One mod's sounds: every <c>.ogg</c> in its audio folder, loaded once in the background.</summary>
public sealed partial class AudioLibrary : IDisposable
{
    /// <summary>The mod's ID: log lines name its cues <c>Identity/cue</c>.</summary>
    public string Identity { get; }
    /// <summary>The folder the library was loaded from; empty for a library loaded from a list of files (<see cref="AudioKit.Load(string, string, System.Collections.Generic.IReadOnlyDictionary{string, string})"/>).</summary>
    public string Folder { get; }
    /// <summary>True once every file has been decoded (or has failed with a logged reason).</summary>
    public bool IsLoaded { get; internal set; }
    public bool IsDisposed { get; private set; }
    /// <summary>Set when the whole library cannot load (missing folder, invalid cues.forge, duplicate identity).</summary>
    public AudioFailure? Error { get; internal set; }

    internal Catalog Catalog = new();
    internal readonly System.Collections.Generic.Dictionary<string, CueHandle> Cues = new(StringComparer.OrdinalIgnoreCase);
    internal readonly System.Collections.Generic.List<ClipLease> Assets = new();
    /// <summary>The library's and its cues' network names (<see cref="SyncKey"/>), for plays every player hears.</summary>
    internal readonly ulong Key;
    internal readonly System.Collections.Generic.Dictionary<ulong, CueHandle> CueKeys = new();
    /// <summary>The absolute path of each clip of a library loaded from a list of files (null for a folder library).</summary>
    internal System.Collections.Generic.Dictionary<string, string>? Files;
    /// <summary>The encoded bytes of each clip of a library loaded from memory, until loading finished (null otherwise).</summary>
    internal System.Collections.Generic.Dictionary<string, byte[]>? Bytes;
    internal AudioLibrary(string identity, string folder) { Identity = identity; Folder = folder; Key = SyncKey.Of(identity); }
    /// <summary>Where the clip <paramref name="file"/> (a name <see cref="Catalog"/> lists) is read from.</summary>
    internal string PathOf(string file) => Files != null ? Files[file] : Path.Combine(Folder, file);

    /// <summary>A reference to <paramref name="cue"/> that finds the cue once and then plays it without looking its name up again, and
    /// plays it for every player (<see cref="AudioCue.PlayEverywhere(Vector3, PlayOptions?, bool)"/>). Make it once (for example in your
    /// plugin's <c>Load</c>, before the library finished loading) and keep it.</summary>
    public AudioCue Cue(string cue) => new(this, cue ?? throw new ArgumentNullException(nameof(cue)));

    /// <summary>The loaded library of <paramref name="identity"/> (case ignored), or null: a replay plays a recorded sound through the library that made it.</summary>
    public static AudioLibrary? Find(string identity) => ForgeAudio.Assets.Find(identity);

    /// <summary>True when <paramref name="cue"/> loaded and can play.</summary>
    public bool Has(string cue) => cue != null && Cues.ContainsKey(cue);
    /// <summary>Plays <paramref name="cue"/> in 2D for the local listener (UI, the player's own sounds).</summary>
    public PlayResult Play(string cue, PlayOptions? options = null) => Engine.Play(this, cue, EmitterKind.TwoD, default, null, options);
    /// <summary>Plays <paramref name="cue"/> at a world position.</summary>
    public PlayResult Play(string cue, Vector3 position, PlayOptions? options = null) => Engine.Play(this, cue, EmitterKind.Positioned, position, null, options);
    /// <summary>Plays <paramref name="cue"/> following <paramref name="follow"/>; it ends when the transform is destroyed.</summary>
    public PlayResult Play(string cue, Transform follow, PlayOptions? options = null) => Engine.Play(this, cue, EmitterKind.Attached, default, follow, options);

    /// <summary>Stops every sound of this library now, or fading out over <paramref name="fadeSeconds"/>.</summary>
    public void StopAll(float fadeSeconds = 0) => Engine.StopLibrary(this, fadeSeconds);

    /// <summary>Ends this library's sounds and frees its clips. Libraries live for the whole session unless disposed.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        // After the kit shut down (plugin unload, game exit) its sounds and clips are already gone.
        if (!AudioKit.Ready) { IsDisposed = true; return; }
        // Callable from any thread: off the main thread the release is queued for the next driver update.
        if (Environment.CurrentManagedThreadId != AudioKit.MainThread) { Engine.QueueDispose(this); return; }
        IsDisposed = true;
        Engine.DisposeLibrary(this);
    }
}

/// <summary>Hears a claimed weapon's actions. Called on the main thread from inside the game's own event, once per action.</summary>
public interface IWeaponActionHandler
{
    /// <summary>The claimed weapon did <paramref name="action"/>. The game's own sounds of the weapon are not touched; play the
    /// mod's cue here. Only the highest-priority claim covering the action is asked.</summary>
    void Play(WeaponAction action);
}

/// <summary>One mod's claim on a weapon's actions (<see cref="AudioKit.ClaimWeaponActions"/>). Dispose it to let go; the kit also
/// ends every claim when the level ends.</summary>
public sealed class WeaponActionClaim : IDisposable
{
    public string ModId { get; }
    public Item Weapon { get; }
    public WeaponAction Kinds { get; }
    public int Priority { get; }
    /// <summary>False once disposed, ended by the level ending, or the kit shut down.</summary>
    public bool Active => Entry is { Live: true };
    internal readonly IWeaponActionHandler Handler;
    internal ClaimBook<WeaponActionClaim>.Entry? Entry;
    internal bool HandlerFailed;

    internal WeaponActionClaim(string modId, Item weapon, WeaponAction kinds, int priority, IWeaponActionHandler handler)
    {
        ModId = modId; Weapon = weapon; Kinds = kinds; Priority = priority; Handler = handler;
    }

    /// <summary>Lets go of the weapon. Safe to call more than once and after the level ended. Main thread only.</summary>
    public void Dispose()
    {
        if (Entry != null) WeaponActions.Book.Remove(Entry);
    }
}

public static partial class AudioKit
{
    public const string FolderName = "audio";
    internal static int MainThread;
    internal static bool Ready;
    internal static bool OnMainThread => Ready && Environment.CurrentManagedThreadId == MainThread;

    /// <summary>Loads the cues <c>cues.forge</c> lists in <paramref name="folder"/> (default: the <c>audio</c> folder next to the
    /// calling plugin's DLL): each cue on one of the folder's buses, each bus mapped to a global category. Call once on the main
    /// thread, usually in your plugin's <c>Load</c>. Returns at once; files decode in the background and plays before that are
    /// refused with NotReady. Problems are logged and reported per cue; this never throws.</summary>
    /// <param name="identity">Your mod's ID (your BepInEx plugin GUID), unique among loaded libraries; log lines name cues <c>identity/cue</c>.</param>
    /// <param name="folder">Absolute folder to load instead of the default.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static AudioLibrary Load(string identity, string? folder = null)
    {
        if (folder == null)
        {
            var location = System.Reflection.Assembly.GetCallingAssembly().Location;
            folder = string.IsNullOrEmpty(location) ? "" : Path.Combine(Path.GetDirectoryName(location)!, FolderName);
        }
        return Assets.Load(identity, folder, null, null);
    }

    /// <summary>Loads the cues of <paramref name="cues"/> (the text of a <c>cues.forge</c>) from the listed files, wherever they are: a
    /// mod whose sounds sit in several folders, or inside other packages, plays them from there without copying them. Each key of
    /// <paramref name="files"/> is a clip name as <c>cues</c> spells it (<c>name.ogg</c>, a cue named like a file plays that file) and each
    /// value the absolute path of an Ogg Vorbis file. Everything else is <see cref="Load(string, string?)"/>: call once on the main thread,
    /// returns at once, files decode in the background, problems are logged and reported per cue, this never throws.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static AudioLibrary Load(string identity, string cues, System.Collections.Generic.IReadOnlyDictionary<string, string> files)
        => Assets.Load(identity, "", files ?? throw new ArgumentNullException(nameof(files)), cues ?? throw new ArgumentNullException(nameof(cues)));

    /// <summary>Loads the cues of <paramref name="cues"/> from Ogg Vorbis bytes a mod already holds in memory (sounds packed inside a
    /// content file of its own); nothing is written to disk. Each key of <paramref name="clips"/> is a clip name as <c>cues</c> spells it
    /// (<c>name.ogg</c>, also <c>sub/name.ogg</c>). Unlike a folder, every clip passes the container check before this returns (one Ogg
    /// Vorbis stream, mono or stereo, at most <paramref name="maxSeconds"/>): one that fails sets <see cref="AudioLibrary.Error"/>, naming
    /// the clip, and the library plays nothing, so a content pack is taken or refused whole. Everything else is
    /// <see cref="Load(string, string?)"/>: main thread, returns at once, decoding in the background, never throws. Dispose the library to
    /// free its clips (a pack reloaded loads a new library under a new identity, then disposes the old one).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static AudioLibrary Load(string identity, string cues, System.Collections.Generic.IReadOnlyDictionary<string, byte[]> clips, double maxSeconds = 3600)
        => Assets.Load(identity, "", null, cues ?? throw new ArgumentNullException(nameof(cues)), clips ?? throw new ArgumentNullException(nameof(clips)), maxSeconds);

    internal static bool ValidIdentity(string? identity) => identity != null && identity.Length <= 100 && Regex.IsMatch(identity, "^[A-Za-z0-9][A-Za-z0-9_.-]*$");
}

/// <summary>Takes over a claimed body sound. Called on the main thread from inside the game's play, once per sound.</summary>
public interface IBodySoundHandler
{
    /// <summary>The game is about to play a <paramref name="kind"/> sound of the claimed body at <paramref name="position"/> (a
    /// footstep's own position; for <see cref="BodySound.Movement"/> the claimed root's position). Return true to drop the game's
    /// sound, false to let it play. Only the highest-priority claim covering the sound is asked.</summary>
    bool Play(BodySound kind, Vector3 position);
}

/// <summary>One mod's claim on a body's sounds (<see cref="AudioKit.ClaimBodySounds"/>). Dispose it to give the sounds back; the
/// kit also ends every claim when the level ends.</summary>
public sealed class BodySoundClaim : IDisposable
{
    public string ModId { get; }
    public GameObject Root { get; }
    public BodySound Kinds { get; }
    public int Priority { get; }
    /// <summary>False once disposed, ended by the level ending, or the kit shut down.</summary>
    public bool Active => Entry is { Live: true };
    internal readonly IBodySoundHandler Handler;
    internal readonly Transform RootTransform;
    internal ClaimBook<BodySoundClaim>.Entry? Entry;
    internal bool HandlerFailed;

    internal BodySoundClaim(string modId, GameObject root, BodySound kinds, int priority, IBodySoundHandler handler)
    {
        ModId = modId; Root = root; Kinds = kinds; Priority = priority; Handler = handler; RootTransform = root.transform;
    }

    /// <summary>Collects the body's transforms again (call after adding or moving parts under the root). Main thread only.</summary>
    public void Refresh()
    {
        if (!Active) return;
        if (ForgeCore.NativeObjects.Gone(Root)) { Dispose(); return; }
        BodySounds.Book.Reset(Entry!, BodySounds.Collect(Root));
    }

    /// <summary>Gives the sounds back. Safe to call more than once and after the level ended. Main thread only.</summary>
    public void Dispose()
    {
        if (Entry != null) BodySounds.Book.Remove(Entry);
    }
}

public static partial class AudioKit
{
    /// <summary>Takes over the game's own <paramref name="kinds"/> sounds of the player body under <paramref name="root"/> (usually a
    /// <c>PlayerAgent</c>'s GameObject) on this peer: each one asks <paramref name="handler"/> instead of playing. Every transform
    /// under the root is collected once now; call <see cref="BodySoundClaim.Refresh"/> if the hierarchy changes. Of several claims on
    /// one body the highest <paramref name="priority"/> answers (the newer at equal priority). Main thread only. Returns null (and
    /// logs) on an invalid argument or when the game's body sounds cannot be told apart by body.</summary>
    public static BodySoundClaim? ClaimBodySounds(string modId, GameObject root, BodySound kinds, IBodySoundHandler handler, int priority = 0)
        => BodySounds.Claim(modId, root, kinds, handler, priority);

    /// <summary>Hears the <paramref name="kinds"/> actions (reload start, reload end, a trigger pull on an empty magazine) of
    /// <paramref name="weapon"/> on this peer: each one asks <paramref name="handler"/> once, so the mod plays its own cue for it. The
    /// game's own weapon sounds come from the weapon's animation and stay. The claim is by the weapon instance, so claim again when
    /// the game builds a new one. Of several claims on one weapon the highest <paramref name="priority"/> answers (the newer at
    /// equal priority). Main thread only. Returns null (and logs) on an invalid argument.</summary>
    public static WeaponActionClaim? ClaimWeaponActions(string modId, Item weapon, WeaponAction kinds, IWeaponActionHandler handler, int priority = 0)
        => WeaponActions.Claim(modId, weapon, kinds, handler, priority);
}
