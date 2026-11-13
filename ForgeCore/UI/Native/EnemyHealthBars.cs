using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Enemies;
using UnityEngine;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>
    /// Adds a switch for enemy health bars: while it is <see cref="EnemyHealthBar.Active"/>, an enemy the local player hits gets a thin
    /// bar over its head showing the health the game holds for it (on every machine the game applies every player's hits, so a
    /// teammate's damage shows too). The piece each hit takes flashes and then shrinks away. Bars show only for enemies in sight (a wall
    /// between hides the bar), fade a few seconds after the last hit and go at once when the enemy dies. Damage numbers and kill marks are
    /// the hit feedback's (<see cref="AddHitFeedback"/>). The player can turn every bar off in ForgeUI's config
    /// (<c>EnemyHealthBars / Enabled</c>).
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Cockpit</c>.</param>
    /// <param name="style">How many bars at most, how long they stay, how far; null for the defaults.</param>
    public EnemyHealthBar? AddEnemyHealthBars(string name, EnemyHealthBarStyle? style = null)
    {
        var id = Full(name, "enemy health bars");
        if (id == null) return null;
        style ??= new EnemyHealthBarStyle();
        var problem = style.Problem();
        if (problem != null) { Plugin.Error("ui-enemy-health-bars-not-added", id + ": enemy health bars not added: " + problem + "."); return null; }
        return EnemyHealthBars.Add(new EnemyHealthBar(id, style));
    }
}

/// <summary>One mod's enemy health bar switch (see <see cref="UIKitMod.AddEnemyHealthBars"/>). Off when added.</summary>
public sealed class EnemyHealthBar
{
    private EnemyHealthBarStyle _style;
    internal EnemyHealthBar(string id, EnemyHealthBarStyle style) { Id = id; _style = style; }

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id { get; }

    /// <summary>Bars are drawn while any mod's switch is on; the style is that of the switch turned on last. Cheap to set every frame.</summary>
    public bool Active
    {
        get => EnemyHealthBars.IsOn(this);
        set => EnemyHealthBars.SetActive(this, value);
    }

    /// <summary>What shows. A style with a problem is refused (logged) and the old one kept.</summary>
    public EnemyHealthBarStyle Style
    {
        get => _style;
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var problem = value.Problem();
            if (problem != null) { Plugin.Error("ui-enemy-health-bar-style-refused", Id + ": enemy health bar style refused: " + problem + "."); return; }
            _style = value;
        }
    }

    /// <summary>Turns the switch off and frees the name.</summary>
    public void Remove() => EnemyHealthBars.Remove(this);
}

/// <summary>
/// The enemy health bars. The local player's hits arrive from <see cref="LocalHits"/>; the bars' state is <see cref="EnemyBarBook"/>;
/// the kit's own <see cref="HudRegion.World"/> element draws them through the player's view (<see cref="WorldView"/>). Positions and
/// line-of-sight casts go through <see cref="ForgeCore.EngineCalls"/>, so a frame allocates nothing.
/// </summary>
internal static class EnemyHealthBars
{
    private const float SightEvery = 0.1f, HeadClearance = 0.3f;
    private const int SightPerFrame = 3;
    private static readonly Dictionary<string, EnemyHealthBar> Registered = new(StringComparer.Ordinal);
    private static readonly ScopeSwitches<EnemyHealthBar> Scopes = new();
    internal static readonly EnemyBarBook Book = new();
    private static readonly Dam_EnemyDamageBase?[] Damage = new Dam_EnemyDamageBase?[EnemyBarBook.Capacity];
    private static readonly Transform?[] Body = new Transform?[EnemyBarBook.Capacity];
    private static readonly Transform?[]?[] Points = new Transform?[]?[EnemyBarBook.Capacity];
    private static readonly Renderer?[]?[] Skins = new Renderer?[]?[EnemyBarBook.Capacity];
    private static readonly float[] Size = new float[EnemyBarBook.Capacity];
    private static readonly Vector3[] Anchors = new Vector3[EnemyBarBook.Capacity];
    private static readonly int[] Due = new int[SightPerFrame];
    private static ConfigEntry<bool>? _enabled;
    private static EnemyBarsView? _view;
    private static int _sightCursor, _sightMask;

