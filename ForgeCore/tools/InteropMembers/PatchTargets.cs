using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>Offline check of every Harmony patch a Forge assembly declares, against the game's interop: the target resolves to exactly
/// one declared method, and each injected parameter of a prefix, postfix or finalizer names a real parameter of it (Harmony throws at
/// patch time otherwise, and the package that owns the patch fails to load; nothing before the game says so).
/// Read: [HarmonyPatch] classes and methods (class and method attributes merged as Harmony merges them; a single-pair TargetMethod
/// counts as a name), and the named-helper form of a manual patch (a call passing typeof(T), the target name and the hook name to a helper
/// that calls Harmony.Patch, the hook being a method of the helper's declaring type). A target found at run time (TargetMethods, a
/// lookup in a loop) is counted as unresolved and not judged. Classes put on through ForgeCore Hooks.ObserveClass are held to its two
/// extra rules: only methods carrying [HarmonyPrefix|Postfix|Finalizer] are patches, and one of each kind per target.</summary>
internal sealed class PatchTargets
{
    internal readonly SortedDictionary<string, SortedSet<string>> Findings = new(StringComparer.Ordinal);
    internal int Verified, Manual, Unresolved, ObserverClasses;
    private static readonly HashSet<string> Special = new(StringComparer.Ordinal)
        { "__instance", "__result", "__resultRef", "__state", "__originalMethod", "__args", "__runOriginal", "__exception" };
    private readonly ReaderParameters _parameters;

    internal PatchTargets(ReaderParameters parameters) { _parameters = parameters; }

    private sealed class Info
    {
        internal TypeReference? Declaring; internal string? Name; internal int? Type; internal TypeReference[]? Args; internal int[]? Variations;
        internal Info Over(Info other) => new()
        {
            Declaring = other.Declaring ?? Declaring,
            Name = other.Name ?? Name,
            Type = other.Type ?? Type,
            Args = other.Args ?? Args,
            Variations = other.Variations ?? Variations
        };
    }

    private enum Kind { None, Prefix, Postfix, Finalizer }

