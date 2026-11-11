using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using SkinForge.Format;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForge.Plugin;

// The skin applied to one gear of the local player (first or third person). Everything it creates is its own and is
// destroyed on revert; the game's shared materials and textures are never written.
sealed class SkinnedGear
{
    public static readonly int MainTex = Shader.PropertyToID("_MainTex"), BumpMap = Shader.PropertyToID("_BumpMap"),
        MetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap"), EmissiveMap = Shader.PropertyToID("_EmissiveMap"),
        EnableEmissive = Shader.PropertyToID("_EnableEmissive"), EmissiveColor = Shader.PropertyToID("_EmissiveColor"),
        EmissionColor = Shader.PropertyToID("_EmissionColor"), Color = Shader.PropertyToID("_Color"),
        Metallic = Shader.PropertyToID("_Metallic"), Glossiness = Shader.PropertyToID("_Glossiness"),
        Smoothness = Shader.PropertyToID("_Smoothness"), Cutoff = Shader.PropertyToID("_Cutoff");

    // The gear shader's emission gain is large: 0.01 shows a linear colour of 1 as itself (in-game measurement in GTFO-Forge).
    public const float EmissionUnit = 0.01f;

    static readonly List<SkinnedGear> Live = new();
    static readonly List<(GearPartHolder Holder, ItemEquippable Item, float Until)> Pending = new();
    static float _nextPending;

    readonly GearPartHolder _holder;
    readonly ItemEquippable _item;
    readonly IntPtr _holderPointer, _feeder;
    readonly string _label;
    readonly List<(Renderer Renderer, Il2CppReferenceArray<Material> Original, Il2CppReferenceArray<Material> Mine)> _swaps = new();
    readonly List<Object> _created = new();
    readonly List<(Material Material, int Property, RenderTexture Target)> _targets = new();
    readonly List<Layer> _layers = new();
    readonly List<AmmoScreen> _screens = new();
    readonly List<Transform> _faceCamera = new();
    readonly List<PartPose> _poses = new();
    Transform? _camera;
    PlayerAmmoStorage? _storage;
    AmmoType _ammoType;
    int _clipSize;

    SkinnedGear(GearPartHolder holder, ItemEquippable item)
    {
        _holder = holder;
        _item = item;
        _holderPointer = holder.Pointer;
        _feeder = holder.MaterialFeeder?.Pointer ?? IntPtr.Zero;
        _label = $"{item.PublicName} [{(item.m_isFirstPerson ? "1P" : "3P")}]";
    }

    // ---- game events (main thread) ----

    public static void Spawned(GearPartHolder holder)
    {
        try
        {
            var item = holder.GetComponentInParent<ItemEquippable>();
            if (item == null) return; // lobby and menu previews
            Forget(holder.Pointer);
            Pending.Add((holder, item, Time.unscaledTime + 30f));
        }
        catch (Exception e) { SkinForgePlugin.Fail($"gear spawn: {e.Message}"); }
    }

    public static void FeederScanned(IntPtr feeder)
    {
        try
        {
            foreach (var gear in Live)
                if (gear._feeder == feeder)
                    foreach (var swap in gear._swaps)
                        if (swap.Renderer != null) swap.Renderer.sharedMaterials = swap.Mine;
        }
        catch (Exception e) { SkinForgePlugin.Fail($"feeder rescan: {e.Message}"); }
    }

