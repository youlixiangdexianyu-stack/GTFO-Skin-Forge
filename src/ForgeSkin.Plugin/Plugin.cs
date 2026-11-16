using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using ForgeSkin.Format;
using HarmonyLib;
using UnityEngine;

namespace ForgeSkin.Plugin;

// The toon setting's three choices, in the picker's order.
enum ToonMode { Forward, Deferred, Off }

[BepInPlugin(Guid, "ForgeSkin", "1.1.1")] // package-version: NAinfini-ForgeSkin
[BepInDependency("NAinfini.ForgeCore", ">=1.0.2")]
[BepInProcess("GTFO.exe")]
public sealed class ForgeSkinPlugin : BasePlugin
{
    // Also the config file name: BepInEx/config/ForgeSkin.cfg.
    public const string Guid = "ForgeSkin";

    internal static ModLog Logger = null!;

    // Errors go to ForgeCore's log. Every error, and the Note lines (a skin read, a gear wearing a skin), also go, flushed at once, to
    // BepInEx/ForgeSkin/forgeskin.log (SkinFolders.AuthorLog, cleared at launch): the editor tails it after 应用到游戏 to show what the
    // game did, and it survives a crash that loses BepInEx's buffered log. Info and Warn lines (every detail) go there only in author
    // mode (Development, set by hand in ForgeSkin.cfg).
    static StreamWriter? _own;
    static bool _verbose;
    static readonly object OwnLock = new();

    // An interpolated Info or Warn line is only built (its holes read) in author mode: AuthorLine skips it otherwise.
    internal static void Info(string message) { if (Logging) Own("info", message); }

    internal static void Info(ref AuthorLine message) { if (message.Open) Own("info", message.Text()); }

    /// <summary>A line the editor waits for after 应用到游戏 (a skin read, a gear wearing a skin): written whenever the log is open.</summary>
    internal static void Note(string message) => Own("info", message);

    /// <summary>Author mode (Development) with the log open: a caller builds a costly Info line only when this is true.</summary>
    internal static bool Logging => _verbose && _own != null;

    /// <summary>A skin author's warning that is no error for the player (a fallback taken, a texture left uncompressed): the editor's feed only.</summary>
    internal static void Warn(string message) { if (Logging) Own("warn", message); }

    internal static void Warn(ref AuthorLine message) { if (message.Open) Own("warn", message.Text()); }

