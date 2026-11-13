using System;
using System.Collections.Generic;
using HarmonyLib;
using SNetwork;
using UnityEngine;
using UnityEngine.UI;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>While true only this mod's HUD elements show, every other mod's wait hidden: for a mod that takes the whole screen for what is
    /// not the local player's game, such as playing a recording. Set it back when done; the last mod to set it has it.</summary>
    /// <example><c>ui.HudAlone = true;</c></example>
    public bool HudAlone
    {
        get => Hud.AloneMod == Id;
        set { if (value) Hud.AloneMod = Id; else if (Hud.AloneMod == Id) Hud.AloneMod = null; }
    }

    /// <summary>A claim to hide the game's own HUD while <see cref="GameHudHide.Active"/> is set, keeping the parts in
    /// <paramref name="keep"/> (by default <see cref="GameHudClaims.DefaultKeep"/>: world-space markers, chat and the crosshair's hit,
    /// kill, charge-up, friendly and ping feedback). Null (logged) when the name cannot be used or is taken.</summary>
    /// <example><c>var hide = ui.HideGameHud("Pilot"); hide.Active = piloting;</c></example>
    public GameHudHide? HideGameHud(string name, GameHudPart keep = GameHudClaims.DefaultKeep)
    {
        var id = Full(name, "game HUD claim");
        if (id == null) return null;
        var hide = new GameHudHide(id, GameHud.Claims.AddHide(id, keep));
        if (GameHud.Register(id, hide)) return hide;
        GameHud.Claims.Remove(hide.Claim);
        return null;
    }

    /// <summary>A claim to hide only the game HUD parts in <paramref name="parts"/> (for example <c>GameHudPart.Vitals</c> alone, or
    /// <c>Crosshair | Compass</c>) while <see cref="GameHudHide.Active"/> is set, the rest of the game's HUD untouched. It is the same
    /// claim as <see cref="HideGameHud"/> with the parts named instead of kept, so both add up the same way: a part stays hidden while any
    /// active claim hides it. Null (logged) when the name cannot be used or is taken.</summary>
    /// <example><c>var hide = ui.HideGameHudParts("NoVitals", GameHudPart.Vitals); hide.Active = true;</c></example>
    public GameHudHide? HideGameHudParts(string name, GameHudPart parts)
    {
        var id = Full(name, "game HUD claim");
        if (id == null) return null;
        var hide = new GameHudHide(id, GameHud.Claims.AddHideOnly(id, parts));
        if (GameHud.Register(id, hide)) return hide;
        GameHud.Claims.Remove(hide.Claim);
        return null;
    }

    /// <summary>A claim to dock the game's chat box in <paramref name="region"/> while <see cref="GameChatMove.Active"/> is set: a space
    /// <paramref name="width"/> wide and as tall as <paramref name="lines"/> chat lines over the input row (measured from the chat box,
    /// <see cref="ChatDockMath"/>) is laid out with every other element, the chat box's lower left corner sits a pad inside the space's,
    /// older lines that would reach above the space are not drawn, and the plate lit behind the chat while the player types is as tall
    /// as what is shown. Null (logged) when the name cannot be used or is taken.</summary>
    /// <example><c>var chat = ui.MoveGameChat("PilotChat", HudRegion.BottomRight, 20, 460f, 6); chat.Active = piloting;</c></example>
    public GameChatMove? MoveGameChat(string name, HudRegion region = HudRegion.BottomRight, int priority = 0, float width = 460f, int lines = ChatDockMath.DefaultLines)
    {
        var id = Full(name, "game HUD claim");
        if (id == null) return null;
        lines = ChatDockMath.Lines(lines);
        width = Math.Max(2f * ChatDockMath.Pad, width);
        var space = Hud.Add(id, new ChatSpace(), new HudSlot(region, priority, width, ChatDockMath.SlotHeight(lines, ChatDockMath.Line, ChatDockMath.InputRow)));
        if (space == null) return null;
        space.Visible = false;
        var move = new GameChatMove(id, GameHud.Claims.AddChatMove(id, priority), space, width, lines);
        if (GameHud.Register(id, move)) return move;
        GameHud.Claims.Remove(move.Claim);
        space.Remove();
        return null;
    }
}

