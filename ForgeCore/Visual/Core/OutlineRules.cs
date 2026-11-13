using EnemyAlert = ForgeCore.EnemyAlert;
using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>A colour as four floats, shown on screen exactly as given (the line is laid over the finished frame, after the game's
/// exposure and tonemapping). Channels above 1 show as 1; alpha is 0..1.</summary>
public readonly struct OutlineColor : IEquatable<OutlineColor>
{
    public OutlineColor(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }
    public bool Equals(OutlineColor other) => R == other.R && G == other.G && B == other.B && A == other.A;
    public override bool Equals(object? obj) => obj is OutlineColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    internal bool Valid => float.IsFinite(R) && float.IsFinite(G) && float.IsFinite(B) && R >= 0f && G >= 0f && B >= 0f && A is >= 0f and <= 1f;
}

/// <summary>How the automatic enemy outline looks and how far it reaches. Every value has a default; give only what you change.</summary>
public sealed class EnemyOutlineStyle
{
    /// <summary>Enemies farther than this many metres from the camera are not outlined.</summary>
    public float Range { get; init; } = 35f;
    /// <summary>Over the last this many metres before <see cref="Range"/> the line fades out instead of popping.</summary>
    public float FadeDistance { get; init; } = 5f;
    /// <summary>At most this many enemies, the nearest first, are outlined at once.</summary>
    public int MaxEnemies { get; init; } = 24;
    /// <summary>Line width in pixels at 1080p; it scales with the screen height. One width is drawn per screen: this style's while it is on.</summary>
    public float Width { get; init; } = OutlineRules.DefaultWidth;
    /// <summary>Asleep (hibernating).</summary>
    public OutlineColor Sleeping { get; init; } = new(0.23f, 0.40f, 0.44f, 0.40f);
    /// <summary>Awake but not hunting: patrolling, scouting.</summary>
    public OutlineColor Awake { get; init; } = new(0.80f, 0.56f, 0.22f, 0.65f);
    /// <summary>Hunting the players (aggressive).</summary>
    public OutlineColor Hunting { get; init; } = new(0.80f, 0.18f, 0.18f, 0.80f);
    /// <summary>True draws the line through walls too (round the whole silhouette): an opt-in for mods that mean to show what is behind a
    /// wall. False (the default) outlines only what the camera sees; Forge Frames' cockpit keeps it false.</summary>
    public bool XRay { get; init; }
    /// <summary>Against outlines added to one enemy with <c>Outlines.Add</c>: an added outline of this priority or higher replaces the
    /// automatic one on that enemy; a lower one is not drawn while the automatic one is.</summary>
    public int Priority { get; init; }

    /// <summary>The largest <see cref="MaxEnemies"/> accepted.</summary>
    public const int MaxEnemiesLimit = 128;
    /// <summary>The largest <see cref="Range"/> accepted, in metres.</summary>
    public const float RangeLimit = 500f;
    /// <summary>The largest <see cref="Width"/> accepted, in pixels at 1080p.</summary>
    public const float WidthLimit = 16f;

    public OutlineColor ColorOf(EnemyAlert state) => state switch
    {
        EnemyAlert.Sleeping => Sleeping,
        EnemyAlert.Awake => Awake,
        EnemyAlert.Hunting => Hunting,
        _ => default,
    };

    /// <summary>Why this style cannot be used, or null.</summary>
    public string? Problem()
    {
        if (!float.IsFinite(Range) || Range <= 0f || Range > RangeLimit) return "Range must be above 0 and at most " + RangeLimit + " metres";
        if (!float.IsFinite(FadeDistance) || FadeDistance < 0f || FadeDistance > Range) return "FadeDistance must be between 0 and Range";
        if (MaxEnemies < 1 || MaxEnemies > MaxEnemiesLimit) return "MaxEnemies must be between 1 and " + MaxEnemiesLimit;
        if (!float.IsFinite(Width) || Width <= 0f || Width > WidthLimit) return "Width must be above 0 and at most " + WidthLimit + " pixels";
        if (!Sleeping.Valid || !Awake.Valid || !Hunting.Valid) return "colours must be finite, not negative, with alpha 0..1";
        return null;
    }
}

/// <summary>Pure rules of the outline: line width on screen, what counts as in view, how many enemies are read per frame.</summary>
internal static class OutlineRules
{
    /// <summary>The line width (pixels at 1080p) while no automatic enemy outline is on.</summary>
    public const float DefaultWidth = 1.6f;

    /// <summary>The line width in pixels on a screen <paramref name="screenHeight"/> pixels tall.</summary>
    public static float PixelWidth(float width1080, int screenHeight) => width1080 * Math.Max(1, screenHeight) / 1080f;

    /// <summary>Enemies this close count as in view whatever the camera's direction, so one beside the player is never dropped.</summary>
    public const float NearAlways = 3f;
    /// <summary>An enemy counts as in view while the cosine between the camera's forward and the direction to it is above this
    /// (a wide cone, wider than any field of view the game allows, so turning does not make edge enemies blink).</summary>
    public const float ViewCosine = -0.1f;
    /// <summary>At most this many tracked enemies have their position and state read from the game per frame.</summary>
    public const int RefreshPerFrame = 24;
    /// <summary>After a limb is shot off the outline's draw list is rebuilt every frame for this many frames, so renderers the game
    /// hides or adds a moment after the hit (its destruction effects run over several frames) are followed.</summary>
    public const int LimbSettleFrames = 45;
    /// <summary>After an enemy is first seen or changes state (waking, climbing out of the ground or a wall) the game may show and hide
    /// body renderers while its animation plays: for this many seconds its renderers are read again every <see cref="WakeScanSeconds"/>,
    /// so the outline follows the body that is drawn and not the one that was drawn when it was first read.</summary>
    public const float WakeSeconds = 6f, WakeScanSeconds = 0.25f;

    /// <summary>Whether an enemy in its wake window (until <paramref name="wakeUntil"/>) reads its renderers again at <paramref name="now"/>.</summary>
    public static bool WakeScanDue(float now, float wakeUntil, float nextScan) => now < wakeUntil && now >= nextScan;

    /// <summary>The range given to the fill of an outline added to one object: it never fades with distance.</summary>
    public const float Unlimited = 1e6f;

    /// <summary>When an outline added for <paramref name="seconds"/> at <paramref name="now"/> ends: never (infinity) for 0.</summary>
    public static float Until(float now, float seconds) => seconds > 0f ? now + seconds : float.PositiveInfinity;

    /// <summary>Why an outline of <paramref name="color"/> lasting <paramref name="seconds"/> cannot be added, or null.</summary>
    public static string? Problem(OutlineColor color, float seconds)
    {
        if (!color.Valid) return "the colour must be finite, not negative, with alpha 0..1";
        if (!float.IsFinite(seconds) || seconds < 0f) return "seconds must be 0 (until removed) or more";
        return null;
    }
}

/// <summary>Which of a target's renderers the outline draws: only those still drawn by the game (alive, enabled), at most a fixed
/// number. A limb shot off takes its renderers out of this set the next time the set is read from the game.</summary>
internal static class OutlineDrawSet
{
    /// <summary>Fills <paramref name="into"/> (cleared first) with the candidates <paramref name="drawn"/> accepts, in order, at most
    /// <paramref name="max"/>; returns how many were skipped because they are no longer drawn.</summary>
    public static int Collect<T>(IEnumerable<T> candidates, Func<T, bool> drawn, int max, List<T> into)
    {
        into.Clear();
        int skipped = 0;
        foreach (var candidate in candidates)
        {
            if (!drawn(candidate)) { skipped++; continue; }
            if (into.Count < max) into.Add(candidate);
        }
        return skipped;
    }
}

/// <summary>
/// The outlines added to single objects, without Unity: each is a claim on a target (a key naming the object) with a priority and an
/// end time. Several claims may name one target; exactly one is drawn: the highest priority, the latest added among equals. A slot's
/// generation changes on every reuse, so an old handle never removes a newer claim. Nothing allocates once the arrays have grown.
/// </summary>
internal sealed class OutlineClaims
{
    private long[] _key = new long[16], _stamp = new long[16];
    private int[] _priority = new int[16], _generation = new int[16];
    private float[] _until = new float[16];
    private bool[] _used = new bool[16];
    private int _high, _live;
    private long _clock;

    /// <summary>One past the highest slot ever used; iterate <c>0..Capacity</c> and skip slots that are not <see cref="Live"/>.</summary>
    public int Capacity => _high;
    /// <summary>Claims held now.</summary>
    public int Count => _live;

    public bool Live(int slot) => slot >= 0 && slot < _high && _used[slot];
    public long Key(int slot) => _key[slot];
    public int Priority(int slot) => _priority[slot];
    public int Generation(int slot) => slot >= 0 && slot < _high ? _generation[slot] : 0;

    /// <summary>Adds a claim on <paramref name="key"/> lasting until <paramref name="until"/> (see <see cref="OutlineRules.Until"/>);
    /// returns its slot.</summary>
    public int Add(long key, int priority, float until)
    {
        int slot = -1;
        for (int i = 0; i < _high; i++) if (!_used[i]) { slot = i; break; }
        if (slot < 0)
        {
            if (_high == _used.Length) Grow();
            slot = _high++;
        }
        _used[slot] = true;
        _key[slot] = key;
        _priority[slot] = priority;
        _until[slot] = until;
        _stamp[slot] = ++_clock;
        _live++;
        return slot;
    }

    /// <summary>Removes the claim in <paramref name="slot"/> if it still is the one of <paramref name="generation"/>.</summary>
    public bool Remove(int slot, int generation)
    {
        if (!Live(slot) || _generation[slot] != generation) return false;
        Free(slot);
        return true;
    }

    /// <summary>Ends every claim whose time is up; returns whether any ended.</summary>
    public bool Expire(float now)
    {
        bool any = false;
        for (int i = 0; i < _high; i++)
            if (_used[i] && now >= _until[i]) { Free(i); any = true; }
        return any;
    }

    /// <summary>The claim drawn on <paramref name="key"/>: the highest priority, the latest among equals; -1 when none.</summary>
    public int Winner(long key)
    {
        int best = -1;
        for (int i = 0; i < _high; i++)
        {
            if (!_used[i] || _key[i] != key) continue;
            if (best < 0 || _priority[i] > _priority[best] || (_priority[i] == _priority[best] && _stamp[i] > _stamp[best])) best = i;
        }
        return best;
    }

    /// <summary>Whether a claim on <paramref name="key"/> replaces a claim of <paramref name="priority"/> older than every added one
    /// (the automatic enemy outline): true when the winner's priority is at least as high.</summary>
    public bool Overrides(long key, int priority)
    {
        int winner = Winner(key);
        return winner >= 0 && _priority[winner] >= priority;
    }

    public void Clear()
    {
        for (int i = 0; i < _high; i++) if (_used[i]) Free(i);
    }

    private void Free(int slot)
    {
        _used[slot] = false;
        _generation[slot]++;
        _live--;
    }

    private void Grow()
    {
        int size = _used.Length * 2;
        Array.Resize(ref _key, size); Array.Resize(ref _stamp, size); Array.Resize(ref _priority, size);
        Array.Resize(ref _generation, size); Array.Resize(ref _until, size); Array.Resize(ref _used, size);
    }
}

/// <summary>
/// What the outline knows of each of ForgeCore's enemy slots, in plain arrays, and the selection of the enemies to outline. The slots
/// and their generations are ForgeCore's (<c>Enemies.Live</c>, <c>Enemies.Generation</c>); a slot's generation changes on every reuse
/// so an old selection never matches a new enemy. Nothing here allocates once the arrays have grown to the number of slots seen.
/// </summary>
public sealed class EnemyOutlineTracker
{
    private bool[] _used = new bool[64];
    private int[] _generation = new int[64];
    private float[] _x = new float[64], _y = new float[64], _z = new float[64];
    private EnemyAlert[] _state = new EnemyAlert[64];
    private int _high, _cursor;

    // The current selection, sorted by slot, and a scratch area for the next one.
    private int[] _selSlot = new int[EnemyOutlineStyle.MaxEnemiesLimit], _selGen = new int[EnemyOutlineStyle.MaxEnemiesLimit];
    private EnemyAlert[] _selState = new EnemyAlert[EnemyOutlineStyle.MaxEnemiesLimit];
    private readonly int[] _newSlot = new int[EnemyOutlineStyle.MaxEnemiesLimit];
    private readonly float[] _newDist = new float[EnemyOutlineStyle.MaxEnemiesLimit];
    private int[] _nextSlot = new int[EnemyOutlineStyle.MaxEnemiesLimit], _nextGen = new int[EnemyOutlineStyle.MaxEnemiesLimit];
    private EnemyAlert[] _nextState = new EnemyAlert[EnemyOutlineStyle.MaxEnemiesLimit];

    /// <summary>One past the highest slot ever set; iterate <c>0..Capacity</c> and skip unused slots.</summary>
    public int Capacity => _high;
    /// <summary>Enemies in the current selection.</summary>
    public int SelectedCount { get; private set; }

    public bool Used(int slot) => slot >= 0 && slot < _high && _used[slot];
    public int Generation(int slot) => Used(slot) ? _generation[slot] : 0;
    public EnemyAlert State(int slot) => Used(slot) ? _state[slot] : EnemyAlert.Off;

    /// <summary>The slot to read next, going round <c>0..slotCount</c> so every enemy is read in turn without reading them all every
    /// frame; -1 when there are no slots.</summary>
    public int NextSlot(int slotCount)
    {
        if (slotCount <= 0) return -1;
        if (_cursor >= slotCount) _cursor = 0;
        return _cursor++;
    }

    /// <summary>Records what was read for <paramref name="slot"/>, holding the enemy of the given generation. A slot not yet set is
    /// not outlined.</summary>
    public void Set(int slot, int generation, float x, float y, float z, EnemyAlert state)
    {
        if (slot < 0) return;
        if (slot >= _used.Length) Grow(slot + 1);
        if (slot >= _high) _high = slot + 1;
        _used[slot] = true;
        _generation[slot] = generation;
        _x[slot] = x; _y[slot] = y; _z[slot] = z; _state[slot] = state;
    }

    /// <summary>Forgets a slot (its enemy left). Returns whether it was in the current selection: the caller must then stop drawing it at once.</summary>
    public bool Release(int slot)
    {
        if (!Used(slot)) return false;
        _used[slot] = false;
        _state[slot] = EnemyAlert.Off;
        return IsSelected(slot);
    }

    public bool IsSelected(int slot)
    {
        for (int i = 0; i < SelectedCount; i++) if (_selSlot[i] == slot) return true;
        return false;
    }

    /// <summary>The slot and state of selected enemy <paramref name="index"/> (0..<see cref="SelectedCount"/>), in slot order.</summary>
    public int SelectedSlot(int index) => _selSlot[index];
    public EnemyAlert SelectedState(int index) => _selState[index];

    /// <summary>Forgets the current selection (for example when drawing stops), so the next <see cref="Select"/> reports a change.</summary>
    public void ClearSelection() => SelectedCount = 0;

    /// <summary>
    /// Picks the nearest enemies within the style's range that are in view of a camera at (<paramref name="cx"/>, <paramref name="cy"/>,
    /// <paramref name="cz"/>) looking along the unit vector (<paramref name="fx"/>, <paramref name="fy"/>, <paramref name="fz"/>), at
    /// most <see cref="EnemyOutlineStyle.MaxEnemies"/>, skipping those with no state. Returns whether the selection (which enemies or
    /// their states) differs from the last one.
    /// </summary>
    public bool Select(EnemyOutlineStyle style, float cx, float cy, float cz, float fx, float fy, float fz)
    {
        int max = Math.Clamp(style.MaxEnemies, 1, EnemyOutlineStyle.MaxEnemiesLimit);
        float range2 = style.Range * style.Range, near2 = OutlineRules.NearAlways * OutlineRules.NearAlways;
        int n = 0;
        for (int slot = 0; slot < _high; slot++)
        {
            if (!_used[slot] || _state[slot] == EnemyAlert.Off) continue;
            float dx = _x[slot] - cx, dy = _y[slot] - cy, dz = _z[slot] - cz;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > range2) continue;
            if (d2 > near2 && dx * fx + dy * fy + dz * fz < OutlineRules.ViewCosine * MathF.Sqrt(d2)) continue;
            // Insertion into the nearest-first list of at most max entries.
            if (n == max && d2 >= _newDist[n - 1]) continue;
            int at = n < max ? n++ : n - 1;
            while (at > 0 && _newDist[at - 1] > d2) { _newDist[at] = _newDist[at - 1]; _newSlot[at] = _newSlot[at - 1]; at--; }
            _newDist[at] = d2; _newSlot[at] = slot;
        }
        // Slot order makes the comparison with the last selection independent of distance order.
        for (int i = 0; i < n; i++)
        {
            int slot = _newSlot[i], at = i;
            while (at > 0 && _nextSlot[at - 1] > slot) { _nextSlot[at] = _nextSlot[at - 1]; at--; }
            _nextSlot[at] = slot;
        }
        bool changed = n != SelectedCount;
        for (int i = 0; i < n; i++)
        {
            int slot = _nextSlot[i];
            _nextGen[i] = _generation[slot];
            _nextState[i] = _state[slot];
            if (!changed && (_selSlot[i] != slot || _selGen[i] != _nextGen[i] || _selState[i] != _nextState[i])) changed = true;
        }
        if (!changed) return false;
        (_selSlot, _nextSlot) = (_nextSlot, _selSlot);
        (_selGen, _nextGen) = (_nextGen, _selGen);
        (_selState, _nextState) = (_nextState, _selState);
        SelectedCount = n;
        return true;
    }

    private void Grow(int needed)
    {
        int size = _used.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _used, size);
        Array.Resize(ref _generation, size);
        Array.Resize(ref _x, size); Array.Resize(ref _y, size); Array.Resize(ref _z, size);
        Array.Resize(ref _state, size);
    }
}

/// <summary>
/// The automatic enemy outlines every mod switched on. Outlines are drawn while any is on; the style is that of the one switched on
/// most recently.
/// </summary>
internal sealed class OutlineScopes<T> where T : class
{
    private readonly List<(T Scope, long Stamp)> _on = new();
    private long _clock;

    /// <summary>The scope whose style is drawn, or null when none is on.</summary>
    public T? Current { get; private set; }

    /// <summary>Switches <paramref name="scope"/> on or off. Returns whether <see cref="Current"/> changed.</summary>
    public bool Set(T scope, bool on)
    {
        int index = -1;
        for (int i = 0; i < _on.Count; i++) if (ReferenceEquals(_on[i].Scope, scope)) { index = i; break; }
        if (on && index < 0) _on.Add((scope, ++_clock));
        else if (!on && index >= 0) _on.RemoveAt(index);
        else return false;
        var before = Current;
        Current = null;
        long best = long.MinValue;
        foreach (var (s, stamp) in _on) if (stamp > best) { best = stamp; Current = s; }
        return !ReferenceEquals(before, Current);
    }

    public bool IsOn(T scope)
    {
        foreach (var (s, _) in _on) if (ReferenceEquals(s, scope)) return true;
        return false;
    }
}
