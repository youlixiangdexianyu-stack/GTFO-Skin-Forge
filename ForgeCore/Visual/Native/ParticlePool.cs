using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// A pack's particle systems authored in Unity: one prefab whose children are world-space, looping particle systems with no emission
/// of their own (the game's interop strips the module setters, so shape, speed, size curve, colour curve and texture sheet are fixed
/// in the prefab). Registered by name (<c>Pack:fx</c>), it is made once (<see cref="Warm"/> or the first emission) and kept for the
/// session; each child is a <see cref="ParticleChannel"/> that moves its system and emits into it. A system is its own pool: its
/// maximum particle count is the budget of everything emitting into it. Nothing allocates after the first use.
/// </summary>
public sealed class ParticlePool
{
    private static readonly NameTable<ParticlePool> All = new();
    private static readonly List<ParticlePool> Pools = new();
    private readonly GameObject _prefab;
    private readonly List<ParticleChannel> _channels = new();
    private GameObject? _holder;
    private bool _failed;

    private ParticlePool(string modId, string name, GameObject prefab)
    {
        ModId = modId;
        Name = name;
        _prefab = prefab;
    }

    public string ModId { get; }
    public string Name { get; }

    /// <summary>Registers <paramref name="prefab"/> as <paramref name="name"/> (<c>Pack:fx</c>); null (logged once) when the name is
    /// bad or taken.</summary>
    public static ParticlePool? Register(string modId, string name, GameObject prefab)
    {
        var log = ForgeCore.Log.For(string.IsNullOrEmpty(modId) ? Plugin.Id : modId);
        if (prefab == null) { log.ErrorOnce("visual-particle-pool-not-registered-no-prefab", "ForgeVisual particle pool " + name + " not registered: no prefab."); return null; }
        if (VisualNames.TrySplit(name, out var pack, out _, out _) && VisualNames.CheckPack(pack) is { } bad) { log.ErrorOnce("visual-particle-pool-not-registered", "ForgeVisual particle pool " + name + " not registered: " + bad + "."); return null; }
        var pool = new ParticlePool(modId, name, prefab);
        if (All.Add(name, pool) is { } why) { log.ErrorOnce("visual-particle-pool-not-registered", "ForgeVisual particle pool " + name + " not registered: " + why + "."); return null; }
        Pools.Add(pool);
        return pool;
    }

    /// <summary>Registers the prefab <paramref name="item"/> of a registered bundle as <c>Pack:item</c>.</summary>
    public static ParticlePool? Register(BundleHandle bundle, string item)
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        if (!bundle.TryModel(item, out var model))
        {
            ForgeCore.Log.For(bundle.ModId).ErrorOnce("visual-particle-pool-not-registered-bundle", "ForgeVisual particle pool " + bundle.Pack + ":" + item + " not registered: the bundle has no prefab " + item + ".");
            return null;
        }
        return Register(bundle.ModId, model.Name, model.Prefab);
    }

    /// <summary>The pool <paramref name="name"/>, or null (logged once).</summary>
    public static ParticlePool? Get(string name)
    {
        if (All.TryGet(name, out var pool)) return pool;
        if (Missing.First("particle pool", name)) Plugin.Logger.ErrorOnce("visual-has-no-particle-pool-particle-pool", "ForgeVisual has no particle pool " + name + ": " + Missing.Why(name, Bundles.Known, "particle pool") + ".");
        return null;
    }

    /// <summary>The child system <paramref name="child"/>; resolve once at setup and keep the channel. <paramref name="capacity"/> is
    /// the maxParticles the prefab was built with (the game's interop does not expose it): when that many are alive, priority-0
    /// emissions are skipped and counted; 0 leaves every emission to Unity's own oldest-first recycling. Null (logged once) when the
    /// prefab has no such system.</summary>
    public ParticleChannel? Channel(string child, int capacity = 0)
    {
        foreach (var c in _channels)
            if (c.Child == child) return c;
        var t = _prefab.transform.Find(child);
        if (t == null || t.GetComponent<ParticleSystem>() == null)
        {
            ForgeCore.Log.For(ModId).ErrorOnce("visual-particle-pool-has-no-particle-system", "ForgeVisual particle pool " + Name + " has no particle system " + child + ".");
            return null;
        }
        var channel = new ParticleChannel(this, child, Math.Max(0, capacity));
        _channels.Add(channel);
        if (_holder != null && !_holder.WasCollected) channel.Bind(_holder.transform);
        return channel;
    }

    /// <summary>Makes the systems now (at level load), so the first effect creates nothing. False (logged once) when it fails.</summary>
    public bool Warm() => Ensure();

    internal bool Ensure()
    {
        if (_holder != null && !_holder.WasCollected) return true;
        if (_failed) return false;
        try
        {
            var holder = Object.Instantiate(_prefab);
            holder.name = "ForgeVisual " + Name;
            Object.DontDestroyOnLoad(holder);
            ForgeCore.Assets.Keep(holder);
            holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _holder = holder;
            foreach (var channel in _channels) channel.Bind(holder.transform);
            return true;
        }
        catch (Exception error)
        {
            _failed = true;
            ForgeCore.Log.For(ModId).Error("visual-particle-pool-could-not-be-made", "ForgeVisual particle pool " + Name + " could not be made; it draws nothing: " + error);
            return false;
        }
    }

    internal static void ClearAll()
    {
        foreach (var pool in Pools)
            foreach (var channel in pool._channels) channel.Clear();
    }
}

