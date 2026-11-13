using System.Diagnostics;
using System.Numerics;
using System.Text;
using ForgeVisual;

namespace ForgeCore.Tests;

/// <summary>
/// Resource integrity of the one action format: a file the site's writer made (<c>fixtures/animation-clip.glb</c>, from
/// <c>encodeAnimationGlb</c> in <c>Web/src/forge/animation-glb.ts</c>: 50 joints, 3 keyframes at 30 per second, the hip moving 1 cm a
/// frame, the left upper arm turning 10 degrees a frame) reads back with its joints and length, and deliberately broken copies are refused.
/// With <c>--animations &lt;folder&gt;</c> it also reads every <c>.glb</c> under that folder (local files, not in Git) and prints the load time and memory.
/// </summary>
internal static class AnimationChecks
{
    public static void Run(Action<bool, string> check, string[] args)
    {
        var good = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "animation-clip.glb"));
        var clip = JointClip.Read(good, "test/fixture");
        check(clip.Joints == ClipRig.Count && clip.Frames == 3 && Math.Abs(clip.Rate - 1f / 30f) < 1e-6f && Math.Abs(clip.Duration - 2f / 30f) < 1e-6f, "animation: joints, keyframes and length");
        check(JointClip.Find("test/fixture") == clip, "animation: a named clip is found by its name");
        var turns = new Quaternion[ClipRig.Count];
        clip.Evaluate(0f, false, turns, out var hip0);
        var rest = Angle(turns[11]);
        clip.Evaluate(clip.Duration, false, turns, out var hip2);
        check(rest < 0.01f && Math.Abs(Angle(turns[11]) - 20f) < 0.05f && Math.Abs(Angle(turns[12]) - 20f) < 0.05f && Angle(turns[15]) < 0.01f, "animation: the left arm turns 20 degrees with its upper arm, the right arm stays at rest");
        check(Math.Abs(hip2.X - hip0.X + 0.02f) < 1e-5f && Math.Abs(hip0.Y) < 1e-5f, "animation: the hip travels 2 cm (mirrored into the game's axes)");

        string json = Encoding.UTF8.GetString(good, 20, (int)BitConverter.ToUInt32(good, 12));
        var broken = new (string Label, byte[] File)[]
        {
            ("truncated", good[..^8]),
            ("not glTF", Patch(good, 0, 0x00)),
            ("a joint renamed", WithJson(good, json.Replace("\"name\":\"head\"", "\"name\":\"heaD\""))),
            ("an extension", WithJson(good, json.Replace("\"asset\":{", "\"extensionsUsed\":[\"KHR_x\"],\"asset\":{"))),
            ("enemy nodes", WithJson(good, json.Replace("\"generator\":\"GTFO-Forge\"", "\"generator\":\"GTFO-Forge\",\"extras\":{\"forge\":{\"actor\":\"snatcher\"}}"))),
            ("a moved rest", WithJson(good, json.Replace("\"translation\":[0,1.04,0]", "\"translation\":[0,1.2,0]"))),
            ("step interpolation", WithJson(good, json.Replace("{\"input\":0,", "{\"interpolation\":\"STEP\",\"input\":0,"))),
            ("a broken quaternion", BreakFirstTurn(good)),
        };
        foreach (var (label, file) in broken)
        {
            var refused = false;
            try { JointClip.Read(file); }
            catch (FormatException) { refused = true; }
            check(refused, "animation: refuses " + label);
        }

        var at = Array.IndexOf(args, "--animations");
        if (at >= 0 && at + 1 < args.Length) Measure(args[at + 1], check);
    }

    private static float Angle(Quaternion q) => 2f * MathF.Acos(Math.Min(1f, Math.Abs(q.W))) * 180f / MathF.PI;

    private static byte[] Patch(byte[] file, int at, byte value)
    {
        var copy = (byte[])file.Clone();
        copy[at] = value;
        return copy;
    }

    /// <summary>The file with its JSON chunk replaced (padded with spaces) and the lengths rewritten.</summary>
    private static byte[] WithJson(byte[] file, string json)
    {
        var oldLength = (int)BitConverter.ToUInt32(file, 12);
        var text = Encoding.UTF8.GetBytes(json);
        var length = (text.Length + 3) & ~3;
        var bin = file.AsSpan(20 + oldLength);
        var copy = new byte[20 + length + bin.Length];
        file.AsSpan(0, 20).CopyTo(copy);
        copy.AsSpan(20, length).Fill(0x20);
        text.CopyTo(copy, 20);
        bin.CopyTo(copy.AsSpan(20 + length));
        BitConverter.GetBytes((uint)copy.Length).CopyTo(copy, 8);
        BitConverter.GetBytes((uint)length).CopyTo(copy, 12);
        return copy;
    }

    /// <summary>The first output float after the time accessor (the hip's x travel is first, then the turns) doubled across a turn: the hip's turn
    /// written after the hip's place is the first rotation, so its w is set to 2.</summary>
    private static byte[] BreakFirstTurn(byte[] file)
    {
        var jsonLength = (int)BitConverter.ToUInt32(file, 12);
        var bin = 28 + jsonLength;
        // bufferView 0: three times; bufferView 1: the hip's 3x3 place, then the hip's 3x4 turn.
        var w = bin + 3 * 4 + 9 * 4 + 3 * 4;
        var copy = (byte[])file.Clone();
        BitConverter.GetBytes(2f).CopyTo(copy, w);
        return copy;
    }

    /// <summary>Reads every action file under <paramref name="folder"/> with the game's reader, on this machine, and prints what it took.</summary>
    private static void Measure(string folder, Action<bool, string> check)
    {
        var files = Directory.GetFiles(folder, "*.glb", SearchOption.AllDirectories);
        var all = files.Select(File.ReadAllBytes).ToArray();
        long bytes = all.Sum(file => (long)file.Length);
        foreach (var file in all) JointClip.Read(file); // warm the JIT and the JSON reader once
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var clips = new List<JointClip>();
        var refused = new List<string>();
        for (var i = 0; i < all.Length; i++)
        {
            try { clips.Add(JointClip.Read(all[i])); }
            catch (FormatException problem) { refused.Add(Path.GetFileName(files[i]) + ": " + problem.Message); }
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // What a clip keeps: a turn (16 bytes) per joint and keyframe, the hip (12 bytes) per keyframe, its 50-turn scratch row.
        long held = clips.Sum(clip => (long)clip.Frames * clip.Joints * 16 + clip.Frames * 12 + ClipRig.Count * 16);
        var turns = new Quaternion[ClipRig.Count];
        var evaluated = GC.GetAllocatedBytesForCurrentThread();
        foreach (var clip in clips) for (var t = 0f; t < clip.Duration; t += 1f / 60f) clip.Evaluate(t, true, turns, out _);
        evaluated = GC.GetAllocatedBytesForCurrentThread() - evaluated;
        Console.WriteLine("animations: " + files.Length + " files, " + bytes + " bytes, read in " + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, allocated "
            + allocated + " bytes while reading, " + held + " bytes held by the clips, " + evaluated + " bytes allocated while playing every clip at 60 per second");
        foreach (var line in refused) Console.WriteLine("animations: refused " + line);
        check(refused.Count == 0 && evaluated == 0, "animation: every local action file reads and plays without allocating");
        GC.KeepAlive(clips);
    }
}
