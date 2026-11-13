using System;
using System.Numerics;

namespace ForgeVisual;

/// <summary>
/// The canonical humanoid a joint clip is written for: 20 body joints and 30 finger joints (thumb to pinky, three each, left then right)
/// in parent-first order, the rest pose their turns are measured from and the standing hip height. These are the site's own rig constants
/// (<c>defaultHumanStructure</c> and <c>defaultHumanPose</c> of <c>Web/src/generated/rig/animation-reference.js</c>, the fingers from the
/// <c>rig</c> of <c>data/catalogs/player-actions.json</c>); the site converts every third-party action onto this rig, so a clip file only
/// holds each joint's turn against its parent (the file's nodes carry this rest pose and <see cref="JointClip.Read(byte[])"/> holds them to it).
/// </summary>
public static class ClipRig
{
    public const int Count = 50;
    /// <summary>The joints before the fingers: a clip may have only these.</summary>
    public const int BodyCount = 20;
    /// <summary>The hip's height when the canonical body stands at rest.</summary>
    public const float HipHeight = 1.04f;

    public static readonly string[] Names =
    {
        "hip", "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot", "spine0", "spine1", "spine2",
        "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand", "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand", "neck", "head",
        "leftThumb1", "leftThumb2", "leftThumb3", "leftIndex1", "leftIndex2", "leftIndex3", "leftMiddle1", "leftMiddle2", "leftMiddle3", "leftRing1", "leftRing2", "leftRing3", "leftPinky1", "leftPinky2", "leftPinky3",
        "rightThumb1", "rightThumb2", "rightThumb3", "rightIndex1", "rightIndex2", "rightIndex3", "rightMiddle1", "rightMiddle2", "rightMiddle3", "rightRing1", "rightRing2", "rightRing3", "rightPinky1", "rightPinky2", "rightPinky3",
    };

    /// <summary>Each joint's parent index (always lower than its own), -1 for the hip.</summary>
    public static readonly int[] Parent =
    {
        -1, 0, 1, 2, 0, 4, 5, 0, 7, 8, 9, 10, 11, 12, 9, 14, 15, 16, 9, 18,
        13, 20, 21, 13, 23, 24, 13, 26, 27, 13, 29, 30, 13, 32, 33, 17, 35, 36, 17, 38, 39, 17, 41, 42, 17, 44, 45, 17, 47, 48,
    };

    internal static readonly Quaternion[] Pose =
    {
        Quaternion.Identity,
        new(0f, 0f, 0.02617695f, 0.9996573f), new(0.04361939f, 0f, 0f, 0.9990482f), new(0.2756374f, 0f, 0f, 0.9612617f),
        new(0f, 0f, -0.02617695f, 0.9996573f), new(0.04361939f, 0f, 0f, 0.9990482f), new(0.2756374f, 0f, 0f, 0.9612617f),
        Quaternion.Identity, Quaternion.Identity, Quaternion.Identity,
        Quaternion.Identity, new(0f, 0f, -0.5446391f, 0.8386706f), new(0f, -0.199368f, 0f, 0.9799247f), new(0.004106232f, 0.07835157f, -0.05217462f, 0.9955511f),
        Quaternion.Identity, new(0f, 0f, 0.5446391f, 0.8386706f), new(0f, 0.199368f, 0f, 0.9799247f), new(0.07835157f, 0.004106232f, -0.9955511f, 0.05217462f),
        new(0.1736482f, 0f, 0f, 0.9848078f), new(-0.1736482f, 0f, 0f, 0.9848078f),
        new(0.4516838f, 0.2627186f, 0.4430338f, 0.7284791f), new(-0.06915969f, 0.1302082f, 0.009105047f, 0.9890298f), new(-0.1564345f, 0.0f, 0.0f, 0.9876884f),
        new(0.07993007f, 0.6635878f, 0.07648233f, 0.7398735f), new(0.04361939f, 0.0f, 0.0f, 0.9990482f), new(0.06975647f, 0.0f, 0.0f, 0.9975641f),
        new(0.08766547f, 0.6894805f, -0.08465756f, 0.7139779f), new(0.0348995f, 0.0f, 0.0f, 0.9993908f), new(0.0f, 0.0f, 0.0f, 1.0f),
        new(0.08901865f, 0.7250969f, -0.09546082f, 0.6761638f), new(0.08715574f, 0.0f, 0.0f, 0.9961947f), new(0.0f, 0.0f, 0.0f, 1.0f),
        new(-0.03088048f, 0.7690083f, -0.2114386f, 0.602467f), new(0.08715574f, 0.0f, 0.0f, 0.9961947f), new(0.0f, 0.0f, 0.0f, 1.0f),
        new(-0.2627186f, -0.4516838f, -0.7284791f, -0.4430338f), new(-0.06915969f, -0.1302082f, -0.009105047f, 0.9890298f), new(-0.1564345f, 0.0f, 0.0f, 0.9876884f),
        new(-0.6635879f, -0.07993009f, -0.7398736f, -0.0764823f), new(0.04361939f, 0.0f, 0.0f, 0.9990482f), new(0.06975647f, 0.0f, 0.0f, 0.9975641f),
        new(-0.6894805f, -0.08766547f, -0.7139779f, 0.08465756f), new(0.0348995f, 0.0f, 0.0f, 0.9993908f), new(0.0f, 0.0f, 0.0f, 1.0f),
        new(0.7250969f, 0.08901865f, 0.6761638f, -0.09546082f), new(0.08715574f, 0.0f, 0.0f, 0.9961947f), new(0.0f, 0.0f, 0.0f, 1.0f),
        new(0.7690083f, -0.03088048f, 0.602467f, -0.2114386f), new(0.08715574f, 0.0f, 0.0f, 0.9961947f), new(0.0f, 0.0f, 0.0f, 1.0f),
    };

