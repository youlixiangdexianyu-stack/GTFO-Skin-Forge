using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using RootMotion.FinalIK;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>The parts of the game's first-person body a worn model can stand in for.</summary>
[Flags]
public enum BodyParts { None = 0, Head = 1, Torso = 2, Legs = 4, Arms = 8 }

/// <summary>How <see cref="Models.WearFirstPerson"/> puts a model on the local player's first-person body.</summary>
public sealed class FirstPersonWearOptions
{
    /// <summary>The model's bones and the game bones they sit on (<see cref="WearBone"/>). Must list <c>LeftUpperArm</c>,
    /// <c>RightUpperArm</c> and <c>Hips</c> (worn or only lining up) to match the two skeletons.</summary>
    public IReadOnlyList<WearBone> Bones { get; set; } = Array.Empty<WearBone>();

    /// <summary>The game's body parts held off while the model is worn (default head, torso and legs: the model draws the lower body,
    /// the game's own arms stay).</summary>
    public BodyParts Hide { get; set; } = BodyParts.Head | BodyParts.Torso | BodyParts.Legs;

    /// <summary>Model bones whose skin counts as their parent bone's (parts hinged directly on a worn bone, which stay shut on it).</summary>
    public IReadOnlyCollection<string>? FollowParent { get; set; }

    /// <summary>Names the wearer in error lines (for example <c>Pack:model</c>); default the model's name.</summary>
    public string? Label { get; set; }
}

internal static class BodyConvert
{
    internal static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);
    internal static NQuaternion N(Quaternion q) => new(q.x, q.y, q.z, q.w);
    internal static Quaternion U(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);
    internal static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The rest pose of every bone skinned by the renderers under <paramref name="root"/> (hidden or not), in
    /// <paramref name="frame"/>'s space, from their meshes' bind poses; the first renderer that skins a bone gives it.</summary>
    internal static Dictionary<IntPtr, Matrix4x4> BindRest(Component root, Transform frame)
    {
        var rest = new Dictionary<IntPtr, Matrix4x4>();
        var toFrame = frame.worldToLocalMatrix;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer == null ? null : renderer.sharedMesh;
            if (mesh == null) continue;
            var bones = renderer!.bones;
            var binds = mesh.bindposes;
            if (bones == null || binds == null || binds.Length != bones.Length) continue;
            var fromMesh = toFrame * renderer.transform.localToWorldMatrix;
            for (var b = 0; b < bones.Length; b++)
                if (bones[b] != null && !rest.ContainsKey(bones[b].Pointer)) rest[bones[b].Pointer] = fromMesh * binds[b].inverse;
        }
        return rest;
    }

    /// <summary>Whether <paramref name="agent"/> is this peer's own player (its wielded item is the first-person one).</summary>
    internal static bool IsLocal(PlayerAgent agent)
        => agent.IsLocallyOwned && agent.Owner != null && ForgeCore.Session.LocalLookup != 0 && agent.Owner.Lookup == ForgeCore.Session.LocalLookup;
}

/// <summary>
/// A model worn on the local player's first-person body (<see cref="Models.WearFirstPerson"/>), which the game animates as always
/// (holding, aiming, swinging, reloading, throwing, walking). Nothing animates or offsets anything frame by frame: every worn model bone
/// is parented once onto its game bone with a fixed rotation (both rest poses matched, see <see cref="WearBone"/>), its joint on the
/// game's joint and scaled to the game's lengths, so in every pose the game plays the model's joints coincide with the game's. A copy of
/// the model's mesh keeps only the triangles skinned to the worn bones. The model sits beside the game's own first-person arm models, on
/// their layer and the game's first-person rendering setup (<c>PlayerBackpackManager.SetFPSRendering</c>, which keeps it out of walls),
/// and shows exactly while the game shows its arms.
/// <para>Building (spawning, matching, cutting, drawing once off screen so its GPU work is done) is the expensive part and happens in
/// <see cref="Models.WearFirstPerson"/>, with nothing changed on the game's body; <see cref="Activate"/> and <see cref="Deactivate"/> only
/// hold the chosen game parts off (<c>forceRenderingOff</c>, giving back the value each had) and show or hide the model, creating nothing.
/// Arms resolve through the game's two hand IK solvers (upper arm, forearm, hand), every other bone through the first-person animator's
/// humanoid bones; a bone that does not resolve, or has no bind pose on the game's body, is reported and left out.</para>
/// </summary>
public sealed class FirstPersonWear
{
    private readonly GameObject _root;
    private readonly Transform[] _worn;
    private readonly Transform?[] _on;
    private readonly PlayerFPSBody _body;
    private readonly SkinnedMeshRenderer _renderer;
    private readonly Mesh _mesh;
    private readonly List<Material> _materials = new();
    private readonly List<Renderer> _hidden = new();
    private readonly bool[] _wasOff;
    private readonly GameObject[] _arms;
    private bool _active, _shown, _held;

