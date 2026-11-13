using System;
using System.Collections.Generic;
using ForgeCore;
using UnityEngine;

namespace ForgeAudio;

/// <summary>One cue of a library, found once and kept (<see cref="AudioLibrary.Cue"/>). Its plays do not look the cue's name up again,
/// and <see cref="PlayEverywhere(Vector3, PlayOptions?, bool)"/> plays it for every player in the session.</summary>
public sealed class AudioCue
{
    public AudioLibrary Library { get; }
    public string Name { get; }
    internal readonly ulong Key;
    private CueHandle? _cue;

    internal AudioCue(AudioLibrary library, string name) { Library = library; Name = name; Key = SyncKey.Of(name); }

    /// <summary>The cue loaded and can play.</summary>
    public bool IsReady => !Library.IsDisposed && (_cue != null || Library.Has(Name));

    /// <summary>In 2D for the local listener, as <see cref="AudioLibrary.Play(string, PlayOptions?)"/>.</summary>
    public PlayResult Play(PlayOptions? options = null) => Resolve(out var cue) is { } failed ? failed : Engine.Play(cue, EmitterKind.TwoD, default, null, options);
    /// <summary>At a world position, as <see cref="AudioLibrary.Play(string, Vector3, PlayOptions?)"/>.</summary>
    public PlayResult Play(Vector3 position, PlayOptions? options = null) => Resolve(out var cue) is { } failed ? failed : Engine.Play(cue, EmitterKind.Positioned, position, null, options);
    /// <summary>Following a transform, as <see cref="AudioLibrary.Play(string, Transform, PlayOptions?)"/>.</summary>
    public PlayResult Play(Transform follow, PlayOptions? options = null) => Resolve(out var cue) is { } failed ? failed : Engine.Play(cue, EmitterKind.Attached, default, follow, options);

    /// <summary>Plays the cue at <paramref name="position"/> here and for every other player in the session, in the same dimension, with
    /// the same variant, pitch and volume (picked here). The result is this player's own play. Other players play it when the message
    /// arrives; a player who joins later does not hear it. A loop is refused (a late joiner would miss it): start a loop on every
    /// player yourself. <paramref name="reliable"/> sends it on the game's reliable channel (never lost; for rare, important sounds);
    /// by default it goes on the unreliable one, like other frequent presentation.</summary>
    public PlayResult PlayEverywhere(Vector3 position, PlayOptions? options = null, bool reliable = false)
        => Resolve(out var cue) is { } failed ? failed : SyncPlays.Play(this, cue, SyncPlace.World, position, options, reliable);

    /// <summary>As <see cref="PlayEverywhere(Vector3, PlayOptions?, bool)"/>, in 2D for every player (each hears it in their own head).</summary>
    public PlayResult PlayEverywhere(PlayOptions? options = null, bool reliable = false)
        => Resolve(out var cue) is { } failed ? failed : SyncPlays.Play(this, cue, SyncPlace.TwoD, default, options, reliable);

    /// <summary>The loaded cue, found once; or why it cannot play.</summary>
    internal PlayResult? Resolve(out CueHandle cue)
    {
        cue = null!;
        if (Engine.Refuse(Library, Name) is { } refused) return refused;
        if (_cue == null)
        {
            if (!Library.Cues.TryGetValue(Name, out var found)) return Engine.Missing(Library, Name);
            _cue = found;
        }
        cue = _cue;
        return null;
    }
}

/// <summary>Plays every player hears, through the kit's own ForgeCore channel (one message per play; ForgeCore's handshake refuses
/// a player whose copy of the kit has another message layout). The sender picks variant, pitch and volume, plays its own copy and
/// sends the rest to every other player; a receiver plays it at once with the same values, as another player's sound. Nothing is
/// stored for later: a player who joins later does not hear plays from before.</summary>
internal static class SyncPlays
{
    private static NetEvent<SyncedPlay>? _lossy, _reliable;
    private static readonly Dictionary<ulong, AudioLibrary> Libraries = new();

    internal static void Open()
    {
        try
        {
            var channel = Net.Open(Plugin.Id);
            _lossy = channel.Event<SyncedPlay>("play", Receive, NetDelivery.Lossy);
            _reliable = channel.Event<SyncedPlay>("play-reliable", Receive);
        }
        catch (Exception error)
        {
            _lossy = _reliable = null;
            Plugin.Error("Plays for every player are off: the kit's network channel could not open: " + error);
        }
    }

    internal static void Register(AudioLibrary library)
    {
        if (Libraries.TryGetValue(library.Key, out var other))
            AudioLog.Report(AudioError.DuplicateLibrary, library.Identity + " and " + other.Identity + " have the same network name; " + library.Identity + "'s plays are not played for other players.");
        else Libraries[library.Key] = library;
    }