    private static bool _goneHooked;

    internal static bool Used { get; private set; }

    internal static EnemyHealthBar? Add(EnemyHealthBar bars)
    {
        if (Registered.ContainsKey(bars.Id))
        {
            Plugin.Error("ui-enemy-health-bars-not-added-name", bars.Id + ": enemy health bars not added: the name is already taken.");
            return null;
        }
        if (!_goneHooked)
        {
            // An enemy that dies or leaves ends its bar at once; ForgeCore patches these three game methods once for every package.
            _goneHooked = true;
            ForgeCore.GameHooks.EnemyDead.Before(Gone);
            ForgeCore.GameHooks.EnemyDespawned.Before(Gone);
            ForgeCore.GameHooks.EnemyDestroyed.Before(Gone);
        }
        PatchGroups.Ensure(PatchGroup.LocalHits);
        _enabled ??= ForgeCore.Settings.Bind(Plugin.Id, "EnemyHealthBars", "Enabled", true,
            "Draws the enemy health bars that mods such as Forge Frames switch on: only for enemies you hit and can see. Turn off to hide them in every mod.");
        if (_view == null)
        {
            _view = Hud.Add(ModIds.Join(Plugin.Id, "EnemyHealthBars"), new EnemyBarsView(), new HudSlot(HudRegion.World));
            if (_view == null) return null;
        }
        Registered.Add(bars.Id, bars);
        Used = true;
        return bars;
    }

    internal static bool IsOn(EnemyHealthBar bars) => Scopes.IsOn(bars);

    internal static void SetActive(EnemyHealthBar bars, bool on)
    {
        if (on && !Registered.ContainsKey(bars.Id)) return;
        if (!Scopes.Set(bars, on)) return;
        var current = Scopes.Current;
        if (current == null) Clear();
    }

    internal static void Remove(EnemyHealthBar bars)
    {
        SetActive(bars, false);
        if (Registered.TryGetValue(bars.Id, out var current) && ReferenceEquals(current, bars)) Registered.Remove(bars.Id);
    }

    private static EnemyHealthBarStyle? Live => Scopes.Current is { } scope && _enabled is { Value: true } ? scope.Style : null;

    /// <summary>Whether the local player's hits are wanted now.</summary>
    internal static bool WantsLocalHits => Used && Live != null;

    internal static void Clear()
    {
        Book.Clear();
        Array.Clear(Damage);
        Array.Clear(Body);
        Array.Clear(Points);
        Array.Clear(Skins);
    }

    /// <summary>A hit of the local player as the game applied it: <paramref name="before"/> − <paramref name="after"/> health out of
    /// <paramref name="max"/>; a drop to zero ends the bar at once.</summary>
    internal static void LocalHit(Dam_EnemyDamageBase target, float before, float after, float max)
    {
        var style = Live;
        if (style == null) return;
        var key = (long)target.Pointer;
        var slot = Book.Hit(key, ForgeCore.Frame.GameTime, before, after, max, style.MaxBars, out _);
        if (Damage[slot] == null || Damage[slot]!.Pointer != target.Pointer) Attach(slot, target);
        if (after <= 0f)
        {
            Book.Remove(key);
            Detach(slot);
        }
    }

