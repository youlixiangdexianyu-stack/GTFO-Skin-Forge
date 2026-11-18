using System.Numerics;
using System.Text.Json;

namespace ForgeSkin.Format;

// Weapon inspect (PLAN §4.14): the motions are data, a table of authored keys and which gun plays which; this file reads the table,
// picks a gun's motions and solves and evaluates a motion for one gun in one pair of hands. Pure: view space (x right, y up, z forward
// from the item camera's eye), degrees in the data, metres. The plugin's Native/Inspect measures the gun, the camera and the arms when
// a look starts and lays the result over the game's pose each frame; nothing here knows a skin, and no gun is a branch in code.

// Where the off hand is at a hand key. Grip: the game's own grip on the gun. Away: lowered out of the view. The rest are on the gun,
// placed from its measured bounds: Under (its own grip slid along the gun), Side (the gun's left side), Top, Muzzle (under the front
// end), Mag (under the magazine node, or along the bottom when the gun has none).
public enum InspectHold : byte { Grip, Away, Under, Side, Top, Muzzle, Mag }

// A key of the gun. Rest: the game's own pose. Otherwise the intent: Turn (muzzle toward the view's right), Tilt (muzzle up), Roll (about
// the bore, top toward the right), in degrees from the gun held level and pointing ahead, and where the gun's bounds centre sits in the view (At, -1..1 of the
// half width and height) with the shown part (InspectLook.Corner) inside Fill of the half view. How far away the gun sits is solved, not authored.
// Spin and Flip free the gun from the grip hand: the hand stays where the key holds the gun while the gun turns about its bounds
// centre, Spin degrees about its side axis (a twirl on the trigger finger) and Flip degrees about its bore; Rise metres up the view
// then tosses it. Rise alone lifts the gun with both hands on it (a heft). They are interpolated as numbers, so a key can spin a whole
// turn or more.
public readonly record struct InspectGunKey(float Time, bool Rest, float Turn, float Tilt, float Roll, Vector2 At, float Fill, float Spin = 0f, float Flip = 0f, float Rise = 0f);

// A key of the off hand: what it holds and, on the gun, where along it (0 the back of the bounds, 1 the front).
public readonly record struct InspectHandKey(float Time, InspectHold On, float Along);

public sealed class InspectMotion
{
    public string Id { get; }
    public InspectGunKey[] Gun { get; }
    public InspectHandKey[] Hand { get; }
    readonly float[] _gunTimes, _handTimes;

    InspectMotion(string id, InspectGunKey[] gun, InspectHandKey[] hand)
    {
        Id = id;
        Gun = gun;
        Hand = hand;
        _gunTimes = gun.Select(k => k.Time).ToArray();
        _handTimes = hand.Select(k => k.Time).ToArray();
        Number = ForgeCore.Fnv.Hash32(id);
    }

    // How a motion is named between players and in a recording: FNV-1a 32 of its id.
    public uint Number { get; }

    public float Seconds => Gun[^1].Time;

    // An interruption puts the gun back this fast.
    public const float FadeOut = 0.15f;

    public const float DefaultFill = 0.95f;

    // The gun keys around `time` and the smooth step between them (a == b past the last key).
    public void GunSpan(float time, out int a, out int b, out float s) => Span(_gunTimes, time, out a, out b, out s);

    public void HandSpan(float time, out int a, out int b, out float s) => Span(_handTimes, time, out a, out b, out s);

    static void Span(float[] times, float time, out int a, out int b, out float s)
    {
        b = 1;
        while (b < times.Length - 1 && times[b] < time) b++;
        a = b - 1;
        if (times.Length == 1 || time >= times[^1]) { a = b = times.Length - 1; s = 0f; return; }
        var t = Math.Clamp((time - times[a]) / (times[b] - times[a]), 0f, 1f);
        s = t * t * (3f - 2f * t);
    }

    // The authored turn and roll at `time` (rest keys 0): what a body that cannot solve the hands (third person) shows of the motion.
    public void Intent(float time, out float turn, out float roll)
    {
        GunSpan(time, out var a, out var b, out var s);
        turn = Gun[a].Turn + (Gun[b].Turn - Gun[a].Turn) * s;
        roll = Gun[a].Roll + (Gun[b].Roll - Gun[a].Roll) * s;
    }

    internal static InspectMotion Read(string id, JsonElement motion)
    {
        var gun = new List<InspectGunKey>();
        foreach (var key in motion.GetProperty("gun").EnumerateArray())
        {
            float Angle(string name, float most = 180f)
            {
                var v = key.TryGetProperty(name, out var e) ? e.GetSingle() : 0f;
                return float.IsFinite(v) && Math.Abs(v) <= most ? v : throw Bad(id, $"{name} is a finite angle of at most {most} degrees");
            }
            var rise = key.TryGetProperty("rise", out var r) ? r.GetSingle() : 0f;
            if (!(rise >= 0f && rise <= 1f)) throw Bad(id, "rise is 0..1 metres");
            var rest = !key.EnumerateObject().Any(p => p.Name != "t");
            var at = Vector2.Zero;
            if (key.TryGetProperty("at", out var place))
            {
                var xy = place.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                at = xy.Length == 2 && xy.All(v => Math.Abs(v) <= 1f) ? new Vector2(xy[0], xy[1]) : throw Bad(id, "at is [x, y], each -1..1");
            }
            var fill = key.TryGetProperty("fill", out var f) ? f.GetSingle() : DefaultFill;
            if (!(fill > 0f && fill <= 1f) || Math.Abs(at.X) >= fill || Math.Abs(at.Y) >= fill) throw Bad(id, "fill is in (0, 1] and at lies inside it");
            gun.Add(new InspectGunKey(Time(id, key, gun.Count == 0 ? -1f : gun[^1].Time), rest, Angle("turn"), Angle("tilt"), Angle("roll"), at, fill, Angle("spin", 1080f), Angle("flip", 1080f), rise));
        }
        if (gun.Count < 2 || gun[0].Time != 0f || !gun[0].Rest || !gun[^1].Rest) throw Bad(id, "the gun has at least two keys, the first at 0, the first and last the game's pose ({ \"t\": … } alone)");
        var hand = new List<InspectHandKey>();
        if (motion.TryGetProperty("hand", out var hands))
            foreach (var key in hands.EnumerateArray())
            {
                var on = key.TryGetProperty("on", out var o) && Enum.TryParse<InspectHold>(o.GetString(), true, out var held) && o.GetString()!.All(char.IsLower)
                    ? held : throw Bad(id, "on is one of grip, away, under, side, top, muzzle, mag");
                // Without along, under is the off hand's own place on the gun (where the game puts it).
                var along = key.TryGetProperty("along", out var l) ? l.GetSingle() : on == InspectHold.Under ? float.NaN : 0.5f;
                if (!(along >= 0f && along <= 1f) && !float.IsNaN(along)) throw Bad(id, "along is 0..1");
                hand.Add(new InspectHandKey(Time(id, key, hand.Count == 0 ? -1f : hand[^1].Time), on, along));
            }
        else hand.Add(new InspectHandKey(0f, InspectHold.Grip, 0.5f));
        if (hand[0].Time != 0f || hand[0].On != InspectHold.Grip || hand[^1].On != InspectHold.Grip || hand[^1].Time > gun[^1].Time)
            throw Bad(id, "the hand's first key is at 0, its first and last keys are grip, and it ends with the gun");
        return new InspectMotion(id, gun.ToArray(), hand.ToArray());
    }

