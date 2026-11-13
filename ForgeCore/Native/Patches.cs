using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ForgeCore;

/// <summary>Puts a module's Harmony patches on the first time the module is used (<see cref="Ensure"/>), never at startup. At
/// startup only <see cref="CheckTargets"/> runs: a module whose game method is missing logs one error and every later call to it
/// throws instead of running without its patch. A mod's own hooks that serve one rarely used feature use <see cref="Lazy"/> the same way.
/// A <see cref="Bracket{TCall}"/> puts on only the patch kinds its subscriptions need (<see cref="Set"/>).</summary>
public static class Patches
{
    private static readonly Harmony Harmony = new(Plugin.Id);
    private static readonly Dictionary<string, string> Missing = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Applied = new(StringComparer.Ordinal);

    /// <summary>Patches that go on the first time <see cref="LazyPatches.Ensure"/> is called (the first use of the feature they serve),
    /// so a session that never uses it never pays for them. Once on they stay: the game keeps a detour for a patched method after an
    /// unpatch, so taking them off would not make the method any cheaper. <paramref name="install"/> runs once with a Harmony instance
    /// for <paramref name="modId"/>; if it throws the error is reported once and the feature stays unpatched.</summary>
    public static LazyPatches Lazy(string modId, Action<Harmony> install)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        return new LazyPatches(modId, install ?? throw new ArgumentNullException(nameof(install)));
    }

    /// <summary>The patch classes of this assembly that <paramref name="groupOf"/> sorts into a group (null: not a lazy patch), put on group by
    /// group at first use (<see cref="LazyGroups{TGroup}.Ensure"/>) under the Harmony ID <paramref name="modId"/>. <paramref name="onFail"/> gets each
    /// class that could not be patched; the other classes of the group still go on. Once on they stay (the game keeps a detour for a patched method after an unpatch).</summary>
    internal static LazyGroups<TGroup> Groups<TGroup>(string modId, Func<Type, TGroup?> groupOf, Action<TGroup, Type, Exception> onFail) where TGroup : struct, Enum
        => new(modId, groupOf, onFail);

    private static Type[]? _ownTypes;

    /// <summary>This assembly's types, read once for every <see cref="LazyGroups{TGroup}"/> (the UI's and the audio's groups share one scan).</summary>
    internal static Type[] OwnTypes => _ownTypes ??= AccessTools.GetTypesFromAssembly(typeof(Patches).Assembly);
    internal static void CheckTargets()
    {
        // Assembly.GetType per loaded assembly, never AccessTools.TypeByName: that calls GetTypes on every assembly for every name,
        // and HarmonyX logs a ReflectionTypeLoadException warning for UnityEngine.CoreModule on each scan. The game's types are spread
        // over many interop assemblies (Modules-ASM, SNet_ASM, GameData-ASM ...), so every assembly is asked.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var loadedReferences = false;
        foreach (var (module, targets) in Modules.Patched)
            foreach (var target in targets)
            {
                Type? type = null;
                foreach (var assembly in assemblies)
                    if ((type = assembly.GetType(target.Type, false)) != null) break;
                // An interop assembly loads when something first touches it, and nothing may have touched this one yet
                // (UnityEngine.AnimationModule at startup). Every target's assembly is one this plugin references, so load those once and ask again.
                if (type == null && !loadedReferences)
                {
                    loadedReferences = true;
                    foreach (var reference in typeof(Patches).Assembly.GetReferencedAssemblies())
                    {
                        try { System.Reflection.Assembly.Load(reference); }
                        catch (Exception error) when (error is System.IO.IOException or BadImageFormatException) { }
                    }
                    assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var assembly in assemblies)
                        if ((type = assembly.GetType(target.Type, false)) != null) break;
                }
                // By name only: most targets are overloaded (Fire, ActivateState ...), so the method name is the whole check here; each patch class pins its exact overload.
                if (type != null && AccessTools.GetDeclaredMethods(type).Exists(m => m.Name == target.Method)) continue;
                Missing[module] = target.Type + "." + target.Method;
                Plugin.Logger.Error("core-module-unavailable-game", "ForgeCore module " + module + " is unavailable: the game has no " + target.Type + "." + target.Method + ".");
            }
    }

    /// <summary>The module's patches are on (first call puts them on); throws when the module is unavailable.</summary>
    internal static void Ensure(string module, Type patches)
    {
        if (Applied.Contains(module)) return;
        if (Missing.TryGetValue(module, out var method))
            throw new InvalidOperationException("ForgeCore module " + module + " is unavailable: " + method + " cannot be patched.");
        Harmony.PatchAll(patches);
        Applied.Add(module);
    }

    internal sealed record Fix(PatchKinds Kind, MethodInfo Method, MethodBase[] Targets);
    private static readonly Dictionary<string, PatchKinds> On = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, (Fix[] Fixes, PatchKinds Kinds, bool Carried)> Classes = new();

    /// <summary>Puts on exactly the <paramref name="wanted"/> kinds of <paramref name="patches"/> (its prefixes, postfixes and finalizers,
    /// each on the targets its [HarmonyPatch] names) and takes the others off; answers the kinds now on. A kind the class has no method
    /// of is not put on; a class whose postfix or finalizer reads the prefix's <c>__state</c> gets its prefix with either. Each change
    /// rebuilds the method's patched body (under Il2CppInterop also its trampoline, one generated method each time), so a bracket
    /// calls this only when its subscriptions change what it needs. Every patch passes the same prologue check as any other
    /// (<see cref="Hooks.Resolve"/>); a patch that fails takes this call's additions off again and throws.</summary>
    internal static PatchKinds Set(string module, Type patches, PatchKinds wanted)
    {
        var (fixes, kinds, carried) = Class(patches);
        wanted &= kinds;
        if (carried && (wanted & (PatchKinds.Postfix | PatchKinds.Finalizer)) != 0) wanted |= PatchKinds.Prefix;
        On.TryGetValue(module, out var on);
        if (wanted == on) return on;
        if ((wanted & ~on) != 0 && Missing.TryGetValue(module, out var method))
            throw new InvalidOperationException("ForgeCore module " + module + " is unavailable: " + method + " cannot be patched.");
        var added = new List<(MethodBase Target, MethodInfo Fix)>();
        try
        {
            foreach (var target in fixes.SelectMany(fix => fix.Targets).Distinct())
            {
                var processor = Harmony.CreateProcessor(target);
                var adds = 0;
                foreach (var fix in fixes)
                {
                    if ((wanted & ~on & fix.Kind) == 0 || Array.IndexOf(fix.Targets, target) < 0) continue;
                    var harmonyMethod = new HarmonyMethod(fix.Method);
                    _ = fix.Kind switch
                    {
                        PatchKinds.Prefix => processor.AddPrefix(harmonyMethod),
                        PatchKinds.Postfix => processor.AddPostfix(harmonyMethod),
                        _ => processor.AddFinalizer(harmonyMethod),
                    };
                    added.Add((target, fix.Method));
                    adds++;
                }
                if (adds != 0) processor.Patch();
            }
        }
        catch
        {
            foreach (var (target, fix) in added)
            {
                try { Harmony.Unpatch(target, fix); }
                catch (Exception cleanup) { Plugin.Logger.ErrorOnce("core-module-could-not-take-failed-patch", "ForgeCore module " + module + " could not take a failed patch off " + target.Name + ": " + cleanup); }
            }
            throw;
        }
        foreach (var fix in fixes)
            if ((on & ~wanted & fix.Kind) != 0)
                foreach (var target in fix.Targets) Harmony.Unpatch(target, fix.Method);
        On[module] = wanted;
        return wanted;
    }

    internal static (Fix[] Fixes, PatchKinds Kinds, bool Carried) Class(Type patches)
    {
        if (Classes.TryGetValue(patches, out var known)) return known;
        var fixes = new List<Fix>();
        var carried = false;
        var shared = HarmonyMethodExtensions.GetFromType(patches);
        foreach (var method in AccessTools.GetDeclaredMethods(patches))
        {
            var kind = method.IsDefined(typeof(HarmonyPrefix), false) ? PatchKinds.Prefix
                : method.IsDefined(typeof(HarmonyPostfix), false) ? PatchKinds.Postfix
                : method.IsDefined(typeof(HarmonyFinalizer), false) ? PatchKinds.Finalizer : PatchKinds.None;
            if (kind == PatchKinds.None) continue;
            if (kind != PatchKinds.Prefix && Array.Exists(method.GetParameters(), parameter => parameter.Name == "__state")) carried = true;
            fixes.Add(new Fix(kind, method, Targets(patches, HarmonyMethod.Merge(shared.Concat(HarmonyMethodExtensions.GetFromMethod(method)).ToList()))));
        }
        var kinds = PatchKinds.None;
        foreach (var fix in fixes) kinds |= fix.Kind;
        return Classes[patches] = (fixes.ToArray(), kinds, carried);
    }

    // Harmony's own reading of [HarmonyPatch]: a declared method (by argument types when named), a property getter, or the class's TargetMethods().
    private static MethodBase[] Targets(Type patches, HarmonyMethod info)
    {
        if (info.declaringType == null)
            return ((IEnumerable<MethodBase>)AccessTools.DeclaredMethod(patches, "TargetMethods").Invoke(null, null)!).ToArray();
        MethodBase? target = info.methodType == MethodType.Getter
            ? AccessTools.DeclaredPropertyGetter(info.declaringType, info.methodName)
            : info.argumentTypes == null ? AccessTools.DeclaredMethod(info.declaringType, info.methodName)
            : AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes);
        return new[] { target ?? throw new MissingMethodException(info.declaringType.FullName, info.methodName) };
    }
}

