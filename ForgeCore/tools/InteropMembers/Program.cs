using Mono.Cecil;
using Mono.Cecil.Cil;

// Run by Tools/Forge/check-interop-members.py, which says what the check is for.
// usage: InteropMembers [--hot <hook,hook> <transition,transition>] <interop dir> <resolve dir;resolve dir> <dll>...
// --hot also counts the interop getters on the hot paths (HotGetters.cs), the named GameHooks entries being the hot ones and the named transitions not walked into.
string[]? hotHooks = null, hotTransitions = null;
if (args.Length > 2 && args[0] == "--hot") { hotHooks = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries); hotTransitions = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries); args = args[3..]; }
var interop = args[0];
var resolver = new DefaultAssemblyResolver();
foreach (var dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
resolver.AddSearchDirectory(interop);
foreach (var dir in args[1].Split(';', StringSplitOptions.RemoveEmptyEntries)) resolver.AddSearchDirectory(dir);
// A game's UnityPlayer.dll, when given: every engine call name an interop body resolves must be in it. Without it those names stay unread, and the run says so.
var playerPath = Environment.GetEnvironmentVariable("FORGE_UNITY_PLAYER");
var player = string.IsNullOrEmpty(playerPath) ? null : File.ReadAllBytes(playerPath);
var scanner = new Scanner(interop, new ReaderParameters { AssemblyResolver = resolver }, player);

// A check that cannot catch the broken case proves nothing: a built-in broken assembly runs first.
var fixture = Fixture.Write(interop, resolver);
var broken = new Scanner(interop, new ReaderParameters { AssemblyResolver = resolver }, Array.Empty<byte>());
broken.Scan(fixture);
File.Delete(fixture);
if (broken.Direct.Count != 1 || !broken.Direct.Keys.Single().Contains(Fixture.Missing)
    || broken.Indirect.Count != 2 || !broken.Indirect.Keys.Any(k => k.Contains("get_loop"))
    || !broken.Indirect.Keys.Any(k => k.Contains("icall " + Fixture.Icall)))
{
    Console.WriteLine("FAIL detection: the broken fixture gave " + broken.Direct.Count + " direct and " + broken.Indirect.Count + " indirect, expected one direct, the stripped get_loop and the absent engine call.");
    return 2;
}
var patchFixture = PatchFixture.Write(interop, resolver);
var brokenPatches = new PatchTargets(new ReaderParameters { AssemblyResolver = resolver });
brokenPatches.Scan(patchFixture);
File.Delete(patchFixture);
if (brokenPatches.Findings.Count != 3 || !brokenPatches.Findings.Keys.Any(k => k.Contains(PatchFixture.Wrong)) || !brokenPatches.Findings.Keys.Any(k => k.Contains(PatchFixture.Ambiguous))
    || !brokenPatches.Findings.Keys.Any(k => k.Contains(PatchFixture.Absent)) || brokenPatches.Verified != 2)
{
    Console.WriteLine("FAIL detection: the broken patch fixture gave " + brokenPatches.Findings.Count + " findings and " + brokenPatches.Verified + " verified targets, expected a wrong parameter name, an overload without argument types and an absent method, and two targets read.");
    return 2;
}
Console.WriteLine("detection: the broken patch fixture is caught (a parameter name the target lacks, an overloaded target without argument types, an absent target; the correct patch not reported)");
Console.WriteLine("detection: the broken fixture is caught (a missing member, a stripped body, an engine call the player lacks; the valid and standard library calls not reported)");
if (player == null) Console.WriteLine("engine call names not checked: no UnityPlayer.dll given");

foreach (var dll in args.Skip(2)) scanner.Scan(dll);
var patches = new PatchTargets(new ReaderParameters { AssemblyResolver = resolver });
foreach (var dll in args.Skip(2)) patches.Scan(dll);
Console.WriteLine("scanned " + (args.Length - 2) + " assemblies: " + scanner.Direct.Count + " direct, " + scanner.Indirect.Count + " indirect");
Report("DIRECT", scanner.Direct);
Report("INDIRECT", scanner.Indirect);
Console.WriteLine("patches: " + patches.Verified + " patch targets read and checked (" + patches.Manual + " by named helper, " + patches.ObserverClasses + " observer classes), " + patches.Unresolved + " found at run time or not resolvable here and not judged, " + patches.Findings.Count + " findings");
Report("PATCH", patches.Findings);
if (hotHooks != null && !HotGetters.Run(scanner, new ReaderParameters { AssemblyResolver = resolver }, hotHooks, hotTransitions!, interop, resolver, args.Skip(2).ToArray())) return 2;
return scanner.Direct.Count + scanner.Indirect.Count + patches.Findings.Count > 0 ? 1 : 0;

static void Report(string title, SortedDictionary<string, SortedSet<string>> found)
{
    foreach (var (member, sites) in found)
    {
        Console.WriteLine(title + " " + member);
        foreach (var site in sites) Console.WriteLine("    at " + site);
    }
}

internal sealed class Scanner
{
    /// <summary>Members a Forge assembly names that the interop does not have.</summary>
    internal readonly SortedDictionary<string, SortedSet<string>> Direct = new(StringComparer.Ordinal);
    /// <summary>Interop methods a Forge assembly calls whose managed body (Il2CppInterop's unstripped legacy properties) names a member that does not exist.</summary>
    internal readonly SortedDictionary<string, SortedSet<string>> Indirect = new(StringComparer.Ordinal);
    private readonly ReaderParameters _parameters;
    private readonly HashSet<string> _interop;
    private readonly Dictionary<string, List<string>> _walked = new(StringComparer.Ordinal);
    private readonly byte[]? _player;
    private readonly Dictionary<string, bool> _inPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _icalls = new(StringComparer.Ordinal);

    internal Scanner(string interop, ReaderParameters parameters, byte[]? player)
    {
        _parameters = parameters;
        _player = player;
        _interop = new HashSet<string>(Directory.GetFiles(interop, "*.dll").Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
    }

    internal void Scan(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, _parameters);
        var label = assembly.Name.Name;
        foreach (var type in All(assembly.MainModule.Types))
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var site = label + " " + type.FullName + "." + method.Name;
                foreach (var instruction in method.Body.Instructions)
                {
                    switch (instruction.Operand)
                    {
                        case TypeReference named:
                            if (IsInterop(named) && Resolve(named) == null) Add(Direct, named.FullName, site);
                            break;
                        case MethodReference or FieldReference:
                            var member = (MemberReference)instruction.Operand;
                            if (member.DeclaringType == null || !IsInterop(member.DeclaringType)) break;
                            var resolved = Resolve(member);
                            if (resolved == null) { Add(Direct, member.FullName, site); break; }
                            if (resolved is MethodDefinition called)
                                foreach (var missing in Walk(called))
                                    Add(Indirect, called.DeclaringType.FullName + "." + called.Name + " [" + missing + "]", site);
                            break;
                    }
                }
            }
    }

    private List<string> Walk(MethodDefinition method)
    {
        if (_walked.TryGetValue(method.FullName, out var known)) return known;
        var found = new List<string>();
        _walked[method.FullName] = found;
        if (!method.HasBody) return found;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not (MethodReference or FieldReference)) continue;
            var member = (MemberReference)instruction.Operand;
            if (member.DeclaringType == null || !IsInterop(member.DeclaringType)) continue;
            var resolved = Resolve(member);
            if (resolved == null) found.Add(member.FullName);
            else if (resolved is MethodDefinition next) found.AddRange(Walk(next));
            else if (_player != null && resolved is FieldDefinition field && IcallName(field) is { } name && !InPlayer(name)) found.Add("icall " + name);
        }
        return found;
    }

    /// <summary>The engine call a body's delegate field holds: Il2CppInterop's unstripped bodies call a static delegate that the declaring type's static constructor resolves by name.</summary>
    private string? IcallName(FieldDefinition field)
    {
        if (!field.IsStatic) return null;
        var type = field.DeclaringType;
        if (!_icalls.TryGetValue(type.FullName, out var names))
        {
            _icalls[type.FullName] = names = new Dictionary<string, string>(StringComparer.Ordinal);
            string? pending = null;
            var constructor = type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
            if (constructor != null)
                foreach (var instruction in constructor.Body.Instructions)
                {
                    if (instruction.Operand is string text) pending = text.Contains("::", StringComparison.Ordinal) ? text : null;
                    else if (instruction.OpCode == OpCodes.Stsfld && pending != null) { names[((FieldReference)instruction.Operand).Name] = pending; pending = null; }
                }
        }
        return names.TryGetValue(field.Name, out var name) ? name : null;
    }

    /// <summary>The name stands in the player as a whole C string.</summary>
    private bool InPlayer(string name)
    {
        if (!_inPlayer.TryGetValue(name, out var found))
            _inPlayer[name] = found = _player.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(name + "\0")) >= 0;
        return found;
    }

    internal bool IsInterop(TypeReference type)
    {
        // A multi-dimensional array's Get and Set come from the runtime, not from a definition.
        if (type is ArrayType) return false;
        while (type is TypeSpecification specification) type = specification.ElementType;
        if (type.IsGenericParameter) return false;
        var name = type.Scope is ModuleDefinition module ? module.Assembly.Name.Name : type.Scope is AssemblyNameReference reference ? reference.Name : null;
        return name != null && _interop.Contains(name) && !IsStandardLibrary(name);
    }

    /// <summary>A plain library compiled against .NET (NVorbis) names the real standard library. The interop folder's netstandard.dll has the same name and only the game's few forwards,
    /// so its members look missing; the game's own runtime library is Il2Cppmscorlib, which stays checked.</summary>
    private static bool IsStandardLibrary(string assembly)
        => assembly is "netstandard" or "mscorlib" or "System" || assembly.StartsWith("System.", StringComparison.Ordinal);

    private static IMetadataTokenProvider? Resolve(MemberReference member)
    {
        try
        {
            return member switch
            {
                MethodReference method => method.Resolve(),
                FieldReference field => field.Resolve(),
                TypeReference type => type.Resolve(),
                _ => null,
            };
        }
        catch (AssemblyResolutionException) { return null; }
    }

    private static void Add(SortedDictionary<string, SortedSet<string>> table, string key, string site)
    {
        if (!table.TryGetValue(key, out var sites)) table[key] = sites = new SortedSet<string>(StringComparer.Ordinal);
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

/// <summary>A small assembly that makes the mistakes this check exists for: a call of the stripped ParticleSystem.loop (its interop body names a MainModule method that does not exist),
/// a call of a method the interop does not have at all, a call whose engine name the player lacks (scanned against an empty player), and a valid call and a standard library call that must not be reported.</summary>
internal static class Fixture
{
    internal const string Missing = "get_doesNotExist";
    internal const string Icall = "UnityEngine.ParticleSystem::get_isEmitting";

    internal static string Write(string interop, IAssemblyResolver resolver)
    {
        using var particles = AssemblyDefinition.ReadAssembly(Path.Combine(interop, "UnityEngine.ParticleSystemModule.dll"), new ReaderParameters { AssemblyResolver = resolver });
        var system = particles.MainModule.GetType("UnityEngine.ParticleSystem");
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("InteropMembersFixture", new Version(1, 0)), "InteropMembersFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Fixture", "Calls", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        var stripped = system.Methods.First(m => m.Name == "get_loop");
        var valid = system.Methods.First(m => m.Name == "get_isPlaying");
        var absent = new MethodReference(Missing, module.TypeSystem.Boolean, module.ImportReference(system)) { HasThis = true };
        var engine = system.Methods.First(m => m.Name == "get_isEmitting");
        var netstandard = new AssemblyNameReference("netstandard", new Version(2, 1, 0, 0));
        module.AssemblyReferences.Add(netstandard);
        var standard = new MethodReference("ToString", module.TypeSystem.String, new TypeReference("System", "NotThere", module, netstandard)) { HasThis = true };
        var index = 0;
        foreach (var call in new[] { module.ImportReference(stripped), module.ImportReference(valid), absent, module.ImportReference(engine), standard })
        {
            var method = new MethodDefinition("Call" + index++, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, call);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(method);
        }
        var path = Path.Combine(Path.GetTempPath(), "InteropMembersFixture-" + Environment.ProcessId + ".dll");
        assembly.Write(path);
        return path;
    }
}
