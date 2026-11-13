namespace ForgeSkin.Format;

// A charm's rope (Attachment.Charm), with no game reference: the plugin drives it, and the editor's
// Editor/src/renderer/viewport/charm.ts is this file line for line in TypeScript (same constants, same order of steps).
// FormatTests.CharmMatchesEditor runs both on one input and compares; change the two together.
//
// Nodes: 0 is the hanging point (the attachment's origin, pinned), 1..S the rope (S = Segments; node S is where the pendant
// hangs), S+1 the pendant's centre on a rigid arm below node S, heavier than a rope node, so the pendant swings on its own.
// Verlet integration in world space at a fixed sub-step, real gravity, distance constraints, a damped bend spring toward
// the straight line (the first segment toward the attachment's -Y), and the gear's body as boxes in the hanging point's
// frame that nodes are pushed out of; a last pass down the rope keeps every piece at most its length. The rope rides
// along with BodyCarry of the player's body (camera) move, so it feels only the rest of the body's speeding up and slowing
// down, while the gear's own move against the body (a turn, a flick, the walking bob) reaches it whole through the hanging
// point: it lags, swings out and swings back. Drag acts on the nodes' move against the hanging point (the chain's own
// friction: walking at a steady speed drags nothing), part of it growing with that speed so a big swing loses more. Weight
// is the charm's mass: drag and the bend spring move a heavier one less, so it swings longer, a light one settles sooner.
// A frame that jumps (Gap, Jump, of the hanging point or the body) puts it at rest: hanging straight along the attachment's -Y.
//
// Matrices are 16 numbers, column-major (element [col * 4 + row]); a pose is position xyz and quaternion xyzw.
public sealed class CharmRope
{
    public const int MaxSegments = 12, MaxPerGear = 4, MaxBoxes = 12, MaxLinks = 48;
    public const double Step = 1.0 / 240; // fixed sub-step, s
    public const int MaxSteps = 24; // per update (0.1 s); the rope only follows the rest of a longer frame rigidly
    public const double Gap = 0.25; // s between updates after which the rope is put at rest
    public const double Jump = 0.5; // m the hanging point moved in one update after which the rope is put at rest
    public const double Gravity = 9.81;
    public const double BodyCarry = 0.85; // share of the body's move the rope rides along with
    public const double SwingDrag = 0.5; // drag per (m/s against the hanging point / reach), at Weight 1
    public const int Iterations = 6, LengthPasses = 4; // per iteration, length sweeps then the body
    public const double MinPiece = 0.005; // m: a rope bends in no piece shorter than this
    public const double MaxSpeed = 5; // m/s, a node's speed relative to the hanging point
    public const double PendantMass = 4; // against one rope node
    // Bend spring: BendRate x Stiffness² rad/s at Weight 1; per sub-step pull and damping capped for stability.
    public const double BendRate = 120, BendMax = 0.25, BendDampMax = 0.5, PendantBend = 0.5;
    public const double DampSlow = 0.04, DampFast = 1; // drag as a share of critical damping at Damping 0 and 1, Weight 1
    // Look: a cord is a tube through the nodes (CordSub points per segment, CordSides around), a chain alternating oval links
    // (Thickness wide), beads balls Thickness across; links and beads are spread evenly along the rope.
    public const int CordSides = 6, CordSub = 3, LinkRing = 8, LinkTube = 4, BeadLat = 4, BeadLon = 6;
    public const double LinkLength = 1.7, LinkWire = 0.17, LinkPitch = 1.1, BeadPitch = 1.2; // times Thickness

    // Metallic and smoothness of the rope's surface per style (the editor's preview uses the same).
    public static double Metallic(CharmStyle s) => s == CharmStyle.Cord ? 0 : s == CharmStyle.Chain ? 0.9 : 0.8;
    public static double Smoothness(CharmStyle s) => s == CharmStyle.Cord ? 0.2 : s == CharmStyle.Chain ? 0.65 : 0.7;

