using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

namespace ForgeVisual;

/// <summary>The one rule every gun model is held by (<c>Data/gun-hold.json</c>, embedded): its size and the game's length range, where each palm sits on
/// the game's own hand, the first-person hold and the arm's reach. No package, gun or model has numbers of its own.</summary>
public sealed class GunHoldRules
{
    /// <summary>The size every model is shown at against its authored size.</summary>
    public float ShownScale { get; private init; }
    /// <summary>The game's own guns' shown length range along the bore (metres); a model outside it is its author's error, never resized.</summary>
    public float LengthMin { get; private init; }
    public float LengthMax { get; private init; }
    /// <summary>Where the palm touches what it holds, in the frame of the game's own hand node (the wrist; read in game): a bare grip point of a model
    /// is that touch point, so the wrist goes there less this, turned by the hand's rotation.</summary>
    public Vector3 RightPalm { get; private init; }
    public Vector3 LeftPalm { get; private init; }
    /// <summary>Added to the replaced gun's hip and relaxed first-person position.</summary>
    public Vector3 Hold { get; private init; }
    /// <summary>The item camera's hip field of view the hold moves toward, and how far (0 keeps the game gun's own, 1 is this value).</summary>
    public int HipItemFov { get; private init; }
    public float HipItemFovWeight { get; private init; }
    /// <summary>Shoulder to hand target over this at the hip: the mount pulls the left hand back along the gun, and reports the right (the model's anchor).</summary>
    public float MaxReach { get; private init; }
    /// <summary>Shoulder to hand target over this is a hint (the arm is nearly straight).</summary>
    public float NearReach { get; private init; }
    /// <summary>The world camera's aim field of view through a 1x sight (the game's own open sights); a ForgeVisual sight's magnification divides its half-angle tangent.</summary>
    public int OpenSightLookFov { get; private init; }
    /// <summary>The game's own holds by preset id (<c>presets</c>): what a grip table's <c>preset</c> names.</summary>
    public IReadOnlyDictionary<string, GunHoldPreset> Presets { get; private init; } = new Dictionary<string, GunHoldPreset>();

    /// <summary>The preset of the gun whose left hand takes <paramref name="leftAnim"/> (one preset per left grip animation), or null.</summary>
    public GunHoldPreset? PresetForHand(string leftAnim)
    {
        foreach (var preset in Presets.Values) if (preset.LeftAnim == leftAnim) return preset;
        return null;
    }

