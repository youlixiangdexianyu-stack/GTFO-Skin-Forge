using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.RuntimeDetour;

namespace ForgeCore;

/// <summary>One package's callbacks on one game method: each is a Harmony patch method with its priority and before/after ids.</summary>
public sealed record HookPart(HarmonyMethod? Prefix = null, HarmonyMethod? Postfix = null, HarmonyMethod? Finalizer = null);

/// <summary>Patches on game methods a package names only at run time (a target found by reflection, a callback class handed over by
/// another package), through the public Harmony API. <see cref="Bracket{TCall}"/> is how several packages see one fixed method;
/// this is how one package owns a method. Every owned method has exactly one owner: a second owner is refused when it declares
/// (native-hook-writer-conflict), and once BepInEx has loaded every plugin ForgeCore checks each owned method's recorded patches and
/// logs one error per method another Forge package also patches. An observer (<see cref="Observe"/>) only reads, so several may
/// watch a method beside its owner. A native entry whose first bytes the detour backend cannot relocate is refused (<see cref="HookPrologue"/>);
/// a target whose by-reference parameters the Il2CppInterop bridge carries wrongly is repaired before it is patched
/// (<see cref="HookBridge"/>) or refused. Main thread only.</summary>
// patch-targets: run time (each target is registered in HookOwners and checked by ForgeCore at startup, see Hooks.CheckAll)
public static class Hooks
{
    private static readonly Dictionary<string, Harmony> Writers = new(StringComparer.Ordinal);
    private static readonly HashSet<(string Owner, MethodBase Target)> Observers = new();
    private static readonly Dictionary<MethodBase, string> Wrapped = new();
    private static readonly HashSet<MethodBase> Reported = new();
    private static readonly Dictionary<MethodBase, string?> Entries = new();

    /// <summary>Puts <paramref name="owner"/>'s whole callback set on <paramref name="target"/>, each part its own Harmony patch in
    /// the order given (Harmony then orders them by priority, then by that order). Declaring the same set again does nothing; a
    /// different set, another owner or an unsupported target throws <see cref="NotSupportedException"/> with the native-hook-* reason
    /// before anything is written. A patch that fails takes the owner's parts off again and releases the claim.</summary>
    public static void Own(string owner, MethodBase target, IReadOnlyList<HookPart> parts)
    {
        Validate(owner, target);
        if (parts == null || parts.Count == 0 || parts.Any(part => part == null || part.Prefix == null && part.Postfix == null && part.Finalizer == null))
            throw new ArgumentException("native-hook-declaration-invalid");
        if (Unsupported(target) is { } unsupported) throw new NotSupportedException(unsupported);
        var claim = HookOwners.Claim(owner, target, Signature(parts));
        if (claim == "") return;
        if (claim != null) throw new NotSupportedException(claim);
        var writer = Writer(HookOwners.OwnPrefix + owner);
        try
        {
            HookBridge.Prepare(target);
            foreach (var part in parts) writer.Patch(target, prefix: part.Prefix, postfix: part.Postfix, finalizer: part.Finalizer);
        }
        catch
        {
            HookOwners.Release(owner, target);
            try { writer.Unpatch(target, HarmonyPatchType.All, writer.Id); }
            catch (Exception cleanup) { Plugin.Logger.ErrorOnce("core-hooks-could-not-be-taken-off", "Hooks of " + owner + " on " + Name(target) + " could not be taken off after a failed patch: " + cleanup); }
            throw;
        }
        Check(target);
    }

    /// <summary>Takes <paramref name="owner"/>'s set off <paramref name="target"/> and releases the method. Nothing when it is not the owner's.</summary>
    public static void Disown(string owner, MethodBase target)
    {
        Validate(owner, target);
        if (HookOwners.OwnerOf(target) != owner) return;
        var writer = Writer(HookOwners.OwnPrefix + owner);
        writer.Unpatch(target, HarmonyPatchType.All, writer.Id);
        HookOwners.Release(owner, target);
    }

    /// <summary>The owner of <paramref name="target"/>, or null when no package owns it here.</summary>
    public static string? OwnerOf(MethodBase target) => HookOwners.OwnerOf(target ?? throw new ArgumentNullException(nameof(target)));

    /// <summary>Why <see cref="Own"/> would refuse <paramref name="owner"/> on <paramref name="target"/> (<see cref="Unsupported"/>,
    /// another owner), or null. Nothing is written.</summary>
    public static string? Refusal(string owner, MethodBase target)
    {
        Validate(owner, target);
        return Unsupported(target) ?? HookOwners.Refusal(owner, target);
    }

    /// <summary>Why <paramref name="target"/> cannot be patched here at all, or null: a by-reference parameter of a shape the
    /// Il2CppInterop bridge carries wrongly and Forge has not reviewed (native-hook-byref-shape-unsupported), or one that needs the
    /// bridge repair while the loaded Il2CppInterop.HarmonySupport is not the pinned build (native-hook-bridge-backend-unavailable).</summary>
    public static string? Unsupported(MethodBase target)
    {
        if (!HookBridge.Il2Cpp(target ?? throw new ArgumentNullException(nameof(target)))) return null;
        if (Entry(target) is { } entry) return entry;
        try { return HookByRef.Repairs(target).Count != 0 && HookBridgeBackend.Generator() == null ? "native-hook-bridge-backend-unavailable" : null; }
        catch (NotSupportedException error) { return error.Message; }
    }

