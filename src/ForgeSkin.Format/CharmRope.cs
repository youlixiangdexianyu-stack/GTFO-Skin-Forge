using ForgeVisual;

namespace ForgeSkin.Format;

// A charm (Attachment.Charm), with no game reference: its rope moved by ForgeCore's chain physics (ChainState, the one algorithm for
// charms and spring bones) and drawn here as the charm's look. The plugin drives it; the editor's backend runs the same file for the
// 3D view (charm.simulate), so the view plays what the game does.
//
// Nodes: 0 is the hanging point (the attachment's origin, pinned), 1..S the rope (S = Segments; node S is where the pendant hangs),
// S+1 the pendant's centre on a rigid arm below node S, PendantMass times a rope node and half as stiff, so the pendant swings on
// its own. At rest it hangs straight along the attachment's -Y. The gear's body is boxes the rope is kept out of.
//
// Matrices are 16 numbers, column-major (element [col * 4 + row]); a pose is position xyz and quaternion xyzw.
public sealed class CharmRope
{
    public const int MaxSegments = 12, MaxPerGear = 4, MaxBoxes = ChainState.MaxBoxes, MaxLinks = 48;
    public const double PendantMass = 4, PendantBend = 0.5; // against one rope node
    // Look: a cord is a tube through the nodes (CordSub points per segment, CordSides around), a chain alternating oval links
    // (Thickness wide), beads balls Thickness across; links and beads are spread evenly along the rope.
    public const int CordSides = 6, CordSub = 3, LinkRing = 8, LinkTube = 4, BeadLat = 4, BeadLon = 6;
    public const double LinkLength = 1.7, LinkWire = 0.17, LinkPitch = 1.1, BeadPitch = 1.2; // times Thickness

    // Metallic and smoothness of the rope's surface per style (the editor's preview takes them from the backend).
    public static double Metallic(CharmStyle s) => s == CharmStyle.Cord ? 0 : s == CharmStyle.Chain ? 0.9 : 0.8;
    public static double Smoothness(CharmStyle s) => s == CharmStyle.Cord ? 0.2 : s == CharmStyle.Chain ? 0.65 : 0.7;

    /// <summary>The chain physics a charm's settings give (ChainSpec.Charm with the author's stiffness, damping, weight and thickness).</summary>
    public static ChainSpec Spec(Charm c)
    {
        var spec = ChainSpec.Charm();
        spec.Stiffness = c.Stiffness;
        spec.Damping = c.Damping;
        spec.Weight = c.Weight;
        spec.Radius = c.Thickness * 0.5;
        spec.Collide = c.Collide;
        return spec;
    }

    readonly ChainState _chain;
    readonly int _s, _style, _elements, _curvePoints, _template;
    readonly double _radius, _thickness;
    readonly double[] _p, _t = new double[16];
    readonly double[] _curve, _arc, _frame; // smoothed rope points, arc length to each, and u, v per point
    readonly double[] _tplPos, _tplNrm; // a link's or bead's vertices and normals in (along, u, v)

    public CharmRope(Charm c, double pendantArm)
    {
        // In double from here on, as the editor computes.
        double length = c.Length, thickness = c.Thickness;
        _s = Math.Clamp(Math.Min(c.Segments, (int)Math.Floor(length / ChainState.MinPiece)), 1, MaxSegments);
        var shape = new ChainShape();
        int node = shape.AddRoot(0, 0, 0);
        for (int i = 0; i < _s; i++) node = shape.Add(node, length / _s, 0, -1, 0);
        shape.Add(node, pendantArm, 0, -1, 0, PendantMass, PendantBend, pendantArm * 0.8, rigid: true);
        _chain = new ChainState(shape, Spec(c));
        _p = _chain.Nodes;
        _thickness = thickness;
        _radius = thickness * 0.5;
        _style = (int)c.Style;
        _curvePoints = _s * CordSub + 1;
        _curve = new double[_curvePoints * 3];
        _arc = new double[_curvePoints];
        _frame = new double[_curvePoints * 6];
        double pitch = thickness * (c.Style == CharmStyle.Chain ? LinkPitch : BeadPitch);
        _elements = c.Style == CharmStyle.Cord ? 0 : Math.Clamp((int)Math.Round(length / pitch), 1, MaxLinks);
        _template = c.Style == CharmStyle.Chain ? LinkRing * LinkTube : c.Style == CharmStyle.Beads ? (BeadLat + 1) * BeadLon : 0;
        _tplPos = new double[_template * 3];
        _tplNrm = new double[_template * 3];
        if (c.Style == CharmStyle.Chain) LinkTemplate();
        else if (c.Style == CharmStyle.Beads) BeadTemplate();
    }

