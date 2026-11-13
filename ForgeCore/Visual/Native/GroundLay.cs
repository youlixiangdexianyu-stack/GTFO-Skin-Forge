using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// A polar disc laid on the floor for the marker shader (uv = local XZ / radius, +Z north; a strip's uv is along its own right and forward
/// over its half width, its spokes running to points round its rectangle; uv2 = height up a step's drape and
/// that drape's height; colour green 1 = this side of every wall, alpha = brightness). Every ring sample is dropped onto the
/// floor below it, so the mark follows floors, slopes and stairs and always reads as one whole ring:
/// <list type="bullet">
/// <item>a step or low obstacle (lower than <see cref="WallCheck"/> above the floor before it) is found at its edge with one
/// level ray and the mark hangs straight down its face, a drape the shader fades in its middle; floor reached over a step
/// is drawn dimmer. Neighbouring spokes are stitched edge to edge, so a step's face is one clean vertical band;</item>
/// <item>anything taller is a wall: the mark ends at its face and carries on behind it, level, only as the faint dashed
/// line the shader draws where something hides it, so it never climbs a wall or spills into the next room. The short
/// shadow a pillar or crate casts on a ring is filled in again from the open samples on either side when the ring is
/// clear between them;</item>
/// <item>with no floor below (a drop) the mark carries on dimmer at the height before it.</item>
/// </list>
/// Rays: one level ray per spoke and one down ray per sample on level floor, plus one level ray per sample on uneven floor,
/// one per step edge and one per filled-in sample. The layer owns its mesh and writes it through buffers made once, so laying
/// the beam reticle again as the beam moves allocates nothing.
/// </summary>
internal sealed class GroundLay
{
    /// <summary>Obstacles at least this tall above the floor before them are walls (m).</summary>
    private const float WallCheck = 0.5f;
    /// <summary>A rise or fall steeper than this between two samples is a step with a drape, not a slope (m).</summary>
    private const float RiserMin = 0.06f;
    /// <summary>Hidden floor further than this above or below the last open floor is not followed: the line stays level (m).</summary>
    private const float HiddenFollow = 0.2f;
    /// <summary>Surfaces whose normal has less up than this are walls or step faces.</summary>
    private const float Walkable = 0.5f;
    /// <summary>Height above the floor, and the gap kept from a wall or step face (m).</summary>
    private const float Lift = 0.03f, Inset = 0.02f;
    /// <summary>Brightness of floor reached over a step, and over a drop.</summary>
    private const float SideLight = 0.6f, VoidLight = 0.5f;
    /// <summary>A hidden run of a ring at most this long (m) is a pillar's or crate's shadow and is filled in again.</summary>
    private const float ShadowRun = 3f;
    /// <summary>Floor triangles whose normal has less up than this (steeper than about 60 degrees) are not drawn.</summary>
    private const float Steep = 0.5f;
    /// <summary>Where a step or wall ends between two spokes, triangles steeper than about 30 degrees are not drawn.</summary>
    private const float SteepAcross = 0.85f;
    private const byte Flat = 0, Riser = 1, Wall = 2;

    private readonly int _spokes, _rings, _slots;
    private float _radius;
    // Each spoke's direction and ring spacing: a disc's spokes are evenly turned and equally long, a strip's run to points spread evenly round its outline.
    private readonly Vector3[] _direction;
    private readonly float[] _spacing;
    // The mark's own axes on the floor: uv is (along right, along forward) / radius.
    private Vector3 _right = Vector3.right, _forward = Vector3.forward;
    // Ring samples, spoke s and ring k (0 = centre) at s * (rings + 1) + k.
    private readonly float[] _height, _light;
    private readonly bool[] _open;
    // The step between ring k - 1 and k of spoke s at s * rings + k - 1: flat, a riser or a wall, and where its edge is.
    private readonly byte[] _kind;
    private readonly float[] _edge;
    // Per spoke: its vertices' distances from the centre, how many it uses and where each edge's near vertex is.
    private readonly float[] _distance;
    private readonly int[] _used, _edges, _edgeAt;
    private readonly Vector3[] Vertices;
    private readonly Vector2[] Uvs, Drapes;
    private readonly Color32[] Colours;
    private readonly int[] Triangles;
    private readonly Il2CppStructArray<Vector3> _vertices;
    private readonly Il2CppStructArray<Vector2> _uvs, _drapes;
    private readonly Il2CppStructArray<Color32> _colours;
    private readonly Il2CppStructArray<int> _triangles;
    internal readonly Mesh Mesh;
    internal int Spokes => _spokes;
    internal int Rings => _rings;
    /// <summary>A marker is drawing this layer.</summary>
    internal bool InUse;

