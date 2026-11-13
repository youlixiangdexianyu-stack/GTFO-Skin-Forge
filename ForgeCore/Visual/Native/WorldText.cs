using TMPro;
using UnityEngine;

namespace ForgeVisual;

/// <summary>How a <see cref="WorldText"/> looks: size, colour, alignment inside its box and the box's pivot. The default is the
/// zone-label look (size 3, white, bottom-aligned, growing upward from its lower edge). <see cref="SceneDepth"/> makes the
/// glyphs hide behind scene geometry (the game's font material otherwise draws over it).</summary>
public sealed record WorldTextStyle
{
    public static readonly WorldTextStyle Default = new();
    public float FontSize { get; init; } = 3;
    public Color Color { get; init; } = Color.white;
    public TextAlignmentOptions Alignment { get; init; } = TextAlignmentOptions.Bottom;
    public Vector2 Pivot { get; init; } = new(0.5f, 0);
    public float LineSpacing { get; init; }
    public bool SceneDepth { get; init; }
}

/// <summary>A line or block of text standing in the world. By default its lower edge sits at the local position (a long text
/// grows upward, never into the floor), and it wraps at a fixed width so shrinking the rect cannot change the wrap. The
/// object is built once and hidden until <see cref="SetShown"/>; <see cref="Destroy"/> removes it with its material.</summary>
public sealed class WorldText
{
    private static readonly int ZTestMode = Shader.PropertyToID("_ZTestMode");
    private static readonly int GuiZTestMode = Shader.PropertyToID("unity_GUIZTestMode");
    private readonly GameObject _object;
    private readonly Transform _transform;
    private readonly TextMeshPro _text;
    private Material? _material;
    private Transform? _camera;
    private Quaternion _rotation;
    private bool _rotationKnown, _shown;
    private Vector3 _position;
    private bool _positionKnown;

    private WorldText(GameObject target, TextMeshPro text)
    {
        _object = target; _text = text;
        // TextMeshPro requires a RectTransform, so adding it destroys the GameObject's plain Transform and puts a
        // RectTransform in its place: the transform is read only after that replacement, otherwise every later
        // write goes to the destroyed one and throws a NullReferenceException.
        _transform = target.transform;
    }

    /// <summary>Builds a hidden text object under <paramref name="parent"/> (no markup, no auto size, bottom-aligned, size 3).</summary>
    public static WorldText Create(Transform parent, string name) => Create(parent, name, WorldTextStyle.Default);

    /// <summary>Builds a hidden text object in <paramref name="style"/>. A null <paramref name="parent"/> leaves it at the scene
    /// root, to be placed through <see cref="Root"/>'s transform.</summary>
    public static WorldText Create(Transform? parent, string name, WorldTextStyle style)
    {
        var target = new GameObject(name);
        if (parent != null) target.transform.SetParent(parent, false);
        var text = target.AddComponent<TextMeshPro>();
        var result = new WorldText(target, text);
        try
        {
            text.richText = false; text.enableAutoSizing = false; text.enableWordWrapping = true;
            text.alignment = style.Alignment; text.fontSize = style.FontSize; text.color = style.Color;
            text.lineSpacing = style.LineSpacing; text.rectTransform.pivot = style.Pivot;
            if (style.SceneDepth)
            {
                result._material = text.fontMaterial;
                result._material.SetFloat(ZTestMode, 4); // LEqual: keep the game's font, use scene depth.
                result._material.SetFloat(GuiZTestMode, 4);
            }
        }
        catch { result.Destroy(); throw; }
        target.SetActive(false);
        return result;
    }

    /// <summary>The object that carries the text component.</summary>
    public GameObject Root => _object;
    /// <summary>The underlying text component (read it back for evidence).</summary>
    public TextMeshPro Text => _text;
    public bool Alive => _object != null && !_object.WasCollected && _text != null && !_text.WasCollected && _transform != null && !_transform.WasCollected;
    public bool Shown => _shown && Alive && _object.activeSelf;

    public void SetShown(bool shown)
    {
        if (_shown == shown || !Alive) return;
        _object.SetActive(shown); _shown = shown;
    }

    /// <summary>Shows <paramref name="content"/> wrapped at <paramref name="lineWidth"/>; the box is as tall as the text, or
    /// <paramref name="height"/> when given. Returns the text's preferred size at that width.</summary>
    public Vector2 SetContent(string content, float lineWidth, float? height = null)
    {
        _text.SetText(content);
        var preferred = _text.GetPreferredValues(content, lineWidth, float.PositiveInfinity);
        _text.rectTransform.sizeDelta = new Vector2(lineWidth, height ?? preferred.y);
        return preferred;
    }

    public void SetLocalPosition(Vector3 position)
    {
        if (_positionKnown && _position.x == position.x && _position.y == position.y && _position.z == position.z) return;
        _transform.localPosition = position; _position = position; _positionKnown = true;
    }

    /// <summary>Turns the text to the camera's rotation, writing only when the camera or its rotation changed. A null camera forgets the last rotation.</summary>
    public void FaceCamera(Transform? camera, Quaternion cameraRotation)
    {
        if (camera == null || camera.WasCollected) { _camera = null; _rotationKnown = false; return; }
        if (!_rotationKnown || _camera == null || _camera.WasCollected || _camera.Pointer != camera.Pointer
            || _rotation.x != cameraRotation.x || _rotation.y != cameraRotation.y || _rotation.z != cameraRotation.z || _rotation.w != cameraRotation.w)
        {
            _transform.rotation = cameraRotation; _rotation = cameraRotation; _rotationKnown = true;
        }
        _camera = camera;
    }

    /// <summary>Destroys the object and the font material copy it made.</summary>
    public void Destroy()
    {
        if (_object != null && !_object.WasCollected) UnityEngine.Object.Destroy(_object);
        if (_material != null && !_material.WasCollected) UnityEngine.Object.Destroy(_material);
        _material = null; _shown = false;
    }
}
