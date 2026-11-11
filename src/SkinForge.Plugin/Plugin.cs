using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SkinForge.Plugin;

[BepInPlugin(Guid, "SkinForge", "1.0.0")]
[BepInProcess("GTFO.exe")]
public sealed class SkinForgePlugin : BasePlugin
{
    // Also the config file name: BepInEx/config/SkinForge.cfg.
    public const string Guid = "SkinForge";

    static ManualLogSource _log = null!;

    internal static void Info(string message) => _log.LogInfo("[SkinForge] " + message);
    internal static void Fail(string message) => _log.LogWarning("[SkinForge] " + message);
    // The interop Vector3.ToString(format) calls a Single.ToString overload that does not exist at runtime; format here.
    internal static string Vec(Vector3 v) => FormattableString.Invariant($"({v.x:0.####}, {v.y:0.####}, {v.z:0.####})");

    public override void Load()
    {
        _log = Log;
        if (!Config.Bind("General", "Enabled", true, "Apply the installed skins to the local player's gear.").Value)
        {
            Info("disabled in SkinForge.cfg");
            return;
        }
        if (Config.Bind("General", "Readback", false, "Write each assembled gear's vanilla part poses to BepInEx/SkinForge/readback/<gearId>.json.").Value)
            Readback.Directory = Path.Combine(Paths.BepInExRootPath, "SkinForge", "readback");
        var skins = Path.Combine(Path.GetDirectoryName(typeof(SkinForgePlugin).Assembly.Location)!, "skins");
        Skins.Watch(skins);
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(SkinForgePlugin).Assembly);
        foreach (var method in harmony.GetPatchedMethods()) Info($"hooked {method.DeclaringType?.Name}.{method.Name}");
        AddComponent<Runner>();
        Info($"loaded, skins folder {skins}");
    }
}

// Main-thread tick: hot reload, pending gear and the live screens and layers.
public sealed class Runner : MonoBehaviour
{
    public Runner(IntPtr pointer) : base(pointer) { }

    public void Update() => SkinnedGear.Tick(Time.unscaledTime, Time.time);
}
