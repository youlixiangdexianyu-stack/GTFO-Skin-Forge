using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Il2CppArrays = ForgeCore.Il2CppArrays;
using Object = UnityEngine.Object;

namespace ForgeUI;

public static partial class UIKit
{
    /// <summary>The player's <c>Screen / Visor</c> setting: false turns every visor overlay off.</summary>
    public static bool VisorEnabled => Plugin.VisorEnabled;
}

public sealed partial class UIKitMod
{
    /// <summary>Hit-direction flashes on the screen edge (an overlay layer, see <see cref="HitEdge"/>). Null (logged) when the name is taken.</summary>
    /// <example><c>var edge = ui.AddHitEdge("HitEdge"); edge.Flash(UIKit.Bearing(attacker), 0.6f);</c></example>
    public HitEdge? AddHitEdge(string name)
    {
        var id = Full(name, "screen layer");
        if (id == null) return null;
        var edge = new HitEdge(id);
        if (!Register(id, edge)) return null;
        ScreenStack.AddEdge(edge);
        return edge;
    }

    /// <summary>Puts an effect on the screen effect stack (merged by channel with every other mod's, drawn in one pass): a red flash, a vignette, low-health
    /// edges, blur, colour split. Effects on one channel merge by priority (only the highest present counts; the strongest of those shows, colours average by
    /// strength), so two mods asking for the same thing never add up to double. The returned handle is yours: <see cref="ScreenEffect.Withdraw"/> fades it out
    /// (releases it), <see cref="ScreenEffect.Set"/> changes a held one every frame. A timed effect (<see cref="ScreenEffectSpec.Seconds"/> above 0) releases itself.
    /// Everything pushed here ends at once when the level ends. An invalid spec or a full stack (64 effects) returns an inactive handle and logs an error.</summary>
    /// <example><c>var hit = ui.PushScreenEffect(ScreenChannel.Flash, new ScreenEffectSpec { Colour = Color.red, Strength = 0.4f, FadeOut = 0.4f });</c></example>
    public ScreenEffect PushScreenEffect(ScreenChannel channel, in ScreenEffectSpec spec) => ScreenStack.PushOwned(Id, channel, spec);

    /// <summary>A helmet visor over the screen (an overlay layer, see <see cref="Visor"/>). Null (logged) when the name is taken.</summary>
    /// <example><c>var visor = ui.AddVisor("Visor"); visor.Show(new VisorState { Strength = 0.62f });</c></example>
    public Visor? AddVisor(string name)
    {
        var id = Full(name, "screen layer");
        if (id == null) return null;
        var visor = new Visor(id);
        return Register(id, visor) ? visor : null;
    }
}

/// <summary>What a <see cref="Visor"/> shows. <c>new VisorState()</c> starts with the struts in place at full strength and nothing else.</summary>
public struct VisorState
{
    public VisorState()
    {
        Strength = 0f;
        Tint = default;
        Cracks = 0f;
        CrackCorner = ScreenCorner.TopLeft;
        Heat = 0f;
        Struts = 1f;
        Brackets = 1f;
        StrutSlide = 1f;
    }

    /// <summary>0..1: the shade's opacity at the screen's corners (clear inside 55 % of the way to the edge); 0.62 is a cockpit visor.</summary>
    public float Strength;
    /// <summary>The shade's colour; default (all zero) is a dark blue-grey.</summary>
    public Color Tint;
    /// <summary>0..1: how far the visor is cracked, each third bringing in one more layer of the crack picture from its impact corner
    /// (<see cref="VisorMath.CracksFor"/> gives it from an armour share); drawn at the player's <c>Screen / CracksMax</c>.</summary>
    public float Cracks;
    /// <summary>The corner the cracks start from; default top left (clear of the bottom corners' readouts).</summary>
    public ScreenCorner CrackCorner;
    /// <summary>0..1: overheating; an orange-red glow up from the bottom edge, and the shade turns red.</summary>
    public float Heat;
    /// <summary>0..1: the window struts' opacity (the slanted lines between the middle window and the side panes), times the player's
    /// <c>Screen / VisorFrame</c>. Default 1.</summary>
    public float Struts;
    /// <summary>0..1: the corner brackets' opacity, times the player's <c>Screen / VisorFrame</c>. Default 1.</summary>
    public float Brackets;
    /// <summary>0..1: the struts and brackets slide down into place from above as it goes from 0 to 1 (a power-up). Default 1.</summary>
    public float StrutSlide;
}
/// <summary>The textures and pieces the overlays share.</summary>
internal static class ScreenOverlays
{
    /// <summary>A white texture whose alpha is <paramref name="alpha"/>.</summary>
    internal static Texture2D Texture(string name, int width, int height, Func<int, int, float> alpha)
        => NativeUi.Build(name, Math.Max(2, width), Math.Max(2, height), 100f, alpha).texture;

