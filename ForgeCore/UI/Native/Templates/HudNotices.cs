using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>
/// One mod's claim on the shared corner notices: short messages ("PICKED UP KEY", "SHIELD DOWN") in <see cref="HudRegion.Notices"/>,
/// each sliding in on a translucent slab with a coloured edge, staying for its time and fading. Every mod pushes into the same list
/// (<see cref="NoticeQueue"/>), so notices stack instead of overlapping: pushing a text that is already showing refreshes it, and a full
/// list drops the oldest. Make it with <see cref="UIKitMod.AddNotices"/>.
/// </summary>
public sealed class HudNotices
{
    internal HudNotices(string id) => Id = id;

    /// <summary>The full ID, <c>mod/name</c>.</summary>
    public string Id { get; }

    /// <summary>Shows <paramref name="text"/> for <paramref name="seconds"/> (not counting the slide-in and fade-out). The time
    /// base is the game's clock, <see cref="ForgeCore.Frame.GameTime"/>.</summary>
    /// <remarks>Text keys are looked up in your language files when you call this.</remarks>
    public void Push(UIText text, NoticeTone tone = NoticeTone.Info, float seconds = 4f) =>
        NoticesView.Queue.Push((text ?? UIText.Empty).In(UIKit.TextsOf(Id)), tone, seconds, ForgeCore.Frame.GameTime, this);

    /// <summary>Takes every notice this claim pushed away.</summary>
    public void Clear() => NoticesView.Queue.Clear(this);
}

/// <summary>The kit's own element that draws the shared notice list in <see cref="HudRegion.Notices"/>.</summary>
internal sealed class NoticesView : HudWidget
{
    internal const int Capacity = 6;
    private const float LinePx = 16f, SlabHeight = 26f, Step = 32f, PadX = 14f, EdgeWidth = 3f, SlideDistance = 36f, Width = 420f;
    internal static readonly NoticeQueue Queue = new(Capacity);
    private static readonly List<HudNotices> Handles = new();
    private static NoticesView? _view;
    private HudCanvas? _canvas;
    private bool _drawn;
    private int _count = -1;

    internal static HudNotices? Register(UIKitMod mod, string name)
    {
        var id = mod.Full(name, "notices");
        if (id == null) return null;
        foreach (var handle in Handles)
            if (handle.Id == id)
            {
                Plugin.Error("ui-notices-not-added-id-already-registered", "Notices not added: the ID '" + id + "' is already registered.");
                return null;
            }
        if (_view == null)
        {
            _view = Hud.Add(ModIds.Join(Plugin.Id, "Notices"), new NoticesView(), new HudSlot(HudRegion.Notices, 0, Width, Step));
            if (_view == null) return null;
            _view.Visible = false;
        }
        var notices = new HudNotices(id);
        Handles.Add(notices);
        return notices;
    }

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _drawn = false;
    }

    internal override void Prepare(HudElement hud, float now)
    {
        Queue.Tick(now);
        hud.Visible = Queue.Count > 0 || _drawn;
        if (Queue.Count == _count) return;
        _count = Queue.Count;
        Hud.SetSlot(hud, new HudSlot(HudRegion.Notices, 0, Width, Math.Max(1, _count) * Step));
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null) return;
        if (Queue.Count == 0 && !_drawn) return;
        if (!_canvas.Begin()) return;
        var (fx, fy) = HudFlow.Of(hud);
        var down = fy <= 0 ? -1f : 1f;
        for (var i = 0; i < Queue.Count; i++)
        {
            var text = Queue.Text(i);
            var alpha = Queue.Alpha(i, now);
            var slide = (1f - Queue.Slide(i, now)) * SlideDistance;
            var width = TextWidth.Estimate(text, LinePx) + PadX * 2f + EdgeWidth;
            var left = ScreenMath.Span(fx, width) + (fx > 0 ? -slide : fx < 0 ? slide : 0f);
            var centre = down * (i * Step + SlabHeight / 2f);
            var tone = UIKitColors.Of(Queue.Tone(i));
            _canvas.Rect(left + width / 2f, centre, width, SlabHeight, UIKitColors.Faded(UIKitColors.Shade, alpha));
            var edgeX = fx < 0 ? left + width - EdgeWidth / 2f : left + EdgeWidth / 2f;
            _canvas.Rect(edgeX, centre, EdgeWidth, SlabHeight, UIKitColors.Faded(tone, alpha));
            var textColour = UIKitColors.Faded(UIKitColors.Bright, alpha);
            if (fx < 0) _canvas.Text(text, left + width - EdgeWidth - PadX, centre, LinePx, textColour, HudAlign.Right);
            else _canvas.Text(text, left + EdgeWidth + PadX, centre, LinePx, textColour);
        }
        _drawn = Queue.Count > 0;
        _canvas.End();
    }
}