    /// <summary>Each spoke has a vertex per ring sample and two more at each edge (its near and far side, one above the
    /// other at a step).</summary>
    internal GroundLay(string name, int spokes, int rings)
    {
        _radius = 1f;
        _spokes = spokes;
        _rings = rings;
        _slots = rings * 3;
        _direction = new Vector3[spokes];
        _spacing = new float[spokes];
        Disc(1f);
        _height = new float[spokes * (rings + 1)];
        _light = new float[spokes * (rings + 1)];
        _open = new bool[spokes * (rings + 1)];
        _kind = new byte[spokes * rings];
        _edge = new float[spokes * rings];
        _distance = new float[1 + spokes * _slots + spokes];
        _used = new int[spokes];
        _edges = new int[spokes];
        _edgeAt = new int[spokes * rings];
        // Each spoke's vertices, then one spare per pair of neighbouring spokes.
        var count = 1 + spokes * _slots + spokes;
        Vertices = new Vector3[count];
        Uvs = new Vector2[count];
        Drapes = new Vector2[count];
        Colours = new Color32[count];
        // Per pair of spokes: the centre triangle, at most one per vertex of either, two per edge and two more at a step's end.
        Triangles = new int[spokes * 3 * (3 + 2 * _slots + 2 * rings)];
        _vertices = new Il2CppStructArray<Vector3>(count);
        _uvs = new Il2CppStructArray<Vector2>(count);
        _drapes = new Il2CppStructArray<Vector2>(count);
        _colours = new Il2CppStructArray<Color32>(count);
        _triangles = new Il2CppStructArray<int>(Triangles.Length);
        Mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        Mesh.MarkDynamic();
    }

    /// <summary>Lays a disc of <paramref name="radius"/> metres reaching <paramref name="extent"/> metres around <paramref name="centre"/>; heights are relative to it.</summary>
    internal void Lay(Vector3 centre, float radius, float extent)
    {
        _radius = Math.Max(0.05f, radius);
        Disc(Math.Max(0.05f, extent));
        Build(centre);
    }

    /// <summary>Lays a strip <paramref name="halfWidth"/> metres to each side of <paramref name="centre"/> and <paramref name="halfLength"/> along
    /// <paramref name="forward"/> (level), reaching <paramref name="margin"/> metres past its outline. Its spokes run to points spread evenly round the
    /// outline, its four corners among them, so the laid floor covers the whole rectangle however long it is.</summary>
    internal void LayStrip(Vector3 centre, Vector3 forward, float halfWidth, float halfLength, float margin)
    {
        _radius = Math.Max(0.05f, halfWidth);
        _forward = new Vector3(forward.x, 0f, forward.z).normalized;
        if (_forward.sqrMagnitude < 0.5f) _forward = Vector3.forward;
        _right = new Vector3(_forward.z, 0f, -_forward.x);
        var a = _radius + margin;
        var b = Math.Max(0.05f, halfLength) + margin;
        // Spokes per side in proportion to its length, at least one each; the walk goes clockwise from the front right corner, as a disc's spokes do.
        var ends = Math.Max(1, (int)Math.Round(_spokes * a / (2f * (a + b))));
        var sides = Math.Max(1, _spokes / 2 - ends);
        ends = _spokes / 2 - sides;
        var s = 0;
        Side(ref s, a, b, a, -b, sides);
        Side(ref s, a, -b, -a, -b, ends);
        Side(ref s, -a, -b, -a, b, sides);
        Side(ref s, -a, b, a, b, _spokes - s);
        Build(centre);
    }

    private void Side(ref int s, float x0, float y0, float x1, float y1, int count)
    {
        for (var j = 0; j < count; j++, s++)
        {
            var t = j / (float)count;
            var flat = _right * (x0 + (x1 - x0) * t) + _forward * (y0 + (y1 - y0) * t);
            var length = flat.magnitude;
            _direction[s] = flat / length;
            _spacing[s] = length / _rings;
        }
    }

    private void Disc(float extent)
    {
        _right = Vector3.right;
        _forward = Vector3.forward;
        for (var s = 0; s < _spokes; s++)
        {
            var angle = s * Mathf.PI * 2f / _spokes;
            _direction[s] = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            _spacing[s] = extent / _rings;
        }
    }