    /// <summary>Reads the rule file; throws <see cref="FormatException"/> naming what is wrong.</summary>
    public static GunHoldRules Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-gun-hold" || !ForgeCore.FormatVersion.Matches(root))
            throw new FormatException("Not a forge-gun-hold file of schema 1.");
        var rules = new GunHoldRules
        {
            ShownScale = root.GetProperty("length").GetProperty("shownScale").GetSingle(),
            LengthMin = root.GetProperty("length").GetProperty("min").GetSingle(),
            LengthMax = root.GetProperty("length").GetProperty("max").GetSingle(),
            RightPalm = Vector(root.GetProperty("palm").GetProperty("right")),
            LeftPalm = Vector(root.GetProperty("palm").GetProperty("left")),
            Hold = Vector(root.GetProperty("hold").GetProperty("offset")),
            HipItemFov = root.GetProperty("hold").GetProperty("hipItemFov").GetInt32(),
            HipItemFovWeight = root.GetProperty("hold").GetProperty("hipItemFovWeight").GetSingle(),
            MaxReach = root.GetProperty("reach").GetProperty("max").GetSingle(),
            NearReach = root.GetProperty("reach").GetProperty("near").GetSingle(),
            OpenSightLookFov = root.GetProperty("sight").GetProperty("openLookFov").GetInt32(),
            Presets = ParsePresets(root.GetProperty("presets")),
        };
        if (rules.OpenSightLookFov is < 10 or > 120) throw new FormatException("gun-hold sight openLookFov must be 10-120 degrees.");
        if (!(rules.LengthMin > 0f) || !(rules.LengthMax >= rules.LengthMin)) throw new FormatException("gun-hold length needs 0 < min <= max.");
        if (!(rules.ShownScale > 0f) || !float.IsFinite(rules.ShownScale)) throw new FormatException("gun-hold shownScale must be positive.");
        if (rules.HipItemFov is < 10 or > 120) throw new FormatException("gun-hold hipItemFov must be 10-120 degrees.");
        if (!(rules.HipItemFovWeight >= 0f && rules.HipItemFovWeight <= 1f)) throw new FormatException("gun-hold hipItemFovWeight must be 0-1.");
        if (!(rules.NearReach > 0f) || !(rules.MaxReach >= rules.NearReach)) throw new FormatException("gun-hold reach needs 0 < near <= max.");
        return rules;
    }

    private static Dictionary<string, GunHoldPreset> ParsePresets(JsonElement presets)
    {
        var all = new Dictionary<string, GunHoldPreset>(StringComparer.Ordinal);
        foreach (var p in presets.EnumerateObject())
        {
            if (p.Name == "about") continue;
            var v = p.Value;
            GunFrame Node(string side)
            {
                var node = v.GetProperty(side);
                var r = node.GetProperty("rotation");
                if (r.GetArrayLength() != 4) throw new FormatException("gun-hold preset " + p.Name + " " + side + " rotation has four numbers.");
                var q = new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
                if (!(MathF.Abs(q.Length() - 1f) <= .001f)) throw new FormatException("gun-hold preset " + p.Name + " " + side + " rotation must be a unit quaternion.");
                return new GunFrame(Vector(node.GetProperty("at")), Quaternion.Normalize(q));
            }
            all[p.Name] = new GunHoldPreset(p.Name, v.GetProperty("gear").GetUInt32(), v.GetProperty("leftAnim").GetString()!, v.GetProperty("rightAnim").GetString()!,
                Node("right"), Node("left"), Vector(v.GetProperty("muzzle")), v.TryGetProperty("sight", out var s) ? Vector(s) : null);
        }
        if (all.Count == 0) throw new FormatException("gun-hold has no presets.");
        return all;
    }

    internal static Vector3 Vector(JsonElement value)
    {
        if (value.GetArrayLength() != 3) throw new FormatException("gun-hold vectors have three numbers.");
        var v = new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new FormatException("gun-hold vectors must be finite.");
        return v;
    }
}

/// <summary>One of the game's own holds (<see cref="GunHoldRules.Presets"/>): the hand nodes (wrists) of the game's guns that hold this way, read in
/// game, in the gun frame, with the gear block that holds so and its grip animations. In game the hands keep the actual gun's nodes; without it
/// (offline, the website) <see cref="On"/> stands the preset in for them.</summary>
public sealed record GunHoldPreset(string Id, uint Gear, string LeftAnim, string RightAnim, GunFrame Right, GunFrame Left, Vector3 Muzzle, Vector3? Sight)
{
    /// <summary><paramref name="model"/> held on this preset's hand nodes, muzzle and sight in place of a game gun's.</summary>
    public GunHoldInput On(GunHoldInput model) => model with
    {
        NativeRight = Right.Position,
        NativeRightRotation = Right.Rotation,
        NativeLeft = Left.Position,
        NativeLeftRotation = Left.Rotation,
        NativeMuzzle = Muzzle,
        NativeSight = Sight,
    };
}

/// <summary>An author's turn of one hand on its grip point: <see cref="Offset"/> moves the point of the hand that touches the gun (the hand
/// node's frame, metres; added to the palm), <see cref="Angle"/> turns the hand about the bore through the grip point (degrees, +X toward +Y).</summary>
public readonly record struct GunHandAdjust(Vector3 Offset, float Angle);

