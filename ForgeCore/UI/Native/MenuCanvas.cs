using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>The menu skins' colours (lobby buttons and cells, the picker): opaque dark plates, so the game's own drawing under them
/// never shows through, white lines at a few strengths, a dim grey for secondary text and amber for a held slot.</summary>
internal static class MenuLook
{
    internal static readonly Color Ink = new(0.047f, 0.063f, 0.075f, 1f);
    internal static readonly Color Panel = new(0.067f, 0.086f, 0.102f, 1f);
    internal static readonly Color Raised = new(0.118f, 0.145f, 0.165f, 1f);
    internal static readonly Color Bay = new(0.09f, 0.11f, 0.125f, 1f);
    internal static readonly Color Line = new(0.92f, 0.94f, 0.96f, 1f);
    internal static readonly Color LineMid = new(0.92f, 0.94f, 0.96f, 0.5f);
    internal static readonly Color LineDim = new(0.92f, 0.94f, 0.96f, 0.22f);
    internal static readonly Color LineFaint = new(0.92f, 0.94f, 0.96f, 0.12f);
    internal static readonly Color Text = new(0.84f, 0.87f, 0.89f, 1f);
    internal static readonly Color Body = new(0.73f, 0.76f, 0.78f, 1f);
    internal static readonly Color Dim = new(0.561f, 0.608f, 0.631f, 1f);
    internal static readonly Color Amber = new(0.949f, 0.639f, 0.227f, 1f);

    private static readonly string[] Numbers = BuildNumbers();

