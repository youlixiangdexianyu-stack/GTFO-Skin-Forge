using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppArrays = ForgeCore.Il2CppArrays;

namespace ForgeUI;

/// <summary>
/// A picture of any item taken from the item's own game model: the model's meshes and materials are copied onto a private stage,
/// photographed twice by a private camera (over black and over white, so the item's edge and colours come out without depending on what
/// alpha the game's shaders write) into one transparent picture, and kept per item type, so each type is rendered once and every later
/// call costs a lookup. Nothing is shipped: the model is whatever the game (or a mod's own item) built. The picture is in the item's
/// colours, like the game's weapon icons, and goes through the same white-with-shading copy on the HUD (<see cref="IconSilhouette"/>).
/// </summary>
public static class ItemModelIcons
{
    // 192 : 62 is the shape the HUD card draws its picture in, so the picture is not stretched.
    private const int Width = 496, Height = 160, Samples = 4, Layer = 31;
    private const float Margin = 1.1f;
    private static readonly Vector3 Stage = new(4000f, 4000f, 4000f);
    private static readonly Dictionary<uint, Sprite?> Made = new();
    // Item types whose model was not there yet, and since when: the picture waits for it instead of being given up on the first ask.
    private static readonly Dictionary<uint, float> Waits = new();
    private const float ModelWaitSeconds = 10f;

    /// <summary>The picture of <paramref name="item"/>'s model, or null when it cannot be had: null while the item is not ready (try again
    /// later), null with one logged error when it has no drawable model or the render failed (that type is not tried again).</summary>
    public static Sprite? Of(Item? item)
    {
        if (item == null || item.WasCollected) return null;
        var block = item.ItemDataBlock;
        if (block == null) return null;
        var id = block.persistentID;
        if (Made.TryGetValue(id, out var cached) && (cached == null || cached.texture != null)) return cached;
        var name = block.publicName ?? block.name ?? id.ToString();
        // The game builds an item's model into its holder when it needs it (a consumable still in a backpack may not have one
        // yet): wait for it, then say it has nothing to draw only after the wait.
        if (!HasModel(item))
        {
            if (!Waits.TryGetValue(id, out var since)) Waits[id] = since = Time.unscaledTime;
            if (Time.unscaledTime - since < ModelWaitSeconds) return null;
        }
        Sprite? sprite;
        try { sprite = Build(item, name); }
        catch (Exception error)
        {
            Plugin.Error("ui-item-model-icon", "Item model icon of '" + name + "' could not be rendered and is not drawn: " + error + " (" + Renderers(item) + ")");
            sprite = null;
        }
        Waits.Remove(id);
        Made[id] = sprite;
        return sprite;
    }

    /// <summary>True while an item's model is not there yet (no renderer under its holder and no model prefab to take the picture
    /// from): the asker asks again on its next quiet rebuild.</summary>
    public static bool Waiting(Item? item)
    {
        if (item == null || item.WasCollected || item.ItemDataBlock == null) return false;
        return !Made.ContainsKey(item.ItemDataBlock.persistentID) && !HasModel(item);
    }

    private static bool HasModel(Item item)
    {
        var holder = item.m_itemModelHolder != null ? item.m_itemModelHolder : item.transform;
        return HasRenderers(holder) || item.BaseModel != null && item.BaseModel.Prefab != null;
    }

