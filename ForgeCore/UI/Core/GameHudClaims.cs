using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>Parts of the game's own HUD that <see cref="GameHudHide"/> hides or keeps.</summary>
[Flags]
public enum GameHudPart
{
    None = 0,
    /// <summary>Health bar, pulse row, health, infection and shield texts and bars.</summary>
    Vitals = 1,
    /// <summary>Every slot of the game's weapon list (names, ammo, icons).</summary>
    WeaponList = 2,
    /// <summary>Objective texts, the objective timer and warden intel.</summary>
    Objectives = 4,
    /// <summary>The crosshair itself: the precision dot, the spread circle and the other crosshair shapes. Hit and kill markers, the
    /// charge-up ring, the friendly-target mark and the ping mark are <see cref="CrosshairFeedback"/>.</summary>
    Crosshair = 8,
    /// <summary>The interaction prompt and its hold ring.</summary>
    Interaction = 16,
    /// <summary>World-space markers: navigation markers, pings, name tags, beacons.</summary>
    NavMarkers = 32,
    /// <summary>The chat box and game event log.</summary>
    Chat = 64,
    Subtitles = 128,
    /// <summary>The on-screen teammate status items.</summary>
    Teammates = 256,
    Compass = 512,
    /// <summary>What the game flashes around the crosshair: hit, weak-spot, no-damage, friendly and kill markers
    /// (<c>ShowHitIndicator</c>, <c>ShowDeathIndicator</c>), the whole charge-up crosshair and its ring
    /// (<c>SetChargeUpVisibleAndProgress</c>), the melee crosshair, the friendly-target mark (<c>SetFriendlyTargetVisible</c>,
    /// <c>PopFriendlyTarget</c>) and the ping mark (<c>ShowPingIndicator</c>, <c>PopAngryPingIndicator</c>).</summary>
    CrosshairFeedback = 1024,
    /// <summary>The game's full-screen colour flash (damage, pickups: <c>TriggerScreenFlash</c>, the layer's flash overlay).</summary>
    ScreenFlash = 2048,
    /// <summary>The cracked-glass overlay of the first-person camera while a big enemy grabs and strangles you
    /// (<c>HUDGlassShatter</c>, driven by <c>PLOC_GrabbedByTank</c>).</summary>
    GlassShatter = 4096,
    /// <summary>The game's helmet-glass reflection of its whole HUD layer (<c>UI_Apply</c>'s reflection: a faint copy of every HUD
    /// text and shape, offset towards the middle of the screen). Hidden, the copies go; the HUD itself stays.</summary>
    GlassReflection = 8192,
    /// <summary>The game's stamina read-out: it has no pieces of its own (it tints the vitals bar and pulses its warning), so hidden, the
    /// game's stamina update and low-stamina warning (<c>PUI_LocalPlayerStatus.UpdateStamina</c>, <c>StartStaminaWarning</c>) stop and the
    /// bar keeps its last look until released. Stamina itself is untouched.</summary>
    Stamina = 16384,
    /// <summary>The game's version label at the bottom right (the watermark layer's version text, <c>PUI_Watermark.m_watermarkText</c>,
    /// where a modded game reads "MODDED" and a version). The FPS counter and the status notices beside it stay.</summary>
    Watermark = 32768,
    /// <summary>The curve of the game's helmet glass over its whole HUD layer (<c>UI_Apply</c>'s skewing: straight lines near the screen's
    /// edges bend). Hidden, the HUD layer, and every panel drawn on it, is flat. Kept by default.</summary>
    GlassCurve = 65536,
    All = 131071,
}

/// <summary>Why the library released a hide or a chat move by itself.</summary>
public enum GameHudRelease
{
    LevelEnded,
    LeftLevel,
    PlayerDied,
    Disconnected,
}

/// <summary>
/// The requests of every mod on the game's HUD, without Unity: which parts are hidden (each active hide hides every part outside its
/// keep list; a part is hidden while at least one active hide hides it) and which chat move wins (the highest priority, ties by ID).
/// The caller enforces the result every frame. No allocation per frame.
/// </summary>
public sealed class GameHudClaims
{
    /// <summary>What a hide keeps unless it says otherwise: what the game draws on the world (navigation markers, pings, name tags,
    /// beacons) and at the crosshair (hit, kill, charge-up, friendly and ping marks), which a HUD of its own does not redraw, and the
    /// chat, and the glass curve. Everything else is the HUD's flat screen layer that a mod redraws in its own style from the readouts.</summary>
    public const GameHudPart DefaultKeep = GameHudPart.NavMarkers | GameHudPart.Chat | GameHudPart.CrosshairFeedback | GameHudPart.GlassCurve;

    internal sealed class Claim
    {
        internal Claim(string id, bool chat, int priority)
        {
            Id = id;
            IsChatMove = chat;
            Priority = priority;
        }

        public string Id { get; }
        public bool IsChatMove { get; }
        public int Priority { get; }
        public bool Active { get; internal set; }
        /// <summary>The parts this claim hides while it is active.</summary>
        public GameHudPart Hides { get; internal set; }
        /// <summary>The parts this claim leaves alone (everything it does not hide).</summary>
        public GameHudPart Keep => GameHudPart.All & ~Hides;
    }

    private readonly List<Claim> _claims = new();

    internal Claim AddHide(string id, GameHudPart keep)
    {
        var claim = new Claim(id, false, 0) { Hides = GameHudPart.All & ~keep };
        _claims.Add(claim);
        return claim;
    }

    /// <summary>A claim that hides only <paramref name="parts"/>.</summary>
    internal Claim AddHideOnly(string id, GameHudPart parts)
    {
        var claim = new Claim(id, false, 0) { Hides = parts & GameHudPart.All };
        _claims.Add(claim);
        return claim;
    }

    internal Claim AddChatMove(string id, int priority)
    {
        var claim = new Claim(id, true, priority);
        _claims.Add(claim);
        return claim;
    }

    internal void Remove(Claim claim) => _claims.Remove(claim);

    internal void SetActive(Claim claim, bool active) => claim.Active = active;

    internal void SetKeep(Claim claim, GameHudPart keep) => claim.Hides = GameHudPart.All & ~keep;

    internal void SetHides(Claim claim, GameHudPart parts) => claim.Hides = parts & GameHudPart.All;

    /// <summary>The parts hidden now.</summary>
    public GameHudPart Hidden
    {
        get
        {
            var hidden = GameHudPart.None;
            for (var i = 0; i < _claims.Count; i++)
            {
                var c = _claims[i];
                if (c.Active && !c.IsChatMove) hidden |= c.Hides;
            }
            return hidden;
        }
    }

    /// <summary>The chat move in effect now, or null.</summary>
    internal Claim? ChatWinner
    {
        get
        {
            Claim? best = null;
            for (var i = 0; i < _claims.Count; i++)
            {
                var c = _claims[i];
                if (!c.Active || !c.IsChatMove) continue;
                if (best == null || c.Priority > best.Priority || (c.Priority == best.Priority && string.CompareOrdinal(c.Id, best.Id) < 0)) best = c;
            }
            return best;
        }
    }

    /// <summary>True while any claim is active.</summary>
    public bool AnyActive
    {
        get
        {
            for (var i = 0; i < _claims.Count; i++) if (_claims[i].Active) return true;
            return false;
        }
    }

    /// <summary>Releases every active claim, calling <paramref name="released"/> for each one released.</summary>
    internal void ReleaseAll(Action<Claim> released)
    {
        for (var i = 0; i < _claims.Count; i++)
        {
            var c = _claims[i];
            if (!c.Active) continue;
            c.Active = false;
            released(c);
        }
    }
}
