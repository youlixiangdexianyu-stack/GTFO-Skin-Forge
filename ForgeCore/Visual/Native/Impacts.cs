using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>Everything one kind of hit shows where it lands: any of an effect by name, a prefab effect, a light flash, a mark on the
/// surface and a camera shake. Every part is optional and given by the mod that owns the look. Make one per kind of hit at load and
/// reuse it.</summary>
public sealed class ImpactLook
{
    /// <summary>An effect by name (<c>Game:</c> or a pack's), played facing the surface normal.</summary>
    public FxHandle? Fx { get; set; }

    /// <summary>The size (m) <see cref="Fx"/> plays at when the play's scale is 1, for an effect that takes a size (<see cref="FxOptions.DefaultSize"/>); NaN: its own size.</summary>
    public float FxSize { get; set; } = float.NaN;

    /// <summary>A prefab effect (a particle system at its root) played from the library's own pool, facing the normal, at
    /// <see cref="ModelScale"/> times the play's scale. Fill the pool ahead with <see cref="Impacts.Prepare"/>.</summary>
    public ModelHandle? Model { get; set; }

    public float ModelScale { get; set; } = 1f;

    /// <summary>A light flash (<see cref="PointLights.Flash"/>), <see cref="LightLift"/> metres off the surface, its peak times the
    /// play's strength.</summary>
    public PointLightSpec? Light { get; set; }

    public float LightLift { get; set; }

    /// <summary>A mark on the surface, on <see cref="Decals.Shared"/>: <see cref="DecalSize"/> metres times the play's scale, shown
    /// for <see cref="DecalLife"/> seconds and fading out over the last <see cref="DecalFade"/>.</summary>
    public SpriteSheet? Decal { get; set; }

    public float DecalSize { get; set; } = 0.3f;
    public float DecalLife { get; set; } = 20f;
    public float DecalFade { get; set; } = 4f;
    public Color DecalColour { get; set; } = Color.white;

    /// <summary>A camera shake from the hit (<see cref="CameraShake.Shake"/>).</summary>
    public ShakeSpec? Shake { get; set; }
}

/// <summary>Plays an <see cref="ImpactLook"/> where something hit. Nothing allocates once the pool holds enough copies.</summary>
public static class Impacts
{
    private static FxInstancePool? _pool;

    private static FxInstancePool Pool => _pool ??= new FxInstancePool("ForgeVisual impacts");

    /// <summary>Shows <paramref name="look"/> at <paramref name="point"/> on a surface facing <paramref name="normal"/> (zero: the
    /// prefab and effect unrotated, the light and mark straight up). <paramref name="scale"/> scales the prefab and the mark,
    /// <paramref name="strength"/> the light.</summary>
    public static void Play(ImpactLook look, Vector3 point, Vector3 normal, float scale = 1f, float strength = 1f)
    {
        if (look == null) return;
        var facing = normal.sqrMagnitude > 1e-8f;
        var up = facing ? normal.normalized : Vector3.up;
        if (look.Fx != null) ForgeVisual.Fx.Play(look.Fx, point, facing ? up : Vector3.zero, look.FxSize * scale, Color.white);
        if (look.Model != null)
        {
            var copy = Pool.Take(look.Model);
            copy.Place(point, facing ? Quaternion.LookRotation(up) : Quaternion.identity, look.ModelScale * scale);
            copy.Start();
        }
        if (look.Light is { } light) PointLights.Flash(point + up * look.LightLift, in light, Time.time, strength);
        if (look.Decal != null) Decals.Shared?.Place(look.Decal, point, up, look.DecalSize * scale, look.DecalColour, look.DecalLife, look.DecalFade);
        if (look.Shake is { } shake) CameraShake.Shake(point, in shake);
    }

    /// <summary>Makes <paramref name="copies"/> copies of the look's prefab, one per step, so a level can fill the pool before the first
    /// hit without one long frame; nothing for a look without a prefab.</summary>
    public static IEnumerable<object?> Prepare(ImpactLook look, int copies)
        => look?.Model == null ? System.Array.Empty<object?>() : Pool.Prepare(look.Model, copies);
}