/// <summary>A model's row in a grip table: the preset its hold is taken from, the muzzle, sight, shell-eject and flashlight points the model
/// gives (model frame, own size, metres; null keeps the model's own socket) and each hand's adjustment. The hands are not in the row: a model's
/// own hand sockets are kept (a missing right grip is the model's origin), a missing left hand is derived from the game's own hold for the
/// preset (<see cref="GunHold.LeftGrip"/>), and the left hand is clamped to the arm's reach.</summary>
public sealed record GunGripEntry(string Preset, Vector3? Muzzle, Vector3? Sight, GunHandAdjust Right, GunHandAdjust Left)
{
    /// <summary>Where the shells leave and where the flashlight node sits (model frame, own size, metres); null keeps the game gun's own.</summary>
    public Vector3? ShellEject { get; init; }
    public Vector3? Flashlight { get; init; }
}

/// <summary>A model that stands in for one part of a game gun (not the whole gun) and carries the part's facts (<c>forge-gun-part-points</c>,
/// schema 1): where its muzzle, left grip point and sight are, in the model's frame on its part (metres). A point left out stays the game gun's.</summary>
public sealed record GunPartPoint(Vector3? Muzzle, Vector3? LeftHand, Vector3? Sight);

/// <summary>A package's table of <see cref="GunPartPoint"/> by model.</summary>
public sealed class GunPartPoints
{
    private readonly Dictionary<string, GunPartPoint> _models = new(StringComparer.Ordinal);

    /// <summary>Reads the table; throws <see cref="FormatException"/> naming what is wrong.</summary>
    public static GunPartPoints Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-gun-part-points" || !ForgeCore.FormatVersion.Matches(root))
            throw new FormatException("Not a forge-gun-part-points file of schema 1.");
        var points = new GunPartPoints();
        foreach (var model in root.GetProperty("models").EnumerateObject())
        {
            var v = model.Value;
            Vector3? Point(string name) => v.TryGetProperty(name, out var p) ? GunHoldRules.Vector(p) : null;
            var row = new GunPartPoint(Point("muzzle"), Point("leftHand"), Point("sight"));
            if (row.Muzzle == null && row.LeftHand == null && row.Sight == null) throw new FormatException("gun-part-points model " + model.Name + " gives no point.");
            points._models[model.Name] = row;
        }
        return points;
    }

    /// <summary>The row of <paramref name="model"/>; false when the table has none.</summary>
    public bool TryGet(string model, out GunPartPoint row) => _models.TryGetValue(model, out row!);

    /// <summary>Every row, by model.</summary>
    public IReadOnlyDictionary<string, GunPartPoint> Models => _models;
}

/// <summary>A package's hold table for its gun models (<c>forge-gun-grips</c>, schema 2): per model (its prefab name) the preset
/// (<see cref="GunHoldRules.Presets"/>) whose game hold the model is held by, the muzzle, sight, shell-eject and flashlight points in the
/// model's own frame at its own size (metres, the export's axes), and an optional <c>adjust</c> per hand. A model's own hand sockets are kept
/// (a missing right grip is its origin); a missing left hand is derived from the game's own hold for the preset and the model's right grip
/// (<see cref="GunHold.LeftGrip"/>), and the mount pulls the left hand back along the gun into the arm's reach; <c>adjust</c> is the only
/// per-gun hand fine-tune, so a row that gives <c>rightHand</c> or <c>leftHand</c> is refused. A listed muzzle, sight, shell-eject or
/// flashlight point's socket is moved there (made when the model has none) before the model is seated; a model absent from the table keeps
/// its own sockets.</summary>
public sealed class GunGrips
{
    private readonly Dictionary<string, GunGripEntry> _models = new(StringComparer.Ordinal);

