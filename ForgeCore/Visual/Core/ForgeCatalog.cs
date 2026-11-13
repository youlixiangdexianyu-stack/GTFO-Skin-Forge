namespace ForgeVisual;

/// <summary>The pieces this library makes itself, by name (<c>Forge:soft-dot</c>). They are drawn in code, so every mod has them
/// without shipping a texture. The website manifest lists these rows.</summary>
internal static class ForgeCatalog
{
    internal const string SoftDot = "soft-dot", SoftLine = "soft-line";

    /// <summary>Each sprite sheet with what it is for.</summary>
    internal static readonly (string Item, string Summary)[] Sprites =
    {
        (SoftDot, "A round soft-edged dot: glows, sparks, smoke puffs and light spots; tinted and sized per use."),
        (SoftLine, "A soft-edged streak across its length: tracers, beams and trails; tinted and sized per use."),
    };
}
