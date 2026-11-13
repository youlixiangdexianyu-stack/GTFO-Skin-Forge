using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ForgeCore;

/// <summary>What the Il2CppInterop bridge needs repaired for one by-reference parameter of a hooked native.</summary>
internal enum ByRefKind
{
    /// <summary>`out` of a shape the bridge can copy back exactly: the stock bridge incorrectly reads the caller's
    /// uninitialized slot on entry. The repair initializes a managed local to its default value and keeps an exact-width
    /// copy-back after the callback.</summary>
    Output,
    /// <summary>`ref` of a floating-point scalar or a blittable struct: the stock bridge reads the argument with a
    /// pointer-width load into a local of that type, which is not a valid conversion (a float or a struct is not a
    /// native int). The managed layout is the native layout, so the repair hands the callback the native address itself
    /// as its `ref`: no copy in, no copy back, and a write by the callback is a write to the native variable.</summary>
    Direct,
    /// <summary>`ref` of an Il2Cpp value type the native only reads: the stock bridge reads the first eight bytes of
    /// the struct as an object pointer. The repair hands the callback a boxed copy of the struct and writes nothing back.</summary>
    ValueRead
}

internal readonly record struct ByRefRepair(int Index, ByRefKind Kind);

/// <summary>The by-reference parameter shapes Runtime installs a hook on. The Il2CppInterop bridge that carries a
/// native call into managed callbacks (`Il2CppDetourMethodPatcher.GenerateNativeToManagedTrampoline`) is correct for
/// reference-type arguments and, in the low bytes it keeps, for integer-width scalars and enums; it is wrong for
/// floating-point scalars, for blittable structs, for outputs and for Il2Cpp value types. Outputs and the first two are
/// repaired by structure; an Il2Cpp value type is admitted only for the
/// exact targets listed here, each reviewed against the native body to only read it. Every other by-reference shape is
/// refused when the declaration is read, before any native write. <see cref="HookBridge"/> repairs the bridge
/// generated for the admitted targets.</summary>
internal static class HookByRef
{
    private sealed record Reviewed(Type ReturnType, ByRefRepair[] Repairs);

    // Keyed by the exact target identity (declaring type, name, full parameter types).
    // These noise targets only read their `ref NM_NoiseData` (noiseMaker +0x0, position +0x10, type +0x28 of a
    // 0x30-byte struct): ForgeDevelopment/evidence/h10-noise-bodies-20403457.json. ListenerDetectNoise forwards to
    // OnNoiseDetected, whose base body only logs; ProcessNode writes maxRays, not noiseData. SetNoiseTarget reads
    // noiseMaker and returns a bool. Keep that return shape reviewed as well as the exact parameter identity.
    private static readonly Dictionary<string, Reviewed> Table = new(StringComparer.Ordinal)
    {
        ["Enemies.EB_Hibernating.OnNoiseDetected(NM_NoiseData&,System.Single)"] = new(typeof(void), new[] { new ByRefRepair(0, ByRefKind.ValueRead) }),
        ["Enemies.EB_Patrolling.OnNoiseDetected(NM_NoiseData&,System.Single)"] = new(typeof(void), new[] { new ByRefRepair(0, ByRefKind.ValueRead) }),
        ["Enemies.EB_StateBase.OnNoiseDetected(NM_NoiseData&,System.Single)"] = new(typeof(void), new[] { new ByRefRepair(0, ByRefKind.ValueRead) }),
        ["Enemies.EnemyAgent.ListenerDetectNoise(NM_NoiseData&,System.Single)"] = new(typeof(void), new[] { new ByRefRepair(0, ByRefKind.ValueRead) }),
        ["NoiseManager.ProcessNode(AIGraph.AIG_CourseNode,NM_NoiseData&,System.Boolean,System.Int32&)"] = new(typeof(void), new[] { new ByRefRepair(1, ByRefKind.ValueRead) }),
        ["Enemies.EB_Hibernating.SetNoiseTarget(NM_NoiseData&)"] = new(typeof(bool), new[] { new ByRefRepair(0, ByRefKind.ValueRead) }),
        ["Enemies.EB_Patrolling.SetNoiseTarget(NM_NoiseData&)"] = new(typeof(bool), new[] { new ByRefRepair(0, ByRefKind.ValueRead) })
    };

