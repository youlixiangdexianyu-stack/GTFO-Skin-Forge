using System;
using System.Collections.Generic;
using System.IO;
using Enemies;
using Gear;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>Registered models by name (<c>Pack:prefab</c>), dressing the game's own objects in them, and putting rigged models on
/// players: worn on the first-person body, following the third-person pose, holding a copy of the held item, and clips played on the
/// player's own body.</summary>
public static class Models
{
    private static readonly NameTable<ModelHandle> All = new();

    /// <summary>The model <paramref name="name"/>, or null (logged once) when the name is bad, its pack is not registered or the pack
    /// has no such model. Resolve once and keep the handle.</summary>
    public static ModelHandle? Get(string name)
    {
        EffectBundle.Ensure(name);
        if (All.TryGet(name, out var model)) return model;
        if (Missing.First("model", name)) Plugin.Logger.ErrorOnce("visual-has-no-model-model", "ForgeVisual has no model " + name + ": " + Missing.Why(name, Bundles.Known, "model") + ".");
        return null;
    }

    /// <summary>Every registered model name.</summary>
    public static IReadOnlyList<string> Names => All.Names;

    internal static void Index(BundleHandle bundle)
    {
        foreach (var model in bundle.AllModels)
            if (All.Add(model.Name, model) is { } why) Plugin.Logger.ErrorOnce("visual-model-not-added", "ForgeVisual model " + model.Name + " not added: " + why + ".");
    }

    /// <summary>
    /// Dresses the game object <paramref name="native"/> (a gear item, a sentry, a thrown item) in <paramref name="look"/>: the
    /// object's own mesh renderers are hidden (its effects, lights and colliders stay), a copy of the model is put under it on its layer,
    /// and with <see cref="DressOptions.FitLength"/> the model takes the native meshes' length along the object's forward axis and their
    /// place. Null when the model cannot be shown yet. <see cref="Dressed.Undo"/> puts the object back. A gun in the hands goes on through <see cref="GunMount"/>.
    /// </summary>
    public static Dressed? Dress(GameObject native, ModelHandle look, DressOptions options)
    {
        if (native == null || native.WasCollected) throw new ArgumentNullException(nameof(native));
        if (look == null) throw new ArgumentNullException(nameof(look));
        options ??= DressOptions.Default;
        var t = native.transform;
        Bounds nativeBounds = default;
        var measured = options.FitLength && NativeBounds(t, null, out nativeBounds);
        var instance = look.Spawn(t.position, t.rotation * options.Rotation, t);
        if (instance == null) return null;
        instance.name = options.Name ?? "ForgeVisual." + look.Name;
        var scale = 1f;
        var hidden = new List<Renderer>();
        if (options.HideNative) HideRenderers(t, renderer => !IsMesh(renderer) || renderer.transform.IsChildOf(instance.transform), hidden);
        if (measured) scale = FitTo(instance, t, nativeBounds, options.MaxScale);
        if (options.Layer) SetLayer(instance.transform, native.layer);
        return new Dressed(native, instance, hidden, scale);
    }

