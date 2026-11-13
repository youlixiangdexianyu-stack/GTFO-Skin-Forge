using ForgeSkin.Format;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// One code effect of the worn skin on one gear (Format CodeEffect): its look (ForgeCore ProceduralLook), when it is on and the clock it
// draws by. Always: on, by the game clock. An event: on for Seconds after it, by the time since it (a repeat restarts it). The player's
// changes (Skins.Tweak) are read again only when they moved; nothing else is written while nothing changes.
sealed class CodeFx : ITriggered
{
    public readonly CodeEffect Effect;
    public readonly ProceduralLook Look;
    readonly string _skin;
    readonly CodeEffectValues _values = new();
    float _start;
    bool _started;
    int _tweaks = -1;

    public CodeFx(string skin, CodeEffect effect)
    {
        _skin = skin;
        Effect = effect;
        CodeEffects.Resolve(effect, null, _values);
        Look = new ProceduralLook(_values.Program);
        Read();
    }

    public bool On(float time) => Effect.Trigger == Trigger.Always || _started && time - _start < Effect.Seconds;
    public float Clock(float time) => Effect.Trigger == Trigger.Always ? time : time - _start;

    public void Start(float time)
    {
        _start = time;
        _started = true;
    }

    public void Update(float time)
    {
        if (_tweaks != Skins.TweakVersion) Read();
    }

    void Read()
    {
        _tweaks = Skins.TweakVersion;
        CodeEffects.Resolve(Effect, Skins.ColoursOf(_skin), _values);
        for (int i = 0; i < _values.Program.Params.Count; i++) Look.Set(i, _values.Params[i]);
        for (int i = 0; i < _values.Program.Colours.Count; i++)
        {
            var c = _values.Colors[i];
            Look.SetColour(i, new Color(c[0], c[1], c[2], c[3]));
        }
        Look.Scale = _values.Transform[0];
        Look.Rotation = _values.Transform[1];
        Look.Spin = _values.Transform[2];
        Look.Intensity = _values.Transform[3];
    }
}

// The copy of one material channel that code effects paint over (CodeEffect.Material). Each frame one of them is on: the channel's
// texture as it is (the skin's texture, or the copy its dynamic layers draw into) is blitted in, then every effect that is on, in the
// skin's order, is blended over the UV islands of the meshes that wear the material (ForgeCore Procedural.Painter with `over`), then the
// mips are made. So the order on one texture is fixed: base texture, dynamic layers, code effects in document order. The copy and its
// painters exist only while the gear is in hand (Acquire / Release); otherwise the material shows the channel's texture again.
sealed class FxCanvas
{
    readonly Material _material;
    readonly int _property;
    readonly bool _albedo;
    readonly LayerTarget? _layers;
    readonly List<(CodeFx Fx, FxBlend Blend)> _effects = new();
    readonly List<(Mesh Mesh, int Submesh)> _meshes;
    readonly Renderer[] _renderers;
    readonly List<ProceduralPainter> _painters = new(); // per effect, one per mesh
    Texture? _source;
    RenderTexture? _out;
    bool _drawn;

    public FxCanvas(Material material, int property, bool albedo, LayerTarget? layers, List<(Mesh, int)> meshes, Renderer[] renderers)
    {
        (_material, _property, _albedo, _layers, _meshes, _renderers) = (material, property, albedo, layers, meshes, renderers);
        _source = material.GetTexture(property);
    }

    public Material Material => _material;
    public int Property => _property;
    public bool Live => _out != null;

    public void Add(CodeFx fx, FxBlend blend) => _effects.Add((fx, blend));

    // The material's texture for this channel changed under the canvas (wear reached another stage).
    public void Rebase()
    {
        var now = _material.GetTexture(_property);
        if (_out != null && now == _out) return;
        _source = now;
        if (_out != null) _material.SetTexture(_property, _out);
        _drawn = true; // draw once with the new base even if no effect is on
    }

