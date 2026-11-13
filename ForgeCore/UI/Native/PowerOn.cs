using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>A screen power-on and power-down played over the whole picture (see <see cref="ScreenPowerOn"/>): your panels and seams cut
    /// the screen, each panel comes on in its block's turn of <see cref="PowerOnSpec.Timeline"/> with ForgeUI's glitch (the picture in
    /// your tint, banding, scanlines, split into your three channel colours with torn bands and noise bars in them) settling to the plain
    /// picture, and the seams fade once every block
    /// has settled; powering down runs the blocks in reverse to black and clears. An exclusive layer at <see cref="PowerOnSpec.Priority"/>.
    /// Built and compiled by <see cref="WarmNextLayer"/> (or <see cref="ScreenPowerOn.Warm"/>). Null (logged) when the name is taken or
    /// the panels do not match the timeline.</summary>
    /// <example><c>var boot = ui.AddPowerOn("Boot", new PowerOnSpec { Panels = polygons, Seams = lines, Timeline = new PowerOnTimeline(blocks, rhythm) });</c></example>
    public ScreenPowerOn? AddPowerOn(string name, PowerOnSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (spec.Timeline == null || spec.Timeline.Panels != spec.Panels.Count)
        {
            Plugin.Error("ui-power-not-registered-timeline-must-name", "Power-on '" + name + "' not registered: its timeline must name a block for each of its " + spec.Panels.Count + " panels.");
            return null;
        }
        var id = Full(name, "screen layer");
        if (id == null) return null;
        var power = new ScreenPowerOn(id, spec);
        return Register(id, power) ? power : null;
    }
}

/// <summary>What a <see cref="ScreenPowerOn"/> plays. Coordinates are of the screen as a unit square: (0, 0) bottom left, (1, 1) top right.</summary>
public sealed class PowerOnSpec
{
    /// <summary>Each panel a convex polygon, its corners in order as x0, y0, x1, y1, ...; panel i belongs to block
    /// <c>Timeline.GroupOf(i)</c>.</summary>
    public IReadOnlyList<float[]> Panels { get; init; } = Array.Empty<float[]>();

    /// <summary>Each seam a straight line x0, y0, x1, y1.</summary>
    public IReadOnlyList<float[]> Seams { get; init; } = Array.Empty<float[]>();

    /// <summary>When each block comes on and settles, and how the power-down runs.</summary>
    public PowerOnTimeline Timeline { get; init; } = null!;

    /// <summary>The picture coming up is drawn monochrome in this colour.</summary>
    public Color Tint { get; init; } = new(0.80f, 0.94f, 1f, 1f);

    /// <summary>The glitch's channel split: the picture is drawn as three copies, slid left, in place and slid right (further the
    /// stronger the glitch, widest in a torn band), each in its colour; where they line up they add back to the picture. Torn bands
    /// and noise bars light up in these colours too. Red, green and blue by default.</summary>
    public Color ChannelLeft { get; init; } = new(1f, 0f, 0f, 1f);

    public Color ChannelMiddle { get; init; } = new(0f, 1f, 0f, 1f);

    public Color ChannelRight { get; init; } = new(0f, 0f, 1f, 1f);

    /// <summary>Against other exclusive screen layers: higher takes the screen, equal or lower waits.</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Your own panel shader instead of ForgeUI's glitch (it gets <c>_State</c> = glitch, black, opacity, seed and the
    /// <c>_Tint</c>, <c>_ChannelLeft</c>, <c>_ChannelMiddle</c> and <c>_ChannelRight</c> colours); null uses ForgeUI's.</summary>
    public Shader? Shader { get; init; }
}

