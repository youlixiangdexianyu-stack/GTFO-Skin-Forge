using System;
using System.Collections.Generic;
using FX_EffectSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>How an effect request ended.</summary>
public enum FxResult
{
    Played,
    /// <summary>The effect exists but this level has not loaded it (the game builds some pools only when gear that uses them is in
    /// the level), or its pack's art could not be loaded.</summary>
    Unavailable,
    /// <summary>The request does not fit the effect: an end for an effect that is not a line, or no end for one that is.</summary>
    Unsupported,
}

/// <summary>An effect resolved by name: a <c>Game:</c> effect or one a pack registered (<see cref="Fx.Register(BundleHandle, string, FxOptions)"/>).</summary>
public sealed class FxHandle
{
    internal FxHandle(string name, GameEffect? game, FxPrefab? prefab)
    {
        Name = name;
        Game = game;
        Prefab = prefab;
        Seen = ForgeCore.RecordingNames.Of(name);
    }

    /// <summary>The handle of a light-only slot (<see cref="FxSlots"/>: a Light and no Item): playing it flashes the light and draws nothing else.</summary>
    internal FxHandle(string name, PointLightSpec glow, float glowDelay) : this(name, null, null)
    {
        Glow = glow;
        GlowDelay = glowDelay;
    }

    public string Name { get; }

    /// <summary>The name's number for reports to <see cref="ForgeCore.Recording"/>.</summary>
    internal readonly int Seen;
    /// <summary>The light of a light-only slot, null for any other handle.</summary>
    internal PointLightSpec? Glow { get; }
    internal float GlowDelay { get; }

    /// <summary>The handle of a slot the table lists as <c>{ "None": true }</c> (<see cref="FxSlots.Find"/>): the moment draws nothing.</summary>
    internal static readonly FxHandle Nothing = new("(nothing)", null, null);

    /// <summary>True for the handle of an explicitly empty slot: <see cref="Fx.Hold"/>, <see cref="Fx.Line(FxHandle, Vector3, Vector3)"/> and the plays draw nothing and report <see cref="FxResult.Played"/>.</summary>
    public bool IsNothing => ReferenceEquals(this, Nothing);

    /// <summary>True for an effect drawn from a point to an end (<see cref="Fx.Line(FxHandle, Vector3, Vector3)"/>).</summary>
    public bool Line => Game?.Line ?? Prefab?.Options.Line ?? false;

    /// <summary>True for an effect that can be held (<see cref="Fx.Hold"/>).</summary>
    public bool Sustained => Prefab != null && Prefab.Options.HoldSeconds > 0f;

    internal GameEffect? Game { get; }
    internal FxPrefab? Prefab { get; }
    internal FX_Pool? Pool;
    internal EX_SpriteMuzzleFlash?[]? Flashes;
    internal int FlashCursor;
}

/// <summary>How a pack's own effect prefab plays.</summary>
public sealed class FxOptions
{
    /// <summary>The size the prefab is authored at (m); a request's size scales it by size / this. NaN: the effect takes no size.</summary>
    public float DefaultSize { get; set; } = float.NaN;

    /// <summary>An extra factor on whatever size the effect plays at (1: none), for a prefab authored larger or smaller than its use.</summary>
    public float BaseScale { get; set; } = 1f;

    /// <summary>The prefab's +Z length is stretched from the point to the end (<see cref="Fx.Line(FxHandle, Vector3, Vector3)"/>): one metre long, or <see cref="DefaultSize"/>
    /// metres when that is set (a line effect then takes no size from the caller).</summary>
    public bool Line { get; set; }

    /// <summary>Above 0: a sustained effect (the prefab loops at its root). A hold keeps emitting this long after the last call. Each caller's key has its own copy.</summary>
    public float HoldSeconds { get; set; }

    /// <summary>Copies kept; a play past that restarts the oldest.</summary>
    public int Copies { get; set; } = 16;

    /// <summary>Seconds a one-shot keeps emitting before it stops and lets its particles finish, however its prefab loops. 0: one round of its longest
    /// layer, start delay included, at most 8 s (the game's build has stripped MainModule.loop, so a play cannot ask whether the prefab loops: a looping prefab that should play shorter sets this).</summary>
    public float Seconds { get; set; }

    /// <summary>The prefab's axis is +Y (a ground effect, a column) rather than +Z (a muzzle, a jet): a request's direction turns +Y onto it.</summary>
    public bool AxisUp { get; set; }

    /// <summary>Where a prefab draws its middle, from its root (m, at scale 1, unrotated), when that is not the root (an explosion authored standing on its root): a request's point is where that
    /// middle stands, at any size and direction, so what is drawn and what the caller means by the point are one place. Zero: the root.</summary>
    public Vector3 Centre { get; set; }

    /// <summary>Above 0: <see cref="Fx.Field"/> covers an area with held copies this many metres apart instead of one copy scaled to it.</summary>
    public float Spacing { get; set; }

    /// <summary>A laser line (<see cref="Line"/>) keeps its uniform <see cref="BaseScale"/> instead of being stretched as a whole: this child of the prefab (a path below the root) goes to the line's end.</summary>
    public string? LaserHead { get; set; }

    /// <summary>The children of a laser line that reach from the point to the end, <see cref="DefaultSize"/> metres long as authored. A stretched billboard takes the length through its renderer's length scale, which
    /// changes its live particles at once (start sizes only reach particles not yet emitted); any other layer, a flat line along its own +X, through its transform's X.</summary>
    public string[]? LaserBodies { get; set; }

    /// <summary>Above 0: a laser line's bodies repeat their texture every this many metres, so a jagged stroke stays jagged at any length instead of stretching out.</summary>
    public float LaserTile { get; set; }

    /// <summary>Above 0: a line that ends in the air (<see cref="Fx.Line(FxHandle, int, Vector3, Vector3, bool, Color)"/> with head false: it hit nothing) fades out over this many metres
    /// before its end instead of stopping square; a line that hit something keeps its end. The Forge particle shaders read it (<c>_LineEnd</c>, <c>_LineFade</c>), others ignore it.</summary>
    public float EndFade { get; set; }

    /// <summary>Seconds a copy may stay after it stopped emitting. A sustained effect with long-lived particles (a standing vortex, a ring) sets it short, so it ends with its hold instead of standing on where nothing is left.</summary>
    public float Linger { get; set; } = 8f;

    /// <summary>A point light that goes with every play (<see cref="FxLights"/>): a one-shot flashes it (peak at once, fading over its life, <see cref="LightDelay"/> late),
    /// a held effect holds it while held. Its colour is multiplied by the caller's hue (<see cref="FxLights"/>); its range grows with the size the effect plays at. Null: no light.</summary>
    public PointLightSpec? Light { get; set; }

    /// <summary>Seconds after the play the light flashes.</summary>
    public float LightDelay { get; set; }

