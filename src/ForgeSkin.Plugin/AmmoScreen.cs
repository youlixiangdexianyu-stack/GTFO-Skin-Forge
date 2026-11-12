using Player;
using ForgeSkin.Format;
using UnityEngine;

namespace ForgeSkin.Plugin;

// A screen in the attachment's local XY plane, centred on its origin, visible face toward local -Z:
// border quad (full size), panel quad inset by the border width, and one quad per digit whose mesh is swapped
// between twelve glyph meshes ("0123456789 -") when the value changes.
sealed class AmmoScreen
{
    const int Blank = 10, Minus = 11;
    const float Step = 0.0002f; // metres between the stacked quads, toward the viewer
    static readonly int[] Pow10 = { 1, 10, 100, 1000, 10000, 100000, 1000000 };

    readonly ScreenSource _source;
    readonly bool _pad;
    readonly MeshFilter[] _cells;
    readonly Mesh[] _glyphs = new Mesh[12];
    readonly Material? _text;
    readonly Color _normal, _lowColor;
    readonly float _lowFraction;
    readonly bool _hasLow;
    readonly PlayerAmmoStorage? _storage;
    readonly AmmoType _ammoType;
    readonly int _clipSize;
    int _value = int.MinValue;
    readonly string _label;
    public readonly List<Material> Surfaces = new(); // border and panel, for attachment Flash
    bool _low;

    public AmmoScreen(SkinnedGear gear, Skin skin, Format.Screen s, Material template, GameObject root)
    {
        _source = s.Source;
        _label = gear.Label;
        _pad = s.PadZero;
        float w = s.Size[0], h = s.Size[1], bw = s.BorderWidth;
        if (s.BorderColor[3] > 0.01f)
            gear.Child(root, "border", gear.Own(Quad(w, h, 0, 1)), Flat(gear, template, s.BorderColor, s.Glow), 0f);
        if (s.PanelColor[3] > 0.01f)
            gear.Child(root, "panel", gear.Own(Quad(w - 2 * bw, h - 2 * bw, 0, 1)), Flat(gear, template, s.PanelColor, 1f), -Step);

        if (s.GlyphAtlas == null) { _cells = Array.Empty<MeshFilter>(); return; }
        var atlas = skin.Texture(s.GlyphAtlas, false);
        float dh = h * s.TextScale, dw = dh * (atlas.width / 12f) / atlas.height;
        float fit = (w - 2 * bw) * 0.95f / (dw * s.Digits);
        if (fit < 1) { dw *= fit; dh *= fit; }
        for (int g = 0; g < 12; g++) _glyphs[g] = gear.Own(Quad(dw, dh, g / 12f, (g + 1) / 12f));

        _text = gear.Own(new Material(template) { name = "ForgeSkin screen text" });
        _text.SetTexture(SkinnedGear.MainTex, atlas);
        _text.SetTexture(SkinnedGear.EmissiveMap, atlas);
        _text.SetTexture(SkinnedGear.MetallicGlossMap, Texture2D.blackTexture);
        _text.SetTexture(SkinnedGear.BumpMap, null);
        if (_text.HasProperty(SkinnedGear.Cutoff))
        {
            _text.SetFloat(SkinnedGear.Cutoff, 0.5f);
            _text.EnableKeyword("_ALPHATEST_ON");
        }
        _normal = Emission(s.TextColor, s.Glow);
        _hasLow = s.LowAmmoColor != null && s.Source is not (ScreenSource.Static or ScreenSource.Kills);
        _lowColor = _hasLow ? Emission(s.LowAmmoColor!, s.Glow) : _normal;
        _lowFraction = s.LowAmmoFraction;
        SkinnedGear.SetEmission(_text, _normal);

        _cells = new MeshFilter[s.Digits];
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = gear.Child(root, "digit" + i, _glyphs[Blank], _text, -2 * Step);
            _cells[i].transform.localPosition = new Vector3(dw * (i + 0.5f - _cells.Length * 0.5f), 0, -2 * Step);
        }

        if (s.Source == ScreenSource.Static)
        {
            var text = s.StaticText ?? "";
            for (int i = 0; i < _cells.Length && i < text.Length; i++)
                _cells[i].sharedMesh = _glyphs[text[i] is >= '0' and <= '9' ? text[i] - '0' : text[i] == '-' ? Minus : Blank];
        }
        else if (s.Source != ScreenSource.Kills) _storage = gear.Storage(out _ammoType, out _clipSize);
    }

    static Color Emission(float[] c, float glow) => new Color(c[0], c[1], c[2]) * (glow * SkinnedGear.EmissionUnit);

    // Opaque gear-shader quad lit by its own colour.
    Material Flat(SkinnedGear gear, Material template, float[] c, float glow)
    {
        var m = gear.Own(new Material(template) { name = "ForgeSkin screen panel" });
        Surfaces.Add(m);
        m.SetTexture(SkinnedGear.MainTex, Texture2D.blackTexture);
        m.SetTexture(SkinnedGear.MetallicGlossMap, Texture2D.blackTexture);
        m.SetTexture(SkinnedGear.BumpMap, null);
        m.SetTexture(SkinnedGear.EmissiveMap, Texture2D.whiteTexture);
        SkinnedGear.SetEmission(m, Emission(c, glow));
        return m;
    }

    static Mesh Quad(float w, float h, float u0, float u1)
    {
        float x = w * 0.5f, y = h * 0.5f;
        var mesh = new Mesh { name = "ForgeSkin quad" };
        mesh.vertices = new[] { new Vector3(-x, -y, 0), new Vector3(x, -y, 0), new Vector3(-x, y, 0), new Vector3(x, y, 0) };
        mesh.uv = new[] { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u0, 1), new Vector2(u1, 1) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.triangles = new[] { 0, 2, 3, 0, 3, 1 }; // clockwise seen from -Z
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
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
        if (_value == int.MinValue) ForgeSkinPlugin.Info($"{_label}: screen {_source} first value {value}");
        _value = value;

        int v = Math.Min(Math.Max(0, value), Pow10[_cells.Length] - 1);
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            int digit = v % 10;
            v /= 10;
            bool blank = !_pad && i != _cells.Length - 1 && digit == 0 && v == 0;
            _cells[i].sharedMesh = _glyphs[blank ? Blank : digit];
        }

        if (!_hasLow || _storage == null) return;
        int max = _source switch
        {
            ScreenSource.AmmoClip => _clipSize,
            ScreenSource.AmmoReserve => _storage.GetBulletMaxCap(_ammoType),
            _ => _clipSize + _storage.GetBulletMaxCap(_ammoType),
        };
        bool low = max > 0 && value <= max * _lowFraction;
        if (low == _low) return;
        _low = low;
        ForgeSkinPlugin.Info($"{_label}: screen {(low ? "low" : "normal")} colour at {value}/{max}");
        _text!.SetColor(SkinnedGear.EmissiveColor, low ? _lowColor : _normal);
    }
}
