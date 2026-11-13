using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The game's chat box (<c>PUI_GameEventLog</c>) docked and lit. Its prefab is a 450 x 400 box laid out from the bottom up: the input
/// row (<c>m_chatActiveRoot</c> while typing, its typed text <c>m_txtInput</c>; <c>m_chatInActiveRoot</c>, the help line, otherwise)
/// along the bottom 20, then the chat lines stacked upwards from <c>m_itemAlign</c> 25 above the bottom, <c>m_logItemHeight</c> each.
/// <para>While a mod's chat move (<see cref="UIKitMod.MoveGameChat"/>) wins, the chat box's lower left corner sits a pad inside its
/// slot's, the slot is sized from the measured line and input row heights, and lines reaching above the slot are not drawn until
/// they fit again; with the move's <see cref="GameChatMove.Look"/> the game's chat backings and help line are not drawn and its texts
/// take the HUD's face, sizes and colours and end inside the slot (<see cref="ChatSkin"/>). While the player types
/// (<c>PlayerChatManager.InChatMode</c>, in a level only: in the lobby the game's player layer stays as the game has it) the whole HUD stays and a lit plate fades in behind the docked chat, as tall as what is shown,
/// on the slot's bottom edge. Not typed in, a look shows only its newest idle lines on a faint backing and fades them out its idle time
/// after the newest line came (<c>AddLogItem</c>) or the chat was closed. Where the game keeps the chat it is left exactly as the game
/// draws it: no plate, no look.</para>
/// </summary>
internal static class ChatDock
{
    private static readonly string ElementId = ModIds.Join(Plugin.Id, "ChatFocus");
    private const float Recheck = 0.1f;
    private static ChatPlate? _plate;
    private static PUI_GameEventLog? _log;
    private static RectTransform? _rect;
    private static Camera? _camera;
    private static Vector3 _home;
    private static bool _moved;
    private static GameChatMove? _owner;
    private static float _line = ChatDockMath.Line, _row = ChatDockMath.InputRow, _gameLine = ChatDockMath.Line;
    private static int _measuredWidth, _measuredHeight, _count = -1, _skinCount = -1;
    private static float _nextCheck, _nextSkin, _top;
    private const float SkinRecheck = 0.25f;
    private static readonly Dictionary<IntPtr, Renderer> Overflow = new();
    private static readonly List<IntPtr> Back = new();
    private static bool _subscribed;
    private static bool _typing, _idle, _failed, _missingLogged;
    private static float _alpha, _last, _lastLine;

    /// <summary>The plate's opacity, 0 while the chat is not typed in.</summary>
    internal static float Alpha => _alpha;

    /// <summary>Where the plate is, in the weapon list's space; null while the chat box cannot be found.</summary>
    internal static HudBox? Plate { get; private set; }

    /// <summary>The input row's height on the plate, HUD units (measured).</summary>
    internal static float Band => _row;

    /// <summary>The plate's accent: the winning move's, the kit's cockpit colour where the game keeps the chat.</summary>
    internal static Color Accent { get; private set; } = UIKitColors.Cockpit;

