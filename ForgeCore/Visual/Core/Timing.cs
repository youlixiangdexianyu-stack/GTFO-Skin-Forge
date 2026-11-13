using System;

namespace ForgeVisual;

/// <summary>The time windows of sustained effects (<c>Fx.Hold</c>): a hold with the key of a live, still-emitting copy of the same effect
/// keeps that copy emitting; a copy not held again within its window stops emitting, lets its particles finish
/// and is then free again. The key is the caller's own (a jet's number, an object's id), so two things close together never share a copy.
/// Fixed arrays per effect, no allocation.</summary>
internal sealed class HoldBook
{
    private readonly bool[] _playing, _emitting;
    private readonly float[] _until;
    private readonly int[] _key;
    private readonly long[] _started;
    private long _sequence;

    internal HoldBook(int copies)
    {
        if (copies <= 0) throw new ArgumentOutOfRangeException(nameof(copies), "An effect keeps at least one copy.");
        _playing = new bool[copies];
        _emitting = new bool[copies];
        _until = new float[copies];
        _key = new int[copies];
        _started = new long[copies];
    }

    internal int Copies => _playing.Length;
    internal bool Playing(int copy) => _playing[copy];
    internal bool Emitting(int copy) => _emitting[copy];

    /// <summary>The playing, still-emitting copy held under <paramref name="key"/>, or -1.</summary>
    internal int Find(int key)
    {
        for (var i = 0; i < _playing.Length; i++)
            if (_playing[i] && _emitting[i] && _key[i] == key) return i;
        return -1;
    }

    /// <summary>A free copy, else the oldest playing one that is not a hold still being kept (<paramref name="hold"/> says which copies were started as holds;
    /// it restarts). When only kept holds are left: another hold gets -1 (it never takes a copy another key keeps alive, which would swap them every frame), a one-shot the oldest of them.</summary>
    internal int Take(bool[] hold, bool forHold)
    {
        int oldest = -1, oldestAny = 0;
        for (var i = 0; i < _playing.Length; i++)
        {
            if (!_playing[i]) return i;
            if (_started[i] < _started[oldestAny]) oldestAny = i;
            if (!(hold[i] && _emitting[i]) && (oldest < 0 || _started[i] < _started[oldest])) oldest = i;
        }
        return oldest >= 0 ? oldest : forHold ? -1 : oldestAny;
    }

    /// <summary>Starts <paramref name="copy"/> under <paramref name="key"/>; a sustained start (<paramref name="window"/> above 0) emits until now + window.</summary>
    internal void Start(int copy, int key, float now, float window)
    {
        _playing[copy] = true;
        _emitting[copy] = window > 0f;
        _until[copy] = window > 0f ? now + window : 0f;
        _started[copy] = ++_sequence;
        _key[copy] = key;
    }

    /// <summary>A held copy keeps emitting for another window from now.</summary>
    internal void Extend(int copy, float now, float window) => _until[copy] = now + window;

    /// <summary>True when <paramref name="copy"/> has to stop emitting now (its window ran out); it keeps playing until its
    /// particles are gone (<see cref="Finished"/>).</summary>
    internal bool Expired(int copy, float now)
    {
        if (!_playing[copy] || !_emitting[copy] || now < _until[copy]) return false;
        _emitting[copy] = false;
        return true;
    }

    /// <summary>Stops emitting at once (the user ended the hold).</summary>
    internal void Stop(int copy) => _emitting[copy] = false;

    internal void Finished(int copy)
    {
        _playing[copy] = false;
        _emitting[copy] = false;
    }
}

/// <summary>The last positions of a moving thing at a fixed sample rate, newest first: the points of a ribbon trail. The newest
/// point always follows the thing; a new point is pushed every <see cref="Step"/> seconds. Fixed storage.</summary>
internal sealed class RibbonRing<T> where T : struct
{
    private readonly T[] _ring;
    private int _head;
    private float _next;

