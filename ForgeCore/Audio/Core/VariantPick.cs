using System;

namespace ForgeAudio;

/// <summary>Picks the variant of a random play: uniform over the variants except the one this cue used last, so the same sound
/// never plays twice in a row. The last pick lives on the loaded cue; nothing is allocated.</summary>
internal static class VariantPick
{
    /// <summary>The next variant in 0..count-1; updates <paramref name="last"/> (-1 before the first play).</summary>
    internal static int Next(int count, ref int last, Random random)
    {
        if (count < 2) { last = 0; return 0; }
        var pick = random.Next(count - 1);
        if (pick >= last) pick++;
        return last = pick;
    }
}
