using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>The screen as the game's HUD sees it, in the weapon list's space: what the HUD regions are laid out on, and where world markers go.
/// The HUD is drawn by the game's UI camera (<c>CellUI_Camera</c>, a perspective camera, disabled and rendered by the game's UI
/// pass) that draws the weapon list's layer. Every HUD element lies on the weapon list's plane. The screen's middle and edges are found
/// by casting that camera's rays through the middle and the four edge midpoints of its view onto that plane and reading the hits in the
/// list's local space (HUD units). The HUD sways, so one measurement wanders; <see cref="ScreenSampler"/> keeps the median of a few and
/// holds it until the resolution, the camera's field of view or the HUD's scale changes. Anything the game draws elsewhere (the vitals
/// stand on a plane of their own) is placed on the list's plane the way the camera sees it (<see cref="Project"/>).</summary>
internal static class HudScreen
{
    private const float Rescan = 1f;

    private static readonly ScreenSampler Sampler = new();
    private static Camera? _camera;
    private static float _nextScan;
    private static bool _missingLogged;
    private static Plane _plane;
    private static Transform? _list;

    /// <summary>The middle of the screen in the weapon list's local space, and half the screen's size, in HUD units (settled).</summary>
    internal static float CentreX, CentreY, HalfWidth, HalfHeight;

    /// <summary>Counts settled screens; everything laid out on the old one is laid out again.</summary>
    internal static int Version => Sampler.Version;

    /// <summary>The HUD camera, once found.</summary>
    internal static Camera? Camera => _camera != null && !_camera.WasCollected ? _camera : null;

    /// <summary>Forgets the settled screen (the game made a new HUD).</summary>
    internal static void Reset()
    {
        Sampler.Reset();
    }

    /// <summary>Settles the screen for this frame. False until a settled screen exists; nothing may be laid out before.</summary>
    internal static bool Measure(PUI_Inventory list)
    {
        var camera = HudCamera(list);
        if (camera == null) return false;
        var listTransform = list.transform;
        _list = listTransform;
        _plane = new Plane(listTransform.forward, listTransform.position);
        var key = new ScreenKey(ForgeCore.Frame.ScreenWidth, ForgeCore.Frame.ScreenHeight, camera.orthographic ? camera.orthographicSize : camera.fieldOfView, listTransform.lossyScale.x);
        ScreenSample? sample = null;
        if (Sampler.Sampling)
        {
            if (OnList(camera, 0.5f, 0.5f, out var centre, out _) && OnList(camera, 0f, 0.5f, out var left, out _) && OnList(camera, 1f, 0.5f, out var right, out _)
                && OnList(camera, 0.5f, 0f, out var bottom, out _) && OnList(camera, 0.5f, 1f, out var top, out _))
                sample = new ScreenSample(centre.x, centre.y, Math.Abs(right.x - left.x) / 2f, Math.Abs(top.y - bottom.y) / 2f);
        }
        Sampler.Add(key, sample);
        if (!Sampler.Ready) return false;
        var settled = Sampler.Settled;
        CentreX = settled.CentreX;
        CentreY = settled.CentreY;
        HalfWidth = settled.HalfWidth;
        HalfHeight = settled.HalfHeight;
        return true;
    }

    /// <summary>Where the camera's ray through viewport point (<paramref name="vx"/>, <paramref name="vy"/>) meets the list's plane, in the
    /// list's local space; <paramref name="depth"/> is the distance along the ray.</summary>
    private static bool OnList(Camera camera, float vx, float vy, out Vector3 local, out float depth)
    {
        local = default;
        var ray = camera.ViewportPointToRay(new Vector3(vx, vy, 0f));
        if (!_plane.Raycast(ray, out depth) || !float.IsFinite(depth) || depth <= 0f) return false;
        local = _list!.InverseTransformPoint(ray.GetPoint(depth));
        return float.IsFinite(local.x) && float.IsFinite(local.y);
    }