    /// <summary>
    /// Wears <paramref name="look"/> on the local player's first-person body (<paramref name="agent"/>'s): see <see cref="FirstPersonWear"/>.
    /// The model is spawned, matched to the game's skeleton by <see cref="FirstPersonWearOptions.Bones"/>, cut to its worn bones and drawn
    /// once off screen; nothing on the game's body changes until <see cref="FirstPersonWear.Activate"/>. It costs a fraction of a second:
    /// build it while the player cannot act (an elevator ride, a countdown). Null (logged once) when the model, its mesh, a bind pose or
    /// the first-person body is missing, or the three bones that line the skeletons up are not on the game's body.
    /// </summary>
    public static FirstPersonWear? WearFirstPerson(PlayerAgent agent, ModelHandle look, FirstPersonWearOptions options)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        if (look == null) throw new ArgumentNullException(nameof(look));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return FirstPersonWear.Build(agent, look, options);
    }

    /// <summary>The model side of a pose copy: the bones of the model under <paramref name="root"/> skinned by <paramref name="skin"/>,
    /// in <paramref name="links"/> order (<see cref="PoseLink"/>), read from its bind poses. Read once per model instance. Null (logged
    /// once, with <paramref name="label"/>) when the links are wrong or a bone or its bind pose is missing.</summary>
    public static PoseTarget? ReadPoseTarget(Transform root, SkinnedMeshRenderer skin, IReadOnlyList<PoseLink> links, string label)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        if (skin == null || skin.WasCollected) throw new ArgumentNullException(nameof(skin));
        return PoseTarget.Read(root, skin, links, label);
    }

    /// <summary>Copies the humanoid pose of the body animated by <paramref name="body"/> (for a player, <c>PlayerAgent.AnimatorBody</c>)
    /// onto <paramref name="target"/>: see <see cref="PoseCopy"/>. Bind once per body; call <see cref="PoseCopy.Apply"/> every frame after
    /// the game animated the body (after its limb IK) and <see cref="PoseCopy.Release"/> when done. Each of <paramref name="reach"/>
    /// (may be empty) bends a limb so its end lands where the body's does (<see cref="PoseReach"/>). Null (logged once) when the body is
    /// not a humanoid, lacks a linked bone or its bind pose, or a reach is wrong.</summary>
    public static PoseCopy? CopyPose(Animator body, PoseTarget target, IReadOnlyList<PoseReach> reach, string label)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (reach == null) throw new ArgumentNullException(nameof(reach));
        return PoseCopy.Bind(body, target, reach, label);
    }

    /// <summary>Copies the pose of a body whose bones the caller found itself (an enemy, by the bone paths its content names) onto
    /// <paramref name="target"/>: <paramref name="bones"/>[i] is the body bone of link i and <paramref name="rest"/>[i] its rest pose in
    /// <paramref name="body"/>'s frame (read it from the body's model as the game ships it when the live body's meshes may have been
    /// reshaped). <paramref name="animator"/>, when given, animates the body even while it is not drawn, until <see cref="PoseCopy.Release"/>.
    /// Null (logged once) when a bone is missing or the counts disagree.</summary>
    public static PoseCopy? CopyPose(Transform body, IReadOnlyList<Transform> bones, IReadOnlyList<Matrix4x4> rest, Animator? animator, PoseTarget target, string label)
    {
        if (body == null || body.WasCollected) throw new ArgumentNullException(nameof(body));
        if (target == null) throw new ArgumentNullException(nameof(target));
        return PoseCopy.Bind(body, bones, rest, animator, target, label);
    }

    /// <summary>A copy of whatever <paramref name="agent"/> holds, for another model's hand, drawn on <paramref name="layer"/>: see
    /// <see cref="HeldMirror"/>.</summary>
    public static HeldMirror MirrorHeld(PlayerAgent agent, int layer, string label)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        return new HeldMirror(agent, layer, label);
    }

    /// <summary>A rig for body clips (<see cref="BodyClipRig"/>): <paramref name="animated"/> (under <paramref name="root"/>) is a hidden
    /// model with the game's player bone names and the clips; <paramref name="rootBone"/> is its hips. Null (logged once) when it has no
    /// such bone.</summary>
    public static BodyClipRig? BodyClipRig(Transform root, GameObject animated, string rootBone, string label)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        if (animated == null || animated.WasCollected) throw new ArgumentNullException(nameof(animated));
        return ForgeVisual.BodyClipRig.Create(root, animated, rootBone, label);
    }

    /// <summary>Plays <paramref name="rig"/>'s clips on <paramref name="agent"/>'s own body: see <see cref="BodyClip"/>. Bind once per
    /// player. Null (logged once) when the body has no hips.</summary>
    public static BodyClip? BindBodyClip(PlayerAgent agent, BodyClipRig rig, BodyClipOptions options)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        return BodyClip.Bind(agent, rig, options ?? new BodyClipOptions());
    }

    /// <summary>Plays <see cref="JointClip"/>s on <paramref name="agent"/>'s own third-person bones: see <see cref="JointPlayer"/>. Bind once
    /// per player. The player's IK is off while a clip plays. Null (logged once) when the body lacks humanoid bones.</summary>
    public static JointPlayer? BindJoints(PlayerAgent agent)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        var joints = JointPlayer.Bind(agent.AnimatorBody, bones => IkPause.Of(agent, bones), "player " + agent.Owner?.Lookup, out _);
        if (joints != null) joints.Reports = true;
        return joints;
    }

    /// <summary>Plays <see cref="JointClip"/>s on <paramref name="enemy"/>'s bones: see <see cref="JointPlayer"/>. Bind once per enemy.
    /// Null (logged once) when the body cannot take a clip, with <paramref name="refusal"/> saying why: <c>no-humanoid-body</c> (it has
    /// no humanoid limbs, like the game's squids) or <c>skeleton</c> (its limbs do not stand like the canonical rig). A body resting
    /// turned half round against its root (the game's Birther family) is read turned round and plays facing its root.</summary>
    public static JointPlayer? BindJoints(EnemyAgent enemy, out string? refusal)
    {
        if (enemy == null || enemy.WasCollected) throw new ArgumentNullException(nameof(enemy));
        var joints = JointPlayer.Bind(enemy.Anim, _ => IkPause.None, "enemy " + enemy.GlobalID, out refusal);
        if (joints != null) joints.Reports = true;
        return joints;
    }

    /// <summary>Plays <see cref="JointClip"/>s on the humanoid body animated by <paramref name="body"/>, a body that is no player or enemy agent (a copy of a player's model
    /// made without a <c>PlayerAgent</c>): its bones are the animator's humanoid bones, there is no IK to pause and its clips are not reported to ForgeCore <c>Recording</c>.
    /// Bind once per body. Null (logged once) when the body cannot take a clip, with <paramref name="refusal"/> saying why, as <see cref="BindJoints(EnemyAgent, out string)"/>.</summary>
    public static JointPlayer? BindJoints(Animator body, string label, out string? refusal)
    {
        if (body == null || body.WasCollected) throw new ArgumentNullException(nameof(body));
        return JointPlayer.Bind(body, _ => IkPause.None, label, out refusal);
    }

    /// <summary>Arms in front of the local player's first-person camera that play <see cref="JointClip"/>s: see <see cref="FirstPersonArms"/>.
    /// Bind once per level. Null (logged once) when the player has no arm meshes to copy or they lack the arm bones.</summary>
    public static FirstPersonArms? BindFirstPersonArms(PlayerAgent agent)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        return ForgeVisual.FirstPersonArms.Build(agent);
    }

    /// <summary>Whether <see cref="BindFirstPersonArms"/> can succeed yet: false until the player's third-person model has its arm meshes (not in the lobby or while the level loads).</summary>
    public static bool FirstPersonArmsReady(PlayerAgent agent)
        => agent != null && !agent.WasCollected && ForgeVisual.FirstPersonArms.Ready(agent);

    /// <summary>The local player's third-person body in the view instead of the first-person arms and item, until the returned view is disposed. Throws for another peer's player.</summary>
    public static OwnBodyView ShowOwnBody(PlayerAgent agent, OwnBodyOptions? options = null)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        if (!agent.IsLocallyOwned) throw new ArgumentException("Only the local player's own body can be shown this way.", nameof(agent));
        return new OwnBodyView(agent, options ?? new OwnBodyOptions());
    }

    /// <summary>Takes the item in <paramref name="agent"/>'s third-person hands out of the picture until <see cref="HeldHiding.Dispose"/>; bind
    /// again after a weapon change. The first-person item is <see cref="FirstPersonArms.Play"/>'s.</summary>
    public static HeldHiding HideHeld(PlayerAgent agent)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        var hidden = new List<Renderer>();
        HeldOff.HoldAll(agent.Inventory == null ? null : agent.Inventory.WieldedItem, hidden, meshesOnly: true);
        return new HeldHiding(hidden);
    }

    /// <summary>Plays <see cref="JointClip"/>s on any skeleton under <paramref name="root"/>, its bones named by <paramref name="map"/>. Bind
    /// once per body. Null, with one error saying what does not fit, when the map names a missing bone or the skeleton does not fit the rig
    /// (<see cref="RigMap"/>). The body's skinned meshes give its rest pose, so nothing else needs declaring.</summary>
    public static JointPlayer? BindJoints(Transform root, RigMap map, string label)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        if (map == null) throw new ArgumentNullException(nameof(map));
        return JointPlayer.Bind(root, map, _ => IkPause.None, label);
    }

    /// <summary>The bounds of the mesh renderers under <paramref name="root"/> in its own frame, skipping <paramref name="skip"/> and
    /// everything under it. False when there is no mesh to measure (an item still being assembled).</summary>
    public static bool NativeBounds(Transform root, Transform? skip, out Bounds bounds)
    {
        bounds = default;
        var meshes = 0;
        var toRoot = root.worldToLocalMatrix;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (skip != null && renderer.transform.IsChildOf(skip)) continue;
            if (renderer.TryCast<MeshRenderer>() != null)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Encapsulate(ref bounds, ref meshes, toRoot * renderer.transform.localToWorldMatrix, filter.sharedMesh.bounds);
            }
            else if (renderer.TryCast<SkinnedMeshRenderer>() != null) Encapsulate(ref bounds, ref meshes, toRoot, renderer.bounds);
        }
        return meshes > 0;
    }

    /// <summary>The world bounds of <paramref name="renderers"/> joined, skipping a destroyed one; false (with default bounds) when none is
    /// left. Read from a spawned instance: a prefab asset does not report renderer bounds reliably.</summary>
    public static bool WorldBounds(IList<Renderer> renderers, out Bounds bounds)
    {
        bounds = default;
        var any = false;
        for (var i = 0; i < renderers.Count; i++)
        {
            var renderer = renderers[i];
            if (renderer == null || renderer.WasCollected) continue;
            if (!any) { bounds = renderer.bounds; any = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return any;
    }

    /// <summary>The world bounds of every renderer under the spawned <paramref name="instance"/> (default bounds when it has none).</summary>
    public static Bounds WorldBounds(GameObject instance)
    {
        WorldBounds(instance.GetComponentsInChildren<Renderer>(true), out var bounds);
        return bounds;
    }

    /// <summary>The direct child of <paramref name="model"/> named <paramref name="name"/> that is not a mesh or renderer (a socket; effects
    /// may hang below it), or null. Throws <see cref="InvalidDataException"/> when two qualify.</summary>
    public static Transform? FindSocket(Transform model, string name)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Socket name is required.", nameof(name));
        Transform? found = null;
        for (var i = 0; i < model.childCount; i++)
        {
            var child = model.GetChild(i);
            if (child.name != name || child.GetComponent<MeshFilter>() != null || child.GetComponent<Renderer>() != null) continue;
            if (found != null) throw new InvalidDataException("Model socket is ambiguous: " + name);
            found = child;
        }
        return found;
    }

    /// <summary><see cref="FindSocket"/> that throws <see cref="InvalidDataException"/> when the socket is missing.</summary>
    public static Transform RequireSocket(Transform model, string name) =>
        FindSocket(model, name) ?? throw new InvalidDataException("Model socket missing: " + name);

    /// <summary>The descendant of <paramref name="root"/> (or itself) named <paramref name="name"/>, depth first, or null: a model's
    /// socket (a muzzle, a hand anchor). Search once and keep the transform.</summary>
    public static Transform? Find(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
            if (Find(root.GetChild(i), name) is { } found) return found;
        return null;
    }

    /// <summary>Puts <paramref name="root"/> and everything under it on <paramref name="layer"/> (first or third person views draw by layer).</summary>
    public static void SetLayer(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer);
    }

    /// <summary>True for a mesh or skinned mesh renderer (not a particle, trail or line renderer).</summary>
    public static bool IsMesh(Renderer renderer) => renderer.TryCast<MeshRenderer>() != null || renderer.TryCast<SkinnedMeshRenderer>() != null;

    /// <summary>Stops drawing every renderer under <paramref name="root"/> (inactive ones too) except those <paramref name="keep"/>
    /// names, and adds each one it turned off to <paramref name="hidden"/>; returns how many that was. Only <c>forceRenderingOff</c> is
    /// written, never <c>enabled</c>: the game switches <c>enabled</c> by itself (a gun's magazine at the end of a reload, culling, placing) and
    /// never writes the force flag, so a hidden renderer stays hidden and no script that reads <c>enabled</c> changes behaviour. A renderer
    /// already forced off is left alone (not added, so a second call returns only the ones that appeared since). To draw only meshes,
    /// have <paramref name="keep"/> return true for what <see cref="IsMesh"/> refuses. Give them back with <see cref="ShowRenderers"/>.</summary>
    public static int HideRenderers(Transform root, Func<Renderer, bool>? keep, List<Renderer> hidden)
    {
        var count = 0;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || HeldOff.IsOff(renderer) || keep != null && keep(renderer)) continue;
            HeldOff.SetUnder(renderer, true);
            hidden.Add(renderer);
            count++;
        }
        if (count > 0 && ForgeCore.Recording.Watched) ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Hidden, 0, null, root, hidden));
        return count;
    }

    /// <summary>Tells the renderers of a sentry that are its effects, not its body: the scanner fan, the muzzle flash and the muzzle and shell-eject
    /// aligns (what the game attaches there). Reads the sentry's parts when called; call it once per sentry.</summary>
    public static Func<Renderer, bool> KeepsSentryEffects(SentryGunInstance sentry)
    {
        var firing = sentry.m_firingComp?.TryCast<SentryGunInstance_Firing_Bullets>();
        var scanner = sentry.m_visualsComp?.TryCast<SentryGunInstance_ScannerVisuals_Plane>()?.m_scannerPlane;
        return renderer =>
        {
            var t = renderer.transform;
            return scanner != null && t.IsChildOf(scanner.transform)
                || firing != null && (firing.m_muzzleFlash != null && t.IsChildOf(firing.m_muzzleFlash.transform) || firing.MuzzleAlign != null && t.IsChildOf(firing.MuzzleAlign)
                    || firing.ShellEjectAlign != null && t.IsChildOf(firing.ShellEjectAlign));
        };
    }

    /// <summary>Stops drawing the body meshes (base, gun, screen) of a sentry, as <see cref="HideRenderers"/> does; its non-mesh renderers, its
    /// <see cref="KeepsSentryEffects"/> and whatever <paramref name="alsoKeep"/> names (a look the caller put on it) stay. Returns how many
    /// renderers were turned off.</summary>
    public static int HideSentryBody(SentryGunInstance sentry, List<Renderer> hidden, Func<Renderer, bool>? alsoKeep = null)
    {
        var effects = KeepsSentryEffects(sentry);
        return HideRenderers(sentry.transform, renderer => !IsMesh(renderer) || effects(renderer) || alsoKeep != null && alsoKeep(renderer), hidden);
    }

    /// <summary>Draws again the renderers <see cref="HideRenderers"/> turned off (one the game destroyed since is skipped) and empties the list.</summary>
    public static void ShowRenderers(List<Renderer> hidden)
    {
        if (hidden.Count > 0 && ForgeCore.Recording.Watched) ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Shown, 0, null, null, hidden));
        foreach (var renderer in hidden)
            if (renderer != null && !renderer.WasCollected) HeldOff.SetUnder(renderer, false);
        hidden.Clear();
    }

    /// <summary>Scales the look uniformly in the world to the native meshes' length when that is longer (never below the model's
    /// own size, at most <paramref name="maxScale"/>), cancelling a parent's uneven axis scale, and centres it on them across and up,
    /// starting where they start along the forward axis. Returns the scale used.</summary>
    private static float FitTo(GameObject look, Transform native, Bounds nativeBounds, float maxScale)
    {
        if (nativeBounds.size.z <= 1e-4f) return 1f;
        var t = look.transform;
        var lossy = native.lossyScale;
        var unit = new Vector3(SafeInverse(lossy.x), SafeInverse(lossy.y), SafeInverse(lossy.z));
        if (!NativeBounds(t, null, out var own) || own.size.z <= 1e-4f) return 1f;
        var scale = Mathf.Clamp(Math.Max(nativeBounds.size.z * Math.Abs(lossy.z), own.size.z) / own.size.z, 1f, Math.Max(1f, maxScale));
        t.localScale = unit * scale;
        var centre = native.InverseTransformPoint(t.TransformPoint(own.center));
        var rear = centre.z - own.size.z * unit.z * scale * 0.5f;
        t.localPosition += new Vector3(nativeBounds.center.x - centre.x, nativeBounds.center.y - centre.y, nativeBounds.min.z - rear);
        return scale;
    }

    /// <summary>Joins the box that holds <paramref name="box"/> (a mesh's own bounds) under <paramref name="matrix"/> into <paramref name="into"/>;
    /// <paramref name="count"/> is how many boxes it holds so far (0: <paramref name="into"/> is replaced). The one way a mesh is measured under a pose,
    /// for meshes with a renderer and for meshes drawn without one (batched imported models).</summary>
    public static void Encapsulate(ref Bounds into, ref int count, Matrix4x4 matrix, Bounds box)
    {
        // Managed arithmetic on the struct fields: the interop Vector3, Matrix4x4 and Bounds members are calls into the game, and a
        // placed batch measures itself every frame it moves.
        Vector3 c = box.m_Center, e = box.m_Extents;
        float minX, minY, minZ, maxX, maxY, maxZ;
        if (count == 0) { minX = minY = minZ = float.PositiveInfinity; maxX = maxY = maxZ = float.NegativeInfinity; }
        else
        {
            Vector3 ic = into.m_Center, ie = into.m_Extents;
            minX = ic.x - ie.x; minY = ic.y - ie.y; minZ = ic.z - ie.z; maxX = ic.x + ie.x; maxY = ic.y + ie.y; maxZ = ic.z + ie.z;
        }
        for (var i = 0; i < 8; i++)
        {
            float x = c.x + ((i & 1) == 0 ? -e.x : e.x), y = c.y + ((i & 2) == 0 ? -e.y : e.y), z = c.z + ((i & 4) == 0 ? -e.z : e.z);
            var p = ForgeCore.Vec.Point(matrix, x, y, z);
            minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); minZ = Math.Min(minZ, p.z);
            maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); maxZ = Math.Max(maxZ, p.z);
        }
        into.m_Center = ForgeCore.Vec.Of((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        into.m_Extents = ForgeCore.Vec.Of((maxX - minX) * 0.5f, (maxY - minY) * 0.5f, (maxZ - minZ) * 0.5f);
        count++;
    }

    internal static float SafeInverse(float v) => Math.Abs(v) < 1e-4f ? 1f : 1f / v;
}

