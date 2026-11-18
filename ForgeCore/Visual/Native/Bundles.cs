using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// Asset bundles of models and effects, registered under a pack name: <c>Bundles.Register(modId, "Arsenal", path)</c>, after which
/// every model in it is <c>Arsenal:prefab</c> to any mod (<see cref="Models.Get"/>). The bundle is loaded through
/// <c>ForgeCore.Assets</c> (one load per file, shared). Registering reads only the bundle's names: a model's prefab is loaded the first
/// time it is read (<see cref="ModelHandle.Prefab"/>), spawned or made ready (<see cref="ModelHandle.Ready"/>, the way to preload it while
/// a level is built). A bundle with a model profile (the Forge model pipeline's <c>ordnance-profile</c>) has each model's materials rebuilt
/// with the game's gear shader, the way the game's own gear is shaded; that shader exists once a level has been built, so a model is
/// dressed on its first spawn or <see cref="ModelHandle.Ready"/> after that, and only the surfaces it uses are made.
/// </summary>
public static class Bundles
{
    private static readonly Dictionary<string, BundleHandle> Packs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the bundle file at <paramref name="path"/> as <paramref name="pack"/>; a relative path is taken from the folder
    /// of the calling mod's DLL. Null (logged once under <paramref name="modId"/>) when the name is taken or bad, the file is missing or
    /// damaged, or its profile is unreadable.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static BundleHandle? Register(string modId, string pack, string path)
    {
        if (string.IsNullOrEmpty(path)) return Fail(modId, pack, "no bundle path was given");
        if (!Path.IsPathRooted(path))
        {
            var folder = Path.GetDirectoryName(System.Reflection.Assembly.GetCallingAssembly().Location);
            if (folder != null) path = Path.Combine(folder, path);
        }
        if (Refuse(modId, pack) is { } why) return Fail(modId, pack, why);
        var bundle = ForgeCore.Assets.Load(modId, path);
        return bundle == null ? Fail(modId, pack, "the bundle " + path + " could not be loaded (see the error above)") : Adopt(modId, pack, bundle, path);
    }

    /// <summary>Registers a bundle held in memory (embedded in a DLL) as <paramref name="pack"/>.</summary>
    public static BundleHandle? RegisterBytes(string modId, string pack, string key, byte[] bytes)
    {
        if (Refuse(modId, pack) is { } why) return Fail(modId, pack, why);
        var bundle = ForgeCore.Assets.LoadBytes(modId, key, bytes);
        return bundle == null ? Fail(modId, pack, "the embedded bundle " + key + " could not be loaded (see the error above)") : Adopt(modId, pack, bundle, key);
    }

    /// <summary>This library's own effect bundle, registered under the reserved pack <c>Forge</c> from the bytes embedded in the DLL.</summary>
    internal static BundleHandle? RegisterBuiltIn(string key, byte[] bytes)
    {
        if (Refuse(Plugin.Id, VisualNames.Forge, builtIn: true) is { } why) return Fail(Plugin.Id, VisualNames.Forge, why);
        var bundle = ForgeCore.Assets.LoadBytes(Plugin.Id, key, bytes);
        return bundle == null ? Fail(Plugin.Id, VisualNames.Forge, "the embedded bundle " + key + " could not be loaded (see the error above)")
            : Adopt(Plugin.Id, VisualNames.Forge, bundle, key);
    }

    /// <summary>The bundle registered as <paramref name="pack"/>, if any.</summary>
    public static bool TryGet(string pack, out BundleHandle bundle)
    {
        EffectBundle.Ensure(pack);
        return Packs.TryGetValue(pack ?? "", out bundle!);
    }

    /// <summary>Every registered pack name.</summary>
    public static IReadOnlyCollection<string> Names => Packs.Keys;

    internal static bool Known(string pack) => Packs.ContainsKey(pack);

    private static string? Refuse(string modId, string pack, bool builtIn = false)
    {
        if (string.IsNullOrEmpty(modId)) return "no mod ID was given";
        if (VisualNames.CheckPack(pack, builtIn) is { } bad) return bad;
        return Packs.TryGetValue(pack, out var taken) ? "pack " + pack + " is already registered by " + taken.ModId : null;
    }

