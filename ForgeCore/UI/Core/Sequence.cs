using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>
/// A timed power-up or power-down: tracks, each a span of the timeline, that write their progress (0..1, eased) to whatever they
/// drive (a template's <c>Visible</c>, a visor's <c>StrutSlide</c>…). Add the tracks once; then <see cref="Play"/>,
/// <see cref="Tick"/> every frame with the clock, and <see cref="Skip"/> to jump to the end (every track gets its final value at once).
/// Playing backwards runs the same tracks from the end to the start (a power-down). No allocation after the tracks are added.
/// </summary>
public sealed class HudSequence
{
    private readonly struct Track
    {
        internal readonly float Start, Seconds;
        internal readonly Action<float> Set;
        internal readonly bool Smooth;
        internal Track(float start, float seconds, Action<float> set, bool smooth)
        {
            Start = start; Seconds = seconds; Set = set; Smooth = smooth;
        }
    }

    private readonly List<Track> _tracks = new();
    private float _startedAt = float.NaN;
    private bool _backwards;

    /// <summary>The timeline's length: the end of its last track, in seconds.</summary>
    public float Length { get; private set; }

    /// <summary>True from <see cref="Play"/> until the timeline has run out or was skipped.</summary>
    public bool Playing { get; private set; }

    /// <summary>Raised once when a run ends, by running out or by <see cref="Skip"/>.</summary>
    public event Action? Finished;

    /// <summary>Adds a track from <paramref name="start"/> lasting <paramref name="seconds"/> (0: a step at <paramref name="start"/>)
    /// that writes its progress to <paramref name="set"/>: 0 before, 1 after, eased in between unless <paramref name="smooth"/> is
    /// false.</summary>
    public HudSequence Add(float start, float seconds, Action<float> set, bool smooth = true)
    {
        if (set == null) throw new ArgumentNullException(nameof(set));
        if (!float.IsFinite(start) || start < 0f) throw new ArgumentOutOfRangeException(nameof(start));
        if (!float.IsFinite(seconds) || seconds < 0f) throw new ArgumentOutOfRangeException(nameof(seconds));
        _tracks.Add(new Track(start, seconds, set, smooth));
        Length = Math.Max(Length, start + seconds);
        return this;
    }

    /// <summary>Starts a run at <paramref name="now"/>; <paramref name="backwards"/> plays it from the end to the start.</summary>
    public void Play(float now, bool backwards = false)
    {
        _startedAt = now;
        _backwards = backwards;
        Playing = true;
        Write(backwards ? Length : 0f);
    }

    /// <summary>Writes every track's value at <paramref name="now"/>; ends the run when the timeline has run out.</summary>
    public void Tick(float now)
    {
        if (!Playing) return;
        var age = Math.Max(0f, now - _startedAt);
        if (age >= Length) { Skip(); return; }
        Write(_backwards ? Length - age : age);
    }

    /// <summary>Jumps to the end of the run: every track gets its final value now.</summary>
    public void Skip()
    {
        if (!Playing) return;
        Playing = false;
        Write(_backwards ? -1f : Length + 1f);
        Finished?.Invoke();
    }

    /// <summary>A track's value at time <paramref name="t"/> of the timeline.</summary>
    public static float Progress(float t, float start, float seconds, bool smooth)
    {
        if (t <= start) return seconds <= 0f && t >= start ? 1f : 0f;
        if (seconds <= 0f || t >= start + seconds) return 1f;
        var p = (t - start) / seconds;
        return smooth ? Ease.OutCubic(p) : p;
    }

    private void Write(float t)
    {
        foreach (var track in _tracks) track.Set(Progress(t, track.Start, track.Seconds, track.Smooth));
    }
}
