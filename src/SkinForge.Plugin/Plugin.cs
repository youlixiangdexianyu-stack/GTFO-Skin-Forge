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

    public override void Load()
    {
        _log = Log;
        if (!Config.Bind("General", "Enabled", true, "Apply the installed skins to the local player's gear.").Value)
        {
            Info("disabled in SkinForge.cfg");
            return;
        }
        var skins = Path.Combine(Path.GetDirectoryName(typeof(SkinForgePlugin).Assembly.Location)!, "skins");
        Skins.Watch(skins);
        new Harmony(Guid).PatchAll(typeof(SkinForgePlugin).Assembly);
        AddComponent<Runner>();
        Info($"loaded, skins folder {skins}");
    }
}

// Main-thread tick: hot reload, pending gear and the live screens and layers.
public sealed class Runner : MonoBehaviour
{
    public Runner(IntPtr pointer) : base(pointer) { }

    public void Update() => SkinnedGear.Tick(Time.unscaledTime, Time.time);

    public void LateUpdate() => SkinnedGear.LateTick();
}