    /// <summary>Reads the table; throws <see cref="FormatException"/> naming what is wrong. Preset ids are checked against the rules where they are used.</summary>
    public static GunGrips Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-gun-grips" || !ForgeCore.FormatVersion.Matches(root))
            throw new FormatException("Not a forge-gun-grips file of schema 2.");
        var grips = new GunGrips();
        foreach (var model in root.GetProperty("models").EnumerateObject())
        {
            var v = model.Value;
            foreach (var field in new[] { "rightHand", "leftHand" })
                if (v.TryGetProperty(field, out _))
                    throw new FormatException("gun-grips model " + model.Name + " gives " + field + ": hands come from the model's RightHand and LeftHand sockets and the preset's vanilla hold, and adjust is the per-gun fine-tune.");
            Vector3? Point(string name) => v.TryGetProperty(name, out var p) ? GunHoldRules.Vector(p) : null;
            var preset = v.TryGetProperty("preset", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(preset)) throw new FormatException("gun-grips model " + model.Name + " names no preset.");
            GunHandAdjust Adjust(string side)
            {
                if (!v.TryGetProperty("adjust", out var a) || !a.TryGetProperty(side, out var hand)) return default;
                var offset = hand.TryGetProperty("offset", out var o) ? GunHoldRules.Vector(o) : Vector3.Zero;
                var angle = hand.TryGetProperty("angle", out var g) ? g.GetSingle() : 0f;
                if (!float.IsFinite(angle)) throw new FormatException("gun-grips model " + model.Name + " " + side + " angle must be finite.");
                return new GunHandAdjust(offset, angle);
            }
            grips._models[model.Name] = new GunGripEntry(preset!, Point("muzzle"), Point("sight"), Adjust("right"), Adjust("left"))
            {
                ShellEject = Point("shellEject"),
                Flashlight = Point("flashlight"),
            };
        }
        return grips;
    }

    /// <summary>Reads the table a package embeds as <paramref name="resource"/>; throws <see cref="System.IO.InvalidDataException"/> when it is missing.</summary>
    public static GunGrips Read(System.Reflection.Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new System.IO.InvalidDataException(assembly.GetName().Name + " is built without " + resource + ".");
        using var reader = new System.IO.StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The row of <paramref name="model"/> (its prefab name); false when the table has none.</summary>
    public bool TryGet(string model, out GunGripEntry entry) => _models.TryGetValue(model, out entry!);

    /// <summary>Every row, by model.</summary>
    public IReadOnlyDictionary<string, GunGripEntry> Models => _models;
}

/// <summary>A grip point of a model, or the wrist pose of a game gun's hand node: a position and, when the model gives one, a rotation. A bare point
/// is held by the game's hand turned as the game turns it: on every game gun the left palm faces the gun from its lower left, never up.</summary>
public readonly record struct GunGrip(Vector3 Position, Quaternion? Rotation = null);

/// <summary>What <see cref="GunHold.Solve"/> reads. Every point is in the replaced game gun's frame (+Z along its bore, +Y up, +X right); the
/// model's points are measured with the model turned to face +Z, at its own size, its origin on the gun's origin.</summary>
public sealed record GunHoldInput
{
    /// <summary>The game gun's right and left hand nodes (the wrist targets the game's hands go to) and their rotations.</summary>
    public Vector3 NativeRight { get; init; }
    public Quaternion NativeRightRotation { get; init; } = Quaternion.Identity;
    public Vector3 NativeLeft { get; init; }
    public Quaternion NativeLeftRotation { get; init; } = Quaternion.Identity;
    public Vector3? NativeMuzzle { get; init; }
    public Vector3? NativeSight { get; init; }
    /// <summary>The model's <c>RightHand</c> and <c>LeftHand</c>: a bare grip point, or with a rotation the wrist pose itself.</summary>
    public GunGrip ModelRight { get; init; }
    public GunGrip ModelLeft { get; init; }
    public Vector3? ModelMuzzle { get; init; }
    public Vector3? ModelSight { get; init; }
    /// <summary>The model's length along the bore at its own size (mesh bounds, metres).</summary>
    public float ModelLength { get; init; }
    /// <summary>The grip table's turn of each hand (<see cref="GunHandAdjust"/>); none by default.</summary>
    public GunHandAdjust RightAdjust { get; init; }
    public GunHandAdjust LeftAdjust { get; init; }
}

/// <summary>Where the model goes and where the wrists go, at rest, in the game gun's frame.</summary>
public readonly record struct GunHoldResult(float Scale, Vector3 Origin, GunFrame Right, GunFrame Left, float ShownLength);

/// <summary>What a hand target is against the arm's reach.</summary>
public enum GunReach { Ok, Near, Out }

