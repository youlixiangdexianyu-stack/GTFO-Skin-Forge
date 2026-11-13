using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using HarmonyLib;
using UnityEngine;

namespace ForgeSkin.Plugin;

[BepInPlugin(Guid, "ForgeSkin", "1.0.3")] // package-version: NAinfini-ForgeSkin
[BepInDependency("NAinfini.ForgeCore")]
[BepInProcess("GTFO.exe")]
public sealed class ForgeSkinPlugin : BasePlugin
{
    // Also the config file name: BepInEx/config/ForgeSkin.cfg.
    public const string Guid = "ForgeSkin";

    internal static ModLog Logger = null!;

    // Errors go to ForgeCore's log. In author mode (Development) every line, errors included, also goes, flushed at once, to
    // BepInEx/plugins/ForgeSkin/forgeskin.log (cleared at launch): the editor tails it after 应用到游戏 to show what the game did,
    // and it survives a crash that loses BepInEx's buffered log. Outside author mode there is no such file and Info lines go nowhere.
    static StreamWriter? _own;
    static readonly object OwnLock = new();

    internal static void Info(string message) => Own("info", message);

    /// <summary>A skin author's warning that is no error for the player (a fallback taken, a texture left uncompressed): the editor's feed only.</summary>
    internal static void Warn(string message) => Own("warn", message);

    /// <summary>An error: a feature failed or data leaves something unusable. Written once per exact text under its registered code.</summary>
    internal static void Fail(string code, string message, Exception? error = null)
    {
        Logger.ErrorOnce(code, error == null ? message : message + ": " + error);
        Own("warn", message);
    }

    static void Own(string level, string message)
    {
        if (_own == null) return;
        try { lock (OwnLock) _own.WriteLine($"{DateTime.UtcNow:O} {level} {message}"); }
        catch (IOException) { _own = null; } // the editor's feed is gone (disk full, file taken away); the game goes on without it
    }
    // The interop Vector3.ToString(format) calls a Single.ToString overload that does not exist at runtime; format here.
    internal static string Vec(Vector3 v) => FormattableString.Invariant($"({v.x:0.####}, {v.y:0.####}, {v.z:0.####})");

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Guid);
        Settings.Adopt(Guid, Config);
        bool dev = Settings.Bind(Guid, "General", "Development", false,
            "Development mode: reload the skins whenever the skins folder changes, so 应用到游戏 shows up in a running game. Off: skins load once when the game starts.").Value;
        // A fixed folder wherever the DLL is (a mod manager installs it in plugins/NAinfini-ForgeSkin/, the editor in
        // plugins/ForgeSkin/): the editor installs packs into its skins/ and reads forgeskin.log from it.
        var folder = Path.Combine(Paths.PluginPath, "ForgeSkin");
        Directory.CreateDirectory(folder);
        if (dev)
            try { _own = new StreamWriter(Path.Combine(folder, "forgeskin.log"), false) { AutoFlush = true }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A read-only folder, another game instance holding the file: the editor gets no feed from this game.
                Logger.Error("skin-author-log-unavailable", "forgeskin.log cannot be written, the editor shows nothing from this game: " + e);
            }
        if (!Settings.Bind(Guid, "General", "Enabled", true, "Apply the installed skins to the local player's gear.").Value)
        {
            Info("disabled in ForgeSkin.cfg");
            return;
        }
        if (Settings.Bind(Guid, "General", "Readback", false, "Write each assembled gear's vanilla part poses to BepInEx/ForgeSkin/readback/<gearId>.json.").Value)
            Readback.Directory = Path.Combine(Paths.BepInExRootPath, "ForgeSkin", "readback");
        var skins = Path.Combine(folder, "skins");
        Skins.Watch(skins, Paths.PluginPath, Path.Combine(Paths.ConfigPath, "ForgeSkin.choices.json"), Path.Combine(Paths.ConfigPath, "ForgeSkin.colours.json"), dev);
        Icons.SkinsChanged(); // the icons the game cached before may show another skin than the one now active
        Picker.Register(Settings.Bind(Guid, "General", "PickerKey", KeyCode.F8,
            "The default key that opens and closes the skin picker in the lobby (the SKINS button opens it too); the game's Controls settings can change it. The picks are kept in BepInEx/config/ForgeSkin.choices.json, the colours in ForgeSkin.colours.json.").Value.ToString());
        Holograms.UseShader = Settings.Bind(Guid, "General", "HologramShader", true,
            "Draw holograms with the game's hologram shader (Cell/Player/Display_Hologram). Off: the gear shader's plain unlit glow.").Value;
        SharedHooks.Subscribe();
        ForgeVisual.GunMount.Mounting += SkinnedGear.Mounting;
        var harmony = new Harmony(Guid);
        // ForgeSkin's own hooks (methods no other package reads), one class at a time: a target a game update renamed loses
        // that event only, not the plugin.
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ForgeSkinPlugin).Assembly))
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Fail("skin-hook-unavailable", $"hook {type.Name} not installed", e); }
        foreach (var method in harmony.GetPatchedMethods()) Info($"hooked {method.DeclaringType?.Name}.{method.Name}");
        Frame.OnUpdate(Guid, Update);
        Info($"loaded, skins folder {skins}, hot reload {(dev ? "on" : "off")}");
    }

    // Main-thread tick: the picker key, hot reload and picks, pending gear and the live screens and layers.
    static void Update()
    {
        Picker.Poll();
        SkinnedGear.Tick(Frame.UnscaledTime, Frame.GameTime);
    }
}
