using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// Pooled copies of a pack's particle prefab that the caller steers itself: a sustained jet held at a muzzle, an orb that follows a
/// projectile, a line stretched between two points, one-shot bursts. Unlike <see cref="Fx"/>'s one-shot calls the caller keeps the copy
/// (<see cref="FxInstance"/>), places it every frame, switches the prefab's layers and state children, and releases it. A copy is
/// reused once none of its particle systems is alive; tinted and untinted copies are separate. Make one pool per owner and
/// <see cref="Dispose"/> it with the owner; the library clears every pool at level end. An effect that cannot be made never takes the caller down: the error is logged once and the caller gets a copy that draws nothing.
/// </summary>
public sealed class FxInstancePool : IDisposable
{
    private static readonly List<FxInstancePool> Pools = new();
    private readonly GameObject _root;
    private readonly List<FxInstance> _instances = new();
    private bool _disposed;

    /// <summary>A pool whose copies live under a new object called <paramref name="name"/>.</summary>
    public FxInstancePool(string name)
    {
        _root = new GameObject(name);
        Object.DontDestroyOnLoad(_root);
        ForgeCore.Assets.Keep(_root);
        Pools.Add(this);
    }

    /// <summary>A free copy of <paramref name="model"/> (a prefab with a particle system at its root), made on the spot when none is
    /// free, already reserved for the caller. <paramref name="tint"/> multiplies the start colour of every layer. A copy that cannot be made (logged once) draws nothing.</summary>
    public FxInstance Take(ModelHandle model, Color? tint = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FxInstancePool));
        if (model == null) return (Failed(null) ?? FxInstance.Blank).Reserve();
        for (var index = _instances.Count - 1; index >= 0; index--)
        {
            var instance = _instances[index];
            if (!instance.Valid) { _instances.RemoveAt(index); continue; }
            if (instance.Is(model, tint) && instance.Free) return instance.Reserve();
        }
        return (Create(model, tint) ?? FxInstance.Blank).Reserve();
    }

    /// <summary>Makes <paramref name="count"/> copies, one per step (including the scan of their particle systems and lights), so a
    /// level can fill its pools before the first use without one long frame.</summary>
    public IEnumerable<object?> Prepare(ModelHandle model, int count, Color? tint = null)
    {
        if (model == null) { Failed(null); yield break; }
        for (var i = 0; i < count; i++)
        {
            if (Create(model, tint) == null) yield break;
            yield return null;
        }
    }

    private FxInstance? Create(ModelHandle model, Color? tint)
    {
        try
        {
            // A pooled copy is not reported to ForgeCore.Recording as a model: it is an effect its caller drives itself.
            var made = model.Make(Vector3.zero, Quaternion.identity, _root.transform)
                ?? throw new InvalidOperationException("ForgeVisual cannot make a copy of " + model.Name + ": its materials are not ready.");
            made.name = tint is { } color ? model.Name + "#" + color : model.Name;
            var created = new FxInstance(model, tint, made);
            _instances.Add(created);
            return created;
        }
        catch (Exception error) { return Failed(model.Name + ": " + error); }
    }

    private FxInstance? Failed(string? why)
    {
        Plugin.Logger.ErrorOnce("visual-pool-draws-nothing", "effect pool " + _root.name + " draws nothing for " + (why ?? "an effect that was not found") + ".");
        return null;
    }

    private void Tick(float now)
    {
        for (var i = 0; i < _instances.Count; i++) _instances[i].Tick(now);
    }

    internal static void TickAll(float now)
    {
        for (var i = 0; i < Pools.Count; i++) Pools[i].Tick(now);
    }

    /// <summary>Stops every copy and hides it (level end, owner reset).</summary>
    public void Clear()
    {
        foreach (var instance in _instances) if (instance.Valid) instance.Kill();
    }

    /// <summary>Destroys the pool and every copy.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pools.Remove(this);
        _instances.Clear();
        if (_root != null) Object.Destroy(_root);
    }

    internal static void ClearAll()
    {
        for (var i = 0; i < Pools.Count; i++) Pools[i].Clear();
    }
}

