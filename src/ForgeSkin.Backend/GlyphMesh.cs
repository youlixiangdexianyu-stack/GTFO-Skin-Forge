using LibTessDotNet;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// Screen digits as real geometry: the gear shader has no transparency, so a textured quad would draw the glyph's
// transparent texels as a solid plate. Each cell of the glyph strip (Screen.Glyphs, white on transparent) is traced at
// alpha 0.5 with marching squares, simplified, and triangulated with its holes (the counters of 0, 6, 8, 9) by LibTess.
// A glyph mesh is in cell units: height 1, width Aspect, centred on the origin, X right, Y up, facing -Z (the screen's
// visible side). A blank cell has no mesh. A static screen's text is traced the same way as one image.
public static class GlyphMesh
{
    public static readonly int Count = Screen.Glyphs.Length;

    public static (ForgeSkinMesh?[] Glyphs, float Aspect) Build(SKBitmap strip)
    {
        int cw = strip.Width / Count, ch = strip.Height;
        var glyphs = new ForgeSkinMesh?[Count];
        for (int g = 0; g < Count; g++) glyphs[g] = Cell(strip, g * cw, cw, ch);
        return (glyphs, (float)cw / ch);
    }

    // A static screen's whole text, rendered as one image: one mesh, height 1, width Aspect.
    public static (ForgeSkinMesh? Mesh, float Aspect) Text(SKBitmap image) => (Cell(image, 0, image.Width, image.Height), (float)image.Width / image.Height);

    static ForgeSkinMesh? Cell(SKBitmap strip, int x0, int w, int h)
    {
        // Alpha with a zero border, so every outline closes inside the grid.
        var a = new float[w + 2, h + 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) a[x + 1, y + 1] = strip.GetPixel(x0 + x, y).Alpha / 255f;

        var tess = new Tess();
        foreach (var loop in Contours(a, w + 2, h + 2))
        {
            var ring = Simplify(loop, 0.2f);
            if (ring.Count < 3) continue;
            // Grid point (i, j) is pixel centre (i - 0.5, j - 0.5) of the cell; to cell units, Y up.
            tess.AddContour(ring.Select(p => new ContourVertex(new Vec3((p.X - 0.5f - w * 0.5f) / h, (h * 0.5f - (p.Y - 0.5f)) / h, 0))).ToArray());
        }
        tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);
        if (tess.ElementCount == 0) return null;

