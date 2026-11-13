using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeAudio;

/// <summary>A decoded clip shared by every cue (of any library) whose file has the same content hash.</summary>
internal sealed class ClipLease
{
    internal readonly string Key;
    internal readonly AudioClip Clip;
    internal readonly long CompressedBytes, DecodedBytes;
    internal int References;
    internal ClipLease(string key, AudioClip clip, long compressed, long decoded)
    {
        Key = key; Clip = clip; CompressedBytes = compressed; DecodedBytes = decoded;
        ForgeCore.Assets.Keep(clip);
    }
}

/// <summary>A loaded cue: its definition and bus, the clips of every variant for each distance stretch (a stretch where
/// nothing plays has none), and its minimum-interval gate.</summary>
internal sealed class CueHandle
{
    internal readonly AudioLibrary Library;
    internal readonly CueDefinition Definition;
    internal readonly BusDefinition Bus;
    internal readonly MixProfile[] Profiles;
    internal readonly ClipLease[]?[] Clips;
    internal readonly int Variants;
    internal double NextAllowed;
    internal int LastVariant = -1;
    internal CueHandle(AudioLibrary library, CueDefinition definition, BusDefinition bus, MixProfile[] profiles, ClipLease[]?[] clips)
    {
        Library = library; Definition = definition; Bus = bus; Profiles = profiles; Clips = clips;
        Variants = clips.First(stretch => stretch != null)!.Length;
        Seen = ForgeCore.RecordingNames.Of(library.Identity + "/" + definition.Name);
    }

    /// <summary>The number of <c>identity/cue</c> for reports to <see cref="ForgeCore.Recording"/>.</summary>
    internal readonly int Seen;
}

/// <summary>Loading runs one file at a time: a worker reads, checks and hashes it; a second worker decodes it unless an
/// identical file is already cached; the main thread creates the Unity clip. Workers touch managed data only and hand
/// results over through their Task, which the main thread polls. Layered cues are then mixed on a worker one (stretch,
/// variant) at a time and uploaded one per frame; decoded PCM, mix output and the upload copy all count against the decoded budget.</summary>
internal static class Assets
{
    private sealed class Prepared
    {
        internal byte[] Data = Array.Empty<byte>();
        internal OggInfo Info;
        internal string Hash = "";
        internal AudioFailure? Failure;
    }
    private sealed class Loading
    {
        internal readonly AudioLibrary Library;
        internal readonly string[] Files;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly Dictionary<string, ClipLease> Clips = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, AudioFailure> Failed = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, DecodedAudio> Pcm = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int> PcmUsers = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, (MixProfile[] Profiles, ClipLease[]?[] Clips)> Mixed = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> DirectFiles, MixedFiles;
        internal Queue<CueDefinition>? Todo;
        internal CueDefinition? MixCue;
        internal MixProfile[] MixProfiles = Array.Empty<MixProfile>();
        internal ClipLease[]?[] MixClips = Array.Empty<ClipLease[]?>();
        internal int MixProfile, MixVariant, MixVariants;
        internal Task<MixedAudio?>? Mixing;
        internal int Index;
        internal Task<Prepared>? Preparing;
        internal Prepared? Ready;
        internal Task<DecodedAudio>? Decoding;
        internal Loading(AudioLibrary library, string[] files)
        { Library = library; Files = files; DirectFiles = library.Catalog.DirectFiles; MixedFiles = library.Catalog.MixedFiles; }
        internal string File => Files[Index];
    }
    private static readonly Queue<Loading> Pending = new();
    private static readonly Dictionary<string, ClipLease> Cache = new(StringComparer.Ordinal);
    private static readonly List<AudioLibrary> Libraries = new();
    private static readonly Dictionary<string, AudioLibrary> ByIdentity = new(StringComparer.OrdinalIgnoreCase);
    private static Loading? _active;
    internal static long CompressedBytes, DecodedBytes;
    /// <summary>The libraries loaded (or loading), in load order.</summary>
    internal static IReadOnlyList<AudioLibrary> Loaded => Libraries;

    /// <summary>The loaded library of <paramref name="identity"/> (case ignored), or null.</summary>
    internal static AudioLibrary? Find(string identity) => ByIdentity.TryGetValue(identity, out var library) ? library : null;