/// <summary>
/// One pooled copy of a prefab effect. Its layers and their names are read once; its Unity particle lights are removed, since the
/// game's clustered renderer only lights through its own point lights (<see cref="PointLights"/>). A copy taken from the pool is
/// reserved until <see cref="Start"/> or <see cref="Keep"/> plays it; a held copy stays out of the pool until <see cref="Release"/>.
/// </summary>
public sealed class FxInstance
{
    /// <summary>Child states one copy can play at once (for example an orb's <c>charging</c> and <c>charged</c>).</summary>
    private const int StateCapacity = 4;
    private readonly ModelHandle _model;
    private readonly Color? _tint;
    private readonly GameObject _object;
    private readonly Transform _transform;
    private readonly ParticleSystem _system;
    private readonly ParticleSystem[] _layers;
    private readonly string[] _names;
    /// <summary>Layers whose emission <see cref="Emit"/> switched off.</summary>
    private readonly bool[] _muted;
    /// <summary>One round of the prefab. A copy played once stops emitting after it, or a looping prefab would stay alive and out of the pool for ever (the game's build has stripped MainModule.loop, so loop cannot be read or switched off).</summary>
    private readonly float _round;
    /// <summary>When a copy played once stops emitting; 0: not due.</summary>
    private float _stopAt;
    private readonly GameObject?[] _states = new GameObject?[StateCapacity];
    private readonly string?[] _stateNames = new string?[StateCapacity];
    private bool _held, _reserved, _started;
    private string? _emitPrefix;
    private bool _emitOn;

    /// <summary>What <see cref="FxInstancePool.Take"/> hands out for a copy it could not make: every call does nothing.</summary>
    internal static readonly FxInstance Blank = new();

    private FxInstance()
    {
        _model = null!;
        _object = null!;
        _transform = null!;
        _system = null!;
        _layers = Array.Empty<ParticleSystem>();
        _names = Array.Empty<string>();
        _muted = Array.Empty<bool>();
    }

    internal FxInstance(ModelHandle model, Color? tint, GameObject gameObject)
    {
        _model = model;
        _tint = tint;
        _object = gameObject;
        _transform = gameObject.transform;
        _system = gameObject.GetComponent<ParticleSystem>() ?? throw new InvalidOperationException(model.Name + " has no ParticleSystem at its root.");
        var layers = gameObject.GetComponentsInChildren<ParticleSystem>(true);
        _layers = new ParticleSystem[layers.Length];
        _names = new string[layers.Length];
        _muted = new bool[layers.Length];
        _round = FxLayers.Longest(layers);
        for (var index = 0; index < layers.Length; index++)
        {
            var layer = _layers[index] = layers[index];
            _names[index] = layer.name;
            if (tint is { } color) Tint(layer, color);
        }
        // The game's interop exposes no particle lights module, so the module is silenced by removing the template
        // lights it copies (each on its own `<layer>_light` child); the copy spawns none of its own.
        foreach (var template in gameObject.GetComponentsInChildren<Light>(true))
            if (template != null) Object.DestroyImmediate(template.gameObject);
        _object.SetActive(false);
    }

    internal bool Is(ModelHandle model, Color? tint) => ReferenceEquals(_model, model) && Nullable.Equals(_tint, tint);

    internal bool Valid => _object != null;

    /// <summary>True when the pool may hand this copy out: not reserved, not held, and nothing of it is still alive.</summary>
    public bool Free => !_reserved && !_held && (!_started || !Valid || !_system.IsAlive(true));

    internal FxInstance Reserve()
    {
        ClearState();
        for (var index = 0; index < _layers.Length; index++) SetEmission(index, true);
        _emitPrefix = null;
        _reserved = true;
        _held = false;
        _started = false;
        _stopAt = 0f;
        return this;
    }

