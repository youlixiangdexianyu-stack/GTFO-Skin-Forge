using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// One mod's claim on the shared banner: the big text a little above the middle of the screen that pops in, blinks, holds and fades
/// ("EXTRACTION OPEN", "WAVE 3"), with an optional line under it. Every mod's banners go through one queue (<see cref="BannerQueue"/>),
/// so two never show at once: <see cref="Show"/> replaces this claim's own banner, takes the slot from a lower priority banner of another
/// mod, or waits behind an equal or higher one. Make it with <see cref="UIKitMod.AddBanner"/>.
/// </summary>
public sealed class HudBanner
{
    internal HudBanner(string id, int priority)
    {
        Id = id;
        Priority = priority;
    }

    /// <summary>The full ID, <c>mod/name</c>.</summary>
    public string Id { get; }

    /// <summary>Against other mods' banners: higher takes the slot, equal or lower waits.</summary>
    public int Priority { get; }

    /// <summary>True while this claim's banner is the one showing.</summary>
    public bool IsShowing => ReferenceEquals(BannerView.Queue.Current?.Owner, this);

    /// <summary>Shows <paramref name="text"/> for <paramref name="seconds"/> (not counting the fade-out), now or when the slot frees.</summary>
    /// <remarks>Text keys are looked up in your language files when you call this.</remarks>
    public void Show(UIText text, NoticeTone tone = NoticeTone.Info, float seconds = 2.5f, UIText? sub = null)
    {
        var texts = UIKit.TextsOf(Id);
        BannerView.Request(this, (text ?? UIText.Empty).In(texts), tone, seconds, (sub ?? UIText.Empty).In(texts));
    }

    /// <summary>Takes this claim's banner away, showing or waiting.</summary>
    public void Hide() => BannerView.Queue.Hide(this, ForgeCore.Frame.GameTime);

    /// <summary>Hides it and gives the name up.</summary>
    public void Remove() => BannerView.Unregister(this);
}

/// <summary>The kit's own element that draws the shared banner queue in <see cref="HudRegion.CenterBanner"/>; its layout priority is the
/// priority of the banner showing.</summary>
internal sealed class BannerView : HudWidget
{
    private const float PopSeconds = 0.18f, BlinkSeconds = 0.9f, MainPx = 46f, SubPx = 18f;
    internal static readonly BannerQueue Queue = new();
    private static readonly List<HudBanner> Handles = new();
    private static readonly List<BannerQueue.Request> Expired = new();
    private static BannerView? _view;
    private HudCanvas? _canvas;
    private int _version = -1;
    private Color _colour;

    internal static HudBanner? Register(UIKitMod mod, string name, int priority)
    {
        var id = mod.Full(name, "banner");
        if (id == null) return null;
        foreach (var handle in Handles)
            if (handle.Id == id)
            {
                Plugin.Error("ui-banner-not-added-id-already-registered", "Banner not added: the ID '" + id + "' is already registered.");
                return null;
            }
        if (_view == null)
        {
            _view = Hud.Add(ModIds.Join(Plugin.Id, "Banner"), new BannerView(), new HudSlot(HudRegion.CenterBanner, 0, 600f, 100f));
            if (_view == null) return null;
            _view.Visible = false;
        }
        var banner = new HudBanner(id, priority);
        Handles.Add(banner);
        return banner;
    }

    internal static void Unregister(HudBanner banner)
    {
        Queue.Hide(banner, ForgeCore.Frame.GameTime);
        Handles.Remove(banner);
    }

    internal static void Request(HudBanner banner, string text, NoticeTone tone, float seconds, string sub)
    {
        Queue.Show(banner, banner.Id, banner.Priority, text, tone, seconds, sub, ForgeCore.Frame.GameTime);
    }

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _version = -1;
    }

    internal override void Prepare(HudElement hud, float now)
    {
        Expired.Clear();
        Queue.Tick(now, Expired);
        var current = Queue.Current;
        hud.Visible = current != null;
        if (current == null || Queue.Version == _version) return;
        _version = Queue.Version;
        _colour = current.Tone == NoticeTone.Info ? UIKitColors.Bright : UIKitColors.Of(current.Tone);
        Hud.SetSlot(hud, new HudSlot(HudRegion.CenterBanner, current.Priority, TextWidth.Estimate(current.Text, MainPx) + 40f, current.Sub.Length > 0 ? 110f : 80f));
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var current = Queue.Current;
        var age = now - Queue.Started;
        if (current == null || current.Text.Length == 0)
        {
            _canvas.End();
            return;
        }
        var alpha = age < BlinkSeconds ? 0.3f + 0.7f * Ease.Blink(age, 3.4f) : 1f;
        if (age >= current.Seconds) alpha *= 1f - Ease.InCubic((age - current.Seconds) / BannerQueue.FadeSeconds);
        var pop = 0.75f + 0.25f * Ease.OutBack(age / PopSeconds);
        var main = MainPx * pop;
        var width = TextWidth.Estimate(current.Text, main);
        var line = width * Ease.OutCubic(age / 0.35f);
        _canvas.Text(current.Text, 0f, 0f, main, UIKitColors.Faded(_colour, alpha), HudAlign.Centre, true);
        _canvas.Rect(0f, -main * 0.62f, line, 2f, UIKitColors.Faded(UIKitColors.Accent, alpha));
        if (current.Sub.Length > 0) _canvas.Text(current.Sub, 0f, -main * 0.62f - 20f, SubPx, UIKitColors.Faded(UIKitColors.Text, alpha), HudAlign.Centre);
        _canvas.End();
    }
}
