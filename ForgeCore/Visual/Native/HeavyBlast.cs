using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The heavy grenade launcher's explosion, drawn under one name by every package that fires one (ForgeOrdnance's grenade launcher,
/// ForgeWeapon_EnergyLab's blast gun) so a recording names it the same whichever package fired it. This library ships no effect of it: each
/// such package ships its own copy of the bundle and slot table and registers where they are (<see cref="Register"/>); the slot is
/// <c>heavy.gl.blast</c> of the pack <c>ForgeShared</c>. The first registered copy that loads is the one used; the copies are the same bytes,
/// so a second package's copy is never loaded beside it (and <c>ForgeCore.Assets</c> loads equal bundle bytes once anyway). The bundle loads when a
/// package that fires it loads its own effects (<see cref="Load"/>), or before that the first time the effect is played or a name of its pack is
/// looked up (a replay of a recorded blast). With no such package installed a <c>ForgeShared:</c> name is not found: <see cref="Fx.Find"/> returns
/// null and logs that once, and nothing is drawn.
/// </summary>
public static class HeavyBlast
{
    /// <summary>The slot the effect library's table gives the launcher's explosion.</summary>
    internal const string Slot = "heavy.gl.blast";
    private const string Pack = "ForgeShared", Prefix = Pack + ":";
    private static readonly List<(string ModId, string Bundle, string Slots)> Copies = new();
    private static FxHandle? _handle;
    private static bool _loaded;

    /// <summary>Registers the calling package's copy of the blast: its bundle and slot table, relative paths taken from the calling mod's DLL folder.
    /// Loads nothing; call it at plugin load so a replay finds the blast before the package's own effects are read.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Register(string modId, string bundlePath, string slotsPath)
    {
        var folder = Path.GetDirectoryName(System.Reflection.Assembly.GetCallingAssembly().Location) ?? "";
        Copies.Add((modId, Path.Combine(folder, bundlePath), Path.Combine(folder, slotsPath)));
    }

    /// <summary>Loads the bundle and the slot table before the first lookup of a <c>ForgeShared:</c> name; every later call is one comparison.</summary>
    internal static void Ensure(string? name)
    {
        if (name != null && name.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase)) Load();
    }

    /// <summary>Loads the first registered copy that loads, once. A package that fires the blast calls this where it loads its own effects (a level
    /// build, not a fight), so the first blast does not pay for loading it; the first play or lookup loads it otherwise. Nothing while no copy is registered.</summary>
    public static void Load()
    {
        if (_loaded || Copies.Count == 0) return;
        _loaded = true;
        foreach (var (modId, bundle, slots) in Copies)
        {
            // A copy whose files are missing or damaged is logged under its package and the next one is tried.
            var set = FxSlots.Load(modId, Pack, bundle, slots);
            if (set == FxSlots.Empty) continue;
            _handle = set.Find(Slot);
            return;
        }
    }

    /// <summary>Draws the launcher's explosion at <paramref name="at"/>, with the light and size the slot table gives it.</summary>
    public static void Play(Vector3 at)
    {
        Load();
        if (_handle != null) Fx.Play(_handle, at, Vector3.up);
    }
}
