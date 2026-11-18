using System;

namespace ForgeUI;

/// <summary>
/// An element coming online (a cockpit power-on, see <c>HudElement.Reveal</c>), <see cref="Seconds"/> long: corner brackets trace out
/// from the element's anchor (<see cref="Trace"/>), a bright scan line then sweeps across it along its region's flow (from the middle out
/// for a centred element) with the content appearing behind it and its texts typing on (<see cref="Sweep"/>), and last the whole element
/// flashes white as its brackets jump outwards and fade (<see cref="Lock"/>, <see cref="Burst"/>). Pure timing and geometry; the canvas
/// draws it.
/// </summary>
public static class HudReveal
{
    public const float Seconds = 0.42f;
    public const float TraceEnd = 0.14f, SweepStart = 0.10f, SweepEnd = 0.30f, LockStart = 0.34f;

    /// <summary>Whether a reveal started <paramref name="age"/> seconds ago is still running.</summary>
    public static bool Running(float age) => age >= 0f && age < Seconds;

    /// <summary>How far the corner brackets have traced out (0..1).</summary>
    public static float Trace(float age) => Ease.OutCubic(age / TraceEnd);

    /// <summary>How far the scan line has swept across the element (0..1).</summary>
    public static float Sweep(float age) => Ease.InOutCubic((age - SweepStart) / (SweepEnd - SweepStart));

    /// <summary>The white lock flash (1 at its start, 0 when the reveal ends; 0 before it).</summary>
    public static float Lock(float age) => age < LockStart ? 0f : 1f - Ease.Clamp01((age - LockStart) / (Seconds - LockStart));

    /// <summary>How far the brackets have jumped outwards at the lock (0..1).</summary>
    public static float Burst(float age) => age < LockStart ? 0f : Ease.OutCubic((age - LockStart) / (Seconds - LockStart));

    /// <summary>The element's extent along one axis from its root, for a flow of -1 (it grows towards negative), 1 or 0 (centred):
    /// from <paramref name="low"/> to <paramref name="high"/>.</summary>
    public static void Extent(int flow, float size, out float low, out float high)
    {
        size = Math.Max(0f, size);
        if (flow > 0) { low = 0f; high = size; }
        else if (flow < 0) { low = -size; high = 0f; }
        else { low = -size / 2f; high = size / 2f; }
    }

    /// <summary>Where the scan line is at <paramref name="sweep"/> across [low, high]: it starts at the root's side (the far side of a
    /// flow of -1 is the low end) and, for a centred element, two lines run out from the middle (<paramref name="other"/> the second).</summary>
    public static float Front(int flow, float low, float high, float sweep, out float other)
    {
        sweep = Ease.Clamp01(sweep);
        if (flow > 0) { other = float.NaN; return low + (high - low) * sweep; }
        if (flow < 0) { other = float.NaN; return high - (high - low) * sweep; }
        var mid = (low + high) / 2f;
        var half = (high - low) / 2f * sweep;
        other = mid - half;
        return mid + half;
    }

    /// <summary>The share (0..1) of the span [<paramref name="left"/>, <paramref name="right"/>] the scan line has passed.</summary>
    public static float Passed(int flow, float low, float high, float sweep, float left, float right)
    {
        if (sweep >= 1f) return 1f;
        if (sweep <= 0f) return 0f;
        var width = right - left;
        var front = Front(flow, low, high, sweep, out var other);
        if (width <= 1e-3f)
        {
            var x = left;
            return flow > 0 ? (x <= front ? 1f : 0f) : flow < 0 ? (x >= front ? 1f : 0f) : (x <= front && x >= other ? 1f : 0f);
        }
        if (flow > 0) return Ease.Clamp01((front - left) / width);
        if (flow < 0) return Ease.Clamp01((right - front) / width);
        var shown = Math.Max(0f, Math.Min(right, front) - Math.Max(left, other));
        return Ease.Clamp01(shown / width);
    }
}