    /// <summary>The box the drawn particles fill (m from the prefab's root, at scale 1, unrotated), as the converter measured it; null: not known.
    /// <see cref="Fx.Seat"/> stands a copy by it so it clears floor and ceiling.</summary>
    public Bounds? Body { get; set; }

    /// <summary>True: never culled (drawn at any distance and from any direction); false: always culled; null (the default): culled unless it plays large
    /// (its reach is <see cref="FxCulling.LargeReach"/> metres or more: a blast, an air strike, which people see from far away). Lines are never culled.</summary>
    public bool? AlwaysDraw { get; set; }

    /// <summary>Metres beyond which a copy is paused and hidden; 0 (the default): the player's <c>Culling / Distance</c> setting. Measured to the surface of its reach.</summary>
    public float CullDistance { get; set; }
}

/// <summary>
/// Culling of pack effects (<see cref="Fx"/>): a copy beyond its distance from the eye or outside the view is paused and its renderers hidden, so it costs
/// neither simulation nor draw; once back in range and in view it resumes where it stopped (a one-shot's window waits for it). The check runs a few copies a
/// frame in turn (<see cref="PerFrame"/>), never all of them at once, and allocates nothing. What an effect reports to <see cref="ForgeCore.Recording"/> is
/// not affected: it is recorded when it is requested, seen or not. The <c>Culling</c> settings of ForgeVisual turn it off or change the default distance.
/// </summary>
internal static class FxCulling
{
    /// <summary>An effect whose reach is at least this many metres is not culled unless its slot says so (<see cref="FxOptions.AlwaysDraw"/>).</summary>
    public const float LargeReach = 10f;

    /// <summary>Copies checked per frame, all effects together.</summary>
    internal const int PerFrame = 24;

    /// <summary>Effects looked at per frame when none of the first ones plays.</summary>
    internal const int PrefabVisits = 32;

    /// <summary>Whether culling is on (the player's setting; a test or a tool may switch it).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The distance, in metres, an effect without its own <see cref="FxOptions.CullDistance"/> is hidden beyond.</summary>
    public static float Distance { get; set; } = 70f;
}

/// <summary>
/// The lights of pack effects (<see cref="FxOptions.Light"/>), drawn through the shared <see cref="PointLights"/> pool. At most <see cref="Max"/> effect lights are lit at once,
/// so a barrage of blasts cannot take every light the other mods need; a play past that draws its particles without a light. Main thread only; nothing allocates.
/// </summary>
internal static class FxLights
{
    /// <summary>Effect lights lit at once, every pack together.</summary>
    public const int Max = 8;
    private const float HoldGrace = 0.25f;
    private static readonly float[] Until = new float[Max];
    private static readonly int[] Generation = new int[Max];

    /// <summary>A budget place until <paramref name="until"/> as a token (its generation and index: a holder whose place lapsed in a stall and
    /// was taken by another play does not keep it as well), or -1 when all are taken.</summary>
    private static int Take(float now, float until)
    {
        for (var i = 0; i < Max; i++)
            if (Until[i] <= now) { Until[i] = until; return Token(i, ++Generation[i]); }
        return -1;
    }

    private static int Token(int index, int generation) => ((generation & 0x7ffffff) << 3) | index;

    /// <summary>The spec in the caller's hue, as the particle shader takes it: colour over its luma (at least 0.05), so a dark or HDR colour changes the
    /// light's hue, not its brightness; its range grows with the size the effect plays at.</summary>
    internal static PointLightSpec Coloured(in PointLightSpec spec, Color colour, float reach)
    {
        var luma = Mathf.Max(colour.r * 0.299f + colour.g * 0.587f + colour.b * 0.114f, 0.05f);
        var hue = new Color(colour.r / luma, colour.g / luma, colour.b / luma, 1f);
        return new(spec.Color * hue, spec.Range * reach, spec.Intensity, spec.Life, spec.Flicker);
    }

    internal static void Flash(in PointLightSpec spec, float delay, Vector3 position, Color colour, float reach, float now)
    {
        if (Take(now, now + delay + spec.Life) < 0) return;
        PointLights.Flash(position, Coloured(spec, colour, reach), now, 1f, delay);
    }

    /// <summary>Keeps a held effect's light this frame; <paramref name="place"/> and <paramref name="handle"/> are the copy's (both start at -1 and 0).</summary>
    internal static void Hold(in PointLightSpec spec, ref int place, ref int handle, Vector3 position, Color colour, float reach, float now)
    {
        var index = place & (Max - 1);
        if (place >= 0 && Until[index] > now && Token(index, Generation[index]) == place) Until[index] = now + HoldGrace;
        else if ((place = Take(now, now + HoldGrace)) < 0) return;
        PointLights.Hold(ref handle, position, Coloured(spec, colour, reach), now);
    }

    internal static void Clear() => Array.Clear(Until, 0, Max);
}

/// <summary>
/// Effects by name. <c>Game:</c> is the game's own catalog (<c>Game:FX_Tripmine</c>, <c>Game:FX_Tracer</c>,
/// <c>Game:Sentrygun_MuzzleFlash_3RD</c>, ...), taken through the entries the game itself uses; a pack's own particle prefabs are
/// registered with <see cref="Register(BundleHandle, string, FxOptions)"/> and played the same way by any mod. Resolve a name once with <see cref="Find"/>; playing
/// looks nothing up and allocates nothing. Effects are drawn on this peer only.
/// </summary>
public static class Fx
{
    private static readonly NameTable<FxHandle> Registered = new();
    private static readonly Dictionary<string, FxHandle> GameHandles = new(StringComparer.Ordinal);
    private static readonly List<FxPrefab> Prefabs = new();
    private static FxHandle? _tracer;
    private static GameObject? _root;

    /// <summary>The effect <paramref name="name"/>, or null (logged once) when it does not exist.</summary>
    public static FxHandle? Find(string name)
    {
        if (name != null && name.StartsWith(VisualNames.Game + ":", StringComparison.Ordinal))
        {
            if (GameHandles.TryGetValue(name, out var known)) return known;
            if (GameCatalog.TryGet(name, out var effect)) return GameHandles[name] = new FxHandle(name, effect, null);
            Plugin.Logger.ErrorOnce("visual-game-effect-unknown", "ForgeVisual: the game's effect catalog has no " + name + ".");
            return null;
        }
        EffectBundle.Ensure(name);
        HeavyBlast.Ensure(name);
        if (Registered.TryGet(name, out var handle)) return handle;
        if (Missing.First("effect", name)) Plugin.Logger.ErrorOnce("visual-effect-unknown", "ForgeVisual has no effect " + name + ": " + Missing.Why(name, Bundles.Known, "effect") + ".");
        return null;
    }

    /// <summary>Every <c>Game:</c> effect name.</summary>
    public static IEnumerable<string> GameNames
    {
        get { foreach (var e in GameCatalog.All) yield return e.Name; }
    }

