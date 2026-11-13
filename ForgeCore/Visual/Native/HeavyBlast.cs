using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The heavy grenade launcher's explosion, shared by every package that fires one: ForgeOrdnance's grenade launcher and
/// ForgeWeapon_EnergyLab's blast gun draw this one effect instead of each shipping its own. The prefab, its scale and its light are data
/// in <c>forge-visual-fx.bundle</c> and <c>fx-slots.forge</c> beside this library (built from the effect library's table like the other
/// packages' effect bundles); the slot is <c>heavy.gl.blast</c>. The bundle loads once the elevator has stopped, or before that the first time the effect is played
/// or a name of its pack is looked up (a replay of a recorded blast), never at start. A clean clone has the table but not the media: the library logs once and
/// draws nothing, as every effect slot does.
/// </summary>
public static class HeavyBlast
{
    /// <summary>The slot the effect library's table gives the launcher's explosion.</summary>
    internal const string Slot = "heavy.gl.blast";
    private const string Pack = "ForgeShared", Prefix = Pack + ":";
    private static FxHandle? _handle;
    private static bool _loaded;

    /// <summary>Loads the bundle and the slot table before the first lookup of a <c>ForgeShared:</c> name; every later call is one comparison.</summary>
    internal static void Ensure(string? name)
    {
        if (name != null && name.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase)) Load();
    }

    /// <summary>Loads the bundle and the slot table once: at landing, or before it by the first play or lookup.</summary>
    internal static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        _handle = FxSlots.Load(Plugin.Id, Pack, "forge-visual-fx.bundle", "fx-slots.forge").Find(Slot);
    }

    /// <summary>Draws the launcher's explosion at <paramref name="at"/>, with the light and size the slot table gives it.</summary>
    public static void Play(Vector3 at)
    {
        Load();
        if (_handle != null) Fx.Play(_handle, at, Vector3.up);
    }
}