/// <summary>
/// The one placement of a gun model on a game gun. The model's size: <see cref="GunHoldRules.ShownScale"/> of its own, always (a length outside
/// the game's range is reported by <see cref="LengthInRange"/>, never squeezed). Along the bore its right grip point lands on the game's right hand node; across, its bore is on the game's bore; in
/// height, its sight is on the game's sight (the game aims by its sight height), else its bore on the game's bore, else its right wrist
/// on the game's. A bare grip point is where the palm touches the gun: each hand keeps the game hand's rotation (the game's own grip pose, fingers
/// and all, is made for it) and its wrist goes where that hand's
/// palm (<see cref="GunHoldRules.RightPalm"/>, <see cref="GunHoldRules.LeftPalm"/>) lands on the point; a grip point with its own rotation is the wrist pose itself.
/// A model's own left grip is kept; where it is missing, <see cref="LeftGrip"/> gives the point that puts the game's own left hand where its
/// right hand is relative to the right grip, and <see cref="IntoReach"/> pulls it back along the gun when the hip's arm cannot reach it.
/// A grip table's <see cref="GunHandAdjust"/> turns a hand about the bore and shifts its touch point (the only per-gun hand fine-tune). The rule
/// depends on the package or gun only through the preset and those adjustments; offline, a <see cref="GunHoldPreset"/> stands in for the game gun's nodes.
/// </summary>
public static class GunHold
{
    /// <summary>The placement; throws <see cref="ArgumentException"/> for a point that is not a finite number or a length that is not positive.</summary>
    public static GunHoldResult Solve(GunHoldInput input, GunHoldRules rules)
    {
        Finite(input.NativeRight); Finite(input.NativeLeft); Finite(input.ModelRight.Position); Finite(input.ModelLeft.Position);
        if (input.NativeMuzzle is { } a) Finite(a);
        if (input.NativeSight is { } b) Finite(b);
        if (input.ModelMuzzle is { } c) Finite(c);
        if (input.ModelSight is { } d) Finite(d);
        if (!(input.ModelLength > 0f) || !float.IsFinite(input.ModelLength)) throw new ArgumentException("A gun model's length must be positive.");
        var length = input.ModelLength;
        var scale = rules.ShownScale;
        Finite(input.RightAdjust.Offset); Finite(input.LeftAdjust.Offset);
        var rightRotation = Turn(Unit(input.ModelRight.Rotation ?? input.NativeRightRotation), input.RightAdjust.Angle);
        var leftRotation = Turn(Unit(input.ModelLeft.Rotation ?? input.NativeLeftRotation), input.LeftAdjust.Angle);
        // A bare grip point is where the palm touches: the wrist sits back from it by the game's own hand, turned as the game's hand is
        // (and as the grip table turns and shifts it).
        var rightWrist = WristFromTouch((input.ModelRight.Rotation == null ? rules.RightPalm : Vector3.Zero) + input.RightAdjust.Offset, rightRotation);
        var leftWrist = WristFromTouch((input.ModelLeft.Rotation == null ? rules.LeftPalm : Vector3.Zero) + input.LeftAdjust.Offset, leftRotation);
        var right = input.ModelRight.Position * scale;
        // Along the bore the right grip point sits on the game's right hand node (where the approved models are seen).
        var z = input.NativeRight.Z - right.Z;
        var x = input.NativeMuzzle is { } nm && input.ModelMuzzle is { } mm
            ? nm.X - scale * mm.X
            : input.NativeRight.X - rightWrist.X - right.X;
        float y;
        if (input.NativeSight is { } ns && input.ModelSight is { } ms) y = ns.Y - scale * ms.Y;
        else if (input.NativeMuzzle is { } nb && input.ModelMuzzle is { } mb) y = nb.Y - scale * mb.Y;
        else y = input.NativeRight.Y - rightWrist.Y - right.Y;
        var origin = new Vector3(x, y, z);
        var rightTarget = new GunFrame(origin + right + rightWrist, rightRotation);
        var leftTarget = new GunFrame(origin + input.ModelLeft.Position * scale + leftWrist, leftRotation);
        return new GunHoldResult(scale, origin, rightTarget, leftTarget, scale * length);
    }

