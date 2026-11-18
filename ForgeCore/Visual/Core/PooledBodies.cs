using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>The live and the parked items of a pool whose bodies Unity may destroy under it (a scene change). Releasing an item first
/// runs the caller's hide while the item is still counted live, so a hide that checks the item's own state still acts, and only then
/// parks it, and only when its body is still usable. Releasing twice, or something never added, changes nothing.</summary>
internal sealed class PooledBodies<T> where T : class
{
    private readonly List<T> _live = new();
    private readonly Stack<T> _free = new();

    internal int LiveCount => _live.Count;
    internal T this[int index] => _live[index];

    /// <summary>A parked item whose body is still usable (parked items with a destroyed body are dropped), or null.</summary>
    internal T? Reuse(Func<T, bool> usable)
    {
        while (_free.Count > 0)
        {
            var item = _free.Pop();
            if (usable(item)) return item;
        }
        return null;
    }

    internal void Add(T item) => _live.Add(item);

    internal void RemoveAt(int index) => _live.RemoveAt(index);

    /// <summary>Hides <paramref name="item"/> (still live while the hide runs), takes it off the live list and parks it when
    /// <paramref name="usable"/>. False when it was not live.</summary>
    internal bool Release(T item, Action<T> hide, bool usable)
    {
        if (!_live.Contains(item)) return false;
        hide(item);
        _live.Remove(item);
        if (usable) _free.Push(item);
        return true;
    }
}
