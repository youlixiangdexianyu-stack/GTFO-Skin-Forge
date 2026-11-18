using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The player's view of the world for everything the kit draws over it (world points, enemy health bars, hit feedback): the camera the
/// player looks through (<see cref="HudScreen.WorldCamera"/>, looked up again every half second or when it is destroyed), its eye and its
/// world-to-clip transform, read once per frame through <see cref="ForgeCore.EngineCalls"/>, so reading allocates nothing.
/// </summary>
internal static class WorldView
{
    private const float CameraRecheck = 0.5f;
    private static Camera? _camera;
    private static Transform? _transform;
    private static float _nextCamera;
    private static int _frame = -1;
    private static bool _ready, _failed;
    private static ClipTransform _clip;
    private static Vector3 _eye;

    /// <summary>The view of this frame; false while there is no camera or the engine calls are missing (logged once).</summary>
    internal static bool Read(out ClipTransform clip, out Vector3 eye)
    {
        var frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            _ready = Refresh();
        }
        clip = _clip;
        eye = _eye;
        return _ready;
    }

    private static bool Refresh()
    {
        if (_failed) return false;
        if (!ForgeCore.EngineCalls.Prepare(out var why))
        {
            _failed = true;
            Plugin.Error("ui-nothing-drawn-over-world-world-points", "Nothing is drawn over the world (world points, enemy health bars, hit feedback): an engine call is missing (" + why + ").");
            return false;
        }
        // Only the fallback for an enemy without limbs needs it, so a build without it keeps the bars (they sit on the eyes then).
        if (!ForgeCore.EngineCalls.HasBounds) Plugin.Error("ui-renderer-bounds-call-missing-so-enemy", "The renderer bounds call is missing, so enemy bars without limbs sit on the eyes.");
        var now = ForgeCore.Frame.GameTime;
        if (ForgeCore.NativeObjects.Gone(_camera) || ForgeCore.NativeObjects.Gone(_transform) || now >= _nextCamera)
        {
            _nextCamera = now + CameraRecheck;
            var camera = HudScreen.WorldCamera();
            if (camera == null) { _camera = null; _transform = null; return false; }
            if (_camera == null || _camera.Pointer != camera.Pointer) { _camera = camera; _transform = camera.transform; }
        }
        _eye = ForgeCore.EngineCalls.Position(_transform!);
        // Multiplied here (projection × view): the interop's matrix operators go through the game's boxing invoke.
        ForgeCore.EngineCalls.Matrices(_camera!, out var v, out var p);
        _clip = ClipTransform.Combine(
            new ClipTransform(v.m00, v.m01, v.m02, v.m03, v.m10, v.m11, v.m12, v.m13, v.m20, v.m21, v.m22, v.m23, v.m30, v.m31, v.m32, v.m33),
            new ClipTransform(p.m00, p.m01, p.m02, p.m03, p.m10, p.m11, p.m12, p.m13, p.m20, p.m21, p.m22, p.m23, p.m30, p.m31, p.m32, p.m33));
        return true;
    }
}
