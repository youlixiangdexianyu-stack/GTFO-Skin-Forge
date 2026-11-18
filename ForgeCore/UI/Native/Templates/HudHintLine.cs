using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>One piece of a <see cref="HintRow"/>: a plain word or phrase, or a key name drawn in a thin box.</summary>
public readonly struct HintPart
{
    private HintPart(UIText text, bool key, bool wide)
    {
        Text = text;
        IsKey = key;
        Wide = wide;
    }

    /// <summary>A plain string or a text key, shown in the language of the mod that owns the line.</summary>
    public UIText Text { get; }

    /// <summary>Drawn in a box.</summary>
    public bool IsKey { get; }

    /// <summary>Leaves a wider gap on the part's left (to set a key apart from the words before it).</summary>
    public bool Wide { get; }

    /// <summary>Words in the HUD's text colour.</summary>
    public static HintPart Word(UIText text) => new(text ?? UIText.Empty, false, false);

    /// <summary>A key name in a box, in the HUD's emphasised colour; <paramref name="wide"/> leaves a wider gap on its left.</summary>
    public static HintPart Key(string name, bool wide = false) => new(name ?? "", true, wide);
}

/// <summary>
/// A line of mixed words and key boxes ("Release [F] to cancel") for use inside a HUD element's build: right-aligned at the
/// position you give, the pieces copied from the game's HUD like everything else the element draws. The pieces are created as the
/// parts need them and written only when something changed. Make it from your element's build callback (it is void after the game
/// rebuilds its HUD) or use <see cref="UIKitMod.AddHintLine"/> for a ready-made element.
/// </summary>
public sealed class HintRow
{
    private const float WordPx = 14f, KeyPx = 14f, KeyPadX = 6f, KeyPadY = 3f, KeyHeight = 18f, Gap = 6f, WideGap = 14f;

    private sealed class Piece
    {
        internal TextMeshPro Text = null!;
        internal SpriteRenderer[] Frame = null!;
    }

    private readonly HudElement _hud;
    private readonly List<Piece> _pieces = new();
    private HintPart[] _parts = Array.Empty<HintPart>();

    public HintRow(HudElement hud) => _hud = hud ?? throw new ArgumentNullException(nameof(hud));

    /// <summary>True when there is at least one part to draw.</summary>
    public bool HasParts => _parts.Length > 0;

    /// <summary>Sets what the line says; empty parts are skipped. Takes effect at the next <see cref="Place"/>.</summary>
    public void Set(params HintPart[] parts) => _parts = parts == null ? Array.Empty<HintPart>() : (HintPart[])parts.Clone();

    /// <summary>Draws the line with its right end at <paramref name="right"/> and its vertical middle at <paramref name="centre"/>
    /// (element units). Returns its width (0 and nothing drawn when it has no parts).</summary>
    public float Place(float right, float centre)
    {
        var x = right;
        // The width is summed from the parts, not read off the end position: "right - x" carries a rounding error that depends on where the
        // line stands, and a caller that sizes its plate by the width and places the line by the plate saw it alternate between two values.
        var used = 0f;
        var drawn = 0;
        for (var i = _parts.Length - 1; i >= 0; i--)
        {
            var part = _parts[i];
            var text = _hud.Show(part.Text);
            if (text.Length == 0) continue;
            var piece = PieceAt(drawn++);
            float width;
            if (part.IsKey)
            {
                width = HudDraw.Line(_hud, piece.Text, text, KeyPx, x - KeyPadX, centre, UIKitColors.Bright, false) + KeyPadX * 2f;
                // The box is as tall as the laid-out glyphs and centred on them, so capitals and accents stay inside it.
                var bounds = piece.Text.textBounds; var scale = piece.Text.transform.localScale.y;
                var height = Math.Max(KeyHeight, bounds.size.y * scale + KeyPadY * 2f);
                HudDraw.Frame(piece.Frame, x, centre + bounds.center.y * scale, width, height, UIKitColors.Line);
            }
            else
            {
                foreach (var f in piece.Frame) HudDraw.SetActive(f, false);
                width = HudDraw.Line(_hud, piece.Text, text, WordPx, x, centre, UIKitColors.Text, false);
            }
            var step = width + (part.Wide ? WideGap : Gap);
            x -= step;
            used += step;
        }
        for (var i = drawn; i < _pieces.Count; i++) Hide(_pieces[i]);
        return drawn == 0 ? 0f : used - LastGap();
    }

    /// <summary>Hides every piece.</summary>
    public void Hide()
    {
        foreach (var piece in _pieces) Hide(piece);
    }

    /// <summary>The gap that follows the leftmost drawn part, which is not part of the line's width.</summary>
    private float LastGap()
    {
        for (var i = 0; i < _parts.Length; i++)
            if (_hud.Show(_parts[i].Text).Length > 0) return _parts[i].Wide ? WideGap : Gap;
        return 0f;
    }

    private Piece PieceAt(int index)
    {
        while (_pieces.Count <= index)
        {
            var piece = new Piece { Text = _hud.AddText(), Frame = new SpriteRenderer[4] };
            piece.Text.gameObject.name = "HintText";
            for (var f = 0; f < 4; f++)
            {
                piece.Frame[f] = _hud.AddSprite(UIKitSprites.Pixel, 0);
                piece.Frame[f].gameObject.name = "HintKeyFrame";
            }
            _pieces.Add(piece);
        }
        return _pieces[index];
    }

    private static void Hide(Piece piece)
    {
        HudDraw.SetActive(piece.Text, false);
        foreach (var f in piece.Frame) HudDraw.SetActive(f, false);
    }
}

/// <summary>
/// A hint line as its own HUD element: <see cref="Set"/> the parts, change them whenever the prompt changes. Make it with
/// <see cref="UIKitMod.AddHintLine"/>.
/// </summary>
public sealed class HudHintLine : HudWidget
{
    private const float Height = 22f;
    private HintRow? _row;
    private HintPart[] _parts = Array.Empty<HintPart>();
    private bool _dirty = true;
    private int _screenVersion, _language = -1;

    internal HudHintLine() { }

    /// <summary>Sets the words and key boxes shown, left to right.</summary>
    public void Set(params HintPart[] parts)
    {
        _parts = parts == null ? Array.Empty<HintPart>() : (HintPart[])parts.Clone();
        _dirty = true;
    }

    internal override void Build(HudElement hud)
    {
        _row = new HintRow(hud);
        _dirty = true;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_row == null) return;
        if (!_dirty && hud.ScreenVersion == _screenVersion && _language == UITexts.Version) return;
        _dirty = false;
        _language = UITexts.Version;
        _screenVersion = hud.ScreenVersion;
        _row.Set(_parts);
        if (!_row.HasParts) { _row.Hide(); return; }
        var (fx, fy) = HudFlow.Of(hud);
        var centre = ScreenMath.Span(fy, Height) + Height / 2f;
        var width = _row.Place(0f, centre);
        var right = fx < 0 ? 0f : fx > 0 ? width : width / 2f;
        if (right != 0f) _row.Place(right, centre);
    }
}