    /// <summary>The <see cref="HookPrologue"/> verdict on the native entry, read once, before Forge's first patch on it. An entry
    /// another mod patched first already carries its detour, so only the managed side is added and it is not judged.</summary>
    private static string? Entry(MethodBase target)
    {
        if (Entries.TryGetValue(target, out var verdict)) return verdict;
        return Harmony.GetPatchInfo(target) != null ? null : Read(target);
    }

    /// <summary>The same verdict for every other Harmony patch a Forge package puts on an Il2Cpp method (a [HarmonyPatch] class,
    /// PatchAll, Harmony.Patch): Harmony asks its patcher resolvers once per method, at the first patch anyone puts on it, before
    /// the detour is written. A refused entry patched from Forge code (a Forge assembly on the stack) throws there, so the patch
    /// fails with the reason and the entry stays untouched; the refused patch is taken out of the method's record so a later
    /// patch does not carry it. Another mod's patches are not judged. Registered once, at ForgeCore's load, before the packages
    /// that depend on it load.</summary>
    internal static void Resolve(object? sender, PatchManager.PatcherResolverEventArgs args)
    {
        var target = args.Original;
        if (!HookBridge.Il2Cpp(target)) return;
        var verdict = Entries.TryGetValue(target, out var known) ? known : Read(target);
        if (verdict == null || FromForge() is not { } package) return;
        if (target.GetPatchInfo() is { } info)
            foreach (var patch in info.prefixes.Concat(info.postfixes).Concat(info.finalizers).Concat(info.transpilers).Concat(info.ilmanipulators).ToArray())
                info.RemovePatch(patch.PatchMethod);
        Plugin.Logger.Error("native-hook-prologue-refused", "package=" + package + " target=" + Name(target) + " reason=" + verdict);
        throw new NotSupportedException(verdict + " target=" + Name(target));
    }

