using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// The interop getters on Forge's hot paths (contract P8), counted for the budget in Tools/Web/perf-budget.json. A hot path is
/// every method reachable by direct calls from a per-frame callback (ForgeCore <c>Frame.OnUpdate</c> or <c>OnLateUpdate</c>, a
/// MonoBehaviour's Update, LateUpdate or FixedUpdate) or from a subscription to one of the named high-frequency GameHooks entries.
/// Each call of an interop <c>get_</c> method in one of them is one site, counted under the assembly that holds it. A call through an
/// interface or abstract method is not followed, so the count is a floor. A method named in <c>transitions</c> (the budget file, each with why it runs once per
/// state change and not per frame) is not walked into; a name no scanned assembly has fails the run, so the list cannot go stale.
/// </summary>
internal sealed class HotGetters
{
    /// <summary>Assembly name to its sites, each "caller -> getter IL_offset".</summary>
    internal readonly SortedDictionary<string, SortedSet<string>> Sites = new(StringComparer.Ordinal);
    private readonly Scanner _interop;
    private readonly ReaderParameters _parameters;
    private readonly HashSet<string> _hooks;
    private readonly HashSet<string> _transitions;
    private readonly HashSet<string> _transitionsFound = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assemblies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly Queue<MethodDefinition> _pending = new();

    private HotGetters(Scanner interop, ReaderParameters parameters, IEnumerable<string> hooks, IEnumerable<string> transitions)
    {
        _interop = interop;
        _parameters = parameters;
        _hooks = new HashSet<string>(hooks, StringComparer.Ordinal);
        _transitions = new HashSet<string>(transitions, StringComparer.Ordinal);
    }

    /// <summary>Scans a built-in fixture first and fails unless it is caught, then prints each assembly's count and sites.</summary>
    internal static bool Run(Scanner interop, ReaderParameters parameters, string[] hooks, string[] transitions, string interopDir, IAssemblyResolver resolver, string[] dlls)
    {
        var core = dlls.FirstOrDefault(d => Path.GetFileName(d).Equals("ForgeCore.dll", StringComparison.OrdinalIgnoreCase));
        if (core == null) { Console.WriteLine("FAIL hot getters: ForgeCore.dll is not among the scanned assemblies, so the fixture cannot be built."); return false; }
        var fixture = WriteFixture(interopDir, core, resolver);
        var probe = new HotGetters(interop, parameters, hooks, new[] { "Fixture.Hot.OneShot" });
        probe.Scan(new[] { fixture });
        var stale = new HotGetters(interop, parameters, hooks, new[] { "Fixture.Hot.Gone" });
        stale.Scan(new[] { fixture });
        File.Delete(fixture);
        var found = probe.Sites.SelectMany(pair => pair.Value).ToList();
        if (found.Count != 2 || found.Any(site => site.Contains(".Cold ", StringComparison.Ordinal) || site.Contains(".OneShot ", StringComparison.Ordinal)) || stale.Stale.Count != 1 || !found.Any(site => site.Contains(".Helper ", StringComparison.Ordinal)))
        {
            Console.WriteLine("FAIL hot getters detection: the fixture gave " + found.Count + " sites (" + string.Join("; ", found) + "), expected the frame callback's and its callee's, not the cold method's nor the transition's, and the stale transition name reported.");
            return false;
        }
        Console.WriteLine("hot getters detection: the fixture is caught (a getter in a frame callback and one in its callee; the cold method's and the transition's not counted, a stale transition name reported)");
        var scan = new HotGetters(interop, parameters, hooks, transitions);
        scan.Scan(dlls);
        if (scan.Stale.Count != 0)
        {
            Console.WriteLine("FAIL hot getters: transitions name methods no scanned assembly has: " + string.Join(", ", scan.Stale));
            return false;
        }
        foreach (var (assembly, sites) in scan.Sites)
        {
            Console.WriteLine("HOTGETTERS " + assembly + " " + sites.Count);
            foreach (var site in sites) Console.WriteLine("    at " + site);
        }
        return true;
    }

    private List<string> Stale => _transitions.Except(_transitionsFound).ToList();

    private void Scan(IEnumerable<string> paths)
    {
        var assemblies = paths.Select(path => AssemblyDefinition.ReadAssembly(path, _parameters)).ToList();
        try
        {
            foreach (var assembly in assemblies) _assemblies.Add(assembly.Name.Name);
            foreach (var assembly in assemblies)
                foreach (var type in All(assembly.MainModule.Types))
                {
                    var behaviour = IsMonoBehaviour(type);
                    foreach (var method in type.Methods)
                    {
                        if (_transitions.Contains(type.FullName + "." + method.Name)) _transitionsFound.Add(type.FullName + "." + method.Name);
                        if (!method.HasBody) continue;
                        if (behaviour && method.Name is "Update" or "LateUpdate" or "FixedUpdate") Enqueue(method);
                        var instructions = method.Body.Instructions;
                        for (var i = 0; i < instructions.Count; i++)
                            if (instructions[i].OpCode.Code is Code.Call or Code.Callvirt && instructions[i].Operand is MethodReference called)
                                foreach (var root in Registered(called, instructions, i)) Enqueue(Resolve(root));
                    }
                }
            while (_pending.Count > 0) Walk(_pending.Dequeue());
        }
        finally { foreach (var assembly in assemblies) assembly.Dispose(); }
    }

