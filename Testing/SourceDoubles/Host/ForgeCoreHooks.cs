using System.Reflection;
using HarmonyLib;

namespace ForgeCore;

// Source double of ForgeCore/Native/Hooks.cs for offline tests of the packages that install through it: each owner's callback set is
// recorded instead of patched, so a test can call the callbacks as the game would. Ownership is decided by the production
// HookOwners (ForgeCore/Core/HookOwners.cs, compiled beside this file); refusals keep ForgeCore's codes.
public sealed record HookPart(HarmonyMethod? Prefix = null, HarmonyMethod? Postfix = null, HarmonyMethod? Finalizer = null);

public static class Hooks
{
    /// <summary>Each owned method's owner and parts, as installed.</summary>
    public static readonly Dictionary<MethodBase, (string Owner, IReadOnlyList<HookPart> Parts)> Owned = new();
    /// <summary>Each active observer and managed-wrapper hook.</summary>
    public static readonly List<(string Owner, MethodBase Target, HookPart Part)> Observed = new();
    /// <summary>Physical installations (Own calls that wrote).</summary>
    public static int Writes { get; private set; }
    /// <summary>What <see cref="Unsupported"/> answers; null admits every target.</summary>
    public static Func<MethodBase, string?> Refuse = _ => null;
    /// <summary>False makes every write fail as the real backend does when the pinned bridge is not loaded.</summary>
    public static bool Available = true;

    public static void Own(string owner, MethodBase target, IReadOnlyList<HookPart> parts)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || parts == null || parts.Count == 0) throw new ArgumentException("native-hook-declaration-invalid");
        if (Unsupported(target) is { } unsupported) throw new NotSupportedException(unsupported);
        var set = string.Join("|", parts.Select(part => Name(part.Prefix) + "," + Name(part.Postfix) + "," + Name(part.Finalizer)));
        var claim = HookOwners.Claim(owner, target, set);
        if (claim == "") return;
        if (claim != null) throw new NotSupportedException(claim);
        if (!Available) { HookOwners.Release(owner, target); throw new NotSupportedException("native-hook-bridge-backend-unavailable"); }
        Writes++;
        Owned[target] = (owner, parts.ToArray());
    }

    public static void Disown(string owner, MethodBase target)
    {
        if (HookOwners.OwnerOf(target) != owner) return;
        Owned.Remove(target);
        HookOwners.Release(owner, target);
    }

    public static string? OwnerOf(MethodBase target) => HookOwners.OwnerOf(target);
    public static string? Refusal(string owner, MethodBase target) => Unsupported(target) ?? HookOwners.Refusal(owner, target);
    public static string? Unsupported(MethodBase target) => Refuse(target ?? throw new ArgumentNullException(nameof(target)));

    public static IDisposable Observe(string owner, MethodBase target, HookPart part)
    {
        if (Unsupported(target) is { } unsupported) throw new NotSupportedException(unsupported);
        if (Observed.Any(row => row.Owner == owner && row.Target == target)) throw new NotSupportedException("native-hook-observation-duplicate");
        if (!Available) throw new NotSupportedException("native-hook-bridge-backend-unavailable");
        var row = (owner, target, part);
        Observed.Add(row);
        return new Removal(() => Observed.Remove(row));
    }

    public static IDisposable Wrap(string owner, MethodInfo target, HookPart part)
    {
        if (Observed.Any(row => row.Target == target && row.Owner.StartsWith("wrapper:", StringComparison.Ordinal)))
            throw new NotSupportedException("managed-hook-declaration-conflict");
        var row = ("wrapper:" + owner, (MethodBase)target, part);
        Observed.Add(row);
        return new Removal(() => Observed.Remove(row));
    }

    private static string Name(HarmonyMethod? method) => method == null ? "" : method.method.DeclaringType?.FullName + "." + method.method.Name + "@" + method.priority;

    private sealed class Removal : IDisposable
    {
        private Action? _remove;
        internal Removal(Action remove) => _remove = remove;
        public void Dispose() { _remove?.Invoke(); _remove = null; }
    }
}