    internal void Scan(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, _parameters);
        var label = assembly.Name.Name;
        var types = All(assembly.MainModule.Types).ToList();
        var observers = ObservedClasses(types);
        foreach (var type in types)
        {
            var classInfo = Read(type.CustomAttributes);
            var observed = observers.Contains(type);
            if (observed) ObserverClasses++;
            var seen = new HashSet<(Kind, string)>();
            foreach (var method in type.Methods)
            {
                var kind = KindOf(method, out var named);
                if (kind == Kind.None) continue;
                var own = Read(method.CustomAttributes);
                if (classInfo == null && own == null) continue;
                var site = label + " " + type.FullName + "." + method.Name;
                if (observed && named) Add("ObserveClass ignores a patch method with no [HarmonyPrefix], [HarmonyPostfix] or [HarmonyFinalizer] attribute", site);
                var info = (classInfo ?? new Info()).Over(own ?? new Info());
                var targets = Targets(info, type, out var soft);
                if (targets == null) { Unresolved++; continue; }
                var resolved = new List<MethodDefinition>();
                foreach (var found in targets)
                {
                    if (found.Error != null) { if (!soft) Add(found.Error, site); else Unresolved++; continue; }
                    resolved.Add(found.Method!);
                }
                foreach (var target in resolved)
                {
                    Verified++;
                    CheckParameters(method, target, site);
                    if (observed && !seen.Add((kind, target.FullName))) Add("ObserveClass allows one " + kind + " per target: " + target.DeclaringType.Name + "." + target.Name, site);
                }
            }
        }
        foreach (var type in types)
            foreach (var method in type.Methods.Where(m => m.HasBody))
                ManualPatches(label, type, method);
    }

    private static Kind KindOf(MethodDefinition method, out bool byName)
    {
        byName = false;
        var names = method.CustomAttributes.Select(a => a.AttributeType.FullName).ToHashSet();
        if (names.Contains("HarmonyLib.HarmonyTranspiler") || names.Contains("HarmonyLib.HarmonyReversePatch") || names.Contains("HarmonyLib.HarmonyILManipulator")) return Kind.None;
        if (names.Contains("HarmonyLib.HarmonyPrefix")) return Kind.Prefix;
        if (names.Contains("HarmonyLib.HarmonyPostfix")) return Kind.Postfix;
        if (names.Contains("HarmonyLib.HarmonyFinalizer")) return Kind.Finalizer;
        byName = true;
        return method.Name switch { "Prefix" => Kind.Prefix, "Postfix" => Kind.Postfix, "Finalizer" => Kind.Finalizer, _ => Kind.None };
    }

    /// <summary>The merged [HarmonyPatch] attributes of one class or method, or null when it has none.</summary>
    private static Info? Read(IEnumerable<CustomAttribute> attributes)
    {
        Info? merged = null;
        foreach (var attribute in attributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
        {
            var info = new Info();
            for (var i = 0; i < attribute.ConstructorArguments.Count; i++)
            {
                var argument = attribute.ConstructorArguments[i];
                switch (attribute.Constructor.Parameters[i].ParameterType.FullName)
                {
                    case "System.Type": info.Declaring = argument.Value as TypeReference; break;
                    case "System.String": info.Name = argument.Value as string; break;
                    case "HarmonyLib.MethodType": info.Type = Convert.ToInt32(argument.Value); break;
                    case "System.Type[]": info.Args = (argument.Value as CustomAttributeArgument[])?.Select(a => (TypeReference)a.Value).ToArray(); break;
                    case "HarmonyLib.ArgumentType[]": info.Variations = (argument.Value as CustomAttributeArgument[])?.Select(a => Convert.ToInt32(a.Value)).ToArray(); break;
                }
            }
            merged = (merged ?? new Info()).Over(info);
        }
        return merged;
    }

    private sealed record Found(MethodDefinition? Method, string? Error);

    /// <summary>The methods the attributes name, or null when they are found at run time. soft: the name came from reading a TargetMethod body, so a miss there is not judged.</summary>
    private List<Found>? Targets(Info info, TypeDefinition patchClass, out bool soft)
    {
        soft = false;
        if (info.Declaring == null || info.Name == null && info.Type is not 3 and not 4)
        {
            var read = TargetMethodRead(patchClass);
            if (read == null) return null;
            soft = true;
            info = new Info { Declaring = read.Value.Type, Name = read.Value.Name };
        }
        return Resolve(info);
    }

    /// <summary>The one (typeof, name) pair a static TargetMethod() names, or null for anything else (TargetMethods, several pairs).</summary>
    private static (TypeReference Type, string Name)? TargetMethodRead(TypeDefinition patchClass)
    {
        var method = patchClass.Methods.SingleOrDefault(m => m.Name == "TargetMethod" && m.HasBody);
        if (method == null) return null;
        var pairs = new List<(TypeReference, string)>();
        TypeReference? last = null;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference type) last = type;
            else if (instruction.OpCode == OpCodes.Ldstr && last != null) { pairs.Add((last, (string)instruction.Operand)); last = null; }
        }
        return pairs.Count == 1 ? pairs[0] : null;
    }

    private List<Found>? Resolve(Info info)
    {
        TypeDefinition? declaring;
        try { declaring = info.Declaring!.Resolve(); }
        catch (AssemblyResolutionException) { declaring = null; }
        if (declaring == null) return null;
        var name = info.Type switch { 1 => "get_" + info.Name, 2 => "set_" + info.Name, 3 => ".ctor", 4 => ".cctor", _ => info.Name };
        if (info.Type is 5 or 6 || name == null) return null;
        var named = declaring.Methods.Where(m => m.Name == name).ToList();
        var shown = declaring.FullName + "." + name;
        if (named.Count == 0) return new() { new(null, "the target has no declared method " + shown) };
        if (info.Args == null)
            return named.Count == 1 ? new() { new(named[0], null) }
                : new() { new(null, "the target " + shown + " has " + named.Count + " overloads and the patch names no argument types (Harmony: AmbiguousMatchException)") };
        var wanted = info.Args.Select((a, i) => a.FullName + (info.Variations is { } v && i < v.Length ? v[i] switch { 1 or 2 => "&", 3 => "*", _ => "" } : "")).ToArray();
        var match = named.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted)).ToList();
        return match.Count == 1 ? new() { new(match[0], null) }
            : new() { new(null, "the target " + shown + " has no overload (" + string.Join(", ", wanted) + ")") };
    }

    private void CheckParameters(MethodDefinition patch, MethodDefinition target, string site)
    {
        foreach (var parameter in patch.Parameters)
        {
            var name = parameter.Name;
            var argument = parameter.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyArgument");
            if (argument != null && argument.ConstructorArguments.Count == 1)
            {
                if (argument.ConstructorArguments[0].Value is int index) { if (index < 0 || index >= target.Parameters.Count) Add("HarmonyArgument(" + index + ") is beyond the parameters of " + Show(target), site); continue; }
                name = argument.ConstructorArguments[0].Value as string ?? name;
            }
            if (name.StartsWith("___", StringComparison.Ordinal))
            {
                if (!HasField(target.DeclaringType, name[3..])) Add("injected field " + name + " is not a field of " + target.DeclaringType.FullName + " or its bases", site);
            }
            else if (name.StartsWith("__", StringComparison.Ordinal))
            {
                var tail = name[2..];
                if (Special.Contains(name)) continue;
                if (int.TryParse(tail, out var index) && index < target.Parameters.Count) continue;
                Add("unknown injected parameter " + name + " (target " + Show(target) + ")", site);
            }
            else if (target.Parameters.All(p => p.Name != name))
                Add("parameter '" + name + "' is not a parameter of the target " + Show(target), site);
        }
    }

    private static string Show(MethodDefinition method)
        => method.DeclaringType.FullName + "." + method.Name + "(" + string.Join(", ", method.Parameters.Select(p => p.ParameterType.Name + " " + p.Name)) + ")";

    private static bool HasField(TypeDefinition? type, string name)
    {
        for (var depth = 0; type != null && depth < 32; depth++)
        {
            if (type.Fields.Any(f => f.Name == name)) return true;
            try { type = type.BaseType?.Resolve(); }
            catch (AssemblyResolutionException) { return true; }
        }
        return false;
    }

    /// <summary>A call whose last three parameters are (Type, string, string) into a helper that calls Harmony.Patch, the three arguments
    /// being typeof(T), the target's name and the hook's name: the hook is a method of the helper's declaring type.</summary>
    private void ManualPatches(string label, TypeDefinition type, MethodDefinition caller)
    {
        var code = caller.Body.Instructions;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].Operand is not MethodReference callee || code[i].OpCode.Code is not (Code.Call or Code.Callvirt)) continue;
            var count = callee.Parameters.Count;
            if (count < 3 || !callee.Parameters.Skip(count - 3).Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Type", "System.String", "System.String" })) continue;
            MethodDefinition? helper;
            try { helper = callee.Resolve(); } catch (AssemblyResolutionException) { continue; }
            if (helper == null || !helper.HasBody || !helper.Body.Instructions.Any(x => x.Operand is MethodReference m && m.DeclaringType.FullName == "HarmonyLib.Harmony" && m.Name == "Patch")) continue;
            var before = new List<Instruction>();
            for (var j = i - 1; j >= 0 && before.Count < 4; j--) if (code[j].OpCode.Code != Code.Nop) before.Insert(0, code[j]);
            if (before.Count < 4 || before[0].OpCode.Code != Code.Ldtoken || before[2].OpCode.Code != Code.Ldstr || before[3].OpCode.Code != Code.Ldstr) continue;
            var site = label + " " + type.FullName + "." + caller.Name + " -> " + helper.Name;
            Manual++;
            var hooks = helper.DeclaringType.Methods.Where(m => m.Name == (string)before[3].Operand).ToList();
            if (hooks.Count != 1) { Add("the hook " + before[3].Operand + " is not exactly one method of " + helper.DeclaringType.FullName, site); continue; }
            var targets = Resolve(new Info { Declaring = (TypeReference)before[0].Operand, Name = (string)before[2].Operand });
            if (targets == null) { Unresolved++; continue; }
            foreach (var found in targets)
            {
                if (found.Error != null) { Add(found.Error, site); continue; }
                Verified++;
                CheckParameters(hooks[0], found.Method!, site);
            }
        }
    }

    /// <summary>The [HarmonyPatch] classes a method calling ForgeCore.Hooks.ObserveClass names: a typeof in that method or in its type's static constructor (the array of watchers).</summary>
    private static HashSet<TypeDefinition> ObservedClasses(List<TypeDefinition> types)
    {
        var observed = new HashSet<TypeDefinition>();
        foreach (var type in types)
            foreach (var method in type.Methods.Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "ObserveClass" && r.DeclaringType.Name == "Hooks")))
                foreach (var body in new[] { method }.Concat(type.Methods.Where(m => m.IsConstructor && m.IsStatic && m.HasBody)))
                    foreach (var instruction in body.Body.Instructions)
                        if (instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference named && Defined(named) is { } found && types.Contains(found)
                            && found.CustomAttributes.Concat(found.Methods.SelectMany(m => m.CustomAttributes)).Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                            observed.Add(found);
        return observed;
    }

    private static TypeDefinition? Defined(TypeReference type)
    {
        try { return type.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private void Add(string message, string site)
    {
        if (!Findings.TryGetValue(message, out var sites)) Findings[message] = sites = new SortedSet<string>(StringComparer.Ordinal);
        sites.Add(site);
    }

    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in All(type.NestedTypes)) yield return nested;
        }
    }
}

