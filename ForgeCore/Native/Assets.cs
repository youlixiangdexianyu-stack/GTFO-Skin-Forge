using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ForgeCore;

/// <summary>Asset bundles loaded once per content: the same bytes are loaded once and the loaded bundle is shared, wherever it sits and
/// whichever mod asks, so two mods or two levels never load it twice; a missing file, a damaged bundle or a missing asset returns null and
/// logs one error under the mod's name.</summary>
public static class Assets
{
    /// <summary>A loaded bundle: its file (null for one loaded from bytes), its length and, once known, its SHA-256. Content is compared
    /// only between bundles of the same length, so a file is hashed only when another bundle has its length.</summary>
    private sealed class Loaded
    {
        internal string? File;
        internal long Length;
        internal string? Id;
        internal AssetBundle Bundle = null!;
        internal string Hash() => Id ??= FileSha256(File!);
    }

    private static readonly List<Loaded> Bundles = new();

    /// <summary>The bundle at <paramref name="path"/> (relative paths are taken from the calling mod's own folder only by the caller).</summary>
    public static AssetBundle? Load(string modId, string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return Fail<AssetBundle>(modId, "core-asset-bundle-missing", "Asset bundle " + full + " is missing.");
        try
        {
            var entry = new Loaded { File = full, Length = new FileInfo(full).Length };
            if (Find(entry) is { } loaded) return loaded;
            var bundle = AssetBundle.LoadFromFile(full);
            if (bundle == null) return Fail<AssetBundle>(modId, "core-asset-bundle-not-loaded", "Asset bundle " + full + " could not be loaded (damaged, or built for another Unity version).");
            entry.Bundle = Keep(bundle);
            Bundles.Add(entry);
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "core-asset-bundle-load-failed", "Asset bundle " + full + " could not be loaded: " + error); }
    }

    /// <summary>A bundle from bytes (an embedded resource, a shader bundle written into the DLL), loaded once per content; <paramref name="key"/>
    /// names it in errors.</summary>
    public static AssetBundle? LoadBytes(string modId, string key, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return Fail<AssetBundle>(modId, "core-asset-bundle-no-bytes", "Asset bundle " + key + " has no bytes.");
        // The bytes are gone after this call, so their hash is taken now (they are in memory and small).
        var entry = new Loaded { Length = bytes.Length, Id = Hash.Sha256Hex(bytes) };
        if (Find(entry) is { } loaded) return loaded;
        try
        {
            var bundle = AssetBundle.LoadFromMemory(bytes);
            if (bundle == null) return Fail<AssetBundle>(modId, "core-asset-bundle-bytes-not-loaded", "Asset bundle " + key + " could not be loaded from its bytes.");
            entry.Bundle = Keep(bundle);
            Bundles.Add(entry);
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "core-asset-bundle-load-failed", "Asset bundle " + key + " could not be loaded: " + error); }
    }

    /// <summary>The loaded bundle with <paramref name="wanted"/>'s content: the same file, or the same length and SHA-256.</summary>
    private static AssetBundle? Find(Loaded wanted)
    {
        Bundles.RemoveAll(static loaded => loaded.Bundle == null || loaded.Bundle.WasCollected);
        foreach (var loaded in Bundles)
        {
            if (loaded.Length != wanted.Length) continue;
            if (wanted.File != null && string.Equals(loaded.File, wanted.File, StringComparison.OrdinalIgnoreCase) || loaded.Hash() == wanted.Hash()) return loaded.Bundle;
        }
        return null;
    }

    /// <summary>The bytes of a resource embedded in ForgeCore.dll (its shader and effect bundles), read whole into one array; empty when this
    /// build lacks it, which <see cref="LoadBytes"/> reports.</summary>
    internal static byte[] Embedded(string resource)
    {
        using var stream = typeof(Assets).Assembly.GetManifestResourceStream(resource);
        if (stream == null) return Array.Empty<byte>();
        var bytes = new byte[stream.Length];
        _ = stream.Read(bytes, 0, bytes.Length); // a manifest resource is in memory and reads whole
        return bytes;
    }

    /// <summary>Starts loading the bundle at <paramref name="path"/>, and first the bundles it depends on (<see cref="Dependencies"/>), over
    /// several frames: poll <see cref="BundleLoad.IsDone"/> from a coroutine or a per-frame step. When <paramref name="expectedDigest"/> is
    /// given, the bundle's <see cref="Digest"/> must match it (any letter case). Unlike <see cref="Load"/> the bundle is not shared or kept:
    /// the caller owns it and ends it with <see cref="BundleLoad.Unload"/>. Its dependencies are shared by every bundle that names them and
    /// unloaded when the last one is.</summary>
    public static BundleLoad LoadAsync(string modId, string path, string? expectedDigest = null)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return BundleLoad.Failed(modId, "core-asset-bundle-missing", "Asset bundle " + full + " is missing.");
            var dependencies = Dependencies(full);
            foreach (var dependency in dependencies)
                if (!File.Exists(dependency)) return BundleLoad.Failed(modId, "core-asset-bundle-dependency-missing", "Asset bundle " + full + " needs " + dependency + ", which is missing.");
            if (expectedDigest != null && !string.Equals(Digest(full), expectedDigest, StringComparison.OrdinalIgnoreCase))
                return BundleLoad.Failed(modId, "core-asset-bundle-changed", "Asset bundle " + full + " or a bundle it needs changed after it was registered.");
            var shared = new SharedBundle[dependencies.Length];
            for (var i = 0; i < shared.Length; i++) shared[i] = SharedBundle.Take(dependencies[i]);
            return new BundleLoad(modId, full, AssetBundle.LoadFromFileAsync(full), shared);
        }
        catch (Exception error) { return BundleLoad.Failed(modId, "core-asset-bundle-load-failed", "Asset bundle " + path + " could not be loaded: " + error); }
    }

    /// <summary>The bundles the bundle at <paramref name="path"/> depends on, as full paths beside it, from Unity's own manifest: the
    /// manifest bundle <c>BuildPipeline.BuildAssetBundles</c> writes into its output folder under that folder's name, shipped the same way
    /// (<c>Models/Models</c> beside <c>Models/gun.bundle</c>). None when the folder has no manifest or it does not list the bundle. Read once
    /// per folder. Throws <see cref="InvalidDataException"/> when that file is not a Unity manifest.</summary>
    public static string[] Dependencies(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? "";
        var manifest = Path.Combine(folder, Path.GetFileName(folder));
        if (!Manifests.TryGetValue(manifest, out var table)) Manifests[manifest] = table = ReadManifest(manifest);
        if (!table.TryGetValue(Path.GetFileName(full), out var names)) return Array.Empty<string>();
        var paths = new string[names.Length];
        for (var i = 0; i < names.Length; i++) paths[i] = Path.Combine(folder, names[i]);
        return paths;
    }

    /// <summary>The content identity of the bundle at <paramref name="path"/> and what it needs: the SHA-256 of the file, or for a bundle
    /// with <see cref="Dependencies"/>, the SHA-256 over its own and each dependency's (name and SHA-256, in order). Throws when a file
    /// cannot be read.</summary>
    public static string Digest(string path)
    {
        var own = FileSha256(path);
        var dependencies = Dependencies(path);
        if (dependencies.Length == 0) return own;
        var text = new System.Text.StringBuilder(own);
        foreach (var dependency in dependencies) text.Append('\n').Append(Path.GetFileName(dependency)).Append(' ').Append(FileSha256(dependency));
        return Hash.Sha256Hex(text.ToString());
    }

    private static readonly Dictionary<string, Dictionary<string, string[]>> Manifests = new(StringComparer.OrdinalIgnoreCase);

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Hash.Sha256Hex(stream);
    }

    private static Dictionary<string, string[]> ReadManifest(string file)
    {
        var table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return table;
        var bundle = AssetBundle.LoadFromFile(file) ?? throw new InvalidDataException("The Unity manifest " + file + " could not be loaded.");
        try
        {
            var manifest = bundle.LoadAsset("AssetBundleManifest", Il2CppType.Of<AssetBundleManifest>())?.TryCast<AssetBundleManifest>()
                ?? throw new InvalidDataException(file + " is not a Unity manifest bundle.");
            foreach (var name in manifest.GetAllAssetBundles())
            {
                var dependencies = manifest.GetAllDependencies(name);
                var names = new string[dependencies.Length];
                for (var i = 0; i < names.Length; i++) names[i] = dependencies[i];
                table[name] = names;
            }
        }
        finally { bundle.Unload(true); }
        return table;
    }

    /// <summary>The asset <paramref name="name"/> of type <typeparamref name="T"/> in <paramref name="bundle"/>.</summary>
    public static T? Get<T>(string modId, AssetBundle bundle, string name) where T : UnityEngine.Object
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        try
        {
            var asset = bundle.LoadAsset(name, Il2CppType.Of<T>());
            var typed = asset == null ? null : ((Il2CppObjectBase)asset).TryCast<T>();
            if (typed != null)
            {
                Keep(typed);
                return typed;
            }
            return Fail<T>(modId, "core-asset-not-in-bundle", "Asset " + name + " (" + typeof(T).Name + ") is not in bundle " + bundle.name + ".");
        }
        catch (Exception error) { return Fail<T>(modId, "core-asset-read-failed", "Asset " + name + " could not be read from bundle " + bundle.name + ": " + error); }
    }

    /// <summary>Marks a loaded asset (or an object a mod keeps, such as a texture, mesh or pooled root) so the game's unused-asset
    /// sweep, which runs on every scene change, never unloads it. Returns the asset so a load can be written as one expression.</summary>
    public static T Keep<T>(T asset) where T : UnityEngine.Object
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
    }

    internal static T? Fail<T>(string modId, string code, string message) where T : class
    {
        Log.For(modId).ErrorOnce(code, message);
        return null;
    }
}