    static float Time(string id, JsonElement key, float after)
    {
        var t = key.GetProperty("t").GetSingle();
        return float.IsFinite(t) && t > after ? t : throw Bad(id, "key times rise from 0");
    }

    static FormatException Bad(string id, string what) => new($"inspect motion {id}: {what}");
}

// The motion table: every motion, the motions of each gun by its offline gear id (a list: repeated presses take the next one), and the
// fallback for a gun the table does not name, by the measured length of its bounds. A later table (a config file, later a pack) replaces
// motions and gun entries by id; its fallback list, when it has one, replaces the whole list.
public sealed class InspectTable
{
    readonly Dictionary<string, InspectMotion> _motions = new(StringComparer.Ordinal);
    readonly Dictionary<uint, string[]> _gear = new();
    (float UpTo, string[] Motions)[] _fallback = Array.Empty<(float, string[])>();

    public IEnumerable<InspectMotion> Motions => _motions.Values;

    // Reads `json` over what the table holds; throws FormatException naming the first fault, and then the table is unchanged.
    public void Add(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var motions = new Dictionary<string, InspectMotion>(_motions, StringComparer.Ordinal);
        if (root.TryGetProperty("motions", out var m))
            foreach (var p in m.EnumerateObject()) motions[p.Name] = InspectMotion.Read(p.Name, p.Value);
        string[] Refs(JsonElement list, string where)
        {
            var ids = list.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
            if (ids.Length == 0) throw new FormatException($"inspect {where}: no motion");
            foreach (var id in ids) if (!motions.ContainsKey(id)) throw new FormatException($"inspect {where}: no motion {id}");
            return ids;
        }
        var gear = new Dictionary<uint, string[]>(_gear);
        if (root.TryGetProperty("gear", out var g))
            foreach (var p in g.EnumerateObject())
                gear[uint.TryParse(p.Name, out var id) ? id : throw new FormatException($"inspect gear {p.Name}: not a gear id")] = Refs(p.Value, "gear " + p.Name);
        var fallback = _fallback;
        if (root.TryGetProperty("fallback", out var f))
        {
            fallback = f.EnumerateArray().Select((e, i) => (e.TryGetProperty("upTo", out var up) ? up.GetSingle() : float.PositiveInfinity, Refs(e.GetProperty("motions"), "fallback " + i))).ToArray();
            if (fallback.Length == 0 || !float.IsPositiveInfinity(fallback[^1].Item1)) throw new FormatException("inspect fallback: the last entry has no upTo");
        }
        foreach (var (_, ids) in gear) foreach (var id in ids) if (!motions.ContainsKey(id)) throw new FormatException($"inspect: no motion {id}");
        _motions.Clear();
        foreach (var (id, motion) in motions) _motions[id] = motion;
        _gear.Clear();
        foreach (var (id, ids) in gear) _gear[id] = ids;
        _fallback = fallback;
    }

    // The motions of gun `gear` (offline gear id, 0 when unknown) whose bounds are `length` metres along its barrel; `fallback` says the
    // table does not name it and the motions come from its length.
    public IReadOnlyList<InspectMotion> For(uint gear, float length, out bool fallback)
    {
        fallback = !_gear.TryGetValue(gear, out var ids);
        if (fallback) ids = _fallback.First(e => length <= e.UpTo).Motions;
        return ids!.Select(id => _motions[id]).ToArray();
    }
}