    /// <summary>The left grip of a model that has none, giving it the game's own left hand: where the game gun's left palm touches relative to its right palm
    /// (<see cref="GunHoldRules.LeftPalm"/> and <see cref="GunHoldRules.RightPalm"/> in the hand nodes' frames), put on the model at its right
    /// grip. The model is turned so its bore follows the gun's and is shown at <see cref="GunHoldRules.ShownScale"/> of its own size, so the
    /// game's offset in metres moves the model's own point by that over the scale; the derived wrist is then exactly the game gun's left hand
    /// relative to its right. A right grip with a rotation is a wrist pose itself: the left wrist goes the game's wrist offset from it and takes
    /// the game's left rotation. Throws <see cref="ArgumentException"/> for a point that is not finite or a rotation that is not a unit quaternion.</summary>
    public static GunGrip LeftGrip(GunGrip right, Vector3 nativeRight, Quaternion nativeRightRotation, Vector3 nativeLeft, Quaternion nativeLeftRotation, GunHoldRules rules)
    {
        Finite(right.Position); Finite(nativeRight); Finite(nativeLeft);
        var scale = rules.ShownScale;
        if (right.Rotation is { } wrist)
        {
            Unit(wrist); Unit(nativeRightRotation); Unit(nativeLeftRotation);
            return new GunGrip(right.Position + (nativeLeft - nativeRight) / scale, nativeLeftRotation);
        }
        Unit(nativeRightRotation); Unit(nativeLeftRotation);
        var rightTouch = nativeRight + Vector3.Transform(rules.RightPalm, nativeRightRotation);
        var leftTouch = nativeLeft + Vector3.Transform(rules.LeftPalm, nativeLeftRotation);
        return new GunGrip(right.Position + (leftTouch - rightTouch) / scale);
    }

    /// <summary>The wrist from the point it touches with: back by <paramref name="touch"/> (the palm and any shift, in the hand node's frame), turned by the hand's rotation.</summary>
    public static Vector3 WristFromTouch(Vector3 touch, Quaternion hand) => -Vector3.Transform(touch, hand);

    /// <summary>A hand target against the arm: shoulder (upper arm bone) to target over <see cref="GunHoldRules.MaxReach"/> is <see cref="GunReach.Out"/>
    /// (at the hip the mount pulls the left hand back along the gun, while aiming it is a hint), over <see cref="GunHoldRules.NearReach"/> is <see cref="GunReach.Near"/>.</summary>
    public static GunReach Reach(float shoulderToTarget, GunHoldRules rules)
        => shoulderToTarget > rules.MaxReach ? GunReach.Out : shoulderToTarget > rules.NearReach ? GunReach.Near : GunReach.Ok;

    /// <summary><paramref name="target"/> pulled back along the gun's <paramref name="along"/> (a unit direction toward the muzzle) until it is
    /// within <see cref="GunHoldRules.MaxReach"/> of <paramref name="shoulder"/>: the near point where the gun's line crosses the arm's reach.
    /// <paramref name="target"/> itself when it is already in reach, or when the line never comes within the reach (nothing on the gun is
    /// reachable from that shoulder).</summary>
    public static Vector3 IntoReach(Vector3 shoulder, Vector3 target, Vector3 along, GunHoldRules rules)
    {
        Finite(shoulder); Finite(target); Finite(along);
        var offset = target - shoulder;
        var outside = offset.LengthSquared() - rules.MaxReach * rules.MaxReach;
        if (outside <= 0f) return target;
        var forward = Vector3.Dot(offset, along);
        var disc = forward * forward - outside;
        if (disc <= 0f) return target;
        var pull = forward - MathF.Sqrt(disc);
        return pull > 0f ? target - along * pull : target;
    }

    /// <summary>Whether a shown length is inside the game's own range (<see cref="GunHoldRules.LengthMin"/>–<see cref="GunHoldRules.LengthMax"/>).
    /// Outside it is the model author's error; the model keeps its size.</summary>
    public static bool LengthInRange(float shownLength, GunHoldRules rules) => shownLength >= rules.LengthMin && shownLength <= rules.LengthMax;