    /// <summary>"01" to "12" (a slot number), made once.</summary>
    internal static string Number(int n) => n >= 1 && n <= Numbers.Length ? Numbers[n - 1] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string[] BuildNumbers()
    {
        var numbers = new string[12];
        for (var i = 0; i < numbers.Length; i++) numbers[i] = (i + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return numbers;
    }

    /// <summary>RRGGBB of a colour, for a TextMeshPro colour tag.</summary>
    internal static string Hex(Color c)
    {
        static int Channel(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return (Channel(c.r) << 16 | Channel(c.g) << 8 | Channel(c.b)).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static Color Faded(Color c, float alpha) => new(c.r, c.g, c.b, c.a * alpha);
}

/// <summary>
/// Drawing on one of the game's menu objects (a lobby bar button, a picker card, the picker's tabs and details box) in that object's own
/// units: call <see cref="Begin"/>, then <see cref="Plate"/>, <see cref="Frame"/>, <see cref="Rect"/>, <see cref="Picture"/>,
/// <see cref="Icon"/>, <see cref="Text"/> and <see cref="Paragraph"/> for everything shown, then <see cref="End"/>. Later calls draw over
/// earlier ones. Pieces are kept in a pool under one root object and written only when something about them changed, and pieces not asked
/// for are hidden, so drawing the same picture again allocates and touches nothing. Sprites take the material, layer and sorting layer of
/// the object's own sprite; texts are copies of the object's own text (its ghost layers and scripts dropped), so they draw with the game's
/// font and through the same clipping as the object.
/// </summary>
internal sealed class MenuCanvas
{
    private struct SpriteLast
    {
        internal Sprite? Sprite;
        internal float X, Y, W, H, Cut, Degrees;
        internal Color Colour;
        internal int Order;
        internal bool Sliced;
    }

    private struct TextLast
    {
        internal string? Text;
        internal float X, Y, Size, Width, Height, Shown, Fit;
        internal Color Colour;
        internal int Order;
        internal HudAlign Align;
        internal bool Wrap, Rich;
    }

    private readonly GameObject _root;
    private readonly Material? _material;
    private readonly int _layer, _sortingLayer, _order;
    private readonly TextMeshPro? _textSource;
    private readonly Vector3 _textScale;
    private readonly List<SpriteRenderer> _sprites = new();
    private readonly List<SpriteLast> _spriteLast = new();
    private readonly List<TextMeshPro> _texts = new();
    private readonly List<int> _textMeshes = new();
    private readonly List<TextLast> _textLast = new();
    private int _usedSprites, _usedTexts, _next;

    /// <param name="parent">The object drawn on; the canvas root is its child at its origin.</param>
    /// <param name="name">The root object's name.</param>
    /// <param name="spriteLike">The object's own sprite: material, layer and sorting layer of every piece.</param>
    /// <param name="textLike">The object's own text, copied for every text piece; null when the canvas draws no text.</param>
    /// <param name="order">Sorting order of the first piece drawn; each later one is one higher.</param>
    internal MenuCanvas(Transform parent, string name, SpriteRenderer spriteLike, TextMeshPro? textLike, int order)
    {
        _layer = spriteLike.gameObject.layer;
        _material = spriteLike.sharedMaterial;
        _sortingLayer = spriteLike.sortingLayerID;
        _order = order;
        _root = new GameObject(name) { layer = _layer };
        _root.transform.SetParent(parent, false);
        _root.transform.localPosition = Vector3.zero;
        _root.transform.localScale = Vector3.one;
        _root.transform.localRotation = Quaternion.identity;
        _textSource = textLike;
        if (textLike != null)
        {
            var own = textLike.transform.lossyScale;
            var here = _root.transform.lossyScale;
            _textScale = new Vector3(own.x / Math.Max(1e-5f, here.x), own.y / Math.Max(1e-5f, here.y), 1f);
        }
    }

    internal GameObject Root => _root;

    /// <summary>
    /// Stops the game's own drawing of the object this canvas sits on: every renderer under it that is not one of this canvas's pieces is
    /// switched off with <c>forceRenderingOff</c>, a flag apart from <c>enabled</c>. The game turns <c>enabled</c> on and off by itself
    /// (hover, intro, show and hide, a page coming back), so a renderer switched off with <c>enabled</c> shows again the next time it does;
    /// <c>forceRenderingOff</c> is never touched by the game, and the object's order or layer no longer decides what is on top. Call it
    /// again after the game may have added renderers.
    /// </summary>
    internal void VeilOriginals()
    {
        if (!Alive) return;
        var parent = _root.transform.parent;
        if (parent == null) return;
        foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.WasCollected || renderer.transform.IsChildOf(_root.transform)) continue;
            if (!renderer.forceRenderingOff) renderer.forceRenderingOff = true;
        }
    }

    /// <summary>
    /// Empties every text of the object this canvas sits on that is not one of this canvas's pieces and turns it off, so the game's own
    /// label makes no content at all: no glyph mesh, no fallback-font sub-mesh (Chinese, among others), no offset ghost copy a text
    /// script derives from the label. <see cref="VeilOriginals"/> only hides renderers that exist when it runs; these appear later. For
    /// an object the game fills with its own words (a tab header) call it after each time it does.
    /// </summary>
    internal void SilenceOriginalTexts()
    {
        if (!Alive) return;
        var parent = _root.transform.parent;
        if (parent == null) return;
        foreach (var text in parent.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (text == null || text.WasCollected || text.transform.IsChildOf(_root.transform)) continue;
            NativeUi.SetText(text, "");
            if (text.enabled) text.enabled = false;
        }
    }

    /// <summary>A collider's rectangle in <paramref name="space"/>'s local units (the collider can sit on another object than the one
    /// drawn on); without a collider, <paramref name="fallback"/>.</summary>
    internal static Rect BoxIn(Transform space, BoxCollider2D? collider, Rect fallback)
    {
        if (collider == null || collider.WasCollected) return fallback;
        var t = collider.transform;
        var o = collider.offset;
        var s = collider.size;
        var a = space.InverseTransformPoint(t.TransformPoint(new Vector3(o.x - s.x / 2f, o.y - s.y / 2f, 0f)));
        var b = space.InverseTransformPoint(t.TransformPoint(new Vector3(o.x + s.x / 2f, o.y + s.y / 2f, 0f)));
        return UnityEngine.Rect.MinMaxRect(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
    }

    /// <summary><paramref name="box"/> shrunk by <paramref name="by"/> on every side.</summary>
    internal static Rect Inset(Rect box, float by) => UnityEngine.Rect.MinMaxRect(box.XMin() + by, box.YMin() + by, Math.Max(box.XMin() + by, box.XMax() - by), Math.Max(box.YMin() + by, box.YMax() - by));

    internal bool Alive => _root != null && !_root.WasCollected;

    /// <summary>True when pieces were made since the last call (a clipped list must hand new pieces to its clipping).</summary>
    internal bool TakeGrown()
    {
        var grown = _grown;
        _grown = false;
        return grown;
    }

    private bool _grown;

    /// <summary>The highest sorting order among the renderers under <paramref name="item"/>: a canvas drawn over the item starts above it.</summary>
    internal static int TopOrder(Component item)
    {
        var top = 0;
        foreach (var sprite in item.GetComponentsInChildren<SpriteRenderer>(true)) top = Math.Max(top, sprite.sortingOrder);
        foreach (var mesh in item.GetComponentsInChildren<MeshRenderer>(true)) top = Math.Max(top, mesh.sortingOrder);
        return top;
    }

    internal void Begin()
    {
        _usedSprites = _usedTexts = 0;
        _next = 0;
    }

    /// <summary>A filled plate with its top left and bottom right corners cut at 45° by <paramref name="cut"/> (0: a plain rectangle).</summary>
    internal void Plate(Rect box, float cut, Color colour)
    {
        if (cut <= 0f) Put(UIKitSprites.Pixel, box.CenterX(), box.CenterY(), box.Width(), box.Height(), 0f, 0f, colour);
        else Put(UIKitSprites.Chamfer, box.CenterX(), box.CenterY(), box.Width(), box.Height(), Math.Min(cut, Math.Min(box.Width(), box.Height()) / 2f), 0f, colour);
    }

    /// <summary>The outline of a plate (see <see cref="Plate"/>); its line is an eighth of <paramref name="cut"/> thick.</summary>
    internal void Frame(Rect box, float cut, Color colour) =>
        Put(UIKitSprites.ChamferLine, box.CenterX(), box.CenterY(), box.Width(), box.Height(), Math.Max(1f, Math.Min(cut, Math.Min(box.Width(), box.Height()) / 2f)), 0f, colour);

    /// <summary>A rectangle, turned <paramref name="degrees"/> anticlockwise about its centre.</summary>
    internal void Rect(Rect box, Color colour, float degrees = 0f) => Put(UIKitSprites.Pixel, box.CenterX(), box.CenterY(), box.Width(), box.Height(), 0f, degrees, colour);

    /// <summary>A sprite stretched over <paramref name="box"/>.</summary>
    internal void Picture(Sprite sprite, Rect box, Color colour)
    {
        var size = sprite.bounds.size;
        if (size.x <= 0f || size.y <= 0f) return;
        Put(sprite, box.CenterX(), box.CenterY(), box.Width() / size.x, box.Height() / size.y, 0f, 0f, colour);
    }

    /// <summary>A picture at the largest size that fits <paramref name="box"/> with its ratio kept, centred in it.</summary>
    internal void Icon(Sprite sprite, Rect box, Color colour)
    {
        var size = sprite.bounds.size;
        var (w, h) = IconFit.Fit(box.Width(), box.Height(), size.x, size.y);
        if (w <= 0f || h <= 0f) return;
        Put(sprite, box.CenterX(), box.CenterY(), w / size.x, h / size.y, 0f, 0f, colour);
    }

    /// <summary>One line of text <paramref name="size"/> tall (the font size, in the canvas's units) on (<paramref name="x"/>,
    /// <paramref name="y"/>): the left end, middle or right end of the line, centred on y. With <paramref name="fit"/> above 0 a longer
    /// line is drawn smaller (down to 0.6 of the size) and then cut with an ellipsis. Shown as written unless <paramref name="rich"/>.
    /// Returns the width drawn.</summary>
    internal float Text(string text, float x, float y, float size, Color colour, HudAlign align = HudAlign.Left, float fit = 0f, bool rich = false)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var label = TakeText(out var index);
        if (label == null) return 0f;
        var last = _textLast[index];
        var order = _order + _next++;
        if (last.Text == text && !last.Wrap && last.X == x && last.Y == y && last.Size == size && last.Colour == colour && last.Align == align && last.Fit == fit
            && last.Rich == rich && last.Order == order) return last.Shown;
        var scale = _textScale;
        label.richText = rich;
        label.enableWordWrapping = false;
        label.enableAutoSizing = false;
        label.fontSize = size / Math.Max(1e-5f, scale.y);
        NativeUi.SetText(label, text);
        var width = label.GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity).x * scale.x;
        var shown = width;
        label.overflowMode = TextOverflowModes.Overflow;
        var box = Math.Max(width, 1f) + size;
        if (fit > 0f && width > fit)
        {
            var shrink = Math.Max(0.6f, fit / width);
            label.fontSize = size * shrink / Math.Max(1e-5f, scale.y);
            shown = Math.Min(fit, width * shrink);
            box = fit;
            if (width * shrink > fit) label.overflowMode = TextOverflowModes.Ellipsis;
        }
        var t = label.rectTransform;
        t.pivot = new Vector2(align == HudAlign.Left ? 0f : align == HudAlign.Centre ? 0.5f : 1f, 0.5f);
        label.alignment = align == HudAlign.Left ? TextAlignmentOptions.MidlineLeft : align == HudAlign.Centre ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineRight;
        t.sizeDelta = new Vector2(box / Math.Max(1e-5f, scale.x), size * 1.6f / Math.Max(1e-5f, scale.y));
        label.transform.localPosition = new Vector3(x, y, 0f);
        label.color = colour;
        Settle(index, label, order);
        _textLast[index] = new TextLast { Text = text, X = x, Y = y, Size = size, Colour = colour, Align = align, Fit = fit, Rich = rich, Order = order, Shown = shown };
        return shown;
    }