    internal static readonly Vector3[] Offset =
    {
        new(0f, HipHeight, 0f),
        new(0.09999999f, 0f, 0f), new(0f, -0.49f, 0f), new(0f, -0.44f, 0f),
        new(-0.09999999f, 0f, 0f), new(0f, -0.49f, 0f), new(0f, -0.44f, 0f),
        new(0f, 0.11f, 0f), new(0f, 0.11f, 0f), new(0f, 0.11f, 0f),
        new(0.03f, 0.19f, 0f), new(0.18f, 0f, 0f), new(0.33f, 0f, 0f), new(0.29f, 0f, 0f),
        new(-0.03f, 0.19f, 0f), new(-0.18f, 0f, 0f), new(-0.33f, 0f, 0f), new(-0.29f, 0f, 0f),
        new(0f, 0.275f, 0.02499937f), new(0f, 0.1f, 0f),
        new(0.042f, -0.013f, 0.027f), new(-2.531308e-16f, 6.772361e-17f, 0.056f), new(-6.723788e-17f, -1.681988e-16f, 0.041f),
        new(0.1097946f, -0.01f, 0.04661564f), new(-7.549517e-17f, -1.512679e-16f, 0.06f), new(1.110223e-16f, -1.89293e-16f, 0.036f),
        new(0.12421f, -1.421085e-16f, 0.0170297f), new(-1.140754e-16f, -1.528573e-16f, 0.056f), new(-4.996003e-17f, -2.63678e-18f, 0.038f),
        new(0.1198904f, 0.0f, -0.01013904f), new(5.884182e-17f, -3.406997e-17f, 0.055f), new(-3.330669e-18f, 2.987194e-17f, 0.032f),
        new(0.1102943f, 0.0004419575f, -0.0334068f), new(1.873501e-17f, 1.022533e-16f, 0.044f), new(1.582068e-17f, -2.252105e-17f, 0.025f),
        new(0.042f, 0.013f, 0.027f), new(-3.308465e-16f, 8.326673e-17f, 0.056f), new(-8.049117e-17f, -1.64313e-16f, 0.041f),
        new(0.1097946f, 0.01f, 0.04661564f), new(7.105427e-17f, -1.421085e-16f, 0.06f), new(-2.220446e-17f, 1.609823e-17f, 0.036f),
        new(0.12421f, 2.842171e-16f, 0.0170297f), new(1.04361e-16f, -2.332856e-16f, 0.056f), new(2.442491e-17f, -5.310335e-16f, 0.038f),
        new(0.1198904f, -1.421085e-16f, -0.01013904f), new(1.421085e-16f, -2.131628e-16f, 0.055f), new(-3.552714e-17f, -4.973799e-16f, 0.032f),
        new(0.1102943f, -0.0004419575f, -0.0334068f), new(5.828671e-18f, -2.206568e-17f, 0.044f), new(1.100509e-16f, -1.351697e-16f, 0.025f),
    };

    /// <summary>Each joint's world rotation in the rest pose.</summary>
    internal static readonly Quaternion[] RestWorld = BuildRestWorld();

    /// <summary>The inverse of <see cref="RestWorld"/>.</summary>
    internal static readonly Quaternion[] RestWorldInverse = Array.ConvertAll(RestWorld, Quaternion.Inverse);

    private static readonly Vector3[] RestPosition = BuildRestPosition();

