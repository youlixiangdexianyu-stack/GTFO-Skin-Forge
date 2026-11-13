namespace ForgeCore;

// No game types: the player test double links this file beside its own Combat part.
static partial class Combat
{
    /// <summary>The smallest health change the game's health packets carry. `pAddHealthData.health` and `pSetHealthData.health` are
    /// `SFloat16` (`ushort internalValue`, `convIn = 65535`), a signed value over -max..max, so one step is 2 * max / 65535 (0.00076
    /// at 25). A replicated full health reads one step under the maximum (24.999237/25), and a change of at most one step lands
    /// nothing: a receiver that close to its maximum is as full as the game can hold.</summary>
    public static double HealthPacketStep(double maximum) => 2d * maximum / 65535d;
}