    private FirstPersonWear(GameObject root, Transform[] worn, Transform?[] on, PlayerFPSBody body, SkinnedMeshRenderer renderer, Mesh mesh, GameObject[] arms, BodyParts hide)
    {
        _root = root;
        _worn = worn;
        _on = on;
        _body = body;
        _renderer = renderer;
        _mesh = mesh;
        _arms = arms;
        foreach (var material in renderer.materials) _materials.Add(material);
        if ((hide & BodyParts.Torso) != 0) Collect(body.m_gfxTorso);
        if ((hide & BodyParts.Legs) != 0) Collect(body.m_gfxLegs);
        if ((hide & BodyParts.Head) != 0) Collect(body.m_gfxHead);
        if ((hide & BodyParts.Arms) != 0) Collect(body.m_gfxArms);
        _wasOff = new bool[_hidden.Count];
    }

    private bool RootAlive => _root != null && !_root.WasCollected;

    /// <summary>The model and the game body it sits on still exist (the game replaces its first-person body now and then: build again).</summary>
    public bool Alive => RootAlive && _body != null && !_body.WasCollected;

    /// <summary>Whether the model is on (<see cref="Activate"/>).</summary>
    public bool Active => _active;

    /// <summary>How many game renderers <see cref="Activate"/> holds off.</summary>
    public int Hidden => _hidden.Count;

