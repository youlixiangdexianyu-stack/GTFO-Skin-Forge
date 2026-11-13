using System;
using BepInEx.Configuration;
using ForgeCore;
using UnityEngine;

namespace ForgeVisual;

/// <summary>The visual part of ForgeCore: started by ForgeCore's plugin (<see cref="Load"/>), stepped by its driver (<see cref="VisualFrame"/>).
/// <see cref="Id"/> names its configuration file, log source and Harmony patches. Its effect bundles, shared marks and shaders are made ready at landing (<see cref="ShaderWarm"/>).</summary>
internal static class Plugin
{
    internal const string Id = "NAinfini.ForgeVisual";

    internal static ModLog Logger { get; private set; } = null!;
    internal static ConfigEntry<bool> FlashLights { get; private set; } = null!;
    internal static ConfigEntry<float> ShakeStrength { get; private set; } = null!;
    internal static ConfigEntry<bool> Outlines { get; private set; } = null!;

    internal static void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        FlashLights = Settings.Bind(Id, "Effects", "FlashLights", true,
            "Short point lights at blasts, hits and muzzles drawn through ForgeVisual, for every mod together. Lights held on burning or glowing things stay.");
        ShakeStrength = Settings.Bind(Id, "Camera", "ShakeStrength", 1f,
            "How strongly blasts, landings and hits drawn through ForgeVisual shake your view, from 0 (off) to 1 (full), for every mod together.");
        Outlines = Settings.Bind(Id, "Outlines", "Enabled", true,
            "Draws the outlines mods put round enemies and objects (Forge Frames' cockpit and others), including those seen through walls. Turn off to hide them in every mod.");
        var culling = Settings.Bind(Id, "Culling", "Enabled", true,
            "Pauses and hides effects that are far from you or outside your view, and brings them back when you look their way. Large effects (blasts, air strikes) are drawn from any distance. Turn off to draw every effect always.");
        var distance = Settings.Bind(Id, "Culling", "Distance", 70f,
            "Metres beyond which an effect is paused and hidden (measured to its edge), 10 to 1000. A slot that names its own distance keeps it.");
        FxCulling.Enabled = culling.Value;
        FxCulling.Distance = Math.Clamp(float.IsFinite(distance.Value) ? distance.Value : 70f, 10f, 1000f);
        culling.SettingChanged += (_, _) => FxCulling.Enabled = culling.Value;
        distance.SettingChanged += (_, _) => FxCulling.Distance = Math.Clamp(float.IsFinite(distance.Value) ? distance.Value : 70f, 10f, 1000f);
        Scene.Subscribe();
        ShaderWarm.Queue();
    }
}

