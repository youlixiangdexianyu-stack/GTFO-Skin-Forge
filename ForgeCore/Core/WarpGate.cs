namespace ForgeCore;

/// <summary>How a warp request reaches the world: <see cref="Synced"/> is the game's replicated warp
/// (<c>PlayerAgent.RequestWarpToSync</c>), which the machine that simulates the player applies at once and which the host may also
/// send for a player another machine simulates; <see cref="Local"/> moves this machine's own player only
/// (<c>PlayerAgent.TeleportTo</c>, no replication).</summary>
public enum WarpMode { Synced, Local }

/// <summary>Why <see cref="Players.Warp"/> did not call the game. <see cref="Faulted"/> means the game's entry threw.</summary>
public enum WarpRefusal { None, NoAgent, NotHost, NotLocal, NotAlive, State, Dimension, Faulted }

/// <summary>What <see cref="Players.Warp"/> found. <see cref="Accepted"/>: no refusal and the game's entry returned. <see cref="Landed"/>:
/// the agent was then read within <see cref="Players.WarpTolerance"/> metres of the request. <see cref="Distance"/>: that read's
/// distance, NaN when nothing was read. A warp that was accepted but did not land is not a success: a position the game found
/// invalid leaves the player where it was, and a player another machine simulates is moved there, after this call, so the host
/// that sent the request reads nothing back for it.</summary>
public readonly record struct WarpResult(bool Accepted, bool Landed, float Distance, WarpRefusal Refusal);

/// <summary>The side and state rules of a warp, on plain facts so they can be tested.</summary>
internal static class WarpGate
{
    /// <summary>The refusal for a request, <see cref="WarpRefusal.None"/> when the game may be called. Synced needs a live agent in
    /// a state the game warps in, on the machine that simulates it or on the host (<see cref="WarpRefusal.NotHost"/>: a client was
    /// asked to move a player it does not simulate); Local needs the local player's own agent in the dimension it is asked to land in.</summary>
    public static WarpRefusal Check(WarpMode mode, bool hasAgent, bool isHost, bool isLocalAgent, bool simulatedHere, bool alive, bool stateWarpable, bool sameDimension)
    {
        if (!hasAgent) return WarpRefusal.NoAgent;
        if (mode == WarpMode.Local)
            return !isLocalAgent ? WarpRefusal.NotLocal : !sameDimension ? WarpRefusal.Dimension : WarpRefusal.None;
        if (!isHost && !simulatedHere) return WarpRefusal.NotHost;
        if (!alive) return WarpRefusal.NotAlive;
        return stateWarpable ? WarpRefusal.None : WarpRefusal.State;
    }
}
