using System;

namespace ForgeUI;

/// <summary>What a point the game's compass shows stands for (see <see cref="UIKit.Compass"/>): the map's locator beacon, a terminal's PING result,
/// a teammate, or a world point a mod placed (whose kind picks its glyph).</summary>
public enum CompassPointKind : byte
{
    /// <summary>A spot with nothing special in it: the plain diamond.</summary>
    Here,
    Enemy,
    Ammo,
    Health,
    /// <summary>Tool refill.</summary>
    Tool,
    /// <summary>A consumable or a disinfection station.</summary>
    Supply,
    /// <summary>Loot, a carried item or an objective item to pick up.</summary>
    Item,
    /// <summary>A resource box or locker.</summary>
    Container,
    Terminal,
    /// <summary>Any door: plain, security, bulkhead, apex, blood, their checkpoint doors and the bulkhead door controller.</summary>
    Door,
    Generator,
    Caution,
    Sign,
    /// <summary>A hydro-stasis unit.</summary>
    Hsu,
    /// <summary>The locator beacon set from the map.</summary>
    Locator,
    /// <summary>Where a terminal's PING command found its item.</summary>
    TerminalPing,
    Teammate,
}

/// <summary>One point of <see cref="UIKit.Compass"/>: where it is from the view (<see cref="Azimuth"/> 0 ahead, 90 right, ±180 behind;
/// NaN when it cannot be told), how far (<see cref="Distance"/>, metres from the eye), what it is, its colour (the player's colour for
/// a teammate, the mod's colour for a point a mod placed, white otherwise; <see cref="PlayerColour"/> and <see cref="Mod"/> say which).</summary>
public readonly struct CompassPoint
{
    public CompassPoint(CompassPointKind kind, float azimuth, float distance, int slot, bool playerColour, float r, float g, float b, bool mod = false)
    {
        Kind = kind; Azimuth = azimuth; Distance = distance; Slot = slot; PlayerColour = playerColour; R = r; G = g; B = b; Mod = mod;
    }

    public CompassPointKind Kind { get; }
    public float Azimuth { get; }
    public float Distance { get; }
    /// <summary>The player slot of a teammate; −1 for a beacon or a mod's point.</summary>
    public int Slot { get; }
    /// <summary>The colour is the player's (a teammate).</summary>
    public bool PlayerColour { get; }
    /// <summary>A point a mod placed (a <c>HudWaypoints</c> point that asks for the bearing ring); <see cref="R"/>, <see cref="G"/>, <see cref="B"/> are its colour.</summary>
    public bool Mod { get; }
    public float R { get; }
    public float G { get; }
    public float B { get; }
}

/// <summary>The game's compass points as rules without Unity: the line glyph each kind is drawn with.</summary>
internal static class CompassPoints
{
    /// <summary>Whether a point of <paramref name="kind"/> is drawn when teammates are (not) included.</summary>
    public static bool Shown(CompassPointKind kind, bool teammates) => teammates || kind != CompassPointKind.Teammate;

    /// <summary>The number of kinds.</summary>
    public const int Kinds = (int)CompassPointKind.Teammate + 1;

    /// <summary>The line glyph of <paramref name="kind"/>: segments x0, y0, x1, y1 in a box from −1 to 1 (y up). Every kind has its own.</summary>
    public static ReadOnlySpan<float> Glyph(CompassPointKind kind) => (uint)kind < (uint)Glyphs.Length ? Glyphs[(int)kind] : Glyphs[0];

