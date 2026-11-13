using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Code = Mono.Cecil.Cil.Code;

namespace ForgeCore;

/// <summary>Repairs the Il2CppInterop native-to-managed bridge for the by-reference shapes <see cref="HookByRef"/>
/// reviews. The stock generator (`Il2CppDetourMethodPatcher.GenerateNativeToManagedTrampoline`, the pinned
/// Il2CppInterop.HarmonySupport build) reads every by-reference argument with a pointer-width `ldind.i`. For a
/// `float` or `double` that is not a valid conversion and the hook's first call fails; for a blittable struct it is a
/// pointer-width load into a struct local; for a `ref` Il2Cpp value type it wraps the first eight bytes of the struct as an
/// object pointer and, after the call, stores that wrapper pointer over them.
///
/// ForgeCore's <c>Hooks</c> is the one installer, so it is also the one place that changes the bridge: before it patches a target
/// whose by-reference shape needs it, a Harmony postfix on the generator edits the generated Cecil body of exactly the targets prepared
/// here. This is the only place Forge reaches into a non-public member of the hooking backend, and only of the one pinned
/// Il2CppInterop.HarmonySupport build (<see cref="HookBridgeBackend"/>); with any other build such a target is refused
/// (native-hook-bridge-backend-unavailable) and every other target patches as usual. Each edit first proves the generated body has the
/// shape the pinned generator emits and throws otherwise, so a changed backend fails loudly instead of running a bridge nobody reviewed.
///   - Output: the input read is removed, the managed local is initialized to default and the callback's value is copied
///     back at the element's actual width.
///   - Direct: the read, the local and the copy-back are removed, so the callback's `ref` is the native address itself.
///   - ValueRead: the read becomes a boxed copy of the struct (`il2cpp_value_box`, the same conversion the generator
///     uses for a by-value struct argument) and the copy-back is removed, so a callback reads the values the native
///     passed and the native's struct is never written. Writes the callback makes land in its copy only.</summary>
// patch-targets: Il2CppDetourMethodPatcher.GenerateNativeToManagedTrampoline, Il2CppDetourMethodPatcher.CopyOriginal (resolved through HookBridgeBackend by reflection)
internal static class HookBridge
{
    private static readonly object Gate = new();
    private static readonly HashSet<MethodBase> Targets = new();
    private static readonly ConditionalWeakTable<DynamicMethodDefinition, object> Repaired = new();
    private static Harmony? _writer;

    /// <summary>Called before <paramref name="target"/> is patched: a target whose by-reference shape needs the repair is
    /// registered for it (the generator observer goes on the first time); any other target is left alone. Throws
    /// <see cref="NotSupportedException"/> with the native-hook-* reason for an unsupported shape or an unpinned backend.</summary>
    internal static void Prepare(MethodBase target)
    {
        if (!Il2Cpp(target)) return;
        var repairs = HookByRef.Repairs(target);
        if (repairs.Count == 0) return;
        lock (Gate)
        {
            if (_writer == null)
            {
                var generator = HookBridgeBackend.Generator() ?? throw new NotSupportedException("native-hook-bridge-backend-unavailable");
                var writer = new Harmony(Plugin.Id + ".bridge");
                writer.Patch(generator, postfix: new HarmonyMethod(typeof(HookBridge), nameof(AfterBridge)));
                _writer = writer;
            }
            Targets.Add(target);
            if (!_copierPatched && repairs.Any(repair => repair.Kind == ByRefKind.ValueRead))
            {
                // The patched method calls the native original through a copy of the interop wrapper, which hands a
                // by-reference value type to the invoker as the boxed object. Without this the native reads the struct
                // 0x10 bytes early (the object header), and a body that dereferences a field faults.
                var copier = HookBridgeBackend.OriginalCopier() ?? throw new NotSupportedException("native-hook-bridge-backend-unavailable");
                _writer.Patch(copier, postfix: new HarmonyMethod(typeof(HookBridge), nameof(AfterCopy)));
                _copierPatched = true;
            }
        }
    }

    private static bool _copierPatched;

