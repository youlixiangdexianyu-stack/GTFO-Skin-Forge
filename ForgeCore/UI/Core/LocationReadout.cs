using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeUI;

/// <summary>
/// The player's position readouts, from real data only: the zone the player stands in (as the game's map names it, "ZONE 49"), the
/// view's horizontal heading (0-359 degrees with the nearest of N/E/S/W) and the altitude (world Y, whole metres, "-212 M"). A value
/// the game does not give (no zone, no camera) reads empty and the line is not drawn. Texts are made once per distinct value and kept,
/// so reading them every frame allocates nothing after the first time each value shows.
/// </summary>
internal sealed class LocationReadout
{
    private static readonly string[] Headings = MakeHeadings();
    private readonly Dictionary<int, string> _zones = new(), _altitudes = new();
    private int _zone = int.MinValue, _heading = -1, _altitude = int.MinValue;

    public string Zone { get; private set; } = "";
    public string Heading { get; private set; } = "";
    public string Altitude { get; private set; } = "";

    /// <summary>Bumped whenever a shown text changes.</summary>
    public int Version { get; private set; }

    /// <summary>The zone number from the player's course node; null when the player has no node (the line hides).</summary>
    public void SetZone(int? number)
    {
        var n = number ?? int.MinValue;
        if (n == _zone) return;
        _zone = n;
        Zone = number is { } z ? Cached(_zones, z, v => "ZONE " + v.ToString(CultureInfo.InvariantCulture)) : "";
        Version++;
    }

    /// <summary>The view's horizontal forward (world X and Z; +Z is north). A forward with no horizontal part (looking straight up or
    /// down) keeps the last heading; NaN hides the line.</summary>
    public void SetHeading(float forwardX, float forwardZ)
    {
        int h;
        if (float.IsNaN(forwardX) || float.IsNaN(forwardZ)) h = -1;
        else if (forwardX * forwardX + forwardZ * forwardZ < 1e-8f) return;
        else h = HeadingDegrees(forwardX, forwardZ);
        if (h == _heading) return;
        _heading = h;
        Heading = h < 0 ? "" : Headings[h];
        Version++;
    }

    /// <summary>The player's world Y; NaN hides the line.</summary>
    public void SetAltitude(float worldY)
    {
        var a = float.IsNaN(worldY) ? int.MinValue : (int)MathF.Round(worldY);
        if (a == _altitude) return;
        _altitude = a;
        Altitude = a == int.MinValue ? "" : Cached(_altitudes, a, v => v.ToString(CultureInfo.InvariantCulture) + " M");
        Version++;
    }

    /// <summary>Forgets every value (leaving the level), so the next reading shows afresh.</summary>
    public void Clear()
    {
        SetZone(null);
        _heading = 0;
        SetHeading(float.NaN, float.NaN);
        SetAltitude(float.NaN);
    }

    /// <summary>0..359, clockwise from +Z (north) towards +X (east).</summary>
    internal static int HeadingDegrees(float forwardX, float forwardZ)
    {
        var d = (int)MathF.Round(MathF.Atan2(forwardX, forwardZ) * (180f / MathF.PI));
        return ((d % 360) + 360) % 360;
    }

    /// <summary>The nearest of N, E, S, W.</summary>
    internal static char Cardinal(int degrees) => "NESW"[((degrees + 45) / 90) % 4];

    private static string[] MakeHeadings()
    {
        var all = new string[360];
        for (var d = 0; d < 360; d++) all[d] = d.ToString("000", CultureInfo.InvariantCulture) + "° " + Cardinal(d);
        return all;
    }

    private static string Cached(Dictionary<int, string> cache, int value, Func<int, string> make)
    {
        if (!cache.TryGetValue(value, out var text)) cache[value] = text = make(value);
        return text;
    }
}