/// <summary>Hides the game's own HUD while <see cref="Active"/> is set (see <see cref="UIKitMod.HideGameHud"/>). Hides of every mod add
/// up: a part is hidden while any active hide does not keep it; the last release puts everything back exactly. The library releases it
/// by itself when the level ends, the player leaves the level or disconnects, or the local player dies (<see cref="Released"/>).</summary>
public sealed class GameHudHide
{
    internal GameHudHide(string id, GameHudClaims.Claim claim)
    {
        Id = id;
        Claim = claim;
    }

    internal GameHudClaims.Claim Claim { get; }

    public string Id { get; }

    /// <summary>True hides, false releases.</summary>
    public bool Active
    {
        get => Claim.Active;
        set
        {
            if (Claim.Active == value) return;
            if (value && !GameHud.CanHold(out var why))
            {
                Plugin.Error("ui-game-hud-claim-not-taken", "Game HUD claim '" + Id + "' not taken: " + why + ".");
                return;
            }
            GameHud.Claims.SetActive(Claim, value);
        }
    }

    /// <summary>The parts this claim hides.</summary>
    public GameHudPart Hides
    {
        get => Claim.Hides;
        set => GameHud.Claims.SetHides(Claim, value);
    }

    /// <summary>The parts this claim keeps: everything it does not hide.</summary>
    public GameHudPart Keep
    {
        get => Claim.Keep;
        set => GameHud.Claims.SetKeep(Claim, value);
    }

    /// <summary>Raised when the library released the claim by itself.</summary>
    public event Action<GameHudRelease>? Released;

    internal void OnReleased(GameHudRelease why) => Released?.Invoke(why);

    /// <summary>Releases and unregisters.</summary>
    public void Remove()
    {
        GameHud.Claims.SetActive(Claim, false);
        GameHud.Unregister(Id, this);
        GameHud.Claims.Remove(Claim);
    }
}

/// <summary>Moves the game's chat box while <see cref="Active"/> is set (see <see cref="UIKitMod.MoveGameChat"/>).</summary>
public sealed class GameChatMove
{
    internal GameChatMove(string id, GameHudClaims.Claim claim, ChatSpace space, float width, int lines)
    {
        Id = id;
        Claim = claim;
        Space = space;
        Width = width;
        Lines = lines;
    }

    internal GameHudClaims.Claim Claim { get; }
    internal ChatSpace Space { get; }

    /// <summary>The docked chat's width, HUD units.</summary>
    internal float Width { get; }

    /// <summary>The chat lines the space holds over the input row.</summary>
    internal int Lines { get; }

    public string Id { get; }

    /// <summary>The colour of the lit plate's left bar and input row while the player types; default the kit's cockpit colour.</summary>
    public Color Accent { get; set; } = UIKitColors.Cockpit;

    /// <summary>The docked chat drawn in the mod's HUD look (no game backings, the HUD's face, sizes and colours, players' name colours
    /// kept); null keeps the game's own look. Given back exactly when the move ends.</summary>
    public ChatLook? Look { get; set; }

    public bool Active
    {
        get => Claim.Active;
        set
        {
            if (Claim.Active == value) return;
            if (value && !GameHud.CanHold(out var why))
            {
                Plugin.Error("ui-chat-move-not-taken", "Chat move '" + Id + "' not taken: " + why + ".");
                return;
            }
            GameHud.Claims.SetActive(Claim, value);
        }
    }

    public event Action<GameHudRelease>? Released;

    internal void OnReleased(GameHudRelease why) => Released?.Invoke(why);

    public void Remove()
    {
        GameHud.Claims.SetActive(Claim, false);
        GameHud.Unregister(Id, this);
        GameHud.Claims.Remove(Claim);
        Space.Remove();
    }
}

/// <summary>The chat-sized space a chat move lays out; it draws nothing.</summary>
internal sealed class ChatSpace : HudWidget
{
    internal override void Build(HudElement hud) { }

    internal override void Tick(HudElement hud, float now, float dt) { }
}

