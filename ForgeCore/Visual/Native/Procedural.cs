using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// Parametric drawing: the library's fixed drawing programs (<see cref="Catalog"/>, <c>Data/fx-programs.json</c>) run by one shader,
/// <c>ForgeVisual/Procedural</c>, chosen by number and fed numbers and colours (<see cref="ProceduralLook"/>). A new program is a change of
/// this library and a rebuild of its shader bundle; nothing is compiled in the game. Two uses: <see cref="Painter"/> lays a mesh out by its
/// UV into a render texture each time it is asked (for a surface's texture), and <see cref="CreateMaterial"/> gives a material that draws
/// the program on geometry in the world or, with <c>firstPerson</c>, in the hands through the game's item camera.
/// </summary>
public static class Procedural
{
    private const string CatalogResource = "ForgeVisual.Data.fx-programs.json";
    private static ProceduralCatalog? _catalog;

    /// <summary>The program catalogue, read once from the copy embedded in this library.</summary>
    public static ProceduralCatalog Catalog => _catalog ??= ReadCatalog();

    private static ProceduralCatalog ReadCatalog()
    {
        using var stream = typeof(Procedural).Assembly.GetManifestResourceStream(CatalogResource)
            ?? throw new System.IO.InvalidDataException("ForgeVisual is built without Data/fx-programs.json.");
        using var reader = new System.IO.StreamReader(stream);
        return ProceduralCatalog.Parse(reader.ReadToEnd());
    }

    internal static readonly int ProgramId = Shader.PropertyToID("_Program"), P0 = Shader.PropertyToID("_FxP0"), P1 = Shader.PropertyToID("_FxP1"),
        Xf = Shader.PropertyToID("_FxXf"), TimeId = Shader.PropertyToID("_FxTime"), View = Shader.PropertyToID("_FxView"), Unwrap = Shader.PropertyToID("_FxUnwrap"),
        Src = Shader.PropertyToID("_Src"), Dst = Shader.PropertyToID("_Dst"), ZTest = Shader.PropertyToID("_ZTest"), Cull = Shader.PropertyToID("_Cull"),
        FpsWeight = Shader.PropertyToID("_FPSWeight");
    internal static readonly int[] ColourIds = { Shader.PropertyToID("_FxC0"), Shader.PropertyToID("_FxC1"), Shader.PropertyToID("_FxC2"), Shader.PropertyToID("_FxC3") };

