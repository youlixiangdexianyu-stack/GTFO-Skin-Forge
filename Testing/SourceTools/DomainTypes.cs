using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ForgeTestInputs;

/// <summary>Splits the compiled engine into one domain's two halves. ForgeEngine.dll holds the kernel, the host and the four
/// domains; ForgeEngine.Framework.dll holds the same sources without the game-touching `Native` folders. A domain's game-touching
/// half is therefore the domain's types in the engine that the Framework build does not contain, and its game-free half is the
/// domain's types in the Framework build. A domain owns the namespace `Forge&lt;Domain&gt;` and everything below it.</summary>
internal static class DomainTypes
{
    private static IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));

    private static bool Owns(string domain, string ns) => ns == "Forge" + domain || ns.StartsWith("Forge" + domain + ".", StringComparison.Ordinal);

    /// <summary>The domain's game-free types, nested types included, from the Framework build.</summary>
    internal static TypeDefinition[] Managed(AssemblyDefinition framework, string domain)
        => framework.MainModule.Types.Where(t => Owns(domain, t.Namespace)).SelectMany(Walk).ToArray();

    /// <summary>The domain's game-touching types, nested types included, from the engine. <paramref name="extra"/> names types of
    /// the domain's `Native` folder that sit in another namespace.</summary>
    internal static TypeDefinition[] Native(AssemblyDefinition engine, AssemblyDefinition framework, string domain, params string[] extra)
    {
        var free = framework.MainModule.Types.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        return engine.MainModule.Types.Where(t => (Owns(domain, t.Namespace) || extra.Contains(t.FullName)) && !free.Contains(t.FullName))
            .SelectMany(Walk).ToArray();
    }
}
