using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>A texture sprites are cut from: one still image or a flipbook of <see cref="Columns"/> x <see cref="Rows"/> cells (counted
/// from the top left). Registered by name (<c>Pack:sheet</c>) so any mod can draw with it; <c>Forge:soft-dot</c> and
/// <c>Forge:soft-line</c> are made in code by this library.</summary>
public sealed class SpriteSheet
{
    private static readonly NameTable<SpriteSheet> All = new();
    private static SpriteSheet? _softDot, _softLine;
    private readonly Texture2D? _texture;
    private readonly Func<Texture2D>? _make;
    private Texture2D? _made;
    private Material? _alpha, _additive;
    private Mesh[]? _cells;

    private SpriteSheet(string name, Texture2D? texture, Func<Texture2D>? make, int columns, int rows, int frames, FxBlend blend, bool sheddable)
    {
        Name = name;
        _texture = texture;
        _make = make;
        Columns = columns;
        Rows = rows;
        Frames = frames;
        Blend = blend;
        Sheddable = sheddable;
        Seen = ForgeCore.RecordingNames.Of(name);
    }

    public string Name { get; }

    /// <summary>The name's number for reports to <see cref="ForgeCore.Recording"/>.</summary>
    internal readonly int Seen;
    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Cells in use (the last row may be partly empty).</summary>
    public int Frames { get; }

    /// <summary>How sprites of this sheet blend by default.</summary>
    public FxBlend Blend { get; }

    /// <summary>Sprites of this sheet (smoke) are given up first when the pool's overdraw budget is exceeded.</summary>
    public bool Sheddable { get; }

    /// <summary>A soft round spot, white, drawn additively.</summary>
    public static SpriteSheet SoftDot => _softDot ??= BuiltIn(ForgeCatalog.SoftDot, Shaders.SoftDot);

    /// <summary>A soft band (opaque along U, soft across V), white, drawn additively: ribbons and line glows.</summary>
    public static SpriteSheet SoftLine => _softLine ??= BuiltIn(ForgeCatalog.SoftLine, Shaders.SoftLine);

    /// <summary>Registers <paramref name="texture"/> as <paramref name="name"/> (<c>Pack:sheet</c>); <paramref name="frames"/> 0 uses
    /// every cell. Null (logged once) when the name is bad or taken or the grid does not fit.</summary>
    public static SpriteSheet? Register(string modId, string name, Texture2D texture, int columns, int rows, FxBlend blend, bool sheddable = false, int frames = 0)
    {
        var log = ForgeCore.Log.For(string.IsNullOrEmpty(modId) ? Plugin.Id : modId);
        if (texture == null) { log.ErrorOnce("visual-sprite-sheet-not-registered-no-texture", "ForgeVisual sprite sheet " + name + " not registered: no texture."); return null; }
        if (columns < 1 || rows < 1 || frames < 0 || frames > columns * rows) { log.ErrorOnce("visual-sprite-sheet-not-registered-x-grid", "ForgeVisual sprite sheet " + name + " not registered: a " + columns + " x " + rows + " grid with " + frames + " frames does not fit."); return null; }
        if (VisualNames.TrySplit(name, out var pack, out _, out _) && VisualNames.CheckPack(pack) is { } bad) { log.ErrorOnce("visual-sprite-sheet-not-registered", "ForgeVisual sprite sheet " + name + " not registered: " + bad + "."); return null; }
        ForgeCore.Assets.Keep(texture);
        var sheet = new SpriteSheet(name, texture, null, columns, rows, frames == 0 ? columns * rows : frames, blend, sheddable);
        if (All.Add(name, sheet) is { } why) { log.ErrorOnce("visual-sprite-sheet-not-registered", "ForgeVisual sprite sheet " + name + " not registered: " + why + "."); return null; }
        return sheet;
    }

    /// <summary>The sheet <paramref name="name"/>, or null (logged once).</summary>
    public static SpriteSheet? Get(string name)
    {
        if (name == VisualNames.Join(VisualNames.Forge, ForgeCatalog.SoftDot)) return SoftDot;
        if (name == VisualNames.Join(VisualNames.Forge, ForgeCatalog.SoftLine)) return SoftLine;
        if (All.TryGet(name, out var sheet)) return sheet;
        if (Missing.First("sprite sheet", name)) Plugin.Logger.ErrorOnce("visual-has-no-sprite-sheet-sprite-sheet", "ForgeVisual has no sprite sheet " + name + ": " + Missing.Why(name, p => p == VisualNames.Forge || SheetPack(p), "sprite sheet") + ".");
        return null;
    }

    private static bool SheetPack(string pack)
    {
        foreach (var n in All.Names)
            if (n.StartsWith(pack + ":", StringComparison.Ordinal)) return true;
        return false;
    }

    private static SpriteSheet BuiltIn(string item, Func<Texture2D> make)
        => new(VisualNames.Join(VisualNames.Forge, item), null, make, 1, 1, 1, FxBlend.Additive, false);

    internal Texture2D Texture
    {
        get
        {
            if (_texture != null) return _texture;
            if (_made == null || _made.WasCollected) _made = _make!();
            return _made;
        }
    }

    internal Material? Material(FxBlend blend)
    {
        ref var slot = ref blend == FxBlend.Additive ? ref _additive : ref _alpha;
        if (slot != null && !slot.WasCollected) return slot;
        return slot = Shaders.Make(Texture, blend, Name + (blend == FxBlend.Additive ? " additive" : " alpha"));
    }

    internal Mesh[] Cells
    {
        get
        {
            if (_cells != null && (_cells.Length == 0 || !_cells[0].WasCollected)) return _cells;
            var cells = new Mesh[Frames];
            for (var i = 0; i < cells.Length; i++) cells[i] = Shaders.Cell(Columns, Rows, i, Name + "#" + i);
            return _cells = cells;
        }
    }
}

/// <summary>How a sprite's quad is turned.</summary>
public enum SpriteFace : byte
{
    /// <summary>Toward the camera.</summary>
    Camera,
    /// <summary>Flat on a surface (<see cref="SpriteSpawn.Normal"/>).</summary>
    Floor,
    /// <summary>Stretched along its motion with its head leading, turned toward the camera.</summary>
    Streak,
}

/// <summary>Called when a sprite's life ends (for example to burst into another effect there).</summary>
public delegate void SpriteEnd(Vector3 position, float scale);

/// <summary>Everything one sprite does. Start from <see cref="Of"/> and set what differs; passed by reference, nothing allocates.</summary>
public struct SpriteSpawn
{
    public SpriteSheet Sheet;
    public SpriteFace Face;
    public FxBlend Blend;
    public Vector3 Position, Velocity;
    /// <summary>The surface a <see cref="SpriteFace.Floor"/> sprite lies on.</summary>
    public Vector3 Normal;
    public float Life, Size0, Size1;
    public Color Colour0, Colour1;
    /// <summary>Seconds before it appears.</summary>
    public float Delay;
    /// <summary>Speed lost per second (fraction) and fall acceleration (m/s²).</summary>
    public float Drag, Gravity;
    /// <summary>Turn rate (radians per second) and starting turn (NaN: random).</summary>
    public float Spin, Rotation;
    /// <summary>A still cell (-1: random); ignored while flipping.</summary>
    public int Cell;
    /// <summary>Play the sheet's frames: over the life (<see cref="FlipFps"/> 0) or looping at that rate; <see cref="FlipFrames"/> 0 = all.</summary>
    public bool Flip;
    public float FlipFps;
    public int FlipFrames;
    /// <summary>Seconds the flipbook is already along when the sprite starts (sprites of one cloud need not show the same frame).</summary>
    public float FlipPhase;
    /// <summary>A streak's length per size, and its extra length per m/s of speed.</summary>
    public float Aspect, Stretch;
    /// <summary>Fraction of the life before the fade starts, and the fade-in time (s).</summary>
    public float Hold, FadeIn;
    /// <summary>Who wins when the pool is full: a request takes over the live sprite of the lowest priority below its own.</summary>
    public int Priority;
    /// <summary>Called at the end of its life, with <see cref="EndScale"/>.</summary>
    public SpriteEnd? OnEnd;
    public float EndScale;

    public static SpriteSpawn Of(SpriteSheet sheet, SpriteFace face, Vector3 position, float life, float size0, float size1, Color colour0, Color colour1) => new()
    {
        Sheet = sheet,
        Face = face,
        Blend = sheet.Blend,
        Position = position,
        Normal = Vector3.up,
        Life = life,
        Size0 = size0,
        Size1 = size1,
        Colour0 = colour0,
        Colour1 = colour1,
        Rotation = float.NaN,
        Cell = -1,
        Aspect = 1f,
        FadeIn = 0.06f,
        EndScale = 1f,
    };
}

/// <summary>One live sprite, for a caller that keeps moving it (an attached flame). Stale once the sprite has ended or was taken over.</summary>
public readonly struct Sprite
{
    internal readonly int Slot, Generation;
    internal readonly DecalRing? Ring;

    internal Sprite(int slot, int generation, DecalRing? ring)
    {
        Slot = slot;
        Generation = generation;
        Ring = ring;
    }

    public static readonly Sprite None = new(-1, 0, null);

    public bool Alive => Slot >= 0 && SpritePool.IsCurrent(this);
}

internal sealed class SpriteParticle
{
    internal Transform T = null!;
    internal MeshRenderer R = null!;
    internal MeshFilter F = null!;
    internal bool Live, Shown, Attached, Drawn;
    internal SpriteSheet Sheet = null!;
    internal SpriteFace Face;
    internal Vector3 Pos, Vel, Normal;
    internal float Drag, Gravity, Spin, Rot, Born, Last, Life, T0, FlipFps, Size0, Size1, Aspect, Stretch, Hold, FadeIn, Cut, EndScale;
    internal int FlipCount, Cell;
    internal Color C0, C1;
    internal SpriteEnd? OnEnd;
}

/// <summary>
/// The shared pool of camera-facing, floor and streak sprites every mod draws into: <see cref="Capacity"/> quads made once (parked
/// disabled), one material per sheet and blend, tints through one property block. A full pool gives a request the slot of a lower
/// priority sprite, or drops it. The step touches
/// only live sprites, allocates nothing and looks nothing up. Sheddable sprites (smoke) fade out lowest first when the whole pool's
/// overdraw passes <see cref="Overdraw.ScreenBudget"/> screens. Long-lived floor marks use their own <see cref="DecalRing"/>s.
/// </summary>
public static class SpritePool
{
    public const int Capacity = 256;
    private const float InvisibleAlpha = 0.004f;
    private static readonly SpriteParticle[] All = new SpriteParticle[Capacity];
    private static readonly SlotBook Book = new(Capacity);
    private static readonly FxRandom Random = new(0x9E3779B9u);
    private static MaterialPropertyBlock? _block;
    private static int _colorId;
    private static bool _ready, _failed;
    private static GameObject? _root;
    private static float _shed, _covTotal, _covShed;
    private static DecalRing?[] _rings = new DecalRing?[4];
    private static int _ringCount;

    public static int Live => Book.LiveCount;

    /// <summary>A random number in [0, 1) from the pool's own source (cheap; for recipes).</summary>
    public static float Rnd() => Random.Next();

    public static float Range(float low, float high) => Random.Range(low, high);

    /// <summary>Starts a sprite; <see cref="Sprite.None"/> when the pool is full or the shaders are missing.</summary>
    public static Sprite Emit(in SpriteSpawn s)
    {
        if (!Ready() || s.Sheet == null) return Sprite.None;
        var slot = Book.Take(s.Priority, false, out var replaced);
        if (slot < 0) return Sprite.None;
        var p = All[slot];
        if (replaced) Hide(p);
        Start(p, in s, Scene.Now);
        return new Sprite(slot, Book.Generation(slot), null);
    }

    /// <summary>Starts a sprite that does not age and stays where it is put (<see cref="Move"/>) until <see cref="Kill"/>: a flame
    /// or glow riding on something. It is never taken over by another request.</summary>
    public static Sprite Attach(in SpriteSpawn s)
    {
        if (!Ready() || s.Sheet == null) return Sprite.None;
        var slot = Book.Take(int.MaxValue, true, out var replaced);
        if (slot < 0) return Sprite.None;
        var p = All[slot];
        if (replaced) Hide(p);
        Start(p, in s, Scene.Now);
        p.Attached = true;
        return new Sprite(slot, Book.Generation(slot), null);
    }

    public static void Move(Sprite sprite, Vector3 position, Vector3 velocity)
    {
        if (!TryGet(sprite, out var p)) return;
        p.Pos = position;
        p.Vel = velocity;
    }

    public static void Resize(Sprite sprite, float size, float aspect)
    {
        if (!TryGet(sprite, out var p)) return;
        p.Size0 = p.Size1 = size;
        p.Aspect = aspect;
    }

    /// <summary>Sets the tint (and its alpha) of an attached sprite, which does not age: a flickering flame, a fading puff.</summary>
    public static void Recolour(Sprite sprite, Color colour)
    {
        if (!TryGet(sprite, out var p)) return;
        p.C0 = p.C1 = colour;
    }

    /// <summary>Sets the turn (radians) of a camera-facing or floor sprite; an attached sprite does not spin by itself.</summary>
    public static void Turn(Sprite sprite, float radians)
    {
        if (!TryGet(sprite, out var p)) return;
        p.Rot = radians;
    }

    public static void Kill(Sprite sprite)
    {
        if (!TryGet(sprite, out var p)) return;
        if (sprite.Ring != null) { sprite.Ring.Kill(sprite.Slot); return; }
        Hide(p);
        Book.Free(sprite.Slot);
    }

    /// <summary>A set of <paramref name="capacity"/> long-lived marks on surfaces (scorches, burns, holes) for one mod: when full the
    /// oldest is replaced, so they never starve the shared sprites and never grow. Make it at load, not in the frame of the first mark.</summary>
    public static DecalRing? CreateDecals(int capacity)
    {
        if (!Ready()) return null;
        var ring = new DecalRing(capacity, _root!.transform);
        if (_ringCount == _rings.Length) Array.Resize(ref _rings, _ringCount * 2);
        _rings[_ringCount++] = ring;
        return ring;
    }

    /// <summary>Draws one faint, tiny sprite of each given sheet in front of the camera for a moment, so the first real effect does not
    /// pay for the GPU's first use of each material.</summary>
    public static void Warm(Vector3 at, params SpriteSheet[] sheets)
    {
        if (!Ready()) return;
        var faint = new Color(1f, 1f, 1f, 0.03f);
        foreach (var sheet in sheets)
        {
            if (sheet == null) continue;
            var s = SpriteSpawn.Of(sheet, SpriteFace.Camera, at, 0.3f, 0.05f, 0.05f, faint, faint);
            s.Cell = 0;
            s.Priority = int.MinValue;
            Emit(in s);
        }
    }

    internal static bool IsCurrent(Sprite sprite)
    {
        if (sprite.Ring != null) return sprite.Ring.IsCurrent(sprite);
        return _ready && sprite.Slot < Capacity && Book.Live(sprite.Slot) && Book.Generation(sprite.Slot) == sprite.Generation;
    }

    private static bool TryGet(Sprite sprite, out SpriteParticle p)
    {
        p = null!;
        if (sprite.Slot < 0 || !IsCurrent(sprite)) return false;
        p = sprite.Ring != null ? sprite.Ring.At(sprite.Slot) : All[sprite.Slot];
        return true;
    }

    internal static SpriteParticle MakeParticle(Transform parent)
    {
        var go = new GameObject("sprite");
        go.transform.SetParent(parent, false);
        var filter = go.AddComponent<MeshFilter>();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.enabled = false;
        return new SpriteParticle { T = go.transform, R = renderer, F = filter };
    }

    private static bool Ready()
    {
        if (_ready && _root != null && !_root.WasCollected) return true;
        if (_failed) return false;
        if (_ready) Rebuild();
        if (Shaders.Particle == null) { _failed = true; return false; }
        _root = new GameObject("ForgeVisual sprites");
        Object.DontDestroyOnLoad(_root);
        ForgeCore.Assets.Keep(_root);
        for (var i = 0; i < Capacity; i++) All[i] = MakeParticle(_root.transform);
        _colorId = Shader.PropertyToID("_Color");
        _block = new MaterialPropertyBlock();
        _ready = true;
        return true;
    }

    /// <summary>The pool's objects were destroyed with a scene: forget them, and the next request builds them again.</summary>
    private static void Rebuild()
    {
        _ready = false;
        Book.Clear();
        _ringCount = 0;
    }

    internal static void Start(SpriteParticle p, in SpriteSpawn s, float now)
    {
        var cells = s.Sheet.Cells;
        var born = now + s.Delay;
        p.Live = true;
        p.Shown = p.Drawn = p.Attached = false;
        p.Sheet = s.Sheet;
        p.Face = s.Face;
        p.Pos = s.Position;
        p.Vel = s.Velocity;
        p.Normal = s.Normal.sqrMagnitude > 1e-6f ? s.Normal : Vector3.up;
        p.Drag = s.Drag;
        p.Gravity = s.Gravity;
        p.Spin = s.Spin;
        p.Rot = float.IsNaN(s.Rotation) ? Random.Next() * 6.2831853f : s.Rotation;
        p.Born = p.Last = born;
        p.T0 = born - s.FlipPhase;
        p.Life = Math.Max(0.01f, s.Life);
        p.Size0 = s.Size0;
        p.Size1 = s.Size1;
        p.Aspect = s.Aspect;
        p.Stretch = s.Stretch;
        p.Hold = s.Hold;
        p.FadeIn = s.FadeIn;
        p.C0 = s.Colour0;
        p.C1 = s.Colour1;
        p.Cut = Random.Next();
        p.OnEnd = s.OnEnd;
        p.EndScale = s.EndScale;
        p.R.sharedMaterial = s.Sheet.Material(s.Blend);
        if (s.Flip)
        {
            p.FlipCount = s.FlipFrames > 0 ? Math.Min(s.FlipFrames, cells.Length) : cells.Length;
            p.FlipFps = s.FlipFps;
            p.Cell = 0;
        }
        else
        {
            p.FlipCount = 0;
            p.FlipFps = 0f;
            p.Cell = s.Cell >= 0 ? s.Cell % cells.Length : (int)(Random.Next() * cells.Length) % cells.Length;
        }
        p.F.sharedMesh = cells[p.Cell];
        p.R.enabled = false;
    }

    private static void Hide(SpriteParticle p)
    {
        p.Live = p.Shown = p.Drawn = false;
        if (p.R != null && !p.R.WasCollected) p.R.enabled = false;
    }

    internal static void Tick(float now)
    {
        if (!_ready) return;
        if (_root == null || _root.WasCollected) { Rebuild(); return; }
        if (Book.LiveCount == 0 && _ringCount == 0) { _covTotal = _covShed = _shed = 0f; return; }
        _covTotal = _covShed = 0f;
        if (Book.LiveCount > 0)
            for (var i = 0; i < Capacity; i++)
            {
                var p = All[i];
                if (!p.Live) continue;
                if (Step(p, now)) continue;
                Book.Free(i);
                Ended(p);
            }
        for (var r = 0; r < _ringCount; r++) _rings[r]!.Tick(now);
        // The next frame's smoke to give up: what is covered beyond the budget (one frame late, so the decision cannot flicker).
        _shed = Overdraw.Shed(_covTotal, _covShed, Overdraw.ScreenBudget);
    }

    /// <summary>Tells the owner of a sprite that <see cref="Step"/> ended, after its slot was given back (so what it emits in turn can take that slot).
    /// A callback that throws is logged and costs nothing else.</summary>
    internal static void Ended(SpriteParticle p)
    {
        var end = p.OnEnd;
        if (end == null) return;
        try { end(p.Pos, p.EndScale); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-sprite-end-failed", "A ForgeVisual sprite's OnEnd callback failed: " + error); }
    }

    /// <summary>One frame of one sprite; false when it has ended.</summary>
    internal static bool Step(SpriteParticle p, float now)
    {
        var age = now - p.Born;
        if (age < 0f) return true;
        if (p.Attached) { p.Born = now; age = 0f; }
        else if (age >= p.Life)
        {
            Hide(p);
            return false;
        }
        if (!p.Shown) { p.Shown = true; p.Last = now; }
        var dt = Mathf.Clamp(now - p.Last, 0f, 0.1f);
        p.Last = now;
        if (!p.Attached)
        {
            if (p.Drag > 0f) p.Vel *= Math.Max(0f, 1f - p.Drag * dt);
            p.Vel.y -= p.Gravity * dt;
            p.Pos += p.Vel * dt;
        }
        var k = p.Attached ? 0f : age / p.Life;
        var ease = 1f - (1f - k) * (1f - k);
        var size = p.Size0 + (p.Size1 - p.Size0) * ease;
        var ramp = k < p.Hold ? 0f : (k - p.Hold) / Math.Max(0.0001f, 1f - p.Hold);
        var fadeIn = p.FadeIn > 0f && !p.Attached ? Math.Min(1f, age / p.FadeIn) : 1f;
        var colour = new Color(p.C0.r + (p.C1.r - p.C0.r) * k, p.C0.g + (p.C1.g - p.C0.g) * k, p.C0.b + (p.C1.b - p.C0.b) * k,
            (p.C0.a + (p.C1.a - p.C0.a) * ramp) * fadeIn);
        var eye = Scene.Eye;
        if (colour.a > InvisibleAlpha && p.Face == SpriteFace.Camera)
        {
            var coverage = Overdraw.Coverage(size, (p.Pos - eye).magnitude, Scene.TanHalfFov);
            _covTotal += coverage;
            if (p.Sheet.Sheddable) _covShed += coverage;
        }
        if (p.Sheet.Sheddable) colour.a *= Overdraw.Fade(p.Cut, _shed);
        if (colour.a <= InvisibleAlpha)
        {
            // Nothing to see (the tail of a fade, or smoke given up): no draw call and no native calls.
            if (p.Drawn) { p.Drawn = false; p.R.enabled = false; }
            return true;
        }
        if (p.FlipCount > 0)
        {
            var frame = p.FlipFps > 0f ? (int)((now - p.T0) * p.FlipFps) % p.FlipCount : Math.Min(p.FlipCount - 1, (int)(k * p.FlipCount));
            if (frame != p.Cell) { p.Cell = frame; p.F.sharedMesh = p.Sheet.Cells[frame]; }
        }
        switch (p.Face)
        {
            case SpriteFace.Floor:
                {
                    var up = Math.Abs(p.Normal.y) > 0.98f ? Vector3.forward : Vector3.up;
                    p.T.SetPositionAndRotation(p.Pos, Quaternion.LookRotation(p.Normal, up) * Quaternion.Euler(0f, 0f, (p.Rot + age * p.Spin) * 57.29578f));
                    p.T.localScale = new Vector3(size, size, 1f);
                    break;
                }
            case SpriteFace.Streak:
                {
                    var speed = p.Vel.magnitude;
                    var tail = speed > 1e-3f ? p.Vel * (-1f / speed) : Vector3.up;
                    var length = Math.Max(size * p.Aspect + speed * p.Stretch, 0.01f);
                    var facing = p.Pos - eye;
                    var look = facing.sqrMagnitude > 1e-4f ? facing : Vector3.forward;
                    p.T.SetPositionAndRotation(p.Pos + tail * (length * 0.5f), Quaternion.LookRotation(look, tail));
                    p.T.localScale = new Vector3(size, length, 1f);
                    break;
                }
            default:
                {
                    var facing = p.Pos - eye;
                    var look = facing.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(facing) : Quaternion.identity;
                    p.T.SetPositionAndRotation(p.Pos, look * Quaternion.Euler(0f, 0f, (p.Rot + age * p.Spin) * 57.29578f));
                    p.T.localScale = new Vector3(size, size, 1f);
                    break;
                }
        }
        _block!.SetColor(_colorId, colour);
        p.R.SetPropertyBlock(_block);
        if (!p.Drawn) { p.Drawn = true; p.R.enabled = true; }
        return true;
    }

    /// <summary>Hides everything and forgets what was running (level left, checkpoint reloaded); the quads stay.</summary>
    internal static void Clear()
    {
        if (!_ready) return;
        for (var i = 0; i < Capacity; i++) Hide(All[i]);
        Book.Clear();
        for (var r = 0; r < _ringCount; r++) _rings[r]!.Clear();
        _shed = 0f;
    }

    /// <summary>The floor under a point (within a metre below, a little above), lifted a hair so a flat mark does not fight it; the
    /// point itself and straight up when no ray hits.</summary>
    public static Vector3 Settle(Vector3 at, out Vector3 normal)
    {
        if (Physics.Raycast(at + Vector3.up * 0.4f, Vector3.down, out var hit, 1.6f, Scene.WorldMask, QueryTriggerInteraction.Ignore))
        {
            normal = hit.normal;
            return hit.point + hit.normal * 0.035f;
        }
        normal = Vector3.up;
        return at + Vector3.up * 0.035f;
    }
}

/// <summary>A set of long-lived marks on any surface (bullet holes, scorches, blood, ground marks): flat quads lifted a little off the
/// surface along its normal, each with a life and a fade-out at its end. The set never grows: a new mark takes a free slot, or, when
/// every slot is taken, the oldest mark. Make one per mod with <see cref="SpritePool.CreateDecals"/>, or share <see cref="Decals.Shared"/>.</summary>
public sealed class DecalRing
{
    /// <summary>How far a mark is lifted off its surface along the normal, so it does not fight the surface for depth.</summary>
    public const float Lift = 0.02f;

    private readonly SpriteParticle[] _marks;
    private readonly DecalSlots _slots;

    internal DecalRing(int capacity, Transform parent)
    {
        _slots = new DecalSlots(Math.Max(1, capacity));
        _marks = new SpriteParticle[_slots.Capacity];
        for (var i = 0; i < _marks.Length; i++) _marks[i] = SpritePool.MakeParticle(parent);
    }

    public int Capacity => _marks.Length;

    /// <summary>Marks alive now.</summary>
    public int Live => _slots.LiveCount;

    /// <summary>Places a mark lying on the spawn's <see cref="SpriteSpawn.Normal"/> (its face is taken as
    /// <see cref="SpriteFace.Floor"/>); the position is used as given.</summary>
    public Sprite Place(in SpriteSpawn s)
    {
        if (s.Sheet == null || !Alive) return Sprite.None;
        if (ForgeCore.Recording.Watched)
            ForgeCore.Recording.Effect(new ForgeCore.ShownEffect(ForgeCore.ShownKind.Decal, s.Sheet.Seen, 0, s.Position, s.Normal, s.Size0, s.Colour0, seconds: s.Life, fade: s.Life * (1f - s.Hold)));
        var slot = _slots.Take();
        var p = _marks[slot];
        SpritePool.Start(p, in s, Scene.Now);
        p.Face = SpriteFace.Floor;
        return new Sprite(slot, _slots.Generation(slot), this);
    }

    /// <summary>A mark of <paramref name="size"/> metres on the surface at <paramref name="point"/> facing
    /// <paramref name="normal"/>, turned at random, shown for <paramref name="life"/> seconds and fading out over the last
    /// <paramref name="fade"/> of them. <paramref name="cell"/> picks the sheet's cell (-1: random).</summary>
    public Sprite Place(SpriteSheet sheet, Vector3 point, Vector3 normal, float size, Color colour, float life, float fade, int cell = -1)
    {
        if (sheet == null) return Sprite.None;
        var n = normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.up;
        var gone = colour;
        gone.a = 0f;
        var s = SpriteSpawn.Of(sheet, SpriteFace.Floor, point + n * Lift, life, size, size, colour, gone);
        s.Normal = n;
        s.Cell = cell;
        s.Hold = DecalMath.Hold(life, fade);
        return Place(in s);
    }

    /// <summary>False once the pool's objects went with a scene and the pool was rebuilt: a mod keeping its own set across levels makes a
    /// new one with <see cref="SpritePool.CreateDecals"/> then. A set made once stays usable from level to level otherwise (its marks are
    /// cleared with the level).</summary>
    public bool Alive => _marks.Length == 0 || (_marks[0].R != null && !_marks[0].R.WasCollected);
    internal bool IsCurrent(Sprite sprite) => _slots.Current(sprite.Slot, sprite.Generation);
    internal SpriteParticle At(int slot) => _marks[slot];

    internal void Kill(int slot)
    {
        var p = _marks[slot];
        p.Live = p.Shown = p.Drawn = false;
        if (p.R != null && !p.R.WasCollected) p.R.enabled = false;
        _slots.End(slot);
    }

    internal void Tick(float now)
    {
        for (var i = 0; i < _marks.Length; i++)
        {
            if (!_marks[i].Live || SpritePool.Step(_marks[i], now)) continue;
            _slots.End(i);
            SpritePool.Ended(_marks[i]);
        }
    }

    internal void Clear()
    {
        foreach (var p in _marks)
        {
            p.Live = p.Shown = p.Drawn = false;
            if (p.R != null && !p.R.WasCollected) p.R.enabled = false;
        }
        _slots.Clear();
    }
}

/// <summary>Marks on any surface for every mod: one shared <see cref="DecalRing"/> of <see cref="SharedCapacity"/> marks (made ahead,
/// before the first one is needed) and <see cref="Stick"/> to find the surface a shot or a blast hits. The game's own decal system
/// (<c>Decals.DeferredDecalSystem</c>) draws only with its own deferred-decal materials registered to its level culling, so it cannot
/// take a mod's texture; these marks are the library's sprites lying on the surface.</summary>
public static class Decals
{
    public const int SharedCapacity = 128;
    private static DecalRing? _shared;

    /// <summary>The shared marks, or null while the library's shaders are missing (logged once).</summary>
    public static DecalRing? Shared
    {
        get
        {
            if (_shared != null && _shared.Alive) return _shared;
            return _shared = SpritePool.CreateDecals(SharedCapacity);
        }
    }

    /// <summary>Builds the shared marks ahead of the first hit (a landing step).</summary>
    internal static void Prepare() => _ = Shared;

    /// <summary>The first world surface along <paramref name="direction"/> from <paramref name="from"/> within
    /// <paramref name="reach"/> metres: its point and normal. False (and the outputs zero) when nothing is hit.</summary>
    public static bool Stick(Vector3 from, Vector3 direction, float reach, out Vector3 point, out Vector3 normal)
    {
        if (direction.sqrMagnitude > 1e-8f && reach > 0f
            && Physics.Raycast(from, direction, out var hit, reach, Scene.WorldMask, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            normal = hit.normal;
            return true;
        }
        point = normal = Vector3.zero;
        return false;
    }
}
