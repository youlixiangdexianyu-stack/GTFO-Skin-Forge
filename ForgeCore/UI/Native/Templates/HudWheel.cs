using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UInput = UnityEngine.Input;

namespace ForgeUI;

/// <summary>One choice on a wheel page: a name, and an icon drawn in its slot (the name is drawn there when there is no icon).</summary>
public sealed class WheelItem
{
    public WheelItem(string id, UIText name, Sprite? icon = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? UIText.Empty;
        Icon = icon;
    }

    /// <summary>Yours: what <see cref="HudWheel.Chosen"/> tells you was picked.</summary>
    public string Id { get; }
    public UIText Name { get; set; }
    public Sprite? Icon { get; set; }

    /// <summary>False greys the slot out: it still lights up under the pointer and shows its name, but it cannot be chosen.</summary>
    public bool Usable { get; set; } = true;

    /// <summary>True makes the slot glow and pulse in the accent colour (a rim and a lit edge) whether or not the pointer is on it; the open wheel shows a change on the next frame. Set it for a choice that answers something now (see <see cref="HudWheel.Highlight"/>).</summary>
    public bool Highlight { get; set; }
}

/// <summary>One page of a wheel: a title shown in the centre and up to eight items, slot 0 at the top, then clockwise.</summary>
public sealed class WheelPage
{
    public WheelPage(UIText title, params WheelItem[] items)
    {
        Title = title ?? UIText.Empty;
        if (items == null || items.Length > WheelControl.MaxSlots) throw new ArgumentException("A wheel page has 0 to " + WheelControl.MaxSlots + " items.", nameof(items));
        Items = items;
    }

    public UIText Title { get; set; }
    public IReadOnlyList<WheelItem> Items { get; }

    // The four shapes the page draws with, kept from the first frame it is drawn (see HudWheel.PageShapes).
    internal Sprite[]? Shapes;
    internal int ShapesKey;
}

/// <summary>
/// A radial wheel in the middle of the screen, opened by one of your key actions (<see cref="UIKitMod.AddWheel"/>). The slots share
/// the circle evenly; each shows its item's icon (or name) and its number, inside a graduated ring in the HUD's own lines. It opens by
/// growing out as the slots arrive one after another, and a new page's slots arrive the same way. A pointer appears at the centre and moves
/// with the mouse (the player's <c>Hud / PointerScale</c>), a chevron on the centre's ring shows its way; the slot under it, by its angle, lifts out, lights in the accent colour
/// with a thicker rim and shows the item's name in the centre; a row of cells in the centre shows the page. Choosing (<see cref="WheelControl"/>): ending the key (releasing it for a Hold key, pressing it again for any
/// other trigger mode) or a left click picks the slot under the pointer and closes the wheel; in the centre's dead zone ending the
/// key just closes it and a click does nothing; the right button or Esc closes it; 1 to 8 pick that slot at once; the scroll wheel
/// turns the page. While it is open the game's look, fire, aim, weapon keys, use, its menu and its communication menu are held off
/// through a ForgeCore <c>InputLease</c> (until the mouse buttons are up after it closes); movement stays the player's. It closes by
/// itself when the player leaves first-person play or <see cref="CanOpen"/> turns false. The player's <c>Wheel</c> settings set the
/// dead zone, the hover time, the text size and a high-contrast look; the slot that is hovered is also told apart by shape (lifted,
/// larger, outlined), never by colour alone.
/// </summary>
public sealed class HudWheel : HudWidget
{
    private const float RadiusPx = 230f, Lift = 6f, Grow = 0.035f, CloseSeconds = 0.14f, Reach = 1.15f;
    private const float OpenSeconds = 0.18f, SlotSeconds = 0.14f, SlotDelay = 0.022f, ArriveLift = -14f, TickDegreesPerSecond = 2.5f;
    private const float FaceOuter = 0.88f, RimInner = 0.9f, RimOuter = 0.925f, RimLitOuter = 0.965f, TicksScale = 1.06f, HubRingInner = 0.965f, NumberRadius = 0.8f, NumberBack = 0.2f;