    public static void Tick(float now, float time)
    {
        if (Skins.Poll(now))
        {
            foreach (var gear in Live)
            {
                gear.Revert();
                if (gear._holder != null) gear.TryApply();
            }
        }
        if (Pending.Count > 0 && now >= _nextPending) ResolvePending(now);
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var gear = Live[i];
            if (gear._holder == null) { gear.Revert(); Live.RemoveAt(i); continue; }
            try { gear.Update(time); }
            catch (Exception e)
            {
                gear.Revert();
                SkinForgePlugin.Fail($"{gear._label}: update failed: {e.Message}; left vanilla");
            }
        }
    }

    // The owner is assigned after the parts spawn; look a few times a second until it is known.
    static void ResolvePending(float now)
    {
        _nextPending = now + 0.25f;
        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            var (holder, item, until) = Pending[i];
            if (holder == null || now > until) { Pending.RemoveAt(i); continue; }
            var player = item.Owner?.Owner;
            if (player == null) continue;
            Pending.RemoveAt(i);
            if (!player.IsLocal || player.IsBot) continue;
            // Every local gear is tracked, skinned or not, so a hot reload can skin it later.
            var gear = new SkinnedGear(holder, item);
            Live.Add(gear);
            gear.TryApply();
        }
    }

    static void Forget(IntPtr holder)
    {
        Pending.RemoveAll(p => p.Holder.Pointer == holder);
        for (int i = Live.Count - 1; i >= 0; i--)
            if (Live[i]._holderPointer == holder) { Live[i].Revert(); Live.RemoveAt(i); }
    }

    void TryApply()
    {
        try { Apply(); }
        catch (Exception e)
        {
            Revert();
            SkinForgePlugin.Fail($"{_label}: {e.Message}; left vanilla");
        }
    }

    // ---- apply / revert ----

    void Apply()
    {
        uint archetype = _item.ArchetypeData?.persistentID ?? 0;
        var overrides = new Dictionary<string, (Skin Skin, MaterialOverride Override)>();
        foreach (var id in Skins.Globals)
            if (Skins.ById.TryGetValue(id, out var global))
                foreach (var m in global.Doc.Materials) overrides[m.Material] = (global, m);
        Skin? weapon = null;
        if (archetype != 0 && Skins.Weapons.TryGetValue(archetype, out var weaponId))
        {
            if (!Skins.ById.TryGetValue(weaponId, out weapon)) throw new InvalidOperationException($"active skin {weaponId} is not installed");
            if (weapon.Doc.Target.ArchetypeId != archetype)
                throw new InvalidOperationException($"skin {weaponId} targets archetype {weapon.Doc.Target.ArchetypeId}, not {archetype}");
            foreach (var m in weapon.Doc.Materials) overrides[m.Material] = (weapon, m);
        }
        if (overrides.Count == 0 && (weapon == null || weapon.Doc.Attachments.Count == 0 && weapon.Doc.Parts.Count == 0)) return;

        ApplyMaterials(overrides);
        int attachments = 0;
        if (weapon != null)
            foreach (var a in weapon.Doc.Attachments)
                if (a.Visible) { Attach(weapon, a); attachments++; }
        // After the attachments, so hiding a part also hides what is mounted under it.
        int hidden = 0;
        if (weapon != null)
            foreach (var p in weapon.Doc.Parts)
            {
                var part = Part(p.Part) ?? throw new InvalidOperationException($"part layout: gear has no part {p.Part}");
                var t = p.Transform == null ? part.transform : Find(part.transform, p.Transform)
                    ?? throw new InvalidOperationException($"part layout: no transform {p.Transform} under {p.Part}");
                _poses.Add(new PartPose(t, p));
                if (!p.Visible) hidden++;
            }
        SkinForgePlugin.Info($"{_label}: archetype {archetype}, skin {weapon?.Doc.Id ?? "-"}, {_swaps.Count} renderers, " +
                             $"{attachments} attachments, {_screens.Count} screens, {_layers.Count} layers, {_poses.Count} part poses ({hidden} hidden)");
    }

    void Revert()
    {
        foreach (var pose in _poses) pose.Revert();
        _poses.Clear();
        foreach (var swap in _swaps)
            if (swap.Renderer != null) swap.Renderer.sharedMaterials = swap.Original;
        foreach (var o in _created)
            if (o != null) Object.Destroy(o);
        _swaps.Clear();
        _created.Clear();
        _targets.Clear();
        _layers.Clear();
        _screens.Clear();
        _faceCamera.Clear();
    }

    void ApplyMaterials(Dictionary<string, (Skin Skin, MaterialOverride Override)> overrides)
    {
        if (overrides.Count == 0) return;
        var looks = new Dictionary<MaterialOverride, Material>();
        foreach (var renderer in _holder.GetComponentsInChildren<Renderer>(true))
        {
            var original = renderer.sharedMaterials;
            Il2CppReferenceArray<Material>? mine = null;
            for (int i = 0; i < original.Length; i++)
            {
                var source = original[i];
                if (source == null || !overrides.TryGetValue(BaseName(source.name), out var o)) continue;
                if (!looks.TryGetValue(o.Override, out var look)) looks[o.Override] = look = Look(o.Skin, o.Override, source);
                mine ??= renderer.sharedMaterials;
                mine[i] = look;
            }
            if (mine == null) continue;
            renderer.sharedMaterials = mine;
            _swaps.Add((renderer, original, mine));
        }
    }

    static string BaseName(string name) => name.EndsWith(" (Instance)", StringComparison.Ordinal) ? name[..^11] : name;

    Material Look(Skin skin, MaterialOverride o, Material source)
    {
        var look = Own(new Material(source) { name = source.name + " (SkinForge)" });
        foreach (var (channel, path) in o.Textures) look.SetTexture(Property(channel), skin.Texture(path, Skin.Linear(channel)));
        if (o.Textures.ContainsKey(TextureChannel.Emissive) && o.Params.EmissiveEnabled != false) SetEmissive(look, true);
        Params(look, o.Params);
        foreach (var layer in o.Layers)
            if (layer.Visible) AddLayer(skin, look, layer);
        return look;
    }

    void Params(Material m, MaterialParams p)
    {
        if (p.EmissiveEnabled is bool on) SetEmissive(m, on);
        if (p.EmissiveColor != null || p.EmissiveIntensity != null)
        {
            var c = p.EmissiveColor is { } e ? new Color(e[0], e[1], e[2]) : m.GetColor(EmissiveColor);
            SetColor(m, EmissiveColor, c * (p.EmissiveIntensity ?? 1f), "emissive colour");
            if (m.HasProperty(EmissionColor)) m.SetColor(EmissionColor, c * (p.EmissiveIntensity ?? 1f));
        }
        if (p.Metallic is float metal) SetFloat(m, Metallic, metal, "metallic");
        if (p.Smoothness is float smooth) SetFloat(m, m.HasProperty(Glossiness) ? Glossiness : Smoothness, smooth, "smoothness");
        if (p.Tint is { } t) SetColor(m, Color, new Color(t[0], t[1], t[2], t[3]), "tint");
    }

    void SetColor(Material m, int property, Color value, string what)
    {
        if (m.HasProperty(property)) m.SetColor(property, value);
        else SkinForgePlugin.Fail($"{_label}: material {m.name} has no {what}; ignored");
    }

    void SetFloat(Material m, int property, float value, string what)
    {
        if (m.HasProperty(property)) m.SetFloat(property, value);
        else SkinForgePlugin.Fail($"{_label}: material {m.name} has no {what}; ignored");
    }

    public static void SetEmissive(Material m, bool on)
    {
        if (m.HasProperty(EnableEmissive)) m.SetFloat(EnableEmissive, on ? 1f : 0f);
        if (on) m.EnableKeyword("ENABLE_EMISSIVE");
        else m.DisableKeyword("ENABLE_EMISSIVE");
    }

    public static void SetEmission(Material m, Color color)
    {
        if (m.HasProperty(EmissiveColor)) m.SetColor(EmissiveColor, color);
        if (m.HasProperty(EmissionColor)) m.SetColor(EmissionColor, color);
        SetEmissive(m, true);
    }

    static int Property(TextureChannel channel) => channel switch
    {
        TextureChannel.Albedo => MainTex,
        TextureChannel.Normal => BumpMap,
        TextureChannel.MetallicSmoothness => MetallicGlossMap,
        _ => EmissiveMap,
    };

    public T Own<T>(T o) where T : Object
    {
        // Assets only referenced from here must survive the game's Resources.UnloadUnusedAssets.
        if (typeof(T) != typeof(GameObject)) o.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _created.Add(o);
        return o;
    }

    // ---- dynamic layers ----

    void AddLayer(Skin skin, Material material, DynamicLayer layer)
    {
        if (SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None)
            throw new InvalidOperationException($"layer {layer.Id}: this GPU cannot copy textures");
        var a = layer.Atlas!;
        var atlas = skin.Texture(a.Path, Skin.Linear(layer.Channel));
        var target = Target(material, layer.Channel);
        int[] r = a.TargetRect;
        if (r[0] < 0 || r[1] < 0 || r[0] + r[2] > target.width || r[1] + r[3] > target.height)
            throw new InvalidOperationException($"layer {layer.Id}: target rect outside the {target.width}x{target.height} texture");
        int rows = (a.FrameCount + a.Columns - 1) / a.Columns;
        if (a.Columns * a.FrameWidth > atlas.width || rows * a.FrameHeight > atlas.height)
            throw new InvalidOperationException($"layer {layer.Id}: atlas {atlas.width}x{atlas.height} smaller than its frames");
        if (layer.Channel == TextureChannel.Emissive) SetEmissive(material, true);
        // Rects are image space (top-left origin); Unity textures start bottom-left.
        _layers.Add(new Layer(atlas, target, a, layer.Fps, layer.Loop, r[0], target.height - r[1] - r[3]));
        SkinForgePlugin.Info($"{_label}: layer {layer.Id} {atlas.width}x{atlas.height} {atlas.format}, {a.FrameCount} frames -> {material.name} {layer.Channel}");
    }

    // One GPU copy of the channel's texture per material; layers write into it.
    RenderTexture Target(Material material, TextureChannel channel)
    {
        int property = Property(channel);
        foreach (var t in _targets)
            if (t.Material == material && t.Property == property) return t.Target;
        var source = material.GetTexture(property);
        if (source == null && channel == TextureChannel.Normal) throw new InvalidOperationException($"{material.name} has no normal map to draw a layer on");
        var linear = Skin.Linear(channel);
        var target = Own(new RenderTexture(source?.width ?? 1024, source?.height ?? 1024, 0, RenderTextureFormat.ARGB32,
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB) { useMipMap = true, autoGenerateMips = false, name = material.name + " layers" });
        target.Create();
        Graphics.Blit(source ?? (channel == TextureChannel.Albedo ? Texture2D.whiteTexture : Texture2D.blackTexture), target);
        target.GenerateMips();
        material.SetTexture(property, target);
        _targets.Add((material, property, target));
        return target;
    }

    // ---- attachments and screens ----

    void Attach(Skin skin, Attachment a)
    {
        var part = Part(a.Part) ?? throw new InvalidOperationException($"attachment {a.Id}: gear has no part {a.Part}");
        var anchor = a.Transform == null ? part.transform : Find(part.transform, a.Transform)
            ?? throw new InvalidOperationException($"attachment {a.Id}: no transform {a.Transform} under {a.Part}");
        var template = part.GetComponentInChildren<Renderer>(true)?.sharedMaterial
            ?? throw new InvalidOperationException($"attachment {a.Id}: part {a.Part} has no material to match");

        var root = Own(new GameObject("SkinForge " + a.Id));
        root.layer = anchor.gameObject.layer;
        var t = root.transform;
        t.SetParent(anchor, false);
        t.localPosition = new Vector3(a.Position[0], a.Position[1], a.Position[2]);
        t.localRotation = Quaternion.Euler(a.Rotation[0], a.Rotation[1], a.Rotation[2]);
        t.localScale = new Vector3(a.Scale[0], a.Scale[1], a.Scale[2]);
        if (a.FaceCamera)
        {
            _faceCamera.Add(t);
            _camera ??= PlayerManager.GetLocalPlayerAgent()?.FPSCamera?.transform;
        }
        if (a.Mesh != null) Child(root, "mesh", skin.Mesh(a.Mesh), Surface(skin, a.Material ?? new AttachmentMaterial(), template), 0f);
        if (a.Screen != null) _screens.Add(new AmmoScreen(this, skin, a.Screen, template, root));
    }

    // Catalog part ids → the holder's spawned parts.
    GameObject? Part(string id) => id switch
    {
        "front" or "body" => _holder.FrontPart,
        "front_attachment_a" => _holder.FrontPartAttachmentA,
        "front_attachment_b" => _holder.FrontPartAttachmentB,
        "receiver" => _holder.ReceiverPart,
        "receiver_attachment" => _holder.ReceiverPartAttachment,
        "stock" => _holder.StockPart,
        "sight" => _holder.SightPart,
        "mag" => _holder.MagPart,
        "flashlight" => _holder.FlashlightPart,
        "tool_main" => _holder.ToolMainPart,
        "tool_grip" => _holder.ToolGripPart,
        "tool_delivery" => _holder.ToolDeliveryPart,
        "tool_payload" => _holder.ToolPayloadPart,
        "tool_targeting" => _holder.ToolTargetingPart,
        "tool_screen" => _holder.ToolScreenPart,
        "melee_head" => _holder.MeleeHeadPart,
        "melee_neck" => _holder.MeleeNeckPart,
        "melee_handle" => _holder.MeleeHandlePart,
        "melee_pommel" => _holder.MeleePommelPart,
        _ => null,
    };

    static Transform? Find(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
            if (Find(parent.GetChild(i), name) is { } found) return found;
        return null;
    }

    // A copy of the anchor part's material keeps the gear shader and its first-person projection.
    Material Surface(Skin skin, AttachmentMaterial spec, Material template)
    {
        var m = Own(new Material(template) { name = "SkinForge attachment" });
        Texture? Tex(TextureChannel c) => spec.Textures.TryGetValue(c, out var p) ? skin.Texture(p, Skin.Linear(c)) : null;
        var color = new Color(spec.Color[0], spec.Color[1], spec.Color[2], spec.Color[3]);
        m.SetTexture(BumpMap, Tex(TextureChannel.Normal));
        m.SetTexture(MetallicGlossMap, Tex(TextureChannel.MetallicSmoothness) ?? Texture2D.blackTexture);
        if (spec.Unlit)
        {
            // Unlit: black albedo, the picture as emission.
            m.SetTexture(MainTex, Texture2D.blackTexture);
            m.SetTexture(EmissiveMap, Tex(TextureChannel.Albedo) ?? Texture2D.whiteTexture);
            SetEmission(m, color * EmissionUnit);
            return m;
        }
        m.SetTexture(MainTex, Tex(TextureChannel.Albedo) ?? Texture2D.whiteTexture);
        if (m.HasProperty(Color)) m.SetColor(Color, color);
        var emissive = Tex(TextureChannel.Emissive);
        m.SetTexture(EmissiveMap, emissive);
        SetEmissive(m, emissive != null);
        Params(m, spec.Params);
        return m;
    }

    public MeshFilter Child(GameObject parent, string name, Mesh mesh, Material material, float z)
    {
        var go = new GameObject(name) { layer = parent.layer };
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = new Vector3(0, 0, z);
        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return filter;
    }

    public PlayerAmmoStorage Storage(out AmmoType type, out int clipSize)
    {
        _storage ??= PlayerBackpackManager.LocalBackpack?.AmmoStorage ?? throw new InvalidOperationException("local player has no ammo storage");
        _ammoType = _item.AmmoType;
        _clipSize = _item.ClipSize;
        type = _ammoType;
        clipSize = _clipSize;
        return _storage;
    }

    // ---- per frame: no allocation, no lookups ----

    // After the Animator: re-applies the deltas on whatever pose animation wrote this frame.
    public static void LateTick()
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var poses = Live[i]._poses;
            for (int j = 0; j < poses.Count; j++) poses[j].Enforce();
        }
    }

    void Update(float time)
    {
        for (int i = 0; i < _layers.Count; i++) _layers[i].Update(time);
        if (_screens.Count > 0)
        {
            int clip = _item.GetCurrentClip();
            for (int i = 0; i < _screens.Count; i++) _screens[i].Update(clip);
        }
        if (_camera != null)
        {
            var eye = _camera.position;
            for (int i = 0; i < _faceCamera.Count; i++)
            {
                var t = _faceCamera[i];
                // Local +Z away from the camera: the screen's visible -Z face looks at it.
                t.rotation = Quaternion.LookRotation(t.position - eye);
            }
        }
    }
}