    internal RibbonRing(int points, float step)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points), "A ribbon has at least two points.");
        if (!(step > 0f)) throw new ArgumentOutOfRangeException(nameof(step), "The sample step must be positive.");
        _ring = new T[points];
        Step = step;
    }

    internal int Count => _ring.Length;
    internal float Step { get; }

    /// <summary>Every point at <paramref name="start"/>.</summary>
    internal void Reset(T start, float now)
    {
        for (var i = 0; i < _ring.Length; i++) _ring[i] = start;
        _head = 0;
        _next = now + Step;
    }

    internal void Sample(T position, float now)
    {
        while (now >= _next)
        {
            _head = (_head + 1) % _ring.Length;
            _ring[_head] = position;
            _next += Step;
            // After a long stall the ribbon restarts its clock instead of filling every missed sample with one point.
            if (now - _next > 0.1f) _next = now + Step;
        }
        _ring[_head] = position;
    }

    /// <summary>Point <paramref name="i"/>: 0 is the newest.</summary>
    internal T this[int i] => _ring[(_head - i + _ring.Length * 2) % _ring.Length];
}

/// <summary>Where the copies of an effect covering a round area stand.</summary>
internal static class FieldPoints
{
    /// <summary>The points of a hexagonal grid <paramref name="spacing"/> metres apart inside <paramref name="radius"/>, as x, z pairs, nearest the centre first, at most
    /// <paramref name="cap"/> (the spacing widens until they fit, so the whole area stays covered).</summary>
    internal static float[] Disc(float radius, float spacing, int cap)
    {
        if (!(radius > 0f) || !(spacing > 0f) || cap < 1) return new[] { 0f, 0f };
        // The disc's area over one hexagonal cell's (0.866 s^2) says how wide the cap allows.
        var s = Math.Max(spacing, (float)Math.Sqrt(Math.PI * radius * radius / (0.866 * cap)));
        var rows = (int)(radius / (s * 0.866f)) + 1;
        var columns = (int)(radius / s) + 1;
        var found = new System.Collections.Generic.List<(float D, float X, float Z)>();
        for (var r = -rows; r <= rows; r++)
            for (var c = -columns - 1; c <= columns + 1; c++)
            {
                var x = (c + (r & 1) * 0.5f) * s;
                var z = r * s * 0.866f;
                var d = x * x + z * z;
                if (d <= radius * radius) found.Add((d, x, z));
            }
        found.Sort((a, b) => a.D.CompareTo(b.D));
        var count = Math.Min(cap, found.Count);
        var points = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            points[i * 2] = found[i].X;
            points[i * 2 + 1] = found[i].Z;
        }
        return points;
    }
}

/// <summary>The numbers behind a ground marker's animation.</summary>
internal static class MarkerMath
{
    /// <summary>A hidden rim (the dim dashed line behind walls) is shown only near the mark: within its reach plus this across.</summary>
    internal const float RevealMargin = 3f, RevealHeight = 2.5f;

    /// <summary>How much of the hidden rim to draw for an eye <paramref name="across"/> metres from the centre on the floor plane and
    /// <paramref name="up"/> above or below it, fading over the last metre, so further rooms and the floors above and below never see it.</summary>
    internal static float Reveal(float across, float up, float reach)
        => ForgeUI.Ease.Clamp01(reach + RevealMargin - across) * ForgeUI.Ease.Clamp01(RevealHeight - Math.Abs(up));

    /// <summary>A mark sweeps on clockwise from north over <paramref name="seconds"/>.</summary>
    internal static float Sweep(float age, float seconds) => seconds <= 0f ? 1f : ForgeUI.Ease.Clamp01(age / seconds);

    /// <summary>The ping's phase after <paramref name="seconds"/> at <paramref name="perSecond"/>, in [0, 1).</summary>
    internal static float Advance(float phase, float perSecond, float seconds)
    {
        var next = phase + Math.Max(0f, perSecond) * Math.Max(0f, seconds);
        return next - (float)Math.Floor(next);
    }

    /// <summary>The blink of a tick, 1 at the tick and fading.</summary>
    internal static float Blink(float sinceTick) => sinceTick < 0f ? 0f : (float)Math.Exp(-sinceTick * 7f);
}