    internal static FirstPersonWear? Build(PlayerAgent agent, ModelHandle look, FirstPersonWearOptions options)
    {
        var label = options.Label ?? look.Name;
        var bones = options.Bones;
        if (bones.Count == 0) { Error(label, "lists no bones"); return null; }
        var body = agent.FPItemHolder == null ? null : agent.FPItemHolder.FPSArms;
        if (body == null || body.WasCollected) { Error(label, "the player has no first-person body"); return null; }
        // Not reported to ForgeCore.Recording: only its owner's first-person view draws it.
        var root = look.Make(Vector3.zero, Quaternion.identity);
        if (root == null) return null;
        root.name = "ForgeVisual.Wear." + look.Name;
        root.SetActive(false);
        foreach (var animation in root.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
        var built = (FirstPersonWear?)null;
        try { built = Bind(agent, body, root, bones, options, label); }
        catch (Exception error) { Error(label, "building failed: " + error); }
        if (built == null && root != null && !root.WasCollected) Object.Destroy(root);
        return built;
    }

    private static FirstPersonWear? Bind(PlayerAgent agent, PlayerFPSBody body, GameObject root, IReadOnlyList<WearBone> bones, FirstPersonWearOptions options, string label)
    {
        var n = bones.Count;
        var model = new Transform[n];
        var missing = "";
        for (var i = 0; i < n; i++)
        {
            var found = Models.Find(root.transform, bones[i].Model);
            if (found == null) missing += (missing.Length > 0 ? ", " : "") + bones[i].Model;
            else model[i] = found;
        }
        if (missing.Length > 0) { Error(label, "the model has no bone " + missing); return null; }
        // Each bone's nearest listed ancestor.
        var parents = new int[n];
        for (var i = 0; i < n; i++)
        {
            parents[i] = -1;
            for (var t = model[i].parent; t != null && parents[i] < 0; t = t.parent)
                for (var j = 0; j < n; j++)
                    if (j != i && model[j].Pointer == t.Pointer) { parents[i] = j; break; }
        }
        var on = new Transform?[n];
        var unresolved = "";
        for (var i = 0; i < n; i++)
        {
            on[i] = Resolve(body, bones[i].Body);
            if (on[i] == null) unresolved += (unresolved.Length > 0 ? ", " : "") + bones[i].Body;
        }
        if (unresolved.Length > 0)
            Error(label, "the game's first-person body has no " + unresolved + " bone (its animator " + (body.m_anim != null && body.m_anim.isHuman ? "is humanoid" : "has no humanoid avatar") + "); those parts are left out", fatal: false);

        var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var source = renderer == null ? null : renderer.sharedMesh;
        if (renderer == null || source == null) { Error(label, "the model has no skinned mesh"); return null; }
        if (!source.isReadable) { Error(label, "the model's mesh is not readable (build its bundle with readable meshes)"); return null; }

        // Rest poses: the model's in its root's space, the game's in the first-person body's space.
        var skin = renderer.bones;
        var binds = source.bindposes;
        if (binds == null || binds.Length != skin.Length) { Error(label, "the model has no bind poses for its bones"); return null; }
        var toRoot = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
        var modelRest = new RestPose[n];
        for (var i = 0; i < n; i++)
        {
            var index = -1;
            for (var b = 0; b < skin.Length; b++)
                if (skin[b] != null && skin[b].Pointer == model[i].Pointer) { index = b; break; }
            if (index < 0) { Error(label, "the model's bone " + bones[i].Model + " skins nothing"); return null; }
            var m = toRoot * binds[index].inverse;
            modelRest[i] = new RestPose(BodyConvert.N(m.GetColumn(3)), BodyConvert.N(m.rotation), m.lossyScale.x);
        }
        var gameRest = new RestPose?[n];
        var bindRest = BodyConvert.BindRest(body, body.transform);
        var noPose = "";
        for (var i = 0; i < n; i++)
        {
            if (on[i] == null) continue;
            if (!bindRest.TryGetValue(on[i]!.Pointer, out var m)) { noPose += (noPose.Length > 0 ? ", " : "") + bones[i].Body; on[i] = null; continue; }
            gameRest[i] = new RestPose(BodyConvert.N(m.GetColumn(3)), BodyConvert.N(m.rotation));
        }
        if (noPose.Length > 0) Error(label, "the game's first-person body has no bind pose for " + noPose + "; those parts are left out", fatal: false);
        if (WearSolver.Solve(bones, parents, modelRest, gameRest, out var offset, out var scale) is { } why) { Error(label, why); return null; }

        var wornBone = new bool[n];
        for (var i = 0; i < n; i++) wornBone[i] = on[i] != null && bones[i].Worn;
        var mesh = Cut(source, skin, model, wornBone, options.FollowParent);
        if (mesh == null) { Error(label, "no triangle of the model is skinned to a worn bone on the game's body"); return null; }
        renderer.sharedMesh = mesh;
        renderer.updateWhenOffscreen = true;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // Beside the game's own arm models, on their layer, set up for first-person rendering the way the game sets up that body.
        var arms = new List<GameObject>();
        Renderer? sample = null;
        if (body.m_gfxArms != null)
            foreach (var go in body.m_gfxArms)
            {
                if (go == null) continue;
                arms.Add(go);
                sample ??= go.GetComponentInChildren<Renderer>(true);
            }
        var parent = arms.Count > 0 && arms[0].transform.parent != null ? arms[0].transform.parent
            : body.m_modelRoot != null ? body.m_modelRoot.transform : body.transform;
        root.transform.SetParent(parent, false);
        Models.SetLayer(root.transform, sample != null ? sample.gameObject.layer : body.gameObject.layer);
        PlayerBackpackManager.SetFPSRendering(root, true);

        // Every worn bone onto its game bone: joint on the game's joint, the fixed rotation, the scale.
        var bodyScale = body.transform.lossyScale.x;
        var worn = new List<Transform>();
        var wornOn = new List<Transform?>();
        for (var i = 0; i < n; i++) if (wornBone[i]) model[i].SetParent(on[i], false);
        for (var i = 0; i < n; i++)
        {
            if (!wornBone[i]) continue;
            model[i].localPosition = Vector3.zero;
            model[i].localRotation = BodyConvert.U(offset[i]);
            model[i].localScale = Vector3.one * WearSolver.LocalScale(scale[i], bodyScale, modelRest[i].Scale, on[i]!.lossyScale.x);
            worn.Add(model[i]);
            wornOn.Add(on[i]);
        }
        var wear = new FirstPersonWear(root, worn.ToArray(), wornOn.ToArray(), body, renderer, mesh, arms.ToArray(), options.Hide);
        wear.DrawOnce(label);
        return wear;
    }

    private void Collect(Il2CppReferenceArray<GameObject>? group)
    {
        if (group == null) return;
        foreach (var go in group)
        {
            if (go == null) continue;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) _hidden.Add(renderer);
        }
    }

