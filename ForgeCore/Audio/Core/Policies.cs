using System;

namespace ForgeAudio;

/// <summary>A category's volume: the game's master slider times the slider it follows (music for music, dialogue for voice, sound
/// effects for the rest), times the kit's master and category volumes from its config.</summary>
internal static class CategoryVolume
{
    internal static float Map(AudioCategory category, float master, float sfx, float music, float dialogue, bool muteBackground, bool focused,
        float kitMaster = 1, float kitCategory = 1)
    {
        if (muteBackground && !focused) return 0;
        var slider = category switch { AudioCategory.Music => music, AudioCategory.Voice => dialogue, _ => sfx };
        return Unit(master) * Unit(slider) * Unit(kitMaster) * Unit(kitCategory);
    }
    private static float Unit(float n) => float.IsFinite(n) ? Math.Clamp(n, 0, 1) : 0;
}

internal enum ListenerDecision { Unavailable, ReuseExternal, KeepOwned, CreateOwned }
internal static class ListenerPolicy
{
    internal static ListenerDecision Decide(bool cameraActive, bool ownedOnCamera, int otherActive)
        => otherActive > 0 ? ListenerDecision.ReuseExternal : !cameraActive ? ListenerDecision.Unavailable
            : ownedOnCamera ? ListenerDecision.KeepOwned : ListenerDecision.CreateOwned;
}

internal enum OneShotState { Playing, Completed, Stalled }
internal struct OneShotProgress
{
    private double _phase, _lastDsp, _lastPosition, _lastProgress, _started;
    internal OneShotProgress(double position, double dsp, double now)
    { _phase = _lastPosition = position; _lastDsp = dsp; _lastProgress = _started = now; }

    internal OneShotState Update(double position, bool playing, double duration, float pitch, double dsp, double now)
    {
        var delta = Math.Max(0, dsp - _lastDsp);
        _lastDsp = dsp;
        _phase = Math.Max(position, _phase + delta * pitch);
        if (delta > 0 || position > _lastPosition) _lastProgress = now;
        _lastPosition = position;
        // Unity can leave isPlaying true after an output failure. Bound that lifetime by DSP progress too.
        if (position >= duration || _phase >= duration + .25 || !playing && now - _started >= .1)
            return OneShotState.Completed;
        return now - _lastProgress >= 2 ? OneShotState.Stalled : OneShotState.Playing;
    }
}

internal enum VoiceGroup { General, Dialogue, Warning, EnvironmentLoop }
internal readonly record struct VoiceRank(int Priority, float Audibility, double Started, VoiceGroup Group = VoiceGroup.General);
internal static class VoicePolicy
{
    internal const int Capacity = 64, LogicalCapacity = 256;
    internal static bool CanSteal(VoiceRank incoming, VoiceRank current, double now)
        => now - current.Started >= .1 && (incoming.Priority > current.Priority
            || incoming.Priority == current.Priority && incoming.Audibility > current.Audibility + .05f);
    internal static bool Less(VoiceRank a, VoiceRank b)
        => a.Priority < b.Priority || a.Priority == b.Priority && (a.Audibility < b.Audibility || a.Audibility == b.Audibility && a.Started < b.Started);
    internal static int Select(ReadOnlySpan<VoiceRank?> active, VoiceRank incoming, double now)
    {
        Span<int> counts = stackalloc int[4];
        counts.Clear();
        for (var i = 0; i < active.Length; i++)
        {
            if (active[i] is not { } rank) return i;
            counts[(int)rank.Group]++;
        }
        var reclaim = counts[(int)incoming.Group] < Reserved(incoming.Group);
        var weakest = -1; var borrowed = -1;
        for (var i = 0; i < active.Length; i++)
        {
            var current = active[i]!.Value;
            if (current.Group != incoming.Group && counts[(int)current.Group] <= Reserved(current.Group)) continue;
            var mayReclaim = reclaim && current.Group != incoming.Group && now - current.Started >= .1;
            if (mayReclaim && (borrowed < 0 || Less(current, active[borrowed]!.Value))) borrowed = i;
            if (CanSteal(incoming, current, now) && (weakest < 0 || Less(current, active[weakest]!.Value))) weakest = i;
        }
        return borrowed >= 0 ? borrowed : weakest;
    }
    internal static int Reserved(VoiceGroup group) => group == VoiceGroup.General ? 0 : 8;
    internal static VoiceGroup Group(AudioCategory category, bool loop, int priority)
        => category == AudioCategory.Voice ? VoiceGroup.Dialogue : priority >= 200 ? VoiceGroup.Warning
            : loop && category is AudioCategory.Music or AudioCategory.Ambience ? VoiceGroup.EnvironmentLoop : VoiceGroup.General;
}

/// <summary>A sound that holds a seat on a bus: its cue priority, how audible it is (distance included) and when it started.</summary>
internal readonly record struct BusMember(int Priority, float Audibility, double Started);