    private static BundleHandle? Adopt(string modId, string pack, AssetBundle bundle, string source)
    {
        ModelProfile? profile = null;
        if (bundle.Contains(ModelProfile.AssetName))
        {
            var text = ForgeCore.Assets.Get<TextAsset>(modId, bundle, ModelProfile.AssetName);
            if (text == null) return Fail(modId, pack, "its model profile could not be read");
            profile = ModelProfile.Read(text.text, out var why);
            if (profile == null) return Fail(modId, pack, why!);
        }
        var handle = new BundleHandle(modId, pack, bundle, source, profile);
        if (handle.Failure != null) return Fail(modId, pack, handle.Failure);
        Packs.Add(pack, handle);
        Models.Index(handle);
        return handle;
    }

    private static BundleHandle? Fail(string modId, string pack, string why)
    {
        ForgeCore.Log.For(string.IsNullOrEmpty(modId) ? Plugin.Id : modId).ErrorOnce("visual-pack-not-registered", "ForgeVisual pack " + pack + " was not registered: " + why + ".");
        return null;
    }
}

/// <summary>One registered bundle. Its models are <see cref="ModelHandle"/>s (<see cref="Models.Get"/>), its standalone textures
/// and game-shaded surfaces can be read by name. Everything taken from it is kept across level changes.</summary>
public sealed class BundleHandle
{
    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap"), EnableEmissive = Shader.PropertyToID("_EnableEmissive");

    private readonly ModelProfile? _profile;
    // Unity lowercases the asset paths of a bundle built without addressable names (Assets/X/FramesFx.prefab is listed as
    // assets/x/framesfx.prefab), so a bundle without a profile names its models in lower case; asking by the prefab's own name must find it.
    private readonly Dictionary<string, ModelHandle> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelProfile.Prefab> _dressing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Material> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _textureNames = new(StringComparer.Ordinal);
    private bool _shaderReady;

