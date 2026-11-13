using System;

namespace ForgeAudio;

/// <summary>Why a load, play or control call did not do what was asked. New values are only ever appended.</summary>
public enum AudioError
{
    None, InvalidSettings, InvalidSource, PathOutsideRoot, UnsupportedCodec, DecodeFailed, AssetLimit,
    InvalidIdentity, DuplicateLibrary, NotReady, LibraryDisposed, CueNotFound, WrongThread, InvalidOptions,
    ListenerUnavailable, TargetDestroyed, VoiceLimit, EnvironmentUnavailable, CallbackFailed, DeviceChanged, Cancelled,
    VoiceFailed
}

public readonly struct AudioFailure
{
    public AudioError Code { get; }
    public string Detail { get; }
    internal AudioFailure(AudioError code, string detail) { Code = code; Detail = detail; }
    public override string ToString() => Code + ": " + Detail;
}

internal readonly struct Result<T>
{
    private readonly T _value;
    public bool Success { get; }
    private readonly AudioFailure _error;
    public AudioFailure Error => !Success && _error.Code == AudioError.None
        ? new AudioFailure(AudioError.NotReady, "Uninitialized result.") : _error;
    public T Value => Success ? _value : throw new InvalidOperationException(Error.ToString());
    private Result(T value) { _value = value; Success = true; _error = default; }
    private Result(AudioFailure error) { _value = default!; Success = false; _error = error; }
    internal static Result<T> Ok(T value) => new(value);
    internal static Result<T> Fail(AudioError code, string detail) => new(new AudioFailure(code, detail));
    internal static Result<T> Fail(AudioFailure failure) => new(failure);
}

/// <summary>Every failure goes to the sink with a key; the mod log writes each key once, so this is the only layer that
/// holds a failure that repeats (one cue failing the same way on every play, a callback failing every frame) back.</summary>
internal static class AudioLog
{
    internal static Action<string, AudioFailure> Sink = (key, error) => Console.Error.WriteLine("ForgeAudio " + error);

    /// <summary>A failure kept once per <paramref name="key"/>; use a fixed key for anything that can repeat with changing text.</summary>
    internal static void Report(string key, AudioFailure error) => Sink(key, error);
    internal static void Report(string key, AudioError code, string detail) => Report(key, new AudioFailure(code, detail));
    /// <summary>A failure kept once per exact text.</summary>
    internal static void Report(AudioFailure error) => Sink(error.ToString(), error);
    internal static void Report(AudioError code, string detail) => Report(new AudioFailure(code, detail));
}
