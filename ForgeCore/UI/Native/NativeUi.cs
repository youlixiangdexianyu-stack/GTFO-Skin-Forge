using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using Il2CppArrays = ForgeCore.Il2CppArrays;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>Direction of an <see cref="UIKitSprites.Arrow"/>.</summary>
public enum ArrowDirection
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>Flat white sprites drawn once in code, for badges and simple shapes in your own UI. Tint them with the renderer's
/// colour. They are never unloaded.</summary>
public static class UIKitSprites
{
    private static Sprite? _pixel, _outline, _fade, _slant;
    private static readonly Dictionary<ArrowDirection, Sprite> Arrows = new(), Glyphs = new();
    private static readonly Sprite?[] DiscPies = new Sprite?[CooldownMath.PieSteps + 1], RingPies = new Sprite?[CooldownMath.PieSteps + 1];

    /// <summary>A white square, one unit across.</summary>
    public static Sprite Pixel => _pixel ??= NativeUi.Build("ForgeUI.Pixel", 4, 4, 4, (_, _) => 1f);

    /// <summary>A one-unit square outline whose line is a thirty-second of its side.</summary>
    public static Sprite Outline => _outline ??= NativeUi.Build("ForgeUI.Outline", 32, 32, 32,
        (x, y) => x < 1 || y < 1 || x > 30 || y > 30 ? 1f : 0f);

    /// <summary>One unit across and 1/16 unit tall, transparent at the left edge and opaque at the right (scale its height by 16).</summary>
    public static Sprite Fade => _fade ??= NativeUi.Build("ForgeUI.Fade", 64, 4, 64, (x, _) => x / 63f);

    /// <summary>A white 32 x 16 parallelogram (pixel units), its top edge shifted right by a quarter of the width: the slanted cut of
    /// bar cells.</summary>
    public static Sprite Slant => _slant ??= BuildSlant();

    /// <summary>One unit across: a soft glow band, solid along its length and fading across it (stretch it over a bar, a little taller).</summary>
    public static Sprite GlowBand => _glowBand ??= NativeUi.Build("ForgeUI.GlowBand", 4, 64, 4, (_, y) => ScreenPatterns.GlowBand(y / 63f));

    /// <summary>One unit across: a soft glow round a circle of 0.78 of the radius (lay it over a ring a little larger than the ring).</summary>
    public static Sprite GlowRing => _glowRing ??= NativeUi.Build("ForgeUI.GlowRing", 128, 128, 128, (x, y) => ScreenPatterns.GlowRing(x / 127f, y / 127f));

    /// <summary>One unit across: a soft round blob (behind a number).</summary>
    public static Sprite GlowBlob => _glowBlob ??= NativeUi.Build("ForgeUI.GlowBlob", 64, 64, 64, (x, y) => ScreenPatterns.GlowBlob(x / 63f, y / 63f));

    private static Sprite? _glowBand, _glowRing, _glowBlob, _chamfer, _chamferLine, _skew, _softBox, _softRing;

    /// <summary>The infinity sign, two units wide and one tall (<see cref="ShapePatterns.Infinity"/>).</summary>
    public static Sprite Infinity => _infinity ??= NativeUi.Build("ForgeUI.Infinity", ShapePatterns.InfinityWidth, ShapePatterns.InfinityHeight, ShapePatterns.InfinityHeight,
        (x, y) => ShapePatterns.Infinity(x + 0.5f, y + 0.5f));

    /// <summary>A diagonal band across the top left corner of a one-unit square, between its corner's half diagonal and its full one:
    /// drawn two cuts wide on a chamfered piece's top left corner, it lines the cut with a band one cut deep.</summary>
    public static Sprite CornerBand => _cornerBand ??= NativeUi.Build("ForgeUI.CornerBand", 32, 32, 32, (x, y) =>
    {
        // Texel distance along the diagonal from the top left corner (x right, y up): the band runs from 16 to 32.
        var d = (x + 0.5f + (32f - (y + 0.5f))) / 1.41421356f;
        return Math.Clamp(d - 16f / 1.41421356f + 0.5f, 0f, 1f) * Math.Clamp(32f / 1.41421356f - d + 0.5f, 0f, 1f);
    });

    private static Sprite? _infinity, _cornerBand;

