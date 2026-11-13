using System;
using System.Globalization;

namespace ForgeCore;

/// <summary>Which expedition: the rundown block's persistent ID, the tier (1 = A through 5 = E, the game's <c>eRundownTier</c> value)
/// and the zero-based index inside the tier.</summary>
public readonly record struct LevelId(uint RundownId, int Tier, int Index)
{
    /// <summary>The unambiguous written form, rundown ID then tier letter then the one-based level number: <c>37-D1</c>.</summary>
    public string Coordinate => RundownId.ToString(CultureInfo.InvariantCulture) + "-" + (char)('A' + Tier - 1) + (Index + 1).ToString(CultureInfo.InvariantCulture);

    public bool IsValid => RundownId != 0 && Tier is >= 1 and <= 5 && Index >= 0;

    /// <summary>Reads the form <see cref="Coordinate"/> writes; false for anything else, with a default result.</summary>
    public static bool TryParse(string? text, out LevelId id)
    {
        id = default;
        if (text == null) return false;
        var dash = text.IndexOf('-');
        if (dash <= 0 || dash + 2 >= text.Length) return false;
        if (!uint.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var rundown)) return false;
        var letter = char.ToUpperInvariant(text[dash + 1]);
        if (letter is < 'A' or > 'E') return false;
        if (!int.TryParse(text.AsSpan(dash + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1) return false;
        var parsed = new LevelId(rundown, letter - 'A' + 1, number - 1);
        if (!parsed.IsValid) return false;
        id = parsed;
        return true;
    }
}

/// <summary>The three seeds the game's level generation draws on: the geometry build seed, the host-ID seed and the session seed. The same
/// level with the same three seeds generates the same map.</summary>
public readonly record struct LevelSeeds(int BuildSeed, int HostIdSeed, int SessionSeed);