    private static void Attach(int slot, Dam_EnemyDamageBase target)
    {
        Damage[slot] = target;
        Points[slot] = null;
        Skins[slot] = null;
        var owner = target.Owner;
        var body = owner == null ? null : owner.transform;
        Body[slot] = body;
        Size[slot] = owner == null ? 1f : Math.Max(0.5f, owner.SizeMultiplier);
        if (body == null) return;
        // The bones the enemy's limbs sit on, cached once; the bar follows the highest of them every frame (see Anchor).
        var limbs = target.DamageLimbs;
        if (limbs != null)
        {
            var found = 0;
            for (var i = 0; i < limbs.Length; i++)
                if (limbs[i] != null && limbs[i].transform != null) found++;
            if (found > 0)
            {
                var points = new Transform[found];
                found = 0;
                for (var i = 0; i < limbs.Length; i++)
                    if (limbs[i] != null && limbs[i].transform is { } t) points[found++] = t;
                Points[slot] = points;
                return;
            }
        }
        // No limbs: the top of the body's skinned meshes instead.
        var renderers = owner!.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (renderers != null && renderers.Length > 0)
        {
            var skins = new Renderer[renderers.Length];
            for (var i = 0; i < skins.Length; i++) skins[i] = renderers[i];
            Skins[slot] = skins;
        }
    }

    private static void Detach(int slot)
    {
        Damage[slot] = null;
        Body[slot] = null;
        Points[slot] = null;
        Skins[slot] = null;
    }

    /// <summary>The enemy died or left the world: its bar goes now.</summary>
    internal static void Gone(EnemyAgent agent)
    {
        if (!Used || agent == null) return;
        for (var i = 0; i < EnemyBarBook.Capacity; i++)
        {
            var damage = Damage[i];
            if (damage == null || !Book.Bars[i].Used) continue;
            var owner = ForgeCore.NativeObjects.Gone(damage) ? null : damage.Owner;
            if (owner != null && owner.Pointer != agent.Pointer) continue;
            Book.Remove(Book.Bars[i].Key);
            Detach(i);
        }
    }

    /// <summary>Per frame, from the kit's element: synced health, trails and life of every bar, line of sight for a few of them.</summary>
    internal static bool Prepare(float now, float dt, out EnemyHealthBarStyle? style, out ClipTransform clip, out Vector3 eye)
    {
        clip = default;
        eye = default;
        style = Live;
        if (style == null) return false;
        // The view first: without the engine calls (logged once by the view) no bone or ray may be read either.
        if (!WorldView.Read(out clip, out eye)) return false;
        if (_sightMask == 0) _sightMask = LayerManager.MASK_WORLD;
        for (var i = 0; i < EnemyBarBook.Capacity; i++)
        {
            ref var bar = ref Book.Bars[i];
            var damage = Damage[i];
            if (!bar.Used) { if (damage != null) Detach(i); continue; }
            if (ForgeCore.NativeObjects.Gone(damage) || ForgeCore.NativeObjects.Gone(Body[i]))
            {
                Book.Remove(bar.Key);
                Detach(i);
                continue;
            }
            var health = damage._Health_k__BackingField;
            if (health <= 0f) { Book.Remove(bar.Key); Detach(i); continue; }
            var max = damage._HealthMax_k__BackingField;
            Book.Sync(i, max > 0f ? health / max : 0f);
            Anchors[i] = ComputeAnchor(i);
        }
        Book.Tick(now, dt, style.Linger);
        var due = Book.DueSight(now, Due, SightPerFrame, ref _sightCursor, SightEvery);
        for (var k = 0; k < due; k++)
        {
            var slot = Due[k];
            Book.Bars[slot].InSight = !ForgeCore.EngineCalls.Blocked(eye, Anchor(slot), _sightMask);
        }
        return true;
    }

    /// <summary>The bar's place, worked out this frame by <see cref="Prepare"/>.</summary>
    internal static Vector3 Anchor(int slot) => Anchors[slot];

