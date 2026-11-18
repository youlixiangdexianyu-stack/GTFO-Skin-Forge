using ForgeVisual;

namespace ForgeCore.Tests;

/// <summary>
/// The one chain physics (Visual/Core/ChainPhysics.cs), core algorithm. A charm's rope (the shape ForgeSkin builds: a pinned hanging point,
/// six pieces, a heavier pendant on a rigid arm, gear boxes one of which holds the hanging point) on a moving and turning anchor and a body on
/// part of its path, at uneven frame times with a stall that puts it at rest, then held still, gives the locked node trajectory (checkpoints
/// within 1e-9 m: the same numbers on every platform's maths library). It stays stable: no node faster than the speed cap allows relative to
/// the anchor, a damped rope held still hangs straight again, and carried at a steady walk it hangs straight (drag is the chain's own).
/// Spring bones: a strand bound bent holds its shape under gravity with restore and sags without; held still at any facing it comes to rest
/// in the same shape, and strands pressed on body capsules come to rest; the angle limit holds; nodes stay out of a capsule; skirt strands linked side by side stay near their rest spacing; a teleport puts the chain at rest instead of flinging it, and a
/// knock-back faster than the body limit does not fling it either.
/// </summary>
internal static class ChainChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const double arm = 0.012;
        var spec = ChainSpec.Charm();
        spec.Radius = 0.001;
        var rope = Charm(spec, 0.05, 6, arm);
        double[] gear = { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0.3, 1.1, -0.2, 1 }; // turned a quarter about Y
        var frames = Frames();
        rope.AddBox(frames[0].M, gear, new[] { -0.15, -0.02, -0.02 }, new[] { 0.15, 0.03, 0.02 });
        rope.AddBox(frames[0].M, gear, new[] { 0.02, -0.12, -0.015 }, new[] { 0.06, -0.03, 0.015 });
        var nodes = new List<double[]>();
        foreach (var f in frames) { rope.Update(f.Time, f.M, f.Body); nodes.Add((double[])rope.Nodes.Clone()); }
        check(rope.Boxes == 2, "chain: both gear boxes taken");
        // Checkpoints: the pendant's centre (last node) at frames 60, 119, 200, 239 and the last.
        double[][] locked =
        {
            new[] { 0.47283970548925475, 0.9669086940594145, -0.31538478894387517 },
            new[] { 0.04879133953938351, 1.1082786791534758, -0.4262145087462142 },
            new[] { 0.5014380803822883, 1.0799862647998957, 0.02317693064672529 },
            new[] { 0.15788418791135028, 0.9717082649556154, -0.3253499594768767 },
            new[] { 0.15823299383496756, 0.9718490734917401, -0.32488140428678114 },
        };
        int[] at = { 60, 119, 200, 239, frames.Count - 1 };
        for (int k = 0; k < at.Length; k++)
        {
            var p = nodes[at[k]];
            double d = Math.Max(Math.Abs(p[^3] - locked[k][0]), Math.Max(Math.Abs(p[^2] - locked[k][1]), Math.Abs(p[^1] - locked[k][2])));
            check(d < 1e-9, $"chain: charm trajectory at frame {at[k]} (pendant {p[^3]:R}, {p[^2]:R}, {p[^1]:R})");
        }
        check(nodes.Take(240).Max(x => Math.Abs(x[^3] - x[0])) > 1e-2, "chain: the pendant swings off the straight hang while moving");
        double fastest = 0;
        for (int f = 1; f < frames.Count; f++)
        {
            double dt = frames[f].Time - frames[f - 1].Time;
            if (dt > ChainState.Gap) continue;
            for (int i = 3; i < nodes[f].Length; i += 3)
                fastest = Math.Max(fastest, Math.Sqrt(Enumerable.Range(0, 3).Sum(j => Math.Pow(nodes[f][i + j] - nodes[f][j] - nodes[f - 1][i + j] + nodes[f - 1][j], 2))) / dt);
        }
        check(fastest <= 3 * ChainState.MaxSpeed, $"chain: no node faster than the cap allows relative to the anchor ({fastest:F2} m/s)");

        // Held still without the boxes, damped: back to hanging straight down within a millimetre.
        var free = Charm(new ChainSpec { Damping = 0.9, Weight = 2.5, Stiffness = 0, Radius = 0.001, Collide = false }, 0.05, 12, arm);
        foreach (var f in frames) free.Update(f.Time, f.M, f.Body);
        var n = free.Nodes;
        check(Math.Abs(n[^3] - n[0]) < 1e-3 && Math.Abs(n[^1] - n[2]) < 1e-3 && Math.Abs(n[1] - n[^2] - 0.05 - arm) < 1e-3, "chain: a damped rope held still hangs straight again");

        // Walking (3.5 m/s reached over a second, then two seconds at it), anchor and body together: hangs straight again.
        var walker = Charm(new ChainSpec { Collide = false }, 0.05, 6, arm);
        for (int i = 0; i <= 180; i++)
        {
            double t = i / 60.0, z = t < 1 ? 3.5 * (t * t * t - t * t * t * t / 2) : 3.5 * (t - 0.5);
            walker.Update(t, Moved(0, 1.1, z), new[] { 0, 1.6, z - 0.4 });
        }
        var w = walker.Nodes;
        check(Math.Abs(w[^1] - w[2]) < 2e-3 && Math.Abs(w[^3] - w[0]) < 2e-3, $"chain: walking does not drag the charm ({w[^1] - w[2]:F4} m behind)");

        // A strand bound sticking out sideways and bending down (a lock of hair): with restore it holds near that shape held still for
        // two seconds; without, it sags.
        double Sag(double restore)
        {
            var hair = ChainSpec.LongHair();
            hair.Restore = restore;
            hair.Collide = false;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.04, 1, -0.2 * i, 0);
            var chain = new ChainState(shape, hair);
            for (int i = 0; i <= 120; i++) chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            return 1.6 - chain.Nodes[^2];
        }
        double held = Sag(1), limp = Sag(0);
        check(held < 0.12 && limp > held + 0.05, $"chain: restore holds a bound shape ({held:F3} m drop) that sags without it ({limp:F3} m)");

        // A strand bound bent every which way, held still with the anchor turned to any facing: it comes to rest, in the same shape
        // (turned with it) as facing straight on.
        {
            double[] Settle(double yaw, out double moved)
            {
                var hair = ChainSpec.LongHair();
                hair.Collide = false;
                var shape = new ChainShape();
                int node = shape.AddRoot(0.05, 0, -0.1);
                for (int i = 0; i < 6; i++) node = shape.Add(node, 0.06, 0.4 - 0.15 * i, -1, -0.3 + 0.1 * i);
                var chain = new ChainState(shape, hair);
                double[] before = Array.Empty<double>();
                for (int i = 0; i <= 480; i++)
                {
                    chain.Update(i / 60.0, Turned(yaw, 0, 1.6, 0), new[] { 0, 1, 0.0 });
                    if (i == 420) before = (double[])chain.Nodes.Clone();
                }
                var p = chain.Nodes;
                moved = Largest(p, before);
                double c = Math.Cos(yaw), s = Math.Sin(yaw);
                var back = new double[p.Length]; // turned back to facing straight on
                for (int k = 0; k < p.Length; k += 3) { back[k] = c * p[k] - s * p[k + 2]; back[k + 1] = p[k + 1]; back[k + 2] = s * p[k] + c * p[k + 2]; }
                return back;
            }
            var straight = Settle(0, out double still);
            double off = 0;
            foreach (double yaw in new[] { Math.PI / 2, Math.PI, -Math.PI / 2, 0.7 })
            {
                off = Math.Max(off, Largest(Settle(yaw, out double moved), straight));
                still = Math.Max(still, moved);
            }
            check(still < 1e-5 && off < 1e-5, $"chain: held still at any facing it rests ({still * 1000:F3} mm in the last second) in the same shape ({off * 1000:F3} mm off)");
        }

        // Strands pressed on a shoulder and the head (light, holding their style), held still: they come to rest.
        {
            var hair = ChainSpec.LongHair();
            hair.Gravity = 0.05;
            var shape = new ChainShape();
            for (int k = 0; k < 3; k++)
            {
                int node = shape.AddRoot(-0.06 + 0.06 * k, 0, -0.1);
                for (int i = 0; i < 6; i++) node = shape.Add(node, 0.07, 0, -1, 0.1);
            }
            var chain = new ChainState(shape, hair) { Capsules = 2 };
            double[] before = Array.Empty<double>();
            for (int i = 0; i <= 480; i++)
            {
                chain.SetCapsule(0, -0.2, 1.3, -0.12, 0.2, 1.3, -0.12, 0.09);
                chain.SetCapsule(1, 0, 1.6, 0, 0, 1.6, 0, 0.1);
                chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
                if (i == 420) before = (double[])chain.Nodes.Clone();
            }
            double moved = Largest(chain.Nodes, before);
            check(moved < 1e-3, $"chain: strands pressed on the body come to rest ({moved * 1000:F2} mm in the last second)");
        }

        // Angle limit: a stiff-free strand bound straight out sideways may bend at most 30 degrees a joint.
        {
            var s = ChainSpec.ShortHair();
            s.Restore = 0; s.Stiffness = 0; s.AngleLimit = 30; s.Collide = false;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 4; i++) node = shape.Add(node, 0.05, 1, 0, 0);
            var chain = new ChainState(shape, s);
            for (int i = 0; i <= 120; i++) chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            var p = chain.Nodes;
            double worst = 0;
            for (int i = 1; i < 5; i++)
            {
                double ax = i == 1 ? 1 : p[(i - 1) * 3] - p[(i - 2) * 3], ay = i == 1 ? 0 : p[(i - 1) * 3 + 1] - p[(i - 2) * 3 + 1];
                double bx = p[i * 3] - p[(i - 1) * 3], by = p[i * 3 + 1] - p[(i - 1) * 3 + 1];
                worst = Math.Max(worst, Math.Acos(Math.Clamp((ax * bx + ay * by) / Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)), -1, 1)) * 180 / Math.PI);
            }
            check(worst < 31 && p[^2] < 1.6 - 0.05, $"chain: the angle limit holds ({worst:F1} degrees at most) while it still bends");
        }

        // A capsule (a thigh) under a hanging strand: no node ends inside it.
        {
            var s = ChainSpec.Skirt();
            s.Restore = 0;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.06, 0, -1, 0);
            var chain = new ChainState(shape, s) { Capsules = 1 };
            double inside = 0;
            for (int i = 0; i <= 120; i++)
            {
                chain.SetCapsule(0, 0.01, 1.4, -0.1, 0.01, 1.4, 0.1, 0.05);
                chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
                if (i == 0) continue; // the first update puts it at rest in its bound shape, straight through the leg
                var p = chain.Nodes;
                for (int k = 1; k < 6; k++)
                {
                    double dx = p[k * 3] - 0.01, dy = p[k * 3 + 1] - 1.4;
                    if (Math.Abs(p[k * 3 + 2]) <= 0.1) inside = Math.Max(inside, 0.05 + s.Radius - Math.Sqrt(dx * dx + dy * dy));
                }
            }
            check(inside < 0.005, $"chain: nodes stay out of a capsule ({inside * 1000:F1} mm in at most)");
        }

        // A skirt: eight strands round a hip, linked side by side in a loop, swung by a turn of the hips; neighbours stay near their spacing.
        {
            var shape = new ChainShape();
            var strands = new List<int[]>();
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4, cx = Math.Cos(a), cz = Math.Sin(a);
                var strand = new int[5];
                strand[0] = shape.AddRoot(cx * 0.15, 0, cz * 0.15);
                for (int j = 1; j < 5; j++) strand[j] = shape.Add(strand[j - 1], 0.08, cx * 0.3, -1, cz * 0.3);
                strands.Add(strand);
            }
            shape.LinkSide(strands, loop: true);
            var chain = new ChainState(shape, ChainSpec.Skirt());
            double worst = 0;
            for (int i = 0; i <= 120; i++)
            {
                double yaw = Math.Sin(i / 60.0 * 6) * 1.2;
                chain.Update(i / 60.0, Turned(yaw, 0, 1.0, 0), new[] { 0, 1, 0.0 });
                var p = chain.Nodes;
                for (int k = 0; k < 8; k++)
                {
                    int a = strands[k][4] * 3, b = strands[(k + 1) % 8][4] * 3;
                    double rest = 2 * Math.Sin(Math.PI / 8) * (0.15 + 4 * 0.08 * 0.3 / Math.Sqrt(1.09));
                    double d = Math.Sqrt(Math.Pow(p[b] - p[a], 2) + Math.Pow(p[b + 1] - p[a + 1], 2) + Math.Pow(p[b + 2] - p[a + 2], 2));
                    worst = Math.Max(worst, Math.Abs(d / rest - 1));
                }
            }
            check(worst < 0.35, $"chain: skirt neighbours stay near their spacing ({worst * 100:F0}% off at most)");
        }

        // A teleport (3 m in one frame) puts the chain at rest; a knock-back at 30 m/s does not fling it past the speed cap.
        {
            var chain = Charm(new ChainSpec { Collide = false }, 0.05, 6, arm);
            for (int i = 0; i <= 30; i++) chain.Update(i / 60.0, Moved(0, 1.1, i / 60.0), new[] { 0, 1.6, i / 60.0 - 0.4 });
            chain.Update(31 / 60.0, Moved(3, 1.1, 0.5), new[] { 3, 1.6, 0.1 });
            var p = chain.Nodes;
            check(Math.Abs(p[^3] - 3) < 1e-9 && Math.Abs(p[^1] - 0.5) < 1e-9, "chain: a teleport puts it at rest where it landed");
            double fast = 0;
            double[] before = (double[])p.Clone();
            for (int i = 32; i <= 50; i++)
            {
                double x = 3 + (i - 31) * 0.5; // 30 m/s
                chain.Update(i / 60.0, Moved(x, 1.1, 0.5), new[] { x, 1.6, 0.1 });
                for (int k = 3; k < p.Length; k += 3)
                    fast = Math.Max(fast, Math.Abs(p[k] - p[0] - before[k] + before[0]) * 60);
                Array.Copy(p, before, p.Length);
            }
            check(fast <= 3 * ChainState.MaxSpeed, $"chain: a knock-back does not fling it ({fast:F2} m/s against the anchor)");
        }
    }

    // ForgeSkin's charm shape: the hanging point, `segments` pieces straight down, the pendant heavier on a rigid arm.
    static ChainState Charm(ChainSpec spec, double length, int segments, double arm)
    {
        var shape = new ChainShape();
        int node = shape.AddRoot(0, 0, 0);
        for (int i = 0; i < segments; i++) node = shape.Add(node, length / segments, 0, -1, 0);
        shape.Add(node, arm, 0, -1, 0, 4, 0.5, arm * 0.8, rigid: true);
        return new ChainState(shape, spec);
    }

    // The largest distance between the same node in two node arrays.
    static double Largest(double[] a, double[] b)
    {
        double worst = 0;
        for (int k = 0; k < a.Length; k += 3)
            worst = Math.Max(worst, Math.Sqrt(Math.Pow(a[k] - b[k], 2) + Math.Pow(a[k + 1] - b[k + 1], 2) + Math.Pow(a[k + 2] - b[k + 2], 2)));
        return worst;
    }

    static double[] Moved(double x, double y, double z) => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1 };

    static double[] Turned(double yaw, double x, double y, double z)
    {
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        return new double[] { c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, x, y, z, 1 };
    }

    // The moving anchor: a Lissajous path turning (yaw and pitch, scaled 1.2) at uneven frame times with a 0.3 s stall at frame 120, the
    // body on part of its path; then held still and upright for 2.1 s.
    static List<(double Time, double[] M, double[] Body)> Frames()
    {
        var frames = new List<(double Time, double[] M, double[] Body)>();
        double time = 0;
        double[] dts = { 1 / 60.0, 1 / 144.0, 1 / 30.0, 0.011 };
        for (int i = 0; i < 240; i++)
        {
            time += i == 120 ? 0.3 : dts[i % dts.Length];
            double yaw = Math.Sin(time * 3.1) * 1.4, pitch = Math.Sin(time * 1.7) * 0.5, s = 1.2;
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            frames.Add((time, new[] { cy * s, 0, -sy * s, 0, sy * sp * s, cp * s, cy * sp * s, 0, sy * cp * s, -sp * s, cy * cp * s, 0,
                0.3 + Math.Sin(time * 2.3) * 0.25, 1.1 + Math.Sin(time * 4.1) * 0.08, -0.2 + Math.Cos(time * 1.9) * 0.3, 1 },
                new[] { 0.3 + Math.Sin(time * 2.3) * 0.25, 1.6, -0.5 + Math.Cos(time * 1.9) * 0.2 }));
        }
        var m = frames[^1].M;
        double[] still = { 1.2, 0, 0, 0, 0, 1.2, 0, 0, 0, 0, 1.2, 0, m[12], m[13], m[14], 1 };
        var body = frames[^1].Body;
        for (int i = 0; i < 120; i++) frames.Add((time += dts[i % dts.Length] * 1.2, still, body));
        return frames;
    }
}
