using System;

namespace ForgeUI;

/// <summary>A hold-to-confirm timer: feed it whether the key is down every frame. <see cref="Progress"/> fills while held and drains
/// quickly after release; <see cref="Update"/> returns true on the single frame the hold completes. Holding on after that does
/// not fire again until the key has been let go.</summary>
internal sealed class HoldTimer
{
    private float _held;
    private bool _fired;

    /// <param name="seconds">How long the key must be held.</param>
    /// <param name="releaseSeconds">How long a full bar takes to drain once released.</param>
    public HoldTimer(float seconds, float releaseSeconds = 0.25f)
    {
        Seconds = Math.Max(0.01f, seconds);
        ReleaseSeconds = Math.Max(0.01f, releaseSeconds);
    }

    public float Seconds { get; set; }

    public float ReleaseSeconds { get; set; }

    /// <summary>How far the hold has got, 0..1.</summary>
    public float Progress => Ease.Clamp01(_held / Seconds);

    public bool Completed => _fired;

    public bool Update(bool down, float dt)
    {
        if (down)
        {
            _held = Math.Min(Seconds, _held + dt);
            if (_held >= Seconds && !_fired) { _fired = true; return true; }
            return false;
        }
        _held = Math.Max(0f, _held - dt * Seconds / ReleaseSeconds);
        if (_held <= 0f) _fired = false;
        return false;
    }

    public void Reset()
    {
        _held = 0f;
        _fired = false;
    }
}
