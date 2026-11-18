using UnityEngine;

namespace ForgeVisual;

/// <summary>A flat coloured rectangle standing behind text or an icon, drawn with the built-in unlit transparent sprite
/// shader (no collider, no per-tick mesh: every backdrop shares one unit quad). It is a child of its parent, so the parent's pose
/// and scale carry it; <see cref="Place"/> sizes and moves it inside the parent.</summary>
public sealed class WorldBackdrop
{
    /// <summary>Render queues of a board's layers. Unity sorts transparent renderers of one queue by their distance to the
    /// camera, each renderer on its own (a text's fallback-font sub-meshes included), so a layer standing a few centimetres
    /// in front of the backdrop would come and go with the viewing angle. Each layer has its own queue instead: the backdrop
    /// first, then a picture or icon on it, then scene-depth text (<see cref="WorldTextStyle.SceneDepth"/>) on top.</summary>
    public const int Queue = 3000, PictureQueue = Queue + 1, TextQueue = Queue + 2;

    private readonly GameObject _object;
    private readonly Material _material;

    private WorldBackdrop(GameObject target, Material material) { _object = target; _material = material; }

    /// <summary>Builds the backdrop under <paramref name="parent"/>; null when the sprite shader is not in the game.</summary>
    public static WorldBackdrop? TryCreate(Transform parent, string name, Color color)
        => SpriteQuad(parent, name, color, Queue) is { } quad ? new WorldBackdrop(quad.Target, quad.Material) : null;

    /// <summary>The unit quad every flat board is drawn as: a child of <paramref name="parent"/> drawn with the built-in
    /// unlit transparent sprite shader in <paramref name="queue"/>, casting no shadow. Null when that shader is not in the
    /// game; a failure on the way leaves neither object behind.</summary>
    internal static (GameObject Target, Material Material)? SpriteQuad(Transform parent, string name, Color color, int queue,
        Texture? texture = null, int? layer = null)
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        var material = new Material(shader) { color = color, renderQueue = queue };
        var target = new GameObject(name);
        try
        {
            if (texture != null) material.mainTexture = texture;
            if (layer is { } value) target.layer = value;
            target.transform.SetParent(parent, false);
            target.AddComponent<MeshFilter>().sharedMesh = Shaders.Quad();
            var renderer = target.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        catch { UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(material); throw; }
        return (target, material);
    }

    public GameObject Root => _object;

    /// <summary>The colour the rectangle shows now; the alpha is its opacity.</summary>
    public Color Color => _material.color;

    /// <summary>Recolours the rectangle in place; the alpha is its opacity.</summary>
    public void SetColor(Color color) => _material.color = color;

    /// <summary>Sizes the rectangle and puts its centre at <paramref name="center"/>; a positive <paramref name="depth"/> is behind the text plane.</summary>
    public void Place(Vector2 size, Vector2 center, float depth = 0.01f)
    {
        _object.transform.localScale = new Vector3(size.x, size.y, 1);
        _object.transform.localPosition = new Vector3(center.x, center.y, depth);
    }

    public void Destroy()
    {
        if (_object != null && !_object.WasCollected) UnityEngine.Object.Destroy(_object);
        if (_material != null && !_material.WasCollected) UnityEngine.Object.Destroy(_material);
    }
}

/// <summary>A flat image board: a texture on a quad of the given size, drawn with the built-in unlit transparent sprite shader.
/// It is a child of its parent on the parent's layer, so the parent's collider is the board's hit box. It draws in
/// <see cref="WorldBackdrop.PictureQueue"/>: over a backdrop behind it and under text on it, from every angle.</summary>
public sealed class ImageBoard
{
    private readonly GameObject _object;
    private readonly Material _material;

    private ImageBoard(GameObject target, Material material) { _object = target; _material = material; }

    /// <summary>Builds the board under <paramref name="parent"/> on <paramref name="layer"/>; null when the sprite shader is not in the game.</summary>
    public static ImageBoard? TryCreate(Transform parent, string name, int layer, Texture2D texture, float width, float height)
    {
        if (WorldBackdrop.SpriteQuad(parent, name, Color.white, WorldBackdrop.PictureQueue, texture, layer) is not { } quad) return null;
        quad.Target.transform.localScale = new Vector3(width, height, 1);
        return new ImageBoard(quad.Target, quad.Material);
    }

    public GameObject Root => _object;

    public void Destroy()
    {
        if (_object != null && !_object.WasCollected) UnityEngine.Object.Destroy(_object);
        if (_material != null && !_material.WasCollected) UnityEngine.Object.Destroy(_material);
    }
}

/// <summary>A sign: a face of a fixed width and height (its hit surface and wrap width) with centred text and an optional backdrop
/// that hugs the shown text plus <c>padding</c> metres on every side. The face is the parent; text and backdrop are its children.</summary>
public sealed class SignBoard
{
    private readonly Transform _face;
    private readonly float _width, _height, _padding;
    private WorldText? _text;

    private SignBoard(Transform face, float width, float height, float padding, WorldBackdrop? backdrop)
    { _face = face; _width = width; _height = height; _padding = padding; Backdrop = backdrop; }

    /// <summary>Builds the sign on <paramref name="face"/>; <paramref name="backdrop"/> null draws no backdrop. Null when the backdrop's shader is not in the game.</summary>
    public static SignBoard? TryCreate(Transform face, string backdropName, float width, float height, float padding, Color? backdrop)
    {
        WorldBackdrop? back = null;
        if (backdrop is { } color)
        {
            back = WorldBackdrop.TryCreate(face, backdropName, color);
            if (back == null) return null;
            back.Place(new Vector2(padding * 2, padding * 2), default);
        }
        return new SignBoard(face, width, height, padding, back);
    }

    public WorldBackdrop? Backdrop { get; }
    /// <summary>The sign's text object, once <see cref="ShowText"/> has made it.</summary>
    public WorldText? Text => _text;

    /// <summary>Shows <paramref name="content"/> centred on the face, just in front of the backdrop, wrapped at <paramref name="lineWidth"/> (the face's width by default). The text is made on first use in <paramref name="style"/> (centre-aligned, centre pivot).</summary>
    public WorldText ShowText(string textName, WorldTextStyle style, string content, float? lineWidth = null)
    {
        if (_text == null)
        {
            _text = WorldText.Create(_face, textName, style with { Alignment = TMPro.TextAlignmentOptions.Center, Pivot = new Vector2(0.5f, 0.5f) });
            _text.SetLocalPosition(new Vector3(0, 0, -0.02f));
        }
        _text.SetShown(true);
        var wrap = lineWidth ?? _width;
        var preferred = _text.SetContent(content, wrap, _height);
        Backdrop?.Place(new Vector2(Mathf.Min(wrap, preferred.x) + _padding * 2, preferred.y + _padding * 2), default);
        return _text;
    }

    /// <summary>Removes the text object; the next <see cref="ShowText"/> makes a new one.</summary>
    public void DestroyText() { _text?.Destroy(); _text = null; }

    public void Destroy()
    {
        DestroyText();
        Backdrop?.Destroy();
    }
}