/// <summary>A registered model. <see cref="Prefab"/> is readable as soon as its pack is registered (to check its parts); spawning
/// waits until its materials are ready (the game's gear shader exists once a level is built).</summary>
public sealed class ModelHandle
{
    internal ModelHandle(BundleHandle bundle, string name, GameObject prefab)
    {
        Bundle = bundle;
        Name = name;
        Prefab = prefab;
        Seen = ForgeCore.RecordingNames.Of(name);
    }

    public BundleHandle Bundle { get; }

    /// <summary><c>Pack:prefab</c>.</summary>
    public string Name { get; }

    /// <summary>The bundle's prefab. Do not change it; spawn instances.</summary>
    public GameObject Prefab { get; }

    /// <summary>True once instances can be spawned with their materials.</summary>
    public bool Ready => Bundle.Prepare();

    /// <summary>A new instance at the given world pose under <paramref name="parent"/> (or none), or null (logged once) when the
    /// materials cannot be prepared yet.</summary>
    public GameObject? Spawn(Vector3 position, Quaternion rotation, Transform? parent = null)
    {
        var instance = Make(position, rotation, parent);
        if (instance != null && ForgeCore.Recording.Watched) ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Spawned, Seen, instance, parent, null));
        return instance;
    }

    /// <summary>A new instance as <see cref="Spawn"/> makes it, not reported to <see cref="ForgeCore.Recording"/>: the library's own pooled effect copies
    /// and the first-person wear, which report what they are themselves.</summary>
    internal GameObject? Make(Vector3 position, Quaternion rotation, Transform? parent = null)
    {
        if (!Bundle.Prepare()) return null;
        return parent == null ? Object.Instantiate(Prefab, position, rotation) : Object.Instantiate(Prefab, position, rotation, parent);
    }

    /// <summary>The name's number for reports to <see cref="ForgeCore.Recording"/>.</summary>
    internal readonly int Seen;
}