    private static string? FromForge()
    {
        foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames())
        {
            var name = frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
            if (name != null && name.StartsWith("Forge", StringComparison.Ordinal)) return name;
        }
        return null;
    }

    private static string? Read(MethodBase target)
    {
        string? verdict;
        var info = (IntPtr)Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target)!.GetValue(null)!;
        var entry = info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
        if (entry == IntPtr.Zero) verdict = "native-body-unavailable";
        else
        {
            var bytes = new byte[HookPrologue.SnapshotLength];
            Marshal.Copy(entry, bytes, 0, bytes.Length);
            verdict = HookPrologue.Rejection(bytes);
        }
        Entries.Add(target, verdict);
        return verdict;
    }

    /// <summary>Puts a read-only observer of <paramref name="owner"/> on <paramref name="target"/> beside its owner and any other
    /// observer; disposing it takes exactly that observer off. One observer per owner and method (native-hook-observation-duplicate).</summary>
    public static IDisposable Observe(string owner, MethodBase target, HookPart part)
    {
        Validate(owner, target);
        if (part == null || part.Prefix == null && part.Postfix == null && part.Finalizer == null) throw new ArgumentException("native-hook-observation-invalid");
        if (Unsupported(target) is { } unsupported) throw new NotSupportedException(unsupported);
        if (!Observers.Add((owner, target))) throw new NotSupportedException("native-hook-observation-duplicate");
        var writer = Writer(HookOwners.ObservePrefix + owner);
        try
        {
            HookBridge.Prepare(target);
            writer.Patch(target, prefix: part.Prefix, postfix: part.Postfix, finalizer: part.Finalizer);
        }
        catch
        {
            Observers.Remove((owner, target));
            try { writer.Unpatch(target, HarmonyPatchType.All, writer.Id); }
            catch (Exception cleanup) { Plugin.Logger.ErrorOnce("core-observer-could-not-be-taken-off", "Observer " + owner + " on " + Name(target) + " could not be taken off after a failed patch: " + cleanup); }
            throw;
        }
        return new Removal(() =>
        {
            writer.Unpatch(target, HarmonyPatchType.All, writer.Id);
            Observers.Remove((owner, target));
        });
    }

    /// <summary>Every patch of a [HarmonyPatch] class as read-only observers of <paramref name="owner"/> (<see cref="Observe"/>): per target
    /// method, the class's prefix, postfix and finalizer on it (one of each kind per target). A package that only reads a method another
    /// package owns writes its patches as usual and marks the class <c>// patch-targets: observation (...)</c>. If one target fails, the
    /// targets already observed are taken off again and the error is thrown; disposing the result takes all of them off.</summary>
    public static IDisposable ObserveClass(string owner, Type patches)
    {
        var fixes = ForgeCore.Patches.Class(patches ?? throw new ArgumentNullException(nameof(patches))).Fixes;
        var taken = new List<IDisposable>();
        try
        {
            foreach (var target in fixes.SelectMany(fix => fix.Targets).Distinct())
            {
                HarmonyMethod? Of(PatchKinds kind) => fixes.Where(fix => fix.Kind == kind && Array.IndexOf(fix.Targets, target) >= 0).Select(fix => new HarmonyMethod(fix.Method)).SingleOrDefault();
                taken.Add(Observe(owner, target, new HookPart(Of(PatchKinds.Prefix), Of(PatchKinds.Postfix), Of(PatchKinds.Finalizer))));
            }
        }
        catch
        {
            foreach (var observer in taken) observer.Dispose();
            throw;
        }
        return new Removal(() => { foreach (var observer in taken) observer.Dispose(); });
    }

    /// <summary>Puts <paramref name="part"/> on the managed interop wrapper of an Il2Cpp method (an IL hook, no native detour), for a
    /// call made from managed code that the native entry never sees. One wrapper hook per method; disposing it takes it off.</summary>
    public static IDisposable Wrap(string owner, MethodInfo target, HookPart part)
    {
        Validate(owner, target);
        if (part == null || part.Prefix == null && part.Postfix == null && part.Finalizer == null) throw new ArgumentException("managed-hook-declaration-invalid");
        if (Wrapped.TryGetValue(target, out var other)) throw new NotSupportedException("managed-hook-declaration-conflict existing=" + other);
        var id = HookOwners.ObservePrefix + owner;
        static Patch[] One(HarmonyMethod? method, string id) => method == null ? Array.Empty<Patch>() : new[] { new Patch(method, 0, id) };
        var info = new PatchInfo { prefixes = One(part.Prefix, id), postfixes = One(part.Postfix, id), finalizers = One(part.Finalizer, id) };
        var hook = new ILHook(target, context => HarmonyManipulator.Manipulate(target, info, context), new ILHookConfig { ManualApply = true });
        try
        {
            hook.Apply();
            if (!hook.IsValid || !hook.IsApplied) throw new InvalidOperationException("managed-hook-ilwrapper-installation-unverified");
        }
        catch { hook.Dispose(); throw; }
        Wrapped.Add(target, owner);
        return new Removal(() =>
        {
            hook.Dispose();
            if (hook.IsApplied) throw new InvalidOperationException("managed-hook-removal-unverified");
            Wrapped.Remove(target);
        });
    }

    /// <summary>Run once BepInEx has loaded every plugin: one error per owned method that more than one Forge package patches.</summary>
    internal static void CheckAll()
    {
        foreach (var line in HookOwners.Shared(Patches))
            Plugin.Logger.Error("native-hook-method-shared", line + " (one game method belongs to one package; the others must subscribe to its owner).");
    }

    private static void Check(MethodBase target)
    {
        var packages = HookOwners.Packages(Patches(target));
        if (packages.Count > 1 && Reported.Add(target))
            Plugin.Logger.Error("native-hook-method-shared", Name(target) + ": " + string.Join(", ", packages)
                + " (one game method belongs to one package; the others must subscribe to its owner).");
    }

    private static IEnumerable<(string Owner, MethodInfo Callback)> Patches(MethodBase target)
    {
        var info = Harmony.GetPatchInfo(target);
        if (info == null) return Enumerable.Empty<(string, MethodInfo)>();
        return info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers).Concat(info.ILManipulators)
            .Select(patch => (patch.owner, patch.PatchMethod));
    }

    private static Harmony Writer(string id)
    {
        if (!Writers.TryGetValue(id, out var writer)) Writers.Add(id, writer = new Harmony(id));
        return writer;
    }

    private static void Validate(string owner, MethodBase target)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128) throw new ArgumentException("native-hook-declaration-invalid owner");
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (target.IsAbstract || target.ContainsGenericParameters) throw new NotSupportedException("native-hook-target-declaration-invalid");
    }

    private static string Signature(IReadOnlyList<HookPart> parts)
    {
        var text = new StringBuilder();
        void Add(string phase, HarmonyMethod? method)
        {
            if (method == null) return;
            text.Append(phase).Append(':').Append(method.method.DeclaringType?.FullName).Append('.').Append(method.method.Name)
                .Append('@').Append(method.priority).Append('<').Append(string.Join(",", method.before ?? Array.Empty<string>()))
                .Append('>').Append(string.Join(",", method.after ?? Array.Empty<string>())).Append(';');
        }
        foreach (var part in parts) { Add("pre", part.Prefix); Add("post", part.Postfix); Add("fin", part.Finalizer); text.Append('|'); }
        return text.ToString();
    }

    internal static string Name(MethodBase target) => target.DeclaringType?.FullName + "." + target.Name;

    private sealed class Removal : IDisposable
    {
        private Action? _remove;
        internal Removal(Action remove) => _remove = remove;
        public void Dispose()
        {
            if (_remove == null) return;
            _remove();
            _remove = null;
        }
    }
}