// One first-person arm as the game's two-bone IK moves it: the shoulder stays, the elbow bends in a plane through the shoulder and the
// hand (its rest plane swung about the shoulder-to-hand line by a swivel: positive raises the elbow outward, as a person lifts the
// forearm across the face), and the hand bone takes the target's rotation. Comfort keeps a hand rotation within what the wrist does from
// its rest pose relative to the forearm: a twist about the forearm (turning the palm) of at most Twist degrees and a bend of at most
// Swing degrees; a pose the game's IK would reach only by wringing the wrist is turned back to the nearest one within those limits.
public readonly struct InspectArm
{
    public const float Twist = 80f, Swing = 75f;
    // The swivel's human range in degrees from the rest plane: the elbow lifts out up to SwivelOut and tucks in up to SwivelIn.
    public const float SwivelOut = 90f, SwivelIn = 20f;

    public readonly Vector3 Shoulder;
    public readonly float Upper, Fore;
    readonly Vector3 _bend;
    readonly float _out; // the sign of a turn about the shoulder-to-hand line that raises the elbow
    readonly Quaternion _rest;

    // From the rest pose: the shoulder, elbow and wrist bones and the hand's IK target.
    public InspectArm(Vector3 shoulder, Vector3 elbow, Vector3 wrist, Vector3 target, Quaternion hand)
    {
        Shoulder = shoulder;
        Upper = Vector3.Distance(shoulder, elbow);
        Fore = Vector3.Distance(elbow, wrist);
        var bend = Vector3.Cross(elbow - shoulder, target - shoulder);
        _bend = bend.LengthSquared() > 1e-10f ? Vector3.Normalize(bend) : Vector3.UnitX;
        _out = 1f;
        _rest = Quaternion.Identity;
        _out = Elbow(target, 10f).Y >= Elbow(target).Y ? 1f : -1f;
        _rest = Quaternion.Inverse(Frame(target)) * hand;
    }

    public float Reach => (Upper + Fore) * 0.98f;

    // The point toward `hand` the arm reaches.
    public Vector3 Within(Vector3 hand)
    {
        var d = hand - Shoulder;
        var l = d.Length();
        return l <= Reach ? hand : Shoulder + d * (Reach / l);
    }

    // The bend plane's normal with the elbow swivelled `swivel` degrees.
    Vector3 Bend(Vector3 hand, float swivel)
    {
        if (swivel == 0f) return _bend;
        var d = hand - Shoulder;
        return d.LengthSquared() > 1e-10f ? Vector3.Transform(_bend, Quaternion.CreateFromAxisAngle(Vector3.Normalize(d), _out * swivel * MathF.PI / 180f)) : _bend;
    }

    public Vector3 Elbow(Vector3 hand, float swivel = 0f)
    {
        var d = hand - Shoulder;
        var l = Math.Clamp(d.Length(), Math.Abs(Upper - Fore) + 1e-4f, Upper + Fore - 1e-4f);
        var dn = Vector3.Normalize(d);
        var x = (Upper * Upper - Fore * Fore + l * l) / (2f * l);
        var h = MathF.Sqrt(MathF.Max(0f, Upper * Upper - x * x));
        var side = Vector3.Cross(dn, Bend(hand, swivel));
        return Shoulder + dn * x + (side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.Zero) * h;
    }

    // The forearm's frame with the hand at `hand`: z along the forearm, x the bend plane's normal.
    public Quaternion Frame(Vector3 hand, float swivel = 0f)
    {
        var bend = Bend(hand, swivel);
        var z = Vector3.Normalize(hand - Elbow(hand, swivel));
        var x = bend - z * Vector3.Dot(bend, z);
        x = x.LengthSquared() > 1e-10f ? Vector3.Normalize(x) : Vector3.Normalize(Vector3.Cross(z, Vector3.UnitY));
        return InspectLook.Look(z, Vector3.Cross(z, x));
    }

    public Quaternion Comfort(Vector3 hand, Quaternion turn, float swivel = 0f)
    {
        Split(hand, turn, swivel, out var frame, out var swing, out var twist);
        return frame * Limit(swing, Swing) * Limit(twist, Twist) * _rest;
    }

    // How far a hand rotation turns the wrist from its rest pose: the twist about the forearm (signed, -180..180) and the bend, in degrees.
    public (float Twist, float Swing) Wring(Vector3 hand, Quaternion turn, float swivel = 0f)
    {
        Split(hand, turn, swivel, out _, out var swing, out var twist);
        return (2f * MathF.Atan2(twist.Z, twist.W) * 180f / MathF.PI, 2f * MathF.Acos(Math.Min(1f, MathF.Abs(swing.W))) * 180f / MathF.PI);
    }

    // A hand rotation as the wrist's turn from its rest pose in the forearm's frame, and back.
    public Quaternion Wrist(Vector3 hand, Quaternion turn, float swivel = 0f) => Wrist(Frame(hand, swivel), turn);

    Quaternion Wrist(Quaternion frame, Quaternion turn)
    {
        var wrist = Quaternion.Normalize(Quaternion.Inverse(frame) * turn * Quaternion.Inverse(_rest));
        return wrist.W < 0f ? -wrist : wrist;
    }

    public Quaternion Hand(Vector3 hand, Quaternion wrist, float swivel = 0f) => Frame(hand, swivel) * wrist * _rest;

    void Split(Vector3 hand, Quaternion turn, float swivel, out Quaternion frame, out Quaternion swing, out Quaternion twist)
    {
        frame = Frame(hand, swivel);
        var wrist = Wrist(frame, turn);
        twist = new Quaternion(0f, 0f, wrist.Z, wrist.W);
        twist = twist.LengthSquared() > 1e-10f ? Quaternion.Normalize(twist) : Quaternion.Identity;
        swing = wrist * Quaternion.Conjugate(twist);
    }

    static Quaternion Limit(Quaternion q, float degrees)
    {
        if (q.W < 0f) q = -q;
        var angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        var max = degrees * MathF.PI / 180f;
        if (angle <= max) return q;
        var axis = new Vector3(q.X, q.Y, q.Z);
        return axis.LengthSquared() > 1e-12f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), max) : q;
    }
}

// What one look is solved from, measured when it starts, in view space: the item camera, the gun's drawn bounds (in the gun's own space),
// the game's pose of the gun and of both hands' IK targets, the two arms and the gun's magazine node (in the gun's space) if it has one.
public struct InspectRig
{
    public float TanHalf, Aspect, Near;
    public Vector3 Centre, Extents;
    public Vector3 GunAt;
    public Quaternion Gun;
    public Vector3 GripAt, OffAt;
    public Quaternion Grip, Off;
    public InspectArm GripArm, OffArm;
    public Vector3? Mag;
}

// What one frame shows of the gun: Inside, the share of its length (back to front of its bounds) that lies in the view (a stock near the
// eye draws large, so a share of the drawn area would count the stock and not the gun); Cover, the share of the view inside the outline
// of its bounds (about twice what the gun itself covers); Depth, how far in front of the eye the nearest corner of its shown part is
// (metres); Centre and Shown, how far out its bounds centre and the farthest corner of its shown part (receiver to muzzle,
// InspectLook.Corner) project (1 the view's edge).
public readonly record struct InspectShot(float Inside, float Cover, float Depth, float Centre, float Shown)
{
    // What a key aims for (InspectLook.Short): most of the gun in the view, not filling it, its shown part not so near the eye it cannot
    // be read, its centre and its shown part in the view (the stock behind the hand may be cut, or pass the eye as in the game's hold).
    public const float MinInside = 0.65f, MaxCover = 0.75f, MinDepth = 0.15f;
    // What no frame may do: leave most of the gun out of the view, or bring its shown part nearer than this (or than the game's own hold
    // has it: a compact gun's receiver sits under 0.1 m from the eye there).
    public const float LeastInside = 0.5f, LeastDepth = 0.12f;

    // The rule this frame breaks, or null. A frame on the way to or from the game's pose (`fromGame`) is held to the poorer of the game's
    // pose and each rule (a rifle's stock reaches behind the eye there), give or take 0.05 of a share and 0.02 m.
    public string? Breaks(in InspectShot game, bool fromGame)
    {
        float share = fromGame ? 0.05f : 1e-3f, metres = fromGame ? 0.02f : 1e-3f;
        if (Inside < (fromGame ? MathF.Min(LeastInside, game.Inside) : LeastInside) - share) return $"only {Inside:P0} of the gun is in the view";
        if (Depth < MathF.Min(LeastDepth, game.Depth) - metres) return $"the gun comes {Depth:0.00} m from the eye";
        return null;
    }
}

