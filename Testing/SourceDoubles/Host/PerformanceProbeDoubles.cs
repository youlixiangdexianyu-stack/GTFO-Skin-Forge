using System.Reflection;

// The clock uses real Windows APIs. Only Unity startup metadata/interop and the
// original native wait trampoline are replaced; no game or detour is installed.
namespace UnityEngine
{
    public static class Application { public static string unityVersion = "offline_test"; }
}
namespace BepInEx.Unity.IL2CPP.Hook
{
    public interface INativeDetour { }
}
namespace Il2CppInterop.Runtime
{
    public static class IL2CPP
    {
        private static bool Unsupported() => false;
        public static T ResolveICall<T>(string name) where T : Delegate => typeof(IL2CPP)
            .GetMethod(nameof(Unsupported), BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();
        public static IntPtr GetIl2CppClass(string assembly, string ns, string name) => IntPtr.Zero;
        public static IntPtr GetIl2CppField(IntPtr type, string name) => IntPtr.Zero;
        public static IntPtr GetIl2CppNestedType(IntPtr type, string name) => IntPtr.Zero;
        public static uint il2cpp_field_get_offset(IntPtr field) => 0;
        public static int il2cpp_class_value_size(IntPtr type, ref uint alignment) => 0;
        public static uint il2cpp_array_length(IntPtr array) => 0;
        public static long il2cpp_gc_get_used_size() => -1;
        public static long il2cpp_gc_get_heap_size() => -1;
        public static IntPtr il2cpp_class_get_method_from_name(IntPtr type, string name, int count) => IntPtr.Zero;
        public static uint il2cpp_method_get_param_count(IntPtr method) => 0;
        public static uint il2cpp_method_get_flags(IntPtr method, ref uint flags) => 0;
        public static IntPtr il2cpp_method_get_return_type(IntPtr method) => IntPtr.Zero;
        public static int il2cpp_type_get_type(IntPtr type) => 1;
    }
}
namespace Il2CppSystem
{
    public class Object { public Object(IntPtr pointer) { } }
    public class Type { public Type(IntPtr pointer) { } public string FullName => "offline"; }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource { public void LogWarning(object data) { } public void LogInfo(object data) { } public void LogError(object data) { } }
}