    // World positions of the nodes, three per node.
    public double[] Nodes => _p;
    public int Boxes => _chain.Boxes;
    public int VertexCount => _style == (int)CharmStyle.Cord ? _curvePoints * CordSides : _elements * _template;

    // A box of the gear's body: min and max in the gear's frame `gear`, taken into the hanging point's frame (`anchor`, both at the
    // same moment); see ChainState.AddBox.
    public void AddBox(double[] anchor, double[] gear, double[] min, double[] max) => _chain.AddBox(anchor, gear, min, max);

    // At rest: straight along the attachment's -Y from the hanging point.
    public void Rest(double[] anchor) => _chain.Rest(anchor);

    // Advances to `time` (seconds) with the hanging point at `anchor` and the player's body (camera) at `body` (xyz). Allocates nothing.
    public void Update(double time, double[] anchor, double[] body) => _chain.Update(time, anchor, body);

    // ---- output ----

    // The pendant's world pose (position xyz, quaternion xyzw): on node S, turned from the attachment's rest pose by the
    // turn that takes its -Y onto the arm.
    public void Pendant(double[] pose)
    {
        int s = _s * 3, e = s + 3;
        double ax = _p[e] - _p[s], ay = _p[e + 1] - _p[s + 1], az = _p[e + 2] - _p[s + 2];
        double len = Math.Sqrt(ax * ax + ay * ay + az * az);
        double[] cur = _chain.Anchor;
        ChainState.Rotate(cur[3], cur[4], cur[5], cur[6], 0, -1, 0, _t, 0);
        double fx = 0, fy = 0, fz = 0, fw = 1;
        if (len > 1e-9)
        {
            ax /= len; ay /= len; az /= len;
            double d = _t[0] * ax + _t[1] * ay + _t[2] * az;
            if (d < -0.999999)
            {
                // Opposite: half a turn about the attachment's X.
                ChainState.Rotate(cur[3], cur[4], cur[5], cur[6], 1, 0, 0, _t, 3);
                fx = _t[3]; fy = _t[4]; fz = _t[5]; fw = 0;
            }
            else
            {
                fx = _t[1] * az - _t[2] * ay; fy = _t[2] * ax - _t[0] * az; fz = _t[0] * ay - _t[1] * ax; fw = 1 + d;
                double l = Math.Sqrt(fx * fx + fy * fy + fz * fz + fw * fw);
                fx /= l; fy /= l; fz /= l; fw /= l;
            }
        }
        double qx = cur[3], qy = cur[4], qz = cur[5], qw = cur[6];
        pose[0] = _p[s]; pose[1] = _p[s + 1]; pose[2] = _p[s + 2];
        pose[3] = fw * qx + fx * qw + fy * qz - fz * qy;
        pose[4] = fw * qy - fx * qz + fy * qw + fz * qx;
        pose[5] = fw * qz + fx * qy - fy * qx + fz * qw;
        pose[6] = fw * qw - fx * qx - fy * qy - fz * qz;
    }

    // The rope's triangles (setup only): front faces wound for the engine's own handedness on either side.
    public int[] Triangles()
    {
        if (_style == (int)CharmStyle.Cord) return Grid(_curvePoints, CordSides, false, 1, 0);
        int rings = _style == (int)CharmStyle.Chain ? LinkRing : BeadLat + 1, sides = _style == (int)CharmStyle.Chain ? LinkTube : BeadLon;
        return Grid(rings, sides, _style == (int)CharmStyle.Chain, _elements, _template);
    }