/// <summary>Bus seats: a bus with N seats lets N of its sounds play at once from one emitter (the same Emitter key; plays without
/// one share a bucket). A new sound takes a free seat, else replaces the weakest holders (lowest priority, then least audible,
/// then oldest) when its priority is higher, or equal and it is not much quieter (at least half as audible) than the holder,
/// else is refused. Replaced sounds fade out quickly instead of cutting, so with one seat a bus is mutually exclusive.</summary>
internal static class BusPolicy
{
    internal const float ReplaceFade = .06f;

    /// <summary>Indexes into <paramref name="holders"/> to fade out so the incoming sound fits in <paramref name="seats"/> seats;
    /// -1 when the incoming sound must be refused because the holders it would have to replace outrank it.</summary>
    internal static int Evict(ReadOnlySpan<BusMember> holders, int seats, int incomingPriority, float incomingAudibility, Span<int> victims)
    {
        var needed = holders.Length - seats + 1;
        if (needed <= 0) return 0;
        for (var i = 0; i < holders.Length; i++) victims[i] = i;
        for (var i = 1; i < holders.Length; i++)
        {
            var at = victims[i]; var j = i - 1;
            while (j >= 0 && Weaker(holders[at], holders[victims[j]])) { victims[j + 1] = victims[j]; j--; }
            victims[j + 1] = at;
        }
        for (var i = 0; i < needed; i++)
        {
            var holder = holders[victims[i]];
            if (holder.Priority > incomingPriority || holder.Priority == incomingPriority && incomingAudibility < holder.Audibility * .5f) return -1;
        }
        return needed;
    }
    internal static bool Weaker(BusMember a, BusMember b)
        => a.Priority != b.Priority ? a.Priority < b.Priority : a.Audibility != b.Audibility ? a.Audibility < b.Audibility : a.Started < b.Started;
}
/// <summary>What a full category does with a newcomer.</summary>
internal enum CategoryAdmission { Room, PushOut, Refuse }

/// <summary>The global category limit, over the sounds of every mod: below the limit a sound is let in; at it, the weakest holder
/// (lowest priority, then least audible, so the farther one, then the oldest) is pushed out (faded quickly) when the newcomer has a
/// higher priority, or the same priority and is more audible (nearer); otherwise the newcomer is refused.</summary>
internal static class CategoryPolicy
{
    internal static CategoryAdmission Admit(ReadOnlySpan<BusMember> holders, int limit, int incomingPriority, float incomingAudibility, out int victim)
    {
        victim = -1;
        if (holders.Length < limit) return CategoryAdmission.Room;
        var weakest = 0;
        for (var i = 1; i < holders.Length; i++)
            if (BusPolicy.Weaker(holders[i], holders[weakest])) weakest = i;
        var holder = holders[weakest];
        if (incomingPriority > holder.Priority || incomingPriority == holder.Priority && incomingAudibility > holder.Audibility)
        {
            victim = weakest;
            return CategoryAdmission.PushOut;
        }
        return CategoryAdmission.Refuse;
    }
}

/// <summary>One sound that ducks: its index among the playing sounds, the categories it lowers (bits) and by how much.</summary>
internal readonly record struct DuckSource(int Index, int Mask, float Factor);

/// <summary>Ducking: while a sound of a bus that ducks categories plays, every other sound of those categories, of any mod, is
/// lowered by its decibels and ramps back once it ends. The lowest factor of all active duckers applies.</summary>
internal static class DuckPolicy
{
    internal const float DownRate = 25, UpRate = 6;
    internal static float Factor(float decibels) => MathF.Pow(10, -decibels / 20);
    /// <summary>The duck factor for the sound at <paramref name="index"/> of <paramref name="category"/>: the lowest factor of the
    /// duckers (other than itself) that duck its category, 1 when none does.</summary>
    internal static float Target(ReadOnlySpan<DuckSource> duckers, int index, AudioCategory category)
    {
        var bit = AudioCategories.Bit(category);
        var target = 1f;
        foreach (var d in duckers)
            if (d.Index != index && (d.Mask & bit) != 0 && d.Factor < target) target = d.Factor;
        return target;
    }
    internal static float Step(float current, float target, float dt)
        => current + (target - current) * (1 - MathF.Exp(-dt * (target < current ? DownRate : UpRate)));
}

internal sealed class OcclusionSchedule
{
    internal const int RaysPerFrame = 4;
    internal const double Interval = .2;
    private readonly double[] _due = new double[VoicePolicy.Capacity];
    private int _cursor, _frame = -1, _used;
    internal void Reset(int slot) => _due[slot] = 0;
    internal int Next(int frame, double now, bool[] eligible)
    {
        if (frame != _frame) { _frame = frame; _used = 0; }
        if (_used == RaysPerFrame) return -1;
        for (var n = 0; n < _due.Length; n++)
        {
            var slot = _cursor; _cursor = (_cursor + 1) % _due.Length;
            if (!eligible[slot] || now < _due[slot]) continue;
            _due[slot] = now + Interval; _used++; return slot;
        }
        return -1;
    }
}

internal sealed class EndGate
{
    internal bool Ended { get; private set; }
    internal bool End() { if (Ended) return false; Ended = true; return true; }
}