    private void Build(Vector3 centre)
    {
        var mask = Scene.WorldMask;
        const QueryTriggerInteraction ignore = QueryTriggerInteraction.Ignore;
        var samples = _rings + 1;
        // The centre sits on the floor under the given point (a beacon ball rests a few centimetres above it).
        var baseY = Physics.Raycast(centre + Vector3.up * 0.9f, Vector3.down, out var under, 2.2f, mask, ignore) ? under.point.y - centre.y : 0f;
        for (var s = 0; s < _spokes; s++)
        {
            var direction = _direction[s];
            var spacing = _spacing[s];
            var o = s * samples;
            // One level ray along the spoke finds a wall on level floor; past a walkable slope it meets, each step casts its own.
            var trustTo = float.MaxValue;
            var wallAt = float.MaxValue;
            if (Physics.Raycast(centre + Vector3.up * (baseY + WallCheck), direction, out var far, spacing * _rings, mask, ignore))
            {
                trustTo = far.distance;
                if (far.normal.y < Walkable) wallAt = far.distance;
            }
            var y = baseY;
            var light = 1f;
            var level = 0f;
            var open = true;
            _height[o] = baseY;
            _light[o] = 1f;
            _open[o] = true;
            for (var k = 1; k <= _rings; k++)
            {
                var r0 = spacing * (k - 1);
                var r1 = spacing * k;
                var step = s * _rings + k - 1;
                _kind[step] = Flat;
                if (open)
                {
                    var wall = -1f;
                    var onBase = Math.Abs(y - baseY) < 0.25f;
                    if (onBase && trustTo > r1) { }
                    else if (onBase && wallAt > r0 && wallAt <= r1) wall = wallAt;
                    else if (Physics.Raycast(centre + direction * r0 + Vector3.up * (y + WallCheck), direction, out var near, spacing, mask, ignore) && near.normal.y < Walkable)
                        wall = r0 + near.distance;
                    if (wall >= 0f)
                    {
                        _kind[step] = Wall;
                        _edge[step] = Math.Max(r0, wall - Inset);
                        open = false;
                    }
                }
                var next = y;
                var landed = Physics.Raycast(centre + direction * r1 + Vector3.up * (y + 0.9f), Vector3.down, out var floor, 2.2f, mask, ignore);
                if (landed)
                {
                    next = floor.point.y - centre.y;
                    // A table or shelf top the level ray passed under: the floor is below it.
                    if (next > y + WallCheck && Physics.Raycast(floor.point + Vector3.down * 0.05f, Vector3.down, out var below, next - y + 1.3f, mask, ignore))
                        next = below.point.y - centre.y;
                }
                if (open)
                {
                    var rise = next - y;
                    if (landed && Math.Abs(rise) > RiserMin)
                    {
                        // A step: one level ray from its lower side finds the face the mark hangs down.
                        var edge = -1f;
                        if (rise > 0f)
                        {
                            if (Physics.Raycast(centre + direction * r0 + Vector3.up * (y + 0.05f), direction, out var face, spacing, mask, ignore) && face.normal.y < Walkable)
                                edge = r0 + face.distance - Inset;
                        }
                        else if (Physics.Raycast(centre + direction * r1 + Vector3.up * (next + 0.05f), -direction, out var face, spacing, mask, ignore) && face.normal.y < Walkable)
                            edge = r1 - face.distance + Inset;
                        if (edge >= 0f)
                        {
                            _kind[step] = Riser;
                            _edge[step] = Mathf.Clamp(edge, r0, r1);
                            level += rise;
                        }
                    }
                    light = !landed ? VoidLight : Math.Abs(level) > RiserMin ? SideLight : 1f;
                }
                else if (!landed || Math.Abs(next - y) > HiddenFollow) next = y;
                y = next;
                _height[o + k] = y;
                _light[o + k] = light;
                _open[o + k] = open;
            }
        }
        FillShadows(centre, samples, mask);
        Write(baseY, samples);
        Stitch();
        for (var i = 0; i < Vertices.Length; i++)
        {
            _vertices[i] = Vertices[i];
            _uvs[i] = Uvs[i];
            _drapes[i] = Drapes[i];
            _colours[i] = Colours[i];
        }
        for (var i = 0; i < Triangles.Length; i++) _triangles[i] = Triangles[i];
        Mesh.vertices = _vertices;
        Mesh.uv = _uvs;
        Mesh.uv2 = _drapes;
        Mesh.colors32 = _colours;
        Mesh.triangles = _triangles;
        Mesh.RecalculateBounds();
    }

