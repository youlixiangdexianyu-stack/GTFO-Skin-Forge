using UnityEngine;

namespace ForgeVisual;

/// <summary>How hard and how far something shakes the camera. Make one per kind of event and reuse it.</summary>
public readonly struct ShakeSpec
{
    /// <summary>Amplitude within <see cref="Inner"/> of the source.</summary>
    public readonly float Amplitude;

    /// <summary>Metres at which the shake has faded out, and within which it is at full <see cref="Amplitude"/>.</summary>
    public readonly float Range, Inner;

    /// <summary>Seconds the shake lasts, and its frequency.</summary>
    public readonly float Seconds, Frequency;

    /// <param name="amplitude">Amplitude at the source.</param>
    /// <param name="range">Metres at which it has faded out (linearly from <paramref name="inner"/>).</param>
    /// <param name="seconds">Seconds it lasts.</param>
    /// <param name="frequency">Its frequency.</param>
    /// <param name="inner">Metres within which it is at full amplitude.</param>
    public ShakeSpec(float amplitude, float range, float seconds = 0.7f, float frequency = 18f, float inner = 0f)
    {
        Amplitude = amplitude;
        Range = range;
        Seconds = seconds;
        Frequency = frequency;
        Inner = inner;
    }
}

/// <summary>
/// The local player's camera shake for every mod, through the game's own <c>FPSCamera.Shake</c> (which also applies the game's own
/// camera-shake setting). A shake falls off with distance (<see cref="ShakeSpec"/>), is scaled by the player's
/// <c>Camera / ShakeStrength</c> (0 turns every Forge shake off, for players who get motion sick), and shakes running together never
/// add up past <see cref="Cap"/>: a new one only fills what is left. A <see cref="Kick"/> is a short push of the view in one direction
/// (a recoil, a blast from one side). Main thread only; nothing allocates.
/// </summary>
public static class CameraShake
{
    /// <summary>The most the running shakes of every mod add up to.</summary>
    public const float Cap = 1f;

    private static readonly ShakeStack Running = new(16);

    /// <summary>The player's strength setting, 0 to 1.</summary>
    public static float Strength => ShakeMath.Setting(Plugin.ShakeStrength.Value);

    /// <summary>Shakes the local camera for something at <paramref name="at"/>; returns the amplitude that reached the camera (0 when
    /// out of range, turned off or the cap is full).</summary>
    public static float Shake(Vector3 at, in ShakeSpec spec, Vector3 direction = default)
    {
        var camera = Camera(out var eye);
        if (camera == null) return 0f;
        var amplitude = spec.Amplitude * ShakeMath.Falloff(Vector3.Distance(eye, at), spec.Inner, spec.Range) * Strength;
        return Apply(camera, amplitude, spec, direction);
    }

    private static float Apply(FPSCamera camera, float amplitude, in ShakeSpec spec, Vector3 direction)
    {
        amplitude = Running.Admit(Time.time, amplitude, spec.Seconds, Cap);
        if (amplitude <= 0f) return 0f;
        if (direction.sqrMagnitude < 1e-8f) camera.Shake(spec.Seconds, amplitude, spec.Frequency);
        else camera.Shake(spec.Seconds, amplitude, spec.Frequency, direction.normalized);
        return amplitude;
    }

    /// <summary>Whether a shake of <paramref name="spec"/> for something at <paramref name="at"/> reaches the local camera at all
    /// (inside its range), whatever the strength setting and the cap then make of it.</summary>
    public static bool InRange(Vector3 at, in ShakeSpec spec)
        => Camera(out var eye) != null && ShakeMath.Falloff(Vector3.Distance(eye, at), spec.Inner, spec.Range) > 0f;

    /// <summary>A shake with no distance falloff, in a world direction when <paramref name="direction"/> is not zero (a camera
    /// shake the author aimed at the viewer); under the same setting and cap. Returns the amplitude that reached the camera.</summary>
    public static float ShakeHere(in ShakeSpec spec, Vector3 direction = default)
    {
        var camera = Camera(out _);
        return camera == null ? 0f : Apply(camera, spec.Amplitude * Strength, spec, direction);
    }

    /// <summary>A short push of the local view along <paramref name="direction"/> (world space) of <paramref name="amplitude"/> over
    /// <paramref name="seconds"/>, under the same setting and cap; returns the amplitude used.</summary>
    public static float Kick(Vector3 direction, float amplitude, float seconds = 0.25f, float frequency = 6f)
    {
        var camera = Camera(out _);
        if (camera == null || direction.sqrMagnitude < 1e-8f) return 0f;
        amplitude = Running.Admit(Time.time, amplitude * Strength, seconds, Cap);
        if (amplitude > 0f) camera.Shake(seconds, amplitude, frequency, direction.normalized);
        return amplitude;
    }

    internal static void Clear() => Running.Clear();

    private static FPSCamera? Camera(out Vector3 eye)
    {
        eye = default;
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return null;
        var camera = agent.FPSCamera;
        if (camera == null || camera.WasCollected) return null;
        eye = agent.transform.position;
        return camera;
    }
}