    /// <summary>Every frame after the game's frame, with the chat move that holds the chat (null: the game keeps it).</summary>
    internal static void Update(GameChatMove? owner)
    {
        Subscribe();
        var log = GuiManager.PlayerLayer?.m_gameEventLog;
        if (log != null && log.WasCollected) log = null;
        if (log == null ? _log != null : _log == null || _log.WasCollected || _log.Pointer != log.Pointer) Attach(log);
        if (!ReferenceEquals(owner, _owner))
        {
            PutBack();
            _owner = owner;
        }
        if (_rect == null || _log == null)
        {
            Focus(null, false);
            return;
        }
        if (ForgeCore.Frame.ScreenWidth != _measuredWidth || ForgeCore.Frame.ScreenHeight != _measuredHeight) Measure();
        HudBox? plate = null;
        Idle = null;
        var moved = false;
        if (owner != null && owner.Space.HudElement is { State: LayoutState.Placed } element)
        {
            Size(owner);
            var slot = element.Box;
            Place(slot);
            var now = ForgeCore.Frame.UnscaledTime;
            var count = _log.m_logItems?.Count ?? 0;
            var look = owner.Look;
            // Lit (typed in, or the plate still fading): every line the slot holds; otherwise, with a look, only the newest few.
            var lit = _typing || _alpha > 0f;
            var idle = look != null && !lit;
            // Every frame while lit; otherwise when the game adds or drops a line (it lays the lines out again then), when the chat
            // stops being lit, and ten times a second.
            if (lit || idle != _idle || count != _count || now >= _nextCheck)
            {
                _nextCheck = now + Recheck;
                _count = count;
                _idle = idle;
                _top = Lines(idle ? ChatDockMath.IdleCeiling(slot, _row, _line, look!.IdleLines) : ChatDockMath.Ceiling(slot));
            }
            plate = ChatDockMath.Plate(slot, _top, _row);
            Accent = owner.Accent;
            moved = true;
            // The look again when the game adds or drops a line (a new line takes it) and four times a second (pieces it switched on).
            if (look != null)
            {
                if (count != _skinCount || now >= _nextSkin || !ChatSkin.On)
                {
                    _nextSkin = now + SkinRecheck;
                    _skinCount = count;
                    ChatSkin.Apply(_log, look, _line > 0f ? _gameLine / _line : 0f, slot.Right - ChatDockMath.Pad);
                }
                // The backing follows the lines actually drawn: the game fades and hides its lines by itself too.
                var shown = ChatSkin.Fade(idle ? ChatDockMath.IdleAlpha(now - _lastLine, look.IdleSeconds, look.IdleOpacity) : 1f);
                IdleAlpha = shown;
                if (idle && shown > 0.01f && _top > ChatDockMath.Corner(slot).Y + _row + 1f) Idle = plate;
            }
            else ChatSkin.Restore();
        }
        else
        {
            ShowOverflow();
            ChatSkin.Restore();
            Accent = UIKitColors.Cockpit;
        }
        Focus(plate, moved);
    }

    /// <summary>The docked chat's lines while not typed in (a look's newest few on a faint backing), as its plate would stand; null while
    /// the chat is lit, has no look or shows no line.</summary>
    internal static HudBox? Idle { get; private set; }

    /// <summary>The highest opacity of a docked line drawn now (the idle backing's).</summary>
    internal static float IdleAlpha { get; private set; } = 1f;