// One animated layer: atlas frames copied into the target region on the GPU when the frame changes.
sealed class Layer
{
    readonly Texture2D _atlas;
    readonly RenderTexture _target;
    readonly int _count, _columns, _width, _height, _atlasHeight, _x, _y;
    readonly float _fps;
    readonly bool _loop;
    int _frame = -1;

    public Layer(Texture2D atlas, RenderTexture target, LayerAtlas a, float fps, bool loop, int x, int y)
    {
        _atlas = atlas; _target = target;
        _count = a.FrameCount; _columns = a.Columns; _width = a.FrameWidth; _height = a.FrameHeight;
        _atlasHeight = atlas.height; _x = x; _y = y; _fps = fps; _loop = loop;
    }

    public void Update(float time)
    {
        int frame = _fps > 0 ? (int)(time * _fps) : 0;
        frame = _loop ? frame % _count : Math.Min(frame, _count - 1);
        if (frame == _frame) return;
        _frame = frame;
        int col = frame % _columns, row = frame / _columns;
        Graphics.CopyTexture(_atlas, 0, 0, col * _width, _atlasHeight - (row + 1) * _height, _width, _height, _target, 0, 0, _x, _y);
        _target.GenerateMips();
    }
}

// One part-layout entry: vanilla local pose kept for revert; the delta goes on top of whatever pose the transform has,
// so an Animator writing it every frame keeps the delta. Hidden parts keep their renderers disabled.
sealed class PartPose
{
    readonly Transform _t;
    readonly Vector3 _dPos, _dScale;
    readonly Quaternion _dRot;
    readonly Renderer[] _hidden;
    readonly bool[] _wasEnabled;
    Vector3 _basePos, _baseScale, _lastPos, _lastScale;
    Quaternion _baseRot, _lastRot;

