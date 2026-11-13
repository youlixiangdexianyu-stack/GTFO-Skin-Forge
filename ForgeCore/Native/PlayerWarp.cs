using System;
using Player;
using UnityEngine;

namespace ForgeCore;

public static partial class Players
{
    /// <summary>The distance within which an agent is read back as having landed: a warp resolves colliders on the way in, so an exact
    /// match is not what the game promises.</summary>
    public const float WarpTolerance = 0.5f;

    /// <summary>Moves <paramref name="agent"/> through the game's own warp entry and reads the agent's position back. The caller
    /// brings a landing the game's sampler accepted (<c>PlayerAgent.SampleWarpPosition</c>). <see cref="WarpMode.Synced"/>: the host
    /// calls <c>RequestWarpToSync</c>, whose session half sends the move to every other machine, for any live agent in one of its
    /// <c>m_warpableStates</c>; no screen effect, sound or bot is taken along. <see cref="WarpMode.Local"/>: this machine's own agent is put
    /// at <paramref name="position"/> with <c>TeleportTo</c> (no gate on life or state, the dimension must be the one it stands in) and its
    /// camera turned to <paramref name="forward"/>. A side or state the entry cannot serve is refused by name before the game is
    /// called; see <see cref="WarpGate"/>. Main thread only.</summary>
    public static WarpResult Warp(PlayerAgent? agent, eDimensionIndex dimension, Vector3 position, Vector3 forward, WarpMode mode)
    {
        var found = agent != null && !agent.WasCollected;
        bool alive = false, warpable = false, local = false, same = false;
        if (found)
        {
            try
            {
                alive = agent!.Alive;
                local = agent.IsLocallyOwned;
                same = agent.DimensionIndex == dimension;
                var locomotion = agent.Locomotion;
                var states = agent.m_warpableStates;
                warpable = locomotion != null && states != null && states.Contains(locomotion.m_currentStateEnum);
            }
            catch (Exception error) { return Faulted(error); }
        }
        var refusal = WarpGate.Check(mode, found, Session.IsHost, local, alive, warpable, same);
        if (refusal != WarpRefusal.None) return new WarpResult(false, false, float.NaN, refusal);
        try
        {
            if (mode == WarpMode.Synced) agent!.RequestWarpToSync(dimension, position, forward, PlayerAgent.WarpOptions.None);
            else
            {
                agent!.TeleportTo(position);
                var camera = agent.FPSCamera;
                if (camera != null && !camera.WasCollected && forward != Vector3.zero) camera.SetLookDirection(forward);
            }
        }
        catch (Exception error) { return Faulted(error); }
        try
        {
            if (agent.WasCollected) return new WarpResult(true, false, float.NaN, WarpRefusal.None);
            // The live body, not Agent.Position: that one is the cached sample the game refreshes on its own update,
            // so right after the warp it still reports the point the agent left (the same stale sample the landing
            // observation documents) and a landed warp would read as not observed.
            var body = agent.transform;
            var distance = body == null ? float.NaN : Vector3.Distance(body.position, position);
            return new WarpResult(true, distance <= WarpTolerance, float.IsFinite(distance) ? distance : float.NaN, WarpRefusal.None);
        }
        catch (Exception error)
        {
            Log.For("ForgeCore").ErrorOnce("core-reading-players-position-after-warp-failed", "Reading a player's position after a warp failed: " + error);
            return new WarpResult(true, false, float.NaN, WarpRefusal.None);
        }
    }

    private static WarpResult Faulted(Exception error)
    {
        Log.For("ForgeCore").ErrorOnce("core-player-warp-failed", "A player warp failed: " + error);
        return new WarpResult(false, false, float.NaN, WarpRefusal.Faulted);
    }
}