    private static readonly InputAction[] Blocked =
    {
        InputAction.LookHorizontal, InputAction.LookVertical, InputAction.Fire, InputAction.Aim, InputAction.Reload, InputAction.Use,
        InputAction.Melee, InputAction.ScrollItems, InputAction.ScrollItemLeft, InputAction.ScrollItemRight, InputAction.SelectStandard,
        InputAction.SelectSpecial, InputAction.SelectMelee, InputAction.SelectTool, InputAction.SelectConsumable, InputAction.SelectResourcePack,
        InputAction.SelectHackingTool, InputAction.Flashlight, InputAction.NavMarkerPing, InputAction.UseOnAlly, InputAction.MenuToggle,
        InputAction.ToggleCommunicationMenu, InputAction.SelectCommunicationMenu,
    };

    private static readonly KeyCode[] Numbers =
    {
        KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8,
    };

    private static readonly KeyCode[] Keypad =
    {
        KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8,
    };

    private static readonly string[] Digits = { "1", "2", "3", "4", "5", "6", "7", "8" };
    private static readonly Dictionary<(ShapeKind, int, int, int), Sprite> Wedges = new();
    private static ConfigEntry<float>? _deadZone, _hoverSeconds, _textScale;
    private static ConfigEntry<bool>? _contrast;

    private readonly WheelControl _control;
    private readonly float[] _hover = new float[WheelControl.MaxSlots];
    private WheelPage[] _pages = Array.Empty<WheelPage>();
    private HudCanvas? _canvas;
    private ForgeCore.InputLease? _lease;
    private int _closedFrame = -1, _flashSlot = -1, _shownPage;
    private float _x, _y, _closedAt = float.NegativeInfinity, _openedAt = float.NegativeInfinity, _pageAt = float.NegativeInfinity;

    internal HudWheel(KeyAction key)
    {
        Key = key;
        _control = new WheelControl(page => page < _pages.Length ? _pages[page].Items.Count : 0,
            (page, slot) => page < _pages.Length && slot < _pages[page].Items.Count && _pages[page].Items[slot].Usable);
        BindSettings();
    }

    /// <summary>The key action that opens it; its trigger mode decides how the key ends it.</summary>
    public KeyAction Key { get; }

    /// <summary>Checked when the key would open it and every frame while open: false keeps it shut (or closes it).</summary>
    public Func<bool>? CanOpen { get; set; }

    /// <summary>Told the item picked, once, on the frame the wheel closes with a choice.</summary>
    public event Action<WheelItem>? Chosen;

    /// <summary>The middle mouse button went down over a slot while the wheel is open: the item under the pointer, for a secondary action of yours (a favourite, a lock). The wheel stays open and nothing is chosen.</summary>
    public event Action<WheelItem>? Secondary;

    public bool IsOpen => _control.IsOpen;

    /// <summary>The wheel holds the game's keys and the mouse: it is open, it closed this frame, or a mouse button is still down after it closed. Keys read past the
    /// lease (<c>InputLease.Down</c>) and the mouse's movement belong to the wheel meanwhile, so a mod that does read them checks this first.</summary>
    public bool HoldsInput => _lease != null;

    /// <summary>The page shown; kept from one opening to the next.</summary>
    public int Page
    {
        get => _control.Page;
        set => _control.Page = _pages.Length == 0 ? 0 : (value % _pages.Length + _pages.Length) % _pages.Length;
    }

    /// <summary>The pages, in scroll order (replaces the ones before; an open wheel stays open on the same page number).</summary>
    public void SetPages(params WheelPage[] pages)
    {
        _pages = pages ?? Array.Empty<WheelPage>();
        _control.Pages = _pages.Length;
        Page = _control.Page;
    }

    /// <summary>Lights the items whose ID is in <paramref name="ids"/> on every page and puts the glow out on the others; call it as often as you like (an open wheel follows at once, nothing is allocated).</summary>
    public void Highlight(HashSet<string> ids)
    {
        foreach (var page in _pages)
            foreach (var item in page.Items) item.Highlight = ids.Contains(item.Id);
    }

