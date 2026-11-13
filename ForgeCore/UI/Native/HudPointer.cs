using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// A mouse pointer on the in-level HUD, for screens a mod lets the player click (a replay's time bar, a panel of rows). While any mod
/// holds it (<see cref="Hold"/>, <see cref="PointerHold.Active"/>) in a level, and the game is not showing one of its own full screens
/// (<see cref="HudFocus.Away"/>), ForgeUI draws a pointer on the HUD and moves it with the mouse, and the game's own look, fire, aim,
/// reload, use, movement and weapon keys are held off through a ForgeCore <c>InputLease</c>, so the view stays still and a click never
/// fires the gun. The system cursor drives it: while shown the cursor is confined to the window instead of locked and hidden under the
/// arrow, so the arrow moves exactly as the system cursor does, whatever the game's look sensitivity. Anything of the game's that locks the
/// cursor again anyway leaves the arrow on the mouse's own movement. Letting go in first-person play locks it again as the game does; on
/// the way to one of the game's own screens the cursor is the game's, which has already set it.
/// Hit tests are in the HUD units of a <see cref="HudRegion.World"/> element (x right, y up); <see cref="ScreenBounds"/> gives the
/// screen's edges in the same units.
/// </summary>
public static class HudPointer
{
    private static readonly List<PointerHold> Holds = new();
    private static readonly List<PointerToggle> Toggles = new();
    private static ForgeCore.InputLease? _lease;
    private static bool _shown, _placed;
    // Where the pointer is, in the weapon list's space (the space every HUD element's root is placed in).
    private static float _x, _y;
    private static PointerView? _view;
    private static Vector3 _mouse;
    private static bool _freed;

    private static readonly InputAction[] Blocked =
    {
        InputAction.LookHorizontal, InputAction.LookVertical, InputAction.Fire, InputAction.Aim, InputAction.Reload, InputAction.Use,
        InputAction.Melee, InputAction.MoveHorizontal, InputAction.MoveVertical, InputAction.Jump, InputAction.Run, InputAction.Crouch,
        InputAction.ScrollItems, InputAction.SelectStandard, InputAction.SelectSpecial, InputAction.SelectMelee, InputAction.SelectTool,
        InputAction.SelectConsumable, InputAction.SelectResourcePack, InputAction.SelectHackingTool, InputAction.Flashlight,
        InputAction.NavMarkerPing, InputAction.UseOnAlly,
    };

    /// <summary>A hold for <paramref name="modId"/>, active from the start; set <see cref="PointerHold.Active"/> false or dispose it to let go.</summary>
    public static PointerHold Hold(string modId)
    {
        var hold = new PointerHold(modId ?? throw new ArgumentNullException(nameof(modId)));
        Holds.Add(hold);
        return hold;
    }

    /// <summary>A hold for <paramref name="modId"/> that Control drives: the pointer shows while Control is held, and a tap of Control
    /// keeps it shown until the next tap (watching and using the screen). It asks for nothing while <see cref="PointerToggle.Enabled"/>
    /// is false; enabling it starts with the pointer hidden.</summary>
    public static PointerToggle Toggle(string modId)
    {
        var toggle = new PointerToggle(Hold(modId));
        Toggles.Add(toggle);
        return toggle;
    }

    internal static void Release(PointerHold hold) => Holds.Remove(hold);

    internal static void Release(PointerToggle toggle) => Toggles.Remove(toggle);

    /// <summary>True while the pointer is on screen this frame.</summary>
    public static bool Shown => _shown;

    /// <summary>The element is the pointer's own (it shows even while a mod has the HUD alone, <see cref="UIKitMod.HudAlone"/>).</summary>
    internal static bool Draws(HudElement element) => _view != null && ReferenceEquals(_view.Element, element);

    /// <summary>The left button went down this frame (only while <see cref="Shown"/>).</summary>
    public static bool Pressed => _shown && UnityEngine.Input.GetMouseButtonDown(0);

    /// <summary>The left button is down (only while <see cref="Shown"/>).</summary>
    public static bool Held => _shown && UnityEngine.Input.GetMouseButton(0);