    /// <summary>Registers the particle prefab <paramref name="item"/> of <paramref name="bundle"/> as <c>Pack:item</c>. Null (logged once)
    /// when the name is taken or the prefab has no particle system at its root.</summary>
    public static FxHandle? Register(BundleHandle bundle, string item, FxOptions options) => Register(bundle, item, item, options);

    /// <summary>Registers the bundle's prefab <paramref name="item"/> under the effect name <paramref name="alias"/>, so one prefab can be
    /// registered again with other options (a size, a hold) under another name.</summary>
    public static FxHandle? Register(BundleHandle bundle, string item, string alias, FxOptions options)
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        options ??= new FxOptions();
        var name = VisualNames.Join(bundle.Pack, alias);
        var log = ForgeCore.Log.For(bundle.ModId);
        if (!bundle.TryModel(item, out var model)) { log.ErrorOnce("visual-effect-prefab-missing", "ForgeVisual effect " + name + " not registered: the bundle has no prefab " + item + "."); return null; }
        if (model.Prefab.GetComponent<ParticleSystem>() == null) { log.ErrorOnce("visual-effect-no-particle-root", "ForgeVisual effect " + name + " not registered: its prefab has no particle system at its root."); return null; }
        var prefab = new FxPrefab(name, model, options);
        var handle = new FxHandle(name, null, prefab);
        if (Registered.Add(name, handle) is { } why) { log.ErrorOnce("visual-effect-not-registered", "ForgeVisual effect " + name + " not registered: " + why + "."); return null; }
        Prefabs.Add(prefab);
        return handle;
    }

    /// <summary>Makes the light-only handle of a slot findable by its name, so a replay of a recording that names it resolves it like any other effect.</summary>
    internal static void RegisterLight(FxHandle handle, string modId)
    {
        if (Registered.Add(handle.Name, handle) is { } why) ForgeCore.Log.For(modId).ErrorOnce("visual-effect-not-registered", "ForgeVisual effect " + handle.Name + " not registered: " + why + ".");
    }

    /// <summary>Plays <paramref name="fx"/> once at <paramref name="position"/>, facing <paramref name="direction"/> (zero: unrotated).</summary>
    public static FxResult Play(FxHandle fx, Vector3 position, Vector3 direction) => Play(fx, position, direction, float.NaN, Color.white);

    /// <summary>Plays <paramref name="fx"/> with a size (m) and a tint. A <c>Game:</c> effect keeps the game's own size and colour;
    /// a pack's effect without a <see cref="FxOptions.DefaultSize"/> ignores the size. The tint goes to the prefab's renderers as
    /// <c>_Color</c> (a library layer converted with <c>@tint</c> takes its hue from it at its own brightness, Tools/Vfx; other layers keep their colour).</summary>
    public static FxResult Play(FxHandle fx, Vector3 position, Vector3 direction, float size, Color colour)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (fx.IsNothing) return FxResult.Played;
        if (fx.Line) return Refuse(fx, "is drawn from a point to an end; use Fx.Line");
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Play, fx.Seen, 0, position, direction, size, colour));
        if (fx.Glow is { } glow) { FxLights.Flash(glow, fx.GlowDelay, position, colour, 1f, Scene.Now); return FxResult.Played; }
        var rotation = Aim(fx.Prefab, direction);
        return fx.Game != null ? PlayGame(fx, position, rotation) : Instances.Play(fx.Prefab!, position, rotation, Vector3.one * Scale(fx.Prefab!, size), colour, false, 0);
    }

    /// <summary>Plays a line effect once from <paramref name="from"/> to <paramref name="to"/> (the game's tracer reads its far end from
    /// <c>FX_Manager.EffectTargetPosition</c>, which is written first, the way a weapon does). A line effect registered with
    /// <see cref="FxOptions.HoldSeconds"/> is held and takes the key form (<see cref="Line(FxHandle, int, Vector3, Vector3, bool, Color)"/>).</summary>
    public static FxResult Line(FxHandle fx, Vector3 from, Vector3 to) => Line(fx, from, to, true);

    /// <summary>The same line; <paramref name="head"/> false leaves a laser line's <see cref="FxOptions.LaserHead"/> out (the line ends in the air, it hit nothing).</summary>
    public static FxResult Line(FxHandle fx, Vector3 from, Vector3 to, bool head) => Line(fx, from, to, head, Color.white);

    /// <summary>The same line in a tint (a pack's effect only: it goes to the renderers as <c>_Color</c>, see <see cref="Play(FxHandle, Vector3, Vector3, float, Color)"/>; a <c>Game:</c> line keeps its own colour).</summary>
    public static FxResult Line(FxHandle fx, Vector3 from, Vector3 to, bool head, Color colour)
    {
        if (fx != null && fx.Sustained) return Refuse(fx, "is a held line; draw it with Fx.Line(handle, key, ...)");
        return Line(fx!, 0, from, to, head, colour);
    }

    /// <summary>A line effect registered with <see cref="FxOptions.HoldSeconds"/> is held like <see cref="Hold"/>: calling this again every frame with the
    /// same <paramref name="key"/> moves and stretches that one live copy, and another key has a copy of its own, however close the lines are (the key comes from
    /// <see cref="NewKey"/>, one per thing that carries the line; any other key is refused). A line that is not held ignores the key.</summary>
    public static FxResult Line(FxHandle fx, int key, Vector3 from, Vector3 to, bool head, Color colour)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (fx.IsNothing) return FxResult.Played;
        if (!fx.Line) return Refuse(fx, "is not a line effect; use Fx.Play");
        if (fx.Sustained && key <= 0) return Refuse(fx, "is a held line and needs a hold key from Fx.NewKey()");
        if (ForgeCore.Recording.Watched)
            ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(fx.Sustained ? ForgeCore.ShownKind.HeldLine : ForgeCore.ShownKind.Line, fx.Seen, fx.Sustained ? key : 0, from, to, float.NaN, colour, head));
        var travel = to - from;
        var rotation = travel.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(travel) : Quaternion.identity;
        if (fx.Game == null)
        {
            var width = fx.Prefab!.Options.BaseScale;
            var length = travel.magnitude;
            return fx.Prefab.Options.LaserBodies != null
                ? Instances.Play(fx.Prefab, from, rotation, new Vector3(width, width, width), colour, fx.Sustained, key, length / width / LineLength(fx.Prefab), head)
                : Instances.Play(fx.Prefab, from, rotation, new Vector3(width, width, length / LineLength(fx.Prefab)), colour, fx.Sustained, key, 1f, head);
        }
        FX_Manager.EffectTargetPosition = to;
        return PlayGame(fx, from, rotation);
    }

    /// <summary>The game's bullet tracer from <paramref name="from"/> to <paramref name="to"/> (<c>Game:FX_Tracer</c>); very short
    /// shots (under half a metre) draw nothing.</summary>
    public static FxResult Tracer(Vector3 from, Vector3 to)
    {
        if ((to - from).sqrMagnitude < 0.25f) return FxResult.Played;
        _tracer ??= Find(VisualNames.Join(VisualNames.Game, "FX_Tracer"));
        return _tracer == null ? FxResult.Unavailable : Line(_tracer, from, to);
    }

    private static int _lastKey;

    /// <summary>A hold key nobody else has (<see cref="Hold"/>, <see cref="Line(FxHandle, int, Vector3, Vector3, bool, Color)"/>), always above 0 (0 is a one-shot's, never a
    /// hold's): take one per thing that carries a held effect (a jet, a beacon, a flying round) and keep it as long as the thing lives; a pooled thing that is used again
    /// for a new shot takes a new one, so the new shot never moves the last one's copy.</summary>
    public static int NewKey() => ++_lastKey;

    /// <summary>Holds a sustained effect at a point: a hold with the key of a live copy moves it and keeps it emitting; a copy not held again within
    /// <see cref="FxOptions.HoldSeconds"/> stops emitting, lets its particles finish and is free again. The key comes from <see cref="NewKey"/> (any other is
    /// refused): each key has its own copy, so two things standing next to each other stay two, and a thing that is moving stays one.</summary>
    public static FxResult Hold(FxHandle fx, int key, Vector3 position, Vector3 direction, float size, Color colour)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (fx.IsNothing) return FxResult.Played;
        if (!fx.Sustained) return Refuse(fx, "is not a sustained effect (register it with HoldSeconds)");
        if (key <= 0) return Refuse(fx, "is held and needs a hold key from Fx.NewKey()");
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Hold, fx.Seen, key, position, direction, size, colour));
        return Instances.Play(fx.Prefab!, position, Aim(fx.Prefab, direction), Vector3.one * Scale(fx.Prefab!, size), colour, true, key);
    }

    /// <summary>Ends the held copy of <paramref name="fx"/> under <paramref name="key"/> at once: its particles are cleared and nothing of it lingers (the trigger was let go,
    /// or the thing that carried it turned into something else). A key with no copy still emitting is ignored.</summary>
    public static void Release(FxHandle fx, int key)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (key <= 0) return;
        if (ForgeCore.Recording.Watched) ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Release, fx.Seen, key, default, default, float.NaN, Color.white));
        fx.Prefab?.Release(key);
    }

    /// <summary>A sustained effect covering a round area: copies <see cref="FxOptions.Spacing"/> metres apart, each placed by <paramref name="settle"/> (null: on the
    /// plane through <paramref name="centre"/>; a point it moves more than 2.5 m up or down is left out, it is on another floor), growing out from the centre. Without a spacing,
    /// one copy at the centre at the area's diameter. Make it once per area; <see cref="FxField.Hold"/> every frame the area stands.</summary>
    public static FxField Field(FxHandle fx, Vector3 centre, Vector3 up, float radius, Func<Vector3, Vector3>? settle = null)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        var spacing = fx.Prefab?.Options.Spacing ?? 0f;
        if (spacing <= 0f) return new FxField(fx, new[] { centre }, up, radius * 2f);
        var offsets = FieldPoints.Disc(radius, spacing, fx.Prefab!.Options.Copies);
        var points = new List<Vector3>(offsets.Length / 2);
        for (var i = 0; i < offsets.Length; i += 2)
        {
            var point = centre + new Vector3(offsets[i], 0f, offsets[i + 1]);
            if (settle != null)
            {
                var settled = settle(point);
                if (Mathf.Abs(settled.y - centre.y) > 2.5f) continue;
                point = settled;
            }
            points.Add(point);
        }
        return new FxField(fx, points.ToArray(), up, float.NaN);
    }

    /// <summary>Where a copy of <paramref name="fx"/> played at <paramref name="size"/> facing <paramref name="direction"/> stands so that what it draws (its
    /// <see cref="FxOptions.Body"/>) is not cut by the level: from <paramref name="point"/> (on a floor, or anywhere) it rises until its body clears the floor below; when
    /// the ceiling over that floor is too low for the whole body, it stands between the two and plays smaller by <paramref name="fit"/> (multiply the size by it), so it
    /// touches neither. Without a measured body: the point itself, fit 1. Level geometry only (<c>LayerManager.MASK_WORLD</c>, trigger volumes ignored).</summary>
    public static Vector3 Seat(FxHandle fx, Vector3 point, Vector3 direction, float size, out float fit)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        fit = 1f;
        if (fx.Prefab?.Options.Body is not { } body) return point;
        // the body's reach below and above the copy's root, turned and scaled the way the copy plays
        var rotation = Aim(fx.Prefab, direction);
        var scale = Scale(fx.Prefab, size);
        float low = 0f, high = 0f;
        for (var corner = 0; corner < 8; corner++)
        {
            var y = (rotation * new Vector3((corner & 1) == 0 ? body.min.x : body.max.x, (corner & 2) == 0 ? body.min.y : body.max.y, (corner & 4) == 0 ? body.min.z : body.max.z)).y * scale;
            low = Mathf.Min(low, y);
            high = Mathf.Max(high, y);
        }
        var below = -low;
        var reach = below + high;
        if (reach <= 0.001f) return point;
        // the floor is looked for as far down as the whole body reaches: a point under a ceiling with room below hangs lower at full size; no floor that near, the room below is open
        var floor = ForgeCore.Level.FloorBelow(point, reach, out var under) ? under.point.y : point.y - reach;
        var centre = Mathf.Max(point.y, floor + below);
        var ceiling = Physics.Raycast(point, Vector3.up, out var over, centre - point.y + high, Scene.WorldMask, QueryTriggerInteraction.Ignore) ? over.point.y : float.PositiveInfinity;
        var space = ceiling - floor;
        if (space >= reach) return new Vector3(point.x, Mathf.Min(centre, ceiling - high), point.z);
        fit = Mathf.Max(0.05f, space / reach);
        return new Vector3(point.x, floor + below * fit, point.z);
    }

    private static Quaternion Aim(FxPrefab? prefab, Vector3 direction)
    {
        if (direction.sqrMagnitude <= 1e-8f) return Quaternion.identity;
        return prefab != null && prefab.Options.AxisUp ? Quaternion.FromToRotation(Vector3.up, direction) : Quaternion.LookRotation(direction);
    }

    /// <summary>A copy of a <c>Game:</c> muzzle flash under <paramref name="parent"/> (a gun hand, a muzzle), played with
    /// <see cref="AttachedFx.Play"/>; null (logged once) when the flash is not loaded or <paramref name="fx"/> is not a muzzle flash.</summary>
    public static AttachedFx? Attach(FxHandle fx, Transform parent)
    {
        if (fx?.IsNothing == true) return null;
        if (fx?.Game is not { Source: GameSource.MuzzleFlash } effect)
        {
            Plugin.Logger.ErrorOnce("visual-effect-not-muzzle-flash", "ForgeVisual: " + fx?.Name + " is not a muzzle flash; only those can be attached.");
            return null;
        }
        var flash = MakeFlash(effect, parent);
        return flash == null ? null : new AttachedFx(flash, fx.Seen);
    }

    private static FxResult PlayGame(FxHandle fx, Vector3 position, Quaternion rotation)
    {
        try
        {
            var effect = fx.Game!;
            if (effect.Source == GameSource.MuzzleFlash)
            {
                var flash = NextFlash(fx);
                if (flash == null) return Unavailable(fx);
                flash.transform.SetPositionAndRotation(position, rotation);
                flash.Play();
                return FxResult.Played;
            }
            var pool = PoolOf(fx);
            if (pool == null) return Unavailable(fx);
            var instance = pool.AquireEffect();
            if (instance == null) return Unavailable(fx);
            instance.Play(null, position, rotation);
            return FxResult.Played;
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("visual-game-effect-play-failed", "ForgeVisual: playing the game's effect " + fx.Name + " failed: " + error);
            return FxResult.Unavailable;
        }
    }

    private static FX_Pool? PoolOf(FxHandle fx)
    {
        if (fx.Pool != null && !fx.Pool.WasCollected) return fx.Pool;
        fx.Pool = null;
        var effect = fx.Game!;
        try
        {
            if (effect.Source == GameSource.PreloadedPool)
            {
                fx.Pool = FX_Manager.GetPreloadedEffectPool(effect.Native);
                if (fx.Pool == null && effect.AssetPath != null)
                {
                    var prefab = ForgeCore.GameAssets.Get<GameObject>(effect.AssetPath);
                    if (prefab != null) fx.Pool = FX_Manager.GetEffectPool(prefab);
                }
            }
            else
            {
                var prefab = Resources.Load(effect.Native)?.TryCast<GameObject>();
                if (prefab != null) fx.Pool = FX_Manager.GetEffectPool(prefab);
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-game-effect-lookup-failed", "ForgeVisual: looking up the game's effect " + fx.Name + " failed: " + error); }
        return fx.Pool;
    }

    private const int FlashCopies = 4;

    private static EX_SpriteMuzzleFlash? NextFlash(FxHandle fx)
    {
        var copies = fx.Flashes ??= new EX_SpriteMuzzleFlash?[FlashCopies];
        var i = fx.FlashCursor++ % copies.Length;
        if (fx.FlashCursor > 1 << 20) fx.FlashCursor = 0;
        var flash = copies[i];
        if (flash != null && !flash.WasCollected) return flash;
        flash = MakeFlash(fx.Game!, Root().transform);
        copies[i] = flash;
        return flash;
    }

    private static EX_SpriteMuzzleFlash? MakeFlash(GameEffect effect, Transform parent)
    {
        var prefab = ForgeCore.GameAssets.Get<GameObject>(effect.Native);
        if (prefab == null) return null;
        var instance = Object.Instantiate(prefab, parent, false);
        var flash = instance.GetComponent<EX_SpriteMuzzleFlash>();
        if (flash != null) return flash;
        Object.Destroy(instance);
        Plugin.Logger.ErrorOnce("visual-game-muzzle-flash-missing", "ForgeVisual: the game's " + effect.Name + " prefab has no EX_SpriteMuzzleFlash.");
        return null;
    }

    private static FxResult Unavailable(FxHandle fx)
    {
        Plugin.Logger.ErrorOnce("visual-game-effect-not-loaded", "ForgeVisual: the game's effect " + fx.Name + " is not loaded in this level; it is not drawn.");
        return FxResult.Unavailable;
    }

    private static FxResult Refuse(FxHandle fx, string why)
    {
        Plugin.Logger.ErrorOnce("visual-effect-refused", "ForgeVisual effect " + fx.Name + " " + why + ".");
        return FxResult.Unsupported;
    }

    /// <summary>The length (m) a line prefab is authored along +Z: its <see cref="FxOptions.DefaultSize"/> when set, else one metre.</summary>
    internal static float LineLength(FxPrefab prefab) => float.IsNaN(prefab.Options.DefaultSize) || prefab.Options.DefaultSize <= 0f ? 1f : prefab.Options.DefaultSize;

    private static float Scale(FxPrefab prefab, float size)
        => (float.IsNaN(prefab.Options.DefaultSize) || float.IsNaN(size) || prefab.Options.DefaultSize <= 0f ? 1f : size / prefab.Options.DefaultSize) * prefab.Options.BaseScale;

    internal static GameObject Root()
    {
        if (_root != null && !_root.WasCollected) return _root;
        _root = new GameObject("ForgeVisual effects");
        Object.DontDestroyOnLoad(_root);
        ForgeCore.Assets.Keep(_root);
        return _root;
    }

    internal static void Tick(float now)
    {
        for (var i = 0; i < Prefabs.Count; i++)
        {
            try { Prefabs[i].Tick(now); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("visual-effect-update-failed", "ForgeVisual effect " + Prefabs[i].Name + " failed to update: " + error); }
        }
        try { CullStep(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-effect-cull-failed", "ForgeVisual effect culling failed: " + error); }
    }

    private static int _cullPrefab;
    private static bool _culling;

    /// <summary>One frame's share of the culling pass: up to <see cref="FxCulling.PerFrame"/> playing copies, continuing where the last frame stopped. With culling off (or no eye) every hidden copy is shown once.</summary>
    private static void CullStep()
    {
        var on = FxCulling.Enabled && Scene.HasEye;
        if (!on)
        {
            if (!_culling) return;
            _culling = false;
            for (var i = 0; i < Prefabs.Count; i++) Prefabs[i].ShowAll();
            return;
        }
        _culling = true;
        var budget = FxCulling.PerFrame;
        var visits = Math.Min(Prefabs.Count, FxCulling.PrefabVisits);
        for (var n = 0; n < visits && budget > 0; n++)
        {
            if (_cullPrefab >= Prefabs.Count) _cullPrefab = 0;
            if (Prefabs[_cullPrefab].Cull(ref budget)) _cullPrefab++;
        }
    }

    /// <summary>Level left: the game's pools belong to its level and are looked up again; every pack effect stops.</summary>
    internal static void Clear()
    {
        FxLights.Clear();
        foreach (var handle in GameHandles.Values) handle.Pool = null;
        for (var i = 0; i < Prefabs.Count; i++)
        {
            try { Prefabs[i].Clear(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("visual-effect-clear-failed", "ForgeVisual effect " + Prefabs[i].Name + " failed to clear: " + error); }
        }
    }

    private static class Instances
    {
        internal static FxResult Play(FxPrefab prefab, Vector3 position, Quaternion rotation, Vector3 scale, Color colour, bool hold, int key, float stretch = 1f, bool head = true)
        {
            // An effect that fails never takes the caller's gameplay down with it.
            try { return prefab.Play(position, rotation, scale, colour, hold, key, stretch, head) ? FxResult.Played : FxResult.Unavailable; }
            catch (Exception error)
            {
                Plugin.Logger.ErrorOnce("visual-effect-play-failed", "ForgeVisual effect " + prefab.Name + " failed to play: " + error);
                prefab.Forget();
                return FxResult.Unavailable;
            }
        }
    }
}

/// <summary>A sustained effect standing over a round area (<see cref="Fx.Field"/>).</summary>
public sealed class FxField
{
    /// <summary>Copies started per frame, so a large area spreads from its centre instead of making them all in one frame.</summary>
    private const int Growth = 4;
    private readonly FxHandle _fx;
    private readonly int[] _keys;
    private readonly Vector3[] _points;
    private readonly Vector3 _up;
    private readonly float _size;
    private int _shown;
    private float _started = -1f;

    /// <summary>Above 0: the copies start evenly over this many seconds, so the area visibly spreads out from its centre (gas rolling out, fire catching) instead of standing at once.</summary>
    public float GrowSeconds { get; set; }

    internal FxField(FxHandle fx, Vector3[] points, Vector3 up, float size)
    {
        _fx = fx;
        _points = points;
        _keys = new int[points.Length];
        for (var i = 0; i < _keys.Length; i++) _keys[i] = Fx.NewKey();
        _up = up;
        _size = size;
    }

    /// <summary>Keeps the area's effect alive this frame; not called, it stops and its particles finish.</summary>
    public void Hold()
    {
        if (_started < 0f) _started = Scene.Now;
        var due = GrowSeconds > 0f ? (int)Math.Ceiling(_points.Length * (Scene.Now - _started) / GrowSeconds) : _points.Length;
        _shown = Math.Min(Math.Min(_points.Length, due), _shown + Growth);
        for (var i = 0; i < _shown; i++) Fx.Hold(_fx, _keys[i], _points[i], _up, _size, Color.white);
    }
}

/// <summary>A copy of a game muzzle flash attached to something.</summary>
public sealed class AttachedFx
{
    private readonly EX_SpriteMuzzleFlash _flash;

    private readonly int _seen;

    internal AttachedFx(EX_SpriteMuzzleFlash flash, int seen) { _flash = flash; _seen = seen; }

    public bool Alive => _flash != null && !_flash.WasCollected;
    public Transform? Transform => Alive ? _flash.transform : null;

    /// <summary>Flashes once; reported to <see cref="ForgeCore.Recording"/> as a one-shot of the flash where it stands.</summary>
    public void Play()
    {
        if (!Alive) return;
        if (ForgeCore.Recording.Watched)
        {
            var t = _flash.transform;
            ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Play, _seen, 0, t.position, t.forward, float.NaN, Color.white));
        }
        _flash.Play();
    }

    public void Destroy()
    {
        if (Alive) Object.Destroy(_flash.gameObject);
    }
}

/// <summary>A pack's effect prefab: a fixed set of copies made on first use, placed by each request and recycled once none of their
/// particle systems is alive. Every play has an end (<see cref="HoldBook"/>): a one-shot stops emitting after its window, a sustained copy when it is no longer held.</summary>
internal sealed class FxPrefab
{
    /// <summary>Seconds a prefab with no looping layer may play.</summary>
    private const float MaxPlay = 8f;
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int LineEndId = Shader.PropertyToID("_LineEnd"), LineFadeId = Shader.PropertyToID("_LineFade");
    private readonly GameObject _prefab;
    private readonly float[] _stopped;
    private float _window;
    private readonly HoldBook _book;
    private readonly GameObject?[] _objects;
    private readonly Transform?[] _transforms;
    private readonly ParticleSystem?[] _systems;
    /// <summary>Every renderer of a copy (particles, kept meshes, lines, trails): each takes the caller's colour.</summary>
    private readonly Renderer[]?[] _renderers;
    /// <summary>The copy's trail and line renderers and their authored widths: a width follows the copy's size, a trail is cleared when the copy starts again elsewhere.</summary>
    private readonly TrailRenderer[]?[] _trails;
    private readonly LineRenderer[]?[] _lines;
    private readonly float[]?[] _trailWidths, _lineWidths;
    private readonly MaterialPropertyBlock _block = new();
    private readonly LaserParts?[] _lasers;
    private readonly int[] _lightPlaces, _lightHandles;
    private int _playing;
    // Culling (FxCulling): per copy, whether it stands paused with its renderers off, is never culled, is kept by a hold key, and its bounding sphere.
    private readonly bool[] _hidden, _always, _hold;
    private readonly Vector3[] _centre;
    private readonly float[] _radius;
    /// <summary>The radius of the sphere round a copy at scale 1.</summary>
    private readonly float _reachUnit;
    private int _cullCursor;

    internal FxPrefab(string name, ModelHandle model, FxOptions options)
    {
        Name = name;
        Model = model;
        _prefab = model.Prefab;
        Options = options;
        var copies = Math.Max(1, options.Copies);
        _hidden = new bool[copies];
        _always = new bool[copies];
        _hold = new bool[copies];
        _centre = new Vector3[copies];
        _radius = new float[copies];
        var nominal = float.IsNaN(options.DefaultSize) || options.DefaultSize <= 0f ? 4f : options.DefaultSize;
        _reachUnit = options.Body is { } body ? body.center.magnitude + body.extents.magnitude : nominal * 0.5f;
        _book = new HoldBook(copies);
        _objects = new GameObject?[copies];
        _transforms = new Transform?[copies];
        _systems = new ParticleSystem?[copies];
        _renderers = new Renderer[copies][];
        _trails = new TrailRenderer[copies][];
        _lines = new LineRenderer[copies][];
        _trailWidths = new float[copies][];
        _lineWidths = new float[copies][];
        _stopped = new float[copies];
        _lasers = new LaserParts?[copies];
        _lightPlaces = new int[copies];
        _lightHandles = new int[copies];
    }

    internal string Name { get; }
    internal ModelHandle Model { get; }
    internal FxOptions Options { get; }

    internal bool Play(Vector3 position, Quaternion rotation, Vector3 scale, Color colour, bool hold, int key, float stretch = 1f, bool head = true)
    {
        var now = Scene.Now;
        if (hold)
        {
            var held = _book.Find(key);
            if (held >= 0 && Ready(held))
            {
                _book.Extend(held, now, Options.HoldSeconds);
                Place(held, position, rotation, scale, colour, stretch, head);
                if (!_hidden[held]) Light(held, position, scale, colour, true, now);
                return true;
            }
        }
        var copy = _book.Take(_hold, hold);
        if (copy < 0) { Plugin.Logger.ErrorOnce("visual-effect-copies-all-held", "ForgeVisual effect " + Name + " has all " + _book.Copies + " copies held by other keys; a further hold was not played (raise the effect's Copies)."); return false; }
        if (!Ready(copy) && !Make(copy)) return false;
        var wasPlaying = _book.Playing(copy);
        _book.Start(copy, key, now, hold ? Options.HoldSeconds : _window);
        _stopped[copy] = 0f;
        _hold[copy] = hold;
        if (_hidden[copy]) Show(copy, false);
        Place(copy, position, rotation, scale, colour, stretch, head);
        // a copy is checked against its sphere from its first frame on; a large one (a blast, an air strike) is seen from afar
        _always[copy] = Options.Line || (Options.AlwaysDraw ?? _radius[copy] >= FxCulling.LargeReach);
        var o = _objects[copy]!;
        var system = _systems[copy]!;
        o.SetActive(true);
        system.Clear(true);
        system.Play(true);
        var trails = _trails[copy]!;
        for (var i = 0; i < trails.Length; i++) trails[i].Clear(); // a reused copy starts here, not with a streak from where it was last
        _lightPlaces[copy] = -1;
        _lightHandles[copy] = 0;
        Light(copy, position, scale, colour, hold, now);
        if (!wasPlaying) _playing++;
        return true;
    }

    /// <summary>The slot's light (<see cref="FxOptions.Light"/>) at the copy: flashed once by a one-shot, kept by a hold. Its reach follows the size the copy plays at (a line's stays as authored).</summary>
    private void Light(int copy, Vector3 position, Vector3 scale, Color colour, bool hold, float now)
    {
        if (Options.Light is not { } spec) return;
        var reach = Options.Line ? 1f : scale.x / Options.BaseScale;
        if (hold) FxLights.Hold(spec, ref _lightPlaces[copy], ref _lightHandles[copy], position, colour, reach, now);
        else FxLights.Flash(spec, Options.LightDelay, position, colour, reach, now);
    }

    private void Place(int copy, Vector3 position, Quaternion rotation, Vector3 scale, Color colour, float stretch, bool head)
    {
        var t = _transforms[copy]!;
        t.SetPositionAndRotation(position - rotation * (Options.Centre * scale.x), rotation);
        t.localScale = scale;
        _centre[copy] = position;
        _radius[copy] = _reachUnit * Mathf.Abs(scale.x);
        _lasers[copy]?.Stretch(stretch, Fx.LineLength(this), head);
        _block.SetColor(ColorId, colour);
        if (Options.EndFade > 0f)
        {
            // a line that hit nothing fades out over its last metres (the shaders fade by the distance to its end); one that hit keeps its end
            var length = _lasers[copy] != null ? stretch * Fx.LineLength(this) * scale.x : scale.z * Fx.LineLength(this);
            _block.SetVector(LineEndId, position + rotation * new Vector3(0f, 0f, length));
            _block.SetFloat(LineFadeId, head ? 0f : Options.EndFade);
        }
        var renderers = _renderers[copy]!;
        for (var i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(_block);
        if (Options.LaserTile > 0f) _lasers[copy]?.Tile(stretch * Fx.LineLength(this) * scale.x / Options.LaserTile);
        // trail and line widths are in metres whatever the transform's scale: they follow the copy's width here
        Widen(_trails[copy]!, _trailWidths[copy]!, scale.x);
        Widen(_lines[copy]!, _lineWidths[copy]!, scale.x);
    }

    private static void Widen(TrailRenderer[] trails, float[] authored, float width)
    {
        for (var i = 0; i < trails.Length; i++) trails[i].widthMultiplier = authored[i] * width;
    }

    private static void Widen(LineRenderer[] lines, float[] authored, float width)
    {
        for (var i = 0; i < lines.Length; i++) lines[i].widthMultiplier = authored[i] * width;
    }

    /// <summary>The copy still exists on the Unity side: `!=` is Unity's check, which sees an object destroyed while its managed wrapper lives on. A prefab whose Stop Action is Destroy
    /// ends its own copy that way (the game's interop has no setter for it, so the converter clears it in the bundle); a dead copy is made again on its next play.</summary>
    private bool Ready(int copy) => _objects[copy] != null && !_objects[copy]!.WasCollected && _systems[copy] != null;

    /// <summary>A play failed part way: copies with a destroyed layer are made again on their next use.</summary>
    internal void Forget()
    {
        for (var i = 0; i < _objects.Length; i++)
        {
            var renderers = _renderers[i];
            if (renderers == null || !Ready(i)) continue;
            for (var r = 0; r < renderers.Length; r++)
            {
                if (renderers[r] != null) continue;
                Object.Destroy(_objects[i]);
                _objects[i] = null;
                if (_book.Playing(i)) { _book.Finished(i); _playing--; }
                break;
            }
        }
    }

    private bool Make(int copy)
    {
        GameObject? o = null;
        try
        {
            o = Object.Instantiate(_prefab, Fx.Root().transform, false);
            o.name = Name;
            if (_window <= 0f) _window = Options.Seconds > 0f ? Options.Seconds : Math.Min(FxLayers.Longest(o.GetComponentsInChildren<ParticleSystem>(true)), MaxPlay);
            o.SetActive(false);
            _objects[copy] = o;
            _transforms[copy] = o.transform;
            _systems[copy] = o.GetComponent<ParticleSystem>();
            _renderers[copy] = o.GetComponentsInChildren<Renderer>(true);
            var trails = _trails[copy] = o.GetComponentsInChildren<TrailRenderer>(true);
            var lines = _lines[copy] = o.GetComponentsInChildren<LineRenderer>(true);
            var trailWidths = _trailWidths[copy] = new float[trails.Length];
            var lineWidths = _lineWidths[copy] = new float[lines.Length];
            for (var i = 0; i < trails.Length; i++) trailWidths[i] = trails[i].widthMultiplier;
            for (var i = 0; i < lines.Length; i++) lineWidths[i] = lines[i].widthMultiplier;
            if (Options.LaserBodies != null) _lasers[copy] = new LaserParts(o.transform, Options, Name);
            if (_book.Playing(copy)) { _book.Finished(copy); _playing--; }
            _hidden[copy] = false;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("visual-effect-make-failed", "ForgeVisual effect " + Name + " could not be made: " + error);
            // A copy that failed part way is not ready: the next play makes it again instead of playing it without its laser.
            if (o != null) Object.Destroy(o);
            _objects[copy] = null;
            return false;
        }
    }

    internal void Tick(float now)
    {
        if (_playing == 0) return;
        for (var i = 0; i < _objects.Length; i++)
        {
            if (!_book.Playing(i)) continue;
            var system = _systems[i]!;
            if (!Ready(i)) { _book.Finished(i); _playing--; continue; }
            if (_hidden[i])
            {
                // Paused out of view: the clock runs on, and a copy whose window is over ends where it stands (nothing to resume or replay on a turn).
                if (!_book.Expired(i, now)) continue;
                system.Clear(true);
                _objects[i]!.SetActive(false);
                _book.Finished(i);
                _playing--;
                continue;
            }
            if (_book.Expired(i, now))
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                _stopped[i] = now;
            }
            if (system.IsAlive(true) && (_stopped[i] <= 0f || now - _stopped[i] < Options.Linger)) continue;
            system.Clear(true);
            _objects[i]!.SetActive(false);
            _book.Finished(i);
            _playing--;
        }
    }

    /// <summary>Checks this effect's playing copies from where the last pass stopped, at most <paramref name="budget"/> of them (which it reduces);
    /// true when every copy has been checked once, so the next frame moves on to the next effect.</summary>
    internal bool Cull(ref int budget)
    {
        if (_playing == 0) { _cullCursor = 0; return true; }
        var eye = Scene.Eye;
        var forward = Scene.EyeForward;
        var cone = Scene.TanCone;
        var limit = Options.CullDistance > 0f ? Options.CullDistance : FxCulling.Distance;
        while (_cullCursor < _objects.Length)
        {
            if (budget <= 0) return false;
            var copy = _cullCursor++;
            if (!_book.Playing(copy) || !_book.Emitting(copy) || !Ready(copy)) continue;
            budget--;
            var c = _centre[copy];
            var hide = !_always[copy] && CullMath.Hidden(_hidden[copy], c.x - eye.x, c.y - eye.y, c.z - eye.z, _radius[copy], limit, forward.x, forward.y, forward.z, cone);
            if (hide == _hidden[copy]) continue;
            if (hide) Hide(copy);
            else Show(copy, true);
        }
        _cullCursor = 0;
        return true;
    }

    /// <summary>Culling switched off: every copy that stands hidden comes back.</summary>
    internal void ShowAll()
    {
        for (var i = 0; i < _objects.Length; i++)
            if (_hidden[i] && _book.Playing(i) && _book.Emitting(i) && Ready(i)) Show(i, true);
    }

    private void Hide(int copy)
    {
        _hidden[copy] = true;
        _systems[copy]!.Pause(true);
        SetDrawn(copy, false);
    }

    /// <summary>The copy is drawn again; <paramref name="resume"/> lets a paused copy run on from where it stopped (a copy about to be played from the start is not resumed).</summary>
    private void Show(int copy, bool resume)
    {
        _hidden[copy] = false;
        SetDrawn(copy, true);
        if (resume) _systems[copy]!.Play(true);
    }

    private void SetDrawn(int copy, bool on)
    {
        var renderers = _renderers[copy]!;
        for (var i = 0; i < renderers.Length; i++) if (renderers[i] != null) renderers[i].enabled = on;
    }

    internal void Release(int key)
    {
        var copy = _book.Find(key);
        if (copy < 0) return;
        _book.Finished(copy);
        _playing--;
        if (!Ready(copy)) return;
        _systems[copy]!.Clear(true);
        _objects[copy]!.SetActive(false);
    }

    internal void Clear()
    {
        for (var i = 0; i < _objects.Length; i++)
        {
            if (!_book.Playing(i)) continue;
            _book.Finished(i);
            if (Ready(i))
            {
                _systems[i]!.Clear(true);
                _objects[i]!.SetActive(false);
            }
        }
        _playing = 0;
    }
}

/// <summary>The head and bodies of one copy of a laser line (<see cref="FxOptions.LaserBodies"/>), found once when the copy is made. Placing them looks nothing up and allocates nothing.</summary>
internal sealed class LaserParts
{
    private readonly Transform _head;
    private readonly ParticleSystemRenderer[] _bodies;
    private readonly float[] _lengthScale;
    private readonly bool[] _billboard;
    private readonly ParticleSystemRenderer[] _headRenderers;
    private readonly MaterialPropertyBlock _tiling = new();
    private static readonly int TilingId = Shader.PropertyToID("_MainTex_ST");
    private bool _headShown = true;

    internal LaserParts(Transform copy, FxOptions options, string effect)
    {
        _head = copy.Find(options.LaserHead) ?? throw new InvalidOperationException(effect + " has no laser head " + options.LaserHead);
        _headRenderers = _head.GetComponentsInChildren<ParticleSystemRenderer>(true);
        var names = options.LaserBodies!;
        _bodies = new ParticleSystemRenderer[names.Length];
        _lengthScale = new float[names.Length];
        _billboard = new bool[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var body = copy.Find(names[i]) ?? throw new InvalidOperationException(effect + " has no laser body " + names[i]);
            _bodies[i] = body.GetComponent<ParticleSystemRenderer>() ?? throw new InvalidOperationException(effect + " laser body " + names[i] + " has no particle renderer");
            _lengthScale[i] = _bodies[i].lengthScale;
            _billboard[i] = _bodies[i].renderMode == ParticleSystemRenderMode.Stretch;
        }
    }

    /// <summary><paramref name="stretch"/> is the line's length over its authored length, in the copy's own units: the copy's uniform scale does the rest.</summary>
    internal void Stretch(float stretch, float authored, bool head)
    {
        if (head != _headShown)
        {
            _headShown = head;
            for (var i = 0; i < _headRenderers.Length; i++) _headRenderers[i].enabled = head;
        }
        _head.localPosition = new Vector3(0f, 0f, stretch * authored);
        for (var i = 0; i < _bodies.Length; i++)
        {
            if (_billboard[i]) _bodies[i].lengthScale = _lengthScale[i] * stretch;
            else
            {
                var t = _bodies[i].transform;
                var kept = t.localScale;
                t.localScale = new Vector3(stretch, kept.y, kept.z);
            }
        }
    }

    /// <summary>The bodies repeat their texture <paramref name="repeats"/> times along the line, on top of the colour block just set; each line starts somewhere else in the texture.</summary>
    internal void Tile(float repeats)
    {
        for (var i = 0; i < _bodies.Length; i++)
        {
            _bodies[i].GetPropertyBlock(_tiling);
            _tiling.SetVector(TilingId, new Vector4(Mathf.Max(1f, Mathf.Round(repeats)), 1f, repeats * 0.61f + i * 0.37f, 0f));
            _bodies[i].SetPropertyBlock(_tiling);
        }
    }
}

/// <summary>What the game's build leaves of a prefab's layers. MainModule keeps only the duration, start delay, start colour and a few setters: the legacy ParticleSystem.loop and
/// scalingMode compile against the interop and throw MissingMethodException in the game (Tools/Forge/check-interop-members.py scans for that). So how a layer scales and whether it loops are fixed in the
/// prefab: the converter (Tools/Vfx) puts every library layer on Hierarchy scaling; a prefab from elsewhere keeps the scaling its author gave it, and a Local layer stays its authored size however the copy is placed.</summary>
internal static class FxLayers
{
    /// <summary>Seconds until the last layer has done one round, start delay included.</summary>
    internal static float Longest(ParticleSystem[] layers)
    {
        var longest = 0f;
        foreach (var layer in layers)
        {
            var main = layer.main;
            longest = Math.Max(longest, main.duration + main.startDelay.constantMax);
        }
        return longest;
    }
}
