using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using ForgeAudio;
using ForgeSkin.Format;
using ForgeVisual;
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
    readonly IntPtr _holderPointer;
    readonly string _label;
    public string Label => _label;
    // Renderers showing our materials: the game's with their originals, and ours (attachments, screens) with no original.
    readonly List<(Renderer Renderer, Il2CppReferenceArray<Material>? Original, Il2CppReferenceArray<Material> Mine)> _swaps = new();
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
    // This level (reset by StateChanged): kills, damage dealt to enemies, and the gun's shots resolved / that hit.
    public int Kills, Shots, ShotHits;
    public float Damage;
    readonly bool _gun; // a ranged gun: its Fire is a shot that hits or misses
    int _shotFrame = -1, _hitFrame = -1; // frame of the unresolved shot (-1: none) and of the last Hit/Kill
    readonly List<AmmoScreen> _screens = new();
    readonly List<Transform> _faceCamera = new();
    readonly List<PartPose> _poses = new();
    readonly List<(GameObject Root, string Id, Material Template)> _roots = new(); // attachment roots
    bool _equipped; // first wield seen: diagnostics logged
    bool _flashlight, _aiming; // edges for FlashlightOn/Off and AimEnd
    int _lastClip = -1, _lastReserve = -1; // edges for Empty and AmmoPickup; -1 until the first poll
    int _unequipFrame = -1; // a base and an override unwield in the same frame are one Unequip
    Transform? _camera;
    FirstPersonItemHolder? _hands; // the 1P item's, kept for the AimEnd poll
    static readonly IntPtr HandsWielded = Native.FieldInfo<FirstPersonItemHolder>(nameof(FirstPersonItemHolder.WieldedItem));
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
        _gun = item != null && ForgeCore.NativeObjects.Is<BulletWeapon>(item);
        for (int i = 0; i < _on.Length; i++) _on[i] = new List<ITriggered>();
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
            if (item == null && Readback.Directory != null) Readback.Write(holder, GearId(holder));
            if (item != null) Consider(holder, item);
            // A menu-mode holder (lobby loadout and gear pages) only ever shows this machine's own gear.
            else if (holder.MenuMode) { Note(holder, $"{holder.GearIDRange?.PublicGearName} [menu]: considered"); Track(holder, null); }
            else if (Icons.IsJob(holder)) { Note(holder, $"{holder.GearIDRange?.PublicGearName} [icon]: skinned for its icon"); Track(holder, null); }
            // Icon renders, and items that only take their holder at OnGearSpawnComplete.
            else Note(holder, $"{holder.GearIDRange?.PublicGearName}: parts spawned without an item and not in menu mode; waiting for the item");
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-gear-spawn-failed", $"gear spawn: {e.Message}", e); }
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
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-readback-failed", $"readback: {e.Message}", e); }
    }

    public static void ItemReady(ItemEquippable item)
    {
        try
        {
            var holder = item.GearPartHolder;
            if (holder != null) Consider(holder, item);
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-gear-spawn-failed", $"gear spawn complete: {e.Message}", e); }
    }

    static void Consider(GearPartHolder holder, ItemEquippable item)
    {
        var pointer = holder.Pointer;
        foreach (var p in Pending)
            if (p.Holder.Pointer == pointer) return;
        foreach (var g in Live)
            if (g._holderPointer == pointer) return;
        // The icon renderer's gear is skinned now: it is rendered before the pending list is looked at again.
        if (Icons.IsJob(holder))
        {
            Note(holder, $"{item.PublicName} [icon]: skinned for its icon");
            Track(holder, item);
            return;
        }
        // The gear id is looked up when the skin is applied (and logged with it), not here on the game's spawn path.
        Note(holder, $"{item.PublicName} [{(item.m_isFirstPerson ? "1P" : "3P")}]: considered");
        Pending.Add((holder, item, Time.unscaledTime));
    }

    // One line per gear and decision.
    static void Note(GearPartHolder holder, string message)
    {
        if (Reported.Add(holder.Pointer)) ForgeSkinPlugin.Info(message);
    }

    public static void Tick(float now, float time)
    {
        if (Skins.Poll(now))
        {
            Icons.SkinsChanged();
            Picker.Changed();
            foreach (var gear in Live)
            {
                gear.Revert();
                if (gear._root != null) gear.TryApply();
            }
        }
        if (Pending.Count > 0 && now >= _nextPending) ResolvePending(now);
        Icons.Tick();
        Wear.Save(now);
        PollWielded(time);
        PollPlayer();
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var gear = Live[i];
            if (gear._root == null) { gear.Revert(); Live.RemoveAt(i); continue; }
            try { gear.Update(time); }
            catch (Exception e)
            {
                gear.Revert();
                ForgeSkinPlugin.Fail("skin-gear-update-failed", $"{gear._label}: update failed: {e.Message}; left vanilla", e);
            }
        }
    }

    static float _levelStart = -1; // unscaled time the current level reached InLevel; -1 outside a level

    // GameStateHook: a new level starts generating (everything of the last one is gone, and
    // pointers may be reused) or the level is entered.
    public static void StateChanged(eGameStateName state)
    {
        _levelStart = state == eGameStateName.InLevel ? Time.unscaledTime : -1;
        if (state != eGameStateName.Generating) return;
        Wear.Save(Time.unscaledTime, true);
        _wielded = _lastItem = IntPtr.Zero;
        _inventory = null;
        _local = IntPtr.Zero;
        _playerDamage = null;
        Reported.Clear();
        DamageAll = DamageTaken = 0;
        foreach (var gear in Live)
        {
            gear.Kills = gear.Shots = gear.ShotHits = 0;
            gear.Damage = 0;
            gear._shotFrame = -1;
        }
    }

    // The local player this level: damage dealt with all their weapons and damage taken; health and infection in percent.
    public static float DamageAll, DamageTaken, Health, Infection;
    static Dam_PlayerDamageBase? _playerDamage; // the local player's, kept while it lives
    static float _lastHealth;
    static readonly IntPtr HealthField = Native.FieldInfo<Dam_SyncedDamageBase>("<Health>k__BackingField"),
        HealthMaxField = Native.FieldInfo<Dam_SyncedDamageBase>("<HealthMax>k__BackingField");

    // Health is read from its field each frame (the getter would box): a drop is damage taken; a rise (heal, revive) is not.
    static void PollPlayer()
    {
        if (_playerDamage == null) return;
        float health = Native.Float(_playerDamage, HealthField), max = Native.Float(_playerDamage, HealthMaxField);
        if (_lastHealth >= 0 && health < _lastHealth) DamageTaken += _lastHealth - health;
        _lastHealth = health;
        Health = max > 0 ? health / max * 100f : 0f;
        Infection = _playerDamage.Infection * 100f;
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
                    // Icon renders and lobby previews are built as third-person items that never get an owner. Gear built
                    // while the level loads waits until 5 s into the level (at most two minutes outside one).
                    if (_levelStart < 0 ? now - since < 120f : now - Math.Max(since, _levelStart) < 5f) continue;
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

    static IntPtr _wielded, _lastItem; // the item in hand now, and the last one held (going down stows it)
    static readonly InventorySlot[] ItemSlots = { InventorySlot.ResourcePack, InventorySlot.Consumable, InventorySlot.InLevelCarry, InventorySlot.HackingTool };

    // Items without a gear holder have no spawn point of their own: the local player's wielded item is compared each
    // frame (one pointer), so taking one out tracks it and is its Equip, and putting it away (dropping or placing a
    // carry item) is its Unequip. The same first-person object can later hold another item; that is a new target.
    static PlayerInventoryBase? _inventory; // the local player's, kept while it lives
    static IntPtr _local; // the local player agent the inventory belongs to
    static readonly IntPtr WieldedGetter = Native.Method(AccessTools.PropertyGetter(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.WieldedItem)));

    static void PollWielded(float time)
    {
        if (_inventory == null && PlayerManager.GetLocalPlayerAgent() is { } local)
        {
            _inventory = local.Inventory;
            _local = local.Pointer;
            _playerDamage = local.Damage;
            _lastHealth = -1; // the first read is not a drop
        }
        // The pointer only: the property would allocate a wrapper every frame.
        var pointer = _inventory == null ? IntPtr.Zero : Native.Call(_inventory, WieldedGetter);
        if (pointer == _wielded) return;
        try
        {
            var item = pointer == IntPtr.Zero ? null : new ItemEquippable(pointer);
            var previous = _wielded;
            _wielded = pointer;
            if (pointer != IntPtr.Zero) _lastItem = pointer;
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
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-wielded-item-failed", $"wielded item: {e.Message}", e); }
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

    internal static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    const string OfflinePrefix = "OfflineGear_ID_";

    internal static uint? Offline(string? instance) =>
        instance != null && instance.StartsWith(OfflinePrefix, StringComparison.Ordinal) && uint.TryParse(instance.AsSpan(OfflinePrefix.Length), out var id) ? id : null;

    static bool SameComps(GearIDRange a, GearIDRange b)
    {
        for (int c = 1; c <= (int)eGearComponent.PlatformPerkCID; c++)
            if (a.GetCompID((eGearComponent)c) != b.GetCompID((eGearComponent)c)) return false;
        return true;
    }

    // ForgeCore GunMount is about to put another package's model on this gun: its own look goes back first, before the
    // mount samples the parts' rest poses and hides the game's meshes, and Apply leaves it alone from then on.
    public static void Mounting(GearPartHolder holder)
    {
        foreach (var gear in Live)
            if (gear._holderPointer == holder.Pointer)
            {
                gear.Revert();
                ForgeSkinPlugin.Info($"{gear._label}: left vanilla, another package mounts its model");
            }
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
            ForgeSkinPlugin.Fail("skin-apply-failed", $"{_label}: {e.Message}; left vanilla", e);
        }
    }

    // ---- apply / revert ----

    void Apply()
    {
        // A gun whose look another package took over (a model mounted on it, ForgeCore GunMount) is that package's to dress.
        if (ForgeVisual.GunMount.Of(_holder) != null)
        {
            ForgeSkinPlugin.Info($"{_label}: skipped, another package mounted its model");
            return;
        }
        var key = _key = _holder != null ? SkinTarget.GearKey(GearId(_holder)) : SkinTarget.ItemKey(_itemId);
        var overrides = new Dictionary<string, (Skin Skin, MaterialOverride Override)>();
        foreach (var id in Skins.Globals)
            if (Skins.ById.TryGetValue(id, out var global))
                foreach (var m in global.Painted(Skins.ColoursOf(id)).Materials) overrides[m.Material] = (global, m);
        Skin? weapon = null;
        SkinDocument? doc = null; // the weapon skin as the player coloured it
        if (Skins.Targets.TryGetValue(key, out var weaponId))
        {
            if (!Skins.ById.TryGetValue(weaponId, out weapon)) throw new InvalidOperationException($"active skin {weaponId} is not installed");
            if (weapon.Doc.Target.Key() != key)
                throw new InvalidOperationException($"skin {weaponId} targets {weapon.Doc.Target.Key()}, not {key}");
            doc = weapon.Painted(Skins.ColoursOf(weaponId));
            foreach (var m in doc.Materials) overrides[m.Material] = (weapon, m);
        }
        if (overrides.Count == 0 && (doc == null || doc.Attachments.Count == 0 && doc.Parts.Count == 0 && doc.Sounds.Count == 0))
        {
            ForgeSkinPlugin.Info($"{_label}: skipped, no active skin for {key}");
            return;
        }

        ApplyMaterials(overrides);
        SkinCasings(overrides);
        int attachments = 0;
        if (weapon != null)
            foreach (var a in doc!.Attachments)
                if (a.Visible) { Attach(weapon, a); attachments++; }
        // First-person gear only: the third-person copy of the same weapon would play every sound a second time.
        // Sounds on one trigger are a group: each firing plays one of them, picked by weight. For LowAmmo the group's
        // largest fraction sets when it fires, so no sound on that trigger is left out.
        int sounds = 0;
        if (weapon != null && _item != null && _item.m_isFirstPerson)
            foreach (var group in doc!.Sounds.GroupBy(s => s.Trigger))
            {
                var picks = group.Select(s => new SoundPickCue.Pick(weapon.Sound(s.File), new PlayOptions { Gain = Math.Clamp(s.Volume, 0, 1) }, s.Weight)).ToArray();
                On(group.Key, new SoundPickCue(picks), group.Max(s => s.LowAmmoFraction));
                foreach (var s in group)
                {
                    sounds++;
                    ForgeSkinPlugin.Info($"{_label}: sound {s.Id} ({s.Name}) {s.File} -> {s.Trigger}, volume {s.Volume}, weight {s.Weight}");
                }
            }
        // After the attachments, so hiding a part also hides what is mounted under it. Other parts mounted under it
        // (a sight on the receiver's sight align) are theirs to hide.
        int hidden = 0;
        var partRoots = new HashSet<IntPtr>();
        foreach (var (id, _) in Parts)
            if (PartObject(id) is { } root) partRoots.Add(root.transform.Pointer);
        if (weapon != null)
            foreach (var p in doc!.Parts)
            {
                var part = PartObject(p.Part) ?? throw new InvalidOperationException($"part layout: gear has no part {p.Part}");
                var t = p.Path == null ? part.transform : part.transform.Find(p.Path)
                    ?? throw new InvalidOperationException($"part layout: no {p.Path} under {p.Part}");
                var pose = new PartPose(t, p, partRoots, p.Mesh == null ? null : weapon.Mesh(p.Mesh));
                _poses.Add(pose);
                ForgeSkinPlugin.Info($"{_label}: pose {p.Part}/{p.Path ?? "."} written, now {pose.Describe()}");
                if (p.Visible == false) hidden++;
            }
        ForgeSkinPlugin.Info($"{_label}: {key}, skin {weapon?.Doc.Id ?? "-"}, {_swaps.Count} renderers, " +
                             $"{attachments} attachments, {_screens.Count} screens, {sounds} sounds, {_layerCount} layers, {_poses.Count} part poses ({hidden} hidden)");
        foreach (var screen in _screens) ForgeSkinPlugin.Info($"{_label}: screen after creation: {screen.Describe()}");
        LogAttachments();
    }

    // A first-person gun's ejected casings in its skin's materials, as world-projection copies of its looks (a casing
    // flies outside the first-person view).
    void SkinCasings(Dictionary<string, (Skin Skin, MaterialOverride Override)> overrides)
    {
        if (!_gun || _item == null || !_item.m_isFirstPerson || overrides.Count == 0) return;
        var made = new Dictionary<IntPtr, Material>();
        Shells.Use(_item, source =>
        {
            if (!overrides.TryGetValue(BaseName(source.name), out var o)) return null;
            if (made.TryGetValue(source.Pointer, out var done)) return done;
            var basis = _looks.TryGetValue(o.Override, out var look) ? look : Look(o.Skin, o.Override, source);
            var m = Own(new Material(basis) { name = basis.name + " casing" });
            Fps(m, false);
            return made[source.Pointer] = m;
        }, _label);
    }

    void Revert()
    {
        if (_item != null) Shells.Release(_item.Pointer);
        // Last first: of two entries on one node, the first one saw the vanilla pose.
        for (int i = _poses.Count - 1; i >= 0; i--) _poses[i].Revert();
        _poses.Clear();
        foreach (var (renderer, original, mine) in _swaps)
        {
            if (renderer == null || original == null) continue;
            var now = renderer.sharedMaterials;
            for (int i = 0; i < original.Length; i++)
            {
                // A slot we did not take keeps what the game shows on it now (its own first-person copy).
                if (mine[i] == original[i])
                {
                    if (i < now.Length) original[i] = now[i];
                    continue;
                }
                if (original[i] is not null && original[i] == null)
                    ForgeSkinPlugin.Warn($"{_label}: revert {renderer.name}[{i}]: the game's material was destroyed while ours was shown");
                ForgeSkinPlugin.Info($"{_label}: revert {renderer.name}[{i}]: showing {Describe(i < now.Length ? now[i] : null)}, restoring {Describe(original[i])}");
                // The game's first-person switch since went to our material (TakeBack), so the original lacks its keyword:
                // the renderer gets what the game would have given it, a copy of the original with the keyword.
                if (_fps is bool fps && original[i] != null && original[i].IsKeywordEnabled(FpsKeyword) != fps)
                {
                    original[i] = new Material(original[i]) { name = original[i].name };
                    Fps(original[i], fps);
                }
            }
            renderer.sharedMaterials = original;
        }
        foreach (var o in _created)
            if (o != null) Object.Destroy(o);
        _swaps.Clear();
        _created.Clear();
        _looks.Clear();
        _worn.Clear();
        _targets.Clear();
        _animated.Clear();
        foreach (var list in _on) list.Clear();
        _lowAmmo.Clear();
        _below.Clear();
        _lastClip = _lastReserve = -1;
        _layerCount = 0;
        _screens.Clear();
        _faceCamera.Clear();
        _roots.Clear();
    }

    // The skin's look per material override, kept until the revert (the casing pool copies them).
    readonly Dictionary<MaterialOverride, Material> _looks = new();
    // Looks whose livery wears with kills: the counter key ("<skin id>|<gear key>") and the stage they show.
    readonly List<(Skin Skin, MaterialOverride Override, Material Look, string Key, WearStage Shown)> _worn = new();
    string _key = "";

    void ApplyMaterials(Dictionary<string, (Skin Skin, MaterialOverride Override)> overrides)
    {
        if (overrides.Count == 0) return;
        var looks = _looks;
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
                // An empty or destroyed slot is listed too: a part whose material was destroyed under it draws nothing.
                if (source == null) { seen.Add($"{renderer.name}: mesh '{mesh}' slot {i} {Describe(source)}"); continue; }
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
                ForgeSkinPlugin.Info($"{_label}: swap {renderer.name}[{i}] {Describe(source)} -> {Describe(look)}");
            }
            if (mine == null) continue;
            renderer.sharedMaterials = mine;
            _swaps.Add((renderer, original, mine));
        }
        foreach (var (name, o) in overrides)
        {
            // A global skin (mags, sights, lights, ammo) covers every gear; only the ones that use its materials take it.
            if (looks.ContainsKey(o.Override) || o.Skin.Doc.Target.Weapon == null) continue;
            // The ejected casing exists only once fired: its own pool takes the skin (SkinCasings).
            if (o.Override.Part == "ammo_shell") continue;
            // No scene-wide search for where else it is drawn: over every renderer of a level that stalls the game for
            // a third of a second per material, on every hot reload. The gear's own slots say what it has instead.
            ForgeSkinPlugin.Warn($"{_label}: material {name} matched no renderer; part {o.Override.Part ?? "-"} " +
                $"{(o.Override.Part != null && PartObject(o.Override.Part) is { } part ? $"at {PathOf(part.transform)}" : "not found")}; " +
                $"this gear has {seen.Count} slots: {string.Join("; ", seen)}");
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
    // The asset name under Unity's copy suffixes and the game's first-person copies (MakeUnique: "X(Clone)_unique_221",
    // "Flashlight_LensA (Instance)(Clone)_unique_529"). Without the last, a hot reload after the game's switch matched only
    // renderers whose mesh is named like the material (a revolver's drum, a bolt kept the vanilla look).
    static readonly System.Text.RegularExpressions.Regex CopySuffix = new(@"(\s?\((Clone|Instance)\)|_unique_\d+)$");

    static string BaseName(string name)
    {
        for (var m = CopySuffix.Match(name); m.Success; m = CopySuffix.Match(name)) name = name[..m.Index];
        return name;
    }

    const string BlendHS = "GTFO/BlendHS";

    Material Look(Skin skin, MaterialOverride o, Material source)
    {
        // Named after the skin's material only: the game's per-gear copies are "<name>(Clone)_unique_<n>", and ours must
        // not pass for one of them.
        var name = $"ForgeSkin {o.Material}";
        Material look;
        // GTFO/BlendHS colours by vertex and samples no albedo texture, so a picture on it would never show. The skin
        // carries the editor's baked look as its base (actions.ts packDoc); it is drawn with a copy of a textured gear
        // material of this gear, stripped of everything that is the donor's own (textures, colour, emission).
        if (source.shader.name == BlendHS && o.Textures.ContainsKey(TextureChannel.Albedo))
        {
            var template = TexturedTemplate() ?? throw new InvalidOperationException($"{source.name} ({BlendHS}): the gear has no textured material to draw it with");
            look = Own(new Material(template) { name = name });
            look.SetTexture(MainTex, Texture2D.whiteTexture);
            look.SetTexture(BumpMap, null);
            look.SetTexture(MetallicGlossMap, Texture2D.blackTexture);
            look.SetTexture(EmissiveMap, null);
            if (look.HasProperty(Color)) look.SetColor(Color, UnityEngine.Color.white);
            Glow.SetColour(look, UnityEngine.Color.black);
            Glow.Clear(look);
            ForgeSkinPlugin.Info($"{_label}: material {source.name} ({BlendHS}) drawn with {template.shader.name} copied from {template.name}");
        }
        else look = Own(new Material(source) { name = name });
        var textures = o.Textures;
        if (o.Wear is { Count: > 0 })
        {
            var counter = $"{skin.Doc.Id}|{_key}";
            var stage = Wear.Stage(o.Wear, Wear.Count(counter));
            _worn.Add((skin, o, look, counter, stage));
            textures = stage.Textures;
            ForgeSkinPlugin.Info($"{_label}: {o.Material} wears with kills: {Wear.Count(counter)} kills, stage at {stage.Kills}");
        }
        foreach (var (channel, path) in textures) look.SetTexture(Property(channel), skin.Texture(path, Skin.Linear(channel)));
        if (o.Textures.ContainsKey(TextureChannel.Emissive) && o.Params.EmissiveEnabled != false) SetEmissive(look, true);
        Params(look, o.Params);
        // One line per layer: where it went (AddLayer) or why it was left out; one bad layer leaves the others drawn.
        foreach (var layer in o.Layers)
        {
            if (!layer.Visible) { ForgeSkinPlugin.Info($"{_label}: layer {layer.Id} ({layer.Name}) on {source.name}: skipped, hidden"); continue; }
            try { AddLayer(skin, look, layer); }
            catch (Exception e) when (e is InvalidOperationException or SkinFormatException)
            {
                ForgeSkinPlugin.Fail("skin-layer-skipped", $"{_label}: layer {layer.Id} ({layer.Name}) on {source.name}: skipped, {e.Message}", e);
            }
        }
        return look;
    }

    void Params(Material m, MaterialParams p)
    {
        if (p.EmissiveEnabled is bool on) SetEmissive(m, on);
        if (p.EmissiveColor != null || p.EmissiveIntensity != null)
        {
            if (!m.HasProperty(EmissiveColor)) ForgeSkinPlugin.Warn($"{_label}: material {m.name} has no emissive colour; ignored");
            else Glow.SetColour(m, (p.EmissiveColor is { } e ? new Color(e[0], e[1], e[2]) : m.GetColor(EmissiveColor)) * (p.EmissiveIntensity ?? 1f));
        }
        if (p.Metallic is float metal) SetFloat(m, Metallic, metal, "metallic");
        if (p.Smoothness is float smooth) SetFloat(m, m.HasProperty(Glossiness) ? Glossiness : Smoothness, smooth, "smoothness");
        if (p.Tint is { } t) SetColor(m, Color, new Color(t[0], t[1], t[2], t[3]), "tint");
    }

    void SetColor(Material m, int property, Color value, string what)
    {
        if (m.HasProperty(property)) m.SetColor(property, value);
        else ForgeSkinPlugin.Warn($"{_label}: material {m.name} has no {what}; ignored");
    }

    void SetFloat(Material m, int property, float value, string what)
    {
        if (m.HasProperty(property)) m.SetFloat(property, value);
        else ForgeSkinPlugin.Warn($"{_label}: material {m.name} has no {what}; ignored");
    }

    // Emission goes through ForgeCore Glow, the one emission write. On: the material glows in its own colour and emission
    // map; off: its glow is switched off and its colour and map stay.
    public static void SetEmissive(Material m, bool on)
    {
        if (!on) Glow.Clear(m);
        else if (m.HasProperty(EmissiveColor)) Glow.Write(m, m.GetColor(EmissiveColor), m.GetTexture(EmissiveMap));
    }

    public const string EmissiveKeyword = "ENABLE_EMISSIVE";

    // Lit in `color` through its own emission map (white when it has none).
    public static void SetEmission(Material m, Color color)
    {
        if (m.HasProperty(EmissiveColor)) Glow.Write(m, color, m.GetTexture(EmissiveMap));
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
        if (typeof(T) != typeof(GameObject)) ForgeCore.Assets.Keep(o);
        // A material made after the game's first-person switch starts with the projection the gear is drawn with now.
        if (_fps is bool fps && o is Material m) Fps(m, fps);
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
                Skin.Linear(layer.Channel) ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
            { name = layer.Id + " base" });
            regionBase.Create();
            Graphics.CopyTexture(target, 0, 0, x, y, r[2], r[3], regionBase, 0, 0, 0, 0);
        }
        var played = new Layer(atlas, target, regionBase, layer, x, y);
        _animated.Add(played);
        On(layer.Trigger, played, layer.LowAmmoFraction);
        _layerCount++;
        ForgeSkinPlugin.Info($"{_label}: layer {layer.Id} {atlas.width}x{atlas.height} {atlas.format}, {a.FrameCount} frames of {a.FrameWidth}x{a.FrameHeight} -> " +
            $"{material.name} {layer.Channel} {target.width}x{target.height} at [{string.Join(", ", r)}], {layer.Trigger} {layer.Play}");
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
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
        { useMipMap = true, autoGenerateMips = false, name = material.name + " layers" });
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
            var surface = Surface(skin, a.Material ?? new AttachmentMaterial(), template, a.Hologram != null);
            Child(root, "mesh", skin.Mesh(a.Mesh), surface, 0f);
            glowing.Add(surface);
        }
        if (a.Screen != null)
        {
            var screen = new AmmoScreen(this, skin, a.Screen, template, root, a.Hologram != null);
            _screens.Add(screen);
            glowing.AddRange(screen.Surfaces);
        }
        if (a.Hologram is { Flicker: > 0 } holo) _animated.Add(new Flicker(root.GetComponentsInChildren<Renderer>(true).ToArray(), holo.Flicker));
        if (a.Swing != null) _animated.Add(new Pendulum(t, a.Swing.Looseness));
        // The game's hologram shader has no emission colour for Flash to drive.
        glowing.RemoveAll(m => !m.HasProperty(EmissiveColor));
        foreach (var e in a.Effects)
        {
            ITriggered effect;
            if (e.Action == EffectAction.Show) effect = new Show(root.GetComponentsInChildren<Renderer>(true).ToArray(), e);
            else
            {
                // Flash needs an emission map to light; a surface without one glows over its whole area.
                foreach (var m in glowing)
                    if (m.GetTexture(EmissiveMap) == null) SetEmission(m, UnityEngine.Color.black);
                effect = new Flash(glowing.ToArray(), e);
            }
            _animated.Add(effect);
            On(e.Trigger, effect, 0.2f);
        }
    }

    // Instance ids in the log tell the game's copies from ours and show when one was destroyed.
    static string Describe(Material? m) => m is null ? "none" : m == null ? "a destroyed material" : $"{m.name}#{m.GetInstanceID()}";

    // A material of this gear drawn with a textured gear shader (texture and emission slots, not BlendHS).
    Material? TexturedTemplate()
    {
        foreach (var r in (_item != null ? _item.transform : _root.transform).GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader.name != BlendHS && !m.name.StartsWith("ForgeSkin ", StringComparison.Ordinal)
                    && m.HasProperty(MainTex) && m.HasProperty(EmissiveColor)) return m;
        return null;
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
        if (_syncing || Live.Count == 0) return; // ForgeCore's drawing switches objects too; only gear tracked here is looked at
        _syncing = true;
        try
        {
            var t = go.transform;
            foreach (var gear in Live)
            {
                if (gear._root == null || !(gear._root.transform.IsChildOf(t) || t.IsChildOf(gear._root.transform))) continue;
                gear._fps = enable;
                gear.TakeBack(t, enable);
                // Attachments under `go` were just switched with it; the others get the game's switch here.
                foreach (var (root, _, _) in gear._roots)
                    if (root != null && !root.transform.IsChildOf(t))
                    {
                        PlayerBackpackManager.SetFPSRendering(root, enable);
                        gear.TakeBack(root.transform, enable);
                    }
            }
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-fps-sync-failed", $"first-person rendering sync: {e.Message}", e); }
        finally { _syncing = false; }
    }

    // SetFPSRendering (GameAssembly 0x175B6F0) gives every enabled renderer under `go` a fresh copy of its slot-0
    // material (MaterialRef.MakeUnique 0x14FA420: Instantiate, "_unique_<n>", renderer.sharedMaterial = copy) and sets
    // ENABLE_FPS_RENDERING on the copies only. On a renderer we skinned or created that copy is a frozen copy of our look,
    // which no longer follows our animated layers or screen colours and which nobody destroys. Ours goes back with the
    // keyword the game just set, and so does every material of ours under `go` the game skipped (disabled renderers:
    // a Show attachment that is hidden right now).
    // The copy is destroyed: in the interop assemblies the only fields holding a MaterialRef are character bodies'
    // (CharacterMaterialHandler and its MaterialGroups) and MaterialGroup's static temp, which is overwritten on its next
    // use; and the next switch copies whatever the renderer shows then, never the old copy.
    void TakeBack(Transform go, bool enable)
    {
        foreach (var (renderer, original, mine) in _swaps)
        {
            if (renderer == null || !renderer.transform.IsChildOf(go)) continue;
            var now = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mine.Length; i++)
            {
                if (original != null && mine[i] == original[i]) continue;
                Fps(mine[i], enable);
                if (i >= now.Length || now[i] == mine[i]) continue;
                ForgeSkinPlugin.Info($"{_label}: fps rendering {enable}: {renderer.name}[{i}] was given {Describe(now[i])}, ours {Describe(mine[i])} put back");
                if (now[i] != null && now[i].name.StartsWith(mine[i].name, StringComparison.Ordinal)) Object.Destroy(now[i]);
                changed = true;
            }
            if (changed) renderer.sharedMaterials = mine;
        }
    }

    const string FpsKeyword = "ENABLE_FPS_RENDERING";
    bool? _fps; // the last first-person switch the game made on this gear; null before the first

    static void Fps(Material m, bool on)
    {
        if (on) m.EnableKeyword(FpsKeyword);
        else m.DisableKeyword(FpsKeyword);
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
        if (first) LogAttachments();
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

    public static void Event(IntPtr item, Trigger trigger, float damage = 0)
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
                if (trigger == Trigger.Kill)
                {
                    gear.Kills++;
                    if (gear._worn.Count > 0) Worn(gear);
                }
                if (trigger is Trigger.Kill or Trigger.Hit)
                {
                    gear.Damage += damage;
                    gear._hitFrame = Time.frameCount;
                }
                if (trigger == Trigger.Fire && gear._gun)
                {
                    if (gear._shotFrame >= 0) gear.ResolveShot(time);
                    gear._shotFrame = Time.frameCount;
                }
                if (trigger == Trigger.Equip) gear.Equipped();
                if (trigger == Trigger.Unequip)
                {
                    if (gear._unequipFrame == Time.frameCount) continue;
                    gear._unequipFrame = Time.frameCount;
                }
                gear.Raise(trigger, gear._on[(int)trigger], time);
            }
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-event-failed", $"event {trigger}: {e.Message}", e); }
    }

    // A kill with a gear whose livery wears with kills: counted once per skin on it, and every live copy of that skin and
    // gear (first and third person) shows the stage now reached.
    static void Worn(SkinnedGear killer)
    {
        foreach (var key in killer._worn.Select(w => w.Key).Distinct()) Wear.Add(key);
        foreach (var gear in Live)
            for (int i = 0; i < gear._worn.Count; i++)
            {
                var (skin, o, look, key, shown) = gear._worn[i];
                var stage = Wear.Stage(o.Wear!, Wear.Count(key));
                if (stage == shown) continue;
                foreach (var (channel, path) in stage.Textures) look.SetTexture(Property(channel), skin.Texture(path, Skin.Linear(channel)));
                gear._worn[i] = (skin, o, look, key, stage);
                ForgeSkinPlugin.Info($"{gear._label}: {o.Material} worn to the stage at {stage.Kills} kills");
            }
    }

    // Whether a hit's source is the local player, as the wielded-item poll last found them.
    public static bool IsLocal(Agents.Agent? source) => source != null && _local != IntPtr.Zero && source.Pointer == _local;

    // The local player went down or was revived: a state of theirs, not of a gear, so the skin of the item in their hands
    // hears it. Going down stows the item, so the one held last stands in then.
    public static void PlayerState(Trigger trigger)
    {
        var item = _inventory == null ? IntPtr.Zero : Native.Call(_inventory, WieldedGetter);
        if (item == IntPtr.Zero) item = _lastItem;
        if (item != IntPtr.Zero) Event(item, trigger);
    }

    // Damage the local player dealt to a living enemy (the health it lost): a kill when it died, else a hit, for the item
    // in their hands (gun or melee weapon alike).
    public static void Damaged(bool killed, float damage)
    {
        DamageAll += damage;
        var wielded = _inventory == null ? IntPtr.Zero : Native.Call(_inventory, WieldedGetter);
        if (wielded != IntPtr.Zero) Event(wielded, killed ? Trigger.Kill : Trigger.Hit, damage);
    }

    // A gun's shot is decided at the end of the frame after it: hitscan damage of the local shooter is applied in the
    // shot's own frame, so a Hit/Kill from then on is its hit (a shotgun's pellets are one shot); none is a Miss.
    void ResolveShot(float time)
    {
        bool hit = _hitFrame >= _shotFrame;
        _shotFrame = -1;
        Shots++;
        if (hit) ShotHits++;
        else Raise(Trigger.Miss, _on[(int)Trigger.Miss], time);
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
    Material Surface(Skin skin, AttachmentMaterial spec, Material template, bool hologram)
    {
        Texture? Tex(TextureChannel c) => spec.Textures.TryGetValue(c, out var p) ? skin.Texture(p, Skin.Linear(c)) : null;
        var color = new Color(spec.Color[0], spec.Color[1], spec.Color[2], spec.Color[3]);
        if (hologram) return Holograms.Surface(this, template, Tex(TextureChannel.Albedo), color, Holograms.Glow * (spec.Params.EmissiveIntensity ?? 1f));
        var m = Own(new Material(template) { name = "ForgeSkin attachment" });
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
        _swaps.Add((renderer, null, renderer.sharedMaterials));
        return filter;
    }

    // Null for the menu preview, which has no ammo, and while the local player has no backpack yet (callers look again later).
    public PlayerAmmoStorage? Storage(out AmmoType type, out int clipSize)
    {
        type = default;
        clipSize = 0;
        if (_item == null) return null;
        _storage ??= PlayerBackpackManager.LocalBackpack?.AmmoStorage;
        _ammoType = _item.AmmoType;
        _clipSize = _item.ClipSize;
        type = _ammoType;
        clipSize = _clipSize;
        return _storage;
    }

    // ---- per frame: no allocation, no lookups ----

    void Update(float time)
    {
        if (_shotFrame >= 0 && Time.frameCount > _shotFrame + 1) ResolveShot(time);
        if (_item != null && (_screens.Count > 0 || _lowAmmo.Count > 0 || _on[(int)Trigger.Empty].Count > 0))
        {
            int clip = _item.GetCurrentClip();
            if (clip == 0 && _lastClip > 0) Raise(Trigger.Empty, _on[(int)Trigger.Empty], time);
            _lastClip = clip;
            for (int i = 0; i < _screens.Count; i++) _screens[i].Update(clip);
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
        if (_on[(int)Trigger.AmmoPickup].Count > 0 && (_storage ?? Storage(out _, out _)) is { } storage)
        {
            int reserve = storage.GetBulletsInPack(_ammoType);
            if (_lastReserve >= 0 && reserve > _lastReserve) Raise(Trigger.AmmoPickup, _on[(int)Trigger.AmmoPickup], time);
            _lastReserve = reserve;
        }
        if (_item != null && _item.m_isFirstPerson && _on[(int)Trigger.AimEnd].Count > 0)
        {
            if (_hands == null) _hands = _item.FPItemHolder;
            bool aiming = _hands != null && _hands.ItemAimTrigger && Native.Field(_hands, HandsWielded) == _itemPointer;
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