/// <summary>How <see cref="Models.Dress"/> puts a model on a game object.</summary>
public sealed class DressOptions
{
    public static readonly DressOptions Default = new();

    /// <summary>Hide the object's own mesh renderers (default true).</summary>
    public bool HideNative { get; set; } = true;

    /// <summary>Take the native meshes' length along the forward axis and their place (gear in the hands).</summary>
    public bool FitLength { get; set; }

    /// <summary>The largest scale <see cref="FitLength"/> may apply (the model is never shrunk below its own size).</summary>
    public float MaxScale { get; set; } = 2f;

    /// <summary>Put the model on the object's layer (default true).</summary>
    public bool Layer { get; set; } = true;

    /// <summary>The model's rotation relative to the object (for example its front turned to +Z).</summary>
    public Quaternion Rotation { get; set; } = Quaternion.identity;

    /// <summary>The name of the model's instance; default <c>ForgeVisual.Pack:prefab</c>.</summary>
    public string? Name { get; set; }

}

/// <summary>
/// The nodes of a game gun that a gun model follows (<see cref="GunMount"/>), read from the gun once it is assembled (the gear part holder's <c>m_aRightHand</c>,
/// <c>m_aLeftHand</c>, <c>m_aMuzzle</c>, <c>m_aMag</c>, or the weapon's own hand grip aligns the game's hands are put on). The model
/// faces the game gun's +Z and is a prefab with these children (any depth): <c>RightHand</c> (the right palm point on the pistol grip; a
/// model without it is held at its origin, the authoring grip), optionally <c>LeftHand</c> (its own palm point, used when present; a model
/// without it gets the game's hold-derived left hand), optionally <c>muzzle</c> (the bore, so the gun is centred on the game's
/// bore) and <c>Magazine</c> (a separate mesh object whose origin is its own centre: the game's reload moves it through the game's magazine
/// node, about that origin).
/// </summary>
public sealed class GunNodes
{
    public const string RightHandSocket = "RightHand";
    public const string LeftHandSocket = "LeftHand";
    public const string MuzzleSocket = "muzzle";
    public const string MagazineSocket = "Magazine";
    public const string SightSocket = "SightLook";
    public const string ShellEjectSocket = "ShellEject";
    public const string FlashlightSocket = "Flashlight";

