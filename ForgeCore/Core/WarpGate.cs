namespace ForgeCore;

/// <summary>How a warp request reaches the world: <see cref="Synced"/> is the host's entry that replicates the move to every machine
/// (<c>PlayerAgent.RequestWarpToSync</c>, any player), <see cref="Local"/> moves this machine's own player only
/// (<c>PlayerAgent.TeleportTo</c>, no replication).</summary>
public enum WarpMode { Synced, Local }

/// <summary>Why <see cref="Players.Warp"/> did not call the game. <see cref="Faulted"/> means the game's entry threw.</summary>
public enum WarpRefusal { None, NoAgent, NotHost, NotLocal, NotAlive, State, Dimension, Faulted }

/// <summary>What <see cref="Players.Warp"/> found. <see cref="Accepted"/>: no refusal and the game's entry returned. <see cref="Landed"/>:
/// the agent was then read within <see cref="Players.WarpTolerance"/> metres of the request. <see cref="Distance"/>: that read's
/// distance, NaN when nothing was read. A warp that was accepted but did not land is not a success: a remote player's position can
/// arrive later than the call, and a position the game found invalid leaves the player where it was.</summary>
public readonly record struct WarpResult(bool Accepted, bool Landed, float Distance, WarpRefusal Refusal);

/// <summary>The side and state rules of a warp, on plain facts so they can be tested.</summary>
internal static class WarpGate
{
    /// <summary>The refusal for a request, <see cref="WarpRefusal.None"/> when the game may be called. Synced needs the host, a live
    /// agent in a state the game warps in; Local needs the local player's own agent in the dimension it is asked to land in.</summary>
    public static WarpRefusal Check(WarpMode mode, bool hasAgent, bool isHost, bool isLocalAgent, bool alive, bool stateWarpable, bool sameDimension)
    {
        if (!hasAgent) return WarpRefusal.NoAgent;
        if (mode == WarpMode.Local)
            return !isLocalAgent ? WarpRefusal.NotLocal : !sameDimension ? WarpRefusal.Dimension : WarpRefusal.None;
        if (!isHost) return WarpRefusal.NotHost;
        if (!alive) return WarpRefusal.NotAlive;
        return stateWarpable ? WarpRefusal.None : WarpRefusal.State;
    }
}
