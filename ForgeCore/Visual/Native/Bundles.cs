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
/// <c>ForgeCore.Assets</c> (one load per file, shared). A bundle with a model profile (the Forge model pipeline's
/// <c>ordnance-profile</c>) has its materials rebuilt with the game's gear shader, the way the game's own gear is shaded; that shader
/// exists once a level has been built, so the rebuild happens on the first spawn (or <see cref="BundleHandle.Prepare"/>) after that.
/// Names and prefabs can be read before.
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
    private readonly Dictionary<string, Material> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private bool _prepared, _prepareFailed;

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
                var prefab = ForgeCore.Assets.Get<GameObject>(modId, bundle, p.name);
                if (prefab == null) { Failure = "the profile lists the prefab " + p.name + " but the bundle has none"; return; }
                _models.Add(p.name, new ModelHandle(this, VisualNames.Join(pack, p.name), prefab));
            }
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
                var prefab = ForgeCore.Assets.Get<GameObject>(modId, bundle, asset);
                if (prefab != null) _models.Add(item, new ModelHandle(this, VisualNames.Join(pack, item), prefab));
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

    /// <summary>A standalone texture the profile lists (effect sprites and atlases), or any texture of a bundle without a profile.
    /// Shared; do not change it.</summary>
    public bool TryTexture(string name, out Texture2D texture)
    {
        if (_profile == null)
        {
            texture = Bundle.Contains(name) ? Get<Texture2D>(name)! : null!;
            return texture != null;
        }
        Prepare();
        return _textures.TryGetValue(name, out texture!);
    }

    /// <summary>Rebuilds the profile's materials with the game's gear shader and puts them on the prefabs, once. False (logged once)
    /// before a level has been built, when the gear shader does not exist yet, or when the bundle lacks a listed asset. A bundle
    /// without a profile is always ready.</summary>
    public bool Prepare()
    {
        if (_prepared) return true;
        if (_profile == null) return _prepared = true;
        if (_prepareFailed) return false;
        if (Shader.Find(LitMaterial.ShaderName) == null)
        {
            ForgeCore.Log.For(ModId).ErrorOnce("visual-pack-games-gear-shader-not-loaded", "ForgeVisual pack " + Pack + ": the game's gear shader is not loaded yet; its models show once a level has been built.");
            return false;
        }
        try
        {
            foreach (var s in _profile.Surfaces)
            {
                var material = ForgeCore.Assets.Keep(LitMaterial.Create(Pack + " " + s.key, Require<Texture2D>(s.albedo), Require<Texture2D>(s.metallic), false, 0f, out var why)
                    ?? throw new InvalidOperationException(why));
                if (s.normal.Length != 0) material.SetTexture(BumpMap, Require<Texture2D>(s.normal));
                if (s.Emissive)
                    Glow.Write(material, new Color(s.emissionColor[0], s.emissionColor[1], s.emissionColor[2], 1f), Require<Texture2D>(s.emission));
                else material.SetFloat(EnableEmissive, 0f);
                _surfaces[s.key] = material;
            }
            foreach (var p in _profile.Prefabs)
            {
                var prefab = _models[p.name].Prefab;
                foreach (var r in p.renderers)
                {
                    var renderer = prefab.transform.Find(r.name)?.GetComponent<Renderer>()
                        ?? throw new InvalidDataException("the prefab " + p.name + " has no renderer " + r.name);
                    var materials = new Material[r.surfaces.Length];
                    for (var i = 0; i < materials.Length; i++) materials[i] = _surfaces[r.surfaces[i]];
                    // Prepared once on the prefab; every instance inherits these shared materials.
                    renderer.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
                }
            }
            foreach (var name in _profile.Textures) _textures[name] = Require<Texture2D>(name);
            return _prepared = true;
        }
        catch (Exception error)
        {
            _prepareFailed = true;
            ForgeCore.Log.For(ModId).ErrorOnce("visual-pack-could-not-prepare-materials-models", "ForgeVisual pack " + Pack + " could not prepare its materials; its models are not shown: " + error);
            return false;
        }
    }

    private T Require<T>(string name) where T : Object => ForgeCore.Assets.Keep(Get<T>(name) ?? throw new InvalidDataException("the bundle has no " + typeof(T).Name + " " + name));

}