    internal void Tick(float now)
    {
        if (_stopAt <= 0f || now < _stopAt) return;
        _stopAt = 0f;
        if (Valid) _system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    /// <summary>Keeps the copy out of the pool until <see cref="Release"/> (a sustained effect).</summary>
    public FxInstance Hold()
    {
        _held = true;
        _stopAt = 0f;
        return this;
    }

    /// <summary>Puts the copy at a pose and a uniform scale.</summary>
    public void Place(Vector3 position, Quaternion rotation, float scale = 1f)
    {
        if (!Valid) return;
        _transform.SetPositionAndRotation(position, rotation);
        _transform.localScale = new Vector3(scale, scale, scale);
    }

    /// <summary>A line member from <paramref name="start"/> to <paramref name="end"/>: its one-metre +Z length stretched.</summary>
    public void Stretch(Vector3 start, Vector3 end)
    {
        if (!Valid) return;
        var travel = end - start;
        var length = travel.magnitude;
        _transform.SetPositionAndRotation(start, length > 0.0001f ? Quaternion.LookRotation(travel) : Quaternion.identity);
        _transform.localScale = new Vector3(1f, 1f, Mathf.Max(length, 0.001f));
    }

    /// <summary>Switches the emission of every layer whose name starts with <paramref name="prefix"/> (a repeat call with the same
    /// arguments does nothing; the game's interop can set emission but not read it, so the last call is remembered here).</summary>
    public void Emit(string prefix, bool on)
    {
        if (_emitPrefix == prefix && _emitOn == on) return;
        _emitPrefix = prefix;
        _emitOn = on;
        for (var index = 0; index < _layers.Length; index++)
            if (_names[index].StartsWith(prefix, StringComparison.Ordinal)) SetEmission(index, on);
    }

    private void SetEmission(int index, bool on)
    {
        if (_muted[index] == !on) return;
        _muted[index] = !on;
        var emission = _layers[index].emission;
        emission.enabled = on;
    }

    /// <summary>Shows the copy and plays it from the start.</summary>
    public void Start()
    {
        _started = true;
        _reserved = false;
        if (!Valid) return;
        _object.SetActive(true);
        _system.Clear(true);
        _system.Play(true);
        _stopAt = _held ? 0f : Scene.Now + Math.Max(_round, 0.01f);
    }

    /// <summary>Starts the copy when it has not been started yet; a held copy calls this every frame.</summary>
    public void Keep()
    {
        if (!_started) Start();
    }

    /// <summary>Lets a held copy go: emission stops, live particles finish, then the pool reuses it.</summary>
    public void Release()
    {
        _reserved = false; // also a copy taken and never started goes back to the pool now
        if (!_held) return;
        _held = false;
        _stopAt = 0f;
        if (_started && Valid) _system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    /// <summary>Activates and plays the child state <paramref name="child"/> (inactive in the prefab) alongside the states already
    /// playing; a state already playing restarts. A missing child, or more than four states at once, throws.</summary>
    public void PlayState(string child)
    {
        if (!Valid) return;
        var slot = -1;
        for (var index = 0; index < StateCapacity; index++)
        {
            if (string.Equals(_stateNames[index], child, StringComparison.Ordinal)) { slot = index; break; }
            if (slot < 0 && _stateNames[index] is null) slot = index;
        }
        if (slot < 0) throw new InvalidOperationException(_model.Name + " plays more than " + StateCapacity + " states.");
        var state = _transform.Find(child);
        if (state == null) throw new InvalidOperationException(_model.Name + " has no state child '" + child + "'.");
        var system = state.GetComponent<ParticleSystem>()
            ?? throw new InvalidOperationException(_model.Name + " state '" + child + "' has no ParticleSystem.");
        state.gameObject.SetActive(true);
        system.Clear(true);
        system.Play(true);
        _states[slot] = state.gameObject;
        _stateNames[slot] = child;
    }

    /// <summary>Switches every playing state child off.</summary>
    public void ClearState()
    {
        for (var index = 0; index < StateCapacity; index++)
        {
            var state = _states[index];
            _states[index] = null;
            _stateNames[index] = null;
            if (state is not null && state != null) state.SetActive(false);
        }
    }

    /// <summary>Stops and hides the copy at once.</summary>
    public void Kill()
    {
        ClearState();
        _held = _reserved = _started = false;
        _stopAt = 0f;
        if (!Valid) return;
        _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        _object.SetActive(false);
    }

    // A single particle colour is stored in the maximum; tint both ends for two-colour layers.
    private static void Tint(ParticleSystem layer, Color tint)
    {
        var main = layer.main;
        var color = main.startColor;
        color.m_ColorMin *= tint;
        color.m_ColorMax *= tint;
        main.startColor = color;
    }
}
