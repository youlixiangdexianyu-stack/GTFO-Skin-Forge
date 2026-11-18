using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ForgeVfx.EditorTools
{
    /// <summary>Builds the Forge effect bundle for the game (StandaloneWindows64, Unity 2019.4.21f1) and writes the
    /// per-item source manifest beside it.
    ///
    ///     Unity.exe -batchmode -quit -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxBuild.Build
    ///         [-forgeVfxOut &lt;folder&gt;] -logFile &lt;log&gt;
    ///
    /// The default output is the repository's `Mods/ForgeCore/Assets/Vfx/`. Before anything is built every effect prefab
    /// is checked: every member of <see cref="ForgeVfxProject.Members"/> has one, it is a particle effect with a
    /// ParticleSystem at the root that loops there if and only if the member is sustained, it carries no script,
    /// every renderer draws with a shader that ships in this project, and every file it depends on is covered by a
    /// `sources.json` entry, which is what the manifest records per item. Any violation fails the build.</summary>
    public static class ForgeVfxBuild
    {
        [MenuItem("Forge VFX/Build effect bundle")]
        public static void Build() => ForgeVfxProject.RunAndExit("Forge VFX build", BuildBundle);

        private static void BuildBundle()
        {
            var output = ForgeVfxProject.Argument("-forgeVfxOut")
                ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "Mods", "ForgeCore", "Assets", "Vfx");
            var sources = Sources();
            var prefabs = ForgeVfxProject.EffectPrefabs();
            var missing = ForgeVfxProject.Members.Select(member => member.Name)
                .Except(prefabs.Select(Path.GetFileNameWithoutExtension)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("No prefab for " + string.Join(", ", missing) + " in " + ForgeVfxProject.EffectsFolder);
            var effects = prefabs.Select(path => Check(path, sources)).ToArray();

            var built = BuildBundleFile(ForgeVfxProject.BundleName, prefabs);
            Directory.CreateDirectory(output);
            var target = Path.Combine(output, ForgeVfxProject.BundleName);
            File.Copy(built, target, true);
            var bytes = File.ReadAllBytes(target);
            var manifest = new Manifest
            {
                bundle = ForgeVfxProject.BundleName,
                sha256 = Sha256(bytes),
                bytes = bytes.LongLength,
                unity = Application.unityVersion,
                target = BuildTarget.StandaloneWindows64.ToString(),
                compression = "LZ4",
                playback = "A prefab plays at the request position facing the request direction along local +Z (a line "
                    + "member faces its far end and is stretched from one metre along +Z to the far end), scaled by size "
                    + "over the member's default size (size); a sustained member loops until its player stops it, and "
                    + "an instance is recycled once no particle system under it is alive.",
                effects = effects
            };
            var json = JsonUtility.ToJson(manifest, true).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(Path.Combine(output, "forge-vfx.manifest.json"), json, new UTF8Encoding(false));
            Debug.Log("Forge VFX bundle: " + target + " (" + bytes.LongLength + " bytes, " + effects.Length + " effects)");
        }

        /// <summary>Builds one bundle of the given prefabs (each addressed by its file name) into the project's staging folder and
        /// returns the file. A shader that fails to compile is still written into the bundle, as an empty program the game draws
        /// nothing with; any shader error logged while the bundle builds fails the build instead. Shared by the member bundle
        /// above and the converted library effects (<see cref="ForgeVfxConvert"/>).</summary>
        internal static string BuildBundleFile(string bundleName, string[] prefabs)
        {
            var staging = Path.Combine(ForgeVfxProject.ProjectFolder, "Library", "ForgeVfxBuild");
            Directory.CreateDirectory(staging);
            var build = new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = prefabs,
                addressableNames = prefabs.Select(Path.GetFileNameWithoutExtension).ToArray()
            };
            var shaderErrors = new List<string>();
            Application.LogCallback watch = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && message.IndexOf("shader", StringComparison.OrdinalIgnoreCase) >= 0)
                    lock (shaderErrors) shaderErrors.Add(message);
            };
            Application.logMessageReceivedThreaded += watch;
            AssetBundleManifest result;
            try
            {
                result = BuildPipeline.BuildAssetBundles(staging, new[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode
                    | BuildAssetBundleOptions.DeterministicAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    BuildTarget.StandaloneWindows64);
            }
            finally { Application.logMessageReceivedThreaded -= watch; }
            if (shaderErrors.Count > 0)
                throw new InvalidOperationException("Shader compilation failed while building the bundle; nothing was written:\n" + string.Join("\n", shaderErrors.Distinct()));
            if (result == null) throw new InvalidOperationException("BuildAssetBundles returned no manifest.");
            var built = Path.Combine(staging, bundleName);
            if (!File.Exists(built)) throw new FileNotFoundException("The bundle was not written.", built);
            return built;
        }
        private static Effect Check(string path, Source[] sources)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var spec = ForgeVfxProject.Spec(name);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException(path + " is not a prefab.");
            var rootSystem = prefab.GetComponent<ParticleSystem>();
            if (rootSystem == null)
                throw new InvalidOperationException(path + ": the root carries the ParticleSystem the game plays and polls.");
            if (spec.Sustained && !rootSystem.main.loop)
                throw new InvalidOperationException(path + ": a sustained member loops at its root until its player stops it.");
            var systems = prefab.GetComponentsInChildren<ParticleSystem>(true);
            var layers = new List<Layer>();
            var end = 0f;
            foreach (var system in systems)
            {
                var main = system.main;
                if (main.loop && !spec.Sustained) throw new InvalidOperationException(path + ": " + system.name + " loops; only a sustained member may loop.");
                if (main.playOnAwake) throw new InvalidOperationException(path + ": " + system.name + " plays on awake; the player starts every layer.");
                var life = main.startLifetime.mode == ParticleSystemCurveMode.Constant ? main.startLifetime.constant : main.startLifetime.constantMax;
                end = Mathf.Max(end, main.startDelay.constantMax + main.duration + life);
                layers.Add(new Layer
                {
                    name = system.name == prefab.name ? "(root)" : system.name,
                    duration = main.duration,
                    lifetime = life,
                    maxParticles = main.maxParticles
                });
            }
            if (prefab.GetComponentsInChildren<MonoBehaviour>(true).Length > 0)
                throw new InvalidOperationException(path + ": carries a script component; the game cannot run project scripts.");
            var shaders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null) throw new InvalidOperationException(path + ": " + renderer.name + " has an empty material slot.");
                var shaderPath = material.shader == null ? null : AssetDatabase.GetAssetPath(material.shader);
                if (shaderPath == null || !shaderPath.StartsWith(ForgeVfxProject.Root, StringComparison.Ordinal))
                    throw new InvalidOperationException(path + ": " + material.name + " draws with " + (material.shader == null ? "no shader" : material.shader.name)
                        + ", which is not a shader of this project; the bundle carries its own shaders.");
                shaders.Add(material.shader.name);
            }
            var items = new List<Item>();
            foreach (var dependency in AssetDatabase.GetDependencies(path, true).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!dependency.StartsWith(ForgeVfxProject.Root, StringComparison.Ordinal))
                    throw new InvalidOperationException(path + " depends on " + dependency + ", outside " + ForgeVfxProject.Root);
                if (dependency.EndsWith(".cs", StringComparison.Ordinal))
                    throw new InvalidOperationException(path + " depends on the script " + dependency);
                var source = sources.Where(s => dependency.StartsWith(s.prefix, StringComparison.Ordinal))
                    .OrderByDescending(s => s.prefix.Length).FirstOrDefault()
                    ?? throw new InvalidOperationException(dependency + " has no sources.json entry; record its source and license first.");
                items.Add(new Item
                {
                    path = dependency,
                    sha256 = Sha256(File.ReadAllBytes(Path.Combine(ForgeVfxProject.ProjectFolder, dependency))),
                    source = source.name,
                    author = source.author,
                    license = source.license,
                    licenseUrl = source.licenseUrl,
                    origin = source.origin,
                    modifications = source.modifications
                });
            }
            return new Effect
            {
                member = name,
                prefab = path,
                line = spec.Line,
                sustained = spec.Sustained,
                size = spec.Size,
                seconds = end,
                layers = layers.ToArray(),
                shaders = shaders.ToArray(),
                items = items.ToArray()
            };
        }

        private static Source[] Sources()
        {
            var file = Path.Combine(ForgeVfxProject.ProjectFolder, ForgeVfxProject.SourcesFile);
            var sources = JsonUtility.FromJson<SourcesFile>(File.ReadAllText(file))?.sources;
            if (sources == null || sources.Length == 0) throw new InvalidOperationException(file + " lists no source.");
            foreach (var source in sources)
                if (string.IsNullOrEmpty(source.prefix) || string.IsNullOrEmpty(source.name) || string.IsNullOrEmpty(source.license)
                    || string.IsNullOrEmpty(source.licenseUrl) || string.IsNullOrEmpty(source.origin))
                    throw new InvalidOperationException(file + ": every source names its prefix, name, license, license URL and origin.");
            return sources;
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

#pragma warning disable 0649 // JsonUtility fills and reads these fields.
        [Serializable] private sealed class SourcesFile { public Source[] sources; }

        [Serializable]
        private sealed class Source
        {
            public string prefix, name, author, license, licenseUrl, origin, modifications;
        }

        [Serializable]
        private sealed class Manifest
        {
            public string bundle, sha256;
            public long bytes;
            public string unity, target, compression, playback;
            public Effect[] effects;
        }

        [Serializable]
        private sealed class Effect
        {
            public string member, prefab;
            public bool line, sustained;
            public float size, seconds;
            public Layer[] layers;
            public string[] shaders;
            public Item[] items;
        }

        [Serializable]
        private sealed class Layer
        {
            public string name;
            public float duration, lifetime;
            public int maxParticles;
        }

        [Serializable]
        private sealed class Item
        {
            public string path, sha256, source, author, license, licenseUrl, origin, modifications;
        }
#pragma warning restore 0649
    }
}