    /// <summary>A little over the enemy's highest point right now, so it follows a stance change (standing, crawling, lying): the
    /// highest of its limb bones (the head, or whatever is highest on a crawler) plus a clearance that grows with its size; with no
    /// limbs, the top of its skinned meshes; with neither, a standing height over its root. Cached transforms only, no allocation.</summary>
    private static Vector3 ComputeAnchor(int slot)
    {
        var p = ForgeCore.EngineCalls.Position(Body[slot]!);
        var size = Size[slot];
        var points = Points[slot];
        if (points != null)
        {
            var best = float.NegativeInfinity;
            for (var i = 0; i < points.Length; i++)
            {
                var t = points[i];
                if (ForgeCore.NativeObjects.Gone(t)) continue;
                var q = ForgeCore.EngineCalls.Position(t);
                if (q.y <= best) continue;
                best = q.y; p.x = q.x; p.z = q.z;
            }
            if (best > float.NegativeInfinity) { p.y = best + HeadClearance * size; return p; }
        }
        var skins = Skins[slot];
        if (skins != null)
        {
            var top = float.NegativeInfinity;
            for (var i = 0; i < skins.Length; i++)
            {
                var r = skins[i];
                if (ForgeCore.NativeObjects.Gone(r) || !ForgeCore.EngineCalls.Bounds(r, out var center, out var extents)) continue;
                var y = center.y + extents.y;
                if (y > top) top = y;
            }
            if (top > float.NegativeInfinity) { p.y = top + HeadClearance * 0.5f * size; return p; }
        }
        p.y += 1.8f * size;
        return p;
    }

    internal static bool Tracked(int slot) => Body[slot] != null;

}

/// <summary>The kit's own element that draws every enemy health bar.</summary>
internal sealed class EnemyBarsView : HudWidget
{
    private const float Width = 56f, Height = 3f, Lean = 2f;
    private HudCanvas? _canvas;
    private bool _drawn;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null) return;
        if (!EnemyHealthBars.Prepare(now, dt, out var style, out var clip, out var eye) || hud.HalfWidth <= 0f || !style!.Bars)
        {
            if (_drawn && _canvas.Begin()) { _canvas.End(); _drawn = false; }
            return;
        }
        if (!_canvas.Begin()) return;
        _drawn = true;
        var book = EnemyHealthBars.Book;
        float hw = hud.HalfWidth, hh = hud.HalfHeight, range2 = style.Range * style.Range;
        for (var i = 0; i < EnemyBarBook.Capacity; i++)
        {
            ref var bar = ref book.Bars[i];
            if (!bar.Used || !bar.InSight || !EnemyHealthBars.Tracked(i)) continue;
            var at = EnemyHealthBars.Anchor(i);
            float dx = at.x - eye.x, dy = at.y - eye.y, dz = at.z - eye.z;
            if (dx * dx + dy * dy + dz * dz > range2) continue;
            if (!clip.ToViewport(at.x, at.y, at.z, out var vx, out var vy) || vx < -0.05f || vx > 1.05f || vy < -0.05f || vy > 1.05f) continue;
            var (x, y) = ScreenMath.ViewportToHud(vx, vy, hw, hh);
            DrawBar(_canvas, bar, x, y, EnemyBarBook.Alpha(bar, now, style.Linger), now);
        }
        _canvas.End();
    }

    /// <summary>A thin slanted track, the health left in ice white (amber below a quarter), and the piece just lost: white for a
    /// moment, then orange while it shrinks to the fill.</summary>
    private static void DrawBar(HudCanvas canvas, in EnemyBarBook.Bar bar, float x, float y, float alpha, float now)
    {
        if (alpha <= 0.003f) return;
        var left = x - Width / 2f;
        canvas.Skew(x, y, Width, Height, Lean, UIKitColors.Faded(UIKitColors.Cockpit, 0.16f * alpha));
        var f = bar.Fraction;
        if (f > 0f) canvas.Skew(left + Width * f / 2f, y, Width * f, Height, Lean, UIKitColors.Faded(f < 0.25f ? UIKitColors.Amber : UIKitColors.Cockpit, 0.78f * alpha));
        var trail = bar.Trail.Trail;
        if (trail > f + 0.002f)
        {
            var flash = EnemyBarBook.Flashing(bar, now);
            var colour = flash ? UIKitColors.Faded(UIKitColors.Bright, 0.95f * alpha) : UIKitColors.Faded(UIKitColors.Orange, 0.7f * alpha);
            canvas.Skew(left + Width * (f + trail) / 2f, y, Width * (trail - f), Height + (flash ? 1f : 0f), Lean, colour);
        }
    }

}