    internal static void Forget(AudioLibrary library)
    {
        if (Libraries.TryGetValue(library.Key, out var known) && ReferenceEquals(known, library)) Libraries.Remove(library.Key);
    }

    internal static PlayResult Play(AudioCue reference, CueHandle cue, SyncPlace place, Vector3 position, PlayOptions? options, bool reliable)
    {
        if (cue.Definition.Loop)
        {
            var failure = new AudioFailure(AudioError.InvalidOptions, cue.Library.Identity + "/" + cue.Definition.Name
                + ": a loop is not played for every player (a player who joins later would miss it); start it on each player.");
            AudioLog.Report(cue.Library.Identity + "|" + cue.Definition.Name + "|everywhere-loop", failure);
            return new PlayResult(PlayStatus.Failed, null, failure);
        }
        var request = Engine.Pick(cue, options ?? Default);
        var world = place == SyncPlace.World;
        if (world && !request.FixedDimension) { request.Dimension = GameContext.DimensionAt(position); request.FixedDimension = true; }
        var result = Engine.Play(cue, world ? EmitterKind.Positioned : EmitterKind.TwoD, position, null, request);
        // A play refused for this call itself is not sent; one only this player cannot hear (distance, budget, no listener yet) is.
        if (result.Status == PlayStatus.Throttled || result.Status == PlayStatus.Failed
            && result.Error.Code is AudioError.InvalidOptions or AudioError.WrongThread or AudioError.LibraryDisposed or AudioError.NotReady)
            return result;
        Send(SyncCodec.Encode(reference.Library.Key, reference.Key, place, position.x, position.y, position.z, request.Gain, request.CueGain,
            request.Pitch, request.StartSeconds, request.Dimension, request.Variant), reliable, cue);
        return result;
    }

    private static readonly PlayOptions Default = new();

    private static void Send(SyncedPlay message, bool reliable, CueHandle cue)
    {
        try { (reliable ? _reliable : _lossy)?.Broadcast(message); }
        catch (Exception error)
        {
            AudioLog.Report("everywhere-send", new AudioFailure(AudioError.VoiceFailed, cue.Library.Identity + "/" + cue.Definition.Name
                + ": the play could not be sent to the other players: " + error));
        }
    }

    private static void Receive(ulong sender, SyncedPlay message)
    {
        if (!AudioKit.Ready) return;
        if (!SyncCodec.TryDecode(message, out var request, out var reason))
        {
            Net.Drop("audio-play-unreadable");
            AudioLog.Report("everywhere-bad|" + sender, new AudioFailure(AudioError.InvalidOptions, "A play from player " + sender + " was dropped: " + reason + "."));
            return;
        }
        // A library or cue this player does not have is reported once per key, naming the sender: the copies of the mod differ.
        if (!Libraries.TryGetValue(request.Library, out var library) || library.IsDisposed)
        {
            AudioLog.Report("everywhere-no-library|" + request.Library, new AudioFailure(AudioError.CueNotFound, "A play from player " + sender
                + " names a sound library (network id " + request.Library.ToString("x") + ") this player does not have; the two copies of the mod differ."));
            return;
        }
        if (!library.IsLoaded) return; // still loading here: ordinary, not a fault
        if (!library.CueKeys.TryGetValue(request.Cue, out var cue))
        {
            AudioLog.Report("everywhere-no-cue|" + library.Identity + "|" + request.Cue, new AudioFailure(AudioError.CueNotFound, library.Identity + ": a play from player " + sender
                + " names a cue (network id " + request.Cue.ToString("x") + ") this player's library does not have or could not load; the two copies of the mod differ."));
            return;
        }
        if (cue.Definition.Loop || request.Variant >= cue.Variants)
        {
            Net.Drop("audio-play-mismatch");
            AudioLog.Report("everywhere-shape|" + library.Identity + "|" + cue.Definition.Name, new AudioFailure(AudioError.InvalidOptions, library.Identity + "/"
                + cue.Definition.Name + ": a play from player " + sender + " does not fit this player's cue (a loop, or variant " + request.Variant
                + " of " + cue.Variants + "); the two copies of the mod differ."));
            return;
        }
        var play = new PlayRequest
        {
            Gain = request.Gain,
            CueGain = request.CueGain,
            Pitch = request.Pitch,
            StartSeconds = request.StartSeconds,
            Variant = request.Variant,
            IsSelf = false,
            Dimension = request.Dimension,
            FixedDimension = request.Place == SyncPlace.World,
        };
        var world = request.Place == SyncPlace.World;
        Engine.Play(cue, world ? EmitterKind.Positioned : EmitterKind.TwoD, world ? new Vector3(request.X, request.Y, request.Z) : default, null, play);
    }
}
