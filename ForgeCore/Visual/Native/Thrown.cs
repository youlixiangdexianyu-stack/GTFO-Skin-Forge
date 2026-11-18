using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The path of a part thrown clear of a model (a hatch lid blown off, a panel, a casing): a parabola with a constant tumble, until it lands
/// on the ground and lies still. Stateless: <see cref="Pose"/> gives the pose <c>seconds</c> after the throw, so every peer that counts
/// from the same moment, a late joiner included, puts the part in the same place with no per-frame state, registry or allocation.
/// </summary>
public readonly struct Thrown
{
    private readonly Vector3 _origin, _velocity, _axis;
    private readonly Quaternion _rotation;
    private readonly float _spin, _gravity;

    /// <param name="origin">Where the part starts.</param>
    /// <param name="rotation">Its rotation at the start.</param>
    /// <param name="velocity">Its start velocity, metres a second.</param>
    /// <param name="spinAxis">The world axis it tumbles about (need not be unit).</param>
    /// <param name="spinDegrees">Degrees a second about <paramref name="spinAxis"/> while it flies.</param>
    /// <param name="groundY">The world height it lands at (the part's own origin).</param>
    /// <param name="gravity">Metres a second squared, down.</param>
    public Thrown(Vector3 origin, Quaternion rotation, Vector3 velocity, Vector3 spinAxis, float spinDegrees, float groundY, float gravity = 9.81f)
    {
        _origin = origin; _rotation = rotation; _velocity = velocity; _spin = spinDegrees; _gravity = Mathf.Max(0.01f, gravity);
        _axis = spinAxis.sqrMagnitude > 1e-8f ? spinAxis.normalized : Vector3.up;
        var rise = origin.y - groundY;
        var disc = velocity.y * velocity.y + 2f * _gravity * Mathf.Max(0f, rise);
        FlightSeconds = (velocity.y + Mathf.Sqrt(disc)) / _gravity;
    }

    /// <summary>Seconds from the throw until it lands (it lies still from then on).</summary>
    public float FlightSeconds { get; }

    /// <summary>Where a thing thrown from <paramref name="from"/> to <paramref name="to"/> is <paramref name="progress"/> (0 to 1) of the way: a
    /// straight line plus a parabola that peaks <paramref name="apex"/> metres above it halfway (the same parabola as a throw with that
    /// gravity, run over one unit of time). Outside 0 to 1 the end points hold. The same on every peer given the same numbers.</summary>
    public static Vector3 Arc(Vector3 from, Vector3 to, float apex, float progress)
        => At(from, to - from + Vector3.up * (4f * apex), 8f * apex, Mathf.Clamp01(progress));

    private static Vector3 At(Vector3 origin, Vector3 velocity, float gravity, float t)
        => origin + velocity * t + Vector3.down * (0.5f * gravity * t * t);

    /// <summary>The pose <paramref name="seconds"/> after the throw (before it: the start pose).</summary>
    public void Pose(float seconds, out Vector3 position, out Quaternion rotation)
    {
        var t = Mathf.Clamp(seconds, 0f, FlightSeconds);
        position = At(_origin, _velocity, _gravity, t);
        rotation = Quaternion.AngleAxis(_spin * t, _axis) * _rotation;
    }
}
