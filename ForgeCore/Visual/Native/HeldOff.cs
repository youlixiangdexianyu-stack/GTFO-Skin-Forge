using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// Renderers held out of the picture by several owners at once (the view from behind, the first-person arms, an action hiding the held item).
/// The value from before the first owner comes back when the last one lets go, in whatever order they let go: an owner that remembered the flag
/// itself would remember another owner's "off" and hand it back after that owner had gone, and the item would stay invisible for good.
/// A dressing that hides or shows the same renderer meanwhile (<see cref="Models.HideRenderers"/>, a worn body) writes through <see cref="SetUnder"/>, so the value that comes back is the one it wants.
/// </summary>
internal static class HeldOff
{
    private static readonly Dictionary<IntPtr, (Renderer Renderer, int Owners, bool Was)> Held = new();

    internal static void Hold(Renderer renderer)
    {
        var key = renderer.Pointer;
        Held[key] = Held.TryGetValue(key, out var held) ? (renderer, held.Owners + 1, held.Was) : (renderer, 1, renderer.forceRenderingOff);
        renderer.forceRenderingOff = true;
    }

    /// <summary>Holds every renderer under <paramref name="root"/> (only meshes with <paramref name="meshesOnly"/>: a muzzle flash is not the item) and adds them to <paramref name="into"/>.</summary>
    internal static void HoldAll(Component? root, List<Renderer> into, bool meshesOnly = false)
    {
        if (root == null || root.WasCollected) return;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (meshesOnly && !Models.IsMesh(renderer)) continue;
            Hold(renderer);
            into.Add(renderer);
        }
    }

    internal static void Release(Renderer renderer)
    {
        if (renderer.WasCollected) return;
        var key = renderer.Pointer;
        if (!Held.TryGetValue(key, out var held)) return;
        if (held.Owners > 1) { Held[key] = (renderer, held.Owners - 1, held.Was); return; }
        Held.Remove(key);
        if (renderer != null) renderer.forceRenderingOff = held.Was;
    }

    /// <summary>Lets go of everything in <paramref name="held"/> and empties it.</summary>
    internal static void ReleaseAll(List<Renderer> held)
    {
        foreach (var renderer in held) Release(renderer);
        held.Clear();
    }

    /// <summary>Whether the renderer is off apart from what its holders do.</summary>
    internal static bool IsOff(Renderer renderer) => Held.TryGetValue(renderer.Pointer, out var held) ? held.Was : renderer.forceRenderingOff;

    /// <summary>Turns the renderer off or on for everyone but its holders: while it is held this is what comes back when they let go.</summary>
    internal static void SetUnder(Renderer renderer, bool off)
    {
        var key = renderer.Pointer;
        if (Held.TryGetValue(key, out var held)) Held[key] = (held.Renderer, held.Owners, off);
        else renderer.forceRenderingOff = off;
    }

    /// <summary>The level ended or a checkpoint reloaded, with owners still holding: what outlives it gets its value back now, and the owners' later releases do nothing.</summary>
    internal static void Forget()
    {
        foreach (var held in Held.Values)
            if (held.Renderer != null && !held.Renderer.WasCollected) held.Renderer.forceRenderingOff = held.Was;
        Held.Clear();
    }
}