    /// <summary>The game added a chat line: the docked lines show again for their idle time. Subscribed to ForgeCore's one patch of
    /// <c>AddLogItem</c> on the first frame the dock updates.</summary>
    private static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        ForgeCore.GameHooks.GameLogAdded.After((_, _) => _lastLine = ForgeCore.Frame.UnscaledTime);
    }

    /// <summary>The level's cleanup: the chat goes back and every line is drawn again.</summary>
    internal static void Reset()
    {
        PutBack();
        _owner = null;
        _log = null;
        _rect = null;
    }

    private static void Attach(PUI_GameEventLog? log)
    {
        PutBack();
        _log = log;
        _rect = log?.transform.TryCast<RectTransform>();
        _camera = log == null ? null : NativeUi.CameraFor(log.transform);
        _home = _rect != null ? _rect.localPosition : default;
        _measuredWidth = _measuredHeight = 0;
        if (log != null && _rect == null && !_missingLogged)
        {
            _missingLogged = true;
            Plugin.Error("ui-games-chat-box-has-no-rect", "The game's chat box has no rect: it is neither docked nor lit.");
        }
    }

    private static void PutBack()
    {
        ShowOverflow();
        ChatSkin.Restore();
        _count = _skinCount = -1;
        if (!_moved) return;
        _moved = false;
        if (_rect != null && !_rect.WasCollected) _rect.localPosition = _home;
    }

    /// <summary>A point of the chat in HUD units, through the chat's own camera (or the screen when it has none).</summary>
    internal static bool ToHud(Vector3 world, out float x, out float y)
    {
        float vx, vy;
        if (_camera != null && !_camera.WasCollected)
        {
            var view = _camera.WorldToViewportPoint(world);
            if (view.z <= 0f) { x = y = 0f; return false; }
            vx = view.x;
            vy = view.y;
        }
        else
        {
            vx = world.x / ForgeCore.Frame.ScreenWidth;
            vy = world.y / ForgeCore.Frame.ScreenHeight;
        }
        return HudScreen.FromViewport(vx, vy, out x, out y);
    }

    /// <summary>The chat line and the input row in HUD units, from the chat box itself: its bottom, one line height above it, and the
    /// line anchor. Measured again when the screen size changes; logged once per chat box.</summary>
    private static void Measure()
    {
        if (_rect == null || _log == null) return;
        var r = _rect.rect;
        _gameLine = _log.m_logItemHeight > 0f ? _log.m_logItemHeight : ChatDockMath.Line;
        if (!ToHud(_rect.TransformPoint(new Vector3(r.XMin(), r.YMin(), 0f)), out _, out var bottom)) return;
        if (!ToHud(_rect.TransformPoint(new Vector3(r.XMin(), r.YMin() + _gameLine, 0f)), out _, out var lineTop)) return;
        var line = lineTop - bottom;
        var row = ChatDockMath.InputRow * line / _gameLine;
        var align = _log.m_itemAlign;
        if (align != null && !align.WasCollected && ToHud(align.position, out _, out var anchor) && anchor - bottom > 1f) row = anchor - bottom;
        if (line > 1f && line < 200f) _line = line;
        if (row > 1f && row < 400f) _row = row;
        _measuredWidth = ForgeCore.Frame.ScreenWidth;
        _measuredHeight = ForgeCore.Frame.ScreenHeight;
    }

    /// <summary>The winning move's slot as tall as its lines and the input row (laid out from the next frame).</summary>
    private static void Size(GameChatMove owner)
    {
        var element = owner.Space.HudElement;
        var height = ChatDockMath.SlotHeight(owner.Lines, _line, _row);
        var slot = element.Slot;
        if (Math.Abs(slot.Height - height) > 0.5f || Math.Abs(slot.Width - owner.Width) > 0.5f) element.Resize(owner.Width, height);
    }

    /// <summary>The chat box's lower left corner on the slot's, a pad in.</summary>
    private static void Place(HudBox slot)
    {
        if (_rect == null) return;
        var (ax, ay) = ChatDockMath.Corner(slot);
        if (!HudScreen.ToViewport(new HudBox(ax, ay, ax, ay), out var low, out _)) return;
        var r = _rect.rect;
        var target = new Vector2(low.x * ForgeCore.Frame.ScreenWidth, low.y * ForgeCore.Frame.ScreenHeight);
        var world = _rect.TransformPoint(new Vector3(r.XMin(), r.YMin(), 0f));
        var camera = _camera != null && !_camera.WasCollected ? _camera : null;
        var screen = camera != null ? camera.WorldToScreenPoint(world) : world;
        _moved = true;
        if (Math.Abs(screen.x - target.x) < 0.5f && Math.Abs(screen.y - target.y) < 0.5f) return;
        var goal = camera != null ? camera.ScreenToWorldPoint(new Vector3(target.x, target.y, screen.z)) : new Vector3(target.x, target.y, world.z);
        _rect.position += goal - world;
    }

    /// <summary>The highest top of the chat lines shown (the chat's bottom when none is), HUD units. A line reaching above
    /// <paramref name="ceiling"/> is not drawn (its text renderer is off) and one that fits again is drawn again.</summary>
    private static float Lines(float ceiling)
    {
        var top = float.MinValue;
        var items = _log?.m_logItems;
        if (items != null)
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected || !item.gameObject.activeInHierarchy) continue;
                if (!ToHud(item.transform.TransformPoint(new Vector3(0f, _gameLine, 0f)), out _, out var lineTop)) continue;
                var over = ChatDockMath.Overflows(ceiling, lineTop);
                var text = item.m_text;
                var renderer = text != null && !text.WasCollected ? text.renderer : null;
                if (renderer != null)
                {
                    var key = renderer.Pointer;
                    if (over)
                    {
                        if (renderer.enabled) renderer.enabled = false;
                        Overflow[key] = renderer;
                    }
                    else if (Overflow.Remove(key)) renderer.enabled = true;
                }
                if (!over && lineTop > top) top = lineTop;
            }
        // Lines the game dropped since: forget them (they are gone with their renderers).
        Back.Clear();
        foreach (var entry in Overflow)
            if (entry.Value == null || entry.Value.WasCollected) Back.Add(entry.Key);
        foreach (var key in Back) Overflow.Remove(key);
        if (top > float.MinValue) return top;
        return _rect != null && ToHud(_rect.TransformPoint(new Vector3(_rect.rect.xMin, _rect.rect.yMin, 0f)), out _, out var bottom) ? bottom : 0f;
    }

    private static void ShowOverflow()
    {
        foreach (var renderer in Overflow.Values)
            if (renderer != null && !renderer.WasCollected) renderer.enabled = true;
        Overflow.Clear();
    }

    // ---- the chat focus ------------------------------------------------------------------------------------------------------

    private static void Focus(HudBox? moved, bool docked)
    {
        if (_failed) return;
        var typing = GameStateManager.CurrentStateName == eGameStateName.InLevel && !HudFocus.Away && PlayerChatManager.InChatMode;
        var now = ForgeCore.Frame.UnscaledTime;
        var dt = _last > 0f ? now - _last : 0f;
        _last = now;
        if (typing != _typing)
        {
            _typing = typing;
            // Closing the chat starts the docked lines' idle time again.
            if (!typing) _lastLine = now;
        }
        _alpha = ChatDockMath.Fade(_alpha, typing, dt);
        var lit = _alpha > 0f || typing;
        if (!lit && Idle == null)
        {
            Plate = null;
            if (_plate != null && _plate.Visible) _plate.Visible = false;
            return;
        }
        if (typing) KeepHudParentsOn();
        Plate = docked && lit ? moved : null;
        if (_plate == null)
        {
            _plate = Hud.Add(ElementId, new ChatPlate(), new HudSlot(HudRegion.World));
            if (_plate == null) { _failed = true; return; }
        }
        var show = Plate != null || Idle != null;
        if (_plate.Visible != show) _plate.Visible = show;
    }

    /// <summary>The game can switch parts of its HUD off while the chat takes the keyboard. The kit's HUD elements are children of the
    /// game's weapon list, so while typing every object from the list up to the root stays active; what the game does to its own
    /// pieces is left as it is (the parts a mod hides are held off by their renderers, not by these objects).</summary>
    private static void KeepHudParentsOn()
    {
        var list = Hud.List;
        if (list == null || list.WasCollected) return;
        for (var t = list.transform; t != null && !t.WasCollected; t = t.parent)
        {
            var go = t.gameObject;
            if (go.activeSelf) continue;
            go.SetActive(true);
        }
    }
}

