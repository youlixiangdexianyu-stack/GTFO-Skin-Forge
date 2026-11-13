namespace ForgeUI;

/// <summary>Where a template's content grows from its root: its region's direction on each axis (see <see cref="HudRegions.Flow"/>).</summary>
internal static class HudFlow
{
    internal static (int X, int Y) Of(HudElement hud) => HudRegions.Flow(hud.Region);
}