    /// <summary>The nodes of the game gun held by <paramref name="weapon"/>, read from its part holder (<paramref name="parts"/>) and, where
    /// the holder has none, from the weapon's own aligns (the game's hand getters fall back the same way). Every node is null while the gun
    /// is not assembled.</summary>
    public static GunNodes Of(GearPartHolder? parts, ItemEquippable? weapon) => new()
    {
        RightHand = First(parts?.m_aRightHand, weapon?.m_rightHandGripAlign),
        LeftHand = First(parts?.m_aLeftHand, weapon?.m_leftHandGripAlign),
        Muzzle = First(parts?.m_aMuzzle, weapon?.MuzzleAlign),
        Magazine = parts?.m_aMag,
        Sight = First(parts?.m_aSightLook, weapon?.SightLookAlign),
        ShellEject = First(parts?.m_aShellEject, weapon?.ShellEjectAlign),
        Flashlight = parts?.m_aFlashlight,
    };


    private static Transform? First(Transform? a, Transform? b) => a != null ? a : b;

    public Transform? RightHand { get; set; }
    public Transform? LeftHand { get; set; }

    public Transform? Muzzle { get; set; }
    public Transform? Magazine { get; set; }
    public Transform? ShellEject { get; set; }
    public Transform? Flashlight { get; set; }

