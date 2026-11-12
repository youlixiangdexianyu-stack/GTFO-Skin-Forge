using Player;
using ForgeSkin.Format;
using UnityEngine;

namespace ForgeSkin.Plugin;

// A screen in the attachment's local XY plane, centred on its origin, visible face toward local -Z:
// border frame (a ring BorderWidth wide inside the edges), panel quad inside it, and one cell per digit whose mesh is
// swapped between the pack's glyph meshes ("0123456789 -", real stroke geometry) when the value changes. Everything is
// opaque gear shader (it has no transparency): the colour as albedo, plus the same colour as emission when it glows in game.
sealed class AmmoScreen
{
    const int Blank = 10;
    const float Step = 0.0002f; // metres between the stacked quads, toward the viewer
    static readonly int[] Pow10 = { 1, 10, 100, 1000, 10000, 100000, 1000000 };

    readonly ScreenSource _source;
    readonly bool _pad;
    readonly MeshFilter[] _cells;
    readonly Mesh?[] _glyphs = new Mesh?[12];
    readonly Material? _text, _border;
    readonly Material _template;
    readonly float[] _textColor;
    readonly float[]? _lowColor;
    // Emission per step of the Glow slider. Measured on the game's own glowing gear materials (Cell/Player gear shaders,
    // _EmissiveColor x _EmissiveMap): the flashlight lens 1.0, HSU lights 0.54, the bio tracker / sentry screens 0.45-0.74,
    // shotgun illumination 0.07, the dimmest sight dots 0.006-0.035. With 0.2 the default Glow 1.5 gives 0.3 (between the
    // shotgun lights and the HSU lights) and the slider's top, 5, matches the flashlight lens.
    const float GlowUnit = 0.2f;

    readonly float _gain; // emission multiplier of text and border; 0: not emissive
    readonly float _lowFraction;
    readonly bool _gradient; // blend the low colour in as the clip approaches the threshold
    readonly float _gradientStart; // clip fraction where the blend begins
    readonly bool _hasLow;
    readonly PlayerAmmoStorage? _storage;
    readonly AmmoType _ammoType;
    readonly int _clipSize;
    int _value = int.MinValue;
    readonly string _label;
    public readonly List<Material> Surfaces = new(); // border and panel, for attachment Flash
    bool _low;
    float _mix; // 0 = text colour, 1 = low colour
    readonly float[] _mixed = new float[3];