    // Rings of `sides` vertices, ring after ring along the forward direction of a frame whose first two radial directions
    // cross to it; `copies` of it, `stride` vertices apart.
    static int[] Grid(int rings, int sides, bool closed, int copies, int stride)
    {
        int quads = (closed ? rings : rings - 1) * sides;
        var t = new int[copies * quads * 6];
        int n = 0;
        for (int c = 0; c < copies; c++)
            for (int i = 0; i < (closed ? rings : rings - 1); i++)
                for (int k = 0; k < sides; k++)
                {
                    int a = c * stride + i * sides + k, b = c * stride + i * sides + (k + 1) % sides;
                    int d = c * stride + (i + 1) % rings * sides + k, e = c * stride + (i + 1) % rings * sides + (k + 1) % sides;
                    t[n++] = a; t[n++] = b; t[n++] = d;
                    t[n++] = b; t[n++] = e; t[n++] = d;
                }
        return t;
    }

    // The rope's vertices and normals (VertexCount each, xyz) in the local space of `anchor` (scale included), from the
    // nodes as they are now. Allocates nothing.
    public void Mesh(double[] anchor, float[] vertices, float[] normals)
    {
        Curve();
        // Inverse of the anchor's 3x3 (vertices) and its transpose (normals).
        double a = anchor[0], b = anchor[4], c = anchor[8], d = anchor[1], e = anchor[5], f = anchor[9], g = anchor[2], h = anchor[6], k = anchor[10];
        double A = e * k - f * h, B = -(d * k - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-18) return;
        double id = 1 / det;
        _t[0] = A * id; _t[1] = -(b * k - c * h) * id; _t[2] = (b * f - c * e) * id;
        _t[3] = B * id; _t[4] = (a * k - c * g) * id; _t[5] = -(a * f - c * d) * id;
        _t[6] = C * id; _t[7] = -(a * h - b * g) * id; _t[8] = (a * e - b * d) * id;
        double tx = anchor[12], ty = anchor[13], tz = anchor[14];
        int n = 0;
        if (_style == (int)CharmStyle.Cord)
        {
            for (int j = 0; j < _curvePoints; j++)
                for (int s = 0; s < CordSides; s++)
                {
                    double th = 2 * Math.PI * s / CordSides, cs = Math.Cos(th), sn = Math.Sin(th);
                    int f6 = j * 6;
                    double nx = _frame[f6] * cs + _frame[f6 + 3] * sn, ny = _frame[f6 + 1] * cs + _frame[f6 + 4] * sn, nz = _frame[f6 + 2] * cs + _frame[f6 + 5] * sn;
                    Emit(vertices, normals, n++, _curve[j * 3] + nx * _radius, _curve[j * 3 + 1] + ny * _radius, _curve[j * 3 + 2] + nz * _radius, nx, ny, nz, tx, ty, tz, anchor);
                }
            return;
        }
        double total = _arc[_curvePoints - 1];
        int seg = 0;
        for (int el = 0; el < _elements; el++)
        {
            double at = (el + 0.5) / _elements * total;
            while (seg < _curvePoints - 2 && _arc[seg + 1] < at) seg++;
            double span = _arc[seg + 1] - _arc[seg], f0 = span > 1e-12 ? (at - _arc[seg]) / span : 0;
            int p0 = seg * 3, p1 = p0 + 3;
            double cx = _curve[p0] + (_curve[p1] - _curve[p0]) * f0, cy = _curve[p0 + 1] + (_curve[p1 + 1] - _curve[p0 + 1]) * f0, cz = _curve[p0 + 2] + (_curve[p1 + 2] - _curve[p0 + 2]) * f0;
            double lx = _curve[p1] - _curve[p0], ly = _curve[p1 + 1] - _curve[p0 + 1], lz = _curve[p1 + 2] - _curve[p0 + 2];
            double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            if (ll < 1e-12) { lx = 0; ly = -1; lz = 0; } else { lx /= ll; ly /= ll; lz /= ll; }
            // u: the point's frame u made square to the along direction; v = along x u. Odd links turn a quarter.
            int f6 = seg * 6;
            double ux = _frame[f6], uy = _frame[f6 + 1], uz = _frame[f6 + 2];
            double dot = ux * lx + uy * ly + uz * lz;
            ux -= lx * dot; uy -= ly * dot; uz -= lz * dot;
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            ux /= ul; uy /= ul; uz /= ul;
            double vx = ly * uz - lz * uy, vy = lz * ux - lx * uz, vz = lx * uy - ly * ux;
            if (_style == (int)CharmStyle.Chain && (el & 1) == 1)
            {
                double ox = ux, oy = uy, oz = uz;
                ux = vx; uy = vy; uz = vz;
                vx = -ox; vy = -oy; vz = -oz;
            }
            for (int i = 0; i < _template; i++)
            {
                int o = i * 3;
                double pa = _tplPos[o], pu = _tplPos[o + 1], pv = _tplPos[o + 2];
                double na = _tplNrm[o], nu = _tplNrm[o + 1], nv = _tplNrm[o + 2];
                Emit(vertices, normals, n++,
                    cx + lx * pa + ux * pu + vx * pv, cy + ly * pa + uy * pu + vy * pv, cz + lz * pa + uz * pu + vz * pv,
                    lx * na + ux * nu + vx * nv, ly * na + uy * nu + vy * nv, lz * na + uz * nu + vz * nv, tx, ty, tz, anchor);
            }
        }
    }