/// <summary>The chat focus plate as a HUD element of the kit (free region, so it takes no room in the layout): a dark plate with cut
/// corners, a lit outline and a bar at its left edge, and the input row along its bottom lit a little brighter under a thin line.</summary>
internal sealed class ChatPlate : HudWidget
{
    private HudCanvas? _canvas;

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var alpha = ChatDock.Alpha;
        if (alpha > 0.01f && ChatDock.Plate is { } box)
        {
            var u = _canvas.Unit;
            var accent = ChatDock.Accent;
            var x = (box.Left + box.Right) / 2f - HudScreen.CentreX;
            var y = (box.Bottom + box.Top) / 2f - HudScreen.CentreY;
            var bottom = box.Bottom - HudScreen.CentreY;
            var cut = 12f * u;
            _canvas.Plate(x, y, box.Width, box.Height, cut, UIKitColors.Faded(UIKitColors.Plate, 0.8f * alpha));
            // The input row: from the plate's bottom to half way into the space between the typed line and the first chat line.
            var band = Math.Min(box.Height - 4f * u, ChatDockMath.Pad + ChatDock.Band - 2.5f);
            _canvas.Rect(x, bottom + band / 2f + u, box.Width - 4f * u, band - 2f * u, UIKitColors.Faded(accent, 0.16f * alpha));
            _canvas.Rect(x, bottom + band, box.Width - 24f * u, u, UIKitColors.Faded(accent, 0.55f * alpha));
            _canvas.Frame(x, y, box.Width, box.Height, cut, UIKitColors.Faded(UIKitColors.Bright, 0.95f * alpha));
            _canvas.Rect(x - box.Width / 2f + 6.5f * u, y, 3f * u, box.Height - 12f * u, UIKitColors.Faded(accent, 0.95f * alpha));
        }
        else if (ChatDock.Idle is { } idle) Idle(idle, ChatDock.IdleAlpha, ChatDock.Accent);
        _canvas.End();
    }

    /// <summary>The lines shown while not typed in: a dark backing from the plate's left edge that thins out towards the right (stacked
    /// strips) behind the lines only, and the plate's left bar, thinner and fainter, all fading with the lines.</summary>
    private void Idle(HudBox box, float alpha, Color accent)
    {
        var u = _canvas!.Unit;
        var top = box.Top - ChatDockMath.Pad / 2f;
        var bottom = box.Bottom + ChatDockMath.Pad + ChatDock.Band - 4f * u;
        if (top - bottom < 4f * u) return;
        var y = (top + bottom) / 2f - HudScreen.CentreY;
        var left = box.Left - HudScreen.CentreX;
        const int strips = 5;
        for (var i = 0; i < strips; i++)
        {
            var width = box.Width * (strips - i) / strips;
            _canvas.Rect(left + width / 2f, y, width, top - bottom, UIKitColors.Faded(UIKitColors.Plate, 0.14f * alpha));
        }
        _canvas.Rect(left + 3.5f * u, y, 2f * u, top - bottom, UIKitColors.Faded(accent, 0.7f * alpha));
    }
}
