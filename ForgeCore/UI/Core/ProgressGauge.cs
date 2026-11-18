using System;

namespace ForgeUI;

/// <summary>What feeds a <see cref="ProgressGauge"/>: a share of a process (a deploy, a scan, a charge), the seconds left of a cooldown or a
/// countdown, or a key held down to confirm.</summary>
public enum ProgressMode : byte
{
    /// <summary>A share, 0..1 while it runs, negative while nothing runs (<see cref="ProgressGauge.Fraction"/>).</summary>
    Fraction,
    /// <summary>Seconds left out of a full length (<see cref="ProgressGauge.Cooldown"/>): a cooldown, a countdown.</summary>
    Cooldown,
    /// <summary>A key held down until <see cref="ProgressGauge.HoldSeconds"/> have passed (<see cref="ProgressGauge.Hold"/>).</summary>
    Hold
}

/// <summary>How a <see cref="ProgressGauge"/> is drawn.</summary>
internal enum ProgressShape : byte
{
    /// <summary>The shared progress bar (<see cref="ProgressBar"/>).</summary>
    Bar,
    /// <summary>A ring swept clockwise from the top.</summary>
    Ring
}

/// <summary>
/// One process as every ForgeUI widget shows it, without Unity: feed it once per frame from one of <see cref="Fraction"/>,
/// <see cref="Cooldown"/> or <see cref="Hold"/>, and read what to draw. <see cref="Fill"/> is how far it has got (a cooldown fills towards
/// ready), <see cref="Sweep"/> the share a ring draws (what is still to go for a cooldown or a hold, what is done for a share),
/// <see cref="Seconds"/> the time left of a cooldown, <see cref="Text"/> the figure to write and <see cref="DoneAge"/> the time since it
/// completed, which drives the shared flash and fold. A process abandoned before its end disappears at once without a flash. Texts are the
/// cached ones of <see cref="NumberText"/>: reading allocates nothing.
/// </summary>
public sealed class ProgressGauge
{
    private readonly HoldTimer _hold = new(1f);
    private float _last, _doneAt;
    private bool _running, _done;

    /// <summary>The source fed last.</summary>
    public ProgressMode Mode { get; private set; }

    /// <summary>How far the process has got, 0..1 (1 once done).</summary>
    public float Fill { get; private set; }

    /// <summary>The seconds left of a cooldown; −1 for the other sources and while none runs.</summary>
    public float Seconds { get; private set; } = -1f;

    /// <summary>Seconds since the process completed; negative while it runs or nothing completed.</summary>
    public float DoneAge { get; private set; } = -1f;

    /// <summary>The process runs now (not yet complete).</summary>
    public bool Running => _running;

    /// <summary>Anything is drawn: the process runs, or it completed less than a flash and a fold ago.</summary>
    public bool Visible { get; private set; }

    /// <summary>How long a hold has to last, in seconds.</summary>
    public float HoldSeconds
    {
        get => _hold.Seconds;
        set => _hold.Seconds = Math.Max(0.01f, value);
    }

    /// <summary>A hold's own share, 0..1: full while the key stays down after completing, draining once it is let go (0 for the other sources).</summary>
    public float Held => Mode == ProgressMode.Hold ? _hold.Progress : 0f;

    /// <summary>The share a ring sweeps: what is still to go for a cooldown or a hold (it empties as the moment comes), what is done for a share.</summary>
    public float Sweep => !_running ? 0f : Mode == ProgressMode.Fraction ? Fill : 1f - Fill;

    /// <summary>The ring's sector count for <see cref="Sweep"/> (see <see cref="CooldownMath.PieStep"/>): 0 when there is nothing to sweep.</summary>
    public int PieStep => CooldownMath.PieStep(Sweep);

    /// <summary>The figure to write: the seconds left of a cooldown ("9.4", whole seconds from 10 on), else the percentage done; empty while
    /// nothing runs.</summary>
    public string Text => !_running ? "" : Mode == ProgressMode.Cooldown ? NumberText.Left(Seconds) : NumberText.Percent((int)MathF.Floor(Fill * 100f));

    /// <summary>A share of a process: 0..1 while it runs, negative while nothing runs. Reaching 1, or stopping within its last tenth (a frame
    /// can skip over the end), completes it.</summary>
    public void Fraction(float fraction, float now)
    {
        Mode = ProgressMode.Fraction;
        Seconds = -1f;
        if (fraction >= 0f && fraction < 1f)
        {
            Run(fraction);
            return;
        }
        Stop(fraction >= 1f || _last >= 0.9f, now);
    }

    /// <summary>A cooldown or countdown with <paramref name="remaining"/> of <paramref name="total"/> seconds left: it fills towards ready and
    /// completes when the time runs out after it ran (0 or less: nothing runs).</summary>
    public void Cooldown(float remaining, float total, float now)
    {
        Mode = ProgressMode.Cooldown;
        if (remaining > 0f)
        {
            Run(1f - CooldownMath.Remaining01(remaining, total));
            Seconds = remaining;
            return;
        }
        Seconds = -1f;
        Stop(true, now);
    }

    /// <summary>A key held to confirm: feed whether it is down every frame with the frame time. The fill grows while held and drains quickly
    /// after release (<see cref="HoldTimer"/>). True on the one frame the hold completes; holding on does not fire again until the key is let
    /// go.</summary>
    public bool Hold(bool down, float dt, float now)
    {
        Mode = ProgressMode.Hold;
        Seconds = -1f;
        var fired = _hold.Update(down, dt);
        var progress = _hold.Progress;
        if (fired) { Run(1f); Stop(true, now); return true; }
        if (progress > 0f && !_hold.Completed) Run(progress);
        else if (_running) Stop(false, now);
        else Settle(now);
        return false;
    }

    /// <summary>Back to nothing running, without a flash.</summary>
    public void Reset()
    {
        _hold.Reset();
        _running = _done = false;
        _last = 0f;
        Fill = 0f;
        Seconds = -1f;
        DoneAge = -1f;
        Visible = false;
    }

    private void Run(float fill)
    {
        _running = true;
        _done = false;
        _last = fill;
        Fill = Ease.Clamp01(fill);
        DoneAge = -1f;
        Visible = true;
    }

    private void Stop(bool complete, float now)
    {
        if (_running)
        {
            _running = false;
            _done = complete;
            if (complete) _doneAt = now;
        }
        Settle(now);
    }

    private void Settle(float now)
    {
        if (_done)
        {
            Fill = 1f;
            DoneAge = now - _doneAt;
            Visible = ProgressBar.Visible(DoneAge);
            if (!Visible) _done = false;
            return;
        }
        Visible = false;
        Fill = 0f;
        DoneAge = -1f;
    }
}