/// <summary>Once a frame, called by ForgeCore's driver, it steps everything this library draws.
/// An ordinary frame allocates nothing.</summary>
internal static class VisualFrame
{
    internal static void Update()
    {
        try { GunMount.TickAll(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-gun-model-update-failed", "ForgeVisual gun model update failed: " + error); }
        try { Scene.Tick(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-frame-update-failed", "ForgeVisual frame update failed: " + error); }
    }

    /// <summary>Joint clips, the pose copies that follow a body, then the outlines, after animation and movement, before the cameras render.</summary>
    internal static void LateUpdate()
    {
        try { JointPlayer.TickAll(); } catch (Exception error) { Scene.StepFailed("joint clips", error); }
        try { PoseCopy.TickAll(); } catch (Exception error) { Scene.StepFailed("pose copies", error); }
        try { FirstPersonArms.TickAll(); } catch (Exception error) { Scene.StepFailed("first-person arms", error); }
        try { OutlineDraw.Tick(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-outline-update-failed", "ForgeVisual outline update failed: " + error); }
    }
}

/// <summary>What every part of the library reads each frame (time, the local eye), and the one place that resets everything this
/// library holds when the level ends or a checkpoint is reloaded (both by the game's events). The pools themselves outlive levels; what they show does not.</summary>
internal static class Scene
{
    private static int _eyeFrame = -1;
    private static Vector3 _eye;
    private static bool _hasEye;
    private static float _tanHalfFov = 0.7f;
    private static Vector3 _forward = Vector3.forward;
    private static float _tanCone = 2f;

    internal static float Now { get; private set; }
    internal static float Delta { get; private set; }

    /// <summary>The local eye: read from the game once a frame (by <see cref="Tick"/>, or by the first caller before it), never more.</summary>
    internal static Vector3 Eye { get { ReadEye(); return _eye; } }
    internal static bool HasEye { get { ReadEye(); return _hasEye; } }

    /// <summary>The direction the eye looks in (a unit vector) and the tangent of the half angle of a cone holding the whole view (<see cref="CullMath.TanCone"/>), read with the eye.</summary>
    internal static Vector3 EyeForward { get { ReadEye(); return _forward; } }
    internal static float TanCone { get { ReadEye(); return _tanCone; } }

    /// <summary>Tangent of half the camera's vertical field of view, read at most once a frame.</summary>
    internal static float TanHalfFov { get { ReadEye(); return _tanHalfFov; } }

    /// <summary>Clears what is shown when the level ends or a checkpoint reloads (the game's own events, not a per-frame check).</summary>
    internal static void Subscribe()
    {
        GameEvents.LevelEnded += () => { LitMaterial.SweepDead(); Clear(); };
        GameEvents.CheckpointReloaded += () => Clear();
    }

    internal static int WorldMask => LayerManager.MASK_WORLD;

    internal static void Tick()
    {
        Now = ForgeCore.Frame.GameTime;
        Delta = Time.deltaTime;
        ReadEye();
        // One step failing must not stop the ones after it every frame: each runs alone and a failure is logged once per step. Direct calls, not
        // delegates, so the hot-path getter walk sees into them.
        try { Ambient.Tick(Delta); } catch (Exception error) { StepFailed("ambient", error); }
        try { SpritePool.Tick(Now); } catch (Exception error) { StepFailed("sprites", error); }
        try { PointLights.Tick(Now); } catch (Exception error) { StepFailed("point lights", error); }
        try { Fx.Tick(Now); } catch (Exception error) { StepFailed("effects", error); }
        try { FxInstancePool.TickAll(Now); } catch (Exception error) { StepFailed("effect instances", error); }
        try { GroundMarker.TickAll(Now); } catch (Exception error) { StepFailed("ground markers", error); }
        try { Beams.TickAll(Now); } catch (Exception error) { StepFailed("beams", error); }
        try { Trails.Tick(Now); } catch (Exception error) { StepFailed("trails", error); }
        try { GlowSpot.TickAll(Now); } catch (Exception error) { StepFailed("glow spots", error); }
    }

    internal static void StepFailed(string step, Exception error)
    {
        Plugin.Logger.ErrorOnce("visual-frame-update-failed", "ForgeVisual frame update (" + step + ") failed: " + error);
    }

    private static void ReadEye()
    {
        var frame = Time.frameCount;
        if (_eyeFrame == frame) return;
        _eyeFrame = frame;
        var camera = ForgeCore.Players.WorldCamera();
        var eye = camera == null ? null : camera.transform;
        _hasEye = eye != null;
        if (eye != null) { _eye = eye.position; _forward = eye.forward; }
        if (camera != null)
        {
            _tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _tanCone = CullMath.TanCone(_tanHalfFov, camera.aspect);
            if (camera.Pointer != _softCamera) { _softCamera = camera.Pointer; SoftDepth(camera); }
        }
    }

    private static readonly int SoftDepthId = Shader.PropertyToID("_ForgeSoftDepth");
    private static float _softDepth = -1f;
    private static IntPtr _softCamera; // the camera SoftDepth last judged: judged again only for another camera object or after a level (Clear)

    /// <summary>Soft particles (the effect shaders' ForgeSoft.cginc) fade a particle where it meets the scene by the camera's depth texture; they are on only
    /// while the eye's camera has one: the player camera renders deferred, which always does, at no extra cost. Without it they draw hard edges, never nothing.</summary>
    private static void SoftDepth(Camera camera)
    {
        var on = camera.actualRenderingPath == RenderingPath.DeferredShading || (camera.depthTextureMode & DepthTextureMode.Depth) != 0 ? 1f : 0f;
        if (on == _softDepth) return;
        _softDepth = on;
        Shader.SetGlobalFloat(SoftDepthId, on);
    }

    internal static void Clear()
    {
        _softCamera = IntPtr.Zero;
        FirstPersonArms.StopAll();
        JointPlayer.StopAll();
        PoseCopy.StopAll();
        HeldOff.Forget();
        SpritePool.Clear();
        PointLights.Clear();
        CameraShake.Clear();
        FxInstancePool.ClearAll();
        ParticlePool.ClearAll();
        Fx.Clear();
        GroundMarker.ClearAll();
        Beams.ClearAll();
        Trails.ClearAll();
        GlowSpot.ClearAll();
        OutlineDraw.Clear();
    }
}