    /// <summary>The repairs the target's bridge needs, in parameter order; empty when the stock bridge is correct for every
    /// by-reference parameter. Throws <see cref="NotSupportedException"/> with the native-hook-* reason for an unsupported shape.</summary>
    internal static IReadOnlyList<ByRefRepair> Repairs(MethodBase target)
    {
        var parameters = target.GetParameters();
        if (!parameters.Any(parameter => parameter.ParameterType.IsByRef)) return Array.Empty<ByRefRepair>();
        var identity = target.DeclaringType?.FullName + "." + target.Name + "("
            + string.Join(",", parameters.Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)) + ")";
        var reviewed = Table.TryGetValue(identity, out var row) ? row.Repairs : Array.Empty<ByRefRepair>();
        var repairs = new List<ByRefRepair>();
        foreach (var parameter in parameters)
        {
            if (!parameter.ParameterType.IsByRef) continue;
            var element = parameter.ParameterType.GetElementType()!;
            var named = Array.FindIndex(reviewed, entry => entry.Index == parameter.Position);
            if (named >= 0)
            {
                if (target is not MethodInfo method || method.ReturnType != row!.ReturnType
                    || !Matches(reviewed[named].Kind, parameter, element)) throw Unsupported(parameter, "reviewed-shape-changed");
                repairs.Add(reviewed[named]);
            }
            else if (parameter.IsOut)
            {
                if (!SupportsOutput(element)) throw Unsupported(parameter, "unreviewed");
                repairs.Add(new ByRefRepair(parameter.Position, ByRefKind.Output));
            }
            else if (IsDirect(element)) repairs.Add(new ByRefRepair(parameter.Position, ByRefKind.Direct));
            else if (!StockBridgeIsCorrect(element)) throw Unsupported(parameter, "unreviewed");
        }
        return repairs;
    }

    private static bool Matches(ByRefKind kind, ParameterInfo parameter, Type element)
        => kind switch
        {
            ByRefKind.ValueRead => IsIl2CppValueType(element) && !parameter.IsOut,
            _ => false
        };

    // A floating-point scalar or a managed blittable struct (UnityEngine.Vector3 and the like): the managed layout is the
    // native layout, so the native address is a valid managed reference to it.
    private static bool IsDirect(Type element)
        => element == typeof(float) || element == typeof(double)
            || element.IsValueType && !element.IsPrimitive && !element.IsEnum && !IsIl2CppValueType(element) && IsBlittable(element);

    private static bool SupportsOutput(Type element)
        => !IsIl2CppValueType(element) && (IsDirect(element) || StockBridgeIsCorrect(element));

    private static bool IsBlittable(Type type)
        => type.IsPrimitive || type.IsEnum
            || type.IsValueType && !IsIl2CppValueType(type)
               && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(field => IsBlittable(field.FieldType));

    // Reference types go through `ldind.i` and a wrapper (correct). Integer-width scalars, `bool`, `char` and enums
    // are read pointer-width into a narrower local: the stock bridge over-reads but keeps the low bytes, and writes back
    // exactly the local's width. That is the status quo every installed hook of those shapes already stands on.
    private static bool StockBridgeIsCorrect(Type element)
        => element == typeof(string) || IsIl2CppObject(element) && !IsIl2CppValueType(element)
            || element.IsEnum || element.IsPrimitive && element != typeof(float) && element != typeof(double);

    private static bool IsIl2CppValueType(Type type) => Inherits(type, "Il2CppSystem.ValueType");
    private static bool IsIl2CppObject(Type type) => Inherits(type, "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");

    private static bool Inherits(Type type, string fullName)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
            if (current.FullName == fullName) return true;
        return false;
    }

    private static NotSupportedException Unsupported(ParameterInfo parameter, string cause)
        => new("native-hook-byref-shape-unsupported parameter=" + parameter.Position + " type=" + parameter.ParameterType.FullName
            + (parameter.IsOut ? " out" : " ref") + " cause=" + cause);
}