/// <summary>A mod's patch set that goes on at first use (<see cref="Patches.Lazy"/>).</summary>
public sealed class LazyPatches
{
    private readonly string _modId;
    private readonly Action<Harmony> _install;
    private bool _tried;

    internal LazyPatches(string modId, Action<Harmony> install) { _modId = modId; _install = install; }

    /// <summary>Puts the patches on the first time; every later call returns at once.</summary>
    public void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try { _install(new Harmony(_modId + ".lazy")); }
        catch (Exception error) { Log.For(_modId).ErrorOnce("core-hooks-could-not-be-put-feature", "Hooks of " + _modId + " could not be put on; the feature they serve stays unpatched: " + error); }
    }
}

/// <summary>This assembly's patch classes of one part (UI, audio) sorted into feature groups (<see cref="Patches.Groups{TGroup}"/>); a group goes on the first time its feature is used.</summary>
internal sealed class LazyGroups<TGroup> where TGroup : struct, Enum
{
    private readonly string _modId;
    private readonly Func<Type, TGroup?> _groupOf;
    private readonly Action<TGroup, Type, Exception> _onFail;
    private readonly HashSet<TGroup> _on = new();
    private Dictionary<TGroup, List<Type>>? _types;
    private Harmony? _harmony;

    internal LazyGroups(string modId, Func<Type, TGroup?> groupOf, Action<TGroup, Type, Exception> onFail)
    {
        _modId = modId; _groupOf = groupOf; _onFail = onFail;
    }

    /// <summary>The group's feature has been used (its patches are on, or were attempted).</summary>
    internal bool Has(TGroup group) => _on.Contains(group);

    /// <summary>Puts the group's patches on the first time (one set lookup on every later call). True when this call put on all of them;
    /// a class that cannot be patched goes to the failure callback and the others still go on.</summary>
    internal bool Ensure(TGroup group)
    {
        if (!_on.Add(group)) return false;
        _types ??= Scan();
        var all = true;
        if (_types.TryGetValue(group, out var types))
        {
            _harmony ??= new Harmony(_modId);
            foreach (var type in types)
            {
                try { _harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception error) { all = false; _onFail(group, type, error); }
            }
        }
        return all;
    }

    private Dictionary<TGroup, List<Type>> Scan()
    {
        var found = new Dictionary<TGroup, List<Type>>();
        foreach (var type in Patches.OwnTypes)
        {
            if (_groupOf(type) is not { } group) continue;
            if (!found.TryGetValue(group, out var list)) found[group] = list = new List<Type>();
            list.Add(type);
        }
        return found;
    }
}
