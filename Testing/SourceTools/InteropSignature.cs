#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeTestInputs;

/// <summary>What an interop assembly holds, independent of the run that generated it. Il2CppInterop writes a new MVID, PE timestamp and checksum on every
/// generation, so a whole-file hash names one profile's generation, not the game (three profiles of one build held three hashes and identical content). This
/// fingerprint covers everything else: every type, field, method, parameter, property and event with its attributes, layout, constant, custom attributes and IL.
/// Evidence keeps it as `signatureSha256`.</summary>
public static class InteropSignature
{
    public static string Sha256(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        return Sha256(assembly);
    }

    public static string Sha256(AssemblyDefinition assembly)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Lines(assembly)))));

    private static List<string> Lines(AssemblyDefinition assembly)
    {
        var lines = new List<string> { "A " + assembly.Name.FullName + Attributes(assembly) };
        foreach (var module in assembly.Modules)
        {
            lines.Add("R " + string.Join(",", module.AssemblyReferences.Select(r => r.FullName).OrderBy(n => n, StringComparer.Ordinal)));
            foreach (var type in module.Types) AddType(type, lines);
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void AddType(TypeDefinition type, List<string> lines)
    {
        lines.Add("T " + type.FullName + " " + (uint)type.Attributes + " base=" + type.BaseType?.FullName
            + " layout=" + (type.HasLayoutInfo ? type.PackingSize + "/" + type.ClassSize : "")
            + " if=" + string.Join(",", type.Interfaces.Select(i => i.InterfaceType.FullName).OrderBy(n => n, StringComparer.Ordinal))
            + " gp=" + GenericParameters(type) + Attributes(type));
        foreach (var field in type.Fields)
            lines.Add("F " + field.FullName + " " + (uint)field.Attributes + " offset=" + field.Offset
                + " const=" + (field.HasConstant ? field.Constant?.GetType().Name + ":" + field.Constant : "")
                + " init=" + Convert.ToHexString(field.InitialValue) + Attributes(field));
        foreach (var method in type.Methods)
            lines.Add("M " + method.FullName + " " + (uint)method.Attributes + " impl=" + (uint)method.ImplAttributes + " gp=" + GenericParameters(method)
                + " params=" + string.Join(",", method.Parameters.Select(p => p.Name + ":" + (uint)p.Attributes + Attributes(p))) + Attributes(method) + Body(method));
        foreach (var property in type.Properties)
            lines.Add("P " + type.FullName + "::" + property.FullName + " " + (uint)property.Attributes + Attributes(property));
        foreach (var @event in type.Events)
            lines.Add("E " + type.FullName + "::" + @event.FullName + " " + (uint)@event.Attributes + Attributes(@event));
        foreach (var nested in type.NestedTypes) AddType(nested, lines);
    }

    private static string GenericParameters(IGenericParameterProvider provider)
        => string.Join(",", provider.GenericParameters.Select(p => p.Name + ":" + (uint)p.Attributes + ":"
            + string.Join("&", p.Constraints.Select(c => c.ConstraintType.FullName).OrderBy(n => n, StringComparer.Ordinal))));

    // The raw blob needs no assembly resolved to read.
    private static string Attributes(ICustomAttributeProvider provider)
        => !provider.HasCustomAttributes ? "" : " attrs=" + string.Join(";", provider.CustomAttributes
            .Select(a => a.Constructor.FullName + "(" + Convert.ToHexString(a.GetBlob()) + ")").OrderBy(n => n, StringComparer.Ordinal));

    private static string Body(MethodDefinition method)
        => !method.HasBody ? "" : " locals=" + string.Join(";", method.Body.Variables.Select(v => v.VariableType.FullName))
            + " il=" + string.Join(";", method.Body.Instructions.Select(i => i.OpCode.Name + " " + Operand(i)));

    private static string Operand(Instruction instruction) => instruction.Operand switch
    {
        null => "",
        Instruction target => "@" + target.Offset,
        Instruction[] targets => string.Join(",", targets.Select(t => t.Offset)),
        VariableDefinition local => "v" + local.Index,
        ParameterDefinition parameter => "p" + parameter.Index,
        MemberReference member => member.FullName,
        var value => value.ToString() ?? "",
    };

    /// <summary>A lock that cannot catch the broken case proves nothing: each edit below goes to one real assembly, read fresh, and only the generation noise may
    /// leave the fingerprint unchanged. Returns what went wrong, or null.</summary>
    public static string? DetectionFailure(string assemblyPath)
    {
        string baseline = Sha256(assemblyPath);
        string? Fails(string name, bool mustChange, Action<AssemblyDefinition> edit)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            edit(assembly);
            return (Sha256(assembly) != baseline) == mustChange ? null : name + (mustChange ? " left the fingerprint unchanged" : " changed the fingerprint");
        }
        static TypeDefinition Host(AssemblyDefinition a) => a.MainModule.Types.First(t => t.Methods.Any(m => m.HasBody && m.Parameters.Count > 0 && m.Body.Instructions.Count > 1) && t.Fields.Count > 0);
        static MethodDefinition Method(AssemblyDefinition a) => Host(a).Methods.First(m => m.HasBody && m.Parameters.Count > 0 && m.Body.Instructions.Count > 1);
        return Fails("a new MVID", false, a => a.MainModule.Mvid = Guid.NewGuid())
            ?? Fails("a renamed method", true, a => Method(a).Name += "_renamed")
            ?? Fails("a changed parameter type", true, a => { var p = Method(a).Parameters[0]; p.ParameterType = a.MainModule.TypeSystem.Int32 == p.ParameterType ? a.MainModule.TypeSystem.Int64 : a.MainModule.TypeSystem.Int32; })
            ?? Fails("a removed field", true, a => Host(a).Fields.RemoveAt(0))
            ?? Fails("a changed body", true, a => Method(a).Body.Instructions.RemoveAt(0));
    }
}
