using System;

namespace ForgeUI;

/// <summary>
/// Which pieces of a drawing pool are switched on, kept here so that a frame never asks the engine: it only reads the switch the pool
/// last wrote. Pieces from <see cref="Lit"/> on are all off, so hiding the unused ones looks only at the pieces that can be on.
/// </summary>
internal sealed class PieceActivity
{
    private bool[] _on = new bool[16];
    private int _count;

    /// <summary>One past the last piece that may be on.</summary>
    internal int Lit { get; private set; }

    /// <summary>A piece was made at the end of the pool; <paramref name="on"/> is the state it was made in.</summary>
    internal void Added(bool on)
    {
        if (_count == _on.Length) Array.Resize(ref _on, _count * 2);
        _on[_count++] = on;
        if (on) Lit = _count;
    }

    /// <summary>Piece <paramref name="index"/> is needed: true when it was off and must be switched on now.</summary>
    internal bool Wake(int index)
    {
        if (index >= Lit) Lit = index + 1;
        if (_on[index]) return false;
        _on[index] = true;
        return true;
    }

    /// <summary>Piece <paramref name="index"/> is no longer needed: true when it was on and must be switched off now.</summary>
    internal bool Sleep(int index)
    {
        if (!_on[index]) return false;
        _on[index] = false;
        return true;
    }

    /// <summary>The pieces from <paramref name="used"/> on have been put away.</summary>
    internal void Settled(int used)
    {
        if (Lit > used) Lit = used;
    }

    /// <summary>The pool was emptied.</summary>
    internal void Clear()
    {
        _count = 0;
        Lit = 0;
    }
}