    // Nine-sliced shapes (see ShapePatterns): drawn by HudCanvas with SpriteDrawMode.Sliced, their border is one unit.
    internal static Sprite Chamfer => _chamfer ??= NativeUi.BuildSliced("ForgeUI.Chamfer", ShapePatterns.Size, ShapePatterns.Border,
        new Vector4(ShapePatterns.Border, ShapePatterns.Border, ShapePatterns.Border, ShapePatterns.Border), ShapePatterns.ChamferFill);
    internal static Sprite ChamferLine => _chamferLine ??= NativeUi.BuildSliced("ForgeUI.ChamferLine", ShapePatterns.Size, ShapePatterns.Border,
        new Vector4(ShapePatterns.Border, ShapePatterns.Border, ShapePatterns.Border, ShapePatterns.Border), ShapePatterns.ChamferLine);
    internal static Sprite Skew => _skew ??= NativeUi.BuildSliced("ForgeUI.Skew", ShapePatterns.Size, ShapePatterns.Border,
        new Vector4(ShapePatterns.Border, 0f, ShapePatterns.Border, 0f), ShapePatterns.SkewFill);
    internal static Sprite SoftBox => _softBox ??= NativeUi.BuildSliced("ForgeUI.SoftBox", ShapePatterns.GlowSize, ShapePatterns.GlowEdge,
        new Vector4(ShapePatterns.GlowBorder, ShapePatterns.GlowBorder, ShapePatterns.GlowBorder, ShapePatterns.GlowBorder), ShapePatterns.GlowFill);
    internal static Sprite SoftRing => _softRing ??= NativeUi.BuildSliced("ForgeUI.SoftRing", ShapePatterns.GlowSize, ShapePatterns.GlowEdge,
        new Vector4(ShapePatterns.GlowBorder, ShapePatterns.GlowBorder, ShapePatterns.GlowBorder, ShapePatterns.GlowBorder), ShapePatterns.GlowRing);