    // Null when ForgeCore's shaders are not loaded (logged by ForgeCore); the material keeps its texture.
    public bool Acquire(string label)
    {
        if (_out != null) return true;
        var rt = new RenderTexture(_source?.width ?? 1024, _source?.height ?? 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        { useMipMap = true, autoGenerateMips = false, name = _material.name + " code effects" };
        rt.Create();
        foreach (var (fx, blend) in _effects)
            foreach (var (mesh, submesh) in _meshes)
            {
                var painter = Procedural.Painter(mesh, submesh, rt, fx.Look, $"{label} {fx.Effect.Id}", blend);
                if (painter == null) { Free(rt); return false; }
                _painters.Add(painter);
            }
        _out = ForgeCore.Assets.Keep(rt);
        _material.SetTexture(_property, _out);
        _drawn = true;
        return true;
    }

    public void Release()
    {
        if (_out == null) return;
        _material.SetTexture(_property, _source);
        Free(_out);
        _out = null;
    }

    void Free(RenderTexture rt)
    {
        foreach (var p in _painters) p.Dispose();
        _painters.Clear();
        Object.Destroy(rt);
    }

    // Before the gear's layer targets make their mips: a layer that drew this frame (its target dirty) is part of the base.
    public void Update(float time)
    {
        if (_out == null) return;
        bool any = false;
        for (int i = 0; i < _effects.Count && !any; i++) any = _effects[i].Fx.On(time);
        if (!any && !_drawn && _layers?.Dirty != true) return;
        // Nothing of it on screen (culled, a third-person copy no camera draws): the last picture stays until it is.
        if (any && !Seen()) return;
        Graphics.Blit(_source != null ? _source : _albedo ? Texture2D.whiteTexture : Texture2D.blackTexture, _out);
        int at = 0;
        foreach (var (fx, _) in _effects)
        {
            bool on = fx.On(time);
            float clock = fx.Clock(time);
            for (int m = 0; m < _meshes.Count; m++, at++)
                if (on) _painters[at].Paint(clock);
        }
        _out.GenerateMips();
        _drawn = any;
    }

    bool Seen()
    {
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].isVisible) return true;
        return false;
    }
}

// A code effect on an attachment's model (CodeEffect.Attachment): a copy of the model, 1 % larger about its centre so it lies just over
// the surface, drawn with a ForgeCore procedural material (alpha or additive, both faces, no depth write). It shows while the gear is in
// hand, the effect is on and the model itself shows (an attachment's Show effect or hologram flicker hide the model).
sealed class FxShell
{
    public const float Lift = 1.01f;
    readonly CodeFx _fx;
    readonly Renderer _model, _renderer;
    readonly Material _material;
    int _applied = -1;

    public FxShell(CodeFx fx, Renderer model, Renderer renderer, Material material) => (_fx, _model, _renderer, _material) = (fx, model, renderer, material);

    public void Update(float time, bool held)
    {
        bool show = held && _fx.On(time) && _model.enabled;
        if (_renderer.enabled != show) _renderer.enabled = show;
        if (!show) return;
        float clock = _fx.Clock(time);
        if (_applied != _fx.Look.Version)
        {
            _fx.Look.Apply(_material, clock);
            _applied = _fx.Look.Version;
        }
        else Procedural.SetTime(_material, clock);
    }
}

sealed partial class SkinnedGear
{
    readonly List<CodeFx> _fx = new();
    readonly List<FxCanvas> _canvases = new();
    readonly List<FxShell> _shells = new();
    bool _fxHeld;
    static IntPtr _keyOfPointer = (IntPtr)(-1);
    static string? _wieldedKey; // the target key of the item in the local player's hands

    // The worn skin's code effects (after materials, attachments and part layout, so the meshes are the ones shown). One bad effect is
    // left out with its reason; the others draw.
    int AddCodeEffects(Skin skin, SkinDocument doc)
    {
        if (doc.CodeEffects is not { Count: > 0 } list || _icon) return 0;
        foreach (var e in list)
        {
            if (!e.Visible) continue;
            // An effect on an attachment the author hid is hidden with it: said, not reported as a problem.
            if (e.Attachment != null && doc.Attachments.Find(a => a.Id == e.Attachment) is { Visible: false })
            {
                ForgeSkinPlugin.Info($"{_label}: code effect {e.Id} ({e.Name}): skipped, its attachment {e.Attachment} is hidden");
                continue;
            }
            try
            {
                var fx = new CodeFx(skin.Doc.Id, e);
                if (e.Material != null) PaintOver(fx, e);
                else DrawOn(fx, e);
                _fx.Add(fx);
                _animated.Add(fx);
                On(e.Trigger, fx, e.LowAmmoFraction);
                ForgeSkinPlugin.Info($"{_label}: code effect {e.Id} ({e.Name}) {e.Program} on {e.Material ?? e.Attachment}{(e.Material != null ? " " + e.Channel : "")}, {e.Blend}, {e.Trigger}");
            }
            catch (Exception x) when (x is InvalidOperationException or SkinFormatException)
            {
                ForgeSkinPlugin.Fail("skin-fx-skipped", $"{_label}: code effect {e.Id} ({e.Name}): skipped, {x.Message}", x);
            }
        }
        return _fx.Count;
    }