    /// <summary>The left button came up this frame (only while <see cref="Shown"/>).</summary>
    public static bool Released => _shown && UnityEngine.Input.GetMouseButtonUp(0);

    /// <summary>The pointer in <paramref name="hud"/>'s own units; false while it is not shown or the element has no HUD.</summary>
    public static bool Position(HudElement hud, out float x, out float y)
    {
        x = y = 0f;
        if (!_shown || hud == null) return false;
        x = _x - hud.Entry.RootX;
        y = _y - hud.Entry.RootY;
        return true;
    }

    /// <summary>The screen's edges in <paramref name="hud"/>'s own units, read this frame through the HUD camera (the settled
    /// <see cref="HudElement.HalfWidth"/> can be off while a mod moves the cameras); false while the element has no HUD.</summary>
    public static bool ScreenBounds(HudElement hud, out float left, out float bottom, out float right, out float top)
    {
        left = bottom = right = top = 0f;
        if (hud == null || !Edges(out left, out bottom, out right, out top)) return false;
        left -= hud.Entry.RootX; right -= hud.Entry.RootX;
        bottom -= hud.Entry.RootY; top -= hud.Entry.RootY;
        return true;
    }

    private static bool Edges(out float left, out float bottom, out float right, out float top)
    {
        right = top = 0f;
        return HudScreen.FromViewport(0f, 0f, out left, out bottom) && HudScreen.FromViewport(1f, 1f, out right, out top) && right > left && top > bottom;
    }

    /// <summary>The pointer is inside the box centred on (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static bool Over(HudElement hud, float x, float y, float width, float height)
        => Position(hud, out var px, out var py) && Math.Abs(px - x) <= width / 2f && Math.Abs(py - y) <= height / 2f;

    // Where the pointer was when the press that is going on began (a click is a press and a release on the same box).
    private static float _downX, _downY;
    private static bool _pressing;

    private static bool Within(float px, float py, HudElement hud, float x, float y, float width, float height)
        => Math.Abs(px - hud.Entry.RootX - x) <= width / 2f && Math.Abs(py - hud.Entry.RootY - y) <= height / 2f;

    /// <summary>The box was clicked this frame: the left button came up over it and had gone down over it (a press that moved off the box and came up elsewhere is no click, and
    /// a press that began elsewhere and came up here is none either). A click fires on the release, as the game's own menu buttons do.</summary>
    public static bool Clicked(HudElement hud, float x, float y, float width, float height)
        => Released && _pressing && hud != null && Within(_downX, _downY, hud, x, y, width, height) && Over(hud, x, y, width, height);

    /// <summary>The left button is down and went down over the box (the pressed look of a button; it stays pressed while the pointer is off it, and does not click there).</summary>
    public static bool PressedOn(HudElement hud, float x, float y, float width, float height)
        => Held && _pressing && hud != null && Within(_downX, _downY, hud, x, y, width, height);

    /// <summary>What a button makes known with a sound: the pointer coming onto it, a click on it, a click on one that is not available.</summary>
    public enum ButtonSoundKind { Hover, Click, Denied }

    /// <summary>The one place a sound for the HUD's buttons is attached: a mod sets it (ForgeUI does not depend on ForgeAudio) and every button drawn through <see cref="HudCanvas"/> calls it.
    /// <see cref="ButtonSoundKind.Hover"/> once as the pointer comes onto a button, <see cref="ButtonSoundKind.Click"/> on its click, <see cref="ButtonSoundKind.Denied"/> on a click on an unavailable one.</summary>
    public static Action<ButtonSoundKind>? ButtonSound { get; set; }

    private static int _hoverNow, _hoverBefore;