    /// <summary>A delivered picture decoded once (possibly shrunk), kept as plain pixels so several masked textures can be cut from it.</summary>
    internal sealed class Picture
    {
        internal Color32[] Pixels = Array.Empty<Color32>();
        internal int Width, Height;
    }

    /// <summary>The picture at <paramref name="path"/>, <paramref name="shrink"/> times smaller (box-filtered); null when the file is
    /// missing or cannot be read (logged).</summary>
    internal static Picture? LoadPicture(string path, int shrink = 1)
    {
        if (!File.Exists(path))
        {
            Plugin.Error("ui-picture-missing", "Picture " + path + " is missing.");
            return null;
        }
        try
        {
            var source = ForgeCore.Images.Decode(File.ReadAllBytes(path), out var failure, readable: true) ?? throw new InvalidDataException(failure);
            source.name = "ForgeUI.Decode." + Path.GetFileName(path);
            source.hideFlags = HideFlags.HideAndDontSave;
            shrink = Math.Max(1, shrink);
            int sw = source.width, sh = source.height, w = Math.Max(1, sw / shrink), h = Math.Max(1, sh / shrink);
            var from = Il2CppArrays.ToManaged(source.GetPixels32());
            Object.Destroy(source);
            var pixels = new Color32[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (var dy = 0; dy < shrink; dy++)
                        for (var dx = 0; dx < shrink; dx++)
                        {
                            var sx = x * shrink + dx;
                            var sy = y * shrink + dy;
                            if (sx >= sw || sy >= sh) continue;
                            var p = from[sy * sw + sx];
                            r += p.r; g += p.g; b += p.b; a += p.a; n++;
                        }
                    n = Math.Max(1, n);
                    pixels[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            return new Picture { Pixels = pixels, Width = w, Height = h };
        }
        catch (Exception error)
        {
            Plugin.Error("ui-picture-could-not-be-loaded", "Picture " + path + " could not be loaded: " + error);
            return null;
        }
    }

    /// <summary><paramref name="picture"/> as its own texture with every pixel's alpha multiplied by <paramref name="mask"/>(u, v).</summary>
    internal static Texture2D MaskedPicture(string name, Picture picture, Func<float, float, float> mask)
    {
        int w = picture.Width, h = picture.Height;
        var pixels = new Color32[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = picture.Pixels[y * w + x];
                p.a = (byte)(p.a * Ease.Clamp01(mask((x + 0.5f) / w, (y + 0.5f) / h)));
                pixels[y * w + x] = p;
            }
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.HideAndDontSave };
        Il2CppArrays.Upload(texture, pixels);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        texture.Apply(false, true);
        return texture;
    }
    internal static Material Material(Texture texture, int queue)
    {
        var shader = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("the game has no Sprites/Default shader");
        var material = ScreenLayer.Material(shader, new Color(1f, 1f, 1f, 0f), queue);
        material.mainTexture = texture;
        return material;
    }

    /// <summary>A straight line from (u0, v0) to (u1, v1) in the unit square, <paramref name="px"/> pixels wide.</summary>
    internal static void Strip(Transform t, float u0, float v0, float u1, float v1, float px)
    {
        var a = ScreenLayer.At(u0, v0);
        var b = ScreenLayer.At(u1, v1);
        var d = b - a;
        t.localPosition = (a + b) * 0.5f + new Vector3(0f, 0f, 1f);
        t.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        t.localScale = new Vector3(d.magnitude + px / ScreenLayer.Height, px / ScreenLayer.Height, 1f);
    }

    internal static void SetColour(Material material, Color colour, ref Color shown)
    {
        if (shown == colour) return;
        shown = colour;
        material.color = colour;
    }

    internal static void Active(GameObject? go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    internal static void Destroy(Object? o)
    {
        if (o != null && !o.WasCollected) Object.Destroy(o);
    }
}

/// <summary>
/// Hit-direction flashes on the screen edge (<see cref="UIKitMod.AddHitEdge"/>): <see cref="Flash"/> lights a band on the edge where the
/// hit came from (straight ahead at the top, behind at the bottom), a bright chamfered orange-red line with a glow fading inwards in
/// armour scratches. At most six at once per edge; nearby hits join. Drawn by the screen effect stack's one full-screen pass (the six
/// strongest flashes of every hit edge), under the visor shade; its band is made for the screen's pixel size, so it is sharp at any resolution.
/// </summary>
public sealed class HitEdge
{
    private readonly EdgeFlashes _flashes = new();

    internal HitEdge(string id)
    {
        Id = id;
        _flashes.Seconds = Plugin.HitFlashSeconds;
    }

    public string Id { get; }

    internal EdgeFlashes Flashes => _flashes;

    /// <summary>A flash at full strength lasts this long (weaker ones are shorter, down to 40 %); default the player's
    /// <c>Screen / HitFlashSeconds</c> (0.3 s). Its brightness never passes the player's <c>Screen / HitFlashPeak</c> (35 %).</summary>
    public float Seconds
    {
        get => _flashes.Seconds;
        set => _flashes.Seconds = Math.Max(0.1f, value);
    }

    /// <summary>Builds the screen effect stack's pass and draws it once off screen. Call it ahead.</summary>
    public bool Warm() => ScreenStack.Warm();

    /// <summary>Lights the edge toward <paramref name="azimuth"/> (degrees from where the player looks) at <paramref name="strength"/> 0..1.</summary>
    public void Flash(float azimuth, float strength) => _flashes.Hit(azimuth, strength, ForgeCore.Frame.GameTime);

    /// <summary>Everything off at once.</summary>
    public void Clear() => _flashes.Clear();
}

/// <summary>
/// A cockpit visor over the screen (<see cref="UIKitMod.AddVisor"/>): a shade darkening the corners, window struts and corner brackets
/// drawn as lines at pixel width (the struts placed by the screen height from the middle, so the middle window keeps its width on any
/// aspect; the brackets on the physical corners, each with its own opacity), the delivered crack picture in layers from one
/// corner and an overheat glow along the bottom edge, all under the player's <c>Screen</c> settings. Off entirely while the player's
/// <c>Screen / Visor</c> setting is off. The shade is a vignette effect on the screen effect stack (it merges with other mods'
/// vignettes); the heat glow, scanline interference and boot sweep are drawn by the stack's full-screen pass; the struts, brackets and
/// cracks are the visor's own pieces over it.
/// </summary>
public sealed class Visor
{
    private const float CrackSide = 0.75f;
    private static readonly Color DefaultTint = new(0.05f, 0.08f, 0.11f, 1f), Red = new(0.95f, 0.22f, 0.12f, 1f), Line = new(0.85f, 0.95f, 0.98f, 1f);
    private readonly ScreenLayer.User _user = new();
    private readonly GameObject?[] _crackPieces = new GameObject?[3];
    private readonly Material?[] _crackMaterials = new Material?[3];
    private readonly Color[] _crackShown = new Color[3];
    // Line sets: 0 the struts, 1 the corner brackets; each a line colour and the fringe's red and cyan copies.
    private readonly GameObject?[] _lineSets = new GameObject?[2];
    private readonly Material?[,] _lineMaterials = new Material?[2, 3];
    private readonly Color[,] _lineShown = new Color[2, 3];
    private GameObject? _group, _frame;
    private readonly List<Texture2D> _textures = new();
    private ScreenEffect _shade;
    private float _interferenceAt = -10f, _interferenceStrength, _interferenceSeconds = 0.25f, _sweepAt = -10f, _sweepSeconds = 1f, _dropShown = -1f;
    private ScreenCorner _cornerShown = (ScreenCorner)255;
    private int _generation = -1;
    private bool _failed, _hasCracks;

    internal Visor(string id) => Id = id;

    public string Id { get; }

    /// <summary>The PNG file of the crack drawing (white lines on transparency, impact corner at the top left) that <see cref="VisorState.Cracks"/>
    /// brings in. Set it before the first <see cref="Warm"/> or <see cref="Show"/>; without one the visor has no cracks. A missing or
    /// unreadable file is logged and the visor has no cracks. The picture belongs to the mod that sets it.</summary>
    public string? CracksPicture { get; set; }

    public bool Warm()
    {
        if (!Build() || !ScreenStack.Warm()) return false;
        ScreenOverlays.Active(_group, true);
        ScreenLayer.RenderOnce();
        ScreenOverlays.Active(_group, false);
        return true;
    }

    /// <summary>Scanline interference on the screen edges for <paramref name="seconds"/> (a hit), at <paramref name="strength"/> 0..1; the
    /// middle of the screen stays clear. Drawn while the visor is shown.</summary>
    public void Interfere(float strength, float seconds = 0.25f)
    {
        _interferenceAt = ForgeCore.Frame.GameTime;
        _interferenceStrength = Ease.Clamp01(strength);
        _interferenceSeconds = Math.Max(0.05f, seconds);
    }

    /// <summary>A thin bright line sweeping the screen from top to bottom over <paramref name="seconds"/> with a faint afterglow (a boot
    /// check). Drawn while the visor is shown.</summary>
    public void Sweep(float seconds)
    {
        _sweepAt = ForgeCore.Frame.GameTime;
        _sweepSeconds = Math.Max(0.1f, seconds);
    }

    /// <summary>Draws the visor with <paramref name="state"/>; call it every frame or when it changes.</summary>
    public void Show(in VisorState state)
    {
        if (!Plugin.VisorEnabled) { Hide(); return; }
        if (!Build()) return;
        var now = ForgeCore.Frame.GameTime;
        var tint = state.Tint == default ? DefaultTint : state.Tint;
        var heat = Ease.Clamp01(state.Heat);
        var shade = new Color(VisorMath.HeatChannel(tint.r, Red.r * 0.5f, heat), VisorMath.HeatChannel(tint.g, Red.g * 0.3f, heat),
            VisorMath.HeatChannel(tint.b, Red.b * 0.3f, heat), 1f);
        Shade(Ease.Clamp01(state.Strength), shade);
        var line = Color.Lerp(Line, Red, heat * 0.6f);
        var fringe = UIKit.HudFringe.Effective(Plugin.Fringe, now);
        var anyLine = false;
        for (var set = 0; set < 2; set++)
        {
            var lineAlpha = Plugin.VisorFrame * Ease.Clamp01(set == 0 ? state.Struts : state.Brackets);
            ScreenOverlays.SetColour(_lineMaterials[set, 0]!, new Color(line.r, line.g, line.b, lineAlpha), ref _lineShown[set, 0]);
            ScreenOverlays.SetColour(_lineMaterials[set, 1]!, new Color(1f, 0.25f, 0.2f, lineAlpha * fringe), ref _lineShown[set, 1]);
            ScreenOverlays.SetColour(_lineMaterials[set, 2]!, new Color(0.3f, 0.55f, 1f, lineAlpha * fringe), ref _lineShown[set, 2]);
            ScreenOverlays.Active(_lineSets[set], lineAlpha > 0.003f);
            anyLine |= lineAlpha > 0.003f;
        }
        ScreenOverlays.Active(_frame, anyLine);
        var drop = VisorMath.StrutDrop(state.StrutSlide);
        if (Math.Abs(drop - _dropShown) > 1e-4f)
        {
            _dropShown = drop;
            _frame!.transform.localPosition = new Vector3(0f, drop, 0f);
        }
        Cracks(state);
        Effects(now, new Color(1f, 0.36f, 0.14f, heat * 0.5f));
        ScreenOverlays.Active(_group, true);
        _user.Use(true);
    }

    /// <summary>The shade as a held vignette on the screen effect stack, pushed again after it was withdrawn.</summary>
    private void Shade(float strength, Color colour)
    {
        if (_shade.Set(strength, colour)) return;
        _shade = ScreenStack.Push(Id, ScreenChannel.Vignette, new ScreenEffectSpec { Strength = strength, Colour = colour, Seconds = 0f, FadeOut = 0f });
    }

    private void Cracks(in VisorState state)
    {
        if (!_hasCracks) return;
        if (state.CrackCorner != _cornerShown)
        {
            _cornerShown = state.CrackCorner;
            var right = state.CrackCorner is ScreenCorner.TopRight or ScreenCorner.BottomRight;
            var bottom = state.CrackCorner is ScreenCorner.BottomLeft or ScreenCorner.BottomRight;
            // The picture's impact point is its bottom right corner: mirror it onto the chosen corner.
            var sx = right ? 1f : -1f;
            var sy = bottom ? 1f : -1f;
            var corner = ScreenLayer.At(right ? 1f : 0f, bottom ? 0f : 1f);
            var centre = corner + new Vector3(-sx * CrackSide / 2f, sy * CrackSide / 2f, 0f);
            foreach (var piece in _crackPieces)
            {
                if (piece == null) continue;
                piece.transform.localPosition = new Vector3(centre.x, centre.y, 1f);
                piece.transform.localScale = new Vector3(sx * CrackSide, sy * CrackSide, 1f);
            }
        }
        for (var i = 0; i < 3; i++)
        {
            var alpha = VisorMath.CrackLayer(state.Cracks, i) * Plugin.CrackMax;
            ScreenOverlays.SetColour(_crackMaterials[i]!, new Color(0.85f, 0.9f, 0.92f, alpha), ref _crackShown[i]);
            ScreenOverlays.Active(_crackPieces[i], alpha > 0.003f);
        }
    }

    /// <summary>The heat glow, scanline interference and boot sweep, handed to the screen effect stack.</summary>
    private void Effects(float now, Color heat)
    {
        var t = (now - _interferenceAt) / _interferenceSeconds;
        var interference = default(Color);
        var jitter = 0f;
        if (t >= 0f && t < 1f && _interferenceStrength > 0f)
        {
            interference = new Color(0.8f, 0.94f, 1f, _interferenceStrength * 0.3f * (1f - t));
            // The lines jump a few pixels every frame.
            jitter = (UnityEngine.Random.value - 0.5f) * 12f / ScreenLayer.Height;
        }
        var s = (now - _sweepAt) / _sweepSeconds;
        var sweep = default(Color);
        // The line runs from the top edge to below the bottom edge so its trail leaves the screen too.
        var lineY = 0.5f - s * 1.3f;
        if (s >= 0f && s < 1f) sweep = new Color(0.8f, 0.94f, 1f, 0.45f * (1f - s * s));
        ScreenStack.Visor(heat, interference, jitter, sweep, lineY);
    }

    public void Hide()
    {
        if (_generation == ScreenLayer.Generation) ScreenOverlays.Active(_group, false);
        _shade.Withdraw();
        ScreenStack.HideVisor();
        _user.Use(false);
    }

    private bool Build()
    {
        if (_failed || !ScreenLayer.Ensure()) return false;
        if (_generation == ScreenLayer.Generation) return true;
        try
        {
            foreach (var t in _textures) ScreenOverlays.Destroy(t);
            _textures.Clear();
            foreach (var m in _lineMaterials) ScreenOverlays.Destroy(m);
            foreach (var m in _crackMaterials) ScreenOverlays.Destroy(m);
            _user.Reset();
            var aspect = ScreenLayer.Aspect;
            _group = ScreenLayer.Group(Id);
            // Cracks: the delivered picture in three layers by distance from its impact corner; without it there are no cracks.
            var cracks = CracksPicture == null ? null : ScreenOverlays.LoadPicture(CracksPicture, 2);
            _hasCracks = cracks != null;
            for (var i = 0; i < 3 && cracks != null; i++)
            {
                var layer = i;
                var texture = ScreenOverlays.MaskedPicture(Id + ".Cracks" + i, cracks, (u, v) => ScreenPatterns.CrackLayer(u, v, layer));
                _textures.Add(texture);
                _crackMaterials[i] = ScreenOverlays.Material(texture, 3903);
                _crackShown[i] = new Color(1f, 1f, 1f, 0f);
                _crackPieces[i] = ScreenLayer.Piece("Cracks" + i, ScreenLayer.Quad, _crackMaterials[i]!, _group);
                _crackPieces[i]!.SetActive(false);
            }
            _cornerShown = (ScreenCorner)255;
            var white = Keep(NativeUi.Build(Id + ".White", 4, 4, 4f, (_, _) => 1f).texture);
            _frame = new GameObject(Id + ".Frame") { layer = _group.layer };
            _frame.transform.SetParent(_group.transform, false);
            var px = Math.Max(1f, MathF.Round(1.4f * ScreenLayer.Height / 1080f));
            var shift = Math.Max(1f, ScreenLayer.Height / 1080f) / Math.Max(1, ScreenLayer.Width);
            var lines = 0;
            var sets = new[] { VisorMath.Struts(aspect), VisorMath.Brackets(aspect) };
            for (var set = 0; set < 2; set++)
            {
                for (var k = 0; k < 3; k++) _lineMaterials[set, k] = ScreenOverlays.Material(white, 3904);
                var holder = _lineSets[set] = new GameObject(Id + (set == 0 ? ".Struts" : ".Brackets")) { layer = _group.layer };
                holder.transform.SetParent(_frame.transform, false);
                foreach (var s in sets[set])
                {
                    var piece = ScreenLayer.Piece("Line" + lines, ScreenLayer.Quad, _lineMaterials[set, 0]!, holder);
                    ScreenOverlays.Strip(piece.transform, s[0], s[1], s[2], s[3], px);
                    var red = ScreenLayer.Piece("Line" + lines + "R", ScreenLayer.Quad, _lineMaterials[set, 1]!, holder);
                    ScreenOverlays.Strip(red.transform, s[0] - shift, s[1], s[2] - shift, s[3], px);
                    var cyan = ScreenLayer.Piece("Line" + lines + "C", ScreenLayer.Quad, _lineMaterials[set, 2]!, holder);
                    ScreenOverlays.Strip(cyan.transform, s[0] + shift, s[1], s[2] + shift, s[3], px);
                    lines++;
                }
                for (var k = 0; k < 3; k++) _lineShown[set, k] = new Color(0f, 0f, 0f, -1f);
            }
            _dropShown = -1f;
            _group.SetActive(false);
            _generation = ScreenLayer.Generation;
        }
        catch (Exception error)
        {
            _failed = true;
            Plugin.Error("ui-screen-layer-could-not-be-built", "Screen layer '" + Id + "' could not be built and is off: " + error);
            return false;
        }
        return true;
    }

    /// <summary>Remembers a texture drawn here, to destroy on the next rebuild; delivered pictures belong to the icon cache and are kept.</summary>
    private Texture2D Keep(Texture2D texture)
    {
        if (!texture.name.StartsWith("ForgeUI.Icon.", StringComparison.Ordinal)) _textures.Add(texture);
        return texture;
    }
}