    /// <summary>Draws the built model once into a small texture with a camera of its own, so its mesh, textures and shader programs
    /// reach the graphics card now and not in the first worn frame. No camera of the game draws it.</summary>
    private void DrawOnce(string label)
    {
        GameObject? holder = null;
        RenderTexture? target = null;
        var wasActive = _root.activeSelf;
        try
        {
            holder = new GameObject("ForgeVisual.WearWarm") { hideFlags = HideFlags.HideAndDontSave };
            var camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << _root.layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.useOcclusionCulling = false;
            camera.allowMSAA = false;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 20f;
            target = RenderTexture.GetTemporary(16, 16, 16);
            camera.targetTexture = target;
            _root.SetActive(true);
            var centre = _renderer.bounds.center;
            holder.transform.position = centre - Vector3.forward * 3f;
            holder.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            camera.Render();
        }
        catch (Exception error) { Error(label, "could not be drawn ahead (the first worn frame pays for it): " + error, fatal: false); }
        finally
        {
            if (RootAlive) _root.SetActive(wasActive);
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (holder != null) Object.Destroy(holder);
        }
    }

    /// <summary>Puts the model on: the chosen game parts stop being drawn and the model shows while the game shows its arms. Creates nothing.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        Hold(true);
    }

    /// <summary>Takes the model off: the game's body is exactly as it was.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _shown = false;
        Hold(false);
        if (RootAlive) _root.SetActive(false);
    }

    /// <summary>Every frame while worn (after the game set its arms' visibility): follows the game's own arm visibility (terminals,
    /// downed, grabs hide the model with the game's arms) and keeps the held-off game parts off should the game switch one back on.</summary>
    public void Follow()
    {
        if (!_active || !RootAlive || _body == null || _body.WasCollected) return;
        for (var i = 0; i < _hidden.Count; i++)
        {
            var renderer = _hidden[i];
            if (renderer != null && !renderer.WasCollected && !renderer.forceRenderingOff) renderer.forceRenderingOff = true;
        }
        var shown = false;
        for (var i = 0; i < _arms.Length; i++)
            if (_arms[i] != null && !_arms[i].WasCollected && _arms[i].activeSelf) { shown = true; break; }
        if (shown == _shown) return;
        _shown = shown;
        _root.SetActive(shown);
    }

    /// <summary>Sets a colour property (for example <c>_EmissiveColor</c>) on the model's own material copies that have it.</summary>
    public void SetColor(string property, Color colour)
    {
        foreach (var material in _materials)
            if (material != null && !material.WasCollected && material.HasProperty(property)) material.SetColor(property, colour);
    }

    private void Hold(bool hide)
    {
        if (_held == hide) return;
        _held = hide;
        for (var i = 0; i < _hidden.Count; i++)
        {
            var renderer = _hidden[i];
            if (renderer == null || renderer.WasCollected) continue;
            if (hide)
            {
                _wasOff[i] = HeldOff.IsOff(renderer);
                HeldOff.SetUnder(renderer, true);
            }
            else HeldOff.SetUnder(renderer, _wasOff[i]);
        }
    }

    /// <summary>Takes the model off and destroys it, its bones (moved onto the game's body), its mesh and material copies.</summary>
    public void Destroy()
    {
        _active = false;
        Hold(false);
        _hidden.Clear();
        for (var i = 0; i < _worn.Length; i++)
        {
            var bone = _worn[i];
            var on = _on[i];
            if (bone != null && !bone.WasCollected && on != null && !on.WasCollected && bone.parent != null && bone.parent.Pointer == on.Pointer)
                Object.Destroy(bone.gameObject);
        }
        foreach (var material in _materials) if (material != null && !material.WasCollected) Object.Destroy(material);
        _materials.Clear();
        if (_mesh != null && !_mesh.WasCollected) Object.Destroy(_mesh);
        if (RootAlive) Object.Destroy(_root);
    }

    /// <summary>The game's first-person bone for <paramref name="bone"/>: the arms through the hand IK solvers, the rest through the
    /// humanoid animator.</summary>
    internal static Transform? Resolve(PlayerFPSBody body, BodyBone bone)
    {
        switch (bone)
        {
            case BodyBone.LeftUpperArm: return Limb(body.m_leftHandIK, 1);
            case BodyBone.LeftLowerArm: return Limb(body.m_leftHandIK, 2);
            case BodyBone.LeftHand: return Limb(body.m_leftHandIK, 3);
            case BodyBone.RightUpperArm: return Limb(body.m_rightHandIK, 1);
            case BodyBone.RightLowerArm: return Limb(body.m_rightHandIK, 2);
            case BodyBone.RightHand: return Limb(body.m_rightHandIK, 3);
        }
        var animator = body.m_anim;
        if (animator == null || !animator.isHuman) return null;
        return animator.GetBoneTransform((HumanBodyBones)(int)bone);
    }

    private static Transform? Limb(LimbIK? ik, int index)
    {
        var solver = ik == null ? null : ik.solver;
        if (solver == null) return null;
        var bone = index == 1 ? solver.bone1 : index == 2 ? solver.bone2 : solver.bone3;
        return bone?.transform;
    }

    /// <summary>A copy of the mesh with only the triangles whose three corners are skinned at least half to worn bones (or to
    /// <paramref name="followParent"/> bones hinged directly on one), those corners' weights renormalised over them.</summary>
    private static Mesh? Cut(Mesh source, Il2CppReferenceArray<Transform> skin, Transform[] model, bool[] worn, IReadOnlyCollection<string>? followParent)
    {
        var follow = followParent != null && followParent.Count > 0 ? new HashSet<string>(followParent, StringComparer.Ordinal) : null;
        var kept = new bool[skin.Length];
        for (var b = 0; b < skin.Length; b++)
        {
            var bone = skin[b];
            if (bone == null) continue;
            var owner = bone;
            // Each bone's name is read once (a call into the game and a new string per read).
            if (follow != null && bone.parent != null && follow.Contains(bone.name)) owner = bone.parent;
            for (var i = 0; i < model.Length; i++)
                if (worn[i] && model[i].Pointer == owner.Pointer) { kept[b] = true; break; }
        }
        var native = ForgeCore.Il2CppArrays.ToManaged(source.boneWeights);
        var weights = new Weights4[native.Length];
        for (var v = 0; v < native.Length; v++)
        {
            var w = native[v];
            weights[v] = new Weights4 { I0 = w.boneIndex0, I1 = w.boneIndex1, I2 = w.boneIndex2, I3 = w.boneIndex3, W0 = w.weight0, W1 = w.weight1, W2 = w.weight2, W3 = w.weight3 };
        }
        var keep = MeshCut.KeepVertices(weights, kept);
        var mesh = Object.Instantiate(source);
        mesh.name = source.name + ".worn";
        var any = false;
        for (var sub = 0; sub < source.subMeshCount; sub++)
        {
            var cut = MeshCut.KeepTriangles(ForgeCore.Il2CppArrays.ToManaged(source.GetTriangles(sub)), keep);
            any |= cut.Length > 0;
            mesh.SetTriangles(ForgeCore.Il2CppArrays.ToGame(cut), sub, true);
        }
        if (!any) { Object.Destroy(mesh); return null; }
        for (var v = 0; v < native.Length; v++)
        {
            if (!keep[v]) continue;
            var w = native[v];
            w.weight0 = weights[v].W0; w.weight1 = weights[v].W1; w.weight2 = weights[v].W2; w.weight3 = weights[v].W3;
            native[v] = w;
        }
        mesh.boneWeights = ForgeCore.Il2CppArrays.ToGame(native);
        return mesh;
    }

    private static void Error(string label, string why, bool fatal = true)
        => Plugin.Logger.ErrorOnce("visual-first-person-wear-games-own-body", "ForgeVisual first-person wear " + label + ": " + why + (fatal ? "; the game's own body stays." : "."));
}