/// <summary>
/// Enforces the hides and the chat move after the game's own frame. A hidden part is hidden at its roots, not piece by piece: the few
/// game objects that hold it (the layers' status, objective, compass, crosshair, prompt and message objects, the squad's items) are
/// collapsed to zero scale, which hides everything under them whatever the game adds there later; a root that holds a piece some other
/// part, the library or the game's skip prompt must keep drawn is not collapsed, its other children are (a handful of objects), and
/// its own renderer or graphic, if it has one, is switched off on its own. Per frame the cost is the number of those objects (tens),
/// never the number of pieces under them (75 600 with the pilot's HUD), and nothing is collected or watched piece by piece: the
/// roots' child counts are looked at four times a second to find what the game built. The game's objects stay active, so its logic,
/// coroutines and timers keep running and the readouts keep updating. Released, every collapsed object gets its own scale back.
/// </summary>
internal static class GameHud
{
    internal static readonly GameHudClaims Claims = new();
    private static readonly Registry<object> Handles = new();
    private static readonly Dictionary<string, object> ById = new(StringComparer.Ordinal);
    private const int Parts = 16;
    // The glass reflection and the glass curve are the game's HUD blit parameters, not pieces: hidden, they are set to 0 (again whenever
    // the game sets them back) and the game's own values are put back on release.
    private static bool _glassOff;
    private static (float Skew, float Amount, float Opacity, float Hud) _homeGlass, _setGlass;
    private const float Recheck = 0.25f, MissingRecheck = 3f;
    private static float _nextMissing;
    // Per part: the roots and the objects above kept pieces whose child counts tell that the game built something (Signature).
    private static readonly List<Component>[] Watched = NewLists<Component>();
    private static readonly int[] Signatures = new int[Parts];
    private static GameHudPart _stale;
    private static bool _changed;
    private const GameHudPart WithRoots = GameHudPart.Vitals | GameHudPart.WeaponList | GameHudPart.Objectives | GameHudPart.Crosshair |
        GameHudPart.CrosshairFeedback | GameHudPart.Interaction | GameHudPart.NavMarkers | GameHudPart.Chat | GameHudPart.Subtitles |
        GameHudPart.Teammates | GameHudPart.Compass | GameHudPart.ScreenFlash | GameHudPart.Watermark;
    // Per part: the objects collapsed for it, and the few single pieces (an own renderer or graphic of an object that holds a kept
    // piece, the glass-shatter camera effect) that are switched off one by one.
    private static readonly List<Transform>[] Covered = NewLists<Transform>();
    private static readonly List<Renderer>[] LooseRenderers = NewLists<Renderer>();
    private static readonly List<Behaviour>[] LooseGraphics = NewLists<Behaviour>();
    private static readonly Dictionary<IntPtr, Renderer>[] OffRenderers = NewMaps<Renderer>();
    private static readonly Dictionary<IntPtr, Behaviour>[] OffGraphics = NewMaps<Behaviour>();
    private static float _nextCheck;

    /// <summary>One collapsed game object: the scale it had before.</summary>
    private sealed class Collapsed
    {
        internal IntPtr Key;
        internal Transform Node = null!;
        internal Vector3 Home;
        internal GameHudPart Part;
    }

    private static readonly Dictionary<IntPtr, Collapsed> Applied = new();
    private static readonly List<Collapsed> Order = new();
    private static readonly Dictionary<IntPtr, (Transform Node, GameHudPart Part)> Wanted = new();

    /// <summary>The parts hidden now.</summary>
    internal static GameHudPart Hidden { get; private set; }

    private static List<T>[] NewLists<T>()
    {
        var lists = new List<T>[Parts];
        for (var i = 0; i < Parts; i++) lists[i] = new List<T>();
        return lists;
    }

    private static Dictionary<IntPtr, T>[] NewMaps<T>()
    {
        var maps = new Dictionary<IntPtr, T>[Parts];
        for (var i = 0; i < Parts; i++) maps[i] = new Dictionary<IntPtr, T>();
        return maps;
    }

    internal static bool Register(string id, object handle)
    {
        PatchGroups.Ensure(PatchGroup.HudHides);
        var problem = Handles.Add(id, handle);
        if (problem == null) { ById[id] = handle; return true; }
        Plugin.Error("ui-game-hud-claim-not-added", "Game HUD claim not added: " + problem + ".");
        return false;
    }

    internal static void Unregister(string id, object handle)
    {
        if (Handles.Remove(id, handle)) ById.Remove(id);
    }

    /// <summary>A claim can be taken only in a level with a living local player.</summary>
    internal static bool CanHold(out string why)
    {
        why = Problem(out _) ?? "";
        return why.Length == 0;
    }

