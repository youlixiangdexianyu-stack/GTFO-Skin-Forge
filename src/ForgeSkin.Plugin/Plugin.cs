using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace ForgeSkin.Plugin;

[BepInPlugin(Guid, "ForgeSkin", "1.0.3")]
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
        catch (Exception) { } // no own log (read-only folder, another game instance holding it): BepInEx's still works
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
        Skins.Watch(skins, Paths.PluginPath, Path.Combine(Paths.ConfigPath, "ForgeSkin.choices.json"), dev);
        Icons.SkinsChanged(); // the icons the game cached before may show another skin than the one now active
        Picker.Key = Config.Bind("General", "PickerKey", KeyCode.F8,
            "Opens and closes the in-game skin picker (Esc also closes it). The picks are kept in BepInEx/config/ForgeSkin.choices.json.").Value;
        Holograms.UseShader = Config.Bind("General", "HologramShader", true,
            "Draw holograms with the game's hologram shader (Cell/Player/Display_Hologram). Off: the gear shader's plain unlit glow.").Value;
        var harmony = new Harmony(Guid);
        // One hook class at a time: a target a game update renamed (the compiler-generated reload callback) loses that
        // event only, not the plugin.
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ForgeSkinPlugin).Assembly))
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Fail($"hook {type.Name} not installed: {(e.InnerException ?? e).Message}"); }
        foreach (var method in harmony.GetPatchedMethods()) Info($"hooked {method.DeclaringType?.Name}.{method.Name}");
        AddComponent<Runner>();
        Picker.View = AddComponent<Picker>();
        Picker.View.enabled = false;
        Info($"loaded, skins folder {skins}, hot reload {(dev ? "on" : "off")}");
    }
}

// Main-thread tick: the picker key, hot reload, pending gear and the live screens and layers.
public sealed class Runner : MonoBehaviour
{
    public Runner(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        Picker.Poll();
        SkinnedGear.Tick(Time.unscaledTime, Time.time);
    }
}
