using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>Marks drawn on the game's crosshair layer: a thin ring, ticks, an optional dot and a landing circle (see
    /// <see cref="CrosshairAttachment"/>). Null (logged) when the name cannot be used or is taken.</summary>
    /// <example><c>var aim = ui.AddCrosshairAttachment("Aim"); aim.Visible = piloting; aim.Tighten = deployed ? 1f : 0f;</c></example>
    public CrosshairAttachment? AddCrosshairAttachment(string name)
    {
        var id = Full(name, "crosshair attachment");
        if (id == null) return null;
        var attachment = new CrosshairAttachment(id);
        return CrosshairAttachments.Add(attachment) ? attachment : null;
    }
}

/// <summary>
/// Marks around the crosshair (<see cref="UIKitMod.AddCrosshairAttachment"/>), made from copies of the game's own crosshair image on its
/// crosshair layer, so they are drawn, shown and hidden with the crosshair and follow it when it moves. Sizes are in pixels at 1080p.
/// Hiding the game's crosshair (<see cref="GameHudPart.Crosshair"/>) leaves these on.
/// </summary>
public sealed class CrosshairAttachment
{
    internal CrosshairAttachment(string id) => Id = id;

    public string Id { get; }

    public bool Visible { get; set; }

    /// <summary>Ring radius, pixels at 1080p; default 34.</summary>
    public float Radius { get; set; } = 34f;

    /// <summary>How many ticks around the ring (0 for none); default 4.</summary>
    public int Ticks { get; set; } = 4;

    public float TickLength { get; set; } = 8f;

    /// <summary>Draws the thin ring; default true.</summary>
    public bool Ring { get; set; } = true;

    /// <summary>Draws a centre dot (to stand in for the game's when that is hidden); default false.</summary>
    public bool Dot { get; set; }

    /// <summary>0..1: the ring and ticks close in to half their radius (eased over about 0.15 s).</summary>
    public float Tighten { get; set; }

    /// <summary>The ring, ticks and dot are hidden while the player aims down sights (the game draws no crosshair then) and come back
    /// when the aim is released, and so is the landing mark: nothing of the attachment is drawn while aiming. Default false.</summary>
    public bool HideWhileAiming { get; set; }

    public Color Colour { get; set; } = UIKitColors.Text;

    internal bool HasMark;
    internal Vector3 MarkAt;
    internal bool MarkOk;
    internal float Shown;

    /// <summary>A landing circle where <paramref name="world"/> appears on the screen; grey when not <paramref name="ok"/>.</summary>
    public void Mark(Vector3 world, bool ok)
    {
        HasMark = true;
        MarkAt = world;
        MarkOk = ok;
    }

    public void ClearMark() => HasMark = false;

    public void Remove() => CrosshairAttachments.Remove(this);
}

/// <summary>Builds and moves every <see cref="CrosshairAttachment"/> after the game's frame.</summary>
internal static class CrosshairAttachments
{
    private const int MaxTicks = 16;
    private static readonly Registry<CrosshairAttachment> Registered = new();
    private static readonly Dictionary<CrosshairAttachment, Pieces> Built = new();
    private static IntPtr _builtFor;
    private static Sprite? _ring, _dot, _markRing;

    private sealed class Pieces
    {
        internal GameObject Root = null!;
        internal Image Ring = null!, Dot = null!, Mark = null!;
        internal readonly Image[] Ticks = new Image[MaxTicks];
        internal Color Colour = new(0f, 0f, 0f, -1f);
    }

    internal static bool Add(CrosshairAttachment attachment)
    {
        var problem = Registered.Add(attachment.Id, attachment);
        if (problem == null) return true;
        Plugin.Error("ui-crosshair-attachment-not-added", "Crosshair attachment not added: " + problem + ".");
        return false;
    }

    internal static void Remove(CrosshairAttachment attachment)
    {
        if (!Registered.Remove(attachment.Id, attachment)) return;
        if (Built.Remove(attachment, out var pieces) && pieces.Root != null && !pieces.Root.WasCollected) Object.Destroy(pieces.Root);
    }

