using System;
using System.Collections.Generic;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// A pair of arms in front of the local player's first-person camera that plays <see cref="JointClip"/>s (<see cref="Models.BindFirstPersonArms"/>).
/// The game's first-person view draws no hands, and it poses its own first-person skeleton again after every other late update (a clip
/// written there never shows, and the gun hangs on it), so this is a copy of the third-person model's skeleton (the same bones; the first-person body's arm meshes are not under its animator, so they cannot be copied with it) with only the arm meshes left: nothing on it
/// but the clip moves it, the camera carries it, and no other player sees it (they see the clip on the third-person body).
/// <para>The copy stands where the player's body would, so the arms come out of real shoulders under the eye and enter the view from below. It plays clips
/// made for this view (<c>"firstPerson": true</c> in the clip sources: a third-person gesture raised for others to see stands up the middle of the screen from here), both arms under the
/// clip unless the mask says otherwise. Main thread only.</para>
/// <para>Like the game's first-person body and held item it is drawn through the item camera, whose field of view belongs to the held item (32 to 55 degrees) and not to the player's setting.
/// The copy is squeezed against that view each frame, so a clip looks the same with any item in hand, aiming included.</para>
/// </summary>
public sealed class FirstPersonArms
{
    // The game's rig: shoulders 1.56 m up and 0.21 m out, the eye at IK_cameraTarget (1.729 m up, 0.049 m forward). Tools/Models/build-joint-clips.ts places a first-person clip's wrists from the same point.
    private static readonly Vector3 ShouldersFromEye = new(0f, -0.17f, -0.05f);

    // The vertical field of view clips are made for: the game's commonest item camera. Tools/Models/preview-joint-clip.py renders with it.
    private const float ViewDegrees = 45f;

    private static readonly List<FirstPersonArms> Active = new();

    private readonly PlayerAgent _agent;
    private readonly GameObject _root;
    private readonly Camera? _itemCamera;
    private readonly Vector3 _place;
    private readonly List<Renderer> _heldOff = new();
    private JointTrack _track;
    private bool _showing;

    private FirstPersonArms(PlayerAgent agent, GameObject root, JointPlayer player, string source, string[] meshes)
    {
        _agent = agent;
        _root = root;
        _itemCamera = agent.FPItemHolder!.m_ItemLayerCamera;
        _place = root.transform.localPosition;
        Player = player;
        Source = source;
        Meshes = meshes;
    }

    /// <summary>The copy and its player still exist; when they do not (they went with the level), bind again.</summary>
    public bool Alive => _root != null && !_root.WasCollected && _agent != null && !_agent.WasCollected;

    /// <summary>The copy's joints, for reading a pose back.</summary>
    public JointPlayer Player { get; }

    /// <summary>Whose arm meshes these are.</summary>
    public string Source { get; }

    /// <summary>The names of the arm meshes drawn.</summary>
    public IReadOnlyList<string> Meshes { get; }

    /// <summary>Shows the arms playing <paramref name="clip"/>, a clip made for this view; any clip already on them stops. The mask says which arms it moves
    /// (default both). <paramref name="hideHeld"/> takes the first-person item out of the view meanwhile. They go away by themselves when a clip that does not loop ends.</summary>
    public JointTrack Play(JointClip clip, in JointPlayback playback, bool hideHeld)
    {
        if (clip == null) throw new ArgumentNullException(nameof(clip));
        Stop();
        _root.SetActive(true);
        var mask = playback.Mask ?? new JointMask().Add(BodyPart.LeftArm).Add(BodyPart.RightArm);
        _track = Player.Play(clip, new JointPlayback(playback.Start, playback.Loop, mask, playback.Weight, playback.FadeIn, playback.FadeOut));
        _showing = true;
        Active.Add(this);
        if (hideHeld) HeldOff.HoldAll(_agent.FPItemHolder, _heldOff);
        return _track;
    }

    /// <summary>The arms go away and the first-person item comes back.</summary>
    public void Stop()
    {
        if (!_showing) return;
        _showing = false;
        Active.Remove(this);
        Player.Stop();
        HeldOff.ReleaseAll(_heldOff);
        if (_root != null && !_root.WasCollected) _root.SetActive(false);
    }

