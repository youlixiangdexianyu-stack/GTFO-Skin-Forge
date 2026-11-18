using System;

namespace ForgeCore;

/// <summary>The first entry of a roster whose owner has a given lookup. The roster itself (the game's agent list) is the caller's;
/// this only fixes the rule: a lookup of 0 names nobody, and an entry that is gone or has no owner reads as 0 and never matches.
/// Allocation-free when <c>lookupAt</c> is a static lambda.</summary>
internal static class RosterSearch
{
    /// <summary>The index of the first of <paramref name="count"/> entries whose <paramref name="lookupAt"/> equals
    /// <paramref name="wanted"/>, or -1.</summary>
    internal static int IndexOf<TState>(int count, TState state, Func<TState, int, ulong> lookupAt, ulong wanted)
    {
        if (wanted == 0) return -1;
        for (var i = 0; i < count; i++)
            if (lookupAt(state, i) == wanted) return i;
        return -1;
    }
}