    /// <summary>Opens the samples of each short hidden run of a ring, walking in from the open sample on either side while
    /// the ring between them is clear.</summary>
    private void FillShadows(Vector3 centre, int samples, int mask)
    {
        for (var k = 1; k <= _rings; k++)
        {
            var first = -1;
            for (var s = 0; s < _spokes && first < 0; s++)
                if (_open[s * samples + k]) first = s;
            if (first < 0) continue;
            var j = 1;
            while (j < _spokes)
            {
                if (_open[(first + j) % _spokes * samples + k]) { j++; continue; }
                var count = 0;
                while (j + count < _spokes && !_open[(first + j + count) % _spokes * samples + k]) count++;
                if (Run(k, (first + j - 1) % _spokes, count) <= ShadowRun)
                {
                    Fill(centre, samples, mask, k, (first + j - 1) % _spokes, count, 1);
                    Fill(centre, samples, mask, k, (first + j + count) % _spokes, count, -1);
                }
                j += count;
            }
        }
    }

    /// <summary>Ring <paramref name="k"/>'s sample on spoke <paramref name="s"/>, level with the centre.</summary>
    private Vector3 Spot(int s, int k) => _direction[s] * (_spacing[s] * k);

    /// <summary>The length of ring <paramref name="k"/> over <paramref name="steps"/> spokes from spoke <paramref name="from"/> (m).</summary>
    private float Run(int k, int from, int steps)
    {
        var length = 0f;
        for (var i = 0; i < steps; i++) length += (Spot((from + i + 1) % _spokes, k) - Spot((from + i) % _spokes, k)).magnitude;
        return length;
    }

    private void Fill(Vector3 centre, int samples, int mask, int k, int from, int count, int way)
    {
        for (var i = 1; i <= count; i++)
        {
            var s = ((from + way) % _spokes + _spokes) % _spokes;
            var at = s * samples + k;
            if (_open[at]) return;
            var h = _height[from * samples + k];
            if (Math.Abs(_height[at] - h) > WallCheck) return;
            var lift = Vector3.up * (h + WallCheck);
            if (Physics.Linecast(centre + Spot(from, k) + lift, centre + Spot(s, k) + lift, mask, QueryTriggerInteraction.Ignore)) return;
            _open[at] = true;
            _light[at] = _light[from * samples + k];
            from = s;
        }
    }

    /// <summary>The vertices of each spoke outward: at an edge its near and far side, then the ring sample.</summary>
    private void Write(float baseY, int samples)
    {
        Vertices[0] = Vector3.up * (baseY + Lift);
        Uvs[0] = Vector2.zero;
        Drapes[0] = Vector2.zero;
        Colours[0] = new Color32(255, 255, 255, 255);
        for (var s = 0; s < _spokes; s++)
        {
            var j = 0;
            _edges[s] = 0;
            for (var k = 1; k <= _rings; k++)
            {
                var a = s * samples + k - 1;
                var b = a + 1;
                var step = s * _rings + k - 1;
                if (_kind[step] != Flat)
                {
                    _edgeAt[s * _rings + _edges[s]++] = j;
                    if (_kind[step] == Riser)
                    {
                        var drop = Math.Abs(_height[b] - _height[a]);
                        Put(s, j++, _edge[step], _height[a], _light[a], _open[a], 0f, drop);
                        Put(s, j++, _edge[step], _height[b], _light[b], _open[b], drop, drop);
                    }
                    else
                    {
                        Put(s, j++, _edge[step], _height[a], _light[a], _open[a], 0f, 0f);
                        Put(s, j++, _edge[step], _height[a], _light[a], false, 0f, 0f);
                    }
                }
                Put(s, j++, _spacing[s] * k, _height[b], _light[b], _open[b], 0f, 0f);
            }
            _used[s] = j;
            var spareAt = 1 + _spokes * _slots + s;
            Vertices[spareAt] = Vertices[0];
            Uvs[spareAt] = Vector2.zero;
            Drapes[spareAt] = Vector2.zero;
            Colours[spareAt] = new Color32(255, 0, 255, 0);
            _distance[spareAt] = 0f;
            // Spare slots sit unused at the centre.
            for (; j < _slots; j++)
            {
                var i = 1 + s * _slots + j;
                Vertices[i] = Vertices[0];
                Uvs[i] = Vector2.zero;
                Drapes[i] = Vector2.zero;
                Colours[i] = new Color32(255, 0, 255, 0);
                _distance[i] = 0f;
            }
        }
    }