    /// <summary>True for a generated interop method (one with a native entry); a managed method has no Il2Cpp bridge to repair.</summary>
    internal static bool Il2Cpp(MethodBase target) => Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target) != null;

    private static void AfterCopy(object __instance, DynamicMethodDefinition __result)
    {
        var original = __instance.GetType().GetProperty("Original")?.GetValue(__instance) as MethodBase
            ?? throw new InvalidOperationException("native-hook-bridge-target-unavailable");
        bool prepared;
        lock (Gate) prepared = Targets.Contains(original);
        if (prepared) RepairOriginalCall(original, __result);
    }

    /// <summary>Edits the copy of <paramref name="original"/>'s interop wrapper so each reviewed by-reference value-type
    /// argument reaches `il2cpp_runtime_invoke` as the address of the struct data. Returns the number of arguments repaired;
    /// throws when the wrapper is not the shape the pinned interop generator emits.</summary>
    internal static int RepairOriginalCall(MethodBase original, DynamicMethodDefinition copy)
    {
        var count = 0;
        foreach (var repair in HookByRef.Repairs(original).Where(repair => repair.Kind == ByRefKind.ValueRead))
        {
            var argument = repair.Index + (original.IsStatic ? 0 : 1);
            var body = copy.Definition.Body;
            // Stock shape of the original call's argument: ldarg A; ldind.ref; call Il2CppObjectBaseToPtrNotNull; stind.i
            var packs = body.Instructions.Where(read => ArgumentOf(read, copy.Definition.HasThis) == argument
                && read.Next is { OpCode.Code: Code.Ldind_Ref, Next: { OpCode.Code: Code.Call, Operand: MethodReference pointer, Next: { OpCode.Code: Code.Stind_I } } }
                && pointer.Name == "Il2CppObjectBaseToPtrNotNull" && pointer.ReturnType.FullName == "System.IntPtr").ToArray();
            if (packs.Length != 1) throw Shape(argument, "expected one boxed by-reference argument of the original call, found " + packs.Length);
            var il = body.GetILProcessor();
            il.InsertAfter(packs[0].Next!.Next!, il.Create(OpCodes.Call, copy.Definition.Module.ImportReference(HookBridgeBackend.UnboxMethod)));
            count++;
        }
        return count;
    }

    private static int ArgumentOf(Instruction instruction, bool hasThis)
        => instruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S && instruction.Operand is ParameterDefinition parameter
            ? parameter.Index + (hasThis ? 1 : 0) : Argument(instruction);

    private static void AfterBridge(object __instance, DynamicMethodDefinition __result)
    {
        var original = __instance.GetType().GetProperty("Original")?.GetValue(__instance) as MethodBase
            ?? throw new InvalidOperationException("native-hook-bridge-target-unavailable");
        bool prepared;
        lock (Gate) prepared = Targets.Contains(original);
        if (prepared) Repair(original, __result);
    }

    /// <summary>Edits the generated bridge of <paramref name="original"/>; returns the number of parameters repaired
    /// (0 when the target needs none or this very bridge was already repaired).</summary>
    internal static int Repair(MethodBase original, DynamicMethodDefinition bridge)
    {
        var repairs = HookByRef.Repairs(original);
        if (repairs.Count == 0) return 0;
        var parameters = original.GetParameters();
        var instance = original.IsStatic ? 0 : 1;
        lock (Repaired)
        {
            if (Repaired.TryGetValue(bridge, out _)) return 0;
            var outputs = repairs.Where(repair => repair.Kind == ByRefKind.Output).ToArray();
            if (outputs.Length != 0) RepairOutputs(bridge, outputs, parameters, instance);
            foreach (var repair in repairs.Where(repair => repair.Kind != ByRefKind.Output))
            {
                var argument = repair.Index + instance;
                switch (repair.Kind)
                {
                    case ByRefKind.Direct: ReadDirect(bridge, argument, parameters[repair.Index].ParameterType.GetElementType()!); break;
                    case ByRefKind.ValueRead: ReadValue(bridge, argument, parameters[repair.Index].ParameterType.GetElementType()!); break;
                    default: throw new InvalidOperationException("native-hook-bridge-kind-unknown");
                }
            }
            Repaired.Add(bridge, new object());
            return repairs.Count;
        }
    }

    // Stock shape for an `out T` at native argument A:
    //   ldarg A; ldind.i; [native -> managed conversion]; stloc L(T); ldloca L
    //   ...  ldarg A; ldloc L; [managed -> native conversion]; stind.*
    // Repaired: the address is popped without reading it, L is initialized to default, and copy-back uses T's width.
    private static void RepairOutputs(DynamicMethodDefinition bridge, IReadOnlyList<ByRefRepair> repairs,
        IReadOnlyList<ParameterInfo> parameters, int instance)
    {
        // Validate every output before changing any of them. A changed backend must leave the bridge untouched.
        var outputs = repairs.Select(repair =>
        {
            var element = parameters[repair.Index].ParameterType.GetElementType()!;
            return Validate(bridge, repair.Index + instance, element);
        }).ToArray();
        var il = bridge.Definition.Body.GetILProcessor();
        foreach (var output in outputs)
        {
            for (var instruction = output.Read.Next; instruction != output.Store; instruction = instruction!.Next)
            {
                instruction!.OpCode = OpCodes.Nop;
                instruction.Operand = null;
            }
            output.Read.OpCode = OpCodes.Pop;
            output.Read.Operand = null;
            output.Store.OpCode = OpCodes.Nop;
            output.Store.Operand = null;
            var address = il.Create(OpCodes.Ldloca, output.Local);
            il.InsertAfter(output.Read, address);
            il.InsertAfter(address, il.Create(OpCodes.Initobj, bridge.Definition.Module.ImportReference(output.Element)));
            output.Copyback.OpCode = ExactCopyback(output.Element);
            output.Copyback.Operand = output.Copyback.OpCode.Code == Code.Stobj
                ? bridge.Definition.Module.ImportReference(output.Element) : null;
        }
    }

    private static (Instruction Read, Instruction Store, VariableDefinition Local, Instruction Copyback, Type Element) Validate(
        DynamicMethodDefinition bridge, int argument, Type element)
    {
        var instructions = bridge.Definition.Body.Instructions;
        var read = Read(instructions, argument);
        var type = TypeName(bridge, element);
        Instruction? cursor = read.Next;
        if (element.IsValueType)
        {
            if (cursor?.OpCode.Code is not (Code.Stloc or Code.Stloc_S))
                throw Shape(argument, "the output read is not followed by its local");
        }
        else if (element == typeof(string))
        {
            if (cursor is not { OpCode.Code: Code.Call, Operand: MethodReference conversion }
                || conversion.Name != "Il2CppStringToManaged" || conversion.ReturnType.FullName != "System.String"
                || conversion.Parameters.Count != 1)
                throw Shape(argument, "the string output input conversion changed");
            cursor = cursor.Next;
        }
        else
        {
            Instruction? creation = null;
            for (var steps = 0; cursor != null && steps < 12 && cursor.OpCode.Code is not (Code.Stloc or Code.Stloc_S);
                 steps++, cursor = cursor.Next)
            {
                if (cursor.OpCode.Code == Code.Newobj)
                {
                    if (creation != null) throw Shape(argument, "more than one output wrapper construction");
                    creation = cursor;
                }
                else if (cursor.OpCode.Code is not (Code.Dup or Code.Brtrue or Code.Brtrue_S or Code.Pop or Code.Ldnull
                             or Code.Br or Code.Br_S or Code.Nop))
                    throw Shape(argument, "unexpected " + cursor.OpCode + " in the output input conversion");
            }
            if (creation?.Operand is not MethodReference constructor || constructor.DeclaringType.FullName != type
                || constructor.Parameters.Count != 1 || constructor.Parameters[0].ParameterType.FullName != "System.IntPtr")
                throw Shape(argument, "no output wrapper construction of " + type);
        }
        if (cursor?.Operand is not VariableDefinition local || cursor.OpCode.Code is not (Code.Stloc or Code.Stloc_S)
            || local.VariableType.FullName != type || !Address(cursor.Next, local))
            throw Shape(argument, "the output local is not the expected " + type);

        var copies = instructions.Where(copy => Argument(copy) == argument && Load(copy.Next, local)).ToArray();
        if (copies.Length != 1) throw Shape(argument, "expected one output copy-back, found " + copies.Length);
        var copyback = copies[0].Next!.Next;
        if (!element.IsValueType)
        {
            var conversion = element == typeof(string) ? "ManagedStringToIl2Cpp" : "Il2CppObjectBaseToPtr";
            if (copyback is not { OpCode.Code: Code.Call, Operand: MethodReference method }
                || method.Name != conversion || method.ReturnType.FullName != "System.IntPtr" || method.Parameters.Count != 1)
                throw Shape(argument, "the output copy-back conversion changed");
            copyback = copyback.Next;
        }
        var generated = GeneratedCopyback(element);
        if (copyback?.OpCode.Code != generated)
            throw Shape(argument, "the output copy-back width changed; expected " + generated + ", found " + copyback?.OpCode.Code);
        return (read, cursor, local, copyback, element);
    }

    private static Code GeneratedCopyback(Type element)
        => element == typeof(byte) || element == typeof(sbyte) || element == typeof(bool) ? Code.Stind_I1
            : element == typeof(short) || element == typeof(ushort) ? Code.Stind_I2
            : element == typeof(int) || element == typeof(uint) ? Code.Stind_I4
            : element == typeof(long) || element == typeof(ulong) ? Code.Stind_I8
            : element == typeof(float) ? Code.Stind_R4
            : element == typeof(double) ? Code.Stind_R8
            : Code.Stind_I;

    private static OpCode ExactCopyback(Type element)
    {
        var type = element.IsEnum ? Enum.GetUnderlyingType(element) : element;
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool)) return OpCodes.Stind_I1;
        if (type == typeof(char) || type == typeof(short) || type == typeof(ushort)) return OpCodes.Stind_I2;
        if (type == typeof(int) || type == typeof(uint)) return OpCodes.Stind_I4;
        if (type == typeof(long) || type == typeof(ulong)) return OpCodes.Stind_I8;
        if (type == typeof(float)) return OpCodes.Stind_R4;
        if (type == typeof(double)) return OpCodes.Stind_R8;
        return type.IsValueType && type != typeof(IntPtr) && type != typeof(UIntPtr) ? OpCodes.Stobj : OpCodes.Stind_I;
    }

    // Stock shape for a `ref` float, double or blittable struct at native argument A:
    //   ldarg A; ldind.i; stloc L(T); ldloca L  ...  ldarg A; ldloc L; stind.r4 | stind.r8 | stind.i
    // Repaired:  ldarg A  ...  (nothing): the native address is the managed reference.
    private static void ReadDirect(DynamicMethodDefinition bridge, int argument, Type element)
    {
        var instructions = bridge.Definition.Body.Instructions;
        var read = Read(instructions, argument);
        var type = TypeName(bridge, element);
        if (read.Next is not { } store || store.OpCode.Code is not (Code.Stloc or Code.Stloc_S)
            || store.Operand is not VariableDefinition local || local.VariableType.FullName != type
            || !Address(store.Next, local))
            throw Shape(argument, "the local that receives the read is not the expected " + type);
        var copies = instructions.Where(copy => Argument(copy) == argument && Load(copy.Next, local)
            && copy.Next!.Next?.OpCode.Code is Code.Stind_R4 or Code.Stind_R8 or Code.Stind_I).ToArray();
        if (copies.Length != 1) throw Shape(argument, "expected one copy-back, found " + copies.Length);
        foreach (var removed in new[] { read, store, store.Next!, copies[0], copies[0].Next!, copies[0].Next!.Next! })
        {
            removed.OpCode = OpCodes.Nop;
            removed.Operand = null;
        }
    }

    // Stock shape for `ref NM_NoiseData` at native argument A:
    //   ldarg A; ldind.i; [dup; brtrue.s X; pop; ldnull; br.s Y; X: newobj T(IntPtr); Y:] stloc L(T); ldloca L
    //   ...  ldarg A; ldloc L; call <wrapper -> IntPtr>; stind.i
    // Repaired:  ldarg A; ldc.i8 <class>; call Box(IntPtr, long); [the same wrapper creation] ...  and no copy-back.
    private static void ReadValue(DynamicMethodDefinition bridge, int argument, Type element)
    {
        var body = bridge.Definition.Body;
        var instructions = body.Instructions;
        var read = Read(instructions, argument);
        Instruction? creation = null, cursor = read.Next;
        for (var steps = 0; cursor != null && steps < 12 && cursor.OpCode.Code is not (Code.Stloc or Code.Stloc_S); steps++, cursor = cursor.Next)
        {
            if (cursor.OpCode.Code == Code.Newobj)
            {
                if (creation != null) throw Shape(argument, "more than one wrapper construction");
                creation = cursor;
            }
            else if (cursor.OpCode.Code is not (Code.Dup or Code.Brtrue or Code.Brtrue_S or Code.Pop or Code.Ldnull or Code.Br or Code.Br_S or Code.Nop))
                throw Shape(argument, "unexpected " + cursor.OpCode + " between the read and the local");
        }
        var type = TypeName(bridge, element);
        if (creation?.Operand is not MethodReference constructor || constructor.DeclaringType.FullName != type
            || constructor.Parameters.Count != 1 || constructor.Parameters[0].ParameterType.FullName != "System.IntPtr")
            throw Shape(argument, "no wrapper construction of " + type);
        if (cursor?.Operand is not VariableDefinition local || cursor.OpCode.Code is not (Code.Stloc or Code.Stloc_S)
            || local.VariableType.FullName != type || !Address(cursor.Next, local))
            throw Shape(argument, "the local that receives the wrapper is not the expected " + type);
        var copies = instructions.Where(copy => Argument(copy) == argument && Load(copy.Next, local)
            && copy.Next!.Next is { OpCode.Code: Code.Call, Operand: MethodReference conversion, Next: { OpCode.Code: Code.Stind_I } }
            && conversion.ReturnType.FullName == "System.IntPtr" && conversion.Parameters.Count == 1).ToArray();
        if (copies.Length != 1) throw Shape(argument, "expected one wrapper copy-back, found " + copies.Length);
        var klass = HookBridgeBackend.ClassPointer(element);
        if (klass == IntPtr.Zero) throw new InvalidOperationException("native-hook-bridge-class-unavailable type=" + type);

        var il = body.GetILProcessor();
        il.InsertBefore(read, il.Create(OpCodes.Ldc_I8, klass.ToInt64()));
        read.OpCode = OpCodes.Call;
        read.Operand = bridge.Definition.Module.ImportReference(HookBridgeBackend.BoxMethod);
        var end = copies[0].Next!.Next!.Next!.Next;
        for (var copy = copies[0]; copy != end; copy = copy.Next!)
        {
            copy.OpCode = OpCodes.Nop;
            copy.Operand = null;
        }
    }

    // Use Cecil's importer for recursive generic arguments, nested names and array/by-ref/pointer shapes.
    private static string TypeName(DynamicMethodDefinition bridge, Type type)
        => bridge.Definition.Module.ImportReference(type).FullName;

    private static Instruction Read(IEnumerable<Instruction> instructions, int argument)
    {
        var reads = instructions.Where(read => read.OpCode.Code == Code.Ldind_I && Argument(read.Previous) == argument).ToArray();
        if (reads.Length != 1) throw Shape(argument, "expected one pointer-width read of the argument, found " + reads.Length);
        return reads[0];
    }

    private static bool Address(Instruction? instruction, VariableDefinition local)
        => instruction is { OpCode.Code: Code.Ldloca or Code.Ldloca_S } && ReferenceEquals(instruction.Operand, local);

    private static bool Load(Instruction? instruction, VariableDefinition local)
        => instruction is { OpCode.Code: Code.Ldloc or Code.Ldloc_S } && ReferenceEquals(instruction.Operand, local);

    private static int Argument(Instruction? instruction) => instruction?.OpCode.Code switch
    {
        Code.Ldarg_0 => 0,
        Code.Ldarg_1 => 1,
        Code.Ldarg_2 => 2,
        Code.Ldarg_3 => 3,
        Code.Ldarg or Code.Ldarg_S => instruction.Operand switch
        {
            ParameterDefinition parameter => parameter.Index,
            int value => value,
            short value => value,
            byte value => value,
            sbyte value => value,
            _ => -1
        },
        _ => -1
    };

    private static InvalidOperationException Shape(int argument, string detail)
        => new("native-hook-bridge-shape-changed argument=" + argument + " " + detail);
}