    private static Sprite BuildSlant()
    {
        const int w = 32, h = 16, skew = 8;
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "ForgeUI.Slant", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color32[w * h];
        for (var y = 0; y < h; y++)
        {
            var left = (int)Math.Round(skew * (1f - y / (h - 1f)));
            for (var x = 0; x < w; x++)
                pixels[y * w + x] = x >= left && x < left + (w - skew) ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
        }
        Il2CppArrays.Upload(texture, pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 1f);
        sprite.name = "ForgeUI.Slant";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// <summary>A full arrow (thick shaft and a head) pointing in <paramref name="direction"/>, one unit across. Unlike the bare
    /// <see cref="Arrow"/> triangle it reads as up or down at HUD size.</summary>
    public static Sprite ArrowGlyph(ArrowDirection direction)
    {
        if (Glyphs.TryGetValue(direction, out var sprite)) return sprite;
        const int size = 64, samples = 4;
        const float scale = 12f / size;
        sprite = NativeUi.Build("ForgeUI.ArrowGlyph" + direction, size, size, size, (x, y) =>
        {
            var covered = 0;
            for (var sy = 0; sy < samples; sy++)
                for (var sx = 0; sx < samples; sx++)
                {
                    var px = (x + (sx + 0.5f) / samples) * scale;
                    var py = (y + (sy + 0.5f) / samples) * scale;
                    // Into the up-pointing arrow's frame (12 units, y up).
                    var (qx, qy) = direction switch
                    {
                        ArrowDirection.Up => (px, py),
                        ArrowDirection.Down => (px, 12f - py),
                        ArrowDirection.Left => (py, 12f - px),
                        _ => (py, px)
                    };
                    var shaft = qx >= 4.4f && qx <= 7.6f && qy >= 0.8f && qy <= 6.5f;
                    // Head: apex (6, 11.2), base at y = 5.6 spanning x 0.8 to 11.2.
                    var head = qy >= 5.6f && qy <= 11.2f && Math.Abs(qx - 6f) <= (11.2f - qy) / 5.6f * 5.2f;
                    if (shaft || head) covered++;
                }
            return covered / (float)(samples * samples);
        });
        Glyphs[direction] = sprite;
        return sprite;
    }

    /// <summary>A disc (or, with <paramref name="ring"/>, a ring) one unit across filled clockwise from the top by
    /// <paramref name="step"/> of <see cref="CooldownMath.PieSteps"/> sectors: a cooldown circle. Each is drawn on first use (a
    /// millisecond or two); <see cref="WarmPies"/> draws them ahead of time a few per frame.</summary>
    public static Sprite Pie(int step, bool ring = false)
    {
        step = Math.Clamp(step, 1, CooldownMath.PieSteps);
        var cache = ring ? RingPies : DiscPies;
        return cache[step] ??= BuildPie(step, ring);
    }

    /// <summary>Draws up to <paramref name="count"/> cooldown sectors that do not exist yet; true when all of them do.</summary>
    public static bool WarmPies(int count, bool ring = false)
    {
        var cache = ring ? RingPies : DiscPies;
        for (var step = 1; step <= CooldownMath.PieSteps; step++)
        {
            if (cache[step] != null) continue;
            if (count-- <= 0) return false;
            cache[step] = BuildPie(step, ring);
        }
        return true;
    }

    private static Sprite BuildPie(int step, bool ring)
    {
        const int size = 48, samples = 3;
        var filled = step / (float)CooldownMath.PieSteps * 6.2831855f;
        var name = "ForgeUI." + (ring ? "Ring" : "Disc") + step;
        return NativeUi.Build(name, size, size, size, (x, y) =>
        {
            var covered = 0;
            for (var sy = 0; sy < samples; sy++)
                for (var sx = 0; sx < samples; sx++)
                {
                    var dx = (x + (sx + 0.5f) / samples) / size - 0.5f;
                    var dy = (y + (sy + 0.5f) / samples) / size - 0.5f;
                    var radius = MathF.Sqrt(dx * dx + dy * dy);
                    if (radius > 0.5f || (ring && radius < 0.36f)) continue;
                    var angle = MathF.Atan2(dx, dy);
                    if (angle < 0f) angle += 6.2831855f;
                    if (angle <= filled) covered++;
                }
            return covered / (float)(samples * samples);
        });
    }

    /// <summary>A filled triangle pointing in <paramref name="direction"/>, one unit across.</summary>
    public static Sprite Arrow(ArrowDirection direction)
    {
        if (Arrows.TryGetValue(direction, out var sprite)) return sprite;
        // 12-unit glyphs, y pointing up.
        var points = direction switch
        {
            ArrowDirection.Up => new[] { new Vector2(6, 11), new Vector2(11, 2), new Vector2(1, 2) },
            ArrowDirection.Down => new[] { new Vector2(1, 10), new Vector2(11, 10), new Vector2(6, 1) },
            ArrowDirection.Left => new[] { new Vector2(1, 6), new Vector2(10, 11), new Vector2(10, 1) },
            _ => new[] { new Vector2(2, 11), new Vector2(11, 6), new Vector2(2, 1) }
        };
        const int size = 48;
        const float scale = size / 12f;
        sprite = NativeUi.Build("ForgeUI.Arrow" + direction, size, size, size, (x, y) =>
            NativeUi.Triangle(new Vector2((x + 0.5f) / scale, (y + 0.5f) / scale), points) * scale + 0.5f);
        Arrows[direction] = sprite;
        return sprite;
    }
}

/// <summary>Copies of the game's own text objects and the sprite builder behind <see cref="UIKitSprites"/>. Every texture and
/// sprite is marked <see cref="HideFlags.HideAndDontSave"/> so scene changes never unload them.</summary>
internal static class NativeUi
{
    /// <summary>Removes every localisation behaviour (the game's <c>LocalizedText</c> family and <c>TMP_Localizer</c>) on and
    /// under <paramref name="root"/>, so the game does not write its own string back. A copy made under an inactive window wakes
    /// only when the window is shown, and <c>TMP_Localizer.Awake</c> would then overwrite the text set before it.</summary>
    internal static void StripLocalization(GameObject root)
    {
        foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null) continue;
            var type = behaviour.GetIl2CppType().Name;
            if (type.Contains("Localiz", StringComparison.Ordinal)) Object.DestroyImmediate(behaviour);
        }
    }

    /// <summary>For the log: the components on <paramref name="go"/> and, one level down, its children with theirs.</summary>
    internal static string Structure(GameObject go)
    {
        var text = new StringBuilder("components [");
        foreach (var component in go.GetComponents<Component>()) text.Append(component == null ? "?" : component.GetIl2CppType().Name).Append(' ');
        var renderer = go.GetComponent<MeshRenderer>();
        text.Append("], materials ").Append(renderer != null ? renderer.sharedMaterials.Length : 0).Append(", children [");
        for (var i = 0; i < go.transform.childCount; i++)
        {
            var child = go.transform.GetChild(i).gameObject;
            text.Append(child.name).Append(" (");
            foreach (var component in child.GetComponents<Component>()) text.Append(component == null ? "?" : component.GetIl2CppType().Name).Append(' ');
            text.Append(") ");
        }
        return text.Append(']').ToString();
    }

    internal static void SetText(TextMeshPro text, string value)
    {
        if (text.text != value) text.SetText(value);
    }

    internal static void SetActive(Component component, bool active)
    {
        if (component.gameObject.activeSelf != active) component.gameObject.SetActive(active);
    }

    internal static Sprite Build(string name, int width, int height, float pixelsPerUnit, Func<int, int, float> alpha)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color32[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)Math.Round(Math.Clamp(alpha(x, y), 0f, 1f) * 255f));
        Il2CppArrays.Upload(texture, pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        sprite.name = name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// <summary>A square nine-sliced sprite: <paramref name="border"/> (left, bottom, right, top texels) keeps its shape at any drawn
    /// size; <paramref name="alpha"/> takes texel centres.</summary>
    internal static Sprite BuildSliced(string name, int size, float pixelsPerUnit, Vector4 border, Func<float, float, float> alpha)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Math.Round(Math.Clamp(alpha(x + 0.5f, y + 0.5f), 0f, 1f) * 255f));
        Il2CppArrays.Upload(texture, pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0u, SpriteMeshType.FullRect, border);
        sprite.name = name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// <summary>Signed distance inside a triangle of either winding: positive inside, in glyph units.</summary>
    internal static float Triangle(Vector2 p, Vector2[] t)
    {
        var inside = float.MaxValue;
        var orientation = Math.Sign((t[1].x - t[0].x) * (t[2].y - t[0].y) - (t[1].y - t[0].y) * (t[2].x - t[0].x));
        for (var i = 0; i < 3; i++)
        {
            var a = t[i];
            var b = t[(i + 1) % 3];
            var edge = b - a;
            var side = orientation * (edge.x * (p.y - a.y) - edge.y * (p.x - a.x)) / edge.magnitude;
            inside = Math.Min(inside, side);
        }
        return inside;
    }

    /// <summary>The camera that draws <paramref name="t"/>: the first camera on it or a parent, else a camera under the game's
    /// GUI root whose culling mask has its layer, else an enabled camera that has it. The game's UI cameras (the menu's
    /// <c>CellUI_Camera</c>) are disabled and rendered by the game's own UI pass, so they are not in <see cref="Camera.allCameras"/>:
    /// "no enabled camera draws this layer" is normal for game UI and says nothing about whether it is drawn.
    /// <c>CM_PageBase.Camera</c> is not it either: that is the apparel preview camera, null in the lobby.</summary>
    internal static Camera? CameraFor(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
        {
            var camera = p.GetComponent<Camera>();
            if (camera != null) return camera;
        }
        var bit = 1 << t.gameObject.layer;
        var gui = GuiManager.Current != null ? GuiManager.Current.m_root : null;
        if (gui != null)
            foreach (var camera in gui.GetComponentsInChildren<Camera>(true))
                if ((camera.cullingMask & bit) != 0) return camera;
        foreach (var camera in Camera.allCameras)
            if ((camera.cullingMask & bit) != 0) return camera;
        return null;
    }

    /// <summary>One line describing where a window is and whether it can be seen, for the log.</summary>
    internal static string Describe(Component? window)
    {
        if (window == null) return "[gone]";
        var t = window.transform;
        var drawn = 0;
        var renderers = window.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers) if (r.enabled && r.gameObject.activeInHierarchy) drawn++;
        var path = new StringBuilder();
        for (var p = t.parent; p != null && path.Length < 120; p = p.parent) path.Append('/').Append(p.name);
        return "[" + window.gameObject.name + " active=" + window.gameObject.activeInHierarchy + " world=" + Vec(t.position) +
               " local=" + Vec(t.localPosition) + " scale=" + Vec(t.lossyScale) + " drawn=" + drawn + "/" + renderers.Length +
               " parents=" + path + "]";
    }

    internal static string Vec(Vector3 v) =>
        "(" + v.x.ToString("0.#", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
        v.z.ToString("0.#", CultureInfo.InvariantCulture) + ")";

    internal static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