/// <summary>A bundle <see cref="Assets.LoadAsync"/> is loading: <see cref="IsDone"/> turns true when the load and its dependencies' ended, then
/// <see cref="Bundle"/> holds the bundle, or null when it or a dependency failed (one error is logged under the mod's name).</summary>
public sealed class BundleLoad
{
    private readonly string _modId, _path;
    private AssetBundleCreateRequest? _request;
    private AssetBundle? _bundle;
    private SharedBundle[] _dependencies;

    internal BundleLoad(string modId, string path, AssetBundleCreateRequest request, SharedBundle[] dependencies)
    { _modId = modId; _path = path; _request = request; _dependencies = dependencies; }

    private BundleLoad(string modId, string code, string message)
    { Assets.Fail<AssetBundle>(modId, code, message); _modId = modId; _path = ""; _dependencies = Array.Empty<SharedBundle>(); }

    internal static BundleLoad Failed(string modId, string code, string message) => new(modId, code, message);

    public bool IsDone
    {
        get
        {
            if (_request == null) return true;
            if (!_request.isDone) return false;
            foreach (var dependency in _dependencies) if (!dependency.IsDone) return false;
            _bundle = _request.assetBundle;
            _request = null;
            if (_bundle == null)
            {
                Assets.Fail<AssetBundle>(_modId, "core-asset-bundle-not-loaded", "Asset bundle " + _path + " could not be loaded (damaged, or built for another Unity version).");
                Unload();
                return true;
            }
            foreach (var dependency in _dependencies)
                if (dependency.Bundle == null)
                {
                    Assets.Fail<AssetBundle>(_modId, "core-asset-bundle-dependency-not-loaded", "Asset bundle " + _path + " needs " + dependency.Path + ", which could not be loaded.");
                    Unload();
                    break;
                }
            return true;
        }
    }