    private static readonly float[][] Glyphs =
    {
        // Here: a diamond.
        new[] { 0f, 1f, 1f, 0f, 1f, 0f, 0f, -1f, 0f, -1f, -1f, 0f, -1f, 0f, 0f, 1f },
        // Enemy: a cross.
        new[] { -1f, -1f, 1f, 1f, -1f, 1f, 1f, -1f },
        // Ammo: three rounds.
        new[] { -0.6f, -1f, -0.6f, 0.7f, 0f, -1f, 0f, 1f, 0.6f, -1f, 0.6f, 0.7f },
        // Health: a plus.
        new[] { 0f, -1f, 0f, 1f, -1f, 0f, 1f, 0f },
        // Tool: a T.
        new[] { -1f, 1f, 1f, 1f, 0f, 1f, 0f, -1f },
        // Supply: a hexagon.
        new[] { 0f, 1f, 0.87f, 0.5f, 0.87f, 0.5f, 0.87f, -0.5f, 0.87f, -0.5f, 0f, -1f, 0f, -1f, -0.87f, -0.5f, -0.87f, -0.5f, -0.87f, 0.5f, -0.87f, 0.5f, 0f, 1f },
        // Item: a box.
        new[] { -0.8f, -0.8f, 0.8f, -0.8f, 0.8f, -0.8f, 0.8f, 0.8f, 0.8f, 0.8f, -0.8f, 0.8f, -0.8f, 0.8f, -0.8f, -0.8f },
        // Container: a box with a lid line.
        new[] { -0.8f, -0.8f, 0.8f, -0.8f, 0.8f, -0.8f, 0.8f, 0.8f, 0.8f, 0.8f, -0.8f, 0.8f, -0.8f, 0.8f, -0.8f, -0.8f, -0.8f, 0.2f, 0.8f, 0.2f },
        // Terminal: a screen on a stand.
        new[] { -1f, 1f, 1f, 1f, 1f, 1f, 1f, -0.3f, 1f, -0.3f, -1f, -0.3f, -1f, -0.3f, -1f, 1f, 0f, -0.3f, 0f, -1f, -0.5f, -1f, 0.5f, -1f },
        // Door: a frame split down the middle.
        new[] { -0.8f, -1f, -0.8f, 1f, -0.8f, 1f, 0.8f, 1f, 0.8f, 1f, 0.8f, -1f, 0f, 1f, 0f, -1f },
        // Generator: a bolt.
        new[] { 0.3f, 1f, -0.4f, 0f, -0.4f, 0f, 0.4f, 0f, 0.4f, 0f, -0.3f, -1f },
        // Caution: a triangle with a bar.
        new[] { 0f, 1f, 1f, -0.8f, 1f, -0.8f, -1f, -0.8f, -1f, -0.8f, 0f, 1f, 0f, 0.4f, 0f, -0.3f },
        // Sign: a flag.
        new[] { -0.7f, -1f, -0.7f, 1f, -0.7f, 1f, 0.8f, 0.6f, 0.8f, 0.6f, -0.7f, 0.2f },
        // HSU: a tall pod with a band.
        new[] { -0.5f, -1f, 0.5f, -1f, 0.5f, -1f, 0.5f, 1f, 0.5f, 1f, -0.5f, 1f, -0.5f, 1f, -0.5f, -1f, -0.5f, 0.4f, 0.5f, 0.4f },
        // Locator: crosshair ticks round a small diamond.
        new[] { 0f, 1f, 0f, 0.55f, 0f, -1f, 0f, -0.55f, 1f, 0f, 0.55f, 0f, -1f, 0f, -0.55f, 0f, 0f, 0.3f, 0.3f, 0f, 0.3f, 0f, 0f, -0.3f, 0f, -0.3f, -0.3f, 0f, -0.3f, 0f, 0f, 0.3f },
        // Terminal ping: two diamonds, one inside the other.
        new[] { 0f, 1f, 1f, 0f, 1f, 0f, 0f, -1f, 0f, -1f, -1f, 0f, -1f, 0f, 0f, 1f, 0f, 0.4f, 0.4f, 0f, 0.4f, 0f, 0f, -0.4f, 0f, -0.4f, -0.4f, 0f, -0.4f, 0f, 0f, 0.4f },
        // Teammate: an arrowhead.
        new[] { -0.8f, -0.7f, 0f, 0.9f, 0f, 0.9f, 0.8f, -0.7f, 0.8f, -0.7f, 0f, -0.2f, 0f, -0.2f, -0.8f, -0.7f },
    };
}

/// <summary>The compass points of one moment: world positions added between <see cref="Begin"/> and <see cref="Locate"/>, which turns
/// them into bearings and distances from the eye. Fixed arrays, no allocation.</summary>
internal sealed class CompassReading
{
    private readonly float[] _x, _y, _z, _r, _g, _b;
    private readonly CompassPointKind[] _kind;
    private readonly bool[] _player, _mod;
    private readonly int[] _slot;
    private readonly CompassPoint[] _points;

    public CompassReading(int capacity)
    {
        capacity = Math.Max(1, capacity);
        _x = new float[capacity]; _y = new float[capacity]; _z = new float[capacity];
        _r = new float[capacity]; _g = new float[capacity]; _b = new float[capacity];
        _kind = new CompassPointKind[capacity];
        _player = new bool[capacity]; _mod = new bool[capacity];
        _slot = new int[capacity];
        _points = new CompassPoint[capacity];
    }

    public int Capacity => _x.Length;

    /// <summary>The points added since <see cref="Begin"/>.</summary>
    public int Count { get; private set; }

    public void Begin() => Count = 0;

    /// <summary>Adds a point at world <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>; false when full or the position is
    /// not a number. The colour is white unless it is a player's (<paramref name="playerColour"/>) or a mod's point (<paramref name="mod"/>).</summary>
    public bool Add(float x, float y, float z, CompassPointKind kind, int slot, bool playerColour, float r, float g, float b, bool mod = false)
    {
        if (Count >= _x.Length || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        var i = Count++;
        _x[i] = x; _y[i] = y; _z[i] = z;
        _kind[i] = kind; _slot[i] = slot; _player[i] = playerColour; _mod[i] = mod;
        if (playerColour || mod) { _r[i] = r; _g[i] = g; _b[i] = b; }
        else { _r[i] = 1f; _g[i] = 1f; _b[i] = 1f; }
        return true;
    }

    /// <summary>Every point's bearing from an eye at <paramref name="eyeX"/>, <paramref name="eyeY"/>, <paramref name="eyeZ"/> looking along
    /// the horizontal <paramref name="forwardX"/>, <paramref name="forwardZ"/>, and its straight-line distance from the eye.</summary>
    public void Locate(float eyeX, float eyeY, float eyeZ, float forwardX, float forwardZ)
    {
        for (var i = 0; i < Count; i++)
        {
            var dx = _x[i] - eyeX;
            var dy = _y[i] - eyeY;
            var dz = _z[i] - eyeZ;
            var azimuth = Bearing.Relative(forwardX, forwardZ, dx, dz);
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            _points[i] = new CompassPoint(_kind[i], azimuth, distance, _slot[i], _player[i], _r[i], _g[i], _b[i], _mod[i]);
        }
    }

    public CompassPoint Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _points[index];
    }
}
