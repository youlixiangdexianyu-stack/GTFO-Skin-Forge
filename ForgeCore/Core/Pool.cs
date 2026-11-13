using System;

namespace ForgeCore;

/// <summary>A fixed-capacity pool. <see cref="Prewarm"/> makes items ahead of use; taking and returning allocate nothing. Main thread only.</summary>
public sealed class Pool<T> where T : class
{
    private readonly Func<T> _create;
    private readonly Action<T>? _taken, _returned;
    private readonly T[] _free;
    private int _freeCount;

    internal Pool(int capacity, Func<T> create, Action<T>? taken, Action<T>? returned)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "A pool holds at least one item.");
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _taken = taken;
        _returned = returned;
        _free = new T[capacity];
    }

    public int Capacity => _free.Length;

    /// <summary>Items made so far (taken plus free).</summary>
    public int Created { get; private set; }

    /// <summary>Items ready to take without making a new one.</summary>
    public int Free => _freeCount;

    /// <summary>Makes items until <paramref name="count"/> exist (never more than <see cref="Capacity"/>).</summary>
    public void Prewarm(int count)
    {
        count = Math.Min(count, Capacity);
        while (Created < count) _free[_freeCount++] = Make();
    }

    /// <summary>A free item, or a new one while fewer than <see cref="Capacity"/> exist; false when every item is taken.</summary>
    public bool TryTake(out T item)
    {
        if (_freeCount > 0)
        {
            item = _free[--_freeCount];
            _free[_freeCount] = null!;
        }
        else if (Created < Capacity) item = Make();
        else
        {
            item = null!;
            return false;
        }
        _taken?.Invoke(item);
        return true;
    }

    /// <summary>Gives a taken item back. Returning more items than were taken is an error, not a silent drop.</summary>
    public void Return(T item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (_freeCount == _free.Length) throw new InvalidOperationException("More items returned than were taken from this pool.");
        _returned?.Invoke(item);
        _free[_freeCount++] = item;
    }

    /// <summary>Forgets a taken item that is gone for good (destroyed by the game): its place can be made again.</summary>
    public void Discard(T item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (Created == _freeCount) throw new InvalidOperationException("More items discarded than were taken from this pool.");
        Created--;
    }

    /// <summary>Hands every free item to <paramref name="release"/> (to destroy it) and forgets it; items still taken stay counted.</summary>
    public void Drain(Action<T> release)
    {
        if (release == null) throw new ArgumentNullException(nameof(release));
        while (_freeCount > 0)
        {
            var item = _free[--_freeCount];
            _free[_freeCount] = null!;
            Created--;
            release(item);
        }
    }

    private T Make()
    {
        var item = _create() ?? throw new InvalidOperationException("The pool's factory returned nothing.");
        Created++;
        return item;
    }
}

/// <summary>Fixed-capacity object pools.</summary>
public static class Pool
{
    /// <summary>A pool of at most <paramref name="capacity"/> items made by <paramref name="create"/>; <paramref name="taken"/> and
    /// <paramref name="returned"/> run on each take and return (for example to show and hide a pooled object).</summary>
    public static Pool<T> Create<T>(int capacity, Func<T> create, Action<T>? taken = null, Action<T>? returned = null) where T : class
        => new(capacity, create, taken, returned);
}
