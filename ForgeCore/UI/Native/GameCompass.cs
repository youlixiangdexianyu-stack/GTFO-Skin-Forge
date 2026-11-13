using System;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>The points the game's compass shows (the map's locator beacon, a terminal's PING result and the
    /// teammates), then the world points mods placed for the bearing ring (<see cref="HudWaypoints"/>; see <see cref="GameCompass"/>).</summary>
    public static GameCompass Compass { get; } = new();
}

/// <summary>
/// The points the game's own compass (the strip at the top of the screen) shows, read from the same synced state the compass draws:
/// the locator beacon set from the map (<c>m_locatorBeacon</c>), where a terminal's PING command found its item
/// (<c>m_terminalPing</c>), and the other players in the level in their colours. The players' middle-mouse pings are not among them:
/// the game draws those itself in the world. Each point has its bearing from the view, its distance
/// from the eye, its <see cref="CompassPointKind"/> and its colour. The game's objects are looked up
/// once a second; every frame only their synced state and positions are read (on first access), into fixed arrays: no allocation.
/// </summary>
public sealed class GameCompass
{
    private const int Slots = 4, ModPoints = 32, Capacity = 2 + Slots + ModPoints;
    private const float LookupEvery = 1f;

    private readonly CompassReading _reading = new(Capacity);
    private SNet_StateReplicator<pNavMarkerState, pNavMarkerInteraction>? _locator, _terminal;
    private readonly PlayerAgent?[] _mates = new PlayerAgent?[Slots];
    private readonly int[] _mateSlot = new int[Slots];
    private readonly Color[] _mateColour = new Color[Slots];
    private int _mateCount;
    private Transform? _eye;
    private int _frame = -1;
    private float _lookupAt = float.NegativeInfinity;
    private bool _failing;

    internal GameCompass() { }

    /// <summary>The points shown now.</summary>
    public int Count { get { Read(); return _reading.Count; } }

    public CompassPoint Get(int index)
    {
        Read();
        return _reading.Get(index);
    }

    private void Read()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _reading.Begin();
        try
        {
            var now = ForgeCore.Frame.UnscaledTime;
            if (now >= _lookupAt || _eye == null || _eye.WasCollected)
            {
                _lookupAt = now + LookupEvery;
                Lookup();
            }
            if (_eye == null || _eye.WasCollected) return;
            Beacon(_locator, CompassPointKind.Locator);
            Beacon(_terminal, CompassPointKind.TerminalPing);
            for (var i = 0; i < _mateCount; i++)
            {
                var mate = _mates[i];
                if (mate == null || mate.WasCollected) continue;
                var at = mate.Position;
                var c = _mateColour[i];
                _reading.Add(at.x, at.y, at.z, CompassPointKind.Teammate, _mateSlot[i], true, c.r, c.g, c.b);
            }
            HudWaypoints.AddToCompass(_reading);
            var eye = _eye.position;
            var forward = _eye.forward;
            _reading.Locate(eye.x, eye.y, eye.z, forward.x, forward.z);
            _failing = false;
        }
        catch (Exception error)
        {
            _reading.Begin();
            if (!_failing) Plugin.Error("ui-reading-games-compass-points-failed", "Reading the game's compass points failed (no points until it reads again): " + error);
            _failing = true;
        }
    }

    private void Beacon(SNet_StateReplicator<pNavMarkerState, pNavMarkerInteraction>? beacon, CompassPointKind kind)
    {
        if (beacon == null || beacon.WasCollected) return;
        var state = beacon.State;
        if (state.status != eNavMarkerStatus.Visible) return;
        var at = state.worldPos;
        _reading.Add(at.x, at.y, at.z, kind, -1, false, 1f, 1f, 1f);
    }

    /// <summary>The game's objects behind the points, once a second: the beacon replicators, the other players with their colours and
    /// slots, and the camera the view is seen through.</summary>
    private void Lookup()
    {
        _mateCount = 0;
        _locator = _terminal = null;
        var camera = HudScreen.WorldCamera();
        _eye = camera != null ? camera.transform : null;
        var gui = GuiManager.Current;
        if (gui != null && !gui.WasCollected)
        {
            _locator = Replicator(gui.m_locatorBeacon);
            _terminal = Replicator(gui.m_terminalPing);
        }
        ForgeCore.Players.Each(this, static (self, info) => self.AddMate(info));
        for (var i = _mateCount; i < Slots; i++) _mates[i] = null;
    }

    private void AddMate(ForgeCore.PlayerInfo info)
    {
        if (_mateCount >= Slots || info.IsLocal) return;
        var owner = info.Agent.Owner;
        _mates[_mateCount] = info.Agent;
        _mateSlot[_mateCount] = info.Slot;
        _mateColour[_mateCount] = owner != null ? owner.PlayerColor : Color.white;
        _mateCount++;
    }

    private static SNet_StateReplicator<pNavMarkerState, pNavMarkerInteraction>? Replicator(SyncedNavMarkerWrapper? wrapper)
        => wrapper == null || wrapper.WasCollected ? null : wrapper.m_stateReplicator;
}
