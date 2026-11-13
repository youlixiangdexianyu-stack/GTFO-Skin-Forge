using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>The shape of a drawn zone volume. The size passed to <see cref="ZoneVolume.Resize"/> reads per shape:
/// a box is X by Y by Z, a sphere is its diameter (X), a cylinder is diameter (X) by height (Y), a capsule is diameter (X) by total height (Y).</summary>
public enum ZoneShape { Box, Sphere, Cylinder, Capsule }

/// <summary>How a zone volume is drawn: wire lines, a see-through solid, or the solid plus a pulsing disc on the floor.</summary>
public enum ZoneStyle { Outline, Solid, Sensor }

/// <summary>The colour and strength of a drawn zone. Opacity and glow are used as given; the shader saturates them.</summary>
public readonly record struct ZoneLook(ZoneStyle Style, float Red, float Green, float Blue, float Opacity, float Glow);

/// <summary>One zone drawn with the library's own geometry and shader (independent of any game material). The vertex buffers
/// and line objects are built once; <see cref="Resize"/> only rewrites them and <see cref="Pulse"/> only changes a material value.
/// With no look the object is just a root other parts (a label) can hang from.</summary>
public sealed class ZoneVolume : IDisposable
{
    public const float LineWidth = 0.04f;
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity"), GlowId = Shader.PropertyToID("_Glow"), PulseId = Shader.PropertyToID("_Pulse");
    private static bool _shaderTried;
    private static Shader? _shader;

    private readonly ZoneShape _shape;
    private readonly GameObject _root, _body;
    private readonly Transform _rootTransform, _bodyTransform;
    private readonly GameObject? _discObject;
    private readonly Material? _material;
    private readonly ZoneMesh? _volume, _disc;
    private readonly List<(LineRenderer Line, Point[] Points)> _lines = new();
    private readonly bool _sensor;
    private Vector3 _position;
    private Quaternion _rotation;
    private bool _poseKnown, _shown;
    private float _x = float.NaN, _y, _z;

    private readonly record struct Point(float X, float Y, float Z, float Spine = 0);

    private ZoneVolume(string name, ZoneShape shape, ZoneLook? look)
    {
        _shape = shape;
        _root = new GameObject(name);
        try
        {
            _body = new GameObject("appearance"); _body.transform.SetParent(_root.transform, false);
            _rootTransform = _root.transform; _bodyTransform = _body.transform;
            if (look is { } appearance)
            {
                _material = new Material(LoadShader()) { name = "ForgeVisual zone appearance", color = new Color(appearance.Red, appearance.Green, appearance.Blue, 1) };
                _material.SetFloat(OpacityId, Math.Clamp(appearance.Opacity, 0, 1));
                _material.SetFloat(GlowId, Math.Max(appearance.Glow, 0));
                _material.SetFloat(PulseId, 1);
                _sensor = appearance.Style == ZoneStyle.Sensor;
                if (appearance.Style == ZoneStyle.Outline) BuildLines();
                else
                {
                    _volume = ZoneMesh.Create(shape);
                    _body.AddComponent<MeshFilter>().sharedMesh = _volume.Mesh;
                    _body.AddComponent<MeshRenderer>().sharedMaterial = _material;
                    if (_sensor)
                    {
                        _disc = ZoneMesh.Create(shape, true);
                        _discObject = new GameObject("ground disc"); _discObject.transform.SetParent(_root.transform, false);
                        _discObject.transform.localPosition = new Vector3(0, 0.01f, 0);
                        _discObject.AddComponent<MeshFilter>().sharedMesh = _disc.Mesh;
                        _discObject.AddComponent<MeshRenderer>().sharedMaterial = _material;
                    }
                }
            }
            _body.SetActive(false); _discObject?.SetActive(false);
        }
        catch
        {
            // A half-built volume would stay in the scene with no owner to destroy it.
            _volume?.Dispose(); _disc?.Dispose();
            if (_material != null) UnityEngine.Object.Destroy(_material);
            UnityEngine.Object.Destroy(_root);
            throw;
        }
    }

    /// <summary>Builds a volume, hidden until <see cref="SetShown"/>. Throws when the library's zone shader cannot be loaded.</summary>
    public static ZoneVolume Create(string name, ZoneShape shape, ZoneLook? look) => new(name, shape, look);

    /// <summary>The root object: it follows <see cref="SetPose"/>'s position, and parts that should move with the zone (a label) are parented here.</summary>
    public Transform Root => _rootTransform;