    /// <summary>The game's sight-look node. With it and the model's <see cref="GunSockets.Sight"/> the model's height is the one that
    /// puts its sight on the game's (the game's aim corrects height only); without, the two hands share the height error.</summary>
    public Transform? Sight { get; set; }

}

/// <summary>The sockets of a gun model that <see cref="GunMount"/> uses. <see cref="Find"/> reads
/// them by the <see cref="GunNodes"/> socket names; a model whose parts and sockets share names builds this itself.</summary>
public sealed class GunSockets
{
    public Transform RightHand { get; set; } = null!;
    public Transform LeftHand { get; set; } = null!;
    public Transform? Muzzle { get; set; }
    public Transform? Sight { get; set; }
    public Transform? Magazine { get; set; }
    /// <summary>Where shells leave and where the flashlight node sits. Only a grip table's point makes them: a model's part of that name is never taken for one.</summary>
    public Transform? ShellEject { get; set; }
    public Transform? Flashlight { get; set; }

    /// <summary>The sockets of a model whose parts and sockets share names (a mesh part called <c>Muzzle</c> beside the <c>Muzzle</c>
    /// socket), which depth-first <see cref="Find"/> cannot tell apart: each socket is a direct child of <paramref name="model"/> with no
    /// renderer, as the model build writes them. <c>RightHand</c>, the muzzle (named <paramref name="muzzleName"/>) and <c>SightLook</c> must
    /// exist exactly once (<see cref="InvalidDataException"/> otherwise); <c>LeftHand</c> is optional (a model without it gets the rule's
    /// derived left hand) and <c>Magazine</c> is the child part of that name, or null.</summary>
    public static GunSockets FromChildren(Transform model, string muzzleName = GunNodes.MuzzleSocket) => new()
    {
        RightHand = Models.RequireSocket(model, GunNodes.RightHandSocket),
        LeftHand = Models.Find(model, GunNodes.LeftHandSocket)!,
        Muzzle = Models.RequireSocket(model, muzzleName),
        Sight = Models.RequireSocket(model, GunNodes.SightSocket),
        Magazine = model.Find(GunNodes.MagazineSocket),
    };

