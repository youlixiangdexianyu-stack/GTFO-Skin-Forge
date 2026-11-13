namespace ForgeUI;

/// <summary>A teammate's ammunition as one fraction, from the game's synced ammo storage of that player.</summary>
internal static class TeammateAmmo
{
    /// <summary>(standard + special) / (standard cap + special cap), clamped to 0..1. A special weapon whose cap is not above zero
    /// (none carried) leaves only the main weapon in the sum; −1 when there is no cap to compare against.</summary>
    public static float Rel(float stdInPack, float stdMax, float spcInPack, float spcMax)
    {
        var hasSpecial = spcMax > 0f;
        var max = stdMax + (hasSpecial ? spcMax : 0f);
        if (!(max > 0f)) return -1f;
        var rel = (stdInPack + (hasSpecial ? spcInPack : 0f)) / max;
        if (float.IsNaN(rel)) return -1f;
        return rel < 0f ? 0f : rel > 1f ? 1f : rel;
    }
}

/// <summary>Which of an inventory slot's counts the game's own weapon list shows: a count its show flag leaves off is −1.</summary>
internal static class InventoryAmmo
{
    public static int Shown(bool show, int value) => show ? value : -1;

    public static float Shown(bool show, float value) => show ? value : -1f;
}