    private static string? Problem(out GameHudRelease reason)
    {
        reason = GameHudRelease.LeftLevel;
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel)
        {
            reason = SNet.IsInLobby ? GameHudRelease.LeftLevel : GameHudRelease.Disconnected;
            return "not in a level";
        }
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive)
        {
            reason = GameHudRelease.PlayerDied;
            return "the local player is not alive";
        }
        return null;
    }

    /// <summary>Releases every claim, telling each owner why.</summary>
    internal static void ReleaseAll(GameHudRelease why)
    {
        if (!Claims.AnyActive) return;
        Claims.ReleaseAll(claim =>
        {
            if (!ById.TryGetValue(claim.Id, out var handle)) return;
            try
            {
                if (handle is GameHudHide hide) hide.OnReleased(why);
                else if (handle is GameChatMove move) move.OnReleased(why);
            }
            catch (Exception error) { Plugin.Error("ui-game-hud-claim-release-callback-failed", "Game HUD claim '" + claim.Id + "' release callback failed: " + error); }
        });
        Apply();
    }

    /// <summary>After the game's frame.</summary>
    internal static void LateUpdate()
    {
        try
        {
            if (Claims.AnyActive && Problem(out var reason) != null) ReleaseAll(reason);
            Apply();
            MoveChat();
            // A readout nobody has read yet costs nothing: it is not ticked and its game hooks are not on.
            if (PatchGroups.Has(PatchGroup.Inventory)) UIKit.InventoryReading.Tick();
            if (PatchGroups.Has(PatchGroup.Pickups)) UIKit.PickupsReading.Tick();
            if (PatchGroups.Has(PatchGroup.Interaction)) UIKit.InteractionReading.Tick();
        }
        catch (Exception error) { Plugin.Error("ui-game-hud-claims-failed-update", "Game HUD claims failed to update: " + error); }
    }

    /// <summary>While a game screen is open (<see cref="HudFocus.Away"/>) no part is hidden: the game shows its screen and puts its own
    /// HUD away as it does without mods; the claims hold again on the return.</summary>
    private static void Apply()
    {
        var hidden = HudFocus.Away ? GameHudPart.None : Claims.Hidden;
        var inLevel = GameStateManager.CurrentStateName == eGameStateName.InLevel;
        if (inLevel) GiveBackDeferred();
        var released = Hidden & ~hidden;
        if (released != GameHudPart.None)
        {
            for (var part = 0; part < Parts; part++)
                if (((int)released & (1 << part)) != 0) Release(part, inLevel);
            // A part that is kept now may lie inside a root that is still hidden: those are collected again to spare it.
            _stale |= hidden;
            _changed = true;
        }
        if (hidden != Hidden)
        {
            _stale |= hidden & ~Hidden;
            Hidden = hidden;
        }
        Glass((hidden & GameHudPart.GlassReflection) != GameHudPart.None, (hidden & GameHudPart.GlassCurve) != GameHudPart.None, Hud.AloneMod != null);
        if (hidden == GameHudPart.None)
        {
            _stale = GameHudPart.None;
            if (_changed) { _changed = false; Reconcile(); }
            return;
        }
        var now = ForgeCore.Frame.UnscaledTime;
        if (now >= _nextCheck)
        {
            _nextCheck = now + Recheck;
            var missing = now >= _nextMissing;
            if (missing) _nextMissing = now + MissingRecheck;
            for (var part = 0; part < Parts; part++)
            {
                var bit = (GameHudPart)(1 << part);
                if ((hidden & bit) == GameHudPart.None) continue;
                // Only the part that changed is collected again: its own roots gained or lost children. A part whose roots do not exist
                // (no layer built, or a piece this game build lacks) is looked for again every few seconds, not on every check.
                if (Signature(part) != Signatures[part] || (missing && Watched[part].Count == 0 && (WithRoots & bit) != GameHudPart.None)) _stale |= bit;
            }
        }
        var collect = _stale & hidden;
        if (collect != GameHudPart.None)
        {
            _stale = GameHudPart.None;
            Collect(hidden, collect);
            _changed = true;
        }
        if (_changed) { _changed = false; Reconcile(); }
        KeepCollapsed(hidden);
    }

    /// <summary>The collapsed objects stay at zero scale (the game's own scale animations and resolution changes write them back), and
    /// the few single pieces stay off. Tens of objects, whatever the game draws under them.</summary>
    private static void KeepCollapsed(GameHudPart hidden)
    {
        for (var i = Order.Count - 1; i >= 0; i--)
        {
            var c = Order[i];
            if (ForgeCore.NativeObjects.Gone(c.Node)) { Applied.Remove(c.Key); Order.RemoveAt(i); continue; }
            if (c.Node.localScale != Vector3.zero) c.Node.localScale = Vector3.zero;
        }
        for (var part = 0; part < Parts; part++)
        {
            if (((int)hidden & (1 << part)) == 0) continue;
            foreach (var r in LooseRenderers[part])
                if (!ForgeCore.NativeObjects.Gone(r) && r.enabled) { r.enabled = false; OffRenderers[part][r.Pointer] = r; }
            foreach (var g in LooseGraphics[part])
                if (!ForgeCore.NativeObjects.Gone(g) && g.enabled) { g.enabled = false; OffGraphics[part][g.Pointer] = g; }
        }
    }

    /// <summary>Brings the collapsed objects in line with what the hidden parts cover now: objects no part covers any more get their scale
    /// back, new ones are collapsed and their scale is remembered.</summary>
    private static void Reconcile()
    {
        Wanted.Clear();
        for (var part = 0; part < Parts; part++)
            foreach (var t in Covered[part])
                if (!ForgeCore.NativeObjects.Gone(t)) Wanted[t.Pointer] = (t, (GameHudPart)(1 << part));
        for (var i = Order.Count - 1; i >= 0; i--)
        {
            var c = Order[i];
            if (ForgeCore.NativeObjects.Gone(c.Node)) { Applied.Remove(c.Key); Order.RemoveAt(i); }
            else if (!Wanted.TryGetValue(c.Key, out var still))
            {
                c.Node.localScale = c.Home;
                Applied.Remove(c.Key);
                Order.RemoveAt(i);
            }
            else c.Part = still.Part;
        }
        foreach (var pair in Wanted)
        {
            if (Applied.ContainsKey(pair.Key)) continue;
            var c = new Collapsed { Key = pair.Key, Node = pair.Value.Node, Home = pair.Value.Node.localScale, Part = pair.Value.Part };
            Applied[pair.Key] = c;
            Order.Add(c);
            c.Node.localScale = Vector3.zero;
        }
    }

    /// <summary>The game's HUD glass reflection (<see cref="GameHudPart.GlassReflection"/>) and glass curve (<see cref="GameHudPart.GlassCurve"/>)
    /// off or back to the game's own values; <paramref name="opaque"/> draws the whole HUD at full strength where the game keeps it at 80%,
    /// which lets a fifth of the picture through every panel (<see cref="UIKitMod.HudAlone"/>).</summary>
    private static void Glass(bool reflectionOff, bool curveOff, bool opaque)
    {
        if (!reflectionOff && !curveOff && !opaque)
        {
            if (!_glassOff) return;
            _glassOff = false;
            SetGlass(_homeGlass);
            return;
        }
        var now = (UI_Apply.s_skewing, UI_Apply.s_reflectionAmount, UI_Apply.s_reflectionOpacity, UI_Apply.s_opacity);
        // First time, or the game set its values again (settings, menu style): those are the ones to put back.
        if (!_glassOff || now != _setGlass) _homeGlass = now;
        _glassOff = true;
        var want = (curveOff ? 0f : _homeGlass.Skew, reflectionOff ? 0f : _homeGlass.Amount, reflectionOff ? 0f : _homeGlass.Opacity, opaque ? 1f : _homeGlass.Hud);
        _setGlass = want;
        if (now != want) SetGlass(want);
    }

    private static void SetGlass((float Skew, float Amount, float Opacity, float Hud) glass)
    {
        UI_Apply.SetParameters(glass.Skew, glass.Amount, glass.Opacity, UI_Apply.s_pixelPatternOpacity, UI_Apply.s_mirrorOpacity,
            UI_Apply.s_backgroundIntensity, glass.Hud);
        UI_Apply.ApplySetttings();
    }

    /// <summary>Gives a released part back: its single pieces are switched on again and its covered objects are forgotten (the caller
    /// reconciles, which gives their scale back). Out of a level (lobby, menu, disconnect) the game manages its own HUD: switching a
    /// piece on now could draw the level's HUD there, so the pieces are given back when the next level is entered, if they still exist.</summary>
    private static void Release(int part, bool inLevel)
    {
        if (inLevel)
        {
            foreach (var r in OffRenderers[part].Values) if (!ForgeCore.NativeObjects.Gone(r)) r.enabled = true;
            foreach (var g in OffGraphics[part].Values) if (!ForgeCore.NativeObjects.Gone(g)) g.enabled = true;
        }
        else
        {
            foreach (var pair in OffRenderers[part]) DeferredRenderers[pair.Key] = pair.Value;
            foreach (var pair in OffGraphics[part]) DeferredGraphics[pair.Key] = pair.Value;
        }
        OffRenderers[part].Clear();
        OffGraphics[part].Clear();
        LooseRenderers[part].Clear();
        LooseGraphics[part].Clear();
        Covered[part].Clear();
        Watched[part].Clear();
    }

    // Single pieces switched off by a hide that ended outside a level, keyed by game object pointer (the values keep the game objects alive).
    private static readonly Dictionary<IntPtr, Renderer> DeferredRenderers = new();
    private static readonly Dictionary<IntPtr, Behaviour> DeferredGraphics = new();

    /// <summary>A level is entered: the pieces left off when the last hide ended outside a level go back on (those that still exist).</summary>
    private static void GiveBackDeferred()
    {
        if (DeferredRenderers.Count == 0 && DeferredGraphics.Count == 0) return;
        foreach (var r in DeferredRenderers.Values) if (!ForgeCore.NativeObjects.Gone(r)) r.enabled = true;
        foreach (var g in DeferredGraphics.Values) if (!ForgeCore.NativeObjects.Gone(g)) g.enabled = true;
        DeferredRenderers.Clear();
        DeferredGraphics.Clear();
    }

    /// <summary>What a part's watched objects look like from outside: which objects they are and how many children they and their children
    /// have. Reads child counts only, so it never walks a tree; a changed value means the game built or removed something under them.</summary>
    private static int Signature(int part)
    {
        var sum = 17;
        foreach (var root in Watched[part])
        {
            if (root == null || root.WasCollected) return -1;
            var t = root.transform;
            var count = t.childCount;
            sum = unchecked(sum * 31 + root.Pointer.GetHashCode());
            if (count > 64)
            {
                sum = unchecked(sum * 31 + count);
                continue;
            }
            // The library's own children (the kit's HUD elements under the weapon list) are not the game's: they do not count.
            var games = 0;
            for (var i = 0; i < count; i++)
            {
                var child = t.GetChild(i);
                if (OwnTransforms.Contains(child.Pointer)) continue;
                games++;
                sum = unchecked(sum * 31 + child.childCount);
            }
            sum = unchecked(sum * 31 + games);
        }
        return sum;
    }

    /// <summary>The objects to collapse for each of the <paramref name="parts"/> (each one hidden); the other hidden parts keep what they have.</summary>
    private static void Collect(GameHudPart hidden, GameHudPart parts)
    {
        var layer = GuiManager.PlayerLayer;
        GatherProtected(hidden, layer);
        for (var part = 0; part < Parts; part++)
        {
            if (((int)parts & (1 << part)) == 0) continue;
            Covered[part].Clear();
            LooseRenderers[part].Clear();
            LooseGraphics[part].Clear();
            Watched[part].Clear();
            var bit = (GameHudPart)(1 << part);
            if (bit == GameHudPart.GlassShatter)
            {
                // A camera image effect, not a GUI object: the game turns it on when the strangle starts (SetGlassShatterProgression
                // enables it once progress is above 0 and never turns it off); turning the component off skips its OnRenderImage.
                var player = ForgeCore.Players.Local;
                var shatter = player == null ? null : player.FPSCamera?.HUDGlassShatter;
                if (shatter != null && !shatter.WasCollected) LooseGraphics[part].Add(shatter);
            }
            else
            {
                RootList.Clear();
                Roots(bit, layer, RootList);
                foreach (var root in RootList) Cover(part, root.transform, true);
                if (bit == GameHudPart.Teammates && layer?.Root != null) Watched[part].Add(layer.Root);
            }
            Signatures[part] = Signature(part);
        }
    }

    /// <summary>Hides what <paramref name="node"/> holds: collapses it, unless a kept object lies under it. Then it stays, its own pieces
    /// are switched off one by one, and every child is covered in turn; a kept object itself is left alone with everything under it.
    /// A hidden root that is also a kept root stays hidden (the hide wins).</summary>
    private static void Cover(int part, Transform node, bool root)
    {
        var pointer = node.Pointer;
        if (!root && Protected.Contains(pointer)) return;
        if (root) Watched[part].Add(node);
        if (!Ancestors.Contains(pointer))
        {
            Covered[part].Add(node);
            return;
        }
        if (!root) Watched[part].Add(node);
        foreach (var r in node.GetComponents<Renderer>()) LooseRenderers[part].Add(r);
        foreach (var g in node.GetComponents<Graphic>()) LooseGraphics[part].Add(g);
        for (var i = 0; i < node.childCount; i++) Cover(part, node.GetChild(i), false);
    }

    // ---- The library's own pieces inside the game's HUD -------------------------------------------------------------------------

    private static readonly List<(GameObject Root, IntPtr Transform)> OwnRoots = new();

    /// <summary>A root object the library placed inside one of the game's HUD trees (a HUD element, a crosshair attachment): it and
    /// everything under it is never hidden.</summary>
    internal static void Own(GameObject root)
    {
        var pointer = root.transform.Pointer;
        OwnRoots.Add((root, pointer));
        OwnTransforms.Add(pointer);
    }

    /// <summary>The transforms of the library's own roots: a part's signature does not count them, so the kit's HUD elements, built
    /// under the game's weapon list, do not make the weapon list collected again.</summary>
    private static readonly HashSet<IntPtr> OwnTransforms = new();

    /// <summary>The game objects a part with roots covers (every part but the glass effects).</summary>
    private static void Roots(GameHudPart part, PlayerGuiLayer? layer, List<Component> into)
    {
        switch (part)
        {
            case GameHudPart.Vitals: Root(into, layer?.m_playerStatus); break;
            case GameHudPart.WeaponList: Root(into, layer?.Inventory); break;
            case GameHudPart.Objectives:
                Root(into, layer?.m_wardenObjective);
                Root(into, layer?.m_objectiveTimer);
                Root(into, layer?.m_wardenIntel);
                break;
            // Not the layers' roots: the crosshair layer's root holds the world marker layer and the interaction layer's root is the
            // game's shared HUD root (33060 renderers in the owner's log of 10-28 13:23, nearly all of them world markers kept on screen).
            case GameHudPart.Crosshair:
                var crosshairs = GuiManager.CrosshairLayer?.m_crosshairs;
                if (crosshairs != null) for (var i = 0; i < crosshairs.Length; i++) Root(into, crosshairs[i]);
                break;
            case GameHudPart.CrosshairFeedback: FeedbackRoots(into); break;
            case GameHudPart.Interaction:
                var interaction = GuiManager.InteractionLayer;
                if (interaction != null && !interaction.WasCollected)
                {
                    Root(into, interaction.m_interactPrompt);
                    Root(into, interaction.m_message);
                    Root(into, interaction.m_timer);
                }
                break;
            case GameHudPart.NavMarkers: Root(into, ForgeCore.NavMarkers.Layer?.Root); break;
            case GameHudPart.Chat: Root(into, layer?.m_gameEventLog); break;
            case GameHudPart.Subtitles: Root(into, layer?.m_subtitles); break;
            case GameHudPart.Teammates:
                if (layer?.Root != null) foreach (var item in layer.Root.GetComponentsInChildren<PUI_TeammateStatusItem>(true)) Root(into, item);
                break;
            case GameHudPart.Compass: Root(into, layer?.m_compass); break;
            case GameHudPart.ScreenFlash: Root(into, layer?.m_screenFlashOverlay); break;
            case GameHudPart.Watermark: Root(into, GuiManager.WatermarkLayer?.m_watermark?.m_watermarkText); break;
        }
    }

    private static void Root(List<Component> into, Component? root)
    {
        if (root != null && !root.WasCollected) into.Add(root);
    }

    private static readonly List<Component> RootList = new(), KeptRoots = new();
    private static readonly HashSet<IntPtr> Protected = new(), Ancestors = new();

    /// <summary>The objects that stay drawn inside a hidden root: the library's own, the game's skip prompt (the "hold to skip" text of
    /// intros, which the player needs to leave a scene wherever it sits) and the roots of the parts no claim hides (the squad's items
    /// aside: finding them walks the whole player layer), and every object above one of them.</summary>
    private static void GatherProtected(GameHudPart hidden, PlayerGuiLayer? layer)
    {
        Protected.Clear();
        Ancestors.Clear();
        for (var i = OwnRoots.Count - 1; i >= 0; i--)
        {
            var (root, transform) = OwnRoots[i];
            if (ForgeCore.NativeObjects.Gone(root)) { OwnRoots.RemoveAt(i); OwnTransforms.Remove(transform); continue; }
            Spare(root.transform);
        }
        var skip = layer?.m_skipText;
        if (skip != null && !skip.WasCollected) Spare(skip.transform);
        KeptRoots.Clear();
        for (var part = 0; part < Parts; part++)
        {
            var bit = (GameHudPart)(1 << part);
            if ((hidden & bit) == GameHudPart.None && (WithRoots & bit) != GameHudPart.None && bit != GameHudPart.Teammates)
                Roots(bit, layer, KeptRoots);
        }
        foreach (var kept in KeptRoots) Spare(kept.transform);
    }

    private static void Spare(Transform node)
    {
        Protected.Add(node.Pointer);
        for (var above = node.parent; above != null; above = above.parent)
            if (!Ancestors.Add(above.Pointer)) break;
    }

    /// <summary>The roots of the crosshair's feedback pieces (<see cref="GameHudPart.CrosshairFeedback"/>): every hit, weak-spot,
    /// no-damage and kill indicator, the friendly indicator and friendly-target mark, the ping mark and the charge-up ring.
    /// Everything else under the crosshair layer's root belongs to <see cref="GameHudPart.Crosshair"/>.</summary>
    private static void FeedbackRoots(List<Component> into)
    {
        var layer = GuiManager.CrosshairLayer;
        if (layer == null || layer.WasCollected) return;
        AddFeedback(layer.m_allHitIndicators, into);
        AddFeedback(layer.m_hitIndicators, into);
        AddFeedback(layer.m_weakspotHitIndicators, into);
        AddFeedback(layer.m_noDamageHitIndicators, into);
        Root(into, layer.m_hitIndicatorFriendly);
        Root(into, layer.m_crosshairFriendlyTarget);
        Root(into, layer.m_pingIndicator);
        var crosshairs = layer.m_crosshairs;
        if (crosshairs != null)
            for (var i = 0; i < crosshairs.Length; i++)
            {
                var crosshair = crosshairs[i];
                if (crosshair == null || crosshair.WasCollected) continue;
                foreach (var chargeUp in crosshair.GetComponentsInChildren<ChargeUpCrosshair>(true)) Root(into, chargeUp);
                foreach (var melee in crosshair.GetComponentsInChildren<MeleeCrosshair>(true)) Root(into, melee);
            }
        Root(into, layer.m_circleCrosshair?.m_chargeupImage);
    }

    private static void AddFeedback(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CrosshairHitIndicator>? indicators, List<Component> into)
    {
        if (indicators == null) return;
        for (var i = 0; i < indicators.Length; i++) Root(into, indicators[i]);
    }

    // ---- The chat move ------------------------------------------------------------------------------------------------------

    /// <summary>The chat: docked by the winning chat move (none while a game screen is open), lit while typed in (<see cref="ChatDock"/>).</summary>
    private static void MoveChat()
    {
        GameChatMove? owner = null;
        if (!HudFocus.Away && Claims.ChatWinner is { } winner && ById.TryGetValue(winner.Id, out var handle)) owner = handle as GameChatMove;
        foreach (var registered in Handles.Ordered)
            if (registered is GameChatMove move) move.Space.Visible = ReferenceEquals(move, owner);
        ChatDock.Update(owner);
    }

    /// <summary>The stamina read-out (<see cref="GameHudPart.Stamina"/>) has no renderers to switch off: while hidden, the game's stamina
    /// update does not run (a warning loop already running is told to end) and no new low-stamina warning starts.</summary>
    [PatchGroup(PatchGroup.HudHides)]
    [HarmonyPatch(typeof(PUI_LocalPlayerStatus), nameof(PUI_LocalPlayerStatus.UpdateStamina))]
    private static class StaminaUpdate
    {
        private static bool Prefix(PUI_LocalPlayerStatus __instance)
        {
            if ((Hidden & GameHudPart.Stamina) == GameHudPart.None) return true;
            if (__instance.m_staminaWarningLooping) __instance.m_staminaWarningLooping = false;
            return false;
        }
    }

    [PatchGroup(PatchGroup.HudHides)]
    [HarmonyPatch(typeof(PUI_LocalPlayerStatus), nameof(PUI_LocalPlayerStatus.StartStaminaWarning))]
    private static class StaminaWarning
    {
        private static bool Prefix() => (Hidden & GameHudPart.Stamina) == GameHudPart.None;
    }

    /// <summary>The level ended (<see cref="ForgeCore.GameEvents.LevelEnded"/>): every claim ends with the level.</summary>
    internal static void LevelEnded()
    {
        try
        {
            ReleaseAll(GameHudRelease.LevelEnded);
            ChatDock.Reset();
        }
        catch (Exception error) { Plugin.Error("ui-releasing-game-hud-claims-at-level", "Releasing the game HUD claims at level cleanup failed: " + error); }
    }
}