    private static bool HasRenderers(Transform root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) if (renderer != null) return true;
        return false;
    }

    /// <summary>What sits under the item's model holder, for the failure line only: each renderer kind with how many are enabled and how
    /// many have a mesh, and the holder's child count. Tells a skinned model, a model not built yet and an empty export apart.</summary>
    private static string Renderers(Item item)
    {
        try
        {
            var holder = item.m_itemModelHolder != null ? item.m_itemModelHolder : item.transform;
            var counts = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                var kind = skinned != null ? "SkinnedMeshRenderer" : renderer.TryCast<MeshRenderer>() != null ? "MeshRenderer" : "other";
                if (!counts.TryGetValue(kind, out var row)) counts[kind] = row = new int[3];
                row[0]++;
                if (renderer.enabled) row[1]++;
                var filter = renderer.GetComponent<MeshFilter>();
                if (skinned != null ? skinned.sharedMesh != null : filter != null && filter.sharedMesh != null) row[2]++;
            }
            var parts = new List<string>();
            foreach (var pair in counts) parts.Add(pair.Key + " total " + pair.Value[0] + " enabled " + pair.Value[1] + " with mesh " + pair.Value[2]);
            return "holder " + (item.m_itemModelHolder != null ? "model" : "item") + ", children " + holder.childCount + ", renderers "
                + (parts.Count == 0 ? "none" : string.Join(", ", parts));
        }
        catch (Exception error) { return "renderers unreadable: " + error.GetType().Name; }
    }

    private static Sprite? Build(Item item, string name)
    {
        var holder = item.m_itemModelHolder != null ? item.m_itemModelHolder : item.transform;
        var stage = new GameObject("ForgeUI item icon stage") { hideFlags = HideFlags.HideAndDontSave };
        var hold = new GameObject("ForgeUI item icon hold") { hideFlags = HideFlags.HideAndDontSave };
        hold.SetActive(false);
        GameObject? prefabModel = null;
        try
        {
            stage.transform.position = Stage;
            // A model the item holds not built yet (a consumable in a backpack before it is equipped): its own model prefab is
            // the same model, copied under the inactive hold so no script of it wakes, to take the picture from.
            var sourceRoot = holder;
            if (!HasRenderers(holder) && item.BaseModel != null && item.BaseModel.Prefab != null)
            {
                prefabModel = UnityEngine.Object.Instantiate(item.BaseModel.Prefab, hold.transform);
                prefabModel.name = "ForgeUI item icon model " + name;
                foreach (var script in prefabModel.GetComponentsInChildren<MonoBehaviour>(true)) if (script != null) UnityEngine.Object.DestroyImmediate(script);
                sourceRoot = prefabModel.transform;
            }
            var toStage = prefabModel != null ? stage.transform.worldToLocalMatrix : holder.worldToLocalMatrix;
            var bounds = default(Bounds);
            var parts = 0;
            int shadowOnly = 0, hidden = 0, unfinished = 0;
            var renderers = sourceRoot.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var source in renderers)
            {
                if (source == null || !source.enabled) continue;
                if (source.shadowCastingMode == ShadowCastingMode.ShadowsOnly) { shadowOnly++; continue; }
                if (!ActiveUnder(source.transform, sourceRoot)) { hidden++; continue; }
                var filter = source.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || source.sharedMaterial == null) { unfinished++; continue; }
                var part = new GameObject("part") { layer = Layer, hideFlags = HideFlags.HideAndDontSave };
                var local = toStage * source.transform.localToWorldMatrix;
                var t = part.transform;
                t.SetParent(stage.transform, false);
                t.localPosition = local.GetColumn(3);
                t.localRotation = local.rotation;
                t.localScale = local.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterials = source.sharedMaterials;
                copy.shadowCastingMode = ShadowCastingMode.Off;
                copy.receiveShadows = false;
                if (parts++ == 0) bounds = copy.bounds; else bounds.Encapsulate(copy.bounds);
            }
            if (parts == 0) throw new InvalidOperationException("the item's model has no visible mesh (shadow-only " + shadowOnly + ", switched off " + hidden + ", no mesh or material " + unfinished + ")");
            return Photograph(stage, bounds, name);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
            UnityEngine.Object.DestroyImmediate(stage);
        }
    }

    private static bool ActiveUnder(Transform t, Transform holder)
    {
        // The holder and everything above it carry the game's wield state (an item not in hand has its model switched off), not a
        // hidden part, so only the objects between the part and the holder decide.
        for (var node = t; node != null && node != holder; node = node.parent)
            if (!node.gameObject.activeSelf) return false;
        return true;
    }

    private static Sprite Photograph(GameObject stage, Bounds bounds, string name)
    {
        var rig = new GameObject("ForgeUI item icon rig") { hideFlags = HideFlags.HideAndDontSave };
        RenderTexture? target = null;
        var previous = RenderTexture.active;
        try
        {
            var mask = 1 << Layer;
            var key = new GameObject("key") { layer = Layer };
            key.transform.SetParent(rig.transform, false);
            var keyLight = key.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.shadows = LightShadows.None;
            keyLight.cullingMask = mask;
            keyLight.intensity = 1.1f;
            var fill = new GameObject("fill") { layer = Layer };
            fill.transform.SetParent(rig.transform, false);
            var fillLight = fill.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.shadows = LightShadows.None;
            fillLight.cullingMask = mask;
            fillLight.intensity = 0.45f;

            var cam = rig.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.cullingMask = mask;
            cam.orthographic = true;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            // Look along the item's thinnest direction with its longest one across the picture, a little from above and the side.
            var e = bounds.extents;
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (a, b) => e[a].CompareTo(e[b]));
            Vector3 thin = axes[order[0]], mid = axes[order[1]], wide = axes[order[2]];
            var view = (thin + mid * 0.45f - wide * 0.35f).normalized;
            var centre = bounds.center;
            var tr = cam.transform;
            tr.position = centre + view * 20f;
            tr.rotation = Quaternion.LookRotation(-view, mid);
            key.transform.rotation = Quaternion.LookRotation(tr.forward + tr.up * -0.5f + tr.right * 0.6f, tr.up);
            fill.transform.rotation = Quaternion.LookRotation(tr.forward + tr.up * 0.2f - tr.right * 0.8f, tr.up);

            float hx = 0f, hy = 0f;
            for (var i = 0; i < 8; i++)
            {
                var corner = bounds.min + Vector3.Scale(bounds.size, new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)) - centre;
                hx = Math.Max(hx, Math.Abs(Vector3.Dot(corner, tr.right)));
                hy = Math.Max(hy, Math.Abs(Vector3.Dot(corner, tr.up)));
            }
            cam.orthographicSize = Math.Max(hy, hx * Height / Width) * Margin;

            target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, Samples);
            cam.targetTexture = target;
            var onBlack = Shot(cam, target, Color.black);
            var onWhite = Shot(cam, target, Color.white);
            var pixels = new Color32[onBlack.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                Color32 a = onBlack[i], b = onWhite[i];
                var coverage = Math.Clamp(1f - ((b.r - a.r) + (b.g - a.g) + (b.b - a.b)) / 765f, 0f, 1f);
                if (coverage < 0.02f) continue;
                pixels[i] = new Color32(Channel(a.r, coverage), Channel(a.g, coverage), Channel(a.b, coverage), (byte)Math.Round(coverage * 255f));
            }
            var picture = new Texture2D(Width, Height, TextureFormat.RGBA32, false) { name = "ForgeUI item " + name, hideFlags = HideFlags.HideAndDontSave };
            Il2CppArrays.Upload(picture, pixels);
            picture.Apply(false, true);
            var sprite = Sprite.Create(picture, new Rect(0, 0, Width, Height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = picture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.DestroyImmediate(rig);
        }
    }

    private static byte Channel(byte value, float coverage) => (byte)Math.Min(255f, Math.Round(value / coverage));

    private static Color32[] Shot(Camera cam, RenderTexture target, Color background)
    {
        cam.backgroundColor = background;
        cam.Render();
        RenderTexture.active = target;
        var read = new Texture2D(Width, Height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            return Il2CppArrays.ToManaged(read.GetPixels32());
        }
        finally { UnityEngine.Object.DestroyImmediate(read); }
    }
}