        int vc = tess.VertexCount;
        var mesh = new ForgeSkinMesh { Positions = new float[vc * 3], Normals = new float[vc * 3], Uv0 = new float[vc * 2], Indices = new uint[tess.ElementCount * 3] };
        float aspect = (float)w / h;
        for (int i = 0; i < vc; i++)
        {
            var p = tess.Vertices[i].Position;
            (mesh.Positions[i * 3], mesh.Positions[i * 3 + 1]) = (p.X, p.Y);
            mesh.Normals[i * 3 + 2] = -1;
            (mesh.Uv0[i * 2], mesh.Uv0[i * 2 + 1]) = (p.X / aspect + 0.5f, p.Y + 0.5f);
        }
        for (int t = 0; t < tess.ElementCount; t++)
        {
            int i0 = tess.Elements[t * 3], i1 = tess.Elements[t * 3 + 1], i2 = tess.Elements[t * 3 + 2];
            Vec3 p0 = tess.Vertices[i0].Position, p1 = tess.Vertices[i1].Position, p2 = tess.Vertices[i2].Position;
            // Unity's front face is clockwise as seen by the viewer; the viewer is on -Z, where X is right and Y up.
            bool counterClockwise = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X) > 0;
            if (counterClockwise) (i1, i2) = (i2, i1);
            (mesh.Indices[t * 3], mesh.Indices[t * 3 + 1], mesh.Indices[t * 3 + 2]) = ((uint)i0, (uint)i1, (uint)i2);
        }
        return mesh;
    }

    // Marching squares at 0.5: crossing points on grid edges (keyed by edge), linked into closed loops.
    static List<List<SKPoint>> Contours(float[,] a, int w, int h)
    {
        long H(int x, int y) => ((long)y * w + x) * 2; // edge (x,y)-(x+1,y)
        long V(int x, int y) => ((long)y * w + x) * 2 + 1; // edge (x,y)-(x,y+1)
        var next = new Dictionary<long, List<long>>();
        void Link(long p, long q)
        {
            (next.TryGetValue(p, out var lp) ? lp : next[p] = new()).Add(q);
            (next.TryGetValue(q, out var lq) ? lq : next[q] = new()).Add(p);
        }
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                bool tl = a[x, y] >= 0.5f, tr = a[x + 1, y] >= 0.5f, br = a[x + 1, y + 1] >= 0.5f, bl = a[x, y + 1] >= 0.5f;
                long top = H(x, y), bottom = H(x, y + 1), left = V(x, y), right = V(x + 1, y);
                int c = (tl ? 8 : 0) | (tr ? 4 : 0) | (br ? 2 : 0) | (bl ? 1 : 0);
                bool centre = (a[x, y] + a[x + 1, y] + a[x + 1, y + 1] + a[x, y + 1]) * 0.25f >= 0.5f;
                switch (c)
                {
                    case 1: case 14: Link(left, bottom); break;
                    case 2: case 13: Link(bottom, right); break;
                    case 3: case 12: Link(left, right); break;
                    case 4: case 11: Link(top, right); break;
                    case 6: case 9: Link(top, bottom); break;
                    case 7: case 8: Link(left, top); break;
                    case 5: // tr and bl inside
                        if (centre) { Link(left, top); Link(bottom, right); } else { Link(top, right); Link(left, bottom); }
                        break;
                    case 10: // tl and br inside
                        if (centre) { Link(top, right); Link(left, bottom); } else { Link(left, top); Link(bottom, right); }
                        break;
                }
            }

        SKPoint Point(long key)
        {
            long cell = key / 2;
            int x = (int)(cell % w), y = (int)(cell / w);
            float v0 = a[x, y], v1 = key % 2 == 0 ? a[x + 1, y] : a[x, y + 1];
            float t = Math.Clamp((0.5f - v0) / (v1 - v0), 0, 1);
            return key % 2 == 0 ? new SKPoint(x + t, y) : new SKPoint(x, y + t);
        }

        var loops = new List<List<SKPoint>>();
        var seen = new HashSet<long>();
        foreach (var start in next.Keys)
        {
            if (!seen.Add(start)) continue;
            var loop = new List<SKPoint> { Point(start) };
            long prev = start, at = next[start][0];
            while (at != start && seen.Add(at))
            {
                loop.Add(Point(at));
                var n = next[at];
                long step = n[0] != prev ? n[0] : n[1];
                (prev, at) = (at, step);
            }
            loops.Add(loop);
        }
        return loops;
    }

    // Douglas-Peucker on a closed ring, split at the point farthest from the first.
    static List<SKPoint> Simplify(List<SKPoint> ring, float tolerance)
    {
        if (ring.Count < 4) return ring;
        int far = 0;
        for (int i = 1; i < ring.Count; i++)
            if (SKPoint.DistanceSquared(ring[i], ring[0]) > SKPoint.DistanceSquared(ring[far], ring[0])) far = i;
        var keep = new bool[ring.Count];
        keep[0] = keep[far] = true;
        void Run(int i0, int i1) // i1 may wrap past the end
        {
            int n = ring.Count;
            var p = ring[i0 % n];
            var q = ring[i1 % n];
            float best = 0;
            int at = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                float d = Distance(ring[i % n], p, q);
                if (d > best) (best, at) = (d, i);
            }
            if (at < 0 || best <= tolerance) return;
            keep[at % n] = true;
            Run(i0, at);
            Run(at, i1);
        }
        Run(0, far);
        Run(far, ring.Count);
        return ring.Where((_, i) => keep[i]).ToList();
    }

    static float Distance(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6f) return SKPoint.Distance(p, a);
        return MathF.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
    }
}