    /// <summary>The hip item field of view a held gun gets: from the replaced game gun's own (<paramref name="gameFov"/>) toward
    /// <see cref="GunHoldRules.HipItemFov"/> by <see cref="GunHoldRules.HipItemFovWeight"/>, to the whole degree the game stores (halves away from zero).</summary>
    public static int HipItemFov(int gameFov, GunHoldRules rules)
        => (int)MathF.Round(gameFov + (rules.HipItemFov - gameFov) * rules.HipItemFovWeight, MidpointRounding.AwayFromZero);

    /// <summary>The world camera's aim field of view through a sight of <paramref name="magnification"/>: the half-angle tangent of
    /// <see cref="GunHoldRules.OpenSightLookFov"/> over the magnification, to the whole degree the game stores. Throws <see cref="ArgumentException"/> under 1.</summary>
    public static int SightLookFov(float magnification, GunHoldRules rules)
    {
        if (!(magnification >= 1f) || !float.IsFinite(magnification)) throw new ArgumentException("A sight's magnification must be 1 or more.");
        var half = MathF.Atan(MathF.Tan(rules.OpenSightLookFov * MathF.PI / 360f) / magnification);
        return (int)MathF.Round(half * 360f / MathF.PI, MidpointRounding.AwayFromZero);
    }

    /// <summary><paramref name="q"/> turned <paramref name="degrees"/> about the bore (+Z, +X toward +Y).</summary>
    private static Quaternion Turn(Quaternion q, float degrees)
    {
        if (!float.IsFinite(degrees)) throw new ArgumentException("A hand's turn must be a finite angle.");
        return degrees == 0f ? q : Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f) * q);
    }

    private static Quaternion Unit(Quaternion q)
    {
        var length = q.Length();
        if (!float.IsFinite(length) || length < 1e-6f) throw new ArgumentException("A grip rotation must be a finite, non-zero quaternion.");
        return Quaternion.Normalize(q);
    }

    private static void Finite(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new ArgumentException("A gun point must be a finite number.");
    }
}

public readonly record struct GunFrame(Vector3 Position, Quaternion Rotation);

/// <summary>How a gun model and its hand targets follow the game's own hand nodes: the model and its targets keep their offset from the right hand node
/// (which the game's animations move), and each wrist target also takes what the game's own node for that hand does on its own (a reload takes the left hand off the gun and back).</summary>
internal static class GunPose
{
    /// <summary>Where <paramref name="restChild"/> goes when its parent moves from <paramref name="restParent"/> to <paramref name="currentParent"/>.</summary>
    internal static GunFrame Follow(in GunFrame restParent, in GunFrame currentParent, in GunFrame restChild)
    {
        Validate(restParent); Validate(currentParent); Validate(restChild);
        var inverse = Quaternion.Inverse(restParent.Rotation);
        var offset = Vector3.Transform(restChild.Position - restParent.Position, inverse);
        var rotation = Quaternion.Normalize(currentParent.Rotation * inverse * restChild.Rotation);
        return new GunFrame(currentParent.Position + Vector3.Transform(offset, currentParent.Rotation), rotation);
    }

    /// <summary>A hand target: <paramref name="target"/> carried with the gun, plus what the game's hand node does on its own.</summary>
    internal static GunFrame FollowHand(in GunFrame restGrip, in GunFrame currentGrip, in GunFrame restHand, in GunFrame currentHand, in GunFrame target)
    {
        Validate(currentHand);
        var donor = Follow(restGrip, currentGrip, restHand);
        var carried = Follow(restGrip, currentGrip, target);
        var rotation = Quaternion.Normalize(currentHand.Rotation * Quaternion.Inverse(donor.Rotation) * carried.Rotation);
        return new GunFrame(carried.Position + currentHand.Position - donor.Position, rotation);
    }

    private static void Validate(in GunFrame frame)
    {
        var p = frame.Position;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(frame.Rotation.LengthSquared()) ||
            MathF.Abs(frame.Rotation.LengthSquared() - 1f) > .001f)
            throw new ArgumentException("A gun socket's pose must be finite with a unit rotation.");
    }
}