    private void Put(int spoke, int slot, float r, float y, float light, bool open, float up, float drop)
    {
        var i = 1 + spoke * _slots + slot;
        var flat = _direction[spoke] * r;
        Vertices[i] = new Vector3(flat.x, y + Lift, flat.z);
        Uvs[i] = new Vector2(Vector3.Dot(flat, _right) / _radius, Vector3.Dot(flat, _forward) / _radius);
        Drapes[i] = new Vector2(up, drop);
        Colours[i] = new Color32(255, open ? (byte)255 : (byte)0, 255, (byte)Math.Round(light * 255f));
        _distance[i] = r / _spacing[spoke]; // in rings: neighbouring spokes of a strip differ in length, they are stitched ring to ring
    }

    /// <summary>Triangles between neighbouring spokes. The floor between each pair of edges is stitched to the matching
    /// floor of the other spoke and each edge to the matching edge, so no triangle spans a step. Where a step or wall leaves
    /// the disc between two spokes (one crosses one edge more), that last edge is joined to the other spoke's end, raised to
    /// the far side's height on a spare vertex; any other mismatch is stitched by distance without its steep
    /// triangles.</summary>
    private void Stitch()
    {
        var n = 0;
        for (var s = 0; s < _spokes; s++)
        {
            var t = (s + 1) % _spokes;
            Triangles[n++] = 0; Triangles[n++] = 1 + s * _slots; Triangles[n++] = 1 + t * _slots;
            var more = _edges[s] >= _edges[t] ? s : t;
            var fewer = more == s ? t : s;
            var extra = _edges[more] - _edges[fewer];
            var x = 1 + more * _slots;
            var y = 1 + fewer * _slots;
            var lastX = x + _used[more] - 1;
            var lastY = y + _used[fewer] - 1;
            if (extra > 1)
            {
                n = Zip(n, x, lastX, y, lastY, SteepAcross);
                continue;
            }
            var fromX = x;
            var fromY = y;
            for (var e = 0; e < _edges[fewer]; e++)
            {
                var nearX = x + _edgeAt[more * _rings + e];
                var nearY = y + _edgeAt[fewer * _rings + e];
                if (nearX > fromX || nearY > fromY) n = Zip(n, fromX, nearX, fromY, nearY, Steep);
                Triangles[n++] = nearX; Triangles[n++] = nearX + 1; Triangles[n++] = nearY;
                Triangles[n++] = nearY; Triangles[n++] = nearX + 1; Triangles[n++] = nearY + 1;
                fromX = nearX + 1;
                fromY = nearY + 1;
            }
            if (extra == 1)
            {
                var nearX = x + _edgeAt[more * _rings + _edges[fewer]];
                var spare = 1 + _spokes * _slots + s;
                Vertices[spare] = new Vector3(Vertices[lastY].x, Vertices[nearX + 1].y, Vertices[lastY].z);
                Uvs[spare] = Uvs[lastY];
                Drapes[spare] = Drapes[nearX + 1];
                Colours[spare] = Colours[nearX + 1];
                _distance[spare] = _distance[lastY];
                if (nearX > fromX || lastY > fromY) n = Zip(n, fromX, nearX, fromY, lastY, Steep);
                Triangles[n++] = nearX; Triangles[n++] = nearX + 1; Triangles[n++] = lastY;
                Triangles[n++] = lastY; Triangles[n++] = nearX + 1; Triangles[n++] = spare;
                n = Zip(n, nearX + 1, lastX, spare, spare, Steep);
            }
            else n = Zip(n, fromX, lastX, fromY, lastY, Steep);
        }
        while (n < Triangles.Length) Triangles[n++] = 0;
    }

    /// <summary>Stitches vertices <paramref name="a"/>..<paramref name="lastA"/> of one spoke to <paramref name="b"/>..
    /// <paramref name="lastB"/> of the next, always advancing on the side whose next vertex is nearer the centre. A triangle
    /// steeper than <paramref name="steep"/> would stand up across the end of a step or wall between the two spokes; it is left
    /// out (a gap a spoke wide at most, where the step's own side face is).</summary>
    private int Zip(int n, int a, int lastA, int b, int lastB, float steep)
    {
        while (a < lastA || b < lastB)
        {
            var c = b >= lastB || (a < lastA && _distance[a + 1] <= _distance[b + 1]) ? ++a : ++b;
            var first = c == a ? a - 1 : a;
            var second = c == b ? b - 1 : b;
            var normal = Vector3.Cross(Vertices[second] - Vertices[first], Vertices[c] - Vertices[first]);
            if (Math.Abs(normal.y) < steep * normal.magnitude) continue;
            Triangles[n++] = first; Triangles[n++] = second; Triangles[n++] = c;
        }
        return n;
    }
}