    /// <summary>Loads a folder (<paramref name="files"/>, <paramref name="clips"/> and <paramref name="cues"/> null) or, when a file list
    /// or clip bytes are given, the cues of <paramref name="cues"/> from exactly those. Clip bytes are checked here, before this returns.</summary>
    internal static AudioLibrary Load(string identity, string folder, IReadOnlyDictionary<string, string>? files, string? cues,
        IReadOnlyDictionary<string, byte[]>? clips = null, double maxSeconds = 3600)
    {
        var library = new AudioLibrary(identity ?? "", folder);
        if (files != null) library.Files = new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase);
        if (clips != null) library.Bytes = new Dictionary<string, byte[]>(clips, StringComparer.OrdinalIgnoreCase);
        AudioFailure? error = null;
        if (!AudioKit.Ready) error = new AudioFailure(AudioError.NotReady, "ForgeAudio is not loaded; add [BepInDependency(\"NAinfini.ForgeCore\")] to your plugin.");
        else if (!AudioKit.OnMainThread) error = new AudioFailure(AudioError.WrongThread, "AudioKit.Load must run on the Unity main thread (your plugin's Load).");
        else if (ClipUpload.Failure is { } upload) error = upload;
        else if (!AudioKit.ValidIdentity(identity)) error = new AudioFailure(AudioError.InvalidIdentity, "identity '" + identity + "' must be letters, digits, '.', '_' or '-' (at most 100).");
        else if (ByIdentity.ContainsKey(library.Identity))
            error = new AudioFailure(AudioError.DuplicateLibrary, "a library named '" + identity + "' is already loaded.");
        else if (library.Files != null)
        {
            var relative = library.Files.FirstOrDefault(row => !Path.IsPathFullyQualified(row.Value));
            if (relative.Key != null) error = new AudioFailure(AudioError.PathOutsideRoot, "clip '" + relative.Key + "' is not an absolute path.");
        }
        else if (library.Bytes != null)
        {
            foreach (var (name, bytes) in library.Bytes)
                if (AssetValidation.Ogg(bytes ?? Array.Empty<byte>(), maxSeconds) is { Success: false } bad)
                {
                    error = new AudioFailure(bad.Error.Code, "clip '" + name + "': " + bad.Error.Detail);
                    break;
                }
        }
        else if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder))
            error = new AudioFailure(AudioError.PathOutsideRoot, "audio folder '" + folder + "' is not an absolute path; pass the folder to AudioKit.Load.");
        else if (!Directory.Exists(folder)) error = new AudioFailure(AudioError.InvalidSource, "audio folder not found: " + folder);
        if (error == null)
        {
            try
            {
                if (library.Files != null || library.Bytes != null) library.Catalog = CueCatalog.Build((IEnumerable<string>?)library.Files?.Keys ?? library.Bytes!.Keys, cues);
                else
                {
                    var root = Path.GetFullPath(folder);
                    var names = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file)).ToArray();
                    var settings = Path.Combine(root, CueCatalog.SettingsFile);
                    library.Catalog = CueCatalog.Build(names, File.Exists(settings) ? File.ReadAllText(settings) : null);
                }
                error = library.Catalog.Error;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { error = new AudioFailure(AudioError.InvalidSource, "audio folder unreadable: " + e); }
        }
        if (error is { } failure)
        {
            library.Error = new AudioFailure(failure.Code, identity + ": " + failure.Detail + " No sounds will play.");
            library.IsLoaded = true;
            AudioLog.Report(library.Error.Value);
            return library;
        }
        foreach (var unavailable in library.Catalog.Failures) AudioLog.Report(unavailable.Value.Code, identity + ": " + unavailable.Value.Detail);
        Libraries.Add(library); ByIdentity[library.Identity] = library;
        SyncPlays.Register(library);
        var loading = new Loading(library, library.Catalog.Files);
        Pending.Enqueue(loading);
        return library;
    }

    internal static void Tick()
    {
        if (_active == null && Pending.Count != 0) _active = Pending.Dequeue();
        var r = _active;
        if (r == null) return;
        if (r.Library.IsDisposed) { Abort(r); return; }
        if (r.Index == r.Files.Length) { if (BuildNext(r)) Complete(r); return; }
        var file = r.File;
        try
        {
            var needPcm = r.MixedFiles.Contains(file); var needClip = r.DirectFiles.Contains(file);
            if (r.Ready == null)
            {
                r.Preparing ??= Task.Run(() => Prepare(r.Library, file, r.Cancel.Token));
                if (!r.Preparing.IsCompleted) return;
                r.Ready = r.Preparing.GetAwaiter().GetResult();
                r.Preparing = null;
                if (r.Ready.Failure is { } failed) { Fail(r, failed); return; }
                // Identical content (in this or any library) decodes once, unless a mixed cue needs its PCM.
                if (needClip && !needPcm && Cache.TryGetValue(r.Ready.Hash, out var cached)) { cached.References++; Accept(r, cached); return; }
                var info = r.Ready.Info;
                // A file wanted both mixed and played directly stays resident twice (its PCM and its clip, unless the clip is cached already).
                var copies = (needPcm ? 1 : 0) + (needClip && !Cache.ContainsKey(r.Ready.Hash) ? 1 : 0);
                if (DecodedBytes + copies * info.DecodedBytes > AssetValidation.MaxDecodedBytes || CompressedBytes + r.Ready.Data.Length > AssetValidation.MaxCompressedCache)
                { Fail(r, new AudioFailure(AudioError.AssetLimit, "the audio cache is full (512 MiB decoded / 256 MiB compressed in total).")); return; }
            }
            if (r.Decoding == null)
            {
                var prepared = r.Ready;
                var token = r.Cancel.Token;
                r.Decoding = Task.Run(() => VorbisDecode.Decode(prepared.Data, prepared.Info, token));
                return;
            }
            if (!r.Decoding.IsCompleted) return;
            var audio = r.Decoding.GetAwaiter().GetResult();
            r.Decoding = null;
            if (needPcm) { r.Pcm[file] = audio; DecodedBytes += (long)audio.Samples.Length * sizeof(float); }
            if (!needClip) { Next(r); return; }
            if (Cache.TryGetValue(r.Ready.Hash, out var shared)) { shared.References++; Accept(r, shared); return; }
            var clip = MakeClip(r.Library.Identity + "/" + CueCatalog.CueName(file), audio);
            var lease = new ClipLease(r.Ready.Hash, clip, r.Ready.Data.Length, (long)audio.Frames * audio.Channels * sizeof(float)) { References = 1 };
            Cache.Add(lease.Key, lease); CompressedBytes += lease.CompressedBytes; DecodedBytes += lease.DecodedBytes;
            Accept(r, lease);
        }
        catch (OperationCanceledException) { Abort(r); }
        catch (Exception e) { Fail(r, new AudioFailure(AudioError.DecodeFailed, e.ToString())); }
    }

    /// <summary>A Unity clip holding <paramref name="audio"/>, refused (and destroyed) when Unity's frequency or length
    /// disagree with it: such a clip would play at the wrong speed.</summary>
    private static AudioClip MakeClip(string name, DecodedAudio audio)
    {
        var clip = AudioClip.Create(name, audio.Frames, audio.Channels, audio.SampleRate, false)
            ?? throw new InvalidDataException("Unity returned no clip.");
        try
        {
            ClipUpload.SetData(clip, audio);
            if (clip.samples != audio.Frames || clip.channels != audio.Channels) throw new InvalidDataException("Unity clip size disagrees with the decoded audio.");
            var expected = (double)audio.Frames / audio.SampleRate;
            if (clip.frequency != audio.SampleRate || Math.Abs(clip.length - expected) > expected * .01 + .001)
                throw new InvalidDataException("Unity clip plays at " + clip.frequency + " Hz for " + clip.length.ToString("F3", CultureInfo.InvariantCulture)
                    + " s, but the audio is " + audio.SampleRate + " Hz for " + expected.ToString("F3", CultureInfo.InvariantCulture) + " s, so it would play at the wrong speed.");
        }
        catch { Object.Destroy(clip); throw; }
        return clip;
    }

    /// <summary>Advances the mixing of layered or capped cues by one step and returns true when none is left. A step starts the
    /// background render of one (distance stretch, variant) or uploads the one that finished, so the main thread creates at most
    /// one Unity clip per frame and never mixes.</summary>
    private static bool BuildNext(Loading r)
    {
        var library = r.Library;
        if (r.Todo == null)
        {
            r.Todo = new Queue<CueDefinition>(library.Catalog.Cues.Values.Where(cue => !cue.IsDirect && !cue.Clips.Any(r.Failed.ContainsKey)).OrderBy(cue => cue.Name, StringComparer.OrdinalIgnoreCase));
            foreach (var file in r.Todo.SelectMany(cue => cue.Clips)) r.PcmUsers[file] = r.PcmUsers.GetValueOrDefault(file) + 1;
        }
        var cue = r.MixCue;
        try
        {
            if (cue == null)
            {
                if (r.Todo.Count == 0) return true;
                cue = r.MixCue = r.Todo.Dequeue();
                r.MixProfiles = CueMix.Profiles(cue);
                r.MixVariants = CueMix.Variants(cue);
                r.MixClips = new ClipLease[]?[r.MixProfiles.Length];
                r.MixProfile = r.MixVariant = 0;
            }
            while (r.MixProfile < r.MixProfiles.Length && r.MixProfiles[r.MixProfile].Silent) r.MixProfile++;
            if (r.MixProfile == r.MixProfiles.Length)
            {
                r.Mixed[cue.Name] = (r.MixProfiles, r.MixClips);
                EndMix(r);
                return false;
            }
            if (r.Mixing == null)
            {
                var profile = r.MixProfiles[r.MixProfile]; var variant = r.MixVariant; var pcm = r.Pcm;
                r.Mixing = Task.Run(() => CueMix.Render(cue, profile, variant, file => pcm[file]));
                return false;
            }
            if (!r.Mixing.IsCompleted) return false;
            var done = r.Mixing; r.Mixing = null;
            var mixed = done.GetAwaiter().GetResult()!;
            if (!CueMix.Fits(DecodedBytes, mixed, AssetValidation.MaxDecodedBytes))
            {
                Report(library, cue, new AudioFailure(AudioError.AssetLimit, "the mix would exceed the audio cache (512 MiB decoded in total)."));
                DropMixed(r);
                EndMix(r);
                return false;
            }
            var audio = new DecodedAudio(mixed.Samples, mixed.Channels, mixed.SampleRate);
            var name = cue.Name + "#" + r.MixProfile + "." + r.MixVariant;
            var clip = MakeClip(library.Identity + "/" + name, audio);
            var lease = new ClipLease("mix:" + library.Identity + "/" + name, clip, 0, (long)audio.Samples.Length * sizeof(float)) { References = 1 };
            DecodedBytes += lease.DecodedBytes; library.Assets.Add(lease);
            (r.MixClips[r.MixProfile] ??= new ClipLease[r.MixVariants])[r.MixVariant] = lease;
            if (++r.MixVariant == r.MixVariants) { r.MixVariant = 0; r.MixProfile++; }
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            Report(library, cue!, new AudioFailure(AudioError.DecodeFailed, library.Identity + ": cue '" + cue!.Name + "' cannot be mixed: " + e));
            DropMixed(r);
            EndMix(r);
        }
        return false;
    }
    /// <summary>The cue failed part way: the clips already made for its earlier variants and stretches are given back (the cue is not played, nothing else holds them).</summary>
    private static void DropMixed(Loading r)
    {
        foreach (var row in r.MixClips)
        {
            if (row == null) continue;
            foreach (var lease in row)
            {
                if (lease == null) continue;
                r.Library.Assets.Remove(lease);
                Release(lease);
            }
        }
    }
    private static void Report(AudioLibrary library, CueDefinition cue, AudioFailure failure)
    {
        if (failure.Code == AudioError.AssetLimit) failure = new AudioFailure(failure.Code, library.Identity + ": cue '" + cue.Name + "' cannot be mixed: " + failure.Detail);
        library.Catalog.Failures[cue.Name] = failure;
        AudioLog.Report(failure);
    }
    /// <summary>The cue is finished (mixed or failed): the decoded PCM no cue still waiting needs is freed.</summary>
    private static void EndMix(Loading r)
    {
        foreach (var file in r.MixCue!.Clips)
            if (--r.PcmUsers[file] == 0 && r.Pcm.Remove(file, out var audio)) DecodedBytes -= (long)audio.Samples.Length * sizeof(float);
        r.MixCue = null; r.MixClips = Array.Empty<ClipLease[]?>();
    }
    /// <summary>Frees every PCM a loading still holds. A render still running keeps reading the dictionary it captured, so it is replaced.</summary>
    private static void FreePcm(Loading r)
    {
        foreach (var audio in r.Pcm.Values) DecodedBytes -= (long)audio.Samples.Length * sizeof(float);
        r.Pcm = new(StringComparer.OrdinalIgnoreCase);
    }
    private static Prepared Prepare(AudioLibrary library, string file, CancellationToken cancellation)
    {
        var result = new Prepared();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes;
            if (library.Bytes != null) bytes = library.Bytes[file];
            else
            {
                var path = library.PathOf(file);
                if (new FileInfo(path).Length > AssetValidation.MaxFileBytes) { result.Failure = new AudioFailure(AudioError.AssetLimit, "file exceeds 64 MiB."); return result; }
                bytes = File.ReadAllBytes(path);
            }
            var info = AssetValidation.Ogg(bytes);
            if (!info.Success) { result.Failure = info.Error; return result; }
            result.Data = bytes; result.Info = info.Value;
            result.Hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { result.Failure = new AudioFailure(AudioError.InvalidSource, "unreadable: " + e); }
        return result;
    }

    private static void Accept(Loading r, ClipLease lease)
    {
        r.Clips[r.File] = lease;
        r.Library.Assets.Add(lease);
        Next(r);
    }
    private static void Fail(Loading r, AudioFailure failure)
    {
        r.Failed[r.File] = new AudioFailure(failure.Code, r.File + ": " + failure.Detail);
        Next(r);
    }
    private static void Next(Loading r) { r.Ready = null; r.Preparing = null; r.Decoding = null; r.Index++; }

    private static void Complete(Loading r)
    {
        var library = r.Library;
        foreach (var definition in library.Catalog.Cues.Values.ToArray())
        {
            if (library.Catalog.Failures.ContainsKey(definition.Name)) continue;
            var broken = definition.Clips.FirstOrDefault(r.Failed.ContainsKey);
            if (broken != null)
            {
                var failure = r.Failed[broken];
                library.Catalog.Failures[definition.Name] = failure;
                AudioLog.Report(failure.Code, library.Identity + ": cue '" + definition.Name + "' unavailable, " + failure.Detail);
                continue;
            }
            var bus = library.Catalog.Buses[definition.Bus];
            if (definition.IsDirect)
            {
                var clips = definition.Layers[0].Clips.Select(file => r.Clips[file]).ToArray();
                library.Cues[definition.Name] = new CueHandle(library, definition, bus, new[] { new MixProfile(0, float.PositiveInfinity, 1) }, new[] { clips });
            }
            else if (r.Mixed.TryGetValue(definition.Name, out var mixed)) library.Cues[definition.Name] = new CueHandle(library, definition, bus, mixed.Profiles, mixed.Clips);
        }
        foreach (var cue in library.Cues.Values)
        {
            var key = SyncKey.Of(cue.Definition.Name);
            if (library.CueKeys.TryGetValue(key, out var clash))
                AudioLog.Report(AudioError.InvalidSettings, library.Identity + ": cues '" + clash.Definition.Name + "' and '" + cue.Definition.Name
                    + "' have the same network name; '" + cue.Definition.Name + "' is not played for other players.");
            else library.CueKeys[key] = cue;
        }
        FreePcm(r);
        library.Bytes = null; // every clip is decoded or failed; the encoded bytes are not read again
        library.IsLoaded = true;
        _active = null;
        r.Cancel.Dispose();
    }
    // A worker still running finishes on managed data only; its result is dropped.
    private static void Abort(Loading r)
    {
        r.Cancel.Cancel();
        FreePcm(r);
        if (_active == r) _active = null;
        r.Library.IsLoaded = true;
    }

    internal static void ReleaseLibrary(AudioLibrary library)
    {
        if (_active?.Library == library) Abort(_active);
        foreach (var lease in library.Assets) Release(lease);
        library.Assets.Clear(); library.Cues.Clear(); library.CueKeys.Clear();
        Libraries.Remove(library);
        // A library refused for a taken name never owned the entry; releasing it must not drop the owner's.
        if (ByIdentity.TryGetValue(library.Identity, out var owner) && ReferenceEquals(owner, library)) ByIdentity.Remove(library.Identity);
        SyncPlays.Forget(library);
    }
    private static void Release(ClipLease lease)
    {
        if (--lease.References != 0) return;
        Cache.Remove(lease.Key); CompressedBytes -= lease.CompressedBytes; DecodedBytes -= lease.DecodedBytes;
        if (lease.Clip != null && !lease.Clip.WasCollected) Object.Destroy(lease.Clip);
    }
    internal static void Clear()
    {
        while (Libraries.Count != 0) Libraries[Libraries.Count - 1].Dispose();
        while (Pending.Count != 0) Abort(Pending.Dequeue());
        if (_active != null) Abort(_active);
    }
}
