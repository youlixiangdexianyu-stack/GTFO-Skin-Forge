using Player;
using UnityEngine;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>True while, in a level, the game shows one of its own full screens (map, terminal, in-game menu): the player is away from first-person play.</summary>
    public static bool GameScreenOpen => HudFocus.Away;

    /// <summary>The game's own player marker (the one the ping key places, synced by the game) at <paramref name="at"/>.</summary>
    public static void Ping(PlayerAgent player, Vector3 at, eNavMarkerStyle style = eNavMarkerStyle.PlayerPingLookat)
        => GuiManager.AttemptSetPlayerPingStatus(player, true, at, style);

    /// <summary>Takes <paramref name="player"/>'s marker away.</summary>
    public static void ClearPing(PlayerAgent player)
        => GuiManager.AttemptSetPlayerPingStatus(player, false, Vector3.zero, eNavMarkerStyle.PlayerPingLookat);

    /// <summary>Where the local player's crosshair ray lands; false when it hits nothing.</summary>
    public static bool AimPoint(out Vector3 at)
    {
        at = default;
        var agent = ForgeCore.Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected || camera.CameraRayObject == null) return false;
        at = camera.CameraRayPos;
        return true;
    }
}
