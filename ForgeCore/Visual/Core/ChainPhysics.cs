using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>What an author sets for a chain (a charm's rope, a hair strand, a skirt, a tail or ribbon), plus the solver's sub-step and
/// passes. The few author values stay in plain ranges; the presets are starting points the editor offers.</summary>
public sealed class ChainSpec
{
    /// <summary>0: limp; 1: a stiff wire that keeps going the way the piece above it points.</summary>
    public double Stiffness { get; set; } = 0.25;
    /// <summary>0: none; 1: springs straight back to the shape it was bound in (hair keeps its style). A hanging rope has none.</summary>
    public double Restore { get; set; }
    /// <summary>0: swings long; 1: settles at once.</summary>
    public double Damping { get; set; } = 0.3;
    /// <summary>The chain's mass, 0.1 to 3: heavier swings longer, lighter settles sooner.</summary>
    public double Weight { get; set; } = 1;
    /// <summary>Times real gravity.</summary>
    public double Gravity { get; set; } = 1;
    /// <summary>0 to 1: the share of the body's own move the chain does not ride along with, so it feels that much of the body's
    /// speeding up and slowing down. The anchor's move against the body (a turn, a flick, a step's bob) always reaches it whole.</summary>
    public double Inertia { get; set; } = 0.15;
    /// <summary>Metres: how far a node is kept out of the colliders.</summary>
    public double Radius { get; set; } = 0.001;
    /// <summary>Degrees a joint may bend away from its bound bend; 0: no limit.</summary>
    public double AngleLimit { get; set; }
    /// <summary>Kept out of the colliders (boxes and capsules).</summary>
    public bool Collide { get; set; } = true;
    /// <summary>The fixed sub-step in seconds; every frame runs as many as fit, whatever the frame rate.</summary>
    public double Step { get; set; } = 1.0 / 240;
    /// <summary>Per sub-step: iterations of (length passes, side links, colliders).</summary>
    public int Iterations { get; set; } = 6;
    public int LengthPasses { get; set; } = 4;

    /// <summary>A short charm chain: ForgeSkin's charm defaults (fine sub-step, many passes: a few nodes, cheap).</summary>
    public static ChainSpec Charm() => new();
    /// <summary>Long hair or a ponytail: holds its style loosely, swings wide, bends at most 50 degrees a joint.</summary>
    public static ChainSpec LongHair() => new() { Stiffness = 0.35, Restore = 0.3, Damping = 0.4, Inertia = 0.3, Radius = 0.015, AngleLimit = 50, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
    /// <summary>Short hair: springs back fast and bends little.</summary>
    public static ChainSpec ShortHair() => new() { Stiffness = 0.6, Restore = 0.7, Damping = 0.6, Inertia = 0.2, Radius = 0.012, AngleLimit = 30, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
    /// <summary>A skirt: strands linked side by side (<see cref="ChainShape.LinkSide"/>), back toward its shape, kept off the legs.</summary>
    public static ChainSpec Skirt() => new() { Stiffness = 0.3, Restore = 0.5, Damping = 0.5, Inertia = 0.25, Radius = 0.02, AngleLimit = 60, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
    /// <summary>A tail or ribbon: loose and slow to settle.</summary>
    public static ChainSpec Tail() => new() { Stiffness = 0.2, Restore = 0.1, Damping = 0.2, Inertia = 0.4, Radius = 0.01, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
}

/// <summary>The nodes of one chain set in the anchor's frame, built once: roots pinned to the anchor, every other node hanging from a
/// parent added before it, and side links between strands (a skirt's hem). Setup only.</summary>
public sealed class ChainShape
{
    internal readonly List<int> Parent = new();
    internal readonly List<double> Length = new(), Mass = new(), Bend = new(), Radius = new();
    internal readonly List<double> Dir = new(); // per node: a root's offset from the anchor, else the unit direction from its parent at rest
    internal readonly List<bool> Rigid = new();
    internal readonly List<int> Side = new(); // node pairs

    public int Count => Parent.Count;

    /// <summary>A root, pinned at (x, y, z) in the anchor's frame.</summary>
    public int AddRoot(double x, double y, double z) => Push(-1, 0, x, y, z, 0, 0, 0, false);

    /// <summary>A node `length` from `parent` along (dx, dy, dz) in the anchor's frame at rest. `mass` against an ordinary node, `bend` scales
    /// the bend spring on it, `radius` below 0 takes the spec's. A rigid node is held at exactly its length (a pendant's arm); any
    /// other is held at most its length.</summary>
    public int Add(int parent, double length, double dx, double dy, double dz, double mass = 1, double bend = 1, double radius = -1, bool rigid = false)
    {
        if (parent < 0 || parent >= Count) throw new ArgumentOutOfRangeException(nameof(parent));
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(l > 0) || !(length > 0) || !(mass > 0)) throw new ArgumentException("a node needs a length, a direction and a mass");
        return Push(parent, length, dx / l, dy / l, dz / l, mass, bend, radius, rigid);
    }

    /// <summary>Links two nodes side by side at their rest distance (half as stiff as a strand's own pieces).</summary>
    public void Link(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Count || b >= Count) throw new ArgumentOutOfRangeException(nameof(b));
        Side.Add(a); Side.Add(b);
    }

    /// <summary>Links strands side by side, node to node at the same depth below their roots (a skirt's hem), each to the next, and
    /// the last to the first when `loop`. A strand is its node indices from the root down.</summary>
    public void LinkSide(IReadOnlyList<int[]> strands, bool loop)
    {
        for (int k = 0; k + 1 < strands.Count || (loop && strands.Count > 2 && k < strands.Count); k++)
        {
            int[] a = strands[k], b = strands[(k + 1) % strands.Count];
            for (int j = 1; j < Math.Min(a.Length, b.Length); j++) Link(a[j], b[j]);
        }
    }

    int Push(int parent, double length, double x, double y, double z, double mass, double bend, double radius, bool rigid)
    {
        Parent.Add(parent); Length.Add(length); Mass.Add(mass); Bend.Add(bend); Radius.Add(radius); Rigid.Add(rigid);
        Dir.Add(x); Dir.Add(y); Dir.Add(z);
        return Count - 1;
    }
}

/// <summary>
/// One chain set moving: the one chain physics for charm ropes and spring bones (hair, skirts, tails), with no game reference so the
/// game, the skin editor's backend and the tests run the same code.
/// <para>Verlet integration in world space at a fixed sub-step under real gravity; each sub-step takes the anchor where it was at that
/// sub-step's own time in the frame (pose interpolated), so the motion does not depend on the frame rate; a frame of more than
/// <see cref="MaxSteps"/> sub-steps only follows the rest rigidly. The chain rides along with (1 - Inertia) of the body's move, so it
/// feels only the rest of the body's speeding up and slowing down; a body faster than <see cref="BodyLimit"/> takes its excess whole
/// (a fall or a knock-back does not fling it). Drag acts on the nodes' move against the anchor, growing with that speed; weight lessens
/// drag and springs. Per sub-step: the bend once (a damped spring toward the straight continuation of the piece above, the first piece
/// toward its bound direction; the restore spring toward the bound shape carried by the anchor; both pulls equal and opposite between a
/// node and the one above, shared by their weights, so a chain held still against a collider settles; the angle limit), then per iteration the
/// length passes, the side links and the colliders (boxes in the anchor's frame, capsules in the world interpolated per sub-step), last
/// a sweep down every strand so no piece is longer than its length. A frame after a gap, or whose anchor or body jumps or turns too far,
/// puts the chain at rest in its bound shape.</para>
/// <para>Data is flat arrays indexed by node (positions, previous positions, weights, lengths), parents before children, so a sweep is
/// one loop; nothing allocates after construction.</para>
/// Matrices are 16 numbers, column-major (element [col * 4 + row]); a pose is position xyz and quaternion xyzw.
/// </summary>
public sealed class ChainState
{
    public const int MaxSteps = 24; // per update; the chain only follows the rest of a longer frame rigidly
    public const double Gap = 0.25; // s between updates after which the chain is put at rest
    public const double Jump = 0.5; // m the anchor or the body moved in one update after which the chain is put at rest
    public const double TurnJump = 0.70710678118654757; // |cos(half the anchor's turn)| below which (over 90 degrees in one update) it is put at rest
    public const double G = 9.81;
    public const double BodyLimit = 10; // m/s of the body's move beyond which the excess is carried whole
    public const double SwingDrag = 0.5; // drag per (m/s against the anchor / reach), at Weight 1
    public const double MinPiece = 0.005; // m: callers make no piece shorter than this
    public const double MaxSpeed = 5; // m/s, a node's speed relative to the anchor
    // Bend spring: BendRate x Stiffness² rad/s at Weight 1; per sub-step pull and damping capped for stability. Restore likewise.
    public const double BendRate = 120, BendMax = 0.25, BendDampMax = 0.5, RestoreRate = 90, RestoreMax = 0.5;
    public const double DampSlow = 0.04, DampFast = 1; // drag as a share of critical damping at Damping 0 and 1, Weight 1
    public const double SideStiffness = 0.5; // side links against a strand's own pieces

    readonly int _n, _iterations, _passes;
    readonly double _step, _gravity, _keep, _swingDrag, _bend, _bendDamp, _restore, _cosLimit, _sinLimit, _carry, _margin;
    readonly bool _collide;
    readonly int[] _parent, _side;
    readonly bool[] _rigid, _straight;
    readonly double[] _len, _dir, _w, _bendK, _bendD, _radius, _sideLen;
    readonly double[] _p, _q, _rm;
    double[] _box = Array.Empty<double>(); // per box: centre 3, axes 3x3, half sizes 3, in the anchor's frame; grown by AddBox
    double[] _world = Array.Empty<double>(); // the same in world space, per sub-step
    int _boxes, _capsules;
    double[] _cap = Array.Empty<double>(), _capFrom = Array.Empty<double>(), _capSub = Array.Empty<double>(); // a, b, radius; grown by Capsules
    readonly double[] _cur = new double[7], _from = new double[7], _sub = new double[7], _last = new double[7], _t = new double[16];
    readonly double[] _body = new double[12]; // the body: at the frame's start, now, this sub-step, the last one
    double _time, _acc, _f;
    bool _started;

    public ChainState(ChainShape shape, ChainSpec spec)
    {
        _n = shape.Count;
        if (_n < 2 || shape.Parent[0] != -1) throw new ArgumentException("a chain needs a root first and a node below it");
        _step = spec.Step;
        _iterations = Math.Max(spec.Iterations, 1);
        _passes = Math.Max(spec.LengthPasses, 1);
        double mass = spec.Weight, damping = spec.Damping, stiffness = spec.Stiffness;
        _parent = shape.Parent.ToArray();
        _len = shape.Length.ToArray();
        _dir = shape.Dir.ToArray();
        _rigid = shape.Rigid.ToArray();
        _side = shape.Side.ToArray();
        _w = new double[_n];
        _bendK = new double[_n];
        _bendD = new double[_n];
        _radius = new double[_n];
        _straight = new bool[_n];
        _p = new double[_n * 3];
        _q = new double[_n * 3];
        _rm = new double[_n * 3];
        // Reach: the longest way from a root down, for the swing's own pace.
        var reachTo = new double[_n];
        double reach = 0;
        for (int i = 0; i < _n; i++)
        {
            int a = _parent[i];
            if (a >= i) throw new ArgumentException("a parent comes before its node");
            if (a < 0) continue;
            reachTo[i] = reachTo[a] + _len[i];
            reach = Math.Max(reach, reachTo[i]);
            // The bend's target is the piece above's direction itself when the node was bound straight on from it.
            _straight[i] = _parent[a] >= 0 && _dir[i * 3] == _dir[a * 3] && _dir[i * 3 + 1] == _dir[a * 3 + 1] && _dir[i * 3 + 2] == _dir[a * 3 + 2];
        }
        _gravity = G * spec.Gravity * _step * _step;
        // Drag on a node's move against the anchor: a share of critical damping for the chain's own swing (sqrt(g / reach)), plus a
        // part growing with that speed (SwingDrag per reach), so a large swing loses more; both less on a heavier chain.
        _swingDrag = SwingDrag / (reach * mass);
        double zeta = Math.Min(DampSlow * Math.Pow(DampFast / DampSlow, damping) / mass, 1);
        _keep = Math.Exp(-2 * zeta * Math.Sqrt(G / reach) * _step);
        // Bending: a spring of BendRate x stiffness² rad/s, damped toward critical as the stiffness nears 1.
        double bend = BendRate * stiffness * stiffness * _step;
        _bend = Math.Min(bend * bend / mass, BendMax);
        _bendDamp = Math.Min(2 * bend * stiffness * stiffness / mass, BendDampMax);
        double restore = RestoreRate * spec.Restore * _step;
        _restore = Math.Min(restore * restore / mass, RestoreMax);
        double limit = spec.AngleLimit > 0 && spec.AngleLimit < 180 ? spec.AngleLimit * Math.PI / 180 : 0;
        _cosLimit = limit > 0 ? Math.Cos(limit) : -2;
        _sinLimit = Math.Sin(limit);
        _carry = 1 - spec.Inertia;
        _collide = spec.Collide;
        _margin = spec.Radius * 2;
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            _w[i] = 1 / shape.Mass[i];
            _bendK[i] = _bend * shape.Bend[i];
            _bendD[i] = _bendDamp * shape.Bend[i];
            _radius[i] = shape.Radius[i] < 0 ? spec.Radius : shape.Radius[i];
        }
        // Side links at their rest distance.
        _sideLen = new double[_side.Length / 2];
        var rest = new double[_n * 3];
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3, a = _parent[i] * 3;
            for (int c = 0; c < 3; c++) rest[o + c] = a < 0 ? _dir[o + c] : rest[a + c] + _dir[o + c] * _len[i];
        }
        for (int k = 0; k < _sideLen.Length; k++)
        {
            int a = _side[k * 2] * 3, b = _side[k * 2 + 1] * 3;
            double x = rest[b] - rest[a], y = rest[b + 1] - rest[a + 1], z = rest[b + 2] - rest[a + 2];
            _sideLen[k] = Math.Sqrt(x * x + y * y + z * z);
        }
    }

    /// <summary>World positions of the nodes, three per node, in the shape's order.</summary>
    public double[] Nodes => _p;
    public int Count => _n;
    public int Boxes => _boxes;
    /// <summary>The anchor's pose (position xyz, quaternion xyzw) at the last update.</summary>
    public double[] Anchor => _cur;

    // ---- colliders ----

    /// <summary>A box of a rigid body that moves with the anchor: min and max in the body's frame `body`, taken into the anchor's frame
    /// (`anchor`, both at the same moment). A box that holds the anchor is cut back to it on its nearest face, so the chain starts
    /// outside; one cut to nothing is left out.</summary>
    public void AddBox(double[] anchor, double[] body, double[] min, double[] max)
    {
        int o = _boxes * 15;
        if (_box.Length < o + 15) { Array.Resize(ref _box, o + 15); Array.Resize(ref _world, o + 15); }
        Pose(anchor, _sub);
        double cx = (min[0] + max[0]) / 2, cy = (min[1] + max[1]) / 2, cz = (min[2] + max[2]) / 2;
        // Centre: body -> world -> anchor's frame (rotation inverse, no scale).
        double wx = body[0] * cx + body[4] * cy + body[8] * cz + body[12] - _sub[0];
        double wy = body[1] * cx + body[5] * cy + body[9] * cz + body[13] - _sub[1];
        double wz = body[2] * cx + body[6] * cy + body[10] * cz + body[14] - _sub[2];
        Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], wx, wy, wz, _box, o);
        for (int j = 0; j < 3; j++)
        {
            double ax = body[j * 4], ay = body[j * 4 + 1], az = body[j * 4 + 2];
            double len = Math.Sqrt(ax * ax + ay * ay + az * az);
            _box[o + 12 + j] = (max[j] - min[j]) / 2 * len;
            if (len > 0) { ax /= len; ay /= len; az /= len; }
            Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], ax, ay, az, _box, o + 3 + j * 3);
        }
        // The anchor (the frame's origin) in box coordinates.
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
            // Keep the side away from the anchor: [-h, l - margin] when it is on the + side, [l + margin, h] else.
            double lo = l >= 0 ? -h : l + _margin, hi = l >= 0 ? l - _margin : h;
            if (hi <= lo) return;
            double shift = (lo + hi) / 2;
            _box[o] += _box[o + 3 + j * 3] * shift;
            _box[o + 1] += _box[o + 4 + j * 3] * shift;
            _box[o + 2] += _box[o + 5 + j * 3] * shift;
            _box[o + 12 + j] = (hi - lo) / 2;
        }
        _boxes++;
    }

    /// <summary>How many capsules the chain is kept out of; set each with <see cref="SetCapsule"/> before every update. Setting a larger
    /// number than before grows the arrays once.</summary>
    public int Capsules
    {
        get => _capsules;
        set
        {
            _capsules = Math.Max(value, 0);
            if (_cap.Length >= _capsules * 7) return;
            Array.Resize(ref _cap, _capsules * 7);
            Array.Resize(ref _capFrom, _capsules * 7);
            Array.Resize(ref _capSub, _capsules * 7);
        }
    }

    /// <summary>Capsule `i` now, in the world: the segment from a to b and its radius (a sphere when a is b). Each sub-step takes it
    /// where it was at that sub-step's time, from the last update's place to this one.</summary>
    public void SetCapsule(int i, double ax, double ay, double az, double bx, double by, double bz, double radius)
    {
        int o = i * 7;
        _cap[o] = ax; _cap[o + 1] = ay; _cap[o + 2] = az; _cap[o + 3] = bx; _cap[o + 4] = by; _cap[o + 5] = bz; _cap[o + 6] = radius;
    }

    // ---- simulation ----

    /// <summary>At rest: the bound shape carried by the anchor.</summary>
    public void Rest(double[] anchor)
    {
        Pose(anchor, _cur);
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3, a = _parent[i] * 3;
            if (a < 0)
            {
                Rotate(_cur[3], _cur[4], _cur[5], _cur[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
                _p[o] = _q[o] = _cur[0] + _t[0];
                _p[o + 1] = _q[o + 1] = _cur[1] + _t[1];
                _p[o + 2] = _q[o + 2] = _cur[2] + _t[2];
                continue;
            }
            Rotate(_cur[3], _cur[4], _cur[5], _cur[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
            _p[o] = _q[o] = _p[a] + _t[0] * _len[i];
            _p[o + 1] = _q[o + 1] = _p[a + 1] + _t[1] * _len[i];
            _p[o + 2] = _q[o + 2] = _p[a + 2] + _t[2] * _len[i];
        }
        Array.Copy(_cur, _from, 7);
        Array.Copy(_cur, _last, 7);
        Array.Copy(_cap, _capFrom, _capsules * 7);
        Array.Fill(_body, double.NaN); // not known until the next update
        _acc = 0;
        _started = true;
    }

    /// <summary>Advances to `time` (seconds) with the anchor at `anchor` and the body (a charm's player camera, a character's root) at
    /// `body` (xyz). Allocates nothing.</summary>
    public void Update(double time, double[] anchor, double[] body)
    {
        double[] b = _body;
        Pose(anchor, _cur);
        double dt = time - _time;
        double jx = _cur[0] - _from[0], jy = _cur[1] - _from[1], jz = _cur[2] - _from[2];
        double bx = body[0] - b[0], by = body[1] - b[1], bz = body[2] - b[2];
        double turn = Math.Abs(_cur[3] * _from[3] + _cur[4] * _from[4] + _cur[5] * _from[5] + _cur[6] * _from[6]);
        if (!_started || dt > Gap || dt < 0 || !(jx * jx + jy * jy + jz * jz <= Jump * Jump) || !(bx * bx + by * by + bz * bz <= Jump * Jump) || turn < TurnJump)
        {
            _time = time;
            Rest(anchor);
            for (int i = 0; i < 12; i++) b[i] = body[i % 3];
            return;
        }
        b[3] = body[0]; b[4] = body[1]; b[5] = body[2];
        _time = time;
        _acc += dt;
        int steps = (int)Math.Floor(_acc / _step);
        _acc -= steps * _step;
        // Each sub-step takes the anchor where it was at that sub-step's own time in this frame (the clock runs _acc behind), so it
        // moves at its real speed whatever the frame times. Past MaxSteps, the frame's start is only followed.
        int skip = Math.Max(0, steps - MaxSteps);
        for (int k = Math.Max(skip, 1); k <= steps; k++)
        {
            double f = 1 - (_acc + (steps - k) * _step) / dt;
            _f = f;
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
        Array.Copy(_cap, _capFrom, _capsules * 7);
        b[0] = b[3]; b[1] = b[4]; b[2] = b[5];
    }

    // A root's place at the sub-step's anchor pose into _t[0..2].
    void RootAt(int o)
    {
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
        _t[0] += _sub[0]; _t[1] += _sub[1]; _t[2] += _sub[2];
    }

    // Moves every node rigidly with the anchor's move from _last to _sub: rotation _sub * inverse(_last) about the anchor, then its move.
    void Follow()
    {
        double ax = _last[3], ay = _last[4], az = _last[5], aw = _last[6];
        double bx = _sub[3], by = _sub[4], bz = _sub[5], bw = _sub[6];
        double dx = bw * -ax + bx * aw + by * -az - bz * -ay;
        double dy = bw * -ay - bx * -az + by * aw + bz * -ax;
        double dz = bw * -az + bx * -ay - by * -ax + bz * aw;
        double dw = bw * aw - bx * -ax - by * -ay - bz * -az;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            if (_parent[i] < 0)
            {
                RootAt(o);
                _p[o] = _q[o] = _t[0]; _p[o + 1] = _q[o + 1] = _t[1]; _p[o + 2] = _q[o + 2] = _t[2];
                continue;
            }
            Carried(_p, o, dx, dy, dz, dw);
            Carried(_q, o, dx, dy, dz, dw);
        }
    }

    // One sub-step from the anchor's pose _last to _sub.
    void Substep()
    {
        // The chain rides along with (1 - Inertia) of the body's move, the excess over BodyLimit whole.
        double[] bd = _body;
        double ex0 = bd[6] - bd[9], ey0 = bd[7] - bd[10], ez0 = bd[8] - bd[11];
        double carry = _carry, moved = Math.Sqrt(ex0 * ex0 + ey0 * ey0 + ez0 * ez0);
        if (moved > BodyLimit * _step) carry += (1 - carry) * (1 - BodyLimit * _step / moved);
        double cx = ex0 * carry, cy = ey0 * carry, cz = ez0 * carry;
        // The anchor's move this sub-step in that riding frame. Verlet there: a node's move against the anchor's loses drag (the
        // chain's own friction, so walking at a steady speed drags nothing) and is capped at MaxSpeed.
        double mx = _sub[0] - _last[0] - cx, my = _sub[1] - _last[1] - cy, mz = _sub[2] - _last[2] - cz;
        double cap = MaxSpeed * _step;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            if (_parent[i] < 0)
            {
                // Pinned: its own move this sub-step (in the riding frame) is what the first pieces below it bend against.
                RootAt(o);
                _rm[o] = _t[0] - _p[o] - cx; _rm[o + 1] = _t[1] - _p[o + 1] - cy; _rm[o + 2] = _t[2] - _p[o + 2] - cz;
                _p[o] = _q[o] = _t[0]; _p[o + 1] = _q[o + 1] = _t[1]; _p[o + 2] = _q[o + 2] = _t[2];
                continue;
            }
            double rx = _p[o] - _q[o] - mx, ry = _p[o + 1] - _q[o + 1] - my, rz = _p[o + 2] - _q[o + 2] - mz;
            double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            double keep = _keep * Math.Exp(-_swingDrag * r);
            rx *= keep; ry *= keep; rz *= keep; r *= keep;
            if (r > cap)
            {
                double k = cap / r;
                rx *= k; ry *= k; rz *= k;
            }
            _p[o] += cx; _p[o + 1] += cy; _p[o + 2] += cz;
            _q[o] = _p[o]; _q[o + 1] = _p[o + 1]; _q[o + 2] = _p[o + 2];
            _p[o] += mx + rx;
            _p[o + 1] += my + ry - _gravity;
            _p[o + 2] += mz + rz;
        }
        for (int b = 0; b < _boxes; b++)
        {
            int o = b * 15;
            Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o], _box[o + 1], _box[o + 2], _world, o);
            _world[o] += _sub[0]; _world[o + 1] += _sub[1]; _world[o + 2] += _sub[2];
            for (int j = 0; j < 3; j++)
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o + 3 + j * 3], _box[o + 4 + j * 3], _box[o + 5 + j * 3], _world, o + 3 + j * 3);
            _world[o + 12] = _box[o + 12]; _world[o + 13] = _box[o + 13]; _world[o + 14] = _box[o + 14];
        }
        for (int c = 0; c < _capsules * 7; c++) _capSub[c] = _capFrom[c] + (_cap[c] - _capFrom[c]) * _f;
        if (_bend > 0 || _restore > 0 || _cosLimit > -2) Bend(mx, my, mz);
        for (int it = 0; it < _iterations; it++)
        {
            for (int pass = 0; pass < _passes; pass++)
                for (int i = 0; i < _n; i++)
                {
                    if (_parent[i] < 0) continue;
                    Hold(_parent[i] * 3, i * 3, _len[i], _w[_parent[i]], _w[i], 1);
                }
            for (int k = 0; k < _sideLen.Length; k++)
                Hold(_side[k * 2] * 3, _side[k * 2 + 1] * 3, _sideLen[k], _w[_side[k * 2]], _w[_side[k * 2 + 1]], SideStiffness);
            if (_collide)
            {
                for (int b = 0; b < _boxes; b++) PushOutOfBox(b * 15);
                for (int c = 0; c < _capsules; c++) PushOutOfCapsule(c * 7);
            }
        }
        // Last, down from the roots: no piece longer than its length and a rigid one exactly its length. Like every correction this
        // becomes speed (the chain's pull); the length passes leave little to take out.
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int o = i * 3, a = _parent[i] * 3;
            double rest = _len[i];
            double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
            double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (len < 1e-9 || (len <= rest && !_rigid[i])) continue;
            double k = rest / len;
            double sx = _p[a] + ex * k - _p[o], sy = _p[a + 1] + ey * k - _p[o + 1], sz = _p[a + 2] + ez * k - _p[o + 2];
            _p[o] += sx; _p[o + 1] += sy; _p[o + 2] += sz;
        }
        // The body's capsules have the last word: a strand may stretch a little round a leg for a sub-step, never sink into it.
        if (_collide)
            for (int c = 0; c < _capsules; c++) PushOutOfCapsule(c * 7);
    }

    // The bend once per sub-step, top down: a damped spring toward the straight continuation of the piece above (the first piece toward
    // its bound direction carried by the anchor, a piece bound at an angle toward that angle turned with the piece above), the restore
    // spring toward the bound shape carried by the anchor (both shared between the node and the one above), then the angle limit
    // around the bend's target.
    void Bend(double mx, double my, double mz)
    {
        for (int i = 0; i < _n; i++)
        {
            int a = _parent[i];
            if (a < 0) continue;
            int o = i * 3, g = _parent[a];
            a *= 3;
            double tx, ty, tz;
            if (g < 0)
            {
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
                tx = _t[0]; ty = _t[1]; tz = _t[2];
            }
            else
            {
                g *= 3;
                tx = _p[a] - _p[g]; ty = _p[a + 1] - _p[g + 1]; tz = _p[a + 2] - _p[g + 2];
                double len = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (len < 1e-9) continue;
                tx /= len; ty /= len; tz /= len;
                if (!_straight[i]) TurnedLike(a, o, ref tx, ref ty, ref tz);
            }
            double rest = _len[i];
            double k = _bendK[i], d = _bendD[i];
            if (k > 0 || d > 0)
            {
                // The node's move this sub-step against the node above's (below a root, the root's move with gravity's).
                double ux = g < 0 ? _rm[a] : _p[a] - _q[a], uy = g < 0 ? _rm[a + 1] - _gravity : _p[a + 1] - _q[a + 1], uz = g < 0 ? _rm[a + 2] : _p[a + 2] - _q[a + 2];
                Share(a, o, (_p[a] + tx * rest - _p[o]) * k - (_p[o] - _q[o] - ux) * d,
                    (_p[a + 1] + ty * rest - _p[o + 1]) * k - (_p[o + 1] - _q[o + 1] - uy) * d,
                    (_p[a + 2] + tz * rest - _p[o + 2]) * k - (_p[o + 2] - _q[o + 2] - uz) * d);
            }
            if (_restore > 0 && g >= 0)
            {
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
                Share(a, o, (_p[a] + _t[0] * rest - _p[o]) * _restore, (_p[a + 1] + _t[1] * rest - _p[o + 1]) * _restore, (_p[a + 2] + _t[2] * rest - _p[o + 2]) * _restore);
            }
            if (_cosLimit > -2) Limit(a, o, tx, ty, tz);
        }
    }

    // Moves node o by (x, y, z) against node a, shared by their weights: o its share, a the rest the other way (a root none). The pull
    // between them is equal and opposite, as a joint's spring is; pulling the child alone kept feeding a swing against the colliders.
    void Share(int a, int o, double x, double y, double z)
    {
        double wo = _w[o / 3], wa = _w[a / 3], so = wo / (wo + wa), sa = wa / (wo + wa);
        _p[o] += x * so; _p[o + 1] += y * so; _p[o + 2] += z * so;
        _p[a] -= x * sa; _p[a + 1] -= y * sa; _p[a + 2] -= z * sa;
    }

    // The bound direction of node o turned by the shortest turn that takes its parent's bound direction onto the parent's current
    // one (t, unit, world) into t. Both bound directions are in the anchor's frame: taken into the world first.
    void TurnedLike(int a, int o, ref double tx, ref double ty, ref double tz)
    {
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[a], _dir[a + 1], _dir[a + 2], _t, 3);
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 6);
        double rx = _t[3], ry = _t[4], rz = _t[5], vx = _t[6], vy = _t[7], vz = _t[8];
        double c = rx * tx + ry * ty + rz * tz;
        if (c < -0.999999) return; // folded back: aim along the parent
        double kx = ry * tz - rz * ty, ky = rz * tx - rx * tz, kz = rx * ty - ry * tx;
        double kv = (kx * vx + ky * vy + kz * vz) / (1 + c);
        tx = vx * c + (ky * vz - kz * vy) + kx * kv;
        ty = vy * c + (kz * vx - kx * vz) + ky * kv;
        tz = vz * c + (kx * vy - ky * vx) + kz * kv;
    }

    // Keeps the piece a -> o within the angle limit around the unit direction t, at its current length.
    void Limit(int a, int o, double tx, double ty, double tz)
    {
        double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        if (len < 1e-9) return;
        double c = (ex * tx + ey * ty + ez * tz) / len;
        if (c >= _cosLimit) return;
        // Perpendicular part of the piece, unit; on the cone's edge in that plane.
        double px = ex / len - tx * c, py = ey / len - ty * c, pz = ez / len - tz * c;
        double pl = Math.Sqrt(px * px + py * py + pz * pz);
        if (pl < 1e-9) { px = ty; py = -tx; pz = 0; pl = Math.Sqrt(px * px + py * py); if (pl < 1e-9) { px = 1; pl = 1; } }
        px /= pl; py /= pl; pz /= pl;
        _p[o] = _p[a] + (tx * _cosLimit + px * _sinLimit) * len;
        _p[o + 1] = _p[a + 1] + (ty * _cosLimit + py * _sinLimit) * len;
        _p[o + 2] = _p[a + 2] + (tz * _cosLimit + pz * _sinLimit) * len;
    }

    // Moves nodes a and b (offsets) toward `rest` apart by their weights, `stiffness` of the way.
    void Hold(int a, int b, double rest, double wa, double wb, double stiffness)
    {
        double ex = _p[b] - _p[a], ey = _p[b + 1] - _p[a + 1], ez = _p[b + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        double sum = wa + wb;
        if (len < 1e-9 || sum <= 0) return;
        double diff = (len - rest) / (len * sum);
        if (stiffness != 1) diff *= stiffness;
        _p[a] += ex * diff * wa; _p[a + 1] += ey * diff * wa; _p[a + 2] += ez * diff * wa;
        _p[b] -= ex * diff * wb; _p[b + 1] -= ey * diff * wb; _p[b + 2] -= ez * diff * wb;
    }

    void PushOutOfBox(int o)
    {
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int v = i * 3;
            double r = _radius[i];
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

    // Out of the capsule at this sub-step: along the line from the nearest point on its axis.
    void PushOutOfCapsule(int o)
    {
        double ax = _capSub[o], ay = _capSub[o + 1], az = _capSub[o + 2];
        double sx = _capSub[o + 3] - ax, sy = _capSub[o + 4] - ay, sz = _capSub[o + 5] - az;
        double ss = sx * sx + sy * sy + sz * sz;
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int v = i * 3;
            double px = _p[v] - ax, py = _p[v + 1] - ay, pz = _p[v + 2] - az;
            double t = ss > 1e-18 ? Math.Clamp((px * sx + py * sy + pz * sz) / ss, 0, 1) : 0;
            double dx = px - sx * t, dy = py - sy * t, dz = pz - sz * t;
            double d2 = dx * dx + dy * dy + dz * dz, r = _capSub[o + 6] + _radius[i];
            if (d2 >= r * r || d2 < 1e-18) continue;
            double d = Math.Sqrt(d2), k = (r - d) / d;
            _p[v] += dx * k; _p[v + 1] += dy * k; _p[v + 2] += dz * k;
        }
    }

    // x = anchor now + d * (x - anchor before), for the node at `o` of `x`.
    void Carried(double[] x, int o, double dx, double dy, double dz, double dw)
    {
        Rotate(dx, dy, dz, dw, x[o] - _last[0], x[o + 1] - _last[1], x[o + 2] - _last[2], _t, 0);
        x[o] = _sub[0] + _t[0];
        x[o + 1] = _sub[1] + _t[1];
        x[o + 2] = _sub[2] + _t[2];
    }

    // ---- math ----

    /// <summary>Position and rotation (unit quaternion) of a matrix whose columns may carry scale.</summary>
    public static void Pose(double[] m, double[] pose)
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

    /// <summary>Rotates (vx, vy, vz) by the unit quaternion (x, y, z, w) into o[at..at+2].</summary>
    public static void Rotate(double x, double y, double z, double w, double vx, double vy, double vz, double[] o, int at)
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