    /// <summary>The callbacks a call registers when it is a frame callback or a subscription to a hot GameHooks entry: the delegates
    /// built between the call and its receiver (a cached lambda builds its delegate with no other call in between).</summary>
    private IEnumerable<MethodReference> Registered(MethodReference called, IList<Instruction> instructions, int at)
    {
        var owner = called.DeclaringType.GetElementType().FullName;
        var frame = owner == "ForgeCore.Frame" && called.Name is "OnUpdate" or "OnLateUpdate";
        var bracket = owner is "ForgeCore.Bracket`1" or "ForgeCore.RayBracket" or "ForgeCore.GameHooks/RayBracket"
            && called.Name is "Before" or "After" or "Around" or "Skip" && called.ReturnType.Name == "IDisposable";
        if (!frame && !bracket) yield break;
        var start = at - 1;
        if (bracket)
        {
            // The entry is the GameHooks field the receiver was loaded from; a bracket held anywhere else is not judged.
            while (start >= 0 && !(instructions[start].OpCode.Code == Code.Ldsfld && instructions[start].Operand is FieldReference field && field.DeclaringType.FullName.StartsWith("ForgeCore.GameHooks", StringComparison.Ordinal)))
            {
                if (instructions[start].OpCode.Code is Code.Call or Code.Callvirt) yield break;
                start--;
            }
            if (start < 0 || !_hooks.Contains(HookName((FieldReference)instructions[start].Operand))) yield break;
        }
        for (var i = at - 1; i >= 0 && i > (bracket ? start : -1); i--)
        {
            var instruction = instructions[i];
            if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn) yield return (MethodReference)instruction.Operand;
            else if (!bracket && instruction.OpCode.Code is Code.Call or Code.Callvirt) yield break;
        }
    }

    private static string HookName(FieldReference field)
        => field.DeclaringType.FullName["ForgeCore.".Length..].Replace('/', '.') + "." + field.Name;

    private void Walk(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference called) continue;
            if (called.DeclaringType != null && _interop.IsInterop(called.DeclaringType))
            {
                if (called.Name.StartsWith("get_", StringComparison.Ordinal))
                    Add(method.Module.Assembly.Name.Name, method.DeclaringType.FullName + "." + method.Name + " -> " + called.DeclaringType.FullName + "." + called.Name + " IL_" + instruction.Offset.ToString("x4"));
                continue;
            }
            Enqueue(Resolve(called));
        }
    }

    private void Enqueue(MethodDefinition? method)
    {
        if (method is not { HasBody: true } || !_assemblies.Contains(method.Module.Assembly.Name.Name) || _transitions.Contains(method.DeclaringType.FullName + "." + method.Name)) return;
        if (_visited.Add(method.Module.Assembly.Name.Name + " " + method.FullName)) _pending.Enqueue(method);
    }

    private void Add(string assembly, string site)
    {
        if (!Sites.TryGetValue(assembly, out var sites)) Sites[assembly] = sites = new SortedSet<string>(StringComparer.Ordinal);
        sites.Add(site);
    }

    private static MethodDefinition? Resolve(MethodReference method)
    {
        try { return method.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static bool IsMonoBehaviour(TypeDefinition type)
    {
        try
        {
            for (var at = type.BaseType; at != null; at = at.Resolve()?.BaseType)
                if (at.FullName == "UnityEngine.MonoBehaviour") return true;
            return false;
        }
        catch (AssemblyResolutionException) { return false; } // A base type in an assembly we do not load cannot be one we scan.
    }

    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in All(type.NestedTypes)) yield return nested;
        }
    }

    /// <summary>A frame callback that reads ParticleSystem.isPlaying and calls a helper that reads it too and calls a transition that reads it, and a cold
    /// method that reads it without being reached: two sites, none in the cold method or the transition.</summary>
    private static string WriteFixture(string interop, string core, IAssemblyResolver resolver)
    {
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var particles = AssemblyDefinition.ReadAssembly(Path.Combine(interop, "UnityEngine.ParticleSystemModule.dll"), parameters);
        using var forge = AssemblyDefinition.ReadAssembly(core, parameters);
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("HotGettersFixture", new Version(1, 0)), "HotGettersFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Fixture", "Hot", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        var getter = module.ImportReference(particles.MainModule.GetType("UnityEngine.ParticleSystem").Methods.First(m => m.Name == "get_isPlaying"));
        var onUpdate = module.ImportReference(forge.MainModule.GetType("ForgeCore.Frame").Methods.First(m => m.Name == "OnUpdate"));
        var action = module.ImportReference(typeof(Action).GetConstructor(new[] { typeof(object), typeof(IntPtr) }));
        MethodDefinition Reader(string name, MethodDefinition? then)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Pop);
            if (then != null) il.Emit(OpCodes.Call, then);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(method);
            return method;
        }
        var tick = Reader("Tick", Reader("Helper", Reader("OneShot", null)));
        Reader("Cold", null);
        var register = new MethodDefinition("Register", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        var code = register.Body.GetILProcessor();
        code.Emit(OpCodes.Ldstr, "fixture");
        code.Emit(OpCodes.Ldnull);
        code.Emit(OpCodes.Ldftn, tick);
        code.Emit(OpCodes.Newobj, action);
        code.Emit(OpCodes.Call, onUpdate);
        code.Emit(OpCodes.Ret);
        type.Methods.Add(register);
        var path = Path.Combine(Path.GetTempPath(), "HotGettersFixture-" + Environment.ProcessId + ".dll");
        assembly.Write(path);
        return path;
    }
}
