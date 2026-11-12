using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace ForgeSkin.Plugin;

[BepInPlugin(Guid, "ForgeSkin", "1.0.0")]
[BepInProcess("GTFO.exe")]
public sealed class ForgeSkinPlugin : BasePlugin
{
    // Also the config file name: BepInEx/config/ForgeSkin.cfg.
    public const string Guid = "ForgeSkin";

    static ManualLogSource _log = null!;

    // Every line also goes, flushed at once, to <plugin folder>/forgeskin.log (cleared at launch): the app tails it after
    // 应用到游戏 to show what the game did, and it survives a crash that loses BepInEx's buffered log.
    static StreamWriter? _own;
    internal static void Info(string message) { _log.LogInfo("[ForgeSkin] " + message); Own("info", message); }
    internal static void Fail(string message) { _log.LogWarning("[ForgeSkin] " + message); Own("warn", message); }

    static void Own(string level, string message)
    {
        try { lock (_log) _own?.WriteLine($"{DateTime.UtcNow:O} {level} {message}"); }
        catch (IOException) { }
    }
    // The interop Vector3.ToString(format) calls a Single.ToString overload that does not exist at runtime; format here.
    internal static string Vec(Vector3 v) => FormattableString.Invariant($"({v.x:0.####}, {v.y:0.####}, {v.z:0.####})");

    public override void Load()
    {
        _log = Log;
        try
        {
            _own = new StreamWriter(Path.Combine(Path.GetDirectoryName(typeof(ForgeSkinPlugin).Assembly.Location)!, "forgeskin.log"), false) { AutoFlush = true };
        }
        catch (IOException) { }
        if (!Config.Bind("General", "Enabled", true, "Apply the installed skins to the local player's gear.").Value)
        {
            Info("disabled in ForgeSkin.cfg");
            return;
        }
        if (Config.Bind("General", "Readback", false, "Write each assembled gear's vanilla part poses to BepInEx/ForgeSkin/readback/<gearId>.json.").Value)
            Readback.Directory = Path.Combine(Paths.BepInExRootPath, "ForgeSkin", "readback");
        var skins = Path.Combine(Path.GetDirectoryName(typeof(ForgeSkinPlugin).Assembly.Location)!, "skins");
        bool dev = Config.Bind("General", "Development", false,
            "Development mode: reload the skins whenever the skins folder changes, so 应用到游戏 shows up in a running game. Off: skins load once when the game starts.").Value;
        Skins.Watch(skins, Paths.PluginPath, dev);
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(ForgeSkinPlugin).Assembly);
        foreach (var method in harmony.GetPatchedMethods()) Info($"hooked {method.DeclaringType?.Name}.{method.Name}");
        AddComponent<Runner>();
        Info($"loaded, skins folder {skins}, hot reload {(dev ? "on" : "off")}");
    }
}

// Main-thread tick: hot reload, pending gear and the live screens and layers.
public sealed class Runner : MonoBehaviour
{
    public Runner(IntPtr pointer) : base(pointer) { }

    public void Update() => SkinnedGear.Tick(Time.unscaledTime, Time.time);
}