    /// <summary>Widens the box (list space) by world <paramref name="bounds"/> as the HUD camera shows them on the list's plane: each
    /// corner goes to the screen and back along the camera's ray onto the plane. False (nothing added) without a camera or a plane hit.</summary>
    internal static bool Project(Bounds bounds, ref float left, ref float bottom, ref float right, ref float top)
    {
        var camera = Camera;
        if (camera == null || _list == null) return false;
        var min = bounds.min;
        var max = bounds.max;
        if (max.x - min.x <= 1e-6f && max.y - min.y <= 1e-6f) return false;
        var any = false;
        for (var corner = 0; corner < 8; corner++)
        {
            var world = new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y, (corner & 4) == 0 ? min.z : max.z);
            var view = camera.WorldToViewportPoint(world);
            if (view.z <= 0f || !OnList(camera, view.x, view.y, out var local, out _)) continue;
            left = Math.Min(left, local.x);
            right = Math.Max(right, local.x);
            bottom = Math.Min(bottom, local.y);
            top = Math.Max(top, local.y);
            any = true;
        }
        return any;
    }

    /// <summary>The point of the weapon list's space (HUD units) that the HUD camera shows at viewport point (<paramref name="vx"/>,
    /// <paramref name="vy"/>); false without a camera or a plane hit.</summary>
    internal static bool FromViewport(float vx, float vy, out float x, out float y)
    {
        x = y = 0f;
        var camera = Camera;
        if (camera == null || _list == null || !OnList(camera, vx, vy, out var local, out _)) return false;
        x = local.x;
        y = local.y;
        return true;
    }

    /// <summary>A box in the list's space as a viewport rectangle (0..1, y up); false without a camera.</summary>
    internal static bool ToViewport(HudBox box, out Vector2 low, out Vector2 high)
    {
        low = high = default;
        var camera = Camera;
        if (camera == null || _list == null) return false;
        var a = camera.WorldToViewportPoint(_list.TransformPoint(new Vector3(box.Left, box.Bottom, 0f)));
        var b = camera.WorldToViewportPoint(_list.TransformPoint(new Vector3(box.Right, box.Top, 0f)));
        low = new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y));
        high = new Vector2(Math.Max(a.x, b.x), Math.Max(a.y, b.y));
        return true;
    }

    internal static bool WorldToHud(HudElement element, Vector3 world, out Vector2 point, out bool behind)
    {
        point = default;
        behind = false;
        if (HalfWidth <= 0f) return false;
        var camera = WorldCamera();
        if (camera == null) return false;
        var view = camera.WorldToViewportPoint(world);
        behind = view.z < 0f;
        var (x, y) = ScreenMath.ViewportToHud(view.x, view.y, HalfWidth, HalfHeight);
        point = new Vector2(x, y);
        return true;
    }

    /// <summary>The camera the player looks through: the game's current one, else the local player's first-person camera.</summary>
    internal static Camera? WorldCamera()
    {
        var frame = Time.frameCount;
        if (_worldCameraFrame == frame && (_worldCamera == null || !_worldCamera.WasCollected)) return _worldCamera;
        _worldCameraFrame = frame;
        return _worldCamera = ForgeCore.Players.WorldCamera();
    }

    private static int _worldCameraFrame = -1;
    private static Camera? _worldCamera;

    /// <summary>The camera that draws the HUD's layer (perspective or orthographic): one under the game's GUI root first (the game's UI
    /// cameras are disabled and rendered by its own UI pass), else an enabled one.</summary>
    private static Camera? HudCamera(PUI_Inventory list)
    {
        if (_camera != null && !_camera.WasCollected) return _camera;
        _camera = null;
        var now = ForgeCore.Frame.GameTime;
        if (now < _nextScan) return null;
        _nextScan = now + Rescan;
        var bit = 1 << list.gameObject.layer;
        var gui = GuiManager.Current != null ? GuiManager.Current.m_root : null;
        if (gui != null)
            foreach (var camera in gui.GetComponentsInChildren<Camera>(true))
                if ((camera.cullingMask & bit) != 0) return Found(camera);
        foreach (var camera in UnityEngine.Camera.allCameras)
            if ((camera.cullingMask & bit) != 0) return Found(camera);
        if (!_missingLogged)
        {
            _missingLogged = true;
            Plugin.Error("ui-no-camera-draws-hud-layer-hud", "No camera draws the HUD layer " + list.gameObject.layer + "; HUD elements stay off.");
        }
        return null;
    }

    private static Camera Found(Camera camera)
    {
        _camera = camera;
        return camera;
    }
}