    /// <summary>The pointer is over the box (as <see cref="Over"/>); the first frame it is, <see cref="ButtonSound"/> hears <see cref="ButtonSoundKind.Hover"/>.</summary>
    public static bool Hover(HudElement hud, float x, float y, float width, float height)
    {
        if (!Over(hud, x, y, width, height)) return false;
        var id = HashCode.Combine(hud, (int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(width));
        _hoverNow = id;
        if (_hoverBefore != id) ButtonSound?.Invoke(ButtonSoundKind.Hover);
        return true;
    }

    /// <summary>A click on a button (<see cref="Clicked"/>) with its sound.</summary>
    public static bool Click(HudElement hud, float x, float y, float width, float height, bool enabled = true)
    {
        if (!Clicked(hud, x, y, width, height)) return false;
        ButtonSound?.Invoke(enabled ? ButtonSoundKind.Click : ButtonSoundKind.Denied);
        return enabled;
    }

    /// <summary>Development: puts the pointer at (<paramref name="x"/>, <paramref name="y"/>) in <paramref name="hud"/>'s units, for a
    /// readback that cannot move the mouse.</summary>
    public static void Place(HudElement hud, float x, float y)
    {
        if (hud == null) return;
        _x = x + hud.Entry.RootX;
        _y = y + hud.Entry.RootY;
        _placed = true;
    }

    internal static void Update()
    {
        if (Toggles.Count == 0 && Holds.Count == 0 && !_shown) return;
        if (Toggles.Count > 0)
        {
            var held = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            var pressed = UnityEngine.Input.GetKeyDown(KeyCode.LeftControl) || UnityEngine.Input.GetKeyDown(KeyCode.RightControl);
            var released = UnityEngine.Input.GetKeyUp(KeyCode.LeftControl) || UnityEngine.Input.GetKeyUp(KeyCode.RightControl);
            foreach (var toggle in Toggles) toggle.Update(held, pressed, released, ForgeCore.Frame.UnscaledTime);
        }
        var want = false;
        foreach (var hold in Holds) want |= hold.Active;
        if (want) want = InLevelPlay();
        if (want)
        {
            if (_lease == null || !_lease.IsHeld)
            {
                _lease = ForgeCore.InputLease.Take(Plugin.Id);
                _lease.Block(Blocked);
            }
            _view ??= Hud.Add(ModIds.Join(Plugin.Id, "Pointer"), new PointerView(), new HudSlot(HudRegion.World));
            Move();
        }
        else if (_shown)
        {
            _lease?.Dispose();
            _lease = null;
            if (_freed)
            {
                _freed = false;
                if (InLevelPlay())
                {
                    if (InputMapper.Current != null) InputMapper.Current.m_mouseLocked = true;
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
        }
        if (_view != null) _view.Visible = want;
        _shown = want;
        // The press that is going on: where it began (the release of it is this frame's or a later one's), over when the button comes up.
        if (!want || !UnityEngine.Input.GetMouseButton(0) && !UnityEngine.Input.GetMouseButtonUp(0)) _pressing = false;
        else if (UnityEngine.Input.GetMouseButtonDown(0)) { _pressing = true; _downX = _x; _downY = _y; }
        _hoverBefore = _hoverNow; _hoverNow = 0;
    }

    private static void Move()
    {
        _freed = true;
        // While this is set InputMapper.CheckLockMouse locks and centres the cursor; the game sets it again itself on a change of focus.
        if (InputMapper.Current != null) InputMapper.Current.m_mouseLocked = false;
        var free = Cursor.lockState != CursorLockMode.Locked;
        Cursor.lockState = CursorLockMode.Confined;
        Cursor.visible = false;
        if (!Edges(out var left, out var bottom, out var right, out var top)) return;
        if (!_placed) { _x = (left + right) / 2f; _y = (bottom + top) / 2f; _placed = true; }
        if (!free)
        {
            // Locked since last frame, so the cursor's place says nothing: the mouse's own movement (the game's InputManager has "Mouse X"
            // and, spelled so, "Mouse y"), as a share of the screen.
            var scale = Speed * (right - left) / Math.Max(1, ForgeCore.Frame.ScreenWidth);
            _x = Math.Clamp(_x + UnityEngine.Input.GetAxisRaw("Mouse X") * scale, left, right);
            _y = Math.Clamp(_y + UnityEngine.Input.GetAxisRaw("Mouse y") * scale, bottom, top);
            return;
        }
        // Only when it moved, so a development Place stays until the mouse is touched.
        var mouse = UnityEngine.Input.mousePosition;
        if (mouse == _mouse) return;
        if (!HudScreen.FromViewport(mouse.x / Math.Max(1, ForgeCore.Frame.ScreenWidth), mouse.y / Math.Max(1, ForgeCore.Frame.ScreenHeight), out var x, out var y)) return;
        _mouse = mouse; _x = x; _y = y;
    }

    private static ConfigEntry<float>? _speed;

    /// <summary>How fast the pointer moves compared with the mouse (the player's <c>Hud / PointerScale</c>); the wheel's pointer uses it too.</summary>
    internal static float Speed => (_speed ??= ForgeCore.Settings.Bind(Plugin.Id, "Hud", "PointerScale", 1f,
        "How fast the HUD pointer and the wheel's pointer move compared with the mouse: 1 moves it by the same share of the screen as the mouse's own movement (the same as the system cursor); 2 is twice as fast.")).Value;

    private static bool InLevelPlay()
    {
        try { return GameStateManager.CurrentStateName == eGameStateName.InLevel && !HudFocus.Away; }
        catch (Exception error) { Plugin.Error("ui-hud-pointer-could-not-read-game", "The HUD pointer could not read the game state: " + error); return false; }
    }

    /// <summary>The pointer itself: an arrow with a dark edge, its tip on the point.</summary>
    private sealed class PointerView : HudWidget
    {
        private HudCanvas? _canvas;

        internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

        internal override void Tick(HudElement hud, float now, float dt)
        {
            if (_canvas == null || !_canvas.Begin()) return;
            if (Position(hud, out var x, out var y))
            {
                var arrow = UIKitSprites.Arrow(ArrowDirection.Up);
                _canvas.Sprite(arrow, x + 6f, y - 10f, 24f, 28f, UIKitColors.Shade, 25f);
                _canvas.Sprite(arrow, x + 6f, y - 10f, 17f, 21f, UIKitColors.Bright, 25f);
            }
            _canvas.End();
        }
    }
}

/// <summary>One mod's hold on the HUD pointer (<see cref="HudPointer.Hold"/>).</summary>
public sealed class PointerHold : IDisposable
{
    internal PointerHold(string owner) => Owner = owner;

    public string Owner { get; }

    /// <summary>While true (the default) this hold asks for the pointer.</summary>
    public bool Active { get; set; } = true;

    public void Dispose()
    {
        Active = false;
        HudPointer.Release(this);
    }
}

/// <summary>One mod's Control-driven hold on the HUD pointer (<see cref="HudPointer.Toggle"/>): watching (pointer hidden, the mod turns
/// the view) or using the screen (pointer shown, the view still).</summary>
public sealed class PointerToggle : IDisposable
{
    private readonly PointerHold _hold;
    private readonly TapOrHold _key = new();
    private bool _enabled;

    internal PointerToggle(PointerHold hold)
    {
        _hold = hold;
        _hold.Active = false;
    }

    public string Owner => _hold.Owner;

    /// <summary>While true Control drives the pointer; setting it (either way) starts again with the pointer hidden.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _key.Set(false); _hold.Active = false; }
    }

    /// <summary>Using the screen: Control held or tapped on, while <see cref="Enabled"/>. Read it to tell whether the mouse should turn
    /// the view.</summary>
    public bool Using => _enabled && _key.On;

    /// <summary>Using the screen or watching without Control (a button, a readback that cannot press keys).</summary>
    public void Set(bool usingScreen)
    {
        _key.Set(usingScreen);
        _hold.Active = Using;
    }

    internal void Update(bool held, bool pressed, bool released, float now)
    {
        if (_enabled) _key.Update(held, pressed, released, now);
        _hold.Active = Using;
    }

    public void Dispose()
    {
        _enabled = false;
        HudPointer.Release(this);
        _hold.Dispose();
    }
}
