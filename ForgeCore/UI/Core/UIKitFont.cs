namespace ForgeUI;

/// <summary>The faces a HUD text can use.</summary>
public enum UIKitFont : byte
{
    /// <summary>The game's own HUD font (the default).</summary>
    Game,
    /// <summary>The kit's narrow technical face: Rajdhani for Latin letters and digits, Glow Sans SC Condensed for Chinese, the game's
    /// font for anything else. The cockpit templates use it.</summary>
    Tech,
}
