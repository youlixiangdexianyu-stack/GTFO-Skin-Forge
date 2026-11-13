using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using UnityEngine;

namespace ForgeCore;

[BepInPlugin(Id, "Forge Core", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeCore", Version = "1.0.0"; // package-version: NAinfini-ForgeCore

    internal static ModLog Logger { get; private set; } = null!;

    public override void Load()
    {
        var own = base.Log;
        // One startup.timing line per library too, so a slow start can be told apart from the packages that depend on it.
        using var timing = new StartupTiming(Id, message => own.LogInfo(message));
        Settings.Adopt(Id, Config);
        ForgeCore.Log.Sinks = id =>
        {
            var source = id == Id ? own : BepInEx.Logging.Logger.CreateLogSource(id);
            return message => source.LogError(message);
        };
        Logger = ForgeCore.Log.For(Id);
        // Before any package that depends on ForgeCore loads: every Il2Cpp entry a Forge package patches gets the same prologue check.
        HarmonyLib.Public.Patching.PatchManager.ResolvePatcher += Hooks.Resolve;
        if (HookBridgeBackend.Mismatch() is { } bridge) Logger.ErrorOnce("core-hook-bridge-mismatch", bridge);
        Store.Root = Path.Combine(Paths.BepInExRootPath, "ForgeCore", "Store");
        Net.Node = new NetNode(new GtfoTransport(), Id) { Clock = static () => Time.time };
        Net.Notice = message => own.LogInfo(message);
        // Before any dependent mod subscribes, so the world has moved on when their own handlers of the same events run.
        LevelAPI.OnBuildStart += static () => Net.Node!.NoteWorld(NetWorldReason.Build);
        Patches.CheckTargets();
        // Once every plugin has loaded (and declared its hooks at load), each owned run-time target must still have one package.
        IL2CPPChainloader.Instance.Finished += Hooks.CheckAll;
        HoldPoint.NeedShutHook = static () => Patches.Ensure(Modules.HoldPointShut, typeof(HoldPointShutHook));
        // Before any dependent mod subscribes, so their own build-done handlers already find the level's dimensions.
        LevelAPI.OnBuildDone += Level.ReadDimensions;
        LevelAPI.OnLevelCleanup += () =>
        {
            Level.SetDimensions(0);
            StackedVolumes.Clear();
            HoldPoint.ForgetAll();
            InputLease.ReleaseAll();
            CameraLease.ReleaseAll();
            Enemies.Clear();
            SpecialSlot.Local.Clear();
            Gear.ForgetToolSlotSentries();
        };
        // Before any dependent mod subscribes, so their own checkpoint handlers read the recalled slot.
        EventAPI.OnCheckpointReached += SpecialSlotHooks.CheckpointReached;
        EventAPI.OnCheckpointReloaded += SpecialSlotHooks.CheckpointReloaded;
        try
        {
            ItemSpawnHook.Start();
        }
        catch (Exception error) { Logger.Error("core-items-spawned-hook", "The Items.Spawned hook could not be put on; handlers of Items.Spawned will not run: " + error); }
        ReplicationMisses.Start(message => own.LogInfo(message));
        // After ForgeCore's own cleanup and recall, so a mod's GameEvents handler sees them done.
        GameEventSources.Start();
        // The UI, audio and visual parts start after the core and before any package that depends on ForgeCore, in this order. Each keeps its
        // own configuration file, log source and Harmony ID (NAinfini.ForgeUI, NAinfini.ForgeAudio, NAinfini.ForgeVisual), so players keep their settings.
        // The frame's clock and screen size are read first, so what the parts build at load sees real values.
        Frame.Stamp();
        timing.Mark("ui");
        ForgeUI.Plugin.Load();
        timing.Mark("audio");
        ForgeAudio.Plugin.Load();
        timing.Mark("visual");
        ForgeVisual.Plugin.Load();
        AddComponent<CoreDriver>();
        timing.Complete();
    }

    public override bool Unload() => false;
}

/// <summary>The only injected type: it answers network handshakes, runs the enemy slow zones' passes, the landing work, the UI, audio and
/// visual parts' frame steps (direct calls, each failing alone) and then every mod's <see cref="Frame"/> callbacks. Nothing here allocates on an ordinary frame.</summary>
internal sealed class CoreDriver : MonoBehaviour
{
    public CoreDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        Frame.Stamp();
        try
        {
            Net.Node!.Tick(Time.realtimeSinceStartup);
            SoloDrop.Tick();
            Enemies.TickZones(Time.time);
            LandingWork.Tick();
        }
        catch (Exception error) { Failed("core", error); }
        try { ForgeUI.UIKitFrame.Update(); } catch (Exception error) { Failed("ui", error); }
        try { ForgeAudio.AudioDriver.Update(); } catch (Exception error) { Failed("audio", error); }
        try { ForgeVisual.VisualFrame.Update(); } catch (Exception error) { Failed("visual", error); }
        Frame.Updates.Run();
    }

    private void LateUpdate()
    {
        try { ForgeUI.UIKitFrame.LateUpdate(); } catch (Exception error) { Failed("ui late", error); }
        try { ForgeVisual.VisualFrame.LateUpdate(); } catch (Exception error) { Failed("visual late", error); }
        Frame.LateUpdates.Run();
    }

    private void FixedUpdate() => Frame.FixedUpdates.Run();

    private void OnDestroy()
    {
        try { ForgeAudio.AudioDriver.Shutdown(); } catch (Exception error) { Failed("audio quit", error); }
        Frame.Quits.Run();
    }

    private static void Failed(string part, Exception error) => Plugin.Logger.ErrorOnce("core-frame-update-failed", "ForgeCore frame update (" + part + ") failed: " + error);
}