    void PaintOver(CodeFx fx, CodeEffect e)
    {
        var o = _looks.FirstOrDefault(l => l.Key.Material == e.Material);
        var look = o.Value ?? throw new InvalidOperationException($"material {e.Material} is on no renderer of this gear");
        int property = Property(e.Channel);
        var canvas = _canvases.Find(c => c.Material == look && c.Property == property);
        if (canvas == null)
        {
            var meshes = new List<(Mesh, int)>();
            var renderers = new List<Renderer>();
            var seen = new HashSet<(IntPtr, int)>();
            foreach (var (renderer, _, mine) in _swaps)
                for (int i = 0; i < mine.Length; i++)
                {
                    if (mine[i] != look || MeshOf(renderer) is not { } mesh || i >= mesh.subMeshCount) continue;
                    if (seen.Add((mesh.Pointer, i))) meshes.Add((mesh, i));
                    if (!renderers.Contains(renderer)) renderers.Add(renderer);
                }
            if (meshes.Count == 0) throw new InvalidOperationException($"material {e.Material} is on no mesh of this gear");
            if (e.Channel == TextureChannel.Emissive) SetEmissive(look, true);
            canvas = new FxCanvas(look, property, e.Channel == TextureChannel.Albedo, _targets.Find(t => t.Material == look && t.Property == property), meshes, renderers.ToArray());
            _canvases.Add(canvas);
        }
        canvas.Add(fx, e.Blend == CodeEffectBlend.Additive ? FxBlend.Additive : FxBlend.Alpha);
    }

    void DrawOn(CodeFx fx, CodeEffect e)
    {
        var (root, _, template) = _roots.Find(r => r.Id == e.Attachment);
        if (root == null) throw new InvalidOperationException($"attachment {e.Attachment} is not shown on this gear");
        var model = Find(root.transform, "mesh")?.GetComponent<MeshRenderer>() ?? throw new InvalidOperationException($"attachment {e.Attachment} has no model");
        var mesh = model.GetComponent<MeshFilter>().sharedMesh;
        // Drawn through the gear's own projection (its part material, which the game switched): a gear in the hands goes through the
        // first-person item camera, and a shell in the world's projection stands off the gun as a small copy. Later switches reach it
        // through TakeBack (_swaps).
        bool firstPerson = template.IsKeywordEnabled(FpsKeyword);
        var material = Procedural.CreateMaterial(e.Blend == CodeEffectBlend.Additive ? FxBlend.Additive : FxBlend.Alpha, firstPerson, "ForgeSkin code effect " + e.Id)
            ?? throw new InvalidOperationException("ForgeCore's shaders are not loaded");
        var shell = Child(model.transform.parent.gameObject, "code effect " + e.Id, mesh, Own(material), 0f);
        var t = shell.transform;
        t.localPosition = model.transform.localPosition + mesh.bounds.center * (1 - FxShell.Lift);
        t.localRotation = model.transform.localRotation;
        t.localScale = model.transform.localScale * FxShell.Lift;
        var renderer = shell.GetComponent<MeshRenderer>();
        renderer.enabled = false;
        _shells.Add(new FxShell(fx, model, renderer, material));
    }

    // Whether the code effects draw: the lobby's gear preview always, an icon never, a first-person item while in the local player's
    // hands, a third-person one while the first-person copy of the same gear is.
    bool FxHeld()
    {
        if (_item == null) return !_icon;
        if (_wielded != _keyOfPointer)
        {
            _keyOfPointer = _wielded;
            _wieldedKey = null;
            foreach (var g in Live)
                if (g._itemPointer == _wielded) { _wieldedKey = g._key; break; }
        }
        return _itemPointer == _wielded || _wieldedKey != null && _wieldedKey == _key;
    }

    // Per frame, after the effects and layers ran and before the layer targets make their mips.
    void UpdateCodeEffects(float time)
    {
        if (_fx.Count == 0) return;
        bool held = FxHeld();
        if (held != _fxHeld)
        {
            _fxHeld = held;
            foreach (var c in _canvases)
                if (!held) c.Release();
                else if (!c.Acquire(_label)) ForgeSkinPlugin.Fail("skin-fx-skipped", $"{_label}: code effects on {c.Material.name} not drawn, ForgeCore's shaders are not loaded");
            ForgeSkinPlugin.Info($"{_label}: code effects {(held ? "drawing" : "released, not in hand")}");
        }
        for (int i = 0; i < _canvases.Count; i++) _canvases[i].Update(time);
        for (int i = 0; i < _shells.Count; i++) _shells[i].Update(time, held);
    }

    // A material whose texture another step replaced (wear stages) paints its effects over the new one.
    void RebaseCodeEffects(Material look)
    {
        foreach (var c in _canvases)
            if (c.Material == look) c.Rebase();
    }

    // Revert: painters and copies go before the materials they draw into are destroyed.
    void ReleaseCodeEffects()
    {
        foreach (var c in _canvases) c.Release();
        _canvases.Clear();
        _shells.Clear();
        _fx.Clear();
        _fxHeld = false;
    }
}