/// <summary>One particle system of a <see cref="ParticlePool"/>.</summary>
public sealed class ParticleChannel
{
    private readonly ParticlePool _pool;
    private ParticleSystem? _system;
    private ParticleSystemRenderer? _renderer;
    private Material? _look;
    private readonly int _capacity;

    internal ParticleChannel(ParticlePool pool, string child, int capacity)
    {
        _pool = pool;
        _capacity = capacity;
        Child = child;
    }

    public string Child { get; }

    /// <summary>The system's particle budget given at <see cref="ParticlePool.Channel"/> (0: not given).</summary>
    public int Capacity => _capacity;

    /// <summary>Particles alive now.</summary>
    public int Live => Usable() ? _system!.particleCount : 0;

    /// <summary>One particle at <paramref name="position"/> moving at <paramref name="velocity"/>, of <paramref name="size"/> metres,
    /// living <paramref name="life"/> seconds, tinted <paramref name="colour"/>. When the system is full a priority above 0 recycles
    /// its oldest particle (Unity's own rule); priority 0 is skipped.</summary>
    public void Emit(Vector3 position, Vector3 velocity, float size, float life, Color colour, int priority = 1)
    {
        if (!Admit(priority)) return;
        _system!.Emit(position, velocity, size, life, colour);
    }

    /// <summary><paramref name="count"/> particles from the system's own shape, placed at <paramref name="position"/> turned by
    /// <paramref name="rotation"/>, with the sizes, speeds and colours the prefab gives.</summary>
    public void Burst(Vector3 position, Quaternion rotation, int count, int priority = 1)
    {
        if (count <= 0 || !Admit(priority)) return;
        _system!.transform.SetPositionAndRotation(position, rotation);
        _system.Emit(count);
    }

    /// <summary>Draws this system with <paramref name="texture"/> through ForgeVisual's particle shader (alpha or additive), instead
    /// of the material the prefab was built with. False (logged once) when the shaders are missing.</summary>
    public bool Look(Texture texture, FxBlend blend)
    {
        _look = Shaders.Make(texture, blend, _pool.Name + "/" + Child);
        if (_look == null) return false;
        if (_renderer != null && !_renderer.WasCollected) _renderer.sharedMaterial = _look;
        return true;
    }

    /// <summary>True when the system can be emitted into: its native object is alive (a destroyed or collected one makes the native
    /// Emit fault the process) and its object is active (the pool's prefab contract; a system that never simulates is not drawn).</summary>
    private bool Usable()
    {
        var system = _system;
        return !ForgeCore.NativeObjects.Gone(system)
            && system!.gameObject != null && system.gameObject.activeInHierarchy;
    }

    private bool Admit(int priority)
    {
        if (!Usable() && (!_pool.Ensure() || !Usable()))
        {
            ForgeCore.Log.For(_pool.ModId).ErrorOnce("visual-particle-system-destroyed-missing-or-inactive", "ForgeVisual particle system " + _pool.Name + "/" + Child + " is destroyed, missing or inactive; its effects are skipped (rebuild the pack's bundle if the prefab is not active).");
            return false;
        }
        // Below its budget every request is taken. At the budget a request of priority above 0 is taken too (Unity recycles the oldest particle of a
        // full system, so the pool never grows past its capacity); priority 0 is refused and the caller counts it. A capacity of 0 means no budget was given.
        var live = _system!.particleCount;
        return _capacity <= 0 || live < _capacity || priority > 0;
    }

    internal void Bind(Transform holder)
    {
        var t = holder.Find(Child);
        _system = t != null ? t.GetComponent<ParticleSystem>() : null;
        _renderer = t != null ? t.GetComponent<ParticleSystemRenderer>() : null;
        if (_system == null) return;
        if (_look != null && _renderer != null) _renderer.sharedMaterial = _look;
        _system.Play();
    }

    internal void Clear()
    {
        if (Usable()) _system!.Clear(true);
    }
}