    /// <summary>A number with its unit (<c>600</c> then <c>s</c>) as one line: the unit is set smaller inside the same string, so both
    /// sit on the line's one baseline whatever the font's metrics are, with a small gap between them. Returns the width drawn.</summary>
    internal float Quantity(string value, string unit, float x, float y, float size, Color valueColour, Color unitColour, float fit = 0f)
    {
        var text = unit.Length == 0 ? value : RichText.Plain(value) + "<size=45%><space=0.25em><color=#" + MenuLook.Hex(unitColour) + ">" + RichText.Plain(unit) + "</color></size>";
        return Text(text, x, y, size, valueColour, HudAlign.Left, fit, unit.Length > 0);
    }

    /// <summary>The width <paramref name="text"/> takes at <paramref name="size"/>, drawing nothing.</summary>
    internal float TextWidth(string text, float size)
    {
        if (string.IsNullOrEmpty(text) || _textSource == null) return 0f;
        if (_widths.TryGetValue((text, size), out var known)) return known;
        var label = _texts.Count > 0 ? _texts[0] : null;
        if (label == null)
        {
            label = TakeText(out _);
            _usedTexts--;
            if (label == null) return 0f;
            _textLast[0] = default;
        }
        var before = label.fontSize;
        label.fontSize = size / Math.Max(1e-5f, _textScale.y);
        var width = label.GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity).x * _textScale.x;
        label.fontSize = before;
        if (_widths.Count >= MaxWidths) _widths.Clear();
        return _widths[(text, size)] = width;
    }

    private const int MaxWidths = 256;
    private readonly Dictionary<(string Text, float Size), float> _widths = new();

    /// <summary>Wrapped text from the top left of <paramref name="box"/>, drawn smaller (down to 0.6 of <paramref name="size"/>) when it
    /// does not fit and then cut with an ellipsis.</summary>
    internal void Paragraph(string text, Rect box, float size, Color colour, bool rich = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var label = TakeText(out var index);
        if (label == null) return;
        var last = _textLast[index];
        var order = _order + _next++;
        if (last.Text == text && last.Wrap && last.X == box.XMin() && last.Y == box.YMax() && last.Width == box.Width() && last.Height == box.Height() && last.Size == size
            && last.Colour == colour && last.Rich == rich && last.Order == order) return;
        var scale = _textScale;
        label.richText = rich;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.fontSizeMax = size / Math.Max(1e-5f, scale.y);
        label.fontSizeMin = label.fontSizeMax * 0.6f;
        label.enableAutoSizing = true;
        var t = label.rectTransform;
        t.pivot = new Vector2(0f, 1f);
        t.sizeDelta = new Vector2(box.Width() / Math.Max(1e-5f, scale.x), box.Height() / Math.Max(1e-5f, scale.y));
        label.alignment = TextAlignmentOptions.TopLeft;
        NativeUi.SetText(label, text);
        label.transform.localPosition = new Vector3(box.XMin(), box.YMax(), 0f);
        label.color = colour;
        Settle(index, label, order);
        _textLast[index] = new TextLast { Text = text, X = box.XMin(), Y = box.YMax(), Width = box.Width(), Height = box.Height(), Size = size, Colour = colour, Rich = rich, Order = order, Wrap = true };
    }

    /// <summary>Ends the picture: every piece that was not asked for is hidden.</summary>
    internal void End()
    {
        for (var i = _usedSprites; i < _sprites.Count; i++) Hide(_sprites[i]);
        for (var i = _usedTexts; i < _texts.Count; i++) Hide(_texts[i]);
    }

    /// <summary>Hides everything.</summary>
    internal void Clear()
    {
        Begin();
        End();
    }

    internal void Destroy()
    {
        if (!Alive) return;
        _root.SetActive(false);
        Object.Destroy(_root);
    }

    private static void Hide(Component piece)
    {
        if (piece != null && !piece.WasCollected && piece.gameObject.activeSelf) piece.gameObject.SetActive(false);
    }

    private void Put(Sprite sprite, float x, float y, float w, float h, float cut, float degrees, Color colour)
    {
        if (!Alive) return;
        var index = _usedSprites++;
        if (index == _sprites.Count)
        {
            var go = new GameObject("Piece") { layer = _layer };
            go.transform.SetParent(_root.transform, false);
            var made = go.AddComponent<SpriteRenderer>();
            if (_material != null) made.sharedMaterial = _material;
            made.sortingLayerID = _sortingLayer;
            _sprites.Add(made);
            _spriteLast.Add(default);
            _grown = true;
        }
        var piece = _sprites[index];
        if (!piece.gameObject.activeSelf) piece.gameObject.SetActive(true);
        var order = _order + _next++;
        var sliced = cut > 0f;
        var last = _spriteLast[index];
        if (ReferenceEquals(last.Sprite, sprite) && last.X == x && last.Y == y && last.W == w && last.H == h && last.Cut == cut && last.Degrees == degrees
            && last.Colour == colour && last.Order == order && last.Sliced == sliced) return;
        if (!ReferenceEquals(piece.sprite, sprite)) piece.sprite = sprite;
        var t = piece.transform;
        t.localPosition = new Vector3(x, y, 0f);
        t.localRotation = degrees == 0f ? Quaternion.identity : Quaternion.Euler(0f, 0f, degrees);
        if (sliced)
        {
            piece.drawMode = SpriteDrawMode.Sliced;
            t.localScale = new Vector3(cut, cut, 1f);
            piece.size = new Vector2(w / cut, h / cut);
        }
        else
        {
            piece.drawMode = SpriteDrawMode.Simple;
            // w and h are the scale of a one-unit sprite (a rectangle) or of a picture (its own size already divided out).
            t.localScale = new Vector3(w, h, 1f);
        }
        piece.color = colour;
        piece.sortingOrder = order;
        _spriteLast[index] = new SpriteLast { Sprite = sprite, X = x, Y = y, W = w, H = h, Cut = cut, Degrees = degrees, Colour = colour, Order = order, Sliced = sliced };
    }

    private TextMeshPro? TakeText(out int index)
    {
        index = _usedTexts;
        if (_textSource == null || _textSource.WasCollected || !Alive)
        {
            index = -1;
            return null;
        }
        _usedTexts++;
        if (index == _texts.Count)
        {
            var go = Object.Instantiate(_textSource.gameObject, _root.transform);
            go.name = "Text";
            NativeUi.StripLocalization(go);
            // The source may be veiled (VeilOriginals), and a copy carries the flag over.
            foreach (var copied in go.GetComponentsInChildren<Renderer>(true))
                if (copied.forceRenderingOff) copied.forceRenderingOff = false;
            var made = go.GetComponent<TextMeshPro>() ?? throw new InvalidOperationException("the menu text copy has no TextMeshPro");
            Hud.StripExtraLayers(go, made);
            made.enableAutoSizing = false;
            made.enabled = true;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = _textScale;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = true;
            _texts.Add(made);
            _textMeshes.Add(0);
            _textLast.Add(default);
            _grown = true;
        }
        var label = _texts[index];
        if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
        return label;
    }

    /// <summary>
    /// Builds the text's glyphs now and gives it its order. Glyphs the game's font lacks (Chinese, among others) come from a fallback
    /// font and are drawn by sub-mesh objects the text makes under itself when it first builds them, normally just before it renders,
    /// after a clipped list has handed its pieces to its clipping, so those glyphs were drawn unclipped. Built here, a new sub-mesh
    /// counts as a new piece (<see cref="TakeGrown"/>), and the order is set through the text, which carries it to its sub-meshes.
    /// </summary>
    private void Settle(int index, TextMeshPro label, int order)
    {
        label.ForceMeshUpdate(true);
        label.sortingLayerID = _sortingLayer;
        label.sortingOrder = order;
        var meshes = label.transform.childCount;
        if (meshes <= _textMeshes[index]) return;
        _textMeshes[index] = meshes;
        _grown = true;
    }
}