    [InterpolatedStringHandler]
    internal ref struct AuthorLine
    {
        DefaultInterpolatedStringHandler _text;
        public readonly bool Open;

        public AuthorLine(int literalLength, int formattedCount, out bool open)
        {
            open = Open = Logging;
            _text = open ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) => _text.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);
        public string Text() => _text.ToStringAndClear();
    }

    /// <summary>An error: a feature failed or data leaves something unusable. Written once per exact text under its registered code.</summary>
    internal static void Fail(string code, string message, Exception? error = null)
    {
        Logger.ErrorOnce(code, error == null ? message : message + ": " + error);
        Own("warn", message);
    }

    static void Own(string level, string message)
    {
        if (_own == null) return;
        lock (OwnLock)
            try { _own?.WriteLine($"{DateTime.UtcNow:O} {level} {message}"); }
            catch (IOException) { _own = null; } // the editor's feed is gone (disk full, file taken away); the game goes on without it
    }

    // Opens the editor's log (cleared), once at launch.
    static void AuthorLog()
    {
        lock (OwnLock)
            if (_own == null)
                try { _own = new StreamWriter(SkinFolders.AuthorLog(Paths.BepInExRootPath), false) { AutoFlush = true }; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A read-only folder, another game instance holding the file: the editor gets no feed from this game.
                    Logger.Error("skin-author-log-unavailable", "forgeskin.log cannot be written, the editor shows nothing from this game: " + e);
                }
    }

    // The settings the picker's ADJUST tab shows (Picker); a change applies at once and is saved to ForgeSkin.cfg.
    internal static ConfigEntry<bool> Enabled = null!, HologramShader = null!, Development = null!;
    // How this machine draws the materials an author marked toon (a display preference: never sent, never synced).
    internal static ConfigEntry<ToonMode> Toon = null!;
    // The interop Vector3.ToString(format) calls a Single.ToString overload that does not exist at runtime; format here.
    internal static string Vec(Vector3 v) => FormattableString.Invariant($"({v.x:0.####}, {v.y:0.####}, {v.z:0.####})");

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Guid);
        Settings.Adopt(Guid, Config);
        Development = Settings.Bind(Guid, "General", "Development", false,
            "Author mode, for skin mod authors: also reload the skins whenever a skin mod in the plugins folder changes, and write every detail to forgeskin.log. The skins folder the editor installs into is followed, and what loaded or failed logged, either way.",
            on => { _verbose = on; Skins.HotReload(on); });
        // BepInEx/ForgeSkin/ wherever the DLL is (SkinFolders): the editor installs packs into its skins/ and reads forgeskin.log from it.
        var bepInEx = Paths.BepInExRootPath;
        Directory.CreateDirectory(SkinFolders.Root(bepInEx));
        bool dev = Development.Value;
        _verbose = dev;
        AuthorLog();
        Enabled = Settings.Bind(Guid, "General", "Enabled", true,
            "Show the installed skins on gear (yours, and teammates' that wear skins you have). Off: all gear keeps its original look; picks and colours are kept.", Skins.Enable);
        Skins.Enabled = Enabled.Value;
        if (!Skins.Enabled) Info("skins off in ForgeSkin.cfg");
        if (Settings.Bind(Guid, "General", "Readback", false, "Write each assembled gear's vanilla part poses to BepInEx/ForgeSkin/readback/<gearId>.json.").Value)
            Readback.Directory = SkinFolders.Readback(bepInEx);
        ForgeSkin.Format.CodeEffects.Catalog = ForgeVisual.Procedural.Catalog; // one catalogue for pack checks and drawing
        var skins = SkinFolders.Skins(bepInEx);
        // TEMPORARY, removed at 1.4.0 with Core/OldSkinFolder.cs (PLAN §6). Packs left in the old folder are a state the player can be in, not
        // an error: the picker says so (yellow, every launch while they are there), the author log once, not the error log.
        if (OldSkinFolder.HasSkins(bepInEx))
        {
            Picker.OldFolder = OldSkinFolder.Of(bepInEx);
            Info($"skins in the old folder {Picker.OldFolder} are not loaded; opening the ForgeSkin editor once moves them to {skins}");
        }
        Skins.Watch(skins, Paths.PluginPath, Path.Combine(Paths.ConfigPath, "ForgeSkin.choices.json"), Path.Combine(Paths.ConfigPath, "ForgeSkin.colours.json"), dev);
        Icons.SkinsChanged(); // the icons the game cached before may show another skin than the one now active
        Picker.Register(); // picks kept in BepInEx/config/ForgeSkin.choices.json, colours in ForgeSkin.colours.json
        Looks.Register(); // the look each player shows the others (ForgeCore Net)
        Characters.Register(); // character models, and ForgeCore's player looks (replay stand-ins, emote cameras)
        HologramShader = Settings.Bind(Guid, "General", "HologramShader", true,
            "Draw holograms with the game's hologram shader (Cell/Player/Display_Hologram). Off: the gear shader's plain unlit glow.",
            on => { Holograms.UseShader = on; Skins.Reapply(); });
        Holograms.UseShader = HologramShader.Value;
        Toon = Settings.Bind(Guid, "General", "Toon", ToonMode.Deferred,
            "How materials a skin's author marked toon are drawn on this machine (only here, never sent to anyone). Deferred: lit by the game, seen by the thermal sight; Forward: lit by ForgeSkin itself, a cleaner toon look the thermal sight does not see; Off: drawn like every other material.",
            _ => Characters.ToonChanged());
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
        Frame.OnQuit(Guid, Skins.SaveColours); // a colour picked in the last half second is the player's too
        Info($"loaded, skins folder {skins}, hot reload {(dev ? "on" : "off")}");
    }

    // Main-thread tick: hot reload and picks, pending gear and the live screens and layers.
    static void Update()
    {
        SkinnedGear.Tick(Frame.UnscaledTime, Frame.GameTime);
    }
}
