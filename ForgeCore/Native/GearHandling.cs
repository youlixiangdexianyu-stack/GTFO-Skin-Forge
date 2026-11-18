using System;
using System.Linq;
using GameData;
using HarmonyLib;
using BulletWeapon = Gear.BulletWeapon;

namespace ForgeCore;

public static partial class Gear
{
    private static (string Owner, Func<BulletWeapon, float, float> Factor)[] _reloadLeases = Array.Empty<(string, Func<BulletWeapon, float, float>)>();

    /// <summary>Scales the reload time of any gun by <paramref name="factor"/> (given the gun and the duration the game answered; 1 =
    /// untouched; the answer is read at once, never kept) for as long as the lease stands; one lease per <paramref name="owner"/>,
    /// null removes it. Leases multiply. The first lease puts on the one <c>BulletWeapon.get_ReloadTime</c> patch. The game's data
    /// blocks are never written; the game's reload animation and ammunition transfer run under the scaled duration.</summary>
    public static void LeaseReload(string owner, Func<BulletWeapon, float, float>? factor)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        Patches.Ensure(Modules.GearReload, typeof(GearReloadHooks));
        var rest = Array.FindAll(_reloadLeases, lease => lease.Owner != owner);
        _reloadLeases = factor == null ? rest : [.. rest, (owner, factor)];
    }

    internal static float ReloadFactor(BulletWeapon weapon, float duration)
    {
        var factor = 1f;
        foreach (var lease in _reloadLeases) factor *= lease.Factor(weapon, duration);
        return factor;
    }

    /// <summary>The block's own readable and writable properties, listed once (reflection is not repeated each time a copy is made). The
    /// block's base class holds <c>persistentID</c>, which a declared-only listing misses; <see cref="CopyArchetype"/> sets it by hand.</summary>
    private static readonly System.Reflection.PropertyInfo[] ArchetypeProperties = typeof(ArchetypeDataBlock)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();

    /// <summary>A copy of one weapon archetype block (every property, and its <c>persistentID</c>), for a gun or sentry that needs its own
    /// numbers while the shared block keeps driving every stock gun. The copy is never registered in the game's data table. Reflection
    /// copies every property, so build the copy once per gun and keep it, not once per shot.</summary>
    public static ArchetypeDataBlock CopyArchetype(ArchetypeDataBlock source)
    {
        var copy = new ArchetypeDataBlock();
        foreach (var property in ArchetypeProperties) property.SetValue(copy, property.GetValue(source));
        copy.persistentID = source.persistentID;
        return copy;
    }

    /// <summary>A copy of <paramref name="source"/> whose view kick (power, so up and sideways alike), crosshair bloom, weapon kick and
    /// shake are scaled by <paramref name="multiplier"/>; the source is untouched. The axis ranges are the kick's direction pattern, not
    /// its strength, and stay.</summary>
    public static RecoilDataBlock ScaledRecoil(RecoilDataBlock source, float multiplier)
    {
        var copy = source.MemberwiseClone().Cast<RecoilDataBlock>();
        // Ranges are reference types: new ones, so the source stays as it is.
        copy.power = new MinMaxValue { Min = source.power.Min * multiplier, Max = source.power.Max * multiplier };
        copy.hipFireCrosshairRecoilPop = source.hipFireCrosshairRecoilPop * multiplier;
        copy.recoilPosImpulse = source.recoilPosImpulse * multiplier;
        copy.recoilPosShift = source.recoilPosShift * multiplier;
        copy.recoilRotImpulse = source.recoilRotImpulse * multiplier;
        copy.concussionIntensity = source.concussionIntensity * multiplier;
        return copy;
    }
}

/// <summary>The one <c>BulletWeapon.get_ReloadTime</c> patch for every mod that scales reloads (<see cref="Gear.LeaseReload"/>).</summary>
internal static class GearReloadHooks
{
    [HarmonyPatch(typeof(BulletWeapon), "get_ReloadTime")]
    [HarmonyPostfix]
    private static void Postfix(BulletWeapon __instance, ref float __result)
    {
        if (__instance == null) return;
        try { __result *= Gear.ReloadFactor(__instance, __result); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-reload-lease-failed", "A reload lease failed: " + error); }
    }
}
