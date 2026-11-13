using System;
using System.Collections.Generic;
using System.Reflection;

namespace ForgeCore;

/// <summary>Who owns each game method a package patches through <c>Hooks</c>: one owner per method, its whole callback set
/// named by a signature. The static patch-target gate cannot read a target named only at run time, so this record is the run-time
/// half of "one game method, one package": a second owner is refused when it declares, and <see cref="Shared"/> finds every owned
/// method whose recorded patches belong to more than one Forge package. Main thread only.</summary>
internal static class HookOwners
{
    /// <summary>The Harmony id prefixes <c>Hooks</c> installs under; the owner follows the prefix.</summary>
    internal const string OwnPrefix = "ForgeCore.Hooks/", ObservePrefix = "ForgeCore.Observe/";

    private static readonly Dictionary<MethodBase, (string Owner, string Set)> Owned = new();

    /// <summary>Null when <paramref name="owner"/> may now install <paramref name="set"/> on <paramref name="target"/> (the claim is
    /// recorded), an empty string when exactly that set is already its own there, otherwise the refusal.</summary>
    internal static string? Claim(string owner, MethodBase target, string set)
    {
        if (Owned.TryGetValue(target, out var known))
            return known.Owner != owner ? "native-hook-writer-conflict existing=" + known.Owner
                : known.Set == set ? "" : "native-hook-declaration-changed";
        Owned.Add(target, (owner, set));
        return null;
    }

    /// <summary>The refusal <see cref="Claim"/> would give, without claiming; null when it would claim or already holds the set.</summary>
    internal static string? Refusal(string owner, MethodBase target)
        => Owned.TryGetValue(target, out var known) && known.Owner != owner ? "native-hook-writer-conflict existing=" + known.Owner : null;

    internal static void Release(string owner, MethodBase target)
    {
        if (Owned.TryGetValue(target, out var known) && known.Owner == owner) Owned.Remove(target);
    }

    internal static string? OwnerOf(MethodBase target) => Owned.TryGetValue(target, out var known) ? known.Owner : null;

    /// <summary>The Forge package one recorded patch belongs to: the assembly its callback is compiled into (ForgeMap.Native is
    /// ForgeMap), whoever installed it; null for an observer (read-only, under <see cref="ObservePrefix"/>) and for a mod that is not Forge's.</summary>
    internal static string? Package(string patchOwner, MethodInfo callback)
    {
        if (patchOwner.StartsWith(ObservePrefix, StringComparison.Ordinal)) return null;
        var name = callback.DeclaringType?.Assembly.GetName().Name ?? "";
        var end = name.IndexOf('.');
        var head = end < 0 ? name : name.Substring(0, end);
        return head.StartsWith("Forge", StringComparison.Ordinal) ? head : null;
    }

    /// <summary>The Forge packages of <paramref name="patches"/> (each recorded patch's Harmony id and callback) in name order.</summary>
    internal static SortedSet<string> Packages(IEnumerable<(string Owner, MethodInfo Callback)> patches)
    {
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (owner, callback) in patches)
            if (Package(owner, callback) is { } package) packages.Add(package);
        return packages;
    }

    /// <summary>Every owned method whose recorded patches (<paramref name="patches"/>) span more than one Forge package, as
    /// "Type.Method: PackageA, PackageB". Empty when every owned method has one package.</summary>
    internal static List<string> Shared(Func<MethodBase, IEnumerable<(string Owner, MethodInfo Callback)>> patches)
    {
        var shared = new List<string>();
        foreach (var target in Owned.Keys)
        {
            var packages = Packages(patches(target));
            if (packages.Count > 1) shared.Add(target.DeclaringType?.FullName + "." + target.Name + ": " + string.Join(", ", packages));
        }
        shared.Sort(StringComparer.Ordinal);
        return shared;
    }
}