    /// <summary>Reads the bundle's names only; no prefab, texture or material is loaded here.</summary>
    internal BundleHandle(string modId, string pack, AssetBundle bundle, string source, ModelProfile? profile)
    {
        ModId = modId;
        Pack = pack;
        Bundle = bundle;
        Source = source;
        _profile = profile;
        if (profile != null)
        {
            foreach (var p in profile.Prefabs)
            {
                if (!bundle.Contains(p.name)) { Failure = "the profile lists the prefab " + p.name + " but the bundle has none"; return; }
                _models.Add(p.name, new ModelHandle(this, VisualNames.Join(pack, p.name), p.name));
                _dressing.Add(p.name, p);
            }
            foreach (var name in profile.Textures) _textureNames.Add(name);
        }
        else
        {
            foreach (var asset in bundle.GetAllAssetNames())
            {
                // A bundle built with addressable names lists them without an extension; either form names a prefab.
                var extension = Path.GetExtension(asset);
                if (extension.Length != 0 && !extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                var item = Path.GetFileNameWithoutExtension(asset);
                if (VisualNames.CheckItem(item) != null || _models.ContainsKey(item)) continue;
                _models.Add(item, new ModelHandle(this, VisualNames.Join(pack, item), asset));
            }
        }
    }

    public string ModId { get; }
    public string Pack { get; }
    public AssetBundle Bundle { get; }
    public string Source { get; }

    internal string? Failure { get; }
    internal IEnumerable<ModelHandle> AllModels => _models.Values;

    /// <summary>Any asset of the bundle by name, through <c>ForgeCore.Assets.Get</c> (null and logged once when it is missing).</summary>
    public T? Get<T>(string name) where T : Object => ForgeCore.Assets.Get<T>(ModId, Bundle, name);

    /// <summary>The model <paramref name="prefab"/> of this bundle (<c>Pack:prefab</c> through <see cref="Models.Get"/> is the same).</summary>
    public bool TryModel(string prefab, out ModelHandle model) => _models.TryGetValue(prefab, out model!);

    /// <summary>A standalone texture the profile lists (effect sprites and atlases), or any texture of a bundle without a profile, loaded
    /// the first time it is asked for. False (logged once) when the bundle does not have it. Shared; do not change it.</summary>
    public bool TryTexture(string name, out Texture2D texture)
    {
        if (_profile == null)
        {
            texture = Bundle.Contains(name) ? Get<Texture2D>(name)! : null!;
            return texture != null;
        }
        if (_textures.TryGetValue(name, out texture!)) return true;
        if (!_textureNames.Contains(name) || Get<Texture2D>(name) is not { } loaded) return false;
        _textures[name] = texture = loaded;
        return true;
    }

    /// <summary>Whether this bundle's models can be dressed and spawned: false (logged once) before a level has been built, when the game's
    /// gear shader does not exist yet. A bundle without a profile is always ready. Each model's materials are made when it is first
    /// spawned or made ready (<see cref="ModelHandle.Ready"/>).</summary>
    public bool Prepare()
    {
        if (_shaderReady || _profile == null) return true;
        if (Shader.Find(LitMaterial.ShaderName) != null) return _shaderReady = true;
        ForgeCore.Log.For(ModId).ErrorOnce("visual-pack-games-gear-shader-not-loaded", "ForgeVisual pack " + Pack + ": the game's gear shader is not loaded yet; its models show once a level has been built.");
        return false;
    }

    /// <summary>Puts the profile's materials, rebuilt with the game's gear shader, on the prefab of model <paramref name="item"/>; once per
    /// model, on the prefab, so every instance inherits them. Makes only the surfaces this model uses (each once for the bundle). Throws
    /// when the bundle lacks a listed renderer or texture. Call after <see cref="Prepare"/>.</summary>
    internal void Dress(string item, GameObject prefab)
    {
        if (!_dressing.TryGetValue(item, out var p)) return;
        foreach (var r in p.renderers)
        {
            var renderer = prefab.transform.Find(r.name)?.GetComponent<Renderer>()
                ?? throw new InvalidDataException("the prefab " + p.name + " has no renderer " + r.name);
            var materials = new Material[r.surfaces.Length];
            for (var i = 0; i < materials.Length; i++) materials[i] = Surface(r.surfaces[i]);
            renderer.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
        }
    }

    /// <summary>Whether the profile lists <paramref name="path"/> (from the prefab's root, '/'-joined) as a renderer or anchor of model
    /// <paramref name="item"/>: the bundle's own name table, written and checked when it was built. False says nothing (a profile need not
    /// list every node, and a bundle without one lists none).</summary>
    internal bool Lists(string item, Func<string, bool> path)
    {
        if (!_dressing.TryGetValue(item, out var p)) return false;
        foreach (var r in p.renderers) if (path(r.name)) return true;
        foreach (var anchor in p.anchors) if (path(anchor)) return true;
        return false;
    }

    private Material Surface(string key)
    {
        if (_surfaces.TryGetValue(key, out var made)) return made;
        ModelProfile.Surface? s = null;
        foreach (var candidate in _profile!.Surfaces) if (candidate.key == key) { s = candidate; break; }
        if (s == null) throw new InvalidDataException("the profile has no surface " + key);
        var material = ForgeCore.Assets.Keep(LitMaterial.Create(Pack + " " + s.key, Require<Texture2D>(s.albedo), Require<Texture2D>(s.metallic), false, 0f, out var why)
            ?? throw new InvalidOperationException(why));
        if (s.normal.Length != 0) material.SetTexture(BumpMap, Require<Texture2D>(s.normal));
        if (s.Emissive)
            Glow.Write(material, new Color(s.emissionColor[0], s.emissionColor[1], s.emissionColor[2], 1f), Require<Texture2D>(s.emission));
        else material.SetFloat(EnableEmissive, 0f);
        return _surfaces[key] = material;
    }

    private T Require<T>(string name) where T : Object => ForgeCore.Assets.Keep(Get<T>(name) ?? throw new InvalidDataException("the bundle has no " + typeof(T).Name + " " + name));

}
