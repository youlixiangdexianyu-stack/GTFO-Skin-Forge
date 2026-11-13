using System.Reflection;

namespace ForgeCore;

/// <summary>
/// Which game methods Harmony may patch. Il2CppInterop builds a native-to-managed trampoline for every patched method; a trampoline
/// for a method with a by-ref primitive parameter (<c>out float</c>, <c>ref int</c>, ...) failed with a NullReferenceException before
/// any patch method ran, on every call (owner log of 2026-10-28, <c>EnemyDetection.DetectOnNoiseDistance_Conditional_AnimatedWindow</c>,
/// 1240 lines), and the game's own check went with it. Such a method is never patched: <see cref="Why"/> names the reason.
/// </summary>
internal static class TrampolineSafety
{
    /// <summary>Null when <paramref name="method"/> can be patched, else the reason it cannot.</summary>
    public static string? Why(MethodBase method)
    {
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            if (type.IsByRef && type.GetElementType() is { IsPrimitive: true })
                return "parameter '" + parameter.Name + "' is by-ref " + type.GetElementType()!.Name + ", which Il2CppInterop's trampoline cannot pass";
        }
        return null;
    }
}