    public AmmoScreen(SkinnedGear gear, Skin skin, Format.Screen s, Material template, GameObject root)
    {
        _source = s.Source;
        _label = gear.Label;
        _pad = s.PadZero;
        _template = template;
        _gain = s.GlowInGame ? s.Glow * GlowUnit : 0f;
        _textColor = s.TextColor;
        float w = s.Size[0], h = s.Size[1], bw = Math.Min(s.BorderWidth, Math.Min(w, h) * 0.5f);
        if (s.BorderColor[3] > 0.01f && bw > 0)
            gear.Child(root, "border", gear.Own(Frame(w, h, bw)), _border = Surface(gear, s.BorderColor, _gain), -Step);
        if (s.PanelColor[3] > 0.01f)
            gear.Child(root, "panel", gear.Own(Quad(w - 2 * bw, h - 2 * bw)), Surface(gear, s.PanelColor, s.GlowInGame ? SkinnedGear.EmissionUnit : 0f), 0f);

        _text = Surface(gear, s.TextColor, _gain);
        Surfaces.Remove(_text);
        _hasLow = s.LowAmmoColor != null && s.Source is not (ScreenSource.Static or ScreenSource.Kills);
        _lowColor = _hasLow ? s.LowAmmoColor : null;
        _lowFraction = s.LowAmmoFraction;
        _gradient = s.LowAmmoGradient;
        _gradientStart = s.LowAmmoGradientStart;
        // Glyph meshes are in cell units (height 1): a cell is scaled by the text height, shrunk until the text fits.
        float inner = (w - 2 * bw) * 0.95f, dh = h * s.TextScale;
        _cells = Array.Empty<MeshFilter>();

        if (s.Source == ScreenSource.Static)
        {
            if (s.TextMesh == null) return;
            dh *= Math.Min(1f, inner / (dh * s.TextAspect));
            gear.Child(root, "text", skin.Mesh(s.TextMesh), _text, -2 * Step).transform.localScale = new Vector3(dh, dh, 1);
            return;
        }
        if (s.GlyphMeshes == null) return;
        for (int g = 0; g < 12; g++) _glyphs[g] = s.GlyphMeshes[g] is { } path ? skin.Mesh(path) : null;
        if (s.Source != ScreenSource.Kills) _storage = gear.Storage(out _ammoType, out _clipSize);
        // Automatic width: the digits of the largest value the source can show; kills have no maximum and grow up to 4.
        _grow = s.Digits == 0 && s.Source == ScreenSource.Kills;
        int width = s.Digits > 0 ? s.Digits : _grow ? 4 : _storage != null ? DigitCount(Max()) : 2;
        _dw = dh * s.GlyphAspect;
        float fit = inner / (_dw * width);
        if (fit < 1) { _dw *= fit; dh *= fit; }

        _cells = new MeshFilter[width];
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = gear.Child(root, "digit" + i, _glyphs[Blank], _text, -2 * Step);
            _cells[i].transform.localScale = new Vector3(dh, dh, 1);
        }
        Place(width);
    }

    readonly bool _grow; // automatic kills: as many cells shown as the value has digits, centred
    readonly float _dw;
    int _shown;

    static int DigitCount(int n)
    {
        int count = 1;
        for (n = Math.Max(0, n); n >= 10; n /= 10) count++;
        return Math.Min(count, Pow10.Length - 1);
    }

    // Centres the first `shown` cells.
    void Place(int shown)
    {
        if (shown == _shown) return;
        _shown = shown;
        for (int i = 0; i < shown; i++) _cells[i].transform.localPosition = new Vector3(_dw * (i + 0.5f - shown * 0.5f), 0, -2 * Step);
    }

    // The largest value an ammo source can show (needs _storage).
    int Max() => _source switch
    {
        ScreenSource.AmmoClip => _clipSize,
        ScreenSource.AmmoReserve => _storage!.GetBulletMaxCap(_ammoType),
        _ => _clipSize + _storage!.GetBulletMaxCap(_ammoType),
    };

    // Opaque gear-shader surface in one colour: the colour as albedo (a 1x1 texture; the shader has no colour
    // property) and, with gain > 0, the same colour times gain as emission. The panel uses the shader's unit (its colour
    // as itself, no bloom); text and border the glow.
    Material Surface(SkinnedGear gear, float[] c, float gain)
    {
        var m = gear.Own(new Material(_template) { name = "ForgeSkin screen" });
        Surfaces.Add(m);
        m.SetTexture(SkinnedGear.MetallicGlossMap, Texture2D.blackTexture);
        m.SetTexture(SkinnedGear.BumpMap, null);
        m.SetTexture(SkinnedGear.EmissiveMap, Texture2D.whiteTexture);
        m.SetTexture(SkinnedGear.MainTex, gear.Own(new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "ForgeSkin screen colour" }));
        Paint(m, c, gain);
        return m;
    }

    static void Paint(Material m, float[] c, float gain)
    {
        var colour = new Color(c[0], c[1], c[2]);
        var albedo = m.GetTexture(SkinnedGear.MainTex).Cast<Texture2D>();
        albedo.SetPixel(0, 0, colour);
        albedo.Apply(false, false);
        if (gain > 0) SkinnedGear.SetEmission(m, colour * gain);
        else
        {
            if (m.HasProperty(SkinnedGear.EmissiveColor)) m.SetColor(SkinnedGear.EmissiveColor, Color.black);
            SkinnedGear.SetEmissive(m, false);
        }
    }

    static Mesh Quad(float w, float h)
    {
        float x = w * 0.5f, y = h * 0.5f;
        var mesh = new Mesh { name = "ForgeSkin quad" };
        mesh.vertices = new[] { new Vector3(-x, -y, 0), new Vector3(x, -y, 0), new Vector3(-x, y, 0), new Vector3(x, y, 0) };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.triangles = new[] { 0, 2, 3, 0, 3, 1 }; // clockwise seen from -Z
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // The ring between the outer edges and the edges inset by bw: four strips, clockwise seen from -Z.
    static Mesh Frame(float w, float h, float bw)
    {
        float x = w * 0.5f, y = h * 0.5f, ix = x - bw, iy = y - bw;
        var v = new[]
        {
            new Vector3(-x, -y, 0), new Vector3(x, -y, 0), new Vector3(x, y, 0), new Vector3(-x, y, 0),
            new Vector3(-ix, -iy, 0), new Vector3(ix, -iy, 0), new Vector3(ix, iy, 0), new Vector3(-ix, iy, 0),
        };
        var uv = new Vector2[8];
        var normals = new Vector3[8];
        for (int i = 0; i < 8; i++) (uv[i], normals[i]) = (new Vector2(v[i].x / w + 0.5f, v[i].y / h + 0.5f), Vector3.back);
        var tris = new int[24];
        for (int k = 0; k < 4; k++)
        {
            int o0 = k, o1 = (k + 1) % 4, i0 = 4 + k, i1 = 4 + (k + 1) % 4;
            (tris[k * 6], tris[k * 6 + 1], tris[k * 6 + 2]) = (o0, i0, i1);
            (tris[k * 6 + 3], tris[k * 6 + 4], tris[k * 6 + 5]) = (o0, i1, o1);
        }
        var mesh = new Mesh { name = "ForgeSkin frame", vertices = v, uv = uv, normals = normals, triangles = tris };
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // What the screen really shows, for checking against the editor in one in-game run.
    public string Describe()
    {
        static string M(Material? m) => m == null ? "none" :
            $"albedo {m.GetTexture(SkinnedGear.MainTex)?.TryCast<Texture2D>()?.GetPixel(0, 0).ToString() ?? "?"}, " +
            $"_EmissiveColor {(m.HasProperty(SkinnedGear.EmissiveColor) ? m.GetColor(SkinnedGear.EmissiveColor).ToString() : "-")}, " +
            $"_EnableEmissive {(m.HasProperty(SkinnedGear.EnableEmissive) ? m.GetFloat(SkinnedGear.EnableEmissive) : -1)}, ENABLE_EMISSIVE {m.IsKeywordEnabled("ENABLE_EMISSIVE")}";
        return $"emission gain {_gain}, text [{M(_text)}]{(_low ? " (low)" : "")}, border [{M(_border)}], " +
               $"template {_template.name} / {_template.shader.name} keywords [{string.Join(" ", _template.shaderKeywords)}]";
    }

    public void Update(int clip, int kills)
    {
        if (_cells.Length == 0 || _source == ScreenSource.Static) return;
        int value;
        if (_source == ScreenSource.Kills) value = kills;
        else if (_storage == null) return;
        else
        {
            int reserve = _source == ScreenSource.AmmoClip ? 0 : _storage.GetBulletsInPack(_ammoType);
            value = _source switch { ScreenSource.AmmoClip => clip, ScreenSource.AmmoReserve => reserve, _ => clip + reserve };
        }
        if (value == _value) return;
        bool first = _value == int.MinValue;
        if (first) ForgeSkinPlugin.Info($"{_label}: screen {_source} first value {value}");
        _value = value;

        int v = Math.Min(Math.Max(0, value), Pow10[_cells.Length] - 1);
        int shown = _grow ? DigitCount(v) : _cells.Length;
        Place(shown);
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (i >= shown) { _cells[i].sharedMesh = _glyphs[Blank]; continue; }
            int digit = v % 10;
            v /= 10;
            bool blank = !_pad && i != shown - 1 && digit == 0 && v == 0;
            _cells[i].sharedMesh = _glyphs[blank ? Blank : digit];
        }

        if (!_hasLow || _storage == null) return;
        int max = Max();
        SetMix(Tint(value, max), value, max, first);
    }

    // 0: text colour, 1: low colour. With the gradient on, the tint starts as the clip falls below the gradient start
    // and reaches the low colour at the threshold; off, it switches at the threshold.
    float Tint(int value, int max)
    {
        if (max <= 0) return 0f; // limit unknown: keep the text colour, as the editor preview does
        float fraction = Math.Min(1f, (float)value / max);
        float from = _gradient ? Math.Max(_lowFraction, _gradientStart) : _lowFraction;
        return from <= _lowFraction
            ? fraction <= _lowFraction ? 1f : 0f
            : Mathf.Clamp01((from - fraction) / (from - _lowFraction));
    }

    void SetMix(float mix, int value, int max, bool first)
    {
        if (first && mix == 0f) return; // the text already has its colour
        if (!first && mix == _mix) return;
        bool wasLow = _low;
        bool wasTinted = _mix > 0f;
        _mix = mix;
        _low = mix >= 1f;
        for (int i = 0; i < 3; i++) _mixed[i] = Mathf.Lerp(_textColor[i], _lowColor![i], mix);
        Paint(_text!, _mixed, _gain);
        if (wasLow != _low)
            ForgeSkinPlugin.Info($"{_label}: screen {(_low ? "low" : "normal")} colour at {value}/{max} (tint {mix.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}): {Describe()}");
        else if (!wasTinted && mix > 0f)
            ForgeSkinPlugin.Info($"{_label}: screen low-ammo tint starts at {value}/{max}");
    }
}
