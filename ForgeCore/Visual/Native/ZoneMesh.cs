using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeVisual;

/// <summary>Forge geometry, independent of the game's baked sensor prefab/material. Native vertex buffers and
/// topology are created once. Resizes only overwrite that buffer; pulses only change the material colour.</summary>
internal sealed class ZoneMesh : IDisposable
{
    private readonly Vector3[] _unit;
    private readonly Il2CppStructArray<Vector3> _vertices;
    private readonly float[] _spine;
    private readonly bool _disc;
    private readonly ZoneShape _shape;
    internal Mesh Mesh { get; }
    private ZoneMesh(ZoneShape shape, Vector3[] unit, float[] spine, int[] triangles, bool disc)
    {
        _shape = shape; _unit = unit; _spine = spine; _disc = disc;
        _vertices = new Il2CppStructArray<Vector3>(unit.Length);
        var colors = new Color[unit.Length];
        Array.Fill(colors, new Color(1, 1, 1, 1));
        Mesh = new Mesh
        {
            name = "ForgeVisual zone volume",
            vertices = _vertices,
            colors = new Il2CppStructArray<Color>(colors),
            triangles = new Il2CppStructArray<int>(triangles)
        };
    }

    internal static ZoneMesh Create(ZoneShape shape, bool disc = false)
    {
        const int sides = 48, rings = 16;
        var points = new List<Vector3>(); var spine = new List<float>(); var triangles = new List<int>();
        void Add(float x, float y, float z, float s = 0) { points.Add(new Vector3(x, y, z)); spine.Add(s); }
        void Face(int a, int b, int c)
        { triangles.Add(a); triangles.Add(b); triangles.Add(c); triangles.Add(c); triangles.Add(b); triangles.Add(a); }
        if (disc || shape == ZoneShape.Cylinder)
        {
            var rows = disc ? 1 : 2;
            for (var row = 0; row < rows; row++)
                for (var i = 0; i < sides; i++)
                { var angle = i * MathF.PI * 2 / sides; Add(MathF.Cos(angle) / 2, disc ? 0 : row - 0.5f, MathF.Sin(angle) / 2); }
            var bottom = points.Count; Add(0, disc ? 0 : -0.5f, 0);
            var top = points.Count; if (!disc) Add(0, 0.5f, 0);
            for (var i = 0; i < sides; i++)
            {
                var n = (i + 1) % sides; Face(bottom, i, n);
                if (!disc) { Face(top, sides + n, sides + i); Face(i, sides + i, n); Face(n, sides + i, sides + n); }
            }
        }
        else if (shape == ZoneShape.Box)
        {
            for (var i = 0; i < 8; i++) Add((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            foreach (var f in new[] { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } })
            { Face(f[0], f[1], f[2]); Face(f[0], f[2], f[3]); }
        }
        else
        {
            // Two equator rows let a capsule keep spherical caps while its spine changes height.
            var count = shape == ZoneShape.Capsule ? rings + 2 : rings + 1;
            for (var row = 0; row < count; row++)
            {
                var latitude = shape == ZoneShape.Capsule && row > rings / 2 ? row - 1 : row;
                var angle = latitude * MathF.PI / rings;
                for (var i = 0; i < sides; i++)
                {
                    var longitude = i * MathF.PI * 2 / sides;
                    Add(MathF.Sin(angle) * MathF.Cos(longitude) / 2, MathF.Cos(angle) / 2,
                        MathF.Sin(angle) * MathF.Sin(longitude) / 2, row <= rings / 2 ? 1 : -1);
                }
            }
            for (var row = 0; row < count - 1; row++) for (var i = 0; i < sides; i++)
            { var a = row * sides + i; var b = row * sides + (i + 1) % sides; Face(a, b, a + sides); Face(b, b + sides, a + sides); }
        }
        return new ZoneMesh(shape, points.ToArray(), spine.ToArray(), triangles.ToArray(), disc);
    }

    internal void Resize(float sizeX, float sizeY, float sizeZ)
    {
        var x = sizeX; var y = sizeY; var z = sizeZ;
        if (_shape is ZoneShape.Sphere or ZoneShape.Cylinder or ZoneShape.Capsule) z = x;
        if (_shape is ZoneShape.Sphere or ZoneShape.Capsule) y = x;
        var spine = _shape == ZoneShape.Capsule ? Math.Max((sizeY - sizeX) / 2, 0) : 0;
        for (var i = 0; i < _unit.Length; i++)
        {
            var p = _unit[i];
            _vertices[i] = new Vector3(p.x * x, _disc ? 0 : (float)(p.y * y + _spine[i] * spine), p.z * z);
        }
        Mesh.vertices = _vertices;
        Mesh.RecalculateBounds();
    }
    public void Dispose() => UnityEngine.Object.Destroy(Mesh);
}
