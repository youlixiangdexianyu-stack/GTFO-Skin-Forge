using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using ForgeSkin.Format;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

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
    static readonly List<(GearPartHolder Holder, ItemEquippable Item, float Since)> Pending = new();
    static readonly HashSet<IntPtr> Reported = new(); // holders whose decision was logged
    static float _nextPending;

    // A gear built from parts has its holder; an item with its own prefabs (pack, consumable, carry item, hacking tool)
    // has none and is skinned on the item itself, its one part `body`.
    readonly GearPartHolder? _holder;
    readonly Component _root; // the holder, or the item
    readonly uint _itemId; // ItemDataBlock persistentID of an item without holder
    readonly ItemEquippable? _item; // null for the menu preview
    readonly IntPtr _holderPointer, _feeder;
    readonly string _label;
    public string Label => _label;
    readonly List<(Renderer Renderer, Il2CppReferenceArray<Material> Original, Il2CppReferenceArray<Material> Mine)> _swaps = new();
    readonly List<Object> _created = new();
    readonly List<(Material Material, int Property, RenderTexture Target)> _targets = new();
    readonly List<ITriggered> _animated = new(); // layers and effects, ticked every frame
    static readonly int TriggerCount = Enum.GetValues<Trigger>().Length;
    readonly List<ITriggered>[] _on = new List<ITriggered>[TriggerCount]; // by Trigger
    readonly List<(ITriggered Handler, float Fraction)> _lowAmmo = new();
    readonly List<bool> _below = new();
    readonly bool[] _logged = new bool[TriggerCount];
    readonly IntPtr _itemPointer;
    int _layerCount;
    public int Kills; // this weapon's kills this level
    readonly List<AmmoScreen> _screens = new();
    readonly List<(Renderer Renderer, Material Material)> _own = new(); // attachment and screen renderers we created
    static readonly MaterialPropertyBlock NoBlock = new(), Probe = new();
    readonly List<Transform> _faceCamera = new();
    readonly List<PartPose> _poses = new();
    readonly List<(GameObject Root, string Id, Material Template)> _roots = new(); // attachment roots
    bool _equipped; // first wield seen: diagnostics logged
    bool _flashlight, _aiming; // edges for FlashlightOn/Off and AimEnd
    int _lastClip = -1, _lastReserve = -1; // edges for Empty and AmmoPickup; -1 until the first poll
    int _unequipFrame = -1; // a base and an override unwield in the same frame are one Unequip
    Transform? _camera;
    PlayerAmmoStorage? _storage;
    AmmoType _ammoType;
    int _clipSize;

    SkinnedGear(GearPartHolder? holder, ItemEquippable? item, uint itemId = 0)
    {
        _holder = holder;
        _root = (Component?)holder ?? item!;
        _itemId = itemId;
        _item = item;
        _holderPointer = _root.Pointer;
        _itemPointer = item?.Pointer ?? IntPtr.Zero;
        _clipSize = item?.ClipSize ?? 0;
        for (int i = 0; i < _on.Length; i++) _on[i] = new List<ITriggered>();
        _feeder = holder?.MaterialFeeder?.Pointer ?? IntPtr.Zero;
        _label = item == null ? $"{holder!.GearIDRange?.PublicGearName} [menu]" : $"{item.PublicName} [{(item.m_isFirstPerson ? "1P" : "3P")}{(holder == null ? " item" : "")}]";
    }

    // ---- game events (main thread) ----

    public static void Spawned(GearPartHolder holder)
    {
        try
        {
            var item = holder.GetComponentInParent<ItemEquippable>();
            if (Forget(holder.Pointer)) Reported.Remove(holder.Pointer);
            // With an item, the readback was taken when its spawn-complete step began (BeforeItemSetup).
            if (item == null) Readback.Write(holder, GearId(holder));
            if (item != null) Consider(holder, item);
            // A menu-mode holder (lobby loadout and gear pages) only ever shows this machine's own gear.
            else if (holder.MenuMode) { Note(holder, $"{holder.GearIDRange?.PublicGearName} [menu]: considered"); Track(holder, null); }
            // Icon renders, and items that only take their holder at OnGearSpawnComplete.
            else Note(holder, $"{holder.GearIDRange?.PublicGearName}: parts spawned without an item and not in menu mode; waiting for the item");
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"gear spawn: {e.Message}"); }
    }

    // dump.cs 565443: the item has its part holder.
    // Prefix of the item's spawn-complete step: the parts are assembled and aligned, but the item's own setup (magazine
    // rounds, bolt and animator state) has not run yet, so this is the vanilla pose readback reports.
    public static void BeforeItemSetup(ItemEquippable item)
    {
        if (Readback.Directory == null) return; // debug only: touch nothing on the game's spawn path otherwise
        try
        {
            var holder = item.GearPartHolder;
            if (holder != null) Readback.Write(holder, GearId(holder));
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"readback: {e.Message}"); }
    }

    public static void ItemReady(ItemEquippable item)
    {
        try
        {
            var holder = item.GearPartHolder;
            if (holder != null) Consider(holder, item);
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"gear spawn complete: {e.Message}"); }
    }

    static void Consider(GearPartHolder holder, ItemEquippable item)
    {
        var pointer = holder.Pointer;
        foreach (var p in Pending)
            if (p.Holder.Pointer == pointer) return;
        foreach (var g in Live)
            if (g._holderPointer == pointer) return;
        Note(holder, $"{item.PublicName} [{(item.m_isFirstPerson ? "1P" : "3P")}]: considered, gear {GearId(holder)}");
        Pending.Add((holder, item, Time.unscaledTime));
    }

    // One line per gear and decision.
    static void Note(GearPartHolder holder, string message)
    {
        if (Reported.Add(holder.Pointer)) ForgeSkinPlugin.Info(message);
    }

    public static void FeederScanned(IntPtr feeder)
    {
        try
        {
            foreach (var gear in Live)
                if (gear._feeder == feeder)
                {
                    foreach (var swap in gear._swaps)
                        if (swap.Renderer != null) swap.Renderer.sharedMaterials = swap.Mine;
                    gear.KeepOwn("after feeder rescan");
                }
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"feeder rescan: {e.Message}"); }
    }

    public static void Tick(float now, float time)
    {
        if (Skins.Poll(now))
        {
            foreach (var gear in Live)
            {
                gear.Revert();
                if (gear._root != null) gear.TryApply();
            }
        }
        if (Pending.Count > 0 && now >= _nextPending) ResolvePending(now);
        PollWielded(time);
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var gear = Live[i];
            if (gear._root == null) { gear.Revert(); Live.RemoveAt(i); continue; }
            try { gear.Update(time); }
            catch (Exception e)
            {
                gear.Revert();
                ForgeSkinPlugin.Fail($"{gear._label}: update failed: {e.Message}; left vanilla");
            }
        }
    }

    // A first-person item only exists for the local player. A third-person one waits for its owner, which can be set
    // long after the parts spawn (gear is built while the level generates); looked at a few times a second.
    static void ResolvePending(float now)
    {
        _nextPending = now + 0.25f;
        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            var (holder, item, since) = Pending[i];
            if (holder == null || item == null) { Pending.RemoveAt(i); continue; }
            if (!item.m_isFirstPerson)
            {
                var player = item.Owner?.Owner;
                if (player == null)
                {
                    // Icon renders and lobby previews are built as third-person items that never get an owner.
                    if (now - since < 5f) continue;
                    Pending.RemoveAt(i);
                    Reported.Remove(holder.Pointer);
                    Note(holder, $"{item.PublicName} [3P]: skipped, no owner (icon/preview)");
                    continue;
                }
                if (!player.IsLocal || player.IsBot)
                {
                    Pending.RemoveAt(i);
                    Reported.Remove(holder.Pointer);
                    Note(holder, $"{item.PublicName} [3P]: skipped, owner {player.NickName} is not the local player");
                    continue;
                }
            }
            Pending.RemoveAt(i);
            Track(holder, item);
        }
    }

    // Every local gear is tracked, skinned or not, so a hot reload can skin it later.
    static void Track(GearPartHolder holder, ItemEquippable? item) => Track(new SkinnedGear(holder, item));

    static SkinnedGear Track(SkinnedGear gear)
    {
        Live.Add(gear);
        gear.TryApply();
        return gear;
    }

    static IntPtr _wielded;
    static readonly InventorySlot[] ItemSlots = { InventorySlot.ResourcePack, InventorySlot.Consumable, InventorySlot.InLevelCarry, InventorySlot.HackingTool };

    // Items without a gear holder have no spawn point of their own: the local player's wielded item is compared each
    // frame (one pointer), so taking one out tracks it and is its Equip, and putting it away (dropping or placing a
    // carry item) is its Unequip. The same first-person object can later hold another item; that is a new target.
    static void PollWielded(float time)
    {
        var item = PlayerManager.GetLocalPlayerAgent()?.Inventory?.WieldedItem;
        var pointer = item?.Pointer ?? IntPtr.Zero;
        if (pointer == _wielded) return;
        try
        {
            var previous = _wielded;
            _wielded = pointer;
            foreach (var gear in Live)
                if (gear._holder == null && gear._itemPointer == previous) gear.Raise(Trigger.Unequip, gear._on[(int)Trigger.Unequip], time);
            if (item == null || item.GearPartHolder != null || !item.m_isFirstPerson) return;
            var data = item.ItemDataBlock;
            if (data == null || Array.IndexOf(ItemSlots, data.inventorySlot) < 0) return;
            SkinnedGear? tracked = null;
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (Live[i]._itemPointer != pointer) continue;
                if (Live[i]._itemId == data.persistentID) tracked = Live[i];
                else { Live[i].Revert(); Live.RemoveAt(i); }
            }
            tracked ??= Track(new SkinnedGear(null, item, data.persistentID));
            tracked.Equipped();
            tracked.Raise(Trigger.Equip, tracked._on[(int)Trigger.Equip], time);
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"wielded item: {e.Message}"); }
    }

    // The gear's PlayerOfflineGearDataBlock persistentID: the game names offline gear "OfflineGear_ID_<id>" in
    // PlayfabItemInstanceId. A range rebuilt from a network packet or JSON keeps only the components, so it is matched
    // against the game's own gear list by components. 0 when unknown.
    static uint GearId(GearPartHolder holder)
    {
        var range = holder.GearIDRange;
        if (range == null) return 0;
        if (Offline(range.PlayfabItemInstanceId) is { } id) return id;
        foreach (var slot in GearSlots)
        {
            var all = GearManager.GetAllGearForSlot(slot);
            if (all == null) continue;
            foreach (var candidate in all)
                if (candidate != null && Offline(candidate.PlayfabItemInstanceId) is { } match && SameComps(candidate, range)) return match;
        }
        return 0;
    }

    static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    const string OfflinePrefix = "OfflineGear_ID_";

    static uint? Offline(string? instance) =>
        instance != null && instance.StartsWith(OfflinePrefix, StringComparison.Ordinal) && uint.TryParse(instance.AsSpan(OfflinePrefix.Length), out var id) ? id : null;

    static bool SameComps(GearIDRange a, GearIDRange b)
    {
        for (int c = 1; c <= (int)eGearComponent.PlatformPerkCID; c++)
            if (a.GetCompID((eGearComponent)c) != b.GetCompID((eGearComponent)c)) return false;
        return true;
    }

    // Reverts and drops a skinned holder whose parts spawn again; true when there was one.
    static bool Forget(IntPtr holder)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
            if (Live[i]._holderPointer == holder) { Live[i].Revert(); Live.RemoveAt(i); return true; }
        return false;
    }

    void TryApply()
    {
        try { Apply(); }
        catch (Exception e)
        {
            Revert();
            ForgeSkinPlugin.Fail($"{_label}: {e.Message}; left vanilla");
        }
    }

    // ---- apply / revert ----

    void Apply()
    {
        var key = _holder != null ? SkinTarget.GearKey(GearId(_holder)) : SkinTarget.ItemKey(_itemId);
        var overrides = new Dictionary<string, (Skin Skin, MaterialOverride Override)>();
        foreach (var id in Skins.Globals)
            if (Skins.ById.TryGetValue(id, out var global))
                foreach (var m in global.Doc.Materials) overrides[m.Material] = (global, m);
        Skin? weapon = null;
        if (Skins.Targets.TryGetValue(key, out var weaponId))
        {
            if (!Skins.ById.TryGetValue(weaponId, out weapon)) throw new InvalidOperationException($"active skin {weaponId} is not installed");
            if (weapon.Doc.Target.Key() != key)
                throw new InvalidOperationException($"skin {weaponId} targets {weapon.Doc.Target.Key()}, not {key}");
            foreach (var m in weapon.Doc.Materials) overrides[m.Material] = (weapon, m);
        }
        if (overrides.Count == 0 && (weapon == null || weapon.Doc.Attachments.Count == 0 && weapon.Doc.Parts.Count == 0 && weapon.Doc.Sounds.Count == 0))
        {
            ForgeSkinPlugin.Info($"{_label}: skipped, no active skin for {key}");
            return;
        }

        ApplyMaterials(overrides);
        int attachments = 0;
        if (weapon != null)
            foreach (var a in weapon.Doc.Attachments)
                if (a.Visible) { Attach(weapon, a); attachments++; }
        // First-person gear only: the third-person copy of the same weapon would play every sound a second time.
        int sounds = 0;
        if (weapon != null && _item != null && _item.m_isFirstPerson)
            foreach (var s in weapon.Doc.Sounds) { On(s.Trigger, new SoundCue(weapon.Sound(s.File), s.Volume), s.LowAmmoFraction); sounds++; }
        // After the attachments, so hiding a part also hides what is mounted under it. Other parts mounted under it
        // (a sight on the receiver's sight align) are theirs to hide.
        int hidden = 0;
        var partRoots = new HashSet<IntPtr>();
        foreach (var (id, _) in Parts)
            if (PartObject(id) is { } root) partRoots.Add(root.transform.Pointer);
        if (weapon != null)
            foreach (var p in weapon.Doc.Parts)
            {
                var part = PartObject(p.Part) ?? throw new InvalidOperationException($"part layout: gear has no part {p.Part}");
                var t = p.Path == null ? part.transform : part.transform.Find(p.Path)
                    ?? throw new InvalidOperationException($"part layout: no {p.Path} under {p.Part}");
                var pose = new PartPose(t, p, partRoots, p.Mesh == null ? null : weapon.Mesh(p.Mesh));
                _poses.Add(pose);
                ForgeSkinPlugin.Info($"{_label}: pose {p.Part}/{p.Path ?? "."} written, now {pose.Describe()}");
                if (p.Visible == false) hidden++;
            }
        if (_on[(int)Trigger.AmmoPickup].Count > 0) Storage(out _, out _); // the reserve is polled per frame
        ForgeSkinPlugin.Info($"{_label}: {key}, skin {weapon?.Doc.Id ?? "-"}, {_swaps.Count} renderers, " +
                             $"{attachments} attachments, {_screens.Count} screens, {sounds} sounds, {_layerCount} layers, {_poses.Count} part poses ({hidden} hidden)");
        KeepOwn("after creation");
        LogAttachments();
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
        _animated.Clear();
        foreach (var list in _on) list.Clear();
        _lowAmmo.Clear();
        _below.Clear();
        _lastClip = _lastReserve = -1;
        _layerCount = 0;
        _screens.Clear();
        _own.Clear();
        _faceCamera.Clear();
        _roots.Clear();
    }

    void ApplyMaterials(Dictionary<string, (Skin Skin, MaterialOverride Override)> overrides)
    {
        if (overrides.Count == 0) return;
        var looks = new Dictionary<MaterialOverride, Material>();
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        // The whole item, not only the part holder: the item's own setup may move parts (light, sight, rounds) out of it.
        var top = _item != null ? _item.transform : _root.transform;
        foreach (var renderer in top.GetComponentsInChildren<Renderer>(true))
        {
            var original = renderer.sharedMaterials;
            // A catalog name may be the material's or the mesh's (the old tool's parts used both).
            var mesh = BaseName(MeshOf(renderer)?.name ?? "");
            overrides.TryGetValue(mesh, out var byMesh);
            Il2CppReferenceArray<Material>? mine = null;
            for (int i = 0; i < original.Length; i++)
            {
                var source = original[i];
                if (source == null) continue;
                var name = BaseName(source.name);
                seen.Add($"{renderer.name}: mesh '{mesh}' material '{name}'");
                if (!overrides.TryGetValue(name, out var o)) o = byMesh;
                if (o.Override == null) continue;
                if (!looks.TryGetValue(o.Override, out var look))
                {
                    looks[o.Override] = look = Look(o.Skin, o.Override, source);
                    if (!renderer.transform.IsChildOf(_root.transform))
                        ForgeSkinPlugin.Info($"{_label}: material {name} found outside the part holder at {PathOf(renderer.transform)}");
                }
                mine ??= renderer.sharedMaterials;
                mine[i] = look;
            }
            if (mine == null) continue;
            renderer.sharedMaterials = mine;
            _swaps.Add((renderer, original, mine));
        }
        foreach (var (name, o) in overrides)
        {
            // A global skin (mags, sights, lights, ammo) covers every gear; only the ones that use its materials take it.
            if (looks.ContainsKey(o.Override) || o.Skin.Doc.Target.Weapon == null) continue;
            // Failure path only: where else the material is drawn right now, so the log says whether the part lives
            // outside this item or is not spawned yet (an ejected casing exists only after a shot).
            var elsewhere = new List<string>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                foreach (var m in r.sharedMaterials)
                    if (m != null && BaseName(m.name) == name) { elsewhere.Add(PathOf(r.transform)); break; }
                if (elsewhere.Count == 3) break;
            }
            if (elsewhere.Count == 0 && o.Override.Part == "ammo_shell")
            {
                ForgeSkinPlugin.Info($"{_label}: material {name} is on the ejected casing, which the game spawns only when firing; not skinned");
                continue;
            }
            ForgeSkinPlugin.Fail($"{_label}: material {name} matched no renderer; part {o.Override.Part ?? "-"} " +
                $"{(o.Override.Part != null && PartObject(o.Override.Part) is { } part ? $"at {PathOf(part.transform)}" : "not found")}; " +
                $"drawn elsewhere: {(elsewhere.Count == 0 ? "nowhere" : string.Join("; ", elsewhere))}; this gear has {seen.Count} slots: {string.Join("; ", seen)}");
        }
    }

    static string PathOf(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    static Mesh? MeshOf(Renderer renderer) =>
        renderer.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? renderer.GetComponent<MeshFilter>()?.sharedMesh;

    // Unity appends " (Instance)" to runtime copies of materials and meshes, " (Clone)" to instantiated objects.
    static string BaseName(string name)
    {
        while (true)
        {
            if (name.EndsWith(" (Instance)", StringComparison.Ordinal)) name = name[..^11];
            else if (name.EndsWith(" (Clone)", StringComparison.Ordinal)) name = name[..^8];
            else return name;
        }
    }

    Material Look(Skin skin, MaterialOverride o, Material source)
    {
        var look = Own(new Material(source) { name = source.name + " (ForgeSkin)" });
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
        else ForgeSkinPlugin.Fail($"{_label}: material {m.name} has no {what}; ignored");
    }

    void SetFloat(Material m, int property, float value, string what)
    {
        if (m.HasProperty(property)) m.SetFloat(property, value);
        else ForgeSkinPlugin.Fail($"{_label}: material {m.name} has no {what}; ignored");
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
        int x = r[0], y = target.height - r[1] - r[3];
        RenderTexture? regionBase = null;
        if (layer.Trigger != Trigger.Always)
        {
            regionBase = Own(new RenderTexture(r[2], r[3], 0, RenderTextureFormat.ARGB32,
                Skin.Linear(layer.Channel) ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB) { name = layer.Id + " base" });
            regionBase.Create();
            Graphics.CopyTexture(target, 0, 0, x, y, r[2], r[3], regionBase, 0, 0, 0, 0);
        }
        var played = new Layer(atlas, target, regionBase, layer, x, y);
        _animated.Add(played);
        On(layer.Trigger, played, layer.LowAmmoFraction);
        _layerCount++;
        ForgeSkinPlugin.Info($"{_label}: layer {layer.Id} {atlas.width}x{atlas.height} {atlas.format}, {a.FrameCount} frames -> {material.name} {layer.Channel}, {layer.Trigger} {layer.Play}");
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
        var part = PartObject(a.Part) ?? throw new InvalidOperationException($"attachment {a.Id}: gear has no part {a.Part}");
        var anchor = a.Transform == null ? part.transform : part.transform.Find(a.Transform) ?? Find(part.transform, a.Transform)
            ?? throw new InvalidOperationException($"attachment {a.Id}: no transform {a.Transform} under {a.Part}");
        var template = Template(part)
            ?? throw new InvalidOperationException($"attachment {a.Id}: part {a.Part} has no material to match");

        var root = Own(new GameObject("ForgeSkin " + a.Id));
        _roots.Add((root, a.Id, template));
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
        var glowing = new List<Material>();
        if (a.Mesh != null)
        {
            var surface = Surface(skin, a.Material ?? new AttachmentMaterial(), template);
            Child(root, "mesh", skin.Mesh(a.Mesh), surface, 0f);
            glowing.Add(surface);
        }
        if (a.Screen != null)
        {
            var screen = new AmmoScreen(this, skin, a.Screen, template, root);
            _screens.Add(screen);
            glowing.AddRange(screen.Surfaces);
        }
        foreach (var e in a.Effects)
        {
            ITriggered effect;
            if (e.Action == EffectAction.Show) effect = new Show(root.GetComponentsInChildren<Renderer>(true).ToArray(), e);
            else
            {
                // Flash needs an emission map to light; a surface without one glows over its whole area.
                foreach (var m in glowing)
                    if (m.GetTexture(EmissiveMap) == null)
                    {
                        m.SetTexture(EmissiveMap, Texture2D.whiteTexture);
                        SetEmission(m, UnityEngine.Color.black);
                    }
                effect = new Flash(glowing.ToArray(), e);
            }
            _animated.Add(effect);
            On(e.Trigger, effect, 0.2f);
        }
    }

    // The part's gear-shader surface (an enabled renderer with the gear's texture and emission slots), else its first material.
    static Material? Template(GameObject part)
    {
        Material? fallback = null;
        foreach (var r in part.GetComponentsInChildren<Renderer>(true))
        {
            var m = r.sharedMaterial;
            if (m == null) continue;
            fallback ??= m;
            if (r.enabled && m.HasProperty(MainTex) && m.HasProperty(EmissiveColor)) return m;
        }
        return fallback;
    }

    // The game draws held gear on its own layer with a first-person projection, set after the gear is built (wield,
    // backpack moves). Our objects take the same layer and the game's own SetFPSRendering.
    static bool _syncing;

    public static void LayerSet(IntPtr item)
    {
        foreach (var gear in Live)
            if (gear._itemPointer == item) gear.SyncLayers();
    }

    public static void FpsRenderingSet(GameObject go, bool enable)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var t = go.transform;
            foreach (var gear in Live)
            {
                if (gear._root == null || !(gear._root.transform.IsChildOf(t) || t.IsChildOf(gear._root.transform))) continue;
                foreach (var (root, _, _) in gear._roots)
                    if (root != null) PlayerBackpackManager.SetFPSRendering(root, enable);
                gear.KeepOwn("after first-person rendering set", quiet: true);
            }
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"first-person rendering sync: {e.Message}"); }
        finally { _syncing = false; }
    }

    void SyncLayers()
    {
        foreach (var (root, _, _) in _roots)
            if (root != null && root.transform.parent != null) SetLayer(root.transform, root.transform.parent.gameObject.layer);
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    // Wield: re-sync layers, write the part poses again, and on the first one log what the game did to them.
    void Equipped()
    {
        SyncLayers();
        bool first = !_equipped;
        _equipped = true;
        foreach (var pose in _poses)
        {
            if (first) ForgeSkinPlugin.Info($"{_label}: pose at first wield before rewrite {pose.Describe()}");
            pose.Write();
        }
        if (first)
        {
            KeepOwn("at first wield");
            LogAttachments();
        }
    }

    // The game's GearMaterialFeeder gives every renderer under the gear a MaterialPropertyBlock, ours included, and a
    // block overrides the material: it would replace our colours and emission with the feeder's values. Our renderers
    // draw only our own materials, so any block on them is cleared and our material put back (keeping the first-person
    // keyword the game set on whatever it swapped in). One line per screen with the block found and the colours shown.
    void KeepOwn(string when, bool quiet = false)
    {
        int blocks = 0, swapped = 0;
        string found = "";
        foreach (var (r, m) in _own)
        {
            if (r == null) continue;
            if (r.HasPropertyBlock())
            {
                r.GetPropertyBlock(Probe);
                if (blocks++ == 0)
                    found = $" (first: empty {Probe.isEmpty}, _EmissiveColor {Probe.GetColor(EmissiveColor)}, _EnableEmissive {Probe.GetFloat(EnableEmissive)}, " +
                            $"_MainTex {Probe.GetTexture(MainTex)?.name ?? "-"})";
                r.SetPropertyBlock(NoBlock);
            }
            var current = r.sharedMaterial;
            if (current == null || current.Pointer != m.Pointer)
            {
                swapped++;
                if (current != null)
                {
                    if (current.IsKeywordEnabled(PlayerBackpackManager.ENABLE_FPS_RENDERING)) m.EnableKeyword(PlayerBackpackManager.ENABLE_FPS_RENDERING);
                    else m.DisableKeyword(PlayerBackpackManager.ENABLE_FPS_RENDERING);
                }
                r.sharedMaterial = m;
            }
        }
        if (quiet && blocks == 0 && swapped == 0) return;
        foreach (var s in _screens)
            ForgeSkinPlugin.Info($"{_label}: screen {when}: {blocks} property blocks cleared{found}, {swapped} materials put back; {s.Describe()}");
        if (_screens.Count == 0 && (blocks > 0 || swapped > 0))
            ForgeSkinPlugin.Info($"{_label}: attachments {when}: {blocks} property blocks cleared{found}, {swapped} materials put back");
    }

    // Where each attachment really is: after every apply and at the first wield (the pose differs while holstered).
    void LogAttachments()
    {
        var eye = _camera ?? PlayerManager.GetLocalPlayerAgent()?.FPSCamera?.transform;
        foreach (var (root, id, template) in _roots)
        {
            if (root == null) continue;
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            int renderers = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) { bounds.Encapsulate(r.bounds); renderers++; }
            var local = eye != null ? eye.InverseTransformPoint(root.transform.position) : Vector3.zero;
            // > 0: the screen's visible side (local -Z) faces the camera; < 0: the camera sees its culled back.
            float facing = eye != null ? Vector3.Dot(-root.transform.forward, (eye.position - root.transform.position).normalized) : 0;
            ForgeSkinPlugin.Info($"{_label}: attachment {id} at camera-local {ForgeSkinPlugin.Vec(local)}, facing camera {facing.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}, bounds size {ForgeSkinPlugin.Vec(bounds.size)}, " +
                $"{renderers} renderers, layer {root.layer} (anchor {root.transform.parent?.gameObject.layer}), active {root.activeInHierarchy}, " +
                $"scale {ForgeSkinPlugin.Vec(root.transform.lossyScale)}, template {template.name} / {template.shader.name}, fps {template.IsKeywordEnabled("ENABLE_FPS_RENDERING")}");
        }
    }

    void On(Trigger trigger, ITriggered handler, float lowAmmoFraction)
    {
        if (trigger == Trigger.Always) return;
        if (trigger != Trigger.LowAmmo) { _on[(int)trigger].Add(handler); return; }
        _lowAmmo.Add((handler, lowAmmoFraction));
        _below.Add(true); // a gear that starts empty (still loading) is not a low-ammo event; the next drop below is
    }

    // ---- events (main thread, from the hooks) ----

    public static void Event(IntPtr item, Trigger trigger)
    {
        try
        {
            float time = Time.time;
            for (int i = 0; i < Live.Count; i++)
            {
                var gear = Live[i];
                if (gear._itemPointer != item) continue;
                // An item without holder gets Equip and Unequip from the wielded-item poll only.
                if (gear._holder == null && trigger is Trigger.Equip or Trigger.Unequip) continue;
                if (trigger == Trigger.Kill) gear.Kills++;
                if (trigger == Trigger.Equip) gear.Equipped();
                if (trigger == Trigger.Unequip)
                {
                    if (gear._unequipFrame == Time.frameCount) continue;
                    gear._unequipFrame = Time.frameCount;
                }
                gear.Raise(trigger, gear._on[(int)trigger], time);
            }
        }
        catch (Exception e) { ForgeSkinPlugin.Fail($"event {trigger}: {e.Message}"); }
    }

    // Damage the local player dealt to a living enemy: a kill when it died, else a hit, for the item in their hands
    // (gun or melee weapon alike).
    public static void Damaged(Agents.Agent? source, bool killed)
    {
        if (source == null) return;
        var local = PlayerManager.GetLocalPlayerAgent();
        if (local == null || local.Pointer != source.Pointer) return;
        var wielded = local.Inventory?.WieldedItem;
        if (wielded != null) Event(wielded.Pointer, killed ? Trigger.Kill : Trigger.Hit);
    }

    // The item's attached flashlight was switched; only a change is an event.
    public static void Flashlight(IntPtr item, bool on)
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var gear = Live[i];
            if (gear._itemPointer != item || gear._flashlight == on) continue;
            gear._flashlight = on;
            Event(item, on ? Trigger.FlashlightOn : Trigger.FlashlightOff);
            return;
        }
    }

    void Raise(Trigger trigger, List<ITriggered> handlers, float time)
    {
        if (!_logged[(int)trigger])
        {
            _logged[(int)trigger] = true;
            ForgeSkinPlugin.Info($"{_label}: event {trigger} -> {handlers.Count} effects");
        }
        for (int i = 0; i < handlers.Count; i++) handlers[i].Start(time);
    }

    // Catalog part ids -> the holder's spawned parts.
    public static readonly (string Id, Func<GearPartHolder, GameObject> Get)[] Parts =
    {
        ("front", h => h.FrontPart), ("front_attachment_a", h => h.FrontPartAttachmentA), ("front_attachment_b", h => h.FrontPartAttachmentB),
        ("receiver", h => h.ReceiverPart), ("receiver_attachment", h => h.ReceiverPartAttachment), ("stock", h => h.StockPart),
        ("sight", h => h.SightPart), ("mag", h => h.MagPart), ("flashlight", h => h.FlashlightPart),
        ("tool_main", h => h.ToolMainPart), ("tool_main_attachment", h => h.ToolMainPartAttachment), ("tool_grip", h => h.ToolGripPart),
        ("tool_delivery", h => h.ToolDeliveryPart), ("tool_delivery_attachment", h => h.ToolDeliveryPartAttachment),
        ("tool_payload", h => h.ToolPayloadPart), ("tool_targeting", h => h.ToolTargetingPart), ("tool_screen", h => h.ToolScreenPart),
        ("melee_head", h => h.MeleeHeadPart), ("melee_neck", h => h.MeleeNeckPart), ("melee_handle", h => h.MeleeHandlePart),
        ("melee_pommel", h => h.MeleePommelPart),
    };

    GameObject? PartObject(string id) => _holder != null ? Part(_holder, id) : id == "body" ? _root.gameObject : null;

    static GameObject? Part(GearPartHolder holder, string id)
    {
        foreach (var (partId, get) in Parts)
            if (partId == id) return get(holder);
        return null;
    }

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
        var m = Own(new Material(template) { name = "ForgeSkin attachment" });
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

    public MeshFilter Child(GameObject parent, string name, Mesh? mesh, Material material, float z)
    {
        var go = new GameObject(name) { layer = parent.layer };
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = new Vector3(0, 0, z);
        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _own.Add((renderer, material));
        return filter;
    }

    // Null for the menu preview, which has no ammo.
    public PlayerAmmoStorage? Storage(out AmmoType type, out int clipSize)
    {
        type = default;
        clipSize = 0;
        if (_item == null) return null;
        _storage ??= PlayerBackpackManager.LocalBackpack?.AmmoStorage ?? throw new InvalidOperationException("local player has no ammo storage");
        _ammoType = _item.AmmoType;
        _clipSize = _item.ClipSize;
        type = _ammoType;
        clipSize = _clipSize;
        return _storage;
    }

    // ---- per frame: no allocation, no lookups ----

    void Update(float time)
    {
        if (_item != null && (_screens.Count > 0 || _lowAmmo.Count > 0 || _on[(int)Trigger.Empty].Count > 0))
        {
            int clip = _item.GetCurrentClip();
            if (clip == 0 && _lastClip > 0) Raise(Trigger.Empty, _on[(int)Trigger.Empty], time);
            _lastClip = clip;
            for (int i = 0; i < _screens.Count; i++) _screens[i].Update(clip, Kills);
            for (int i = 0; i < _lowAmmo.Count; i++)
            {
                bool below = _clipSize > 0 && clip < _clipSize * _lowAmmo[i].Fraction;
                if (below && !_below[i])
                {
                    if (!_logged[(int)Trigger.LowAmmo])
                    {
                        _logged[(int)Trigger.LowAmmo] = true;
                        ForgeSkinPlugin.Info($"{_label}: event LowAmmo -> {_lowAmmo.Count} effects");
                    }
                    _lowAmmo[i].Handler.Start(time);
                }
                _below[i] = below;
            }
        }
        if (_storage != null && _on[(int)Trigger.AmmoPickup].Count > 0)
        {
            int reserve = _storage.GetBulletsInPack(_ammoType);
            if (_lastReserve >= 0 && reserve > _lastReserve) Raise(Trigger.AmmoPickup, _on[(int)Trigger.AmmoPickup], time);
            _lastReserve = reserve;
        }
        if (_item != null && _item.m_isFirstPerson && _on[(int)Trigger.AimEnd].Count > 0)
        {
            var hands = _item.FPItemHolder;
            bool aiming = hands != null && hands.ItemAimTrigger && hands.WieldedItem != null && hands.WieldedItem.Pointer == _itemPointer;
            if (_aiming && !aiming) Raise(Trigger.AimEnd, _on[(int)Trigger.AimEnd], time);
            _aiming = aiming;
        }
        for (int i = 0; i < _animated.Count; i++) _animated[i].Update(time);
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

// One part-layout entry: absolute local values written once on the assembled gear; what they replaced is kept for revert.
sealed class PartPose
{
    readonly Transform _t;
    readonly PartTransform _p;
    readonly Vector3 _position, _scale;
    readonly Quaternion _rotation;
    readonly Renderer[] _hidden;
    readonly HashSet<IntPtr> _partRoots;
    readonly MeshFilter? _filter;
    readonly Mesh? _mesh, _vanillaMesh;

    public PartPose(Transform t, PartTransform p, HashSet<IntPtr> partRoots, Mesh? mesh)
    {
        _t = t;
        _p = p;
        _partRoots = partRoots;
        if (mesh != null)
        {
            _filter = t.GetComponent<MeshFilter>();
            if (_filter == null || _filter.sharedMesh == null)
                throw new InvalidOperationException($"{p.Part}/{p.Path}: no model of its own to replace" + (t.GetComponent<SkinnedMeshRenderer>() != null ? " (skinned meshes are not supported)" : ""));
            (_mesh, _vanillaMesh) = (mesh, _filter.sharedMesh);
        }
        _position = t.localPosition;
        _rotation = t.localRotation;
        _scale = t.localScale;
        _hidden = p.Visible == false ? t.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && !UnderOtherPart(r.transform)).ToArray() : Array.Empty<Renderer>();
        Write();
    }

    bool UnderOtherPart(Transform r)
    {
        for (var x = r; x != null && x.Pointer != _t.Pointer; x = x.parent)
            if (_partRoots.Contains(x.Pointer)) return true;
        return false;
    }

    public void Write()
    {
        if (_t == null) return;
        if (_p.Position is { } pos) _t.localPosition = new Vector3(pos[0], pos[1], pos[2]);
        if (_p.Rotation is { } rot) _t.localEulerAngles = new Vector3(rot[0], rot[1], rot[2]);
        if (_p.Scale is { } scale) _t.localScale = new Vector3(scale[0], scale[1], scale[2]);
        foreach (var r in _hidden)
            if (r != null) r.enabled = false;
        if (_filter != null) _filter.sharedMesh = _mesh;
    }

    public string Describe()
    {
        if (_t == null) return "gone";
        var animator = _t.GetComponentInParent<Animator>();
        return $"local position {ForgeSkinPlugin.Vec(_t.localPosition)} euler {ForgeSkinPlugin.Vec(_t.localEulerAngles)} scale {ForgeSkinPlugin.Vec(_t.localScale)}" +
               (animator != null ? $", under animator {animator.name}" : ", no animator");
    }

    public void Revert()
    {
        foreach (var r in _hidden)
            if (r != null) r.enabled = true;
        if (_filter != null) _filter.sharedMesh = _vanillaMesh;
        if (_t == null) return;
        _t.localPosition = _position;
        _t.localRotation = _rotation;
        _t.localScale = _scale;
    }
}