// One look solved for one gun in one pair of hands. Each gun key is turned into a pose of the gun in view space: the intent's turns are
// applied to the game's pose, and the grip hand (which stays on the gun) is placed, by a sweep of its arm's reach and its elbow's
// swivel, where its wrist holds that turn within its limits, the off hand reaches the places it touches about that key, the frame keeps
// the frame rules (InspectShot) with a margin, and the shown part of the gun (receiver to muzzle, Corner) needs the narrowest item
// camera view; that view is the key's (never narrower than the game's, at most WideDegrees). The first-person arms are short (about
// 0.6 m from the shoulder), so a rifle's front shows only in a somewhat wider item camera view: the gun and hands draw smaller, the
// world's camera is untouched. Every frame of the look is then sampled at 60 a second; a look whose frames break a rule has a Fault
// and is not played. The off hand's keys on the gun are points of the gun's bounds, so one motion fits every gun of a kind; between
// keys it moves on a smooth step and its wrist is kept within its limits the same way.
public sealed class InspectLook
{
    public const float WideDegrees = 75f;
    const float Wrist = 0.05f; // the wrist bone stands this far off the surface the palm touches
    static readonly Vector3 AwayFromShoulder = new(0.02f, -0.42f, 0.22f);

    public InspectMotion Motion { get; }
    // Per gun key, the item camera's tangent of half its vertical field of view (the game's at a rest key).
    public float[] View { get; }
    readonly Vector3[] _gunAt;
    readonly Quaternion[] _gun;
    readonly Quaternion[] _wrist; // per gun key, the grip hand's wrist turn in its forearm's frame (InspectArm.Wrist)
    readonly Vector3[] _handAt;
    readonly Quaternion[] _hand;
    // Per gun key, the grip arm's elbow swivel in degrees (0 at a rest key).
    public float[] Swivel { get; }
    readonly InspectArm _off, _arm;
    readonly Vector3 _gripAt; // the grip hand's target in the gun's space
    readonly Quaternion _grip;
    readonly Vector3 _centre; // the gun's bounds centre, which a free gun turns about
    readonly InspectRig _rig;

    public InspectLook(InspectMotion motion, in InspectRig rig)
    {
        Motion = motion;
        var keys = motion.Gun;
        View = new float[keys.Length];
        Swivel = new float[keys.Length];
        _gunAt = new Vector3[keys.Length];
        _gun = new Quaternion[keys.Length];
        _wrist = new Quaternion[keys.Length];
        var toGun = Quaternion.Inverse(rig.Gun);
        var gripAt = Vector3.Transform(rig.GripAt - rig.GunAt, toGun);
        var grip = toGun * rig.Grip;
        (_gripAt, _grip, _centre, _rig) = (gripAt, grip, rig.Centre, rig);
        (_off, _arm) = (rig.OffArm, rig.GripArm);
        var hands = motion.Hand;
        _handAt = new Vector3[hands.Length];
        _hand = new Quaternion[hands.Length];
        var offAt = Vector3.Transform(rig.OffAt - rig.GunAt, toGun);
        var off = toGun * rig.Off;
        for (var i = 0; i < hands.Length; i++) Anchor(hands[i], rig, offAt, off, out _handAt[i], out _hand[i]);
        var touches = new List<Vector3>();
        for (var i = 0; i < keys.Length; i++)
        {
            View[i] = rig.TanHalf;
            if (keys[i].Rest) continue;
            // The off hand's places on the gun at its keys nearest in time to this one and either side of this key's time.
            touches.Clear();
            motion.HandSpan(keys[i].Time, out var ha, out var hb, out _);
            for (var h = 0; h < hands.Length; h++)
                if (OnGun(hands[h].On) && (Nearest(keys, hands[h].Time) == i || h == ha || h == hb)) touches.Add(_handAt[h]);
            // A key next to one with another spin or flip turns the gun freely: every turn of it is weighed.
            var turning = (i > 0 && (keys[i - 1].Spin != keys[i].Spin || keys[i - 1].Flip != keys[i].Flip)) || (i < keys.Length - 1 && (keys[i + 1].Spin != keys[i].Spin || keys[i + 1].Flip != keys[i].Flip));
            Solve(keys[i], turning, rig, gripAt, grip, touches, out _gunAt[i], out _gun[i], out View[i], out Swivel[i]);
            _wrist[i] = _arm.Wrist(_gunAt[i] + Vector3.Transform(gripAt, _gun[i]), _gun[i] * grip, Swivel[i]);
        }
        // Between two keys of its own the gun's centre may bow off the straight line (forward, down, out) when the straight way breaks a
        // frame rule (a long gun turning across the view swings its stock at the eye): the least bow whose frames keep the rules.
        _game = Shot(rig, rig.GunAt, rig.Gun, rig.TanHalf, gripAt);
        _bow = new Vector3[keys.Length];
        for (var i = 0; i + 1 < keys.Length; i++)
        {
            if ((keys[i].Rest && keys[i + 1].Rest) || SpanBreaks(i, out _) < 0f) continue;
            var fewest = int.MaxValue;
            var chosen = Vector3.Zero;
            foreach (var bow in Bows)
            {
                _bow[i] = bow;
                var broken = SpanBreaks(i, out var count);
                if (broken < 0f) { chosen = bow; break; }
                if (count < fewest) (fewest, chosen) = (count, bow);
            }
            _bow[i] = chosen;
        }
        // Every frame of the motion at 60 a second, against the bar of how far it has moved from the game's pose.
        Fault = float.NaN;
        var last = default(Wrists);
        for (var f = 0; f * Step <= motion.Seconds && float.IsNaN(Fault); f++)
            if ((Breaks(f * Step) ?? Wrings(f * Step, f == 0, ref last)) is { } broken) (Fault, FaultRule) = (f * Step, broken);
    }

    // Both wrists as one frame leaves them: each hand's twist about its forearm (wound on from the first frame), its bend, and the grip
    // arm's elbow swivel, in degrees.
    struct Wrists { public float Grip, GripSwing, Swivel, Off, OffSwing; }

    // Most a wrist's twist or bend or the grip elbow's swivel may move in a 60th of a second, and how far past its limit a forearm may wind.
    // (An off hand dropping out of view bends its wrist about 10 degrees a frame; a limit flipping across the half turn jumps 160.)
    public const float WringStep = 15f, WoundPast = 5f;

