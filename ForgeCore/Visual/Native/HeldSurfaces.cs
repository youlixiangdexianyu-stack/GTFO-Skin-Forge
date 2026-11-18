using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>The surfaces of a model hung on a held item, shown the way the holder is seen: in first person every material slot is drawn with the
/// game's item camera (<see cref="LitMaterial.FirstPerson"/>, kept copies) and casts and receives no shadow, as the game's own held parts do; in
/// the world they draw their own materials with shadows. A model on a first-person item that skips this is drawn by the world camera too and
/// shows as a second, smaller copy away from the hands. Read once (<see cref="Of"/>) and call <see cref="Show"/> whenever the holder's view is
/// set or changes; a part added under the roots later needs a new reading.</summary>
public sealed class HeldSurfaces
{
    private readonly (Renderer Renderer, Il2CppReferenceArray<Material> World, Il2CppReferenceArray<Material> FirstPerson)[] _surfaces;

    private HeldSurfaces((Renderer, Il2CppReferenceArray<Material>, Il2CppReferenceArray<Material>)[] surfaces) => _surfaces = surfaces;

    /// <summary>Reads the mesh renderers under <paramref name="roots"/> (inactive ones too) with their current materials as the world look.</summary>
    public static HeldSurfaces Of(params Transform?[] roots)
    {
        var read = new List<(Renderer, Il2CppReferenceArray<Material>, Il2CppReferenceArray<Material>)>();
        foreach (var root in roots)
        {
            if (root == null) continue;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!Models.IsMesh(renderer)) continue;
                var world = renderer.sharedMaterials;
                var first = new Il2CppReferenceArray<Material>(world.Length);
                for (var i = 0; i < world.Length; i++) first[i] = LitMaterial.FirstPerson(world[i])!;
                read.Add((renderer, world, first));
            }
        }
        return new HeldSurfaces(read.ToArray());
    }

    /// <summary>Shows the surfaces as first person (<paramref name="firstPerson"/>) or as the world sees them.</summary>
    public void Show(bool firstPerson)
    {
        foreach (var (renderer, world, first) in _surfaces)
        {
            if (renderer == null || renderer.WasCollected) continue;
            renderer.sharedMaterials = firstPerson ? first : world;
            renderer.shadowCastingMode = firstPerson ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveShadows = !firstPerson;
        }
    }
}