    internal static void LateUpdate()
    {
        var list = Registered.Ordered;
        if (list.Count == 0) return;
        try
        {
            var layer = GuiManager.CrosshairLayer;
            var circle = layer?.m_circleCrosshair;
            if (circle == null || circle.WasCollected || circle.m_crosshairImage == null) return;
            var parent = circle.transform.parent;
            if (parent == null) return;
            if (_builtFor != parent.Pointer)
            {
                foreach (var pieces in Built.Values)
                    if (pieces.Root != null && !pieces.Root.WasCollected) Object.Destroy(pieces.Root);
                Built.Clear();
                _builtFor = parent.Pointer;
            }
            var rect = parent.TryCast<RectTransform>();
            var size = rect != null ? rect.rect.size : new Vector2(1920f, 1080f);
            var unit = size.y > 1f ? size.y / 1080f : 1f;
            var now = ForgeCore.Frame.UnscaledDeltaTime;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (!Built.TryGetValue(a, out var pieces) || pieces.Root == null || pieces.Root.WasCollected)
                    Built[a] = pieces = Build(a, circle.m_crosshairImage, parent);
                Place(a, pieces, circle.transform.localPosition, size, unit, now);
            }
        }
        catch (Exception error) { Plugin.Error("ui-crosshair-attachments-failed-update", "Crosshair attachments failed to update: " + error); }
    }

    private static Pieces Build(CrosshairAttachment a, Image template, Transform parent)
    {
        _ring ??= NativeUi.Build("ForgeUI.CrosshairRing", 128, 128, 100f, (x, y) => RingAlpha(x, y, 128, 0.94f, 0.03f));
        _dot ??= NativeUi.Build("ForgeUI.CrosshairDot", 16, 16, 100f, (x, y) => RingAlpha(x, y, 16, 0.5f, 1f));
        _markRing ??= NativeUi.Build("ForgeUI.CrosshairMark", 64, 64, 100f, (x, y) => MarkAlpha(x, y));
        var root = new GameObject("ForgeUI.Crosshair." + a.Id) { layer = template.gameObject.layer };
        root.AddComponent<RectTransform>();
        root.transform.SetParent(parent, false);
        GameHud.Own(root);
        var pieces = new Pieces { Root = root };
        pieces.Ring = Copy(template, root.transform, _ring, "Ring");
        pieces.Dot = Copy(template, root.transform, _dot, "Dot");
        pieces.Mark = Copy(template, root.transform, _markRing, "Mark");
        for (var i = 0; i < MaxTicks; i++) pieces.Ticks[i] = Copy(template, root.transform, UIKitSprites.Pixel, "Tick" + i);
        return pieces;
    }

    /// <summary>A copy of the game's crosshair image object with only its image left, showing <paramref name="sprite"/>.</summary>
    private static Image Copy(Image template, Transform parent, Sprite sprite, string name)
    {
        var go = Object.Instantiate(template.gameObject, parent, false);
        go.name = name;
        for (var c = go.transform.childCount - 1; c >= 0; c--) Object.DestroyImmediate(go.transform.GetChild(c).gameObject);
        foreach (var behaviour in go.GetComponents<MonoBehaviour>())
            if (behaviour.TryCast<Graphic>() == null) Object.DestroyImmediate(behaviour);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        // The copy takes the template's switch, which is off while the game's crosshair is hidden.
        image.enabled = true;
        image.material = null;
        image.type = Image.Type.Simple;
        image.raycastTarget = false;
        image.rectTransform.anchoredPosition = Vector2.zero;
        image.rectTransform.localRotation = Quaternion.identity;
        image.rectTransform.localScale = Vector3.one;
        go.SetActive(false);
        return image;
    }

    private static void Place(CrosshairAttachment a, Pieces p, Vector3 centre, Vector2 size, float unit, float dt)
    {
        var visible = a.Visible && !HudFocus.Away;
        if (p.Root.activeSelf != visible) p.Root.SetActive(visible);
        if (!visible) return;
        if (p.Root.transform.localPosition != centre) p.Root.transform.localPosition = centre;
        a.Shown = Ease.Damp(a.Shown, Ease.Clamp01(a.Tighten), 1f / 0.15f, dt);
        var radius = a.Radius * (1f - 0.5f * a.Shown) * unit;
        if (p.Colour != a.Colour)
        {
            p.Colour = a.Colour;
            p.Ring.color = p.Dot.color = a.Colour;
            foreach (var t in p.Ticks) t.color = a.Colour;
        }
        var drawn = CrosshairRules.Drawn(true, a.HideWhileAiming, UIKit.Inventory.Aiming);
        Show(p.Ring, drawn && a.Ring);
        if (drawn && a.Ring) Size(p.Ring, Vector2.zero, radius * 2f, radius * 2f, 0f);
        Show(p.Dot, drawn && a.Dot);
        if (drawn && a.Dot) Size(p.Dot, Vector2.zero, 4f * unit, 4f * unit, 0f);
        var ticks = drawn ? Math.Clamp(a.Ticks, 0, MaxTicks) : 0;
        for (var i = 0; i < MaxTicks; i++)
        {
            var on = i < ticks;
            Show(p.Ticks[i], on);
            if (!on) continue;
            var angle = 90f - i * 360f / ticks;
            var r = angle * Mathf.Deg2Rad;
            var along = radius + a.TickLength * unit / 2f + 2f * unit;
            Size(p.Ticks[i], new Vector2(Mathf.Cos(r) * along, Mathf.Sin(r) * along), a.TickLength * unit, 2f * unit, angle);
        }
        var marked = CrosshairRules.Graphics(true, a.HideWhileAiming, UIKit.Inventory.Aiming, false, false, 0, a.HasMark) > 0 && Project(a.MarkAt, size, out var at);
        Show(p.Mark, marked);
        if (!marked) return;
        Project(a.MarkAt, size, out at);
        var colour = a.MarkOk ? UIKitColors.Accent : UIKitColors.Dim;
        if (p.Mark.color != colour) p.Mark.color = colour;
        Size(p.Mark, at - new Vector2(centre.x, centre.y), 22f * unit, 22f * unit, 0f);
    }

    /// <summary>A world point on the crosshair layer, relative to its parent's centre; false when behind the camera.</summary>
    private static bool Project(Vector3 world, Vector2 size, out Vector2 at)
    {
        at = default;
        var camera = HudScreen.WorldCamera();
        if (camera == null) return false;
        var view = camera.WorldToViewportPoint(world);
        if (view.z <= 0f) return false;
        at = new Vector2((view.x - 0.5f) * size.x, (view.y - 0.5f) * size.y);
        return true;
    }

    private static void Show(Image image, bool on)
    {
        if (image.gameObject.activeSelf != on) image.gameObject.SetActive(on);
    }

    private static void Size(Image image, Vector2 at, float width, float height, float degrees)
    {
        var t = image.rectTransform;
        var size = new Vector2(width, height);
        if (t.sizeDelta != size) t.sizeDelta = size;
        if (t.anchoredPosition != at) t.anchoredPosition = at;
        var rotation = Quaternion.Euler(0f, 0f, degrees);
        if (t.localRotation != rotation) t.localRotation = rotation;
    }

    private static float RingAlpha(int x, int y, int n, float radius, float width)
    {
        var u = (x + 0.5f) / n * 2f - 1f;
        var v = (y + 0.5f) / n * 2f - 1f;
        var d = MathF.Sqrt(u * u + v * v);
        var px = 2f / n;
        return Ease.Clamp01((width / 2f - MathF.Abs(d - radius + width / 2f)) / px + 0.5f);
    }

    /// <summary>A diamond outline with a gap at each corner: the landing mark.</summary>
    private static float MarkAlpha(int x, int y)
    {
        var u = MathF.Abs((x + 0.5f) / 32f - 1f);
        var v = MathF.Abs((y + 0.5f) / 32f - 1f);
        var d = u + v;
        var line = Ease.Clamp01((0.06f - MathF.Abs(d - 0.85f)) / (2f / 64f) + 0.5f);
        return MathF.Max(u, v) > 0.72f ? 0f : line;
    }
}