    public AssetBundle? Bundle => IsDone ? _bundle : null;

    /// <summary>Starts loading the asset <paramref name="name"/> from the bundle or, when it is not there, from the dependency that holds it.</summary>
    public AssetBundleRequest LoadAssetAsync(string name, Il2CppSystem.Type type)
    {
        var bundle = Bundle ?? throw new InvalidOperationException("Asset bundle " + _path + " is not loaded.");
        if (!bundle.Contains(name))
            foreach (var dependency in _dependencies)
                if (dependency.Bundle!.Contains(name)) return dependency.Bundle.LoadAssetAsync(name, type);
        return bundle.LoadAssetAsync(name, type);
    }

    /// <summary>Once <see cref="IsDone"/>: unloads the bundle with every asset taken from it, and lets go of its dependencies (each unloads
    /// with the last bundle that needs it).</summary>
    public void Unload()
    {
        _bundle?.Unload(true);
        _bundle = null;
        foreach (var dependency in _dependencies) dependency.Release();
        _dependencies = Array.Empty<SharedBundle>();
    }
}

/// <summary>A dependency bundle loaded once for every bundle that needs it, and unloaded when the last of them lets go.</summary>
internal sealed class SharedBundle
{
    private static readonly Dictionary<string, SharedBundle> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private AssetBundleCreateRequest? _request;
    private int _users;

    private SharedBundle(string path) { Path = path; _request = AssetBundle.LoadFromFileAsync(path); }

    internal string Path { get; }
    internal AssetBundle? Bundle { get; private set; }

    internal static SharedBundle Take(string path)
    {
        if (!Loaded.TryGetValue(path, out var shared)) Loaded.Add(path, shared = new SharedBundle(path));
        shared._users++;
        return shared;
    }

    internal bool IsDone
    {
        get
        {
            if (_request == null) return true;
            if (!_request.isDone) return false;
            Bundle = _request.assetBundle;
            _request = null;
            return true;
        }
    }

    // Only a finished BundleLoad lets go, and it finishes after its dependencies, so this load has ended.
    internal void Release()
    {
        if (--_users > 0) return;
        Loaded.Remove(Path);
        Bundle?.Unload(true);
        Bundle = null;
    }
}