    /// <summary>A new material that draws a program on geometry with <paramref name="blend"/>, both faces, behind nearer surfaces, writing no
    /// depth; with <paramref name="firstPerson"/> through the game's first-person item camera, as the guns in the hands are (put the geometry
    /// on the held item's layer). Null while the library's shaders are not loaded (logged once). The caller owns it; write its look with
    /// <see cref="ProceduralLook.Apply"/>.</summary>
    public static Material? CreateMaterial(FxBlend blend, bool firstPerson, string name)
    {
        var shader = Shaders.Procedural;
        if (shader == null) return null;
        var material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
        var add = blend == FxBlend.Additive;
        material.SetFloat(Src, (float)BlendMode.SrcAlpha);
        material.SetFloat(Dst, (float)(add ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        material.SetFloat(ZTest, (float)CompareFunction.LessEqual);
        material.SetFloat(Cull, (float)CullMode.Off);
        material.SetFloat(Unwrap, 0f);
        if (firstPerson)
        {
            material.SetFloat(FpsWeight, 1f);
            material.EnableKeyword(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING);
        }
        return material;
    }

    /// <summary>A painter that lays submesh <paramref name="submesh"/> of <paramref name="mesh"/> out by its first UV into
    /// <paramref name="target"/> (cleared to transparent first) with a look you set; call <see cref="ProceduralPainter.Paint"/> when it should
    /// change, typically once a frame. The mesh's rest shape gives the object-space position and normal (a skinned mesh's shared mesh is fine).
    /// With <paramref name="over"/> the target is not cleared: the program is blended over what the target holds (alpha or additive), only
    /// where the mesh's UV islands are, so several painters can lay their meshes over one copy of a surface's texture. The target's alpha
    /// is blended with the same factors. Null while the library's shaders are not loaded. The caller owns the target;
    /// <see cref="ProceduralPainter.Dispose"/> frees the rest.</summary>
    public static ProceduralPainter? Painter(Mesh mesh, int submesh, RenderTexture target, ProceduralLook look, string label, FxBlend? over = null)
    {
        if (mesh == null || target == null || look == null) throw new ArgumentNullException(mesh == null ? nameof(mesh) : target == null ? nameof(target) : nameof(look));
        if (submesh < 0 || submesh >= mesh.subMeshCount) throw new ArgumentOutOfRangeException(nameof(submesh), submesh, "the mesh has " + mesh.subMeshCount + " submeshes");
        var shader = Shaders.Procedural;
        if (shader == null) return null;
        var material = new Material(shader) { name = "ForgeVisual procedural " + label, hideFlags = HideFlags.HideAndDontSave };
        material.SetFloat(Src, (float)(over == null ? BlendMode.One : BlendMode.SrcAlpha));
        material.SetFloat(Dst, (float)(over == null ? BlendMode.Zero : over == FxBlend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        material.SetFloat(ZTest, (float)CompareFunction.Always);
        material.SetFloat(Cull, (float)CullMode.Off);
        material.SetFloat(Unwrap, 1f);
        return new ProceduralPainter(mesh, submesh, target, look, material, label, clear: over == null);
    }

    /// <summary>Writes only the time (seconds) to a material of <see cref="CreateMaterial"/>, for the frames its look did not change
    /// (<see cref="ProceduralLook.Version"/>); <see cref="ProceduralLook.Apply"/> writes everything.</summary>
    public static void SetTime(Material material, float time) => material.SetFloat(TimeId, time);
}

/// <summary>
/// What a layer draws: a program, its numbers (at most eight, kept in each one's range), its colours (sRGB, at most four) and the layer
/// transform (size, rotation, spin, intensity). Changing the program puts every number and colour back to the program's defaults. Nothing
/// here allocates after construction.
/// </summary>
public sealed class ProceduralLook
{
    private readonly float[] _values = new float[ProceduralCatalog.MaxParams];
    private readonly Color[] _colours = new Color[ProceduralCatalog.MaxColours];
    private float _scale = 1f, _rotation, _spin, _intensity = 1f;

    public ProceduralLook(ProceduralProgram program) => Use(program);

    public ProceduralProgram Program { get; private set; } = null!;

    /// <summary>Goes up on every change, so a painter writes its material only after one.</summary>
    public int Version { get; private set; }

    /// <summary>Draws <paramref name="program"/> with its default numbers and colours.</summary>
    public void Use(ProceduralProgram program)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Array.Clear(_values, 0, _values.Length);
        foreach (var p in program.Params) _values[p.Index] = p.Default;
        for (var i = 0; i < _colours.Length; i++) _colours[i] = Color.clear;
        foreach (var c in program.Colours) _colours[c.Index] = new Color(c.Default[0], c.Default[1], c.Default[2], c.Default[3]);
        Version++;
    }

    /// <summary>Number <paramref name="index"/> of the program (its <see cref="ProceduralParam.Index"/>).</summary>
    public float Get(int index) => _values[Checked(index, Program.Params.Count, "number")];

    /// <summary>Sets number <paramref name="index"/>, kept inside its range.</summary>
    public void Set(int index, float value)
    {
        var v = Program.Params[Checked(index, Program.Params.Count, "number")].Clamp(value);
        if (_values[index] == v) return;
        _values[index] = v;
        Version++;
    }

    public Color Colour(int slot) => _colours[Checked(slot, Program.Colours.Count, "colour")];

    /// <summary>Sets colour slot <paramref name="slot"/> (sRGB, alpha is coverage).</summary>
    public void SetColour(int slot, Color colour)
    {
        if (_colours[Checked(slot, Program.Colours.Count, "colour")] == colour) return;
        _colours[slot] = colour;
        Version++;
    }

    /// <summary>The pattern's size against the UV (1: as authored), kept in the catalogue's transform range.</summary>
    public float Scale { get => _scale; set => Change(ref _scale, Procedural.Catalog.Transform[0].Clamp(value)); }

    /// <summary>Degrees the pattern is turned about the UV centre.</summary>
    public float Rotation { get => _rotation; set => Change(ref _rotation, Procedural.Catalog.Transform[1].Clamp(value)); }

    /// <summary>Degrees a second the pattern keeps turning.</summary>
    public float Spin { get => _spin; set => Change(ref _spin, Procedural.Catalog.Transform[2].Clamp(value)); }

    /// <summary>Multiplies the colour (0 hides the layer, above 1 brightens it past its colours).</summary>
    public float Intensity { get => _intensity; set => Change(ref _intensity, Procedural.Catalog.Transform[3].Clamp(value)); }

    private void Change(ref float field, float value)
    {
        if (field == value) return;
        field = value;
        Version++;
    }

    private static int Checked(int index, int count, string what) =>
        index >= 0 && index < count ? index : throw new ArgumentOutOfRangeException(nameof(index), index, "the program has " + count + " " + what + (count == 1 ? "" : "s"));

    /// <summary>Writes the program, numbers, colours and transform to <paramref name="material"/> (one of <see cref="Procedural"/>'s), and
    /// the time in seconds the pattern moves by. Property ids are fixed; nothing is allocated.</summary>
    public void Apply(Material material, float time)
    {
        material.SetInt(Procedural.ProgramId, Program.Index);
        material.SetVector(Procedural.P0, new Vector4(_values[0], _values[1], _values[2], _values[3]));
        material.SetVector(Procedural.P1, new Vector4(_values[4], _values[5], _values[6], _values[7]));
        for (var i = 0; i < _colours.Length; i++) material.SetColor(Procedural.ColourIds[i], _colours[i]);
        material.SetVector(Procedural.Xf, new Vector4(_scale, _rotation * Mathf.Deg2Rad, _spin * Mathf.Deg2Rad, _intensity));
        material.SetFloat(Procedural.TimeId, time);
    }
}

/// <summary>
/// Lays one submesh out by its UV into a render texture with a <see cref="ProceduralLook"/> (<see cref="Procedural.Painter"/>). The draw is
/// one command buffer made once (target, clear, mesh); <see cref="Paint"/> writes the material only when the look changed, then the time,
/// and runs the buffer: no allocation, no lookup.
/// </summary>
public sealed class ProceduralPainter : IDisposable
{
    private readonly Material _material;
    private readonly CommandBuffer _buffer;
    private int _applied = -1;
    private Vector3 _view = Vector3.forward;
    private bool _viewDirty = true;

    internal ProceduralPainter(Mesh mesh, int submesh, RenderTexture target, ProceduralLook look, Material material, string label, bool clear)
    {
        Target = target;
        Look = look;
        _material = material;
        _buffer = new CommandBuffer { name = "ForgeVisual procedural " + label };
        _buffer.SetRenderTarget(new RenderTargetIdentifier(target));
        if (clear) _buffer.ClearRenderTarget(false, true, Color.clear);
        _buffer.DrawMesh(mesh, Matrix4x4.identity, material, submesh, 0);
    }

    public RenderTexture Target { get; }

    /// <summary>The look painted; change it freely, the next <see cref="Paint"/> picks the change up.</summary>
    public ProceduralLook Look { get; }

    /// <summary>The object-space direction toward the eye that a rim-light program shades against (a texture has no eye); +Z by default.</summary>
    public Vector3 ViewDirection
    {
        get => _view;
        set { if (value != _view) { _view = value; _viewDirty = true; } }
    }

    public bool Disposed { get; private set; }

    /// <summary>Paints the target now with the pattern at <paramref name="time"/> seconds.</summary>
    public void Paint(float time)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(ProceduralPainter));
        if (_applied != Look.Version)
        {
            Look.Apply(_material, time);
            _applied = Look.Version;
        }
        else _material.SetFloat(Procedural.TimeId, time);
        if (_viewDirty)
        {
            _material.SetVector(Procedural.View, new Vector4(_view.x, _view.y, _view.z, 0f));
            _viewDirty = false;
        }
        Graphics.ExecuteCommandBuffer(_buffer);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _buffer.Release();
        if (_material != null) Object.Destroy(_material);
    }
}