    // The wrist rule this frame breaks after the last, or null: no joint jumps from one frame to the next (a hand rotation crossing the
    // wrist's half turn flips the way it is limited), and neither forearm winds further from its rest than the wrist's twist limit.
    string? Wrings(float time, bool first, ref Wrists last)
    {
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var at, out var turn, out _);
        GripPose(HeldAt, HeldTurn, out var hand, out var handTurn);
        var grip = _arm.Wring(hand, handTurn, HeldSwivel);
        HandPose(time, 1f, at, turn, _rig.OffAt, _rig.Off, out var offAt, out var offTurn);
        var off = _off.Wring(offAt, offTurn);
        static float Turned(float from, float to) => to - from - MathF.Round((to - from) / 360f) * 360f;
        var now = first
            ? new Wrists { Grip = grip.Twist, Off = off.Twist }
            : new Wrists { Grip = last.Grip + Turned(last.Grip, grip.Twist), Off = last.Off + Turned(last.Off, off.Twist) };
        (now.GripSwing, now.Swivel, now.OffSwing) = (grip.Swing, HeldSwivel, off.Swing);
        var was = first ? now : last;
        last = now;
        var step = MathF.Max(MathF.Max(MathF.Abs(now.Grip - was.Grip), MathF.Abs(now.Off - was.Off)),
            MathF.Max(MathF.Abs(now.Swivel - was.Swivel), MathF.Max(MathF.Abs(now.GripSwing - was.GripSwing), MathF.Abs(now.OffSwing - was.OffSwing))));
        if (step > WringStep) return $"a wrist or elbow turns {step:0} degrees in a 60th of a second";
        var wound = MathF.Max(MathF.Abs(now.Grip), MathF.Abs(now.Off));
        return wound > InspectArm.Twist + WoundPast ? $"a forearm winds {wound:0} degrees from its rest" : null;
    }

    readonly InspectShot _game;
    readonly Vector3[] _bow; // per gun key, how far the centre bows off the straight line to the next key at the middle of the way

    // The bows tried, least first: up to 0.25 m forward, 0.15 m down and 0.1 m to either side.
    static readonly Vector3[] Bows = (
        from x in new[] { 0f, -0.1f, 0.1f }
        from y in new[] { 0f, -0.05f, -0.1f, -0.15f }
        from z in new[] { 0f, 0.05f, 0.1f, 0.15f, 0.2f, 0.25f }
        select new Vector3(x, y, z)).OrderBy(v => v.Length()).ToArray();

    // The first sampled time in the way from gun key `key` to the next that breaks a rule (-1 when none) and how many do.
    float SpanBreaks(int key, out int count)
    {
        count = 0;
        var first = -1f;
        for (var time = Motion.Gun[key].Time; time <= Motion.Gun[key + 1].Time; time += Step)
            if (Breaks(time) != null) { count++; if (first < 0f) first = time; }
        return first;
    }

    // The frame rule the look breaks at `time`, or null.
    string? Breaks(float time)
    {
        var keys = Motion.Gun;
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var at, out var turn, out var view);
        Motion.GunSpan(time, out var a, out var b, out _);
        var broken = Shot(_rig, at, turn, view, _gripAt).Breaks(_game, keys[a].Rest || keys[b].Rest);
        if (broken != null) return broken;
        // Both hands stay within their arms' reach of the gun: the grip hand where it holds it, an off hand on the gun where it touches.
        var hand = HeldAt + Vector3.Transform(_gripAt, HeldTurn);
        if (!keys[a].Rest && !keys[b].Rest && Vector3.Distance(hand, _rig.GripArm.Shoulder) > _rig.GripArm.Reach + 0.02f)
            return "the grip hand cannot reach the gun";
        Motion.HandSpan(time, out var ha, out var hb, out var hs);
        if (OnGun(Motion.Hand[ha].On) && OnGun(Motion.Hand[hb].On)
            && Vector3.Distance(HeldAt + Vector3.Transform(Vector3.Lerp(_handAt[ha], _handAt[hb], hs), HeldTurn), _rig.OffArm.Shoulder) > _rig.OffArm.Reach + 0.02f)
            return "the off hand cannot reach the gun";
        return null;
    }
    const float Step = 1f / 60f;

    // The first time (sampled at 60 a second) a frame of the look breaks a frame rule (InspectShot), NaN when none does, and which rule.
    public float Fault { get; }
    public string? FaultRule { get; private set; }

    // What one frame shows of the gun with its bounds at `at`, `turn` in an item camera of tangent `view`.
    public static InspectShot Shot(in InspectRig rig, Vector3 at, Quaternion turn, float view, Vector3 gripAt)
    {
        float near = rig.Near, aspect = rig.Aspect;
        Vector2 Screen(Vector3 p) => new(p.X / (MathF.Max(p.Z, near) * view * aspect), p.Y / (MathF.Max(p.Z, near) * view));
        Span<Vector2> box = stackalloc Vector2[8];
        float depth = float.MaxValue, shown = 0f;
        Vector3 lo = rig.Centre - rig.Extents, hi = rig.Centre + rig.Extents;
        for (var i = 0; i < 8; i++)
        {
            var p = at + Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), turn);
            box[i] = Screen(p);
            var c = at + Vector3.Transform(Corner(rig, i, gripAt), turn);
            var q = Screen(c);
            depth = MathF.Min(depth, c.Z);
            shown = MathF.Max(shown, c.Z <= rig.Near ? float.MaxValue : MathF.Max(MathF.Abs(q.X), MathF.Abs(q.Y)));
        }
        // The share of the gun's length (its bounds' centre line, back to front) that is in the view.
        const int Along = 32;
        var inside = 0;
        for (var i = 0; i <= Along; i++)
        {
            var p = at + Vector3.Transform(rig.Centre + new Vector3(0f, 0f, rig.Extents.Z * (2f * i / Along - 1f)), turn);
            var q = Screen(p);
            if (p.Z > near && MathF.Abs(q.X) <= 1f && MathF.Abs(q.Y) <= 1f) inside++;
        }
        var centre = at + Vector3.Transform(rig.Centre, turn);
        var m = Screen(centre);
        return new InspectShot(inside / (Along + 1f), Area(Clip(Hull(box))) / 4f, depth, centre.Z <= near ? float.MaxValue : MathF.Max(MathF.Abs(m.X), MathF.Abs(m.Y)), shown);
    }

    // The convex hull of points, counter-clockwise (monotone chain).
    static List<Vector2> Hull(Span<Vector2> points)
    {
        var sorted = points.ToArray();
        Array.Sort(sorted, (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = hull.Count;
            for (var i = 0; i < sorted.Length; i++)
            {
                var p = sorted[pass == 0 ? i : sorted.Length - 1 - i];
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    // A convex polygon cut to the view (-1..1 on both axes).
    static List<Vector2> Clip(List<Vector2> poly)
    {
        for (var edge = 0; edge < 4 && poly.Count > 0; edge++)
        {
            var axis = edge & 1;
            var sign = edge < 2 ? 1f : -1f;
            float Out(Vector2 p) => sign * (axis == 0 ? p.X : p.Y) - 1f; // > 0 past this edge
            var cut = new List<Vector2>();
            for (var i = 0; i < poly.Count; i++)
            {
                Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
                float dp = Out(p), dq = Out(q);
                if (dp <= 0f) cut.Add(p);
                if ((dp <= 0f) != (dq <= 0f)) cut.Add(p + (q - p) * (dp / (dp - dq)));
            }
            poly = cut;
        }
        return poly;
    }

    static float Area(List<Vector2> poly)
    {
        var area = 0f;
        for (var i = 0; i < poly.Count; i++) area += poly[i].X * poly[(i + 1) % poly.Count].Y - poly[(i + 1) % poly.Count].X * poly[i].Y;
        return MathF.Abs(area) * 0.5f;
    }

    static bool OnGun(InspectHold on) => on is not (InspectHold.Grip or InspectHold.Away);

    static int Nearest(InspectGunKey[] keys, float time)
    {
        var best = -1;
        for (var i = 0; i < keys.Length; i++)
            if (!keys[i].Rest && (best < 0 || MathF.Abs(keys[i].Time - time) < MathF.Abs(keys[best].Time - time))) best = i;
        return best;
    }

    // The rotation that turns z to `forward` and y toward `up` (Unity's LookRotation).
    public static Quaternion Look(Vector3 forward, Vector3 up)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Normalize(Vector3.Cross(up, z));
        var y = Vector3.Cross(z, x);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f)));
    }

    // The gun's pose at `time` (`weight` 1: the motion's, 0: the game's), given the game's pose this frame, and the item camera's
    // tangent of half its field of view (`rest` the game's).
    public void GunPose(float time, float weight, Vector3 liveAt, Quaternion live, float rest, out Vector3 at, out Quaternion turn, out float view)
    {
        Motion.GunSpan(time, out var a, out var b, out var s);
        var ka = Motion.Gun[a].Rest;
        var kb = Motion.Gun[b].Rest;
        // The bounds centre moves on a straight line between keys (so it stays in the view between two keys that show it) while the gun
        // turns about it; to and from the game's pose the back of the gun does (so the stock leaves the eye and comes back without
        // swinging nearer).
        var pin = ka || kb ? _centre - new Vector3(0f, 0f, _rig.Extents.Z) : _gripAt;
        Vector3 Mid(Vector3 p, Quaternion q) => p + Vector3.Transform(pin, q);
        var liveMid = Mid(liveAt, live);
        var mid = Vector3.Lerp(ka ? liveMid : Mid(_gunAt[a], _gun[a]), kb ? liveMid : Mid(_gunAt[b], _gun[b]), s) + 4f * s * (1f - s) * _bow[a];
        turn = Quaternion.Slerp(ka ? live : _gun[a], kb ? live : _gun[b], s);
        // Between two keys of the motion's own the gun turns with the grip hand's wrist, which goes between the keys' wrist turns the
        // short way in its forearm's frame (the grip hand is at the centre line there): the short way in view space can wind the forearm
        // a whole turn round while the arm swings.
        var swivel = (ka ? 0f : Swivel[a]) + ((kb ? 0f : Swivel[b]) - (ka ? 0f : Swivel[a])) * s;
        if (!ka && !kb) turn = Quaternion.Normalize(_arm.Hand(mid, Quaternion.Slerp(_wrist[a], _wrist[b], s), swivel) * Quaternion.Inverse(_grip));
        view = (ka ? rest : View[a]) + ((kb ? rest : View[b]) - (ka ? rest : View[a])) * s;
        mid = Vector3.Lerp(liveMid, mid, weight);
        turn = Quaternion.Slerp(live, turn, weight);
        at = mid - Vector3.Transform(pin, turn);
        view = rest + (view - rest) * weight;
        HeldSwivel = swivel * weight;
        InspectGunKey ga = Motion.Gun[a], gb = Motion.Gun[b];
        // Rise alone lifts the gun in both hands (a heft); with a spin or flip it is a toss.
        var rise = (ga.Rise + (gb.Rise - ga.Rise) * s) * weight;
        (HeldAt, HeldTurn) = (at, turn);
        // A free gun turns about its bounds centre (a toss turns about the middle, a twirl about the trigger guard in front of the hand):
        // the hand stays at the held pose.
        // A game's-pose key spins to the whole turn nearest its neighbour, so a gun spun round is not wound back; an interruption fades the
        // spin from its nearest whole turn.
        static float Wind(bool rest, float value, float other) => rest ? MathF.Round(other / 360f) * 360f : value;
        static float Fade(float angle, float weight) => (angle - MathF.Round(angle / 360f) * 360f) * weight;
        var spin = Wind(ga.Rest, ga.Spin, gb.Spin) + (Wind(gb.Rest, gb.Spin, ga.Spin) - Wind(ga.Rest, ga.Spin, gb.Spin)) * s;
        var flip = Wind(ga.Rest, ga.Flip, gb.Flip) + (Wind(gb.Rest, gb.Flip, ga.Flip) - Wind(ga.Rest, ga.Flip, gb.Flip)) * s;
        if (weight < 1f) (spin, flip) = (Fade(spin, weight), Fade(flip, weight));
        Free = spin != 0f || flip != 0f;
        if (!Free) { at.Y += rise; HeldAt = at; }
        else
        {
            const float Degrees = MathF.PI / 180f;
            var pivot = at + Vector3.Transform(_centre, turn);
            turn = Quaternion.Normalize(turn * Quaternion.CreateFromAxisAngle(Vector3.UnitX, spin * Degrees) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, flip * Degrees));
            at = pivot + new Vector3(0f, rise, 0f) - Vector3.Transform(_centre, turn);
        }
        // Between two keys of the motion's own the item camera widens as far as this frame needs to keep the shown part in the view.
        if (!ka && !kb) view = MathF.Max(view, rest + (ViewFor(Whole, _rig, turn, at, _gripAt) - rest) * weight);
    }

    static readonly InspectGunKey Whole = default(InspectGunKey) with { Fill = 1f };

    // Set by GunPose: whether the gun is free of the grip hand this frame, the pose it is held in (where the grip hand stays) and the
    // grip arm's elbow swivel.
    public bool Free { get; private set; }
    public Vector3 HeldAt { get; private set; }
    public Quaternion HeldTurn { get; private set; }
    public float HeldSwivel { get; private set; }

    // Where the grip arm's elbow is this frame (set by GunPose): the point the game's IK bends the arm toward.
    public Vector3 GripElbow() => _arm.Elbow(HeldAt + Vector3.Transform(_gripAt, HeldTurn), HeldSwivel);

    // The grip hand's IK target for a gun held at `at`, `turn`.
    public void GripPose(Vector3 at, Quaternion turn, out Vector3 hand, out Quaternion handTurn)
    {
        hand = at + Vector3.Transform(_gripAt, turn);
        handTurn = turn * _grip;
    }

    // Whether the off hand leaves its grip at `time`.
    public bool HandOff(float time)
    {
        Motion.HandSpan(time, out var a, out var b, out _);
        return Motion.Hand[a].On != InspectHold.Grip || Motion.Hand[b].On != InspectHold.Grip;
    }

    // The off hand's target at `time`, given the gun's pose this frame and the game's target on its grip.
    public void HandPose(float time, float weight, Vector3 gunAt, Quaternion gun, Vector3 liveAt, Quaternion live, out Vector3 at, out Quaternion turn)
    {
        Motion.HandSpan(time, out var a, out var b, out var s);
        Place(a, gunAt, gun, liveAt, live, out var atA, out var turnA);
        Place(b, gunAt, gun, liveAt, live, out var atB, out var turnB);
        at = _off.Within(Vector3.Lerp(liveAt, Vector3.Lerp(atA, atB, s), weight));
        turn = _off.Comfort(at, Quaternion.Slerp(live, Quaternion.Slerp(turnA, turnB, s), weight));
    }

    void Place(int key, Vector3 gunAt, Quaternion gun, Vector3 liveAt, Quaternion live, out Vector3 at, out Quaternion turn)
    {
        switch (Motion.Hand[key].On)
        {
            case InspectHold.Grip: at = liveAt; turn = live; return;
            case InspectHold.Away: at = _handAt[key]; turn = _hand[key]; return;
            default: at = gunAt + Vector3.Transform(_handAt[key], gun); turn = gun * _hand[key]; return;
        }
    }

    void Anchor(in InspectHandKey key, in InspectRig rig, Vector3 offAt, Quaternion off, out Vector3 at, out Quaternion turn)
    {
        Vector3 c = rig.Centre, e = rig.Extents;
        float Along(float f) => c.Z - e.Z + f * 2f * e.Z;
        turn = off;
        switch (key.On)
        {
            case InspectHold.Away:
                at = rig.OffArm.Shoulder + AwayFromShoulder;
                turn = rig.Off;
                return;
            case InspectHold.Under: at = new Vector3(offAt.X, offAt.Y, float.IsNaN(key.Along) ? offAt.Z : Along(key.Along)); return;
            case InspectHold.Muzzle: at = new Vector3(offAt.X, offAt.Y, Along(0.97f)); return;
            case InspectHold.Mag: at = new Vector3(offAt.X, c.Y - e.Y - Wrist, rig.Mag?.Z ?? Along(key.Along)); return;
            case InspectHold.Side:
            case InspectHold.Top:
                // The grip's hand turned about the bore until it faces the side or the top.
                var top = key.On == InspectHold.Top;
                float rx = offAt.X - c.X, ry = offAt.Y - c.Y, dx = top ? 0f : -1f, dy = top ? 1f : 0f;
                turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(rx * dy - ry * dx, rx * dx + ry * dy)) * off;
                at = top ? new Vector3(c.X, c.Y + e.Y + Wrist, Along(key.Along)) : new Vector3(c.X - e.X - Wrist, c.Y, Along(key.Along));
                return;
            default: at = offAt; return;
        }
    }

    // How far the grip hand may move from where the game holds the gun, in metres.
    const float Move = 0.3f;

    // Where the grip hand stays: in the lower right of the view (at least HandRight of the half width right of the middle and at most
    // HandHigh of the half height above it, in the game's view, or as far as the game's own hold is), and at most Deeper metres farther
    // or Nearer metres nearer than the game holds it: a little up and forward to bring more of the gun in, never the middle of the view.
    const float HandRight = 0.2f, HandHigh = 0f, Deeper = 0.18f, Nearer = 0.08f;

    // Whether a grip hand at `hand` keeps its place, with `slack` (metres and shares) of give.
    static bool HandKept(in InspectRig rig, Vector3 hand, float slack)
    {
        float near = rig.Near, tan = rig.TanHalf, aspect = rig.Aspect;
        Vector2 Screen(Vector3 p) => new(p.X / (MathF.Max(p.Z, near) * tan * aspect), p.Y / (MathF.Max(p.Z, near) * tan));
        Vector2 at = Screen(hand), game = Screen(rig.GripAt);
        return at.X >= MathF.Min(HandRight, game.X) - slack && at.Y <= MathF.Max(HandHigh, game.Y) + slack
            && hand.Z <= rig.GripAt.Z + Deeper + slack && hand.Z >= rig.GripAt.Z - Nearer - slack;
    }

    // One gun key: the place of the grip hand and the swivel of its elbow that cost least (Cost), the turn kept within the wrist's limits
    // there and the view its frames need (Frames).
    static void Solve(in InspectGunKey key, bool turning, in InspectRig rig, Vector3 gripAt, Quaternion grip, List<Vector3> touches, out Vector3 at, out Quaternion turn, out float view, out float swivel)
    {
        const float Degrees = MathF.PI / 180f;
        // From the gun held level and pointing ahead (its own axes on the view's; the game's ready pose), not from the game's pose this
        // frame, which may be lowered or swaying.
        turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, key.Turn * Degrees) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -key.Tilt * Degrees)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -key.Roll * Degrees); // turning +x toward +y takes the top left
        var want = turn;
        var hold = turn * grip;
        var offset = Vector3.Transform(gripAt, turn);
        var arm = rig.GripArm;
        var best = float.MaxValue;
        var hand = rig.GripAt;
        swivel = 0f;
        // Where the hand holds the gun and how far the elbow swings out: within Move of where the game holds it, in front of the eye, in
        // the arm's reach and the swivel's range; a coarse sweep, then a fine one round the best.
        for (var pass = 0; pass < 2; pass++)
        {
            float step = pass == 0 ? 0.05f : 0.0125f, span = pass == 0 ? Move : 0.05f;
            float turnStep = pass == 0 ? 10f : 2.5f, from = pass == 0 ? -InspectArm.SwivelIn : MathF.Max(-InspectArm.SwivelIn, swivel - 5f);
            float to = pass == 0 ? InspectArm.SwivelOut : MathF.Min(InspectArm.SwivelOut, swivel + 5f);
            var around = pass == 0 ? rig.GripAt : hand;
            var n = (int)MathF.Round(2f * span / step) + 1; // places per axis
            var m = (int)MathF.Round((to - from) / turnStep) + 1; // swivels
            for (var i = 0; i < n * n * n * m; i++)
            {
                var c = i / m;
                var p = around + new Vector3(c % n, c / n % n, c / (n * n)) * step - new Vector3(span);
                if (Vector3.Distance(p, arm.Shoulder) > arm.Reach || Vector3.Distance(p, rig.GripAt) > Move || !HandKept(rig, p, 0f)) continue;
                var s = from + i % m * turnStep;
                var cost = Cost(key, rig, arm, p, s, p - offset, want, hold, gripAt, touches);
                // The frame rules (InspectShot) weigh above everything else; measured only for a place that could still win.
                if (cost < best) cost += 10f * Frames(key, turning, rig, want, p - offset, gripAt, Margin, out _);
                if (cost < best) (best, hand, swivel) = (cost, p, s);
            }
        }
        turn = Quaternion.Normalize(arm.Comfort(hand, hold, swivel) * Quaternion.Inverse(grip));
        at = hand - Vector3.Transform(gripAt, turn);
        Frames(key, turning, rig, turn, at, gripAt, 0f, out view);
    }

    // How far the frames a key shows fall short of the frame rules (0: they keep them) and the item camera view they need: the gun held
    // at `at`, `turn`, lifted by the key's rise, and when it is `turning` (spun or flipped about its bounds centre) at every 30 degrees of it.
    static float Frames(in InspectGunKey key, bool turning, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt, float margin, out float view)
    {
        at.Y += key.Rise;
        var pivot = at + Vector3.Transform(rig.Centre, turn);
        var frames = turning ? 24 : 1;
        Span<Quaternion> turns = stackalloc Quaternion[frames];
        for (var k = 0; k < frames; k++)
            turns[k] = Quaternion.Normalize(turn * Quaternion.CreateFromAxisAngle(k < 12 ? Vector3.UnitX : Vector3.UnitZ, k % 12 * MathF.PI / 6f));
        view = 0f;
        foreach (var q in turns) view = MathF.Max(view, ViewFor(key, rig, q, pivot - Vector3.Transform(rig.Centre, q), gripAt));
        var shortBy = 0f;
        foreach (var q in turns) shortBy = MathF.Max(shortBy, Short(Shot(rig, pivot - Vector3.Transform(rig.Centre, q), q, view, gripAt), margin));
        return shortBy;
    }

    // The item camera's view for a pose: what its shown part needs, never narrower than the game's, at most WideDegrees.
    static float ViewFor(in InspectGunKey key, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt) =>
        MathF.Min(MathF.Max(rig.TanHalf, Needed(key, rig, turn, at, gripAt)), MathF.Tan(WideDegrees * MathF.PI / 360f));

    // How far a frame falls short of the frame rules, by `margin` (a share of each bound) stricter, summed. A key is solved a margin
    // inside the rules so the frames between keys keep them.
    const float Margin = 0.1f;

    static float Short(in InspectShot shot, float margin) =>
        MathF.Max(0f, InspectShot.MinInside * (1f + margin) - shot.Inside) + MathF.Max(0f, shot.Cover - InspectShot.MaxCover * (1f - margin))
        + MathF.Max(0f, InspectShot.MinDepth * (1f + margin) - shot.Depth)
        + MathF.Min(10f, MathF.Max(0f, shot.Centre - 1f)) + MathF.Min(10f, MathF.Max(0f, shot.Shown - 1f));
    // How a place of the grip hand and a swivel of its elbow serve a key, lower better: the view the shown part of the gun needs (tangent
    // of half of it), three times as much for every bit of that past the game's own view (widening is the last resort), how far the hand
    // is from where the game holds it (an arm reaching across the view looks wrong), a little for every degree the elbow swings from rest,
    // the centre's distance from the key's point of the view, every metre a place the off hand touches at this key is out of its arm's
    // reach, and far more for every degree the wrist would have to bend past its limits to hold the turn there.
    static float Cost(in InspectGunKey key, in InspectRig rig, in InspectArm arm, Vector3 hand, float swivel, Vector3 at, Quaternion want, Quaternion hold, Vector3 gripAt, List<Vector3> touches)
    {
        var bend = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(arm.Comfort(hand, hold, swivel), hold)))) * 180f / MathF.PI;
        if (bend > 2f) return 100f + bend;
        var centre = at + Vector3.Transform(rig.Centre, want);
        if (centre.Z <= rig.Near) return 99f;
        var off = new Vector2(centre.X / (centre.Z * rig.TanHalf * rig.Aspect), centre.Y / (centre.Z * rig.TanHalf)) - key.At;
        var need = Needed(key, rig, want, at, gripAt);
        var cost = need + 3f * MathF.Max(0f, need - rig.TanHalf) + 1.5f * Vector3.Distance(hand, rig.GripAt) + 0.002f * MathF.Abs(swivel) + 0.05f * off.Length();
        foreach (var t in touches) cost += 3f * MathF.Max(0f, Vector3.Distance(at + Vector3.Transform(t, want), rig.OffArm.Shoulder) - rig.OffArm.Reach);
        return cost;
    }
    // A corner of the part of the gun's bounds that is shown: ahead of and above the grip hand (receiver to muzzle); the stock behind the
    // hand and the grip and magazine below it may leave the view.
    static Vector3 Corner(in InspectRig rig, int i, Vector3 gripAt)
    {
        Vector3 lo = rig.Centre - rig.Extents, hi = rig.Centre + rig.Extents;
        float back = Math.Clamp(gripAt.Z, lo.Z, hi.Z), low = Math.Clamp(gripAt.Y, lo.Y, hi.Y);
        return new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? low : hi.Y, (i & 4) == 0 ? back : hi.Z);
    }

    // The tangent of half the vertical view that shows every corner of the shown part inside the key's fill of the view.
    static float Needed(in InspectGunKey key, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt)
    {
        var need = 0f;
        for (var i = 0; i < 8; i++)
        {
            var p = at + Vector3.Transform(Corner(rig, i, gripAt), turn);
            if (p.Z < rig.Near + 0.02f) return 50f;
            need = MathF.Max(need, MathF.Max(MathF.Abs(p.X) / rig.Aspect, MathF.Abs(p.Y)) / (p.Z * key.Fill));
        }
        return need;
    }
}