    /// <summary>After the clips have posed (LateUpdate): arms whose clip ended, or whose camera went with the level, go away.</summary>
    internal static void TickAll()
    {
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var arms = Active[i];
            if (arms._root == null || arms._root.WasCollected || !arms._track.Playing) arms.Stop();
            else arms.FitView();
        }
    }

    // Squeezing across the view is the same picture as changing the field of view, and it leaves the clip's depth alone.
    private void FitView()
    {
        if (_itemCamera == null || _itemCamera.WasCollected) return;
        var squeeze = Mathf.Tan(_itemCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(ViewDegrees * 0.5f * Mathf.Deg2Rad);
        var frame = _root.transform;
        frame.localScale = new Vector3(squeeze, squeeze, 1f);
        frame.localPosition = new Vector3(_place.x * squeeze, _place.y * squeeze, _place.z);
    }

    internal static void StopAll()
    {
        for (var i = Active.Count - 1; i >= 0; i--) Active[i].Stop();
    }

    internal static FirstPersonArms? Build(PlayerAgent agent)
    {
        var label = "first-person arms of player " + agent.Owner?.Lookup;
        var camera = agent.FPSCamera;
        var body = agent.FPItemHolder == null ? null : agent.FPItemHolder.FPSArms;
        if (camera == null || camera.WasCollected || body == null || body.WasCollected)
        {
            Plugin.Logger.ErrorOnce("visual-player-has-no-first-person-camera", "ForgeVisual " + label + ": the player has no first-person camera or body.");
            return null;
        }
        Renderer? sample = null;
        if (body.m_gfxArms != null)
            foreach (var part in body.m_gfxArms)
                if (part != null) sample ??= part.GetComponentInChildren<Renderer>(true);
        var layer = sample != null ? sample.gameObject.layer : agent.FPItemHolder!.gameObject.layer;
        var model = agent.PlayerSyncModel;
        return (model == null ? null : Copy(agent, agent.AnimatorBody, model.m_gfxArms, "third-person model", camera.transform, layer, label)) ?? Failed(label);
    }

    /// <summary>The third-person model has its arm meshes under its animator. Before the level's players are built (the lobby, the load) it has not, and
    /// <see cref="Build"/> would report that as an error; a caller that binds ahead of time waits for this instead.</summary>
    internal static bool Ready(PlayerAgent agent)
        => agent.PlayerSyncModel is { } model && ArmPaths(agent.AnimatorBody, model.m_gfxArms).Count != 0;

    private static List<string> ArmPaths(Animator source, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>? parts)
    {
        var paths = new List<string>();
        if (source == null || source.WasCollected || parts == null) return paths;
        foreach (var part in parts)
        {
            if (part == null) continue;
            foreach (var skin in part.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh != null && PathUnder(source.transform, skin.transform) is { } path && !paths.Contains(path)) paths.Add(path);
        }
        return paths;
    }

    private static FirstPersonArms? Failed(string label)
    {
        Plugin.Logger.ErrorOnce("visual-third-person-model-has-no-arm", "ForgeVisual " + label + ": the third-person model has no arm meshes under its animator; no clip shows in first person.");
        return null;
    }

    private static FirstPersonArms? Copy(PlayerAgent agent, Animator source, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>? parts, string name, Transform camera, int layer, string label)
    {
        var paths = ArmPaths(source, parts);
        if (paths.Count == 0) return null;

        // Under an inactive object no script of the copy wakes; all of them go before it does.
        var cradle = new GameObject("ForgeVisual.FirstPersonArms");
        cradle.SetActive(false);
        var copy = Object.Instantiate(source.gameObject, cradle.transform);
        copy.name = "ForgeVisual.FirstPersonArms." + name;
        foreach (var component in copy.GetComponentsInChildren<Component>(true))
        {
            if (component.TryCast<Transform>() != null || component.TryCast<Animator>() != null || component.TryCast<SkinnedMeshRenderer>() != null) continue;
            Object.DestroyImmediate(component);
        }
        copy.transform.SetParent(camera, false);
        // The game scales the model (1.2, read in the game); clips and the framing below are made for the skeleton at its own size, and FitView sets this scale every frame.
        copy.transform.localScale = Vector3.one;
        Object.Destroy(cradle);
        copy.SetActive(true);
        var animator = copy.GetComponent<Animator>();
        var player = JointPlayer.Bind(animator, _ => IkPause.None, label, out _, RigShape.Arms);
        animator.enabled = false;
        if (player == null) { Object.Destroy(copy); return null; }

        // The bind pose: straight up, facing the copy's forward, so only the clip's arms and the framing decide what shows.
        var frame = copy.transform;
        frame.localRotation = Quaternion.identity;
        frame.localPosition = Vector3.zero;
        var rest = BodyConvert.BindRest(frame, frame);
        foreach (var bone in copy.GetComponentsInChildren<Transform>(true))
            if (rest.TryGetValue(bone.Pointer, out var at))
                bone.SetPositionAndRotation(frame.TransformPoint(at.GetColumn(3)), frame.rotation * at.rotation);

        // Framed by the shoulders, the bones the sleeves are skinned to: their bind pose is always known, a head bone nothing on this body is skinned to keeps whatever pose the animator left.
        var left = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var right = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (left == null || right == null) { Object.Destroy(copy); return null; }
        frame.position += camera.TransformPoint(ShouldersFromEye) - (left.position + right.position) / 2f;

        var keep = new HashSet<IntPtr>();
        var meshes = new List<string>();
        foreach (var path in paths)
        {
            var skin = frame.Find(path)?.GetComponent<SkinnedMeshRenderer>();
            if (skin == null) continue;
            keep.Add(skin.Pointer);
            meshes.Add(skin.sharedMesh.name);
            for (var up = skin.transform; up != frame; up = up.parent) up.gameObject.SetActive(true);
            skin.enabled = true;
            skin.forceRenderingOff = false;
            skin.updateWhenOffscreen = true;
            skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            skin.receiveShadows = false;
        }
        foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (!keep.Contains(skin.Pointer)) Object.DestroyImmediate(skin);
        Models.SetLayer(frame, layer);
        PlayerBackpackManager.SetFPSRendering(copy, true);
        // The keyword alone leaves the arms in the world's projection: the shader blends to the item camera's by _FPSWeight, which the FPS_Content on the game's body sets and the copy does not keep.
        FPS_Content.SetEnabledForGameObject(copy, true, 1f);
        var arms = new FirstPersonArms(agent, copy, player, name, meshes.ToArray());
        copy.SetActive(false);
        return arms;
    }

    private static string? PathUnder(Transform root, Transform bone)
    {
        var path = bone.name;
        for (var up = bone.parent; up != null; up = up.parent)
        {
            if (up.Pointer == root.Pointer) return path;
            path = up.name + "/" + path;
        }
        return null;
    }
}