    void Emit(float[] vertices, float[] normals, int i, double x, double y, double z, double nx, double ny, double nz, double tx, double ty, double tz, double[] m)
    {
        x -= tx; y -= ty; z -= tz;
        int o = i * 3;
        vertices[o] = (float)(_t[0] * x + _t[1] * y + _t[2] * z);
        vertices[o + 1] = (float)(_t[3] * x + _t[4] * y + _t[5] * z);
        vertices[o + 2] = (float)(_t[6] * x + _t[7] * y + _t[8] * z);
        // A normal goes by the transpose of the matrix (the inverse's inverse-transpose).
        double ax = m[0] * nx + m[1] * ny + m[2] * nz, ay = m[4] * nx + m[5] * ny + m[6] * nz, az = m[8] * nx + m[9] * ny + m[10] * nz;
        double l = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (l < 1e-12) l = 1;
        normals[o] = (float)(ax / l);
        normals[o + 1] = (float)(ay / l);
        normals[o + 2] = (float)(az / l);
    }

    // Smoothed rope (Catmull-Rom through nodes 0..S, ends repeated), its arc lengths and a frame (u, v) per point carried
    // along without twisting; u x v is the forward direction.
    void Curve()
    {
        int m = 0;
        for (int i = 0; i < _s; i++)
        {
            int i0 = Math.Max(i - 1, 0) * 3, i1 = i * 3, i2 = (i + 1) * 3, i3 = Math.Min(i + 2, _s) * 3;
            for (int j = 0; j < CordSub; j++)
            {
                double t = (double)j / CordSub, t2 = t * t, t3 = t2 * t;
                for (int c = 0; c < 3; c++)
                    _curve[m * 3 + c] = 0.5 * (2 * _p[i1 + c] + (-_p[i0 + c] + _p[i2 + c]) * t
                        + (2 * _p[i0 + c] - 5 * _p[i1 + c] + 4 * _p[i2 + c] - _p[i3 + c]) * t2
                        + (-_p[i0 + c] + 3 * _p[i1 + c] - 3 * _p[i2 + c] + _p[i3 + c]) * t3);
                m++;
            }
        }
        _curve[m * 3] = _p[_s * 3]; _curve[m * 3 + 1] = _p[_s * 3 + 1]; _curve[m * 3 + 2] = _p[_s * 3 + 2];
        _arc[0] = 0;
        double ux = 0, uy = 0, uz = 0;
        for (int j = 0; j < _curvePoints; j++)
        {
            int a = Math.Max(j - 1, 0) * 3, b = Math.Min(j + 1, _curvePoints - 1) * 3;
            double fx = _curve[b] - _curve[a], fy = _curve[b + 1] - _curve[a + 1], fz = _curve[b + 2] - _curve[a + 2];
            double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (fl < 1e-12) { fx = 0; fy = -1; fz = 0; } else { fx /= fl; fy /= fl; fz /= fl; }
            if (j > 0)
            {
                double sx = _curve[j * 3] - _curve[j * 3 - 3], sy = _curve[j * 3 + 1] - _curve[j * 3 - 2], sz = _curve[j * 3 + 2] - _curve[j * 3 - 1];
                _arc[j] = _arc[j - 1] + Math.Sqrt(sx * sx + sy * sy + sz * sz);
            }
            if (j == 0)
            {
                // Any direction square to the first: X, or Y when the rope runs along X.
                double rx = Math.Abs(fx) < 0.9 ? 1 : 0, ry = Math.Abs(fx) < 0.9 ? 0 : 1;
                ux = ry * fz; uy = -rx * fz; uz = rx * fy - ry * fx;
            }
            else
            {
                double dot = ux * fx + uy * fy + uz * fz;
                ux -= fx * dot; uy -= fy * dot; uz -= fz * dot;
            }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12)
            {
                double rx = Math.Abs(fx) < 0.9 ? 1 : 0, ry = Math.Abs(fx) < 0.9 ? 0 : 1;
                ux = ry * fz; uy = -rx * fz; uz = rx * fy - ry * fx;
                ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            }
            ux /= ul; uy /= ul; uz /= ul;
            int o = j * 6;
            _frame[o] = ux; _frame[o + 1] = uy; _frame[o + 2] = uz;
            _frame[o + 3] = fy * uz - fz * uy; _frame[o + 4] = fz * ux - fx * uz; _frame[o + 5] = fx * uy - fy * ux;
        }
    }

    // A chain link: an oval ring in the (along, u) plane, LinkLength x 1 Thickness outside, a wire LinkWire thick; ring
    // after ring around the oval, its cross-section turning so that its radial directions cross to the ring's forward.
    void LinkTemplate()
    {
        double w = _thickness, r = w * LinkWire, A = w * LinkLength / 2 - r, B = w / 2 - r;
        for (int i = 0; i < LinkRing; i++)
        {
            double phi = 2 * Math.PI * i / LinkRing, ca = Math.Cos(phi), sa = Math.Sin(phi);
            double mx = ca / A, mu = sa / B, ml = Math.Sqrt(mx * mx + mu * mu);
            mx /= ml; mu /= ml;
            for (int k = 0; k < LinkTube; k++)
            {
                double psi = 2 * Math.PI * k / LinkTube, cp = Math.Cos(psi), sp = Math.Sin(psi);
                int o = (i * LinkTube + k) * 3;
                double na = mx * cp, nu = mu * cp, nv = -sp; // m x (-v) is the ring's forward
                _tplNrm[o] = na; _tplNrm[o + 1] = nu; _tplNrm[o + 2] = nv;
                _tplPos[o] = A * ca + na * r; _tplPos[o + 1] = B * sa + nu * r; _tplPos[o + 2] = nv * r;
            }
        }
    }

    // A bead: a ball Thickness across, from the end ahead (+along) to the end behind.
    void BeadTemplate()
    {
        double r = _thickness / 2;
        for (int i = 0; i <= BeadLat; i++)
        {
            double a = Math.PI * i / BeadLat, ca = Math.Cos(a), sa = Math.Sin(a);
            for (int k = 0; k < BeadLon; k++)
            {
                double b = 2 * Math.PI * k / BeadLon;
                int o = (i * BeadLon + k) * 3;
                double na = ca, nu = Math.Cos(b) * sa, nv = -Math.Sin(b) * sa;
                _tplNrm[o] = na; _tplNrm[o + 1] = nu; _tplNrm[o + 2] = nv;
                _tplPos[o] = na * r; _tplPos[o + 1] = nu * r; _tplPos[o + 2] = nv * r;
            }
        }
    }
}