    /// <summary>Closes it without a choice.</summary>
    public void Close()
    {
        if (_control.Close() == WheelStep.Closed) Closed(-1);
    }

    private static void BindSettings()
    {
        _deadZone ??= ForgeCore.Settings.Bind(Plugin.Id, "Wheel", "DeadZone", 0.35f,
            "The radius of a wheel's centre, as a share of the wheel (0.15 to 0.6): releasing the key or clicking there chooses nothing.");
        _hoverSeconds ??= ForgeCore.Settings.Bind(Plugin.Id, "Wheel", "HoverSeconds", 0.08f,
            "How long a wheel slot takes to lift out under the pointer, in seconds (0 to 0.3).");
        _textScale ??= ForgeCore.Settings.Bind(Plugin.Id, "Wheel", "TextScale", 1f, "The size of the texts on a wheel, 0.75 to 2.");
        _contrast ??= ForgeCore.Settings.Bind(Plugin.Id, "Wheel", "HighContrast", false,
            "Draws wheels with solid dark slots, white texts and a white outline round the hovered slot instead of the accent colour.");
    }

    private static float DeadZone => Math.Clamp(_deadZone?.Value ?? 0.35f, 0.15f, 0.6f);
    private static float HoverSeconds => Math.Clamp(_hoverSeconds?.Value ?? 0.08f, 0f, 0.3f);
    private static float TextScale => Math.Clamp(_textScale?.Value ?? 1f, 0.75f, 2f);
    private static bool HighContrast => _contrast?.Value ?? false;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud, rectOrder: -300, spriteOrder: -200) { Font = UIKitFont.Tech };
        BuildShapes();
    }

    // Input is read every frame, shown or not, before the layout.
    internal override void Prepare(HudElement hud, float now)
    {
        try { Read(hud); }
        catch (Exception error)
        {
            Plugin.Error("ui-wheel-failed-closed", Id + ": the wheel failed and was closed: " + error);
            if (_control.IsOpen) { _control.Close(); Closed(-1); }
        }
        if (_lease != null && !_control.IsOpen && Time.frameCount > _closedFrame && !UInput.GetMouseButton(0) && !UInput.GetMouseButton(1))
        {
            _lease.Dispose();
            _lease = null;
        }
        Visible = _control.IsOpen || ForgeCore.Frame.UnscaledTime - _closedAt < CloseSeconds;
    }

    private void Read(HudElement hud)
    {
        _control.DeadZone = DeadZone;
        var hold = Key.Mode == KeyMode.Hold;
        var input = new WheelInput
        {
            Open = Key.Pressed,
            End = hold ? Key.Released : Key.Pressed,
            Lost = !Playing(),
        };
        if (_control.IsOpen)
        {
            input.Click = UInput.GetMouseButtonDown(0);
            input.Cancel = UInput.GetMouseButtonDown(1) || UInput.GetKeyDown(KeyCode.Escape);
            for (var i = 0; i < Numbers.Length; i++)
                if (UInput.GetKeyDown(Numbers[i]) || UInput.GetKeyDown(Keypad[i])) { input.Number = i + 1; break; }
            var scroll = UInput.mouseScrollDelta.y;
            input.Scroll = scroll < 0f ? 1 : scroll > 0f ? -1 : 0;
            MovePointer(hud);
            input.X = _x;
            input.Y = _y;
            if (UInput.GetMouseButtonDown(2)) SecondaryOnHovered();
        }
        switch (_control.Step(input))
        {
            case WheelStep.Opened:
                _x = _y = 0f;
                Array.Clear(_hover);
                _flashSlot = -1;
                _openedAt = ForgeCore.Frame.UnscaledTime;
                if (_lease == null || !_lease.IsHeld)
                {
                    _lease = ForgeCore.InputLease.Take(Plugin.Id);
                    _lease.Block(Blocked);
                }
                break;
            case WheelStep.Chosen:
                Closed(_control.ChosenSlot);
                var item = _pages[_control.ChosenPage].Items[_control.ChosenSlot];
                try { Chosen?.Invoke(item); }
                catch (Exception error) { Plugin.Error("ui-wheel-choice-handler-threw", Id + ": a wheel choice handler threw: " + error); }
                break;
            case WheelStep.Closed:
                Closed(-1);
                break;
        }
    }

    private void SecondaryOnHovered()
    {
        var slot = _control.Hovered;
        if (_control.Page >= _pages.Length || slot < 0 || slot >= _pages[_control.Page].Items.Count) return;
        try { Secondary?.Invoke(_pages[_control.Page].Items[slot]); }
        catch (Exception error) { Plugin.Error("ui-wheel-secondary-handler-threw", Id + ": a wheel secondary handler threw: " + error); }
    }

    private void Closed(int slot)
    {
        _closedFrame = Time.frameCount;
        _closedAt = ForgeCore.Frame.UnscaledTime;
        _flashSlot = slot;
    }

    private bool Playing()
    {
        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel || HudFocus.Away) return false;
            if (FocusStateManager.CurrentState != eFocusState.FPS) return false;
        }
        catch (Exception error) { Plugin.Error("ui-wheel-could-not-read-game-state", Id + ": the wheel could not read the game state: " + error); return false; }
        return CanOpen?.Invoke() ?? true;
    }

    // The mouse's own movement, as the HUD pointer reads it, turned into wheel radii; the pointer stays a little past the rim.
    private void MovePointer(HudElement hud)
    {
        if (!HudPointer.ScreenBounds(hud, out var left, out _, out var right, out _)) return;
        var radius = RadiusPx * Unit(hud);
        if (radius <= 0f) return;
        var scale = HudPointer.Speed * (right - left) / Math.Max(1, ForgeCore.Frame.ScreenWidth) / radius;
        (_x, _y) = WheelMath.Clamp(_x + UInput.GetAxisRaw("Mouse X") * scale, _y + UInput.GetAxisRaw("Mouse y") * scale, Reach);
    }

    private static float Unit(HudElement hud) => hud.HalfHeight > 0f ? hud.HalfHeight / 540f : 1f;

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        if (_pages.Length > 0 && HudPointer.ScreenBounds(hud, out var left, out var bottom, out var right, out var top))
            Draw((left + right) / 2f, (bottom + top) / 2f, dt);
        _canvas.End();
    }

    private void Draw(float cx, float cy, float dt)
    {
        var canvas = _canvas!;
        var unit = canvas.Unit;
        var now = ForgeCore.Frame.UnscaledTime;
        var dead = DeadZone;
        var text = TextScale;
        var contrast = HighContrast;
        var open = _control.IsOpen;
        var opening = open ? Ease.OutCubic(Math.Clamp((now - _openedAt) / OpenSeconds, 0f, 1f)) : 1f;
        var fade = open ? opening : Math.Clamp(1f - (now - _closedAt) / CloseSeconds, 0f, 1f);
        var radius = RadiusPx * unit * (open ? 0.9f + 0.1f * opening : 1f + 0.03f * (1f - fade));
        if (_shownPage != _control.Page)
        {
            _shownPage = _control.Page;
            _pageAt = now;
        }
        var page = _pages[Math.Min(_control.Page, _pages.Length - 1)];
        var count = page.Items.Count;
        var hovered = open ? _control.Hovered : _flashSlot;
        var hoverSeconds = HoverSeconds;
        var inner = dead + 0.03f;
        var shapes = PageShapes(page, count, inner);
        Sprite fillShape = shapes[0], edgeShape = shapes[1], rimShape = shapes[2], rimLit = shapes[3];
        var line = contrast ? Color.white : UIKitColors.Cockpit;

        var dark = contrast ? new Color(0f, 0f, 0f, 0.5f) : new Color(UIKitColors.Shade.r, UIKitColors.Shade.g, UIKitColors.Shade.b, 0.5f);
        canvas.Sprite(Shape(ShapeKind.Soft, 1, 0f, 1f), cx, cy, radius * 2.7f, radius * 2.7f, UIKitColors.Faded(dark, fade));
        var ticks = radius * 2f * TicksScale;
        canvas.Sprite(Shape(ShapeKind.Ticks, 1, 0f, 1f), cx, cy, ticks, ticks, UIKitColors.Faded(line, 0.42f * fade), now * TickDegreesPerSecond);

        var sinceShown = now - Math.Max(_openedAt, _pageAt);
        var half = MathF.PI / Math.Max(1, count);
        for (var slot = 0; slot < count; slot++)
        {
            var item = page.Items[slot];
            _hover[slot] = WheelMath.Approach(_hover[slot], slot == hovered ? 1f : 0f, dt, hoverSeconds);
            var h = _hover[slot];
            var arrive = open ? Ease.OutCubic(Math.Clamp((sinceShown - slot * SlotDelay) / SlotSeconds, 0f, 1f)) : 1f;
            var show = fade * arrive;
            if (show <= 0f) continue;
            var (dx, dy) = WheelMath.Direction(slot, count);
            var lift = (Lift * h + (1f - arrive) * ArriveLift) * unit;
            var size = 2f * radius * (1f + Grow * h);
            var x = cx + dx * lift;
            var y = cy + dy * lift;
            var degrees = -slot * 360f / count;
            var flash = !open && slot == _flashSlot;
            var lit = Math.Max(flash ? 1f : h, item.Highlight ? 0.6f + 0.25f * MathF.Sin(now * 6f) : 0f);

            var fill = contrast ? new Color(0f, 0f, 0f, item.Usable ? 0.82f : 0.55f) : UIKitColors.Faded(UIKitColors.Plate, item.Usable ? 0.66f : 0.4f);
            if (!contrast && lit > 0f) fill = Color.Lerp(fill, UIKitColors.Faded(UIKitColors.Accent, flash ? 0.8f : 0.34f), lit);
            canvas.Sprite(fillShape, x, y, size, size, UIKitColors.Faded(fill, show), degrees);
            var edge = Color.Lerp(UIKitColors.Faded(line, item.Usable ? 0.34f : 0.14f), contrast ? Color.white : UIKitColors.Accent, lit);
            canvas.Sprite(edgeShape, x, y, size, size, UIKitColors.Faded(edge, show), degrees);
            canvas.Sprite(rimShape, x, y, size, size, UIKitColors.Faded(line, (item.Usable ? 0.5f : 0.16f) * (1f - lit) * show), degrees);
            if (lit > 0f) canvas.Sprite(rimLit, x, y, size, size, UIKitColors.Faded(flash ? Color.white : contrast ? Color.white : UIKitColors.Accent, lit * show), degrees);

            var grown = radius * (1f + Grow * h);
            var middle = (inner + FaceOuter) / 2f * grown;
            var ix = x + dx * middle;
            var iy = y + dy * middle;
            var ink = !item.Usable ? (contrast ? new Color(1f, 1f, 1f, 0.5f) : UIKitColors.Faded(line, 0.32f)) : Color.Lerp(UIKitColors.Faded(line, 0.96f), Color.white, lit);
            ink = UIKitColors.Faded(ink, show);
            if (item.Icon != null)
            {
                var box = grown * (FaceOuter - inner) * 0.82f * (1f + 0.1f * h);
                canvas.Icon(item.Icon, ix, iy, box, box, ink);
            }
            else canvas.Text(Show(item.Name), ix, iy, 15f * text, ink, HudAlign.Centre, bold: h > 0.5f);

            // The number sits in the slot's clockwise corner, against the rim.
            var at = (slot * 2f + 1f - NumberBack) * half;
            var nr = grown * NumberRadius;
            canvas.Text(Digits[slot], x + MathF.Sin(at) * nr, y + MathF.Cos(at) * nr, 11f * text,
                UIKitColors.Faded(lit > 0.5f ? (contrast ? Color.white : UIKitColors.Accent) : UIKitColors.Faded(line, contrast ? 1f : 0.55f), show), HudAlign.Centre, bold: lit > 0.5f);
        }

        // The centre: a dark disc and its ring, the hovered item's name or the page's title, a cell per page.
        var hub = 2f * dead * radius * 0.92f;
        canvas.Sprite(Shape(ShapeKind.Fill, 1, 0f, 1f), cx, cy, hub, hub, UIKitColors.Faded(contrast ? Color.black : UIKitColors.Plate, (contrast ? 0.9f : 0.82f) * fade));
        canvas.Sprite(Shape(ShapeKind.Fill, 1, HubRingInner, 1f), cx, cy, hub, hub, UIKitColors.Faded(line, 0.6f * fade));
        var named = hovered >= 0 && hovered < count;
        var name = named ? Show(page.Items[hovered].Name) : Show(page.Title);
        var many = _pages.Length > 1;
        canvas.Text(name, cx, cy + (many ? 7f * unit * text : 0f), (named ? 20f : 17f) * text,
            UIKitColors.Faded(contrast || named ? Color.white : UIKitColors.Faded(line, 0.92f), fade), HudAlign.Centre, named);
        if (many)
        {
            var step = 15f * unit;
            var left = cx - (_pages.Length - 1) * step / 2f;
            for (var i = 0; i < _pages.Length; i++)
            {
                var on = i == _control.Page;
                canvas.Cell(left + i * step, cy - 15f * unit * text, 12f * unit, (on ? 6f : 4f) * unit,
                    UIKitColors.Faded(on ? (contrast ? Color.white : UIKitColors.Accent) : UIKitColors.Faded(line, 0.3f), fade));
            }
        }

        if (!open) return;
        var px = cx + _x * radius;
        var py = cy + _y * radius;
        var reach = MathF.Sqrt(_x * _x + _y * _y);
        if (reach > dead * 0.92f)
        {
            // A chevron on the hub's ring points where the pointer is.
            var ux = _x / reach;
            var uy = _y / reach;
            var tip = dead * radius * 0.92f + 13f * unit;
            var back = tip - 10f * unit;
            var side = 9f * unit;
            var colour = UIKitColors.Faded(contrast ? Color.white : UIKitColors.Accent, opening);
            canvas.Line(cx + ux * back - uy * side, cy + uy * back + ux * side, cx + ux * tip, cy + uy * tip, 2.5f * unit, colour);
            canvas.Line(cx + ux * back + uy * side, cy + uy * back - ux * side, cx + ux * tip, cy + uy * tip, 2.5f * unit, colour);
        }
        canvas.Sprite(UIKitSprites.Pixel, px, py, 12f * unit, 12f * unit, UIKitColors.Shade, 45f);
        canvas.Sprite(UIKitSprites.Pixel, px, py, 7f * unit, 7f * unit, contrast ? Color.white : UIKitColors.Bright, 45f);
    }

    // A page's four shapes, looked up once and kept on the page: drawing a frame does no dictionary lookups. They are made again when the dead zone changes or the game collected one.
    private static Sprite[] PageShapes(WheelPage page, int count, float inner)
    {
        var key = (int)MathF.Round(inner * 200f);
        var kept = page.Shapes;
        if (kept != null && page.ShapesKey == key && !kept[0].WasCollected && !kept[1].WasCollected && !kept[2].WasCollected && !kept[3].WasCollected) return kept;
        page.ShapesKey = key;
        return page.Shapes = new[]
        {
            Shape(ShapeKind.Fill, count, inner, FaceOuter), Shape(ShapeKind.Edge, count, inner, FaceOuter),
            Shape(ShapeKind.Fill, count, RimInner, RimOuter), Shape(ShapeKind.Fill, count, RimInner, RimLitOuter),
        };
    }

    private enum ShapeKind : byte { Fill, Edge, Soft, Ticks }

    // Every shape the pages will need, at the HUD's making: a shape costs a moment to draw and the first opening must not pay for it.
    private void BuildShapes()
    {
        var dead = DeadZone;
        Shape(ShapeKind.Soft, 1, 0f, 1f);
        Shape(ShapeKind.Ticks, 1, 0f, 1f);
        Shape(ShapeKind.Fill, 1, 0f, 1f);
        Shape(ShapeKind.Fill, 1, HubRingInner, 1f);
        foreach (var page in _pages)
        {
            var count = page.Items.Count;
            Shape(ShapeKind.Fill, count, dead + 0.03f, FaceOuter);
            Shape(ShapeKind.Edge, count, dead + 0.03f, FaceOuter);
            Shape(ShapeKind.Fill, count, RimInner, RimOuter);
            Shape(ShapeKind.Fill, count, RimInner, RimLitOuter);
        }
    }

    /// <summary>One slot of <paramref name="count"/>, pointing up: a ring segment from <paramref name="inner"/> to <paramref name="outer"/> (shares of the
    /// radius) with an even gap to its neighbours, filled or as its outline; one slot is a whole disc or ring. <see cref="ShapeKind.Soft"/> is a disc fading
    /// out from its centre and <see cref="ShapeKind.Ticks"/> a ring of graduations. One unit across, drawn once per shape.</summary>
    private static Sprite Shape(ShapeKind kind, int count, float inner, float outer)
    {
        count = Math.Clamp(count, 1, WheelControl.MaxSlots);
        var key = (kind, count, (int)MathF.Round(inner * 200f), (int)MathF.Round(outer * 200f));
        if (Wedges.TryGetValue(key, out var sprite) && sprite != null && !sprite.WasCollected) return sprite;
        const int size = 512, samples = 2;
        const float gap = 0.008f, line = 0.0032f;
        var half = MathF.PI / count;
        float from = inner / 2f, to = outer / 2f;
        sprite = NativeUi.Build("ForgeUI.Wheel." + kind + count + "." + key.Item3 + "." + key.Item4, size, size, size, (px, py) =>
        {
            var covered = 0f;
            for (var sy = 0; sy < samples; sy++)
                for (var sx = 0; sx < samples; sx++)
                {
                    var x = (px + (sx + 0.5f) / samples) / size - 0.5f;
                    var y = (py + (sy + 0.5f) / samples) / size - 0.5f;
                    var r = MathF.Sqrt(x * x + y * y);
                    switch (kind)
                    {
                        case ShapeKind.Soft:
                            if (r < 0.5f) { var k = 1f - r / 0.5f; covered += k * k * (3f - 2f * k); }
                            break;
                        case ShapeKind.Ticks:
                            if (r > 0.5f || r < 0.47f) break;
                            var degrees = (MathF.Atan2(x, y) * 57.29578f + 360f) % 360f;
                            var major = degrees % 30f;
                            if (MathF.Min(major, 30f - major) * 0.01745f * r < 0.0018f) { covered += 1f; break; }
                            var minor = degrees % 6f;
                            if (r >= 0.485f && MathF.Min(minor, 6f - minor) * 0.01745f * r < 0.0011f) covered += 1f;
                            break;
                        default:
                            if (r > to || r < from) break;
                            var away = MathF.Min(r - from, to - r);
                            if (count > 1)
                            {
                                var angle = MathF.Abs(MathF.Atan2(x, y));
                                if (angle > half) break;
                                var beside = r * MathF.Sin(half - angle) - gap;
                                if (beside < 0f) break;
                                away = MathF.Min(away, beside);
                            }
                            if (kind == ShapeKind.Fill || away < line) covered += 1f;
                            break;
                    }
                }
            return covered / (samples * samples);
        });
        Wedges[key] = sprite;
        return sprite;
    }
}

public sealed partial class UIKitMod
{
    /// <summary>A radial wheel opened by <paramref name="key"/> (see <see cref="HudWheel"/>); give it pages with
    /// <see cref="HudWheel.SetPages"/> and listen to <see cref="HudWheel.Chosen"/>. Hidden until the key opens it. Register the key
    /// with <see cref="KeyMode.Hold"/> as its default to get "hold to open, release to choose".</summary>
    /// <example><c>var wheel = ui.AddWheel("Emotes", ui.AddKeyAction("Wheel", "keys.wheel", "keys.group", "H", KeyMode.Hold)!);</c></example>
    public HudWheel? AddWheel(string name, KeyAction key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var id = Full(name, "HUD element");
        if (id == null) return null;
        var wheel = Hud.Add(id, new HudWheel(key), new HudSlot(HudRegion.World));
        if (wheel != null) wheel.Visible = false;
        return wheel;
    }
}
