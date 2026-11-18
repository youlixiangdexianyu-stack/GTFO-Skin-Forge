using System;

namespace ForgeUI;

/// <summary>One filled slot of the weapon list as far as the held magazine is concerned (see <see cref="HeldMagazine.Pick"/>).</summary>
public readonly record struct SlotFacts(bool Selected, int Clip, int ClipSize, bool Infinite);

/// <summary>
/// The magazine of the item in hand: rounds loaded and the magazine's size, or no magazine at all (a scanner, a hammer, a carried
/// cell, an empty hand), which reads as unlimited. An empty magazine is still a magazine (0 of its size), never "none".
/// </summary>
public readonly record struct HeldMagazine(bool Limited, int Clip, int Size)
{
    /// <summary>No magazine: reads as unlimited.</summary>
    public static HeldMagazine Unlimited => default;

    /// <summary>The magazine of one slot: the game's list shows no rounds (−1) or an infinite supply → unlimited.</summary>
    public static HeldMagazine Of(in SlotFacts slot) =>
        slot.Infinite || slot.Clip < 0 ? Unlimited : new HeldMagazine(true, slot.Clip, slot.ClipSize);

    /// <summary>The selected slot's magazine; unlimited when no slot is selected (the carried item, an empty hand).</summary>
    public static HeldMagazine Pick(ReadOnlySpan<SlotFacts> slots)
    {
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].Selected) return Of(slots[i]);
        return Unlimited;
    }

    /// <summary>How full the magazine is, 0..1; an unlimited one reads full.</summary>
    public float Fraction => !Limited ? 1f : Size > 0 ? Math.Clamp(Clip / (float)Size, 0f, 1f) : Clip > 0 ? 1f : 0f;
}

/// <summary>When a mark on the game's crosshair layer is drawn.</summary>
internal static class CrosshairRules
{
    /// <summary>Every mark that stands in for the crosshair (ring, ticks, dot, landing mark) is hidden while aiming down sights (the game draws none then) and drawn again after.</summary>
    public static bool Drawn(bool visible, bool hideWhileAiming, bool aiming) => visible && !(hideWhileAiming && aiming);

    /// <summary>How many graphics (ring, dot, ticks, landing mark) an attachment draws: none while it is hidden by the aim.</summary>
    public static int Graphics(bool visible, bool hideWhileAiming, bool aiming, bool ring, bool dot, int ticks, bool mark) =>
        Drawn(visible, hideWhileAiming, aiming) ? (ring ? 1 : 0) + (dot ? 1 : 0) + Math.Max(0, ticks) + (mark ? 1 : 0) : 0;
}
