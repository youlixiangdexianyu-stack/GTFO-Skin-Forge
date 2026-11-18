using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVfx.EditorTools
{
    /// <summary>Converts effects staged from the Unity 6 effect library (`Tools/Vfx/FxLibrary/fx_import.py`) into Forge effect prefabs for
    /// the game (Unity 2019.4 built-in pipeline), builds them into one bundle and renders them for review. Batch entries, all
    /// reading `Assets/ForgeVfx/Library/_manifest/*.json`:
    ///
    ///     Unity.exe -batchmode -quit -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxConvert.Convert
    ///         [-forgeVfxEffects slug,slug] [-forgeVfxOut &lt;folder&gt;] -logFile &lt;log&gt;
    ///     ... ForgeVfxConvert.BuildBundle   (all converted prefabs into `forge-vfx-library.bundle` + manifest in the out folder)
    ///     ... ForgeVfxConvert.Preview       (loads that bundle from disk and renders each effect: `&lt;slug&gt;.after.png`)
    ///
    /// Convert replaces every material of a library prefab by one of the three Forge particle shaders (additive, alpha-blended,
    /// premultiplied), chosen from the blend mode the library material had, with its main texture, tiling and tint; strips scripts,
    /// lights and VFX Graph components; makes sure the root carries the ParticleSystem the game plays; and records for each
    /// material what the Forge shaders cannot reproduce (dissolve, noise, distortion, soft particles ...) in
    /// `&lt;slug&gt;.convert.json`. Nothing is silently dropped: every removed component and every lost property is in that report.</summary>
    public static class ForgeVfxConvert
    {
        internal const string LibraryRoot = "Assets/ForgeVfx/Library/";
        internal const string ConvertedRoot = "Assets/ForgeVfx/Converted/";
        internal const string BundleName = "forge-vfx-library.bundle";
        private const string AdditiveShader = "ForgeVfx/Particles Additive", AlphaShader = "ForgeVfx/Particles Alpha Blended",
            PremultipliedShader = "ForgeVfx/Particles Premultiplied", UberShader = "ForgeVfx/Particles Uber", DistortShader = "ForgeVfx/Particles Distort";
        /// <summary>The ports of the PRO Effects (Knife) shaders, staged by fx_import from the local input Source/vfx-knife-ports (never in Git).</summary>
        private const string KnifePrefix = "ForgeVfx/Knife/";
        /// <summary>The port of Hovl's HS_ShockWave graph (flow distortion driven by the library's custom data).</summary>
        private const string FlowShader = "ForgeVfx/Particles Flow";

        /// <summary>A ported library shader (Knife, Flow): it reads the library's vertex streams and custom data and keeps its own look.</summary>
        private static bool IsPort(Material m) => m != null && (m.shader.name.StartsWith(KnifePrefix, StringComparison.Ordinal) || m.shader.name == FlowShader);

        private static string Out => ForgeVfxProject.Argument("-forgeVfxOut")
            ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "artifacts", "work", "ordnance-next", "vfx", "convert");

        [MenuItem("Forge VFX/Convert staged library effects")]
        public static void Convert() => ForgeVfxProject.RunAndExit("Forge VFX convert", ConvertAll);

        [MenuItem("Forge VFX/Build library effect bundle")]
        public static void BuildBundle() => ForgeVfxProject.RunAndExit("Forge VFX library bundle", BuildLibraryBundle);

        [MenuItem("Forge VFX/Render library effect previews")]
        public static void Preview() => ForgeVfxProject.RunAndExit("Forge VFX library preview", RenderLibrary);

        // ---- the library manifests (written by fx_import.py) ----

#pragma warning disable 0649 // JsonUtility fills these fields.
        [Serializable] internal sealed class TextureInfo { public string prop, path; public float[] scale, offset; }

        [Serializable] internal sealed class UberFloat { public string prop; public float value; }
        [Serializable] internal sealed class UberColor { public string prop; public float[] rgba; }

        /// <summary>How fx_import.py mapped a custom library shader onto the Forge Uber (or Distort) shader: the shader to use, the extra
        /// textures, floats, colours and keywords. Null for a plain texture-times-tint material.</summary>
        [Serializable]
        internal sealed class UberInfo
        {
            public string shader;
            public TextureInfo[] textures;
            public UberFloat[] floats;
            public UberColor[] colors;
            public string[] keywords;
            /// <summary>The layer has no picture of its own (a Knife bolt, rim or dissolving colour): its main texture stays the shader's white.</summary>
            public bool solid;
            /// <summary>A faithful port of the library shader (ForgeVfx/Knife/..., ForgeVfx/Particles Flow): its renderer keeps the library's vertex streams and custom data, which the shader reads,
            /// and a plain mesh drawn with it is part of the effect, not an anchor.</summary>
            public bool port;
        }

        /// <summary>One layer of a composed effect (fx_import.stage_compose): a converted prefab placed under the composed root.</summary>
        [Serializable]
        internal sealed class ComposePart
        {
            public string slug;
            public float delay, scale = 1f;
            public float[] offset, rotate;
        }

        [Serializable]
        internal sealed class MaterialInfo
        {
            public string guid, path, name, shader, blend, blendWhy;
            public TextureInfo mainTexture;
            public float[] tint;
            public string[] lost;
            public UberInfo uber;
            /// <summary>The library material's own soft-particle switch (fx_import.soft_of); fade 0 when it has none.</summary>
            public SoftInfo soft;
        }

        /// <summary>Soft particles as the library material fades: per metre, or per unit of 1 / depth with inverse (a raw depth difference).</summary>
        [Serializable] internal sealed class SoftInfo { public float fade; public bool inverse; }

        [Serializable]
        internal sealed class LibraryEffect
        {
            public string id, slug, name, pack, license, libraryPackage, prefab;
            /// <summary>Set for a flipbook texture picked on its own (no library prefab): the texture under <see cref="LibraryRoot"/> and its grid.</summary>
            public string sheetTexture;
            /// <summary>Children (paths below the prefab root) the adjustments file removes from this variant, with everything under them.</summary>
            public string[] disable;
            public int sheetColumns, sheetRows;
            public float seconds;
            public bool loop;
            public MaterialInfo[] materials;
            public string[] missingGuids, droppedScripts, files;
            /// <summary>The particle systems (by name) PRO Effects' ParticleGroupEmitter lists, read from the prefab by fx_import: emitted from script once
            /// per shot (<see cref="scriptEmitted"/>), or with emission switched on by the script (<see cref="scriptEmissionOn"/>, its emissionEnabled).</summary>
            public string[] scriptEmitted, scriptEmissionOn;
            /// <summary>Set for a composed effect: the converted prefabs it is layered from.</summary>
            public ComposePart[] parts;
            /// <summary>Set for an own effect declared as data (fx-own.json).</summary>
            public ForgeVfxOwnEffects.Recipe recipe;
        }
#pragma warning restore 0649

        internal static LibraryEffect[] Effects()
        {
            var filter = ForgeVfxProject.Argument("-forgeVfxEffects");
            var wanted = filter == null ? null : new HashSet<string>(filter.Split(','), StringComparer.Ordinal);
            var folder = Path.Combine(ForgeVfxProject.ProjectFolder, LibraryRoot.TrimEnd('/'), "_manifest");
            return Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => JsonUtility.FromJson<LibraryEffect>(File.ReadAllText(p)))
                .Where(e => wanted == null || wanted.Contains(e.slug)).ToArray();
        }

        // ---- convert ----

        [Serializable] private sealed class MaterialReport { public string slot, shader, from, blend, blendWhy, texture; public string[] lost; }

        [Serializable]
        private sealed class Report
        {
            public string id, slug, name, pack, license, libraryPackage, source, saved;
            public bool ok, rootSystemAdded;
            public string error;
            public int particleSystems, renderers, trails, lines, meshParticles, removedMissingScripts, removedLights, removedOther, maxParticles;
            public bool loop;
            public MaterialReport[] materials;
            public string[] notes;
        }

        private static void ConvertAll()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(ConvertedRoot.TrimEnd('/'));
            Directory.CreateDirectory(ConvertedRoot + "Materials");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var failures = 0;
            foreach (var effect in Effects())
            {
                var report = new Report { id = effect.id, slug = effect.slug, name = effect.name, pack = effect.pack, license = effect.license, libraryPackage = effect.libraryPackage, source = effect.prefab };
                try { ConvertOne(effect, report); report.ok = true; }
                catch (Exception error) { report.ok = false; report.error = error.GetType().Name + ": " + error.Message; failures++; Debug.LogError("convert " + effect.slug + ": " + error); }
                File.WriteAllText(Path.Combine(output, effect.slug + ".convert.json"), JsonUtility.ToJson(report, true), new UTF8Encoding(false));
                Debug.Log("convert " + effect.slug + (report.ok ? " ok" : " FAILED") + ": " + report.particleSystems + " systems, " + (report.materials == null ? 0 : report.materials.Length) + " materials");
            }
            AssetDatabase.SaveAssets();
            if (failures > 0) throw new InvalidOperationException(failures + " effect(s) failed; see the .convert.json reports.");
        }

        private static void ConvertOne(LibraryEffect effect, Report report)
        {
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(effect.sheetTexture)) { ConvertSheet(effect, report, notes); report.notes = notes.ToArray(); return; }
            if (effect.parts != null && effect.parts.Length > 0) { ConvertCompose(effect, report, notes); report.notes = notes.ToArray(); return; }
            if (effect.id.StartsWith("forge-own:", StringComparison.Ordinal)) { ConvertOwn(effect, report, notes); report.notes = notes.ToArray(); return; }
            // A model's remapped materials (externalObjects in its .meta) take only if they were imported before it: imported again now, its mesh draws with the library material.
            foreach (var file in effect.files ?? new string[0])
                if (file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                    AssetDatabase.ImportAsset(LibraryRoot + file, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var root = PrefabUtility.LoadPrefabContents(effect.prefab);
            try
            {
                RemoveChildren(root, effect.disable, notes);
                RemoveAnchorMeshes(root, effect, notes);
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    report.removedMissingScripts += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                foreach (var light in root.GetComponentsInChildren<Light>(true)) { UnityEngine.Object.DestroyImmediate(light); report.removedLights++; }
                // Any script that did resolve (none should: the library's scripts are not staged) cannot run in the game.
                foreach (var script in root.GetComponentsInChildren<MonoBehaviour>(true)) { UnityEngine.Object.DestroyImmediate(script); report.removedOther++; }

                var systems = root.GetComponentsInChildren<ParticleSystem>(true);
                report.particleSystems = systems.Length;
                report.loop = effect.loop;
                // PRO Effects' ParticleGroupEmitter keeps the layers it lists with emission off and emits them from script, once per shot (the first burst's count,
                // or one particle); the script does not run in the game, so each layer it lists gets that one burst at the start of the play.
                var scriptEmitted = new HashSet<string>(effect.scriptEmitted ?? new string[0], StringComparer.Ordinal);
                var scriptOn = new HashSet<string>(effect.scriptEmissionOn ?? new string[0], StringComparer.Ordinal);
                foreach (var system in systems)
                {
                    if (scriptOn.Contains(system.name) && !system.emission.enabled)
                    {
                        var emission = system.emission;
                        emission.enabled = true;
                        notes.Add("emission on (its script switched it on): " + system.name);
                    }
                    else if (scriptEmitted.Contains(system.name) && !system.emission.enabled) EmitOnce(system, notes);
                    var main = system.main;
                    main.playOnAwake = false;
                    main.stopAction = ParticleSystemStopAction.None; // a layer that destroys itself when it stops would take a pooled copy with it
                    report.maxParticles += main.maxParticles;
                }

                var byGuid = effect.materials.ToDictionary(m => m.guid, m => m, StringComparer.Ordinal);
                var made = new Dictionary<string, Material>(StringComparer.Ordinal);
                var materialReports = new List<MaterialReport>();
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    report.renderers++;
                    if (renderer is TrailRenderer) report.trails++;
                    else if (renderer is LineRenderer) report.lines++;
                    else if (renderer is ParticleSystemRenderer psr && psr.renderMode == ParticleSystemRenderMode.Mesh)
                    {
                        report.meshParticles++;
                        if (psr.mesh == null) { psr.renderMode = ParticleSystemRenderMode.Billboard; notes.Add("mesh particles without a mesh drawn as billboards: " + psr.name); }
                    }
                    // A newer Unity's sort mode (4, by view depth) is past 2019.4's last one: its particle geometry job crashes the editor and the game. Sorted by distance, the nearest match.
                    if (renderer is ParticleSystemRenderer sorted && (int)sorted.sortMode > (int)ParticleSystemSortMode.YoungestInFront)
                    {
                        notes.Add("sort mode " + (int)sorted.sortMode + " of a newer Unity sorted by distance: " + sorted.name);
                        sorted.sortMode = ParticleSystemSortMode.Distance;
                    }
                    var slots = renderer.sharedMaterials;
                    for (var i = 0; i < slots.Length; i++)
                    {
                        var path = slots[i] == null ? null : AssetDatabase.GetAssetPath(slots[i]);
                        var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
                        MaterialInfo info;
                        if (guid == null || !byGuid.TryGetValue(guid, out info))
                        {
                            // A built-in default (no library file): plain alpha-blended white; recorded so nobody mistakes it for the original.
                            info = new MaterialInfo { guid = "builtin:" + (slots[i] == null ? "null" : slots[i].name), name = slots[i] == null ? "null" : slots[i].name,
                                shader = slots[i] == null || slots[i].shader == null ? "(none)" : slots[i].shader.name, blend = "alpha", blendWhy = "no library material",
                                tint = new[] { 1f, 1f, 1f, 1f }, lost = new[] { "original material not in the library prefab's closure" } };
                        }
                        Material material;
                        if (!made.TryGetValue(info.guid, out material))
                        {
                            material = MakeMaterial(effect, info, made.Count, materialReports);
                            made[info.guid] = material;
                        }
                        slots[i] = material;
                    }
                    renderer.sharedMaterials = slots;
                    if (renderer is ParticleSystemRenderer streamRenderer)
                    {
                        streamRenderer.enableGPUInstancing = false;
                        // A ported library shader reads the library's vertex streams and custom data as they are; the other Forge shaders read position, colour and UV
                        // (and UV2 + AnimBlend for flipbook blending, or custom data and normals for the Uber features that read them), and the library's
                        // custom streams carry data only its own shaders read.
                        if (!slots.Any(IsPort))
                        {
                            var custom = slots.Any(m => m != null && m.IsKeywordEnabled("_CUSTOM_DATA")) && !slots.Any(m => m != null && m.IsKeywordEnabled("_FLIPBOOK"));
                            var customData = streamRenderer.GetComponent<ParticleSystem>().customData;
                            customData.enabled = custom && customData.enabled;
                            if (slots.Any(m => m != null && m.IsKeywordEnabled("_FLIPBOOK"))) AddFlipbookStreams(streamRenderer);
                            else
                            {
                                var streams = new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV };
                                if (slots.Any(m => m != null && m.IsKeywordEnabled("_EDGEFADE"))) streams.Insert(1, ParticleSystemVertexStream.Normal);
                                // after UV: custom1.xy fills TEXCOORD0.zw and custom1.zw + custom2.xy TEXCOORD1, where the Uber shader reads them
                                if (custom) streams.AddRange(new[] { ParticleSystemVertexStream.Custom1XYZW, ParticleSystemVertexStream.Custom2XY });
                                streamRenderer.SetActiveVertexStreams(streams);
                            }
                        }
                    }
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                report.materials = materialReports.ToArray();
                DropLengthFades(root, effect.disable, notes);
                TuneMaterials(root, effect.disable, notes);
                SilenceHiddenLayers(root);

                // The game plays the ParticleSystem at the root and polls it; a library prefab often has an empty root over several
                // child systems, so the root gets a silent system (no emission, no drawing) whose Play(true) starts every layer.
                if (root.GetComponent<ParticleSystem>() == null) { AddSilentRoot(root, effect.loop, systems); report.rootSystemAdded = true; }
                if (effect.droppedScripts != null && effect.droppedScripts.Length > 0)
                    notes.Add("library scripts not carried over: " + string.Join(", ", effect.droppedScripts));
                if (effect.missingGuids != null && effect.missingGuids.Length > 0)
                    notes.Add("unresolved references (shaders, scripts, packages): " + effect.missingGuids.Length);
                TakeScaleFromTransforms(root);
                var saved = ConvertedRoot + effect.slug + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, saved);
                report.saved = saved;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            report.notes = notes.ToArray();
        }

        /// <summary>A layer emitted by script: its emission on with nothing over time or distance and one burst at the start (its first burst's count, else one particle).</summary>
        private static void EmitOnce(ParticleSystem system, List<string> notes)
        {
            var emission = system.emission;
            var burst = emission.burstCount > 0 ? emission.GetBurst(0) : new ParticleSystem.Burst(0f, 1);
            burst.time = 0f;
            burst.cycleCount = 1;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;
            emission.SetBursts(new[] { burst });
            notes.Add("emitted once at the start (its script emitted it): " + system.name);
        }

        /// <summary>The silent particle system the game plays and polls at a root that has none: no emission, no drawing; its Play(true) starts every layer.</summary>
        private static void AddSilentRoot(GameObject root, bool loop, ParticleSystem[] layers)
        {
            var rootSystem = root.AddComponent<ParticleSystem>();
            var main = rootSystem.main;
            main.playOnAwake = false;
            main.stopAction = ParticleSystemStopAction.None; // a layer that destroys itself when it stops would take a pooled copy with it
            main.loop = loop;
            main.duration = Mathf.Max(0.1f, layers.Length == 0 ? 0.1f : layers.Max(s => s.main.duration));
            main.startLifetime = 0.01f;
            main.maxParticles = 1;
            var emission = rootSystem.emission;
            emission.enabled = false;
            var shape = rootSystem.shape;
            shape.enabled = false;
            root.GetComponent<ParticleSystemRenderer>().enabled = false;
        }

        /// <summary>A composed effect (fx-adjust `layers`): a copy of each part's converted prefab under one silent root, placed at the part's offset, turned by its
        /// rotation, scaled by its scale (every layer is on Hierarchy scaling) and started its delay later. Each part keeps its own materials and changes; the
        /// parts must have been converted before (fx_slots.py does that first).</summary>
        private static void ConvertCompose(LibraryEffect effect, Report report, List<string> notes)
        {
            var root = new GameObject(effect.slug);
            try
            {
                for (var i = 0; i < effect.parts.Length; i++)
                {
                    var part = effect.parts[i];
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ConvertedRoot + part.slug + ".prefab")
                        ?? throw new InvalidOperationException("layer " + part.slug + " is not converted");
                    var copy = UnityEngine.Object.Instantiate(prefab, root.transform, false);
                    copy.name = i + "." + part.slug;
                    if (part.offset != null && part.offset.Length == 3) copy.transform.localPosition = new Vector3(part.offset[0], part.offset[1], part.offset[2]);
                    if (part.rotate != null && part.rotate.Length == 3) copy.transform.localEulerAngles = new Vector3(part.rotate[0], part.rotate[1], part.rotate[2]);
                    var scale = part.scale > 0f ? part.scale : 1f;
                    copy.transform.localScale *= scale;
                    // a trail's or line's width is in metres, not scaled by its transform
                    foreach (var trail in copy.GetComponentsInChildren<TrailRenderer>(true)) trail.widthMultiplier *= scale;
                    foreach (var line in copy.GetComponentsInChildren<LineRenderer>(true)) line.widthMultiplier *= scale;
                    foreach (var layer in copy.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = layer.main;
                        if (part.delay > 0f)
                        {
                            var delay = main.startDelay;
                            delay.constant += part.delay; delay.constantMin += part.delay; delay.constantMax += part.delay;
                            main.startDelay = delay;
                        }
                        report.maxParticles += main.maxParticles;
                    }
                    notes.Add("layer " + copy.name + (part.delay > 0f ? " +" + part.delay + " s" : "") + (part.scale > 0f && part.scale != 1f ? " x" + part.scale : ""));
                }
                var systems = root.GetComponentsInChildren<ParticleSystem>(true);
                report.particleSystems = systems.Length;
                report.renderers = root.GetComponentsInChildren<Renderer>(true).Length;
                // The library's loop flag of a part says how it was authored, not what its conversion left: the composite loops only if a converted layer that emits does (a part's silent root emits nothing).
                report.loop = systems.Any(s => s.main.loop && s.emission.enabled);
                foreach (var silent in systems.Where(s => !s.emission.enabled)) { var main = silent.main; main.loop = report.loop; }
                AddSilentRoot(root, report.loop, systems);
                report.rootSystemAdded = true;
                TakeScaleFromTransforms(root);
                var saved = ConvertedRoot + effect.slug + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, saved);
                report.saved = saved;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>One of Forge's own effects (`forge-own:&lt;member&gt;`): a prefab made by <see cref="ForgeVfxOwnEffects"/>, or a recipe of fx-own.json built here
        /// (<see cref="ForgeVfxOwnEffects.FromRecipe"/>, library textures on Forge's particle shaders). Its Forge materials stay; its particle
        /// lights go off (the mods light the scene with the game's own lights, `PointLights`), the adjustments' children are left out, and it is saved
        /// beside the converted library effects so the same bundle carries it. Its textures are generated, not tracked: run ForgeVfxOwnEffects.Create first.</summary>
        private static void ConvertOwn(LibraryEffect effect, Report report, List<string> notes)
        {
            var recipe = effect.recipe != null && effect.recipe.layers != null && effect.recipe.layers.Length > 0;
            var made = new List<MaterialReport>();
            var root = recipe ? ForgeVfxOwnEffects.FromRecipe(effect.slug, effect.recipe, (layer, index) => RecipeMaterial(effect, layer, index, made))
                : PrefabUtility.LoadPrefabContents(effect.prefab);
            report.materials = made.ToArray();
            try
            {
                RemoveChildren(root, effect.disable, notes);
                if (recipe) TuneMaterials(root, effect.disable, notes); // its materials are its own; a prefab's own materials are shared and tracked, never changed
                if (recipe)
                {
                    // every recipe layer is a child: the root gets the silent system the game plays, looping if a layer left that emits does
                    var layers = root.GetComponentsInChildren<ParticleSystem>(true);
                    effect.loop = layers.Any(s => s.main.loop && s.emission.enabled);
                    AddSilentRoot(root, effect.loop, layers);
                    report.rootSystemAdded = true;
                }
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled) continue; // the silent root draws nothing
                    report.renderers++;
                    foreach (var material in renderer.sharedMaterials)
                        if (material == null || material.mainTexture == null)
                            throw new InvalidOperationException(renderer.name + " has no texture: run ForgeVfxOwnEffects.Create in this project first");
                }
                var systems = root.GetComponentsInChildren<ParticleSystem>(true);
                report.particleSystems = systems.Length;
                report.loop = effect.loop;
                foreach (var system in systems)
                {
                    var main = system.main;
                    main.playOnAwake = false;
                    report.maxParticles += main.maxParticles;
                    var lights = system.lights;
                    if (lights.enabled) { lights.enabled = false; report.removedLights++; }
                }
                TakeScaleFromTransforms(root);
                var saved = ConvertedRoot + effect.slug + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, saved);
                report.saved = saved;
            }
            finally
            {
                if (recipe) UnityEngine.Object.DestroyImmediate(root);
                else PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>A recipe layer's material: an own material (`own:&lt;name&gt;` of Own/Materials, shared, never changed here) or a staged library texture on the
        /// Forge particle shader of the layer's blend (additive, alpha, premultiplied), its tint the layer's `tint` brightness.</summary>
        private static Material RecipeMaterial(LibraryEffect effect, ForgeVfxOwnEffects.RecipeLayer layer, int index, List<MaterialReport> reports)
        {
            if (!string.IsNullOrEmpty(layer.material))
            {
                if (!layer.material.StartsWith("own:", StringComparison.Ordinal)) throw new InvalidOperationException("a recipe material is own:<name>: " + layer.material);
                var own = AssetDatabase.LoadAssetAtPath<Material>(ForgeVfxProject.Root + "Own/Materials/" + layer.material.Substring(4) + ".mat")
                    ?? throw new InvalidOperationException("no own material " + layer.material + ": run ForgeVfxOwnEffects.Create first");
                // Its texture is generated, not tracked: missing, a tuned copy of the material would keep no texture and draw hard squares.
                if (own.mainTexture == null) throw new InvalidOperationException("own material " + layer.material + " has no texture: run ForgeVfxOwnEffects.Create first");
                return own;
            }
            if (string.IsNullOrEmpty(layer.texture)) throw new InvalidOperationException("recipe layer " + layer.name + " names neither a material nor a texture");
            var k = layer.tint > 0f ? layer.tint : 1f;
            var info = new MaterialInfo
            {
                guid = "recipe:" + effect.slug + ":" + index, name = layer.name, shader = "(recipe)", blend = string.IsNullOrEmpty(layer.blend) ? "additive" : layer.blend, blendWhy = "recipe",
                mainTexture = new TextureInfo { prop = "_MainTex", path = layer.texture, scale = new[] { 1f, 1f }, offset = new[] { 0f, 0f } },
                tint = new[] { k, k, k, 1f }, lost = new string[0],
            };
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(LibraryRoot + layer.texture) == null) throw new InvalidOperationException("recipe texture " + layer.texture + " is not staged");
            return MakeMaterial(effect, info, index, reports);
        }

        /// <summary>Every layer takes its size, speed and shape from the transform chain (Hierarchy scaling), so a copy's scale reaches every layer whatever the library's author left them on. It has to be in the prefab: the game's build has stripped MainModule.scalingMode, the game cannot set it.</summary>
        private static void TakeScaleFromTransforms(GameObject root)
        {
            foreach (var layer in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = layer.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
        }

        /// <summary>A flipbook texture on its own becomes one puff: a single particle that plays the sheet's frames over its life (the particle system's
        /// Texture Sheet Animation), grows a little and fades out, drawn by the Uber shader with frame blending (the next frame and the blend factor ride
        /// vertex streams UV2 and AnimBlend), alpha blended. The texture's alpha is the shape.</summary>
        private static void ConvertSheet(LibraryEffect effect, Report report, List<string> notes)
        {
            var info = new MaterialInfo
            {
                guid = "sheet:" + effect.slug, name = effect.name, shader = "(flipbook texture)", blend = "alpha", blendWhy = "a smoke flipbook is drawn alpha blended",
                mainTexture = new TextureInfo { prop = "_MainTex", path = effect.sheetTexture, scale = new[] { 1f, 1f }, offset = new[] { 0f, 0f } },
                tint = new[] { 1f, 1f, 1f, 1f }, lost = new string[0],
                uber = new UberInfo { shader = UberShader, keywords = new[] { "_FLIPBOOK" } },
            };
            var reports = new List<MaterialReport>();
            var material = MakeMaterial(effect, info, 0, reports);
            var root = new GameObject(effect.slug);
            try
            {
                var system = root.AddComponent<ParticleSystem>();
                var main = system.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = effect.seconds;
                main.startLifetime = effect.seconds;
                main.startSpeed = 0f;
                main.startSize = 1f;
                main.startColor = Color.white;
                main.maxParticles = 4;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = system.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var shape = system.shape;
                shape.enabled = true; // a point half a unit up, so the puff sits above the spawn point instead of half under it
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.001f;
                shape.position = new Vector3(0f, 0.5f, 0f);
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.numTilesX = effect.sheetColumns;
                sheet.numTilesY = effect.sheetRows;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                sheet.cycleCount = 1;
                var size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 1.2f));
                var fade = new Gradient();
                fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
                var colour = system.colorOverLifetime;
                colour.enabled = true;
                colour.color = new ParticleSystem.MinMaxGradient(fade);
                var renderer = root.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = material;
                renderer.enableGPUInstancing = false;
                AddFlipbookStreams(renderer);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                report.particleSystems = 1; report.renderers = 1; report.maxParticles = main.maxParticles; report.loop = false;
                report.materials = reports.ToArray();
                notes.Add("built from a " + effect.sheetColumns + "x" + effect.sheetRows + " flipbook texture: one puff over " + effect.seconds + " s");
                TakeScaleFromTransforms(root);
                var saved = ConvertedRoot + effect.slug + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, saved);
                report.saved = saved;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>Removes the named children (path below the root, '/' separated) with everything under them; a path that matches nothing is an error,
        /// so a stale adjustment is found instead of silently doing nothing.</summary>
        /// <summary>A plain mesh in an effect prefab is the anchor the library's scene shows it on (a white ball under a hit effect), not part of the effect: only particles and trails are played.</summary>
        /// <summary>`@keepmesh:path` keeps that mesh (a projectile's body, a rail gun's spiral): its material is converted like the particles' and it is shown while the copy plays.
        /// A mesh drawn with a ported library shader (a Knife bolt, rim or core) is kept as well: it is the effect.</summary>
        private static void RemoveAnchorMeshes(GameObject root, LibraryEffect effect, List<string> notes)
        {
            var kept = new HashSet<Transform>();
            var ported = new HashSet<string>(effect.materials.Where(m => m.uber != null && m.uber.port).Select(m => m.guid), StringComparer.Ordinal);
            foreach (var mesh in root.GetComponentsInChildren<MeshRenderer>(true))
                if (mesh.sharedMaterials.Any(m => m != null && ported.Contains(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m)))))
                {
                    kept.Add(mesh.transform);
                    notes.Add("kept mesh " + mesh.name + " (ported shader)");
                }
            foreach (var spec in effect.disable ?? new string[0])
            {
                if (!spec.StartsWith("@keepmesh:", StringComparison.Ordinal)) continue;
                var t = root.transform.Find(spec.Substring(10));
                if (t == null || t.GetComponent<MeshRenderer>() == null) throw new InvalidOperationException("the adjustment keeps mesh '" + spec + "' but the prefab has no such mesh");
                kept.Add(t);
                notes.Add("kept mesh " + spec.Substring(10));
            }
            foreach (var mesh in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (kept.Contains(mesh.transform)) continue;
                var filter = mesh.GetComponent<MeshFilter>();
                notes.Add("removed anchor mesh " + mesh.name);
                UnityEngine.Object.DestroyImmediate(mesh);
                if (filter != null) UnityEngine.Object.DestroyImmediate(filter);
            }
        }

        private static void RemoveChildren(GameObject root, string[] paths, List<string> notes)
        {
            if (paths != null && paths.Length > 0)
                // Children of a nested prefab cannot be moved or removed until it is unpacked.
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    if (transform.gameObject != root && PrefabUtility.IsOutermostPrefabInstanceRoot(transform.gameObject))
                        PrefabUtility.UnpackPrefabInstance(transform.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var path in paths ?? new string[0])
            {
                if (path == "@nodelay")
                {
                    // An engine prefab starts its layers one to three seconds in; a shell's flight is often over by then.
                    foreach (var layer in root.GetComponentsInChildren<ParticleSystem>(true)) { var main = layer.main; main.startDelay = 0f; }
                    notes.Add("start delays removed");
                    continue;
                }
                if (path.StartsWith("@cap:", StringComparison.Ordinal))
                {
                    CapSizes(root, float.Parse(path.Substring(5), System.Globalization.CultureInfo.InvariantCulture), notes);
                    continue;
                }
                if (path.StartsWith("@beam:", StringComparison.Ordinal))
                {
                    var spec = path.Substring(6).Split(':');
                    FlatBeam(root, spec[0], float.Parse(spec[1], System.Globalization.CultureInfo.InvariantCulture), notes);
                    CrossedLine(root, spec[0], 1f, notes);
                    continue;
                }
                if (path.StartsWith("@line:", StringComparison.Ordinal))
                {
                    var thick = path.LastIndexOf(':');
                    CrossedLine(root, path.Substring(6, thick - 6), float.Parse(path.Substring(thick + 1), System.Globalization.CultureInfo.InvariantCulture), notes);
                    continue;
                }
                if (path.StartsWith("@nomask:", StringComparison.Ordinal) || path.StartsWith("@bright:", StringComparison.Ordinal) || path.StartsWith("@mips:", StringComparison.Ordinal) || path.StartsWith("@tint:", StringComparison.Ordinal)
                    || path.StartsWith("@colour:", StringComparison.Ordinal) || path.StartsWith("@keepmesh:", StringComparison.Ordinal) || path.StartsWith("@soft:", StringComparison.Ordinal)) continue; // done on the materials and meshes, see DropLengthFades, TuneMaterials, RemoveAnchorMeshes
                if (LayerParameter(root, path, notes)) continue;
                if (path.StartsWith("@hide:", StringComparison.Ordinal))
                {
                    var hidden = path == "@hide:." ? root.transform : root.transform.Find(path.Substring(6));
                    if (hidden == null) throw new InvalidOperationException("the adjustment hides '" + path + "' but the prefab has no such layer");
                    var drawn = hidden.GetComponent<ParticleSystemRenderer>();
                    if (drawn == null) throw new InvalidOperationException("the adjustment hides '" + path + "' but it draws nothing (a recipe's root is silent: name the layer, or disable it)");
                    drawn.enabled = false;
                    notes.Add("hidden, its children stay: " + path);
                    continue;
                }
                if (path == "@flash")
                {
                    InstantFlash(root, notes);
                    continue;
                }
                if (path.StartsWith("@life:", StringComparison.Ordinal))
                {
                    var factorAt = path.LastIndexOf(':');
                    ShorterLife(root, path.Substring(6, factorAt - 6), float.Parse(path.Substring(factorAt + 1), System.Globalization.CultureInfo.InvariantCulture), notes);
                    continue;
                }
                if (path.StartsWith("@loop:", StringComparison.Ordinal))
                {
                    var loop = path.Substring(6).Split(':');
                    Looping(root, loop[0], loop.Length > 1 ? float.Parse(loop[1], System.Globalization.CultureInfo.InvariantCulture) : 0f, notes);
                    continue;
                }
                if (path == "@silent")
                {
                    // The root system stays (the game plays and polls it), it draws nothing.
                    root.GetComponent<ParticleSystemRenderer>().enabled = false;
                    notes.Add("root layer not drawn");
                    continue;
                }
                if (path.StartsWith("=", StringComparison.Ordinal))
                {
                    // `=child`: the prefab is a library showcase laid out in space; only this child stays, moved to the root.
                    var kept = root.transform.Find(path.Substring(1));
                    if (kept == null) throw new InvalidOperationException("the adjustment keeps '" + path + "' but the prefab has no such child");
                    kept.SetParent(root.transform, false);
                    kept.localPosition = Vector3.zero;
                    for (var i = root.transform.childCount - 1; i >= 0; i--)
                        if (root.transform.GetChild(i) != kept) UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
                    notes.Add("kept only child " + path.Substring(1));
                    continue;
                }
                var child = root.transform.Find(path);
                if (child == null) throw new InvalidOperationException("the adjustment removes '" + path + "' but the prefab has no such child");
                UnityEngine.Object.DestroyImmediate(child.gameObject);
                notes.Add("removed child " + path);
            }
        }

        /// <summary>The layers `@op:layer:value` names: `*` every layer, `.` the root, else the child at that path. A name that matches nothing throws, so a stale adjustment is found.</summary>
        private static ParticleSystem[] LayersNamed(GameObject root, string name, string spec)
        {
            if (name == "*") return root.GetComponentsInChildren<ParticleSystem>(true);
            var layer = (name == "." ? root.transform : root.transform.Find(name))?.GetComponent<ParticleSystem>();
            if (layer == null) throw new InvalidOperationException("the adjustment " + spec + " names a layer the prefab does not have");
            return new[] { layer };
        }

        /// <summary>Per-layer settings written into the prefab, so the game never reads or writes them (its build stripped most of MainModule):
        /// `@size:layer:k` start size times k, `@speed:layer:k` start speed times k, `@count:layer:k` rate, bursts and particle cap times k,
        /// `@delay:layer:s` start delay s seconds, `@gravity:layer:g` gravity modifier g. True when <paramref name="spec"/> was one of them.</summary>
        private static bool LayerParameter(GameObject root, string spec, List<string> notes)
        {
            var op = spec.Substring(0, spec.IndexOf(':') + 1);
            if (op != "@size:" && op != "@speed:" && op != "@count:" && op != "@delay:" && op != "@gravity:") return false;
            var at = spec.LastIndexOf(':');
            if (at < op.Length) throw new InvalidOperationException("the adjustment " + spec + " is @op:layer:value");
            var value = float.Parse(spec.Substring(at + 1), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var layer in LayersNamed(root, spec.Substring(op.Length, at - op.Length), spec))
            {
                var main = layer.main;
                switch (op)
                {
                    case "@size:":
                        if (main.startSize3D) { main.startSizeX = Scaled(main.startSizeX, value); main.startSizeY = Scaled(main.startSizeY, value); main.startSizeZ = Scaled(main.startSizeZ, value); }
                        else main.startSize = Scaled(main.startSize, value);
                        break;
                    case "@speed:": main.startSpeed = Scaled(main.startSpeed, value); break;
                    case "@delay:": main.startDelay = value; break;
                    case "@gravity:": main.gravityModifier = value; break;
                    case "@count:":
                        var emission = layer.emission;
                        emission.rateOverTime = Scaled(emission.rateOverTime, value);
                        var bursts = new ParticleSystem.Burst[emission.burstCount];
                        emission.GetBursts(bursts);
                        for (var i = 0; i < bursts.Length; i++) bursts[i].count = Scaled(bursts[i].count, value);
                        emission.SetBursts(bursts);
                        main.maxParticles = Mathf.Max(1, Mathf.CeilToInt(main.maxParticles * Mathf.Max(1f, value)));
                        break;
                }
            }
            notes.Add(spec);
            return true;
        }

        /// <summary>`@cap:n`: a layer whose start size is above n metres (as authored) comes down to n, its axes in proportion: a laser's glows are authored 15 to 20 m across beside a beam 3 m wide. A stretched
        /// billboard is left alone: it is the beam, and its size along the line is the line's length.</summary>
        private static void CapSizes(GameObject root, float cap, List<string> notes)
        {
            foreach (var layer in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (layer.GetComponent<ParticleSystemRenderer>().renderMode == ParticleSystemRenderMode.Stretch) continue;
                var main = layer.main;
                var largest = main.startSize3D ? Mathf.Max(Largest(main.startSizeX), Largest(main.startSizeY), Largest(main.startSizeZ)) : Largest(main.startSize);
                if (largest <= cap) continue;
                var factor = cap / largest;
                if (main.startSize3D)
                {
                    main.startSizeX = Scaled(main.startSizeX, factor);
                    main.startSizeY = Scaled(main.startSizeY, factor);
                    main.startSizeZ = Scaled(main.startSizeZ, factor);
                }
                else main.startSize = Scaled(main.startSize, factor);
                notes.Add("start size capped: " + layer.name + " x" + factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static float Largest(ParticleSystem.MinMaxCurve size)
        {
            switch (size.mode)
            {
                case ParticleSystemCurveMode.Constant: return size.constant;
                case ParticleSystemCurveMode.TwoConstants: return Mathf.Max(size.constantMin, size.constantMax);
                default: return size.curveMultiplier;
            }
        }

        private static ParticleSystem.MinMaxCurve Scaled(ParticleSystem.MinMaxCurve size, float factor)
        {
            size.constant *= factor;
            size.constantMin *= factor;
            size.constantMax *= factor;
            size.curveMultiplier *= factor;
            return size;
        }

        /// <summary>`@flash`: a muzzle flash authored as a looping emitter of ten particles a second shows its first one a tenth of a second after the shot, and only inside the slot's window. Every looping layer becomes one particle at once, then nothing.</summary>
        private static void InstantFlash(GameObject root, List<string> notes)
        {
            foreach (var layer in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = layer.main;
                if (!main.loop) continue;
                main.loop = false;
                main.duration = 0.1f;
                main.startLifetime = Scaled(main.startLifetime, Mathf.Max(1f, 0.07f / Mathf.Max(0.001f, Largest(main.startLifetime))));
                var emission = layer.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
            }
            notes.Add("one flash at once");
        }

        /// <summary>`@life:layer:factor` (`*`: every layer): that layer's particles live this share of their time, and a looping layer's round shortens with it. Its fades run with it, so a slow plume becomes a quick puff.</summary>
        private static void ShorterLife(GameObject root, string name, float factor, List<string> notes)
        {
            var layers = name == "*" ? root.GetComponentsInChildren<ParticleSystem>(true) : new[] { root.transform.Find(name)?.GetComponent<ParticleSystem>() };
            if (layers[0] == null) throw new InvalidOperationException("the adjustment shortens '" + name + "' but the prefab has no such layer");
            foreach (var layer in layers)
            {
                var main = layer.main;
                main.startLifetime = Scaled(main.startLifetime, factor);
                if (main.loop) main.duration = Mathf.Max(0.05f, main.duration * factor);
            }
            notes.Add("life x" + factor + ": " + name);
        }

        /// <summary>`@loop:layer[:seconds]` (`*`: every layer, `.`: the root): that layer emits round after round, for a slot held longer than its round (a layer that stops leaves the effect thinning out until the
        /// whole copy is dead and a new one starts). `seconds` is the new round: how often a one-burst layer repeats. It keeps enough particles for one round to overlap the last.</summary>
        private static void Looping(GameObject root, string name, float seconds, List<string> notes)
        {
            var layers = name == "*" ? root.GetComponentsInChildren<ParticleSystem>(true) : new[] { name == "." ? root.GetComponent<ParticleSystem>() : root.transform.Find(name)?.GetComponent<ParticleSystem>() };
            if (layers[0] == null) throw new InvalidOperationException("the adjustment loops '" + name + "' but the prefab has no such layer");
            foreach (var layer in layers)
            {
                var main = layer.main;
                main.loop = true;
                if (seconds > 0f) main.duration = seconds;
                var emission = layer.emission;
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                var perBurst = bursts.Sum(b => Largest(b.count) * Mathf.Max(1, b.cycleCount));
                var life = Largest(main.startLifetime);
                main.maxParticles = Mathf.Max(main.maxParticles, Mathf.CeilToInt(Largest(emission.rateOverTime) * life + perBurst * (life / main.duration + 1f)));
            }
            notes.Add("loops: " + name + (seconds > 0f ? " every " + seconds + " s" : ""));
        }

        /// <summary>`@bright:layer:k` (`.`: the root): that layer draws additive, k times as bright (a thin line seen from afar stays a light instead of a dim stroke over the room). `@mips:layer`: its texture keeps its stroke's coverage in its mipmaps: a stroke under one pixel fades evenly instead of breaking into dashes.</summary>
        private static void TuneMaterials(GameObject root, string[] specs, List<string> notes)
        {
            var colourIndex = 0; // counted over every @colour rule: two rules of one colour must not write the same .mat
            foreach (var spec in specs ?? new string[0])
            {
                if (spec.StartsWith("@bright:", StringComparison.Ordinal))
                {
                    var at = spec.LastIndexOf(':');
                    var named = spec.Substring(8, at - 8);
                    var layer = named == "." ? root.transform : root.transform.Find(named);
                    if (layer == null) throw new InvalidOperationException("the adjustment brightens '" + spec + "' but the prefab has no such layer");
                    var material = Tunable(layer.GetComponent<Renderer>(), root);
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)BlendMode.One);
                    material.SetColor("_TintColor", material.GetColor("_TintColor") * float.Parse(spec.Substring(at + 1), System.Globalization.CultureInfo.InvariantCulture));
                    EditorUtility.SetDirty(material);
                    notes.Add("additive: " + spec);
                }
                else if (spec.StartsWith("@tint:", StringComparison.Ordinal))
                {
                    var names = spec.Substring(6);
                    // `*` leaves out the refraction layers (they draw no colour of their own) and the ports (they keep the library's look).
                    var layers = names == "*" ? root.GetComponentsInChildren<ParticleSystemRenderer>(true).Where(r => r.enabled && r.sharedMaterial != null && r.sharedMaterial.shader.name != "ForgeVfx/Particles Distort"
                            && !IsPort(r.sharedMaterial)).Select(r => r.transform).ToArray()
                        : names.Split(',').Select(n => n == "." ? root.transform : root.transform.Find(n)).ToArray();
                    foreach (var layer in layers)
                    {
                        if (layer == null) throw new InvalidOperationException("the adjustment tints '" + spec + "' but the prefab has no such layer");
                        var material = Tunable(layer.GetComponent<Renderer>(), root);
                        if (material != null) ToUber(material);
                        if (material == null || material.shader.name != "ForgeVfx/Particles Uber") throw new InvalidOperationException("the adjustment tints " + layer.name + " but its material is not ForgeVfx/Particles Uber");
                        material.EnableKeyword("_TINT");
                        material.SetFloat("_UseTint", 1f);
                        material.SetFloat("_CallerWeight", 1f);
                        EditorUtility.SetDirty(material);
                    }
                    notes.Add("hue from the caller's colour: " + names);
                }
                else if (spec.StartsWith("@colour:", StringComparison.Ordinal))
                {
                    // `@colour:layer:#rrggbb[:k]`: a fixed hue at the layer's own brightness (its hottest part stays white), k times as bright; the layer gets a
                    // material of its own, so a layer sharing the library material keeps its colour. With @tint as well the caller's colour multiplies it.
                    var hash = spec.IndexOf(":#", StringComparison.Ordinal);
                    if (hash < 0) throw new InvalidOperationException("the adjustment " + spec + " is @colour:layer:#rrggbb[:k]");
                    var rest = spec.Substring(hash + 1).Split(':');
                    if (!ColorUtility.TryParseHtmlString(rest[0], out var hue)) throw new InvalidOperationException("the adjustment " + spec + " has no #rrggbb colour");
                    var gain = rest.Length > 1 ? float.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                    foreach (var layer in LayersNamed(root, spec.Substring(8, hash - 8), spec))
                    {
                        var renderer = layer.GetComponent<Renderer>();
                        if (renderer.sharedMaterial == null || renderer.sharedMaterial.shader.name == DistortShader) continue;
                        // one fixed file per effect, layer and colour, written over on every conversion
                        var material = new Material(renderer.sharedMaterial) { name = root.name + "_colour" + colourIndex++ + "_" + layer.name + "_" + rest[0].TrimStart('#') };
                        ToUber(material);
                        if (material.shader.name != UberShader) throw new InvalidOperationException("the adjustment colours " + layer.name + " but its material is not ForgeVfx/Particles Uber");
                        if (!material.IsKeywordEnabled("_TINT")) material.SetFloat("_CallerWeight", 0f);
                        material.EnableKeyword("_TINT");
                        material.SetFloat("_UseTint", 1f);
                        material.SetColor("_Hue", hue);
                        var tint = material.GetColor("_TintColor");
                        material.SetColor("_TintColor", new Color(tint.r * gain, tint.g * gain, tint.b * gain, tint.a));
                        AssetDatabase.CreateAsset(material, ConvertedRoot + "Materials/" + material.name + ".mat");
                        renderer.sharedMaterial = material;
                    }
                    notes.Add("fixed hue: " + spec);
                }
                else if (spec.StartsWith("@soft:", StringComparison.Ordinal))
                {
                    // `@soft:layer,layer[:metres]` (`*` every drawn Uber or plain layer, `.` the root): soft particles, the layer fades out where it meets a wall or
                    // the floor over that many metres (default 0.5), so a large sheet cutting through geometry shows no straight edge. Off without a depth texture.
                    var body = spec.Substring(6);
                    var at = body.LastIndexOf(':');
                    var metres = at > 0 ? float.Parse(body.Substring(at + 1), System.Globalization.CultureInfo.InvariantCulture) : 0.5f;
                    var names = at > 0 ? body.Substring(0, at) : body;
                    var renderers = names == "*" ? root.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.sharedMaterial != null).ToArray()
                        : names.Split(',').Select(n => (n == "." ? root.transform : root.transform.Find(n))?.GetComponent<Renderer>()
                            ?? throw new InvalidOperationException("the adjustment softens '" + n + "' but the prefab has no such layer")).ToArray();
                    foreach (var renderer in renderers)
                    {
                        if (names == "*" && renderer.sharedMaterial.shader.name.StartsWith(KnifePrefix, StringComparison.Ordinal)) continue; // a Knife port keeps its own look
                        var material = Tunable(renderer, root);
                        if (!Soften(material, 1f / Mathf.Max(0.05f, metres), false))
                        {
                            if (names == "*") continue; // a refraction shader has no soft fade
                            throw new InvalidOperationException("the adjustment softens " + renderer.name + " but its material has no soft fade");
                        }
                        EditorUtility.SetDirty(material);
                    }
                    notes.Add("soft particles: " + spec);
                }
                else if (spec.StartsWith("@mips:", StringComparison.Ordinal))
                {
                    var layer = root.transform.Find(spec.Substring(6));
                    if (layer == null) throw new InvalidOperationException("the adjustment gives mipmaps to '" + spec + "' but the prefab has no such layer");
                    var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(layer.GetComponent<Renderer>().sharedMaterial.mainTexture));
                    importer.mipmapEnabled = true;
                    importer.mipMapsPreserveCoverage = true;
                    importer.alphaTestReferenceValue = 0.25f;
                    importer.SaveAndReimport();
                    notes.Add("coverage mipmaps: " + spec);
                }
            }
        }

        /// <summary>The material a tuning rule may change: a recipe layer's own material (Own/Materials) is shared by every effect that names it, so the layer
        /// first moves to a copy of its own (one fixed file per effect and layer, written over on every conversion); a converted material is the effect's already.</summary>
        private static Material Tunable(Renderer renderer, GameObject root)
        {
            var material = renderer.sharedMaterial;
            if (material == null || !AssetDatabase.GetAssetPath(material).StartsWith(ForgeVfxProject.Root + "Own/", StringComparison.Ordinal)) return material;
            var copy = new Material(material) { name = root.name + "_" + renderer.name + "_" + material.name };
            AssetDatabase.CreateAsset(copy, ConvertedRoot + "Materials/" + copy.name + ".mat");
            renderer.sharedMaterial = copy;
            return copy;
        }

        /// <summary>A layer on one of Forge's plain particle shaders moves to Particles Uber with no feature on, which draws the same (its header): additive
        /// SrcAlpha One, alpha blended SrcAlpha OneMinusSrcAlpha, premultiplied One OneMinusSrcAlpha (its textures already carry the alpha), same texture and tint.
        /// Only Uber can take the caller's hue (`@tint`). Other shaders are left as they are.</summary>
        private static void ToUber(Material material)
        {
            float src, dst;
            switch (material.shader.name)
            {
                case "ForgeVfx/Particles Additive": src = (float)BlendMode.SrcAlpha; dst = (float)BlendMode.One; break;
                case "ForgeVfx/Particles Alpha Blended": src = (float)BlendMode.SrcAlpha; dst = (float)BlendMode.OneMinusSrcAlpha; break;
                case "ForgeVfx/Particles Premultiplied": src = (float)BlendMode.One; dst = (float)BlendMode.OneMinusSrcAlpha; break;
                default: return;
            }
            var texture = material.mainTexture;
            var scale = material.mainTextureScale;
            var offset = material.mainTextureOffset;
            var tint = material.GetColor("_TintColor");
            material.shader = Shader.Find("ForgeVfx/Particles Uber") ?? throw new InvalidOperationException("ForgeVfx/Particles Uber is not in the project");
            material.mainTexture = texture;
            material.mainTextureScale = scale;
            material.mainTextureOffset = offset;
            material.SetColor("_TintColor", tint);
            material.SetFloat("_SrcBlend", src);
            material.SetFloat("_DstBlend", dst);
            material.SetFloat("_Premul", 0f);
        }

        /// <summary>A layer the prefab does not draw stays silent and ends at once: left emitting it would keep the effect alive for seconds with nothing to see.</summary>
        private static void SilenceHiddenLayers(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (renderer.enabled) continue;
                var layer = renderer.GetComponent<ParticleSystem>();
                var emission = layer.emission;
                emission.enabled = false;
                var main = layer.main;
                main.loop = false;
                main.duration = 0.05f;
            }
        }

        /// <summary>`@nomask:layer,layer`: a laser body's mask fades its two ends over a share of its length, which is a gap of metres once it is stretched to any length: the mask goes, the head and the muzzle cover the ends.</summary>
        private static void DropLengthFades(GameObject root, string[] specs, List<string> notes)
        {
            foreach (var spec in specs ?? new string[0])
            {
                if (!spec.StartsWith("@nomask:", StringComparison.Ordinal)) continue;
                foreach (var name in spec.Substring(8).Split(','))
                {
                    var layer = root.transform.Find(name);
                    if (layer == null) throw new InvalidOperationException("the adjustment drops the mask of '" + name + "' but the prefab has no such child");
                    var material = layer.GetComponent<Renderer>().sharedMaterial;
                    material.DisableKeyword("_MASK");
                    material.SetFloat("_UseMask", 0f);
                    EditorUtility.SetDirty(material);
                    notes.Add("mask dropped: " + name);
                }
            }
        }

        /// <summary>`@line:child:thickness`: a lightning line authored as a flat quad running along its own +X with its end sparkles under it becomes a line along the prefab's +Z: the quad is turned onto +Z, made thicker
        /// (its bolt is a thin stroke across the quad) and crossed with a second quad at a right angle (a flat quad vanishes seen edge-on), and the sparkles hang on the root, so stretching the quads (ForgeVisual's <c>Fx.Line</c>) leaves them round.</summary>
        private static void CrossedLine(GameObject root, string name, float thickness, List<string> notes)
        {
            var line = root.transform.Find(name);
            if (line == null) throw new InvalidOperationException("the adjustment makes a line of '" + name + "' but the prefab has no such child");
            var turn = Quaternion.Euler(0f, -90f, 0f);
            for (var i = line.childCount - 1; i >= 0; i--)
            {
                var child = line.GetChild(i);
                child.SetParent(root.transform, false);
                child.localPosition = turn * child.localPosition;
            }
            line.localPosition = Vector3.zero;
            line.localRotation = turn;
            line.localScale = new Vector3(1f, thickness, 1f);
            var crossed = UnityEngine.Object.Instantiate(line.gameObject, root.transform);
            crossed.name = name + " cross";
            crossed.transform.localRotation = Quaternion.Euler(0f, 0f, 90f) * turn;
            notes.Add("line along +Z, crossed: " + name);
        }

        /// <summary>`@beam:layer:length` (with `@line`'s crossing): a laser body drawn as one stretched billboard is culled as soon as its particle, at the start of the line, leaves the view, however far the
        /// stretch reaches into it (Unity sizes a stretched particle's bounds by its width only): standing beside a beam that stands up from the floor, looking up at it, it is gone. It becomes a flat quad the
        /// line's authored length along +X, starting at the layer's origin (aligned to the layer, not the camera; crossed by @line so it is not lost edge-on), which ForgeVisual stretches through the
        /// layer's transform: its bounds stretch with it.</summary>
        private static void FlatBeam(GameObject root, string name, float length, List<string> notes)
        {
            var layer = root.transform.Find(name)?.GetComponent<ParticleSystem>();
            if (layer == null) throw new InvalidOperationException("the adjustment makes a flat beam of '" + name + "' but the prefab has no such layer");
            var renderer = layer.GetComponent<ParticleSystemRenderer>();
            if (renderer.renderMode != ParticleSystemRenderMode.Stretch) throw new InvalidOperationException("the adjustment makes a flat beam of '" + name + "' but it is not a stretched billboard");
            var main = layer.main;
            var width = Largest(main.startSize3D ? main.startSizeX : main.startSize);
            main.startSize3D = true;
            main.startSizeX = length;
            main.startSizeY = width;
            main.startSizeZ = 1f;
            main.startSpeed = 0f;
            main.startRotation3D = false;
            main.startRotation = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var shape = layer.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.0001f;
            shape.position = new Vector3(length / 2f, 0f, 0f);
            shape.rotation = Vector3.zero;
            shape.scale = Vector3.one;
            var spin = layer.rotationOverLifetime;
            spin.enabled = false;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.Local;
            renderer.pivot = Vector3.zero;
            renderer.lengthScale = 1f;
            notes.Add("flat beam " + length + " long along +X: " + name);
        }

        private const string SoftDotPath = ConvertedRoot + "SoftDot.png";

        /// <summary>The soft round dot Unity's built-in particle material draws when a material has no texture (white, alpha falling off smoothly to the rim).</summary>
        private static Texture2D SoftDot()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(SoftDotPath);
            if (existing != null) return existing;
            const int size = 64;
            var dot = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var d = Mathf.Clamp01(new Vector2(x - (size - 1) / 2f, y - (size - 1) / 2f).magnitude / (size / 2f));
                    var a = 1f - d * d * (3f - 2f * d); // smoothstep falloff
                    dot.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            File.WriteAllBytes(SoftDotPath, dot.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(dot);
            AssetDatabase.ImportAsset(SoftDotPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(SoftDotPath);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(SoftDotPath);
        }

        private static Material MakeMaterial(LibraryEffect effect, MaterialInfo info, int index, List<MaterialReport> reports)
        {
            var uber = info.uber != null && !string.IsNullOrEmpty(info.uber.shader) ? info.uber : null;
            var shaderName = uber != null ? uber.shader
                : info.blend == "additive" ? AdditiveShader : info.blend == "premultiplied" ? PremultipliedShader : AlphaShader;
            var shader = Shader.Find(shaderName) ?? throw new InvalidOperationException("The project has no shader " + shaderName);
            var material = new Material(shader) { name = effect.slug + "_" + index };
            Texture2D texture = null;
            if (info.mainTexture != null && !string.IsNullOrEmpty(info.mainTexture.path))
            {
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(LibraryRoot + info.mainTexture.path);
                if (texture != null)
                {
                    material.SetTexture("_MainTex", texture);
                    material.SetTextureScale("_MainTex", new Vector2(info.mainTexture.scale[0], info.mainTexture.scale[1]));
                    material.SetTextureOffset("_MainTex", new Vector2(info.mainTexture.offset[0], info.mainTexture.offset[1]));
                }
            }
            if (texture == null && (uber == null || !uber.solid)) { texture = SoftDot(); material.SetTexture("_MainTex", texture); } // the library's own "default particle" is a built-in texture: a soft round dot
            var tint = info.tint != null && info.tint.Length == 4 ? new Color(info.tint[0], info.tint[1], info.tint[2], info.tint[3]) : Color.white;
            material.SetColor("_TintColor", tint);
            if (uber != null) ApplyUber(material, info, uber);
            AlphaOnly(material, texture);
            AlphaFromRgb(material, texture, info.blend);
            // the library material's own soft particles (its depth fade switch), as it fades
            if (info.soft != null && info.soft.fade > 0f) Soften(material, info.soft.fade, info.soft.inverse);
            AssetDatabase.CreateAsset(material, ConvertedRoot + "Materials/" + material.name + ".mat");
            reports.Add(new MaterialReport
            {
                slot = material.name, shader = shaderName, from = info.shader + (string.IsNullOrEmpty(info.path) ? "" : " | " + info.path),
                blend = info.blend, blendWhy = info.blendWhy,
                texture = texture != null ? AssetDatabase.GetAssetPath(texture) : "(none)", lost = info.lost ?? new string[0]
            });
            return material;
        }

        /// <summary>The Uber shader takes its blend from two material floats (additive SrcAlpha One, alpha SrcAlpha OneMinusSrcAlpha,
        /// premultiplied One OneMinusSrcAlpha); the Distort shader has no blend. Everything else comes from the manifest mapping.</summary>
        private static void ApplyUber(Material material, MaterialInfo info, UberInfo uber)
        {
            if (material.HasProperty("_SrcBlend"))
            {
                var premultiplied = info.blend == "premultiplied";
                material.SetFloat("_SrcBlend", premultiplied ? (float)BlendMode.One : (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", info.blend == "additive" ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_Premul", premultiplied ? 1f : 0f);
            }
            foreach (var t in uber.textures ?? new TextureInfo[0])
            {
                var texture = string.IsNullOrEmpty(t.path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(LibraryRoot + t.path);
                if (texture == null || !material.HasProperty(t.prop)) continue;
                material.SetTexture(t.prop, texture);
                if (t.scale != null && t.scale.Length == 2) material.SetTextureScale(t.prop, new Vector2(t.scale[0], t.scale[1]));
                if (t.offset != null && t.offset.Length == 2) material.SetTextureOffset(t.prop, new Vector2(t.offset[0], t.offset[1]));
            }
            foreach (var f in uber.floats ?? new UberFloat[0]) if (material.HasProperty(f.prop)) material.SetFloat(f.prop, f.value);
            foreach (var c in uber.colors ?? new UberColor[0])
                if (material.HasProperty(c.prop) && c.rgba != null && c.rgba.Length == 4) material.SetColor(c.prop, new Color(c.rgba[0], c.rgba[1], c.rgba[2], c.rgba[3]));
            foreach (var keyword in uber.keywords ?? new string[0]) material.EnableKeyword(keyword);
        }

        /// <summary>Soft particles on a layer: a plain layer moves to Uber first; false when its shader has no soft fade (refraction, a Knife port).
        /// <paramref name="perUnit"/> per metre, or per unit of 1 / depth with <paramref name="inverse"/> (ForgeSoft.cginc).</summary>
        private static bool Soften(Material material, float perUnit, bool inverse)
        {
            ToUber(material);
            if (!material.HasProperty("_SoftFade")) return false;
            material.EnableKeyword("_SOFT");
            material.SetFloat("_UseSoft", 1f);
            material.SetFloat("_SoftFade", perUnit);
            material.SetFloat("_SoftInverse", inverse ? 1f : 0f);
            return true;
        }

        /// <summary>An Alpha8 texture samples black in its colour channels: on a plain or Uber particle shader it would draw a black, untintable shape. Its layer moves
        /// to Uber with _ALPHA_ONLY, the alpha as the shape and the colour from the tint (and the caller's hue with @tint).</summary>
        private static void AlphaOnly(Material material, Texture texture)
        {
            if (!(texture is Texture2D flat) || flat.format != TextureFormat.Alpha8) return;
            ToUber(material);
            if (material.shader.name != UberShader) return;
            material.EnableKeyword("_ALPHA_ONLY");
            material.SetFloat("_AlphaOnly", 1f);
        }

        /// <summary>A texture imported with no alpha samples alpha 1: blended over the scene (not added) every particle would draw as a square. Unless a mask
        /// replaces the alpha, its layer moves to Uber with _ALPHA_FROM_RGB, the alpha the brightest colour channel (Unity's alpha from grayscale).</summary>
        private static void AlphaFromRgb(Material material, Texture texture, string blend)
        {
            if (blend == "additive" || !(texture is Texture2D flat) || flat.format == TextureFormat.Alpha8) return;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            if (importer == null || importer.alphaSource == TextureImporterAlphaSource.FromGrayScale
                || importer.alphaSource == TextureImporterAlphaSource.FromInput && importer.DoesSourceTextureHaveAlpha()) return;
            if (material.IsKeywordEnabled("_MASK") && material.HasProperty("_MaskReplace") && material.GetFloat("_MaskReplace") > 0.5f) return;
            ToUber(material);
            if (material.shader.name != UberShader) return;
            material.EnableKeyword("_ALPHA_FROM_RGB");
            material.SetFloat("_AlphaFromRgb", 1f);
        }

        /// <summary>A flipbook-blending material reads the next frame and the blend factor from vertex streams UV2 and AnimBlend.</summary>
        private static void AddFlipbookStreams(ParticleSystemRenderer renderer)
        {
            var streams = new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend
            };
            renderer.SetActiveVertexStreams(streams);
        }

        // ---- bundle ----

        [Serializable]
        private sealed class BundleManifest
        {
            public string bundle, sha256, unity, target;
            public long bytes;
            public BundleEffect[] effects;
        }

        [Serializable]
        private sealed class BundleEffect
        {
            public string slug, id, name, pack, license, libraryPackage, prefab;
            public float seconds;
            public bool loop;
            public string[] shaders;
            public BundleItem[] items;
        }

        [Serializable] private sealed class BundleItem { public string path, sha256; }

        private static void BuildLibraryBundle()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            var effects = Effects().Where(e => File.Exists(Path.Combine(ForgeVfxProject.ProjectFolder, ConvertedRoot + e.slug + ".prefab"))).ToArray();
            if (effects.Length == 0) throw new InvalidOperationException("No converted prefab; run Convert first.");
            var prefabs = effects.Select(e => ConvertedRoot + e.slug + ".prefab").ToArray();
            // A drawn layer on one of Forge's particle shaders without its texture renders as a hard-edged square: refused here, not found in the game.
            var untextured = prefabs.SelectMany(p => AssetDatabase.LoadAssetAtPath<GameObject>(p).GetComponentsInChildren<ParticleSystemRenderer>(true)
                    .Where(r => r.enabled && r.sharedMaterial != null && r.sharedMaterial.shader.name.StartsWith("ForgeVfx/", StringComparison.Ordinal)
                        && r.sharedMaterial.HasProperty("_MainTex") && r.sharedMaterial.mainTexture == null)
                    .Select(r => Path.GetFileNameWithoutExtension(p) + "/" + r.name + " (" + r.sharedMaterial.name + ")"))
                .ToArray();
            if (untextured.Length > 0) throw new InvalidOperationException("drawn layers without a texture: " + string.Join(", ", untextured) + "; convert them again after ForgeVfxOwnEffects.Create");
            // -forgeVfxBundleName <file>: one bundle per package from its own slice of the effects (-forgeVfxEffects); default is the whole set.
            var bundleName = ForgeVfxProject.Argument("-forgeVfxBundleName") ?? BundleName;
            var built = ForgeVfxBuild.BuildBundleFile(bundleName, prefabs);
            var target = Path.Combine(output, bundleName);
            File.Copy(built, target, true);
            var bytes = File.ReadAllBytes(target);
            var items = new List<BundleEffect>();
            foreach (var effect in effects)
            {
                var path = ConvertedRoot + effect.slug + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var shaders = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null && material.shader != null) shaders.Add(material.shader.name);
                items.Add(new BundleEffect
                {
                    slug = effect.slug, id = effect.id, name = effect.name, pack = effect.pack, license = effect.license, libraryPackage = effect.libraryPackage,
                    prefab = path, seconds = effect.seconds, loop = effect.loop, shaders = shaders.ToArray(),
                    items = AssetDatabase.GetDependencies(path, true).OrderBy(p => p, StringComparer.Ordinal)
                        .Where(p => !p.EndsWith(".cs", StringComparison.Ordinal))
                        .Select(p => new BundleItem { path = p, sha256 = Sha256(File.ReadAllBytes(Path.Combine(ForgeVfxProject.ProjectFolder, p))) }).ToArray()
                });
            }
            var manifest = new BundleManifest
            {
                bundle = bundleName, sha256 = Sha256(bytes), bytes = bytes.LongLength, unity = Application.unityVersion,
                target = BuildTarget.StandaloneWindows64.ToString(), effects = items.ToArray()
            };
            File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(bundleName) + ".manifest.json"), JsonUtility.ToJson(manifest, true).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
            Debug.Log("Forge VFX library bundle: " + target + " (" + bytes.LongLength + " bytes, " + effects.Length + " effects)");
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        // ---- preview: the bundle as a mod loads it ----

        private const int Cell = 160, Grid = 4;
        private const uint Seed = 20261104;

        /// <summary>Loads the built bundle from disk (not the project's prefabs) and renders each effect the way the effect library's own
        /// preview does, so before and after can be laid side by side: 16 frames at equal steps over the effect's length (one-shots:
        /// their longest system; loops: 3 s), in a 4 x 4 sheet of 160 px cells, from one fixed oblique camera framed on the effect's
        /// extent over the run. No post-processing (the library preview adds bloom and tone mapping). A shader that did not compile
        /// shows as magenta and is counted; the run fails if any effect is more than 10 % magenta.</summary>
        private static void RenderLibrary()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            var bundlePath = ForgeVfxProject.Argument("-forgeVfxBundle") ?? Path.Combine(output, BundleName);
            AssetBundle.UnloadAllAssetBundles(true);
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null) throw new InvalidOperationException("Unity could not load " + bundlePath);
            var failures = new List<string>();
            var summary = new StringBuilder();
            foreach (var shaderName in new[] { AdditiveShader, AlphaShader, PremultipliedShader, UberShader, DistortShader })
            {
                // The editor's own compile result for the project shader (the pass the game's Direct3D 11 build uses): every message with its severity.
                var shader = Shader.Find(shaderName);
                if (shader == null) { failures.Add("the project has no shader " + shaderName); continue; }
                var messages = ShaderUtil.GetShaderMessages(shader);
                var errors = messages.Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                summary.AppendLine("shader " + shaderName + ": supported=" + shader.isSupported + ", errors=" + errors + ", messages=" + messages.Length);
                foreach (var m in messages.Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)) failures.Add(shaderName + ": " + m.message + " (line " + m.line + ")");
                if (!shader.isSupported) failures.Add(shaderName + " is not supported on this device");
            }
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camera = Stage();
                var target = new RenderTexture(Cell, Cell, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                camera.targetTexture = target;
                var frame = new Texture2D(Cell, Cell, TextureFormat.RGB24, false);
                var effects = Effects();
                foreach (var effect in effects)
                {
                    // Resumable: Unity 2019.4 sometimes crashes the whole editor while drawing particles in batch mode. The driver relaunches this
                    // entry; an effect whose sheet exists is skipped, and `rendering.txt` names the effect the last run died on.
                    if (File.Exists(Path.Combine(output, effect.slug + ".after.png"))) continue;
                    var prefab = bundle.LoadAsset<GameObject>(effect.slug);
                    if (prefab == null) { failures.Add(effect.slug + " is not in the bundle"); continue; }
                    File.WriteAllText(Path.Combine(output, "rendering.txt"), effect.slug);
                    var magenta = RenderEffect(effect, prefab, camera, target, frame, output);
                    File.Delete(Path.Combine(output, "rendering.txt"));
                    summary.AppendLine(effect.slug + ": magenta " + (magenta * 100f).ToString("0.0") + " %, " + LastFraming);
                    if (magenta > 0.1f) failures.Add(effect.slug + " draws " + (magenta * 100f).ToString("0.0") + " % magenta (a shader did not compile)");
                }
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(frame);
                target.Release();
            }
            finally { bundle.Unload(true); }
            summary.AppendLine("graphics: " + SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsDeviceName);
            File.AppendAllText(Path.Combine(output, "preview.txt"), summary.ToString());
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
        }

        private static string LastFraming = "";

        private static float RenderEffect(LibraryEffect effect, GameObject prefab, Camera camera, RenderTexture target, Texture2D frame, string output)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                var root = instance.GetComponent<ParticleSystem>();
                foreach (var system in systems)
                {
                    system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    system.useAutoRandomSeed = false;
                    system.randomSeed = Seed;
                }
                var length = effect.loop ? 3f : Mathf.Clamp(effect.seconds > 0f ? effect.seconds : 2f, 1f, 5f);
                if (!effect.loop)
                {
                    // A one-shot's database length is often far longer than what it draws (a muzzle flash lives a tenth of a second): sample over the
                    // time it actually has live particles, so the sheet shows the effect and not 16 empty frames.
                    var alive = 0f;
                    for (var t = 0f; t <= 5f; t += 0.05f)
                    {
                        root.Simulate(t, true, true, false);
                        var live = false;
                        foreach (var system in systems) if (system.particleCount > 0) { live = true; break; }
                        if (live) alive = t;
                    }
                    if (alive > 0f) length = Mathf.Clamp(alive * 1.1f, 0.2f, 5f);
                }
                var times = Enumerable.Range(0, Grid * Grid).Select(i => length * i / (Grid * Grid - 1)).ToArray();
                // Pass 1: the union of the drawn bounds over the run frames the camera.
                var bounds = new Bounds(instance.transform.position, Vector3.zero);
                var any = false;
                // Small particles (thrown stones, sparks) fly far from the body of the effect and would drag the frame off it, so the frame follows the
                // particles of body size when there are any.
                var big = new Bounds(instance.transform.position, Vector3.zero);
                var anyBig = false;
                var peak = 0;
                var particles = new ParticleSystem.Particle[256];
                foreach (var time in times)
                {
                    root.Simulate(time, true, true, false);
                    // First use of a shader variant compiles it on demand; drawing every frame once here (camera not yet framed) keeps that out
                    // of the captured pass, where the editor crashed in batch mode when variants compiled mid-run.
                    camera.Render();
                    foreach (var system in systems)
                    {
                        var count = system.particleCount;
                        if (count == 0) continue;
                        peak = Mathf.Max(peak, count);
                        if (particles.Length < count) particles = new ParticleSystem.Particle[count];
                        system.GetParticles(particles);
                        var local = system.main.simulationSpace != ParticleSystemSimulationSpace.World;
                        for (var k = 0; k < count; k++)
                        {
                            var position = local ? system.transform.TransformPoint(particles[k].position) : particles[k].position;
                            var size = Mathf.Min(particles[k].GetCurrentSize(system), 6f);
                            var box = new Bounds(position, Vector3.one * size);
                            if (!any) { bounds = box; any = true; } else bounds.Encapsulate(box);
                            if (size < 0.4f) continue;
                            if (!anyBig) { big = box; anyBig = true; } else big.Encapsulate(box);
                        }
                    }
                }
                if (anyBig) bounds = big;
                var extent = Mathf.Clamp(bounds.extents.magnitude, 0.5f, 25f);
                var center = any ? bounds.center : Vector3.up;
                var direction = new Vector3(-0.55f, 0.42f, -0.72f).normalized;
                camera.transform.position = center + direction * (extent * 2.1f);
                camera.transform.LookAt(center);
                LastFraming = "peak " + peak + " particles, framed on " + center.ToString("0.0") + " extent " + extent.ToString("0.0");
                var sheet = new Texture2D(Cell * Grid, Cell * Grid, TextureFormat.RGB24, false);
                var magenta = 0L;
                try
                {
                    for (var index = 0; index < times.Length; index++)
                    {
                        root.Simulate(times[index], true, true, false);
                        // Unity 2019.4 simulates particle systems on worker jobs; rendering before they finish crashes the editor in batch mode
                        // (an access violation inside Camera.Render). Reading the particles back waits for them.
                        foreach (var system in systems) if (system.particleCount > 0) { if (particles.Length < system.particleCount) particles = new ParticleSystem.Particle[system.particleCount]; system.GetParticles(particles); }
                        camera.Render();
                        RenderTexture.active = target;
                        frame.ReadPixels(new Rect(0, 0, Cell, Cell), 0, 0);
                        frame.Apply(false);
                        RenderTexture.active = null;
                        foreach (var pixel in frame.GetPixels32())
                            if (pixel.r > 200 && pixel.b > 200 && pixel.g < 60) magenta++;
                        sheet.SetPixels((index % Grid) * Cell, (Grid - 1 - index / Grid) * Cell, Cell, Cell, frame.GetPixels());
                    }
                    sheet.Apply(false);
                    File.WriteAllBytes(Path.Combine(output, effect.slug + ".after.png"), sheet.EncodeToPNG());
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
                return magenta / (float)(Cell * Cell * times.Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        private static Camera Stage()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.40f, 0.44f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(8f, 1f, 8f);
            floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.11f, 0.115f, 0.13f) };
            var camera = new GameObject("preview camera").AddComponent<Camera>();
            camera.fieldOfView = 35f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.165f, 0.176f, 0.2f);
            camera.allowHDR = false;
            camera.allowMSAA = true;
            return camera;
        }
    }
}