    readonly int _s, _n, _style, _elements, _curvePoints, _template;
    readonly double _seg, _arm, _gravity, _keep, _swingDrag, _bend, _bendDamp, _radius, _pendantRadius, _thickness;
    readonly double[] _p, _q, _w;
    readonly double[] _box = new double[MaxBoxes * 15]; // per box: centre 3, axes 3x3, half sizes 3, in the hanging point's frame
    readonly double[] _world = new double[MaxBoxes * 15]; // the same in world space, per sub-step
    int _boxes;
    readonly double[] _cur = new double[7], _from = new double[7], _sub = new double[7], _last = new double[7], _t = new double[16];
    readonly double[] _body = new double[12]; // the player's body: at the frame's start, now, this sub-step, the last one
    readonly double[] _curve, _arc, _frame; // smoothed rope points, arc length to each, and u, v per point
    readonly double[] _tplPos, _tplNrm; // a link's or bead's vertices and normals in (along, u, v)
    double _time, _acc;
    bool _started;

    public CharmRope(Charm c, double pendantArm)
    {
        // In double from here on, as the editor computes.
        double length = c.Length, thickness = c.Thickness, stiffness = c.Stiffness, mass = c.Weight, damping = c.Damping;
        _s = Math.Clamp(Math.Min(c.Segments, (int)Math.Floor(length / MinPiece)), 1, MaxSegments);
        _n = _s + 2;
        _seg = length / _s;
        _arm = pendantArm;
        _gravity = Gravity * Step * Step;
        // Drag on a node's move against the hanging point: a share of critical damping for the rope's own swing (sqrt(g / reach)),
        // plus a part growing with that speed (SwingDrag per reach), so a large swing loses more; both less on a heavier charm.
        _swingDrag = SwingDrag / ((length + pendantArm) * mass);
        double zeta = Math.Min(DampSlow * Math.Pow(DampFast / DampSlow, damping) / mass, 1);
        _keep = Math.Exp(-2 * zeta * Math.Sqrt(Gravity / (length + pendantArm)) * Step);
        // Bending: a spring of BendRate x stiffness² rad/s, damped toward critical as the stiffness nears 1.
        double bend = BendRate * stiffness * stiffness * Step;
        _bend = Math.Min(bend * bend / mass, BendMax);
        _bendDamp = Math.Min(2 * bend * stiffness * stiffness / mass, BendDampMax);
        _thickness = thickness;
        _radius = thickness * 0.5;
        _pendantRadius = pendantArm * 0.8;
        _style = (int)c.Style;
        _p = new double[_n * 3];
        _q = new double[_n * 3];
        _w = new double[_n];
        for (int i = 1; i < _n; i++) _w[i] = i == _n - 1 ? 1 / PendantMass : 1;
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
    public int Boxes => _boxes;
    public int VertexCount => _style == (int)CharmStyle.Cord ? _curvePoints * CordSides : _elements * _template;

    // ---- the gear's body ----

    // A box of the gear's body: min and max in the gear's frame `gear`, taken into the hanging point's frame (`anchor`, both
    // at the same moment). A box that holds the hanging point is cut back to it on its nearest face, so the rope starts
    // outside; one cut to nothing, or past MaxBoxes, is left out.
    public void AddBox(double[] anchor, double[] gear, double[] min, double[] max)
    {
        if (_boxes >= MaxBoxes) return;
        Pose(anchor, _sub);
        int o = _boxes * 15;
        double cx = (min[0] + max[0]) / 2, cy = (min[1] + max[1]) / 2, cz = (min[2] + max[2]) / 2;
        // Centre: gear -> world -> hanging point's frame (rotation inverse, no scale).
        double wx = gear[0] * cx + gear[4] * cy + gear[8] * cz + gear[12] - _sub[0];
        double wy = gear[1] * cx + gear[5] * cy + gear[9] * cz + gear[13] - _sub[1];
        double wz = gear[2] * cx + gear[6] * cy + gear[10] * cz + gear[14] - _sub[2];
        Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], wx, wy, wz, _box, o);
        for (int j = 0; j < 3; j++)
        {
            double ax = gear[j * 4], ay = gear[j * 4 + 1], az = gear[j * 4 + 2];
            double len = Math.Sqrt(ax * ax + ay * ay + az * az);
            _box[o + 12 + j] = (max[j] - min[j]) / 2 * len;
            if (len > 0) { ax /= len; ay /= len; az /= len; }
            Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], ax, ay, az, _box, o + 3 + j * 3);
        }
        // The hanging point (the frame's origin) in box coordinates.
        int nearest = -1;
        double nearestGap = double.MaxValue;
        for (int j = 0; j < 3; j++)
        {
            double l = -(_box[o] * _box[o + 3 + j * 3] + _box[o + 1] * _box[o + 4 + j * 3] + _box[o + 2] * _box[o + 5 + j * 3]);
            double gap = _box[o + 12 + j] - Math.Abs(l);
            if (gap <= 0) { _boxes++; return; } // outside
            if (gap < nearestGap) { nearestGap = gap; nearest = j; }
        }
        {
            int j = nearest;
            double l = -(_box[o] * _box[o + 3 + j * 3] + _box[o + 1] * _box[o + 4 + j * 3] + _box[o + 2] * _box[o + 5 + j * 3]);
            double h = _box[o + 12 + j];
            // Keep the side away from the hanging point: [-h, l - margin] when it is on the + side, [l + margin, h] else.
            double lo = l >= 0 ? -h : l + _thickness, hi = l >= 0 ? l - _thickness : h;
            if (hi <= lo) return;
            double shift = (lo + hi) / 2;
            _box[o] += _box[o + 3 + j * 3] * shift;
            _box[o + 1] += _box[o + 4 + j * 3] * shift;
            _box[o + 2] += _box[o + 5 + j * 3] * shift;
            _box[o + 12 + j] = (hi - lo) / 2;
        }
        _boxes++;
    }

    // ---- simulation ----

    // At rest: straight along the attachment's -Y from the hanging point.
    public void Rest(double[] anchor)
    {
        Pose(anchor, _cur);
        Rotate(_cur[3], _cur[4], _cur[5], _cur[6], 0, -1, 0, _t, 0);
        for (int i = 0; i < _n; i++)
        {
            double d = Math.Min(i, _s) * _seg + (i > _s ? _arm : 0);
            _p[i * 3] = _q[i * 3] = _cur[0] + _t[0] * d;
            _p[i * 3 + 1] = _q[i * 3 + 1] = _cur[1] + _t[1] * d;
            _p[i * 3 + 2] = _q[i * 3 + 2] = _cur[2] + _t[2] * d;
        }
        Array.Copy(_cur, _from, 7);
        Array.Copy(_cur, _last, 7);
        Array.Fill(_body, double.NaN); // not known until the next update
        _acc = 0;
        _started = true;
    }

    // Advances to `time` (seconds) with the hanging point at `anchor` and the player's body (camera) at `body` (xyz).
    // Allocates nothing.
    public void Update(double time, double[] anchor, double[] body)
    {
        double[] b = _body;
        Pose(anchor, _cur);
        double dt = time - _time;
        double jx = _cur[0] - _from[0], jy = _cur[1] - _from[1], jz = _cur[2] - _from[2];
        double bx = body[0] - b[0], by = body[1] - b[1], bz = body[2] - b[2];
        if (!_started || dt > Gap || dt < 0 || !(jx * jx + jy * jy + jz * jz <= Jump * Jump) || !(bx * bx + by * by + bz * bz <= Jump * Jump))
        {
            _time = time;
            Rest(anchor);
            for (int i = 0; i < 12; i++) b[i] = body[i % 3];
            return;
        }
        b[3] = body[0]; b[4] = body[1]; b[5] = body[2];
        _time = time;
        _acc += dt;
        int steps = (int)Math.Floor(_acc / Step);
        _acc -= steps * Step;
        // Each sub-step takes the hanging point where it was at that sub-step's own time in this frame (the clock runs _acc
        // behind), so it moves at its real speed whatever the frame times. Past MaxSteps, the frame's start is only followed.
        int skip = Math.Max(0, steps - MaxSteps);
        for (int k = Math.Max(skip, 1); k <= steps; k++)
        {
            double f = 1 - (_acc + (steps - k) * Step) / dt;
            _sub[0] = _from[0] + (_cur[0] - _from[0]) * f;
            _sub[1] = _from[1] + (_cur[1] - _from[1]) * f;
            _sub[2] = _from[2] + (_cur[2] - _from[2]) * f;
            Slerp(_from, _cur, f, _sub);
            for (int i = 0; i < 3; i++) b[6 + i] = b[i] + (b[3 + i] - b[i]) * f;
            if (k == skip) Follow();
            else Substep();
            Array.Copy(_sub, _last, 7);
            b[9] = b[6]; b[10] = b[7]; b[11] = b[8];
        }
        Array.Copy(_cur, _from, 7);
        b[0] = b[3]; b[1] = b[4]; b[2] = b[5];
    }

    // Moves every node rigidly with the hanging point's move from _last to _sub: rotation _sub * inverse(_last) about the
    // hanging point, then its move.
    void Follow()
    {
        double ax = _last[3], ay = _last[4], az = _last[5], aw = _last[6];
        double bx = _sub[3], by = _sub[4], bz = _sub[5], bw = _sub[6];
        double dx = bw * -ax + bx * aw + by * -az - bz * -ay;
        double dy = bw * -ay - bx * -az + by * aw + bz * -ax;
        double dz = bw * -az + bx * -ay - by * -ax + bz * aw;
        double dw = bw * aw - bx * -ax - by * -ay - bz * -az;
        for (int i = 1; i < _n; i++)
        {
            Carried(_p, i * 3, dx, dy, dz, dw);
            Carried(_q, i * 3, dx, dy, dz, dw);
        }
    }

    // One sub-step from the hanging point's pose _last to _sub.
    void Substep()
    {
        // The rope rides along with BodyCarry of the player's body move, so it feels only the rest of the body's speeding up
        // and slowing down; the gear's own move against the body (a turn, a flick, the walking bob) reaches it whole.
        double[] bd = _body;
        double cx = (bd[6] - bd[9]) * BodyCarry, cy = (bd[7] - bd[10]) * BodyCarry, cz = (bd[8] - bd[11]) * BodyCarry;
        // The hanging point's move this sub-step in that riding frame. Verlet there: a node's move against the hanging point's
        // loses drag (the chain's own friction, so walking at a steady speed drags nothing) and is capped at MaxSpeed.
        double mx = _sub[0] - _last[0] - cx, my = _sub[1] - _last[1] - cy, mz = _sub[2] - _last[2] - cz;
        for (int i = 1; i < _n; i++)
        {
            int o = i * 3;
            double rx = _p[o] - _q[o] - mx, ry = _p[o + 1] - _q[o + 1] - my, rz = _p[o + 2] - _q[o + 2] - mz;
            double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            double keep = _keep * Math.Exp(-_swingDrag * r);
            rx *= keep; ry *= keep; rz *= keep; r *= keep;
            if (r > MaxSpeed * Step)
            {
                double k = MaxSpeed * Step / r;
                rx *= k; ry *= k; rz *= k;
            }
            _p[o] += cx; _p[o + 1] += cy; _p[o + 2] += cz;
            _q[o] = _p[o]; _q[o + 1] = _p[o + 1]; _q[o + 2] = _p[o + 2];
            _p[o] += mx + rx;
            _p[o + 1] += my + ry - _gravity;
            _p[o + 2] += mz + rz;
        }
        _p[0] = _q[0] = _sub[0];
        _p[1] = _q[1] = _sub[1];
        _p[2] = _q[2] = _sub[2];
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], 0, -1, 0, _t, 0); // the attachment's -Y now
        for (int b = 0; b < _boxes; b++)
        {
            int o = b * 15;
            Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o], _box[o + 1], _box[o + 2], _world, o);
            _world[o] += _sub[0]; _world[o + 1] += _sub[1]; _world[o + 2] += _sub[2];
            for (int j = 0; j < 3; j++)
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o + 3 + j * 3], _box[o + 4 + j * 3], _box[o + 5 + j * 3], _world, o + 3 + j * 3);
            _world[o + 12] = _box[o + 12]; _world[o + 13] = _box[o + 13]; _world[o + 14] = _box[o + 14];
        }
        // The bend once: a damped spring toward the straight line, the first piece toward the attachment's -Y. Then per
        // iteration the lengths (LengthPasses sweeps, so the rope does not stretch and swing fast like a spring) and the body.
        if (_bend > 0)
            for (int i = 1; i < _n; i++)
            {
                int o = i * 3, a = o - 3;
                double tx = _t[0], ty = _t[1], tz = _t[2];
                if (i > 1)
                {
                    tx = _p[a] - _p[a - 3]; ty = _p[a + 1] - _p[a - 2]; tz = _p[a + 2] - _p[a - 1];
                    double len = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (len < 1e-9) continue;
                    tx /= len; ty /= len; tz /= len;
                }
                double rest = i - 1 < _s ? _seg : _arm;
                double k = i == _n - 1 ? _bend * PendantBend : _bend, d = i == _n - 1 ? _bendDamp * PendantBend : _bendDamp;
                // The node's move this sub-step against the node above's (for the first, the hanging point's move with gravity's).
                double ux = i > 1 ? _p[a] - _q[a] : mx, uy = i > 1 ? _p[a + 1] - _q[a + 1] : my - _gravity, uz = i > 1 ? _p[a + 2] - _q[a + 2] : mz;
                _p[o] += (_p[a] + tx * rest - _p[o]) * k - (_p[o] - _q[o] - ux) * d;
                _p[o + 1] += (_p[a + 1] + ty * rest - _p[o + 1]) * k - (_p[o + 1] - _q[o + 1] - uy) * d;
                _p[o + 2] += (_p[a + 2] + tz * rest - _p[o + 2]) * k - (_p[o + 2] - _q[o + 2] - uz) * d;
            }
        for (int it = 0; it < Iterations; it++)
        {
            for (int pass = 0; pass < LengthPasses; pass++)
                for (int i = 0; i < _n - 1; i++)
                {
                    int a = i * 3, b = a + 3;
                    double rest = i < _s ? _seg : _arm;
                    double ex = _p[b] - _p[a], ey = _p[b + 1] - _p[a + 1], ez = _p[b + 2] - _p[a + 2];
                    double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    double sum = _w[i] + _w[i + 1];
                    if (len < 1e-9 || sum <= 0) continue;
                    double diff = (len - rest) / (len * sum);
                    _p[a] += ex * diff * _w[i]; _p[a + 1] += ey * diff * _w[i]; _p[a + 2] += ez * diff * _w[i];
                    _p[b] -= ex * diff * _w[i + 1]; _p[b + 1] -= ey * diff * _w[i + 1]; _p[b + 2] -= ez * diff * _w[i + 1];
                }
            for (int b = 0; b < _boxes; b++)
            {
                int o = b * 15;
                for (int i = 1; i < _n; i++)
                {
                    int v = i * 3;
                    double r = i == _n - 1 ? _pendantRadius : _radius;
                    double rx = _p[v] - _world[o], ry = _p[v + 1] - _world[o + 1], rz = _p[v + 2] - _world[o + 2];
                    int axis = -1;
                    double best = double.MaxValue, push = 0;
                    for (int j = 0; j < 3; j++)
                    {
                        double l = rx * _world[o + 3 + j * 3] + ry * _world[o + 4 + j * 3] + rz * _world[o + 5 + j * 3];
                        double e = _world[o + 12 + j] + r;
                        double gap = e - Math.Abs(l);
                        if (gap <= 0) { axis = -1; break; }
                        if (gap < best) { best = gap; axis = j; push = (l >= 0 ? e : -e) - l; }
                    }
                    if (axis < 0) continue;
                    _p[v] += _world[o + 3 + axis * 3] * push;
                    _p[v + 1] += _world[o + 4 + axis * 3] * push;
                    _p[v + 2] += _world[o + 5 + axis * 3] * push;
                }
            }
        }
        // Last, down from the hanging point: no rope piece longer than its length and the pendant's arm exactly its length.
        // Like every correction this becomes speed (the rope's pull); the length passes leave little to take out.
        for (int i = 1; i < _n; i++)
        {
            int o = i * 3, a = o - 3;
            double rest = i - 1 < _s ? _seg : _arm;
            double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
            double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (len < 1e-9 || (len <= rest && i - 1 < _s)) continue;
            double k = rest / len;
            double sx = _p[a] + ex * k - _p[o], sy = _p[a + 1] + ey * k - _p[o + 1], sz = _p[a + 2] + ez * k - _p[o + 2];
            _p[o] += sx; _p[o + 1] += sy; _p[o + 2] += sz;
        }
    }

    // x = hanging point now + d * (x - hanging point before), for the node at `o` of `x`.
    void Carried(double[] x, int o, double dx, double dy, double dz, double dw)
    {
        Rotate(dx, dy, dz, dw, x[o] - _last[0], x[o + 1] - _last[1], x[o + 2] - _last[2], _t, 0);
        x[o] = _sub[0] + _t[0];
        x[o + 1] = _sub[1] + _t[1];
        x[o + 2] = _sub[2] + _t[2];
    }

    // ---- output ----

    // The pendant's world pose (position xyz, quaternion xyzw): on node S, turned from the attachment's rest pose by the
    // turn that takes its -Y onto the arm.
    public void Pendant(double[] pose)
    {
        int s = _s * 3, e = s + 3;
        double ax = _p[e] - _p[s], ay = _p[e + 1] - _p[s + 1], az = _p[e + 2] - _p[s + 2];
        double len = Math.Sqrt(ax * ax + ay * ay + az * az);
        Rotate(_cur[3], _cur[4], _cur[5], _cur[6], 0, -1, 0, _t, 0);
        double fx = 0, fy = 0, fz = 0, fw = 1;
        if (len > 1e-9)
        {
            ax /= len; ay /= len; az /= len;
            double d = _t[0] * ax + _t[1] * ay + _t[2] * az;
            if (d < -0.999999)
            {
                // Opposite: half a turn about the attachment's X.
                Rotate(_cur[3], _cur[4], _cur[5], _cur[6], 1, 0, 0, _t, 3);
                fx = _t[3]; fy = _t[4]; fz = _t[5]; fw = 0;
            }
            else
            {
                fx = _t[1] * az - _t[2] * ay; fy = _t[2] * ax - _t[0] * az; fz = _t[0] * ay - _t[1] * ax; fw = 1 + d;
                double l = Math.Sqrt(fx * fx + fy * fy + fz * fz + fw * fw);
                fx /= l; fy /= l; fz /= l; fw /= l;
            }
        }
        double qx = _cur[3], qy = _cur[4], qz = _cur[5], qw = _cur[6];
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

    // ---- math ----

    // Position and rotation (unit quaternion) of a matrix whose columns may carry scale.
    static void Pose(double[] m, double[] pose)
    {
        pose[0] = m[12]; pose[1] = m[13]; pose[2] = m[14];
        double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
        double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
        double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
        double m00 = m[0] / sx, m10 = m[1] / sx, m20 = m[2] / sx;
        double m01 = m[4] / sy, m11 = m[5] / sy, m21 = m[6] / sy;
        double m02 = m[8] / sz, m12 = m[9] / sz, m22 = m[10] / sz;
        double tr = m00 + m11 + m22, x, y, z, w;
        if (tr > 0)
        {
            double s = 0.5 / Math.Sqrt(tr + 1);
            w = 0.25 / s; x = (m21 - m12) * s; y = (m02 - m20) * s; z = (m10 - m01) * s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = 2 * Math.Sqrt(1 + m00 - m11 - m22);
            w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = 2 * Math.Sqrt(1 + m11 - m00 - m22);
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
        }
        else
        {
            double s = 2 * Math.Sqrt(1 + m22 - m00 - m11);
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
        }
        double l = Math.Sqrt(x * x + y * y + z * z + w * w);
        pose[3] = x / l; pose[4] = y / l; pose[5] = z / l; pose[6] = w / l;
    }

    // Rotates (vx, vy, vz) by the unit quaternion (x, y, z, w) into o[at..at+2].
    static void Rotate(double x, double y, double z, double w, double vx, double vy, double vz, double[] o, int at)
    {
        double cx = y * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - y * vx;
        double ex = y * cz - z * cy, ey = z * cx - x * cz, ez = x * cy - y * cx;
        o[at] = vx + 2 * (w * cx + ex);
        o[at + 1] = vy + 2 * (w * cy + ey);
        o[at + 2] = vz + 2 * (w * cz + ez);
    }

    // The rotation f of the way from a's to b's (poses, quaternion at 3..6) into o[3..6].
    static void Slerp(double[] a, double[] b, double f, double[] o)
    {
        double bx = b[3], by = b[4], bz = b[5], bw = b[6];
        double d = a[3] * bx + a[4] * by + a[5] * bz + a[6] * bw;
        if (d < 0) { d = -d; bx = -bx; by = -by; bz = -bz; bw = -bw; }
        double ka, kb;
        if (d > 0.9995) { ka = 1 - f; kb = f; }
        else
        {
            double th = Math.Acos(d), sn = Math.Sin(th);
            ka = Math.Sin((1 - f) * th) / sn; kb = Math.Sin(f * th) / sn;
        }
        double x = a[3] * ka + bx * kb, y = a[4] * ka + by * kb, z = a[5] * ka + bz * kb, w = a[6] * ka + bw * kb;
        double l = Math.Sqrt(x * x + y * y + z * z + w * w);
        o[3] = x / l; o[4] = y / l; o[5] = z / l; o[6] = w / l;
    }
}
