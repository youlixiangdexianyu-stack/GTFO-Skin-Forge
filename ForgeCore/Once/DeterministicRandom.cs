namespace ForgeCore;

/// <summary>Random numbers that are a pure function of their keys, so every peer, a replay and a checkpoint reload draw the same value with
/// no packet and without touching the game's own random sequence: SplitMix64 over the keys. Not for secrets; the output of one key never
/// shifts another's. No game or Unity type.</summary>
public static class DeterministicRandom
{
    /// <summary>One 64-bit value from two keys (a bucket and an index, a row and its draw count).</summary>
    public static ulong Mix(ulong left, ulong right)
    {
        unchecked
        {
            var value = left * 0x9E3779B97F4A7C15UL + right * 0xBF58476D1CE4E5B9UL;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    /// <summary>A uniform number in [0, 1) from <paramref name="hash"/> (the top 53 bits).</summary>
    public static double Unit(ulong hash) => (hash >> 11) / 9007199254740992d;

    /// <summary>A uniform number in [0, 1) from two keys.</summary>
    public static double Unit(ulong left, ulong right) => Unit(Mix(left, right));
}
