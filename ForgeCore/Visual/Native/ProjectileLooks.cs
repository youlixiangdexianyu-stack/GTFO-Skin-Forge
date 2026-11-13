using System;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The look of one of the game's own projectiles (<c>ProjectileType</c>, the manager's prefab table) without its behaviour: a copy of the
/// prefab that keeps only what draws (meshes, particles, trails, lines, lights). It flies, hits and hurts nothing; the caller moves it and
/// destroys it. ForgeWeapon's extended bullet flights and ForgeReplay's recorded enemy projectiles both draw through here.
/// </summary>
public static class ProjectileLooks
{
    /// <summary>A drawing-only copy of the prefab of <paramref name="type"/> at <paramref name="origin"/>, or null with the
    /// <paramref name="reason"/> (<c>manager-unavailable</c>, <c>prefab-table-unavailable</c>, <c>prefab-index-out-of-range</c>,
    /// <c>prefab-missing</c>, <c>native-speed-unavailable</c>). With <paramref name="needsSpeed"/> the prefab's own
    /// <c>ProjectileTargeting.Speed</c> is read into <paramref name="speed"/>. The game's prefabs, live projectiles and lists are not
    /// touched: the copy is made under an inactive parent, so its combat scripts never run Awake or OnEnable, and is stripped and checked
    /// before it is switched on.</summary>
    public static GameObject? Copy(ProjectileType type, Vector3 origin, Quaternion rotation, bool needsSpeed, out float speed, out string reason)
    {
        speed = float.NaN;
        reason = "";
        var manager = ProjectileManager.Current;
        if (manager == null) { reason = "manager-unavailable"; return null; }
        var prefabs = manager.m_projectilePrefabs;
        if (prefabs == null) { reason = "prefab-table-unavailable"; return null; }
        var index = (int)type;
        if (index < 0 || index >= prefabs.Length) { reason = "prefab-index-out-of-range"; return null; }
        if (prefabs[index] == null) { reason = "prefab-missing"; return null; }
        var prefab = prefabs[index];
        if (needsSpeed)
        {
            var targeting = prefab.GetComponent<ProjectileTargeting>();
            speed = targeting == null ? float.NaN : targeting.Speed;
            if (!float.IsFinite(speed)) { reason = "native-speed-unavailable"; return null; }
        }
        GameObject? staging = null;
        GameObject? visual = null;
        try
        {
            staging = new GameObject();
            staging.name = "ForgeProjectile.Staging";
            staging.SetActive(false);
            visual = UnityEngine.Object.Instantiate<GameObject>(prefab, staging.transform);
            // Dependencies can prevent removal until the requiring component has been removed.
            for (var pass = 0; pass < 4; pass++)
            {
                var removed = false;
                foreach (var component in visual.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || Presentation(component)) continue;
                    UnityEngine.Object.DestroyImmediate(component);
                    removed = true;
                }
                if (!removed) break;
            }
            foreach (var component in visual.GetComponentsInChildren<Component>(true))
                if (component != null && !Presentation(component))
                    throw new InvalidOperationException("projectile-visual-not-pure");
            visual.name = "ForgeProjectile.Visual";
            visual.transform.position = origin;
            visual.transform.rotation = rotation;
            visual.transform.SetParent(null, true);
            visual.SetActive(true);
            return visual;
        }
        catch
        {
            if (visual != null) UnityEngine.Object.Destroy(visual);
            throw;
        }
        finally
        {
            if (staging != null) UnityEngine.Object.Destroy(staging);
        }
    }

    private static bool Presentation(Component component)
        => component.TryCast<Transform>() != null || component.TryCast<MeshFilter>() != null
            || component.TryCast<MeshRenderer>() != null || component.TryCast<SkinnedMeshRenderer>() != null
            || component.TryCast<LODGroup>() != null || component.TryCast<Light>() != null
            || component.TryCast<ParticleSystem>() != null || component.TryCast<ParticleSystemRenderer>() != null
            || component.TryCast<TrailRenderer>() != null || component.TryCast<LineRenderer>() != null;
}
