using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ForgeVfx.EditorTools
{
    /// <summary>Checks the built bundle the way a mod uses it, before anyone starts the game:
    ///
    ///     Unity.exe -batchmode -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxBundleCheck.Run
    ///         [-forgeVfxBundle &lt;bundle&gt;] [-forgeVfxPreviewOut &lt;folder&gt;] -logFile &lt;log&gt;
    ///
    /// It loads `Mods/ForgeCore/Assets/Vfx/forge-vfx.bundle` from disk (not the project's prefabs), requires exactly one
    /// prefab per effect member, instantiates each, requires every renderer's material to use a shader this GPU supports
    /// (a shader that failed to compile falls back to Unity's magenta error shader), plays each effect and renders it
    /// once, and counts magenta pixels in the frame. The frames go to `&lt;previews&gt;/bundle-check.png` (one tile per
    /// effect) and the findings to `bundle-check.txt`. Any missing prefab, unsupported shader or magenta frame fails
    /// the run with exit code 1.</summary>
    public static class ForgeVfxBundleCheck
    {
        private const int Width = 400, Height = 300, Columns = 4;
        private const uint Seed = 20260926;

        [MenuItem("Forge VFX/Check built bundle")]
        public static void Run() => ForgeVfxProject.RunAndExit("Forge VFX bundle check", Check);

        private static void Check()
        {
            var bundlePath = ForgeVfxProject.Argument("-forgeVfxBundle")
                ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "Mods", "ForgeCore", "Assets", "Vfx", ForgeVfxProject.BundleName);
            var output = ForgeVfxProject.Argument("-forgeVfxPreviewOut")
                ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "artifacts", "work", "fx", "previews");
            Directory.CreateDirectory(output);
            AssetBundle.UnloadAllAssetBundles(true);
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null) throw new InvalidOperationException("Unity could not load " + bundlePath);
            var report = new StringBuilder();
            var failures = new List<string>();
            report.AppendLine("bundle: " + bundlePath + " (" + new FileInfo(bundlePath).Length + " bytes)");
            report.AppendLine("graphics: " + SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsDeviceName);
            try
            {
                // The build addresses each prefab by its member name alone; nothing else is addressable.
                var names = bundle.GetAllAssetNames().Select(n => Path.GetFileNameWithoutExtension(n))
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var expected = ForgeVfxProject.Members.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (!names.SequenceEqual(expected))
                    failures.Add("prefabs " + string.Join(",", names) + " differ from the members " + string.Join(",", expected));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camera = ForgeVfxPreview.Stage();
                var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                camera.targetTexture = target;
                var frame = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                var rows = (expected.Length + Columns - 1) / Columns;
                var sheet = new Texture2D(Width * Columns, Height * rows, TextureFormat.RGB24, false);
                sheet.SetPixels(Enumerable.Repeat(Color.black, sheet.width * sheet.height).ToArray());
                try
                {
                    for (var index = 0; index < expected.Length; index++)
                    {
                        var member = expected[index];
                        var prefab = bundle.LoadAsset<GameObject>(member);
                        if (prefab == null) { failures.Add(member + ": not in the bundle"); continue; }
                        var instance = UnityEngine.Object.Instantiate(prefab);
                        try
                        {
                            CheckEffect(member, instance, camera, target, frame, report, failures);
                            var column = index % Columns;
                            var row = index / Columns;
                            sheet.SetPixels(column * Width, (rows - 1 - row) * Height, Width, Height, frame.GetPixels());
                        }
                        finally { UnityEngine.Object.DestroyImmediate(instance); }
                    }
                    sheet.Apply(false);
                    File.WriteAllBytes(Path.Combine(output, "bundle-check.png"), sheet.EncodeToPNG());
                }
                finally
                {
                    camera.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(frame);
                    UnityEngine.Object.DestroyImmediate(sheet);
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
            finally { bundle.Unload(true); }
            report.AppendLine(failures.Count == 0 ? "result: PASS" : "result: FAIL\n  " + string.Join("\n  ", failures));
            File.WriteAllText(Path.Combine(output, "bundle-check.txt"), report.ToString());
            Debug.Log("Forge VFX bundle check:\n" + report);
            if (failures.Count > 0) throw new InvalidOperationException(failures.Count + " bundle check failure(s); see bundle-check.txt");
        }

        private static void CheckEffect(string member, GameObject instance, Camera camera, RenderTexture target, Texture2D frame,
            StringBuilder report, List<string> failures)
        {
            var spec = ForgeVfxProject.Spec(member);
            var root = instance.GetComponent<ParticleSystem>();
            if (root == null) { failures.Add(member + ": no ParticleSystem at the root"); return; }
            var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            var renderers = instance.GetComponentsInChildren<ParticleSystemRenderer>(true);
            var shaders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var renderer in renderers)
            {
                var material = renderer.sharedMaterial;
                if (material == null || material.shader == null) { failures.Add(member + "/" + renderer.name + ": no material or shader"); continue; }
                shaders.Add(material.shader.name);
                if (!material.shader.isSupported || material.shader.name == "Hidden/InternalErrorShader")
                    failures.Add(member + "/" + renderer.name + ": shader " + material.shader.name + " is not supported");
                if (renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.mesh == null)
                    failures.Add(member + "/" + renderer.name + ": mesh render mode without a mesh");
            }
            foreach (var system in systems)
            {
                system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.useAutoRandomSeed = false;
                system.randomSeed = Seed;
            }
            ForgeVfxPreview.Place(instance.transform, spec, camera);
            // Well into the effect: a one-shot at a fifth of its length, a sustained effect while it is held.
            var length = systems.Max(s => s.main.startDelay.constantMax + s.main.duration + s.main.startLifetime.constantMax);
            var time = spec.Sustained ? 0.5f : Mathf.Max(0.05f, length * 0.12f);
            root.Simulate(time, true, true, false);
            var particles = systems.Sum(s => s.particleCount);
            camera.Render();
            RenderTexture.active = target;
            frame.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            frame.Apply(false);
            RenderTexture.active = null;
            var pixels = frame.GetPixels32();
            var magenta = pixels.Count(p => p.r > 200 && p.g < 40 && p.b > 200);
            var lit = pixels.Count(p => p.r + p.g + p.b > 150);
            if (particles == 0) failures.Add(member + ": no particles alive at " + time.ToString("0.00") + " s");
            if (magenta > 20) failures.Add(member + ": " + magenta + " magenta pixels (a shader fell back to the error shader)");
            if (lit == 0) failures.Add(member + ": the frame is empty");
            report.AppendLine(member + ": layers=" + systems.Length + " particles=" + particles + " at " + time.ToString("0.00")
                + " s, lit pixels=" + lit + ", magenta=" + magenta + ", shaders=" + string.Join("|", shaders));
        }
    }
}