    private static Vector3[] BuildRestPosition()
    {
        var at = new Vector3[Count];
        for (var j = 0; j < Count; j++) at[j] = Parent[j] < 0 ? Offset[j] : at[Parent[j]] + Vector3.Transform(Offset[j], RestWorld[Parent[j]]);
        return at;
    }

    /// <summary>The canonical body's hip-to-ankle height at rest: a body's own leg length over this scales the hip's travel.</summary>
    public static readonly float LegHeight = HipHeight - (RestPosition[3].Y + RestPosition[6].Y) / 2f;

    /// <summary>Where joint <paramref name="joint"/> is in the rest pose, in the game's axes.</summary>
    internal static Vector3 RestPoint(int joint)
    {
        var at = RestPosition[joint];
        return new Vector3(-at.X, at.Y, at.Z);
    }

    /// <summary>The direction from joint <paramref name="from"/> to joint <paramref name="to"/> in the rest pose, in the game's axes
    /// (the unit vector a body's bone is lined up with before a clip's turn is laid over it).</summary>
    public static Vector3 RestDirection(int from, int to)
    {
        var d = Vector3.Normalize(RestPosition[to] - RestPosition[from]);
        return new Vector3(-d.X, d.Y, d.Z);
    }

    private static Quaternion[] BuildRestWorld()
    {
        var world = new Quaternion[Count];
        for (var j = 0; j < Count; j++) world[j] = Parent[j] < 0 ? Pose[j] : Quaternion.Normalize(world[Parent[j]] * Pose[j]);
        return world;
    }

    /// <summary>A rotation of the canonical (right-handed) frame as the same turn in the game's left-handed one: the X axis mirrored.</summary>
    internal static Quaternion Mirror(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);
}

/// <summary>
/// A third-party action converted onto the canonical humanoid (<see cref="ClipRig"/>): per frame the hip's place and every joint's turn
/// against its parent, at a fixed rate. <see cref="Evaluate"/> gives, for any moment, how far each joint has turned from the rest pose
/// in the game's world axes, which a mod lays over any humanoid body's own rest pose; the clip itself knows no body. A clip file is
/// a Forge animation file (<see cref="Read(byte[], string)"/>).
/// </summary>
public sealed partial class JointClip
{
    private readonly Quaternion[] _local;
    private readonly Vector3[] _hip;
    private readonly Quaternion[] _world = new Quaternion[ClipRig.Count];

    private JointClip(float rate, int joints, Quaternion[] local, Vector3[] hip)
    {
        Rate = rate;
        Joints = joints;
        _local = local;
        _hip = hip;
        Frames = hip.Length;
        Duration = (Frames - 1) * rate;
    }

    private static readonly System.Collections.Generic.Dictionary<string, JointClip> Named = new(StringComparer.Ordinal);

    /// <summary>The name a clip was read under (<see cref="Read(byte[], string)"/>), or <c>game/clipname</c> for a game clip captured by
    /// <c>JointPlayer.CaptureGameClip</c>; empty for a clip that has none. A clip with a name is what a recording can carry (ForgeCore <c>Recording</c>
    /// reports the name, not the bones); one without is played but not recorded.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The clip read under <paramref name="name"/> in this process, or null: how a replay finds the clip a recording named. A name is
    /// <c>ModId/clip</c>, so two mods do not take the same one.</summary>
    public static JointClip? Find(string name) => Named.TryGetValue(name, out var clip) ? clip : null;

    /// <summary>Seconds between two frames.</summary>
    public float Rate { get; }

    public int Frames { get; }

    /// <summary>How many of <see cref="ClipRig.Names"/> the clip drives: all of them, or only the body (<see cref="ClipRig.BodyCount"/>), and
    /// then the fingers keep the game's own pose.</summary>
    public int Joints { get; }

    public float Duration { get; }

    /// <summary>A clip from sampled poses: <paramref name="turns"/> holds, frame by frame, each of the first <paramref name="joints"/> joints
    /// turn in the shape <see cref="Evaluate"/> gives it, and <paramref name="hips"/> each frame's hip travel the same way.</summary>
    public static JointClip FromTurns(float rate, int joints, Quaternion[] turns, Vector3[] hips)
    {
        if (!(rate > 0f) || hips.Length < 2 || turns.Length != hips.Length * joints) throw new ArgumentException("the sampled poses do not fill the clip");
        var local = new Quaternion[turns.Length];
        var world = new Quaternion[joints];
        var hip = new Vector3[hips.Length];
        for (var frame = 0; frame < hips.Length; frame++)
        {
            for (var j = 0; j < joints; j++)
            {
                world[j] = Quaternion.Normalize(ClipRig.Mirror(turns[frame * joints + j]) * ClipRig.RestWorld[j]);
                var parent = ClipRig.Parent[j];
                local[frame * joints + j] = parent < 0 ? world[j] : Quaternion.Normalize(Quaternion.Inverse(world[parent]) * world[j]);
            }
            hip[frame] = new Vector3(-hips[frame].X, hips[frame].Y + ClipRig.HipHeight, hips[frame].Z);
        }
        return new JointClip(rate, joints, local, hip);
    }

