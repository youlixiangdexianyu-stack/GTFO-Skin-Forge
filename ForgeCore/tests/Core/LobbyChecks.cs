using ForgeUI;

namespace ForgeCore.Tests;

/// <summary>The core algorithm of the lobby button layout: two half-width buttons wanted under APPAREL share one row, left to right,
/// when the area holds a single row; a full-width button next to them takes a second row.</summary>
internal static class LobbyChecks
{
    public static void Run(Action<bool, string> check)
    {
        var button = new HudBox(-225f, -27.5f, 225f, 27.5f);
        // 1 row of room under APPAREL: 73 units under its bottom edge, none to spare.
        var floor = button.Bottom - LobbyLayout.Step - 1f;
        var spots = new LobbySpots[3];
        spots[0] = LobbyLayout.Below(button, floor, 1, 0);
        var half = LobbyLayout.Width(LobbyWidth.Half, button.Width);
        var area = new int[2]; var row = new int[2]; var offset = new float[2]; var used = new int[3];
        LobbyLayout.Assign(new[] { LobbyArea.Below, LobbyArea.Below }, new[] { half, half }, 2, spots, area, row, offset, used);
        check(spots[0].Rows == 1 && area[0] == 0 && area[1] == 0 && row[0] == 0 && row[1] == 0 && offset[0] == 0f && Math.Abs(offset[1] - (half + LobbyLayout.Gap)) < 1e-3f,
            "lobby layout: two half-width buttons share the one row under APPAREL, first left");
        var full = LobbyLayout.Width(LobbyWidth.Full, button.Width);
        LobbyLayout.Assign(new[] { LobbyArea.Below, LobbyArea.Below }, new[] { full, full }, 2, spots, area, row, offset, used);
        check(area[0] == 0 && area[1] < 0, "lobby layout: a second full-width button finds no room in a one-row area");
    }
}