    /// <summary>The sockets of <paramref name="model"/> by the <see cref="GunNodes"/> names (any depth); a missing <c>RightHand</c> leaves the
    /// model held at its origin and a missing <c>LeftHand</c> is derived by the mount, so neither has to exist.</summary>
    public static GunSockets Find(Transform model) => new()
    {
        RightHand = Models.Find(model, GunNodes.RightHandSocket)!,
        LeftHand = Models.Find(model, GunNodes.LeftHandSocket)!,
        Muzzle = Models.Find(model, GunNodes.MuzzleSocket),
        Sight = Models.Find(model, GunNodes.SightSocket),
        Magazine = Models.Find(model, GunNodes.MagazineSocket),
    };
}

/// <summary>A game object wearing a model.</summary>
public sealed class Dressed
{
    private readonly List<Renderer> _hidden;

    internal Dressed(GameObject native, GameObject look, List<Renderer> hidden, float scale)
    {
        Native = native;
        Look = look;
        _hidden = hidden;
        Scale = scale;
    }

    public GameObject Native { get; }
    public GameObject Look { get; }

    /// <summary>The world scale the dressing applied (1 = the model's own size).</summary>
    public float Scale { get; }

    /// <summary>How many of the object's renderers were hidden.</summary>
    public int Hidden => _hidden.Count;

    /// <summary>True while the object and the model both exist.</summary>
    public bool Alive => Native != null && !Native.WasCollected && Look != null && !Look.WasCollected;

    /// <summary>Hides mesh renderers the object gained since it was dressed (parts the game adds late); returns how many.</summary>
    public int HideLate()
    {
        if (!Alive) return 0;
        var before = _hidden.Count;
        var look = Look.transform;
        Models.HideRenderers(Native.transform, renderer => !Models.IsMesh(renderer) || renderer.transform.IsChildOf(look), _hidden);
        return _hidden.Count - before;
    }

    /// <summary>Removes the model and shows the object's own meshes again.</summary>
    public void Undo()
    {
        Models.ShowRenderers(_hidden);
        if (Look != null && !Look.WasCollected) Object.Destroy(Look);
    }
}

/// <summary>The renderers <see cref="Models.HideHeld"/> holds off.</summary>
public sealed class HeldHiding : IDisposable
{
    private readonly List<Renderer> _hidden;

    internal HeldHiding(List<Renderer> hidden) => _hidden = hidden;

    public void Dispose() => HeldOff.ReleaseAll(_hidden);
}