    public PartPose(Transform t, PartTransform p)
    {
        _t = t;
        _dPos = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
        _dRot = Quaternion.Euler(p.Rotation[0], p.Rotation[1], p.Rotation[2]);
        _dScale = new Vector3(p.Scale[0], p.Scale[1], p.Scale[2]);
        _hidden = p.Visible ? Array.Empty<Renderer>() : t.GetComponentsInChildren<Renderer>(true).ToArray();
        _wasEnabled = new bool[_hidden.Length];
        for (int i = 0; i < _hidden.Length; i++) _wasEnabled[i] = _hidden[i].enabled;
        _basePos = t.localPosition;
        _baseRot = t.localRotation;
        _baseScale = t.localScale;
        t.localPosition = _lastPos = _basePos + _dPos;
        t.localRotation = _lastRot = _baseRot * _dRot;
        t.localScale = _lastScale = Vector3.Scale(_baseScale, _dScale);
        Enforce();
    }

    public void Enforce()
    {
        if (_t == null) return;
        var pos = _t.localPosition;
        if (pos != _lastPos) { _basePos = pos; _t.localPosition = _lastPos = pos + _dPos; }
        var rot = _t.localRotation;
        if (rot != _lastRot) { _baseRot = rot; _t.localRotation = _lastRot = rot * _dRot; }
        var scale = _t.localScale;
        if (scale != _lastScale) { _baseScale = scale; _t.localScale = _lastScale = Vector3.Scale(scale, _dScale); }
        for (int i = 0; i < _hidden.Length; i++)
            if (_hidden[i] != null) _hidden[i].enabled = false;
    }

    public void Revert()
    {
        for (int i = 0; i < _hidden.Length; i++)
            if (_hidden[i] != null) _hidden[i].enabled = _wasEnabled[i];
        if (_t == null) return;
        _t.localPosition = _basePos;
        _t.localRotation = _baseRot;
        _t.localScale = _baseScale;
    }
}
