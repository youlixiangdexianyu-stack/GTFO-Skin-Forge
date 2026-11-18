using System;

namespace ForgeVisual;

/// <summary>The effect bundle Forge ships: particle prefabs built by <c>Tools/Vfx/ForgeVfx</c> (Unity 2019.4.21f1, the game's own version),
/// embedded in this DLL and registered under the reserved pack <c>Forge</c> (<c>Forge:forge_sparks</c>) once the elevator has stopped (<see cref="Load"/>, a landing step),
/// or before that the first time a name of that pack, or the pack itself, is looked up (<see cref="Ensure"/>), so no fight pays for the load. Every Forge mod draws its effects from
/// this one copy; Unity refuses a second load of the same bundle, so nothing else loads it. A DLL built without the file registers nothing,
/// and a model asked for by name is then refused by name.</summary>
internal static class EffectBundle
{
    internal const string File = "forge-vfx.bundle";
    internal const string Resource = "ForgeVisual.Vfx." + File;
    private const string Prefix = VisualNames.Forge + ":";
    private static bool _tried;

    /// <summary>Registers the pack before the first lookup that needs it; every later call is one comparison.</summary>
    internal static void Ensure(string? name)
    {
        if (_tried || name == null) return;
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, VisualNames.Forge, StringComparison.OrdinalIgnoreCase)) return;
        Load();
    }

    /// <summary>Registers the pack once: at landing, or before it by the first lookup that needs it.</summary>
    internal static void Load()
    {
        if (_tried) return;
        _tried = true;
        Register();
    }

    private static void Register()
    {
        var bytes = ForgeCore.Assets.Embedded(Resource);
        if (bytes.Length == 0)
        {
            Plugin.Logger.ErrorOnce("visual-forge-pack-missing", "ForgeVisual pack Forge was not registered: " + Resource + " is not embedded in this build, so the Forge effects are refused.");
            return;
        }
        var bundle = Bundles.RegisterBuiltIn(File, bytes);
        if (bundle == null) return;
        foreach (var effect in ForgeEffects.All)
            Fx.Register(bundle, effect.Item, new FxOptions { DefaultSize = effect.DefaultSize, Line = effect.Line, HoldSeconds = effect.HoldSeconds });
    }
}
