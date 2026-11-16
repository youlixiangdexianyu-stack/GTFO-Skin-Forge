using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// charm.simulate: the 3D view's charm, moved by the game's own code (Core CharmRope on ForgeCore's chain physics) along the hanging
// point's path the view sends, one frame per entry. The view plays the frames back; it computes no rope of its own.
public static class CharmPreview
{
    public sealed record Frame(double Time, double[] M, double[] Body);
    public sealed record Box(double[] Min, double[] Max);

    public const int MaxFrames = 1200; // 20 s at 60 per second

    public static object Simulate(Charm charm, double arm, double[]? gear, List<Box>? boxes, List<Frame> frames)
    {
        if (frames.Count is 0 or > MaxFrames) throw new BackendException("bad-request", $"frames: 1 to {MaxFrames}");
        if (frames.Any(f => f.M is not { Length: 16 } || f.Body is not { Length: 3 })) throw new BackendException("bad-request", "frames: m has 16 numbers, body 3");
        var rope = new CharmRope(charm, Math.Clamp(arm, 0.004, 0.1));
        if (charm.Collide && gear is { Length: 16 } && boxes != null)
            foreach (var b in boxes.Take(CharmRope.MaxBoxes)) rope.AddBox(frames[0].M, gear, b.Min, b.Max);
        int count = rope.VertexCount * 3;
        var v = new float[count];
        var n = new float[count];
        var pose = new double[7];
        var bytes = new byte[count * 4];
        var output = new List<object>(frames.Count);
        foreach (var f in frames)
        {
            rope.Update(f.Time, f.M, f.Body);
            rope.Pendant(pose);
            rope.Mesh(f.M, v, n);
            output.Add(new { pendant = pose.ToArray(), vertices = Base64(v, bytes), normals = Base64(n, bytes) });
        }
        return new
        {
            triangles = rope.Triangles(),
            metallic = CharmRope.Metallic(charm.Style),
            smoothness = CharmRope.Smoothness(charm.Style),
            frames = output,
        };
    }

    // Little-endian float32, as a Float32Array reads it.
    static string Base64(float[] values, byte[] bytes)
    {
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }
}