/// <summary>A registered power-on (see <see cref="UIKitMod.AddPowerOn"/>), played by your clock: <see cref="Fade"/> to black before it,
/// <see cref="PowerOn"/> with the seconds since it started, <see cref="PowerDown"/> with the seconds since leaving began. Each call only
/// writes a vector per panel once it is warm; it hides itself when its part is over. Without its shader (logged once) the panels are
/// plain black until their block comes on.</summary>
public sealed class ScreenPowerOn
{
    private const string BuiltIn = "ForgeUI.PowerOn";
    private readonly PowerOnSpec _spec;
    private ScreenPanels? _panels;
    private bool _made, _visible;

    internal ScreenPowerOn(string id, PowerOnSpec spec)
    {
        Id = id;
        _spec = spec;
    }

    public string Id { get; }

    public int Priority => _spec.Priority;

    public PowerOnTimeline Timeline => _spec.Timeline;

    /// <summary>Builds the panels with the shader and draws them once off screen (the shader and its screen grab compile). Call it ahead.</summary>
    public bool Warm() => Panels()?.Warm() ?? false;

    /// <summary>Solid black at <paramref name="alpha"/> over everything (0 hides it).</summary>
    public void Fade(float alpha)
    {
        if (alpha <= 0f) { Hide(); return; }
        var panels = Panels();
        if (panels == null) return;
        _visible = true;
        for (var p = 0; p < panels.PanelCount; p++) panels.Panel(p, false, default, 0f);
        panels.Seams(0f);
        panels.Fade(alpha);
    }

    /// <summary>The power-on, <paramref name="s"/> seconds after it started; true (and hidden) once the picture is whole
    /// (<see cref="PowerOnTimeline.Assembled"/>).</summary>
    public bool PowerOn(float s)
    {
        var timeline = _spec.Timeline;
        if (timeline.IsAssembled(s)) { Hide(); return true; }
        var panels = Panels();
        if (panels == null) return false;
        _visible = true;
        panels.Fade(0f);
        for (var p = 0; p < panels.PanelCount; p++)
        {
            if (timeline.Plain(p, s)) { panels.Panel(p, false, default, 0f); continue; }
            var black = timeline.Black(p, s) ? 1f : 0f;
            panels.Panel(p, true, new Vector4(timeline.Shift(p, s), black, 1f, timeline.Seed(p)), black);
        }
        panels.Seams(timeline.SeamAlpha(s));
        return false;
    }

    /// <summary>Powering down, <paramref name="d"/> seconds in: block by block (the last first) the picture glitches and goes black, the
    /// black holds, then it clears; hidden once it has cleared.</summary>
    public void PowerDown(float d)
    {
        var timeline = _spec.Timeline;
        if (d >= timeline.DownSeconds) { Hide(); return; }
        var panels = Panels();
        if (panels == null) return;
        _visible = true;
        panels.Fade(0f);
        var alpha = timeline.DownAlpha(d);
        for (var p = 0; p < panels.PanelCount; p++)
        {
            var shift = timeline.DownShift(p, d);
            if (shift <= 0f) { panels.Panel(p, false, default, 0f); continue; }
            var black = timeline.DownBlack(p, d);
            panels.Panel(p, true, new Vector4(shift, black, alpha, timeline.Seed(p)), black * alpha);
        }
        panels.Seams(d > 0f ? alpha : 0f);
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        _panels?.Hide();
    }

    private ScreenPanels? Panels()
    {
        if (_made) return _panels;
        _made = true;
        var shader = _spec.Shader ?? UIKit.LoadShader(BuiltIn, PowerOnShaderBytes.Read, PowerOnShaderBytes.AssetName);
        _panels = new ScreenPanels(Id, new ScreenPanelsSpec
        {
            Panels = _spec.Panels,
            Seams = _spec.Seams,
            Shader = shader,
            Colours = new[] { ("_Tint", _spec.Tint), ("_ChannelLeft", _spec.ChannelLeft), ("_ChannelMiddle", _spec.ChannelMiddle), ("_ChannelRight", _spec.ChannelRight) },
        }, _spec.Priority);
        return _panels;
    }
}