/// <summary>A small assembly of patches that make the mistakes this check exists for, against ParticleSystem: a postfix naming a parameter the target does not have,
/// a patch on an overloaded method with no argument types, a patch on a method that does not exist, and a correct patch (reached by its argument types) that must not be reported.</summary>
internal static class PatchFixture
{
    internal const string Wrong = "parameter 'wrongName' is not a parameter of the target";
    internal const string Ambiguous = "overloads and the patch names no argument types";
    internal const string Absent = "the target has no declared method UnityEngine.ParticleSystem.NoSuchMethod";

    internal static string Write(string interop, IAssemblyResolver resolver)
    {
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var particles = AssemblyDefinition.ReadAssembly(Path.Combine(interop, "UnityEngine.ParticleSystemModule.dll"), parameters);
        using var harmony = resolver.Resolve(new AssemblyNameReference("0Harmony", null));
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("PatchFixture", new Version(1, 0)), "PatchFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var system = module.ImportReference(particles.MainModule.GetType("UnityEngine.ParticleSystem"));
        var patchType = harmony.MainModule.GetType("HarmonyLib.HarmonyPatch");
        var byName = module.ImportReference(patchType.Methods.First(m => m.IsConstructor && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Type", "System.String" })));
        var byArguments = module.ImportReference(patchType.Methods.First(m => m.IsConstructor && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Type", "System.String", "System.Type[]" })));
        var postfix = module.ImportReference(harmony.MainModule.GetType("HarmonyLib.HarmonyPostfix").Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0));
        CustomAttribute Patch(string name, params TypeReference[] arguments)
        {
            var attribute = new CustomAttribute(arguments.Length == 0 ? byName : byArguments);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(attribute.Constructor.Parameters[0].ParameterType, system));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name));
            if (arguments.Length != 0)
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(attribute.Constructor.Parameters[2].ParameterType,
                    arguments.Select(a => new CustomAttributeArgument(attribute.Constructor.Parameters[0].ParameterType, a)).ToArray()));
            return attribute;
        }
        void Class(string className, CustomAttribute attribute, string parameterName)
        {
            var type = new TypeDefinition("Fixture", className, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Hook", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            if (parameterName != "") method.Parameters.Add(new ParameterDefinition(parameterName, ParameterAttributes.None, module.TypeSystem.Boolean));
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            method.CustomAttributes.Add(new CustomAttribute(postfix));
            method.CustomAttributes.Add(attribute);
            type.Methods.Add(method);
        }
        var boolean = module.TypeSystem.Boolean;
        Class("WrongName", Patch("Play", boolean), "wrongName");
        Class("Ambiguous", Patch("Stop"), "");
        Class("Absent", Patch("NoSuchMethod"), "");
        Class("Correct", Patch("Play", boolean), "withChildren");
        var path = Path.Combine(Path.GetTempPath(), "PatchFixture-" + Environment.ProcessId + ".dll");
        assembly.Write(path);
        return path;
    }
}