    /// <summary>Reads a clip file (<see cref="Read(byte[])"/>) and names the clip <paramref name="name"/>.</summary>
    public static JointClip Read(byte[] file, string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A clip name is not empty.", nameof(name));
        var clip = Read(file);
        clip.Name = name;
        Named[name] = clip;
        return clip;
    }

    /// <summary>Seats the clip on the floor, in two steps. First, a clip whose every frame is off the floor by more than a hand's width (a lying body that kept a
    /// standing hip height, so it hovers) is lowered as a whole by one amount, so a jump or flip inside it keeps its height. Then each frame whose lowest body joint is
    /// below an ankle's height (a kneeling or crawling body with the hip too low, sinking through the floor) has its hip raised by exactly that frame's shortfall;
    /// every other frame stays where it is, so standing frames touch the floor and a flip's apex is untouched. The lowest joint changes smoothly from frame to frame,
    /// so the hip does too. Returns the largest move made, in the canonical body's metres (positive lowered, negative raised; 0 when nothing moved). Call it once, after reading.</summary>
    public float SeatOnFloor()
    {
        var world = new Quaternion[ClipRig.BodyCount];
        var at = new Vector3[ClipRig.BodyCount];
        var ankle = ClipRig.HipHeight - ClipRig.LegHeight;
        var lowest = new float[Frames];
        var floor = float.MaxValue;
        for (var frame = 0; frame < Frames; frame++)
        {
            lowest[frame] = float.MaxValue;
            for (var j = 0; j < ClipRig.BodyCount; j++)
            {
                var parent = ClipRig.Parent[j];
                var local = _local[frame * Joints + j];
                world[j] = parent < 0 ? local : Quaternion.Normalize(world[parent] * local);
                at[j] = parent < 0 ? _hip[frame] : at[parent] + Vector3.Transform(ClipRig.Offset[j], world[parent]);
                lowest[frame] = Math.Min(lowest[frame], at[j].Y);
            }
            floor = Math.Min(floor, lowest[frame]);
        }
        var moved = 0f;
        if (floor - ankle > SeatTolerance)
        {
            moved = floor - ankle;
            for (var frame = 0; frame < Frames; frame++) { _hip[frame].Y -= moved; lowest[frame] -= moved; }
        }
        var raised = 0f;
        for (var frame = 0; frame < Frames; frame++)
        {
            var shortfall = ankle - lowest[frame];
            if (shortfall <= SinkTolerance) continue;
            _hip[frame].Y += shortfall;
            raised = Math.Max(raised, shortfall);
        }
        return moved != 0f ? moved : -raised;
    }

    private const float SinkTolerance = 0.01f;

    private const float SeatTolerance = 0.1f;

    /// <summary>The clip at <paramref name="time"/> seconds (wrapped when <paramref name="loop"/>, else held at its ends):
    /// <paramref name="turns"/>[joint] is how far that joint has turned from the canonical rest pose, in the game's world axes (so a body
    /// lays it over its own rest rotation), and <paramref name="hip"/> is the hip's travel from its standing place, in the canonical
    /// body's metres and the game's axes. Fills the caller's array up to <see cref="Joints"/>; allocates nothing.</summary>
    public void Evaluate(float time, bool loop, Quaternion[] turns, out Vector3 hip)
    {
        if (turns.Length < ClipRig.Count) throw new ArgumentException("turns holds fewer than " + ClipRig.Count + " joints", nameof(turns));
        if (loop && Duration > 0f) { time %= Duration; if (time < 0f) time += Duration; }
        var position = Math.Clamp(time, 0f, Duration) / Rate;
        var a = Math.Min((int)position, Frames - 2);
        var t = position - a;
        for (var j = 0; j < Joints; j++)
        {
            var turn = Quaternion.Slerp(_local[a * Joints + j], _local[(a + 1) * Joints + j], t);
            var parent = ClipRig.Parent[j];
            _world[j] = parent < 0 ? turn : Quaternion.Normalize(_world[parent] * turn);
            turns[j] = ClipRig.Mirror(Quaternion.Normalize(_world[j] * ClipRig.RestWorldInverse[j]));
        }
        var at = Vector3.Lerp(_hip[a], _hip[a + 1], t);
        hip = new Vector3(-at.X, at.Y - ClipRig.HipHeight, at.Z);
    }
}
