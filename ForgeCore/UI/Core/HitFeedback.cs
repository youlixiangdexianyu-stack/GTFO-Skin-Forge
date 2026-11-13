using System;

namespace ForgeUI;

/// <summary>How hard a hit landed, for the hit marker and damage numbers.</summary>
public enum HitKind : byte
{
    Hit,
    Critical,
    Kill
}

/// <summary>
/// Floating damage numbers as state, without Unity: a fixed pool of at most <see cref="Capacity"/> numbers. Hits on one target close
/// together (same non-zero key within <see cref="MergeSeconds"/> of its last hit) add up into one number; past the cap the number born
/// longest ago is dropped for the new one. Every array is made once: adding, ticking and reading
/// allocate nothing.
/// </summary>
internal sealed class FloatingNumbers
{
    /// <summary>The most numbers any pool may hold.</summary>
    public const int Limit = 32;

    public struct Number
    {
        /// <summary>The target hits add up for; 0 never merges.</summary>
        public long Key;
        public bool Used;
        public float Amount, Born, Last;
        public HitKind Kind;
        /// <summary>A place in the world (projected every frame) when <see cref="World"/>, else HUD units from the screen's middle.</summary>
        public float X, Y, Z;
        public bool World;
        public int Frame;
    }

    private readonly Number[] _numbers;

    public FloatingNumbers(int capacity) => _numbers = new Number[Math.Clamp(capacity, 1, Limit)];

    public int Capacity => _numbers.Length;

    /// <summary>Seconds within which hits on one key add into the same number.</summary>
    public float MergeSeconds { get; set; } = 0.45f;

    /// <summary>Seconds a number lives after its last hit, rising and fading.</summary>
    public float Seconds { get; set; } = 0.8f;

    public ref readonly Number this[int slot] => ref _numbers[slot];

    /// <summary>Adds <paramref name="amount"/>; returns the slot it went to.</summary>
    public int Add(long key, float now, float amount, HitKind kind, float x, float y, float z, bool world, int frame)
    {
        if (key != 0)
            for (var i = 0; i < _numbers.Length; i++)
            {
                ref var n = ref _numbers[i];
                if (!n.Used || n.Key != key || now - n.Last > MergeSeconds) continue;
                n.Amount += amount;
                n.Last = now;
                n.Frame = frame;
                if (kind > n.Kind) n.Kind = kind;
                return i;
            }
        var slot = -1;
        for (var i = 0; i < _numbers.Length && slot < 0; i++) if (!_numbers[i].Used) slot = i;
        if (slot < 0)
        {
            slot = 0;
            for (var i = 1; i < _numbers.Length; i++) if (_numbers[i].Born < _numbers[slot].Born) slot = i;
        }
        _numbers[slot] = new Number { Key = key, Used = true, Amount = amount, Born = now, Last = now, Kind = kind, X = x, Y = y, Z = z, World = world, Frame = frame };
        return slot;
    }

    /// <summary>The hit of <paramref name="frame"/> on <paramref name="key"/> was on a weak spot: its number turns critical.</summary>
    public void MarkCritical(long key, int frame)
    {
        for (var i = 0; i < _numbers.Length; i++)
        {
            ref var n = ref _numbers[i];
            if (n.Used && n.Key == key && n.Frame == frame && n.Kind == HitKind.Hit) n.Kind = HitKind.Critical;
        }
    }

    /// <summary>Frees numbers past their life.</summary>
    public void Tick(float now)
    {
        for (var i = 0; i < _numbers.Length; i++)
            if (_numbers[i].Used && now - _numbers[i].Last > Seconds) _numbers[i] = default;
    }

    /// <summary>How far a number has risen (0..1, from its first hit) and how faded it is (0..1, from its last), so a run of merged hits
    /// stays readable.</summary>
    public void Motion(in Number n, float now, out float rise, out float fade)
    {
        rise = Ease.OutCubic(Ease.Clamp01((now - n.Born) / Seconds));
        fade = Ease.Clamp01((now - n.Last) / Seconds);
    }

    public void Clear() => Array.Clear(_numbers);
}

/// <summary>
/// The rest of the hit feedback as state, without Unity: the hit marker's flash (the strongest kind of one frame wins), the kill marks
/// where killed targets were (a ring of <see cref="Kills"/>, the oldest overwritten) and when the last kill was, for the confirmation
/// beside the crosshair.
/// </summary>
internal sealed class HitFeedbackBook
{
    public const int Kills = 4;
    public const float HitSeconds = 0.22f, KillPulseSeconds = 0.5f, KillMarkSeconds = 0.35f, ConfirmSeconds = 0.3f;

    public struct Kill
    {
        public bool Used;
        public float Born, X, Y, Z;
    }

    public readonly Kill[] Marks = new Kill[Kills];
    private int _nextKill, _pulseFrame = int.MinValue;

    public HitFeedbackBook(int numbers) => Numbers = new FloatingNumbers(numbers);

    public FloatingNumbers Numbers { get; }

    public HitKind PulseKind { get; private set; }
    public float PulseStart { get; private set; } = float.NegativeInfinity;
    public float LastKill { get; private set; } = float.NegativeInfinity;

    /// <summary>Starts the hit marker's flash; a weaker kind in the same frame keeps the stronger one.</summary>
    public void Pulse(HitKind kind, float now, int frame)
    {
        if (frame == _pulseFrame && kind <= PulseKind) return;
        _pulseFrame = frame;
        PulseKind = kind;
        PulseStart = now;
    }

    /// <summary>How far the flash has run (0..1); 1 or more when none shows.</summary>
    public float PulseProgress(float now) => (now - PulseStart) / (PulseKind == HitKind.Kill ? KillPulseSeconds : HitSeconds);

    /// <summary>A kill: a mark at the place and the confirmation starts.</summary>
    public void Killed(float now, float x, float y, float z)
    {
        Marks[_nextKill] = new Kill { Used = true, Born = now, X = x, Y = y, Z = z };
        _nextKill = (_nextKill + 1) % Kills;
        LastKill = now;
    }

    public void Tick(float now)
    {
        Numbers.Tick(now);
        for (var i = 0; i < Kills; i++)
            if (Marks[i].Used && now - Marks[i].Born > KillMarkSeconds) Marks[i] = default;
    }

    public void Clear()
    {
        Numbers.Clear();
        Array.Clear(Marks);
        LastKill = PulseStart = float.NegativeInfinity;
        _pulseFrame = int.MinValue;
    }
}
