using System.Collections.Generic;

namespace ForgeUI;

/// <summary>
/// The switches every mod made for one shared feature (enemy health bars, hit feedback). The feature is drawn while any switch is on; the
/// style is that of the switch turned on most recently.
/// </summary>
internal sealed class ScopeSwitches<T> where T : class
{
    private readonly List<(T Scope, long Stamp)> _on = new();
    private long _clock;

    /// <summary>The switch whose style is drawn, or null when none is on.</summary>
    public T? Current { get; private set; }

    /// <summary>Turns <paramref name="scope"/> on or off. Returns whether <see cref="Current"/> changed.</summary>
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