    /// <summary>Moves the root to <paramref name="position"/> and turns the body (not the floor disc) to <paramref name="rotation"/>; unchanged values are not rewritten.</summary>
    public void SetPose(Vector3 position, Quaternion rotation)
    {
        if (Gone()) return;
        if (!_poseKnown || !Same(_position, position)) { _rootTransform.position = position; _position = position; }
        if (!_poseKnown || !Same(_rotation, rotation)) { _bodyTransform.rotation = rotation; _rotation = rotation; }
        _poseKnown = true;
    }

    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _body.SetActive(shown); _discObject?.SetActive(shown); _shown = shown;
    }

    /// <summary>Sets the volume size when it differs from the last one; see <see cref="ZoneShape"/> for what each value means per shape.</summary>
    public void Resize(float sizeX, float sizeY, float sizeZ)
    {
        if (_x == sizeX && _y == sizeY && _z == sizeZ) return;
        _x = sizeX; _y = sizeY; _z = sizeZ;
        _volume?.Resize(sizeX, sizeY, sizeZ); _disc?.Resize(sizeX, sizeY, sizeZ);
        var y = _shape is ZoneShape.Sphere or ZoneShape.Capsule ? sizeX : sizeY;
        var z = _shape == ZoneShape.Box ? sizeZ : sizeX;
        var spine = _shape == ZoneShape.Capsule ? Math.Max((sizeY - sizeX) / 2, 0) : 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            var (line, points) = _lines[i];
            for (var j = 0; j < points.Length; j++) { var p = points[j]; line.SetPosition(j, new Vector3(p.X * sizeX, p.Y * y + p.Spine * spine, p.Z * z)); }
        }
    }

    /// <summary>A sensor's breathing: brightness and opacity follow a one-second sine of <paramref name="seconds"/>. Other styles ignore it.</summary>
    public void Pulse(double seconds)
    {
        if (_sensor && _material != null) _material.SetFloat(PulseId, (float)(0.75 + 0.25 * Math.Sin(seconds * Math.PI * 2)));
    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(_root); _volume?.Dispose(); _disc?.Dispose();
        if (_material != null) UnityEngine.Object.Destroy(_material);
    }

    private bool Gone() => _root == null || _root.WasCollected || _rootTransform == null || _rootTransform.WasCollected
        || _bodyTransform == null || _bodyTransform.WasCollected;

    private void BuildLines()
    {
        void Line(Point[] points, bool closed = true)
        {
            var child = new GameObject("outline"); child.transform.SetParent(_body.transform, false);
            var line = child.AddComponent<LineRenderer>(); line.useWorldSpace = false; line.loop = closed;
            line.sharedMaterial = _material; line.startColor = line.endColor = new Color(1, 1, 1, 1);
            line.startWidth = line.endWidth = LineWidth; line.positionCount = points.Length;
            _lines.Add((line, points));
        }
        void Circle(int plane, float height = 0, float spine = 0)
        {
            var points = new Point[48];
            for (var i = 0; i < points.Length; i++)
            {
                var a = i * MathF.PI * 2 / points.Length; var x = MathF.Cos(a) / 2; var y = MathF.Sin(a) / 2;
                points[i] = plane == 0 ? new Point(x, height, y, spine) : plane == 1 ? new Point(x, y, 0) : new Point(0, y, x);
                if (_shape == ZoneShape.Capsule && plane != 0) points[i] = points[i] with { Spine = y >= 0 ? 1 : -1 };
            }
            Line(points);
        }
        if (_shape == ZoneShape.Box)
        {
            foreach (var y in new[] { -0.5f, 0.5f }) Line(new[] { new Point(-0.5f, y, -0.5f), new Point(0.5f, y, -0.5f), new Point(0.5f, y, 0.5f), new Point(-0.5f, y, 0.5f) });
            foreach (var x in new[] { -0.5f, 0.5f }) foreach (var z in new[] { -0.5f, 0.5f }) Line(new[] { new Point(x, -0.5f, z), new Point(x, 0.5f, z) }, false);
        }
        else if (_shape == ZoneShape.Cylinder)
        {
            Circle(0, -0.5f); Circle(0, 0.5f);
            for (var i = 0; i < 4; i++) { var a = i * MathF.PI / 2; var x = MathF.Cos(a) / 2; var z = MathF.Sin(a) / 2; Line(new[] { new Point(x, -0.5f, z), new Point(x, 0.5f, z) }, false); }
        }
        else if (_shape == ZoneShape.Capsule) { Circle(0, 0, 1); Circle(0, 0, -1); Circle(1); Circle(2); }
        else { Circle(0); Circle(1); Circle(2); }
    }

    private static Shader LoadShader()
    {
        if (_shader != null && !_shader.WasCollected) return _shader;
        if (_shaderTried) throw new InvalidOperationException("ForgeVisual's zone shader could not be loaded earlier.");
        _shaderTried = true;
        var bundle = ForgeCore.Assets.LoadBytes(Plugin.Id, "forgevisual-zone-shader", ZoneShaderBytes.Read())
            ?? throw new InvalidOperationException("ForgeVisual's zone shader bundle cannot be loaded.");
        var shader = ForgeCore.Assets.Get<Shader>(Plugin.Id, bundle, "Assets/TriggerZone.shader")
            ?? throw new InvalidOperationException("ForgeVisual's zone shader asset is missing.");
        if (!shader.isSupported) throw new InvalidOperationException("ForgeVisual's zone shader is not supported on this device.");
        return _shader = shader;
    }

    private static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    private static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
}
