using ForgeCore;
using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// Ejected casings come from one shared pool per casing type (WeaponShellManager.m_shellPools, indexed by ShellTypes), so a
// skinned gun gets a pool of its own: a copy of its casing prefab drawn with the gun's skinned materials (in world
// projection, the casing flies outside the first-person view), appended after the game's pools. While the gun ejects, its
// casing type points at that pool. A few slots serve the local player's guns; the oldest is reused when they run out.
static class Shells
{
    const int Slots = 4, Count = 50;
    const float Lifetime = 20f; // seconds a casing lies before its pool takes it back

    static int _base = -1; // index of the first of our pools in m_shellPools, -1 before the game set its pools up
    static readonly IntPtr[] Owners = new IntPtr[Slots]; // item pointer per slot
    static readonly List<int> InUse = new(); // oldest first
    // The pool we built per slot. Held here as well as in the game's array: the reference keeps the pool alive for the
    // game's collector, and its root is ours to destroy.
    static readonly GameObjectPool?[] Pools = new GameObjectPool?[Slots];
    static readonly int[] Vanilla = new int[Slots]; // the game's pool a slot shows while it has none of ours

    // dump: WeaponShellManager.Setup (RVA 0x16912F0) makes the pool array and runs SetupPools (RVA 0x1690F80) once per game
    // start; the array is rebuilt with ours appended. A slot without a pool of ours shows one of the game's (its empty
    // Shell_None pool at first), so every entry is a pool the game made.
    public static void PoolsSetUp(WeaponShellManager wsm)
    {
        Array.Clear(Owners);
        Array.Clear(Pools);
        Array.Clear(Vanilla);
        InUse.Clear();
        var game = wsm.m_shellPools;
        _base = game.Length;
        var pools = new Il2CppReferenceArray<GameObjectPool>(_base + Slots);
        // Stored with the collector's write barrier, as the game's own stores (SetupPools): a pool stored without it into an array the
        // collector had already visited was freed while the array still pointed at it.
        for (int i = 0; i < _base + Slots; i++) Il2CppArrays.Store(pools, i, game[i < _base ? i : 0]);
        wsm.m_shellPools = pools;
        ForgeSkinPlugin.Info($"shells: {Slots} pools for skinned casings after the game's {_base}");
    }

    /// <summary>
    /// Gives `item` a pool of casings whose materials `look` replaces (null keeps the game's). False when the casing
    /// has none of the skin's materials or the game has no shell pools yet.
    /// </summary>
    public static bool Use(ItemEquippable item, Func<Material, Material?> look, string label)
    {
        var wsm = WeaponShellManager.Current;
        if (wsm == null || _base < 0 || item.ShellCasingData == null) return false;
        var type = item.ShellCasingData.ShellCasingType;
        GameObject? prefab = (int)type switch
        {
            1 => wsm.m_shell_9mm_prefab,
            2 => wsm.m_shell_45_ACP_prefab,
            3 => wsm.m_shell_338_prefab,
            4 => wsm.m_shell_12_Gauge_prefab,
            _ => null,
        };
        if (prefab == null) return false;
        var copy = Object.Instantiate(prefab);
        copy.name = $"ForgeSkin casing {label}";
        int swapped = 0;
        foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && look(mats[i]) is { } mine) { mats[i] = mine; changed = true; swapped++; }
            if (changed) r.sharedMaterials = mats;
        }
        if (swapped == 0)
        {
            Object.Destroy(copy);
            return false;
        }
        Release(item.Pointer);
        int slot = Array.IndexOf(Owners, IntPtr.Zero);
        if (slot < 0)
        {
            slot = InUse[0];
            Free(slot);
        }
        var pool = new GameObjectPool();
        pool.Setup(copy, Count, (GameObjectPoolType)1, $"ForgeSkin casings {label}");
        pool.GetRootObject().transform.SetParent(wsm.m_root.transform);
        foreach (var o in pool.GetAllObjects()) o.AddComponent<DisableAfterDelay>().delay = Lifetime;
        // Kept as the pool's template (a pool that grows copies it), out of sight under the pool.
        copy.transform.SetParent(pool.GetRootObject().transform, false);
        copy.SetActive(false);
        Pools[slot] = pool;
        Vanilla[slot] = (int)type;
        Il2CppArrays.Store(wsm.m_shellPools, _base + slot, pool);
        Owners[slot] = item.Pointer;
        InUse.Add(slot);
        ForgeSkinPlugin.Info($"{label}: ejected casings ({type}) skinned, {swapped} material slots, pool {_base + slot}");
        return true;
    }

    /// <summary>Frees the pool of the gun at `item` (its revert).</summary>
    public static void Release(IntPtr item)
    {
        int slot = Array.IndexOf(Owners, item);
        if (slot >= 0) Free(slot);
    }

    // The slot shows the game's pool of the gun's casing type again (an eject already queued under the slot's number
    // throws a game casing), then the root of the pool we built goes, once.
    static void Free(int slot)
    {
        Owners[slot] = IntPtr.Zero;
        InUse.Remove(slot);
        var pools = WeaponShellManager.Current?.m_shellPools;
        if (pools != null && _base >= 0 && _base + slot < pools.Length) Il2CppArrays.Store(pools, _base + slot, pools[Vanilla[slot]]);
        if (Pools[slot]?.GetRootObject() is { } root && root != null) Object.Destroy(root);
        Pools[slot] = null;
    }

    // While a gun with a pool ejects, its casing type names that pool; the game's own value goes back right after.
    public static bool Redirect(ItemEquippable item, out ShellTypes original)
    {
        original = default;
        if (_base < 0 || item.ShellCasingData == null) return false;
        int slot = Array.IndexOf(Owners, item.Pointer);
        if (slot < 0) return false;
        original = item.ShellCasingData.ShellCasingType;
        item.ShellCasingData.ShellCasingType = (ShellTypes)(_base + slot);
        return true;
    }
}

[HarmonyPatch(typeof(WeaponShellManager), nameof(WeaponShellManager.SetupPools))]
static class ShellPoolsHook
{
    static void Postfix(WeaponShellManager __instance)
    {
        try { if (NativeObjects.Is<WeaponShellManager>(__instance)) Shells.PoolsSetUp(__instance); }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-shells-setup-failed", $"shells: {e.Message}", e); }
    }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.EjectCasing))]
static class EjectCasingHook
{
    static void Prefix(BulletWeapon __instance, out (bool Redirected, ShellTypes Original) __state)
    {
        __state = default;
        if (!NativeObjects.Is<BulletWeapon>(__instance)) return;
        __state.Redirected = Shells.Redirect(__instance, out __state.Original);
    }

    static void Postfix(BulletWeapon __instance, (bool Redirected, ShellTypes Original) __state)
    {
        if (__state.Redirected) __instance.ShellCasingData.ShellCasingType = __state.Original;
    }
}
