using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Yuyu.HelAmmoDisplay;

[BepInPlugin(GUID, PluginName, PluginVersion)]
[BepInProcess("GTFO.exe")]
public sealed class HelAmmoDisplayPlugin : BasePlugin
{
    public const string GUID = "Yuyu.HelAmmoDisplay";
    public const string PluginName = "HEL Ammo Display";
    public const string PluginVersion = "1.6.0";

    internal static HelAmmoDisplayPlugin Instance { get; private set; }
    internal static ManualLogSource LogSource { get; private set; }

    private Harmony harmony;

    public override void Load()
    {
        Instance = this;
        LogSource = Log;

        DisplayConfig.Bind(Config);
        Config.Save();

        harmony = new Harmony(GUID);
        harmony.PatchAll(typeof(HelAmmoDisplayPlugin).Assembly);

        LogSource.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    public override bool Unload()
    {
        AmmoDisplayManager.Dispose();
        harmony?.UnpatchSelf();
        return true;
    }
}
