using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeCore;

/// <summary>
/// Direct engine calls for per-frame reads (the outline's and the world features': enemy health bars, world waypoints, hit feedback).
/// The generated wrappers of these Unity members return their struct through the game's boxing invoke, which allocates on the game's
/// heap every call; these resolve the engine's own internal calls once and write straight into a local. All are present in this game
/// build (their names are in <c>GameAssembly.dll</c>'s internal-call table); the renderer bounds call is optional (<see cref="HasBounds"/>).
/// </summary>
internal static class EngineCalls
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadVector(IntPtr self, out Vector3 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadQuaternion(IntPtr self, out Quaternion value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadMatrix(IntPtr self, out Matrix4x4 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadScene(out PhysicsSceneHandle scene);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RaycastTest(ref PhysicsSceneHandle scene, ref RayData ray, float maxDistance, int layerMask, int triggers);

    /// <summary>Unity's <c>PhysicsScene</c>: one handle.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PhysicsSceneHandle { public int Handle; }

    /// <summary>Unity's <c>Ray</c>: origin, then a unit direction.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RayData { public Vector3 Origin, Direction; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadBounds(IntPtr self, out BoundsData value);

    /// <summary>Unity's <c>Bounds</c>: centre, then half-size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BoundsData { public Vector3 Center, Extents; }

    private static ReadBounds? _bounds;
    private static ReadVector? _position;
    private static ReadQuaternion? _rotation;
    private static ReadMatrix? _view, _projection;
    private static ReadInt? _screenHeight;
    private static RaycastTest? _raycast;
    private static PhysicsSceneHandle _scene;
    private static bool _sceneRead;
    private static string? _failure;

    /// <summary>After <see cref="Prepare"/>: the renderer bounds call exists (each user says what it does without it).</summary>
    internal static bool HasBounds => _bounds != null;

    /// <summary>Resolves every call once. False (and <paramref name="why"/>) when the engine lacks one of them.</summary>
    internal static bool Prepare(out string why)
    {
        why = _failure ?? "";
        if (_failure != null) return false;
        if (_position != null) return true;
        try
        {
            _position = IL2CPP.ResolveICall<ReadVector>("UnityEngine.Transform::get_position_Injected");
            _rotation = IL2CPP.ResolveICall<ReadQuaternion>("UnityEngine.Transform::get_rotation_Injected");
            _view = IL2CPP.ResolveICall<ReadMatrix>("UnityEngine.Camera::get_worldToCameraMatrix_Injected");
            _projection = IL2CPP.ResolveICall<ReadMatrix>("UnityEngine.Camera::get_projectionMatrix_Injected");
            _screenHeight = IL2CPP.ResolveICall<ReadInt>("UnityEngine.Screen::get_height");
            _raycast = IL2CPP.ResolveICall<RaycastTest>("UnityEngine.PhysicsScene::Internal_RaycastTest_Injected");
            var scene = IL2CPP.ResolveICall<ReadScene>("UnityEngine.Physics::get_defaultPhysicsScene_Injected");
            scene(out _scene);
            _sceneRead = true;
            try { _bounds = IL2CPP.ResolveICall<ReadBounds>("UnityEngine.Renderer::get_bounds_Injected"); }
            catch (Exception) { _bounds = null; }
            return true;
        }
        catch (Exception error)
        {
            _position = null;
            why = _failure = error.ToString();
            return false;
        }
    }

    internal static Vector3 Position(Transform t)
    {
        _position!(t.Pointer, out var p);
        return p;
    }

    internal static Quaternion Rotation(Transform t)
    {
        _rotation!(t.Pointer, out var q);
        return q;
    }

    /// <summary>A renderer's world bounds (centre and half-size): where its body is drawn this frame, a skinned body following its bones.
    /// False when the engine lacks the call.</summary>
    internal static bool Bounds(Renderer r, out Vector3 center, out Vector3 extents)
    {
        center = extents = default;
        if (_bounds == null) return false;
        _bounds(r.Pointer, out var b);
        center = b.Center;
        extents = b.Extents;
        return true;
    }

    internal static int ScreenHeight() => _screenHeight!();

    /// <summary>The camera's world-to-camera and projection matrices.</summary>
    internal static void Matrices(Camera camera, out Matrix4x4 view, out Matrix4x4 projection)
    {
        _view!(camera.Pointer, out view);
        _projection!(camera.Pointer, out projection);
    }

    /// <summary>True when something on <paramref name="mask"/> lies between <paramref name="from"/> and <paramref name="to"/>
    /// (triggers ignored). Plain float maths: the interop's vector operators allocate.</summary>
    internal static bool Blocked(Vector3 from, Vector3 to, int mask)
    {
        float dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance < 0.01f || !_sceneRead) return false;
        var ray = new RayData { Origin = from, Direction = new Vector3(dx / distance, dy / distance, dz / distance) };
        return _raycast!(ref _scene, ref ray, distance, mask, (int)QueryTriggerInteraction.Ignore);
    }
}
