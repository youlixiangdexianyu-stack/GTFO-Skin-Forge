using Player;

namespace ForgeCore;

public static partial class Level
{
    /// <summary>The player agent of the game's in-level list (<c>PlayerManager.PlayerAgentsInLevel</c>) whose network player has
    /// <paramref name="lookup"/>, or null when there is none (no level, the player left, an agent already collected). A lookup of 0 names
    /// nobody. Main thread only.</summary>
    public static PlayerAgent? PlayerByLookup(ulong lookup) => PlayerByLookup(lookup, out _);

    /// <summary>As <see cref="PlayerByLookup(ulong)"/>, also giving the agent's position in the game's list (-1 when there is none), so
    /// a caller that keeps the agent can check it again at that position with <see cref="IsPlayer"/> instead of scanning.</summary>
    public static PlayerAgent? PlayerByLookup(ulong lookup, out int index)
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        index = agents == null ? -1 : RosterSearch.IndexOf(agents.Count, agents, static (list, i) => LookupOf(list[i]), lookup);
        return index < 0 ? null : agents![index];
    }

    /// <summary>True when <paramref name="agent"/> is a live agent whose network player has <paramref name="lookup"/> (never for 0).</summary>
    public static bool IsPlayer(PlayerAgent? agent, ulong lookup) => lookup != 0 && LookupOf(agent) == lookup;

    private static ulong LookupOf(PlayerAgent? agent)
        => agent == null || agent.WasCollected || agent.Owner == null ? 0UL : agent.Owner.Lookup;
}
