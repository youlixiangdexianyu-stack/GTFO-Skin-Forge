using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ForgeVfx.EditorTools
{
    /// <summary>Renders every effect prefab from one fixed camera, for review outside the game.
    ///
    ///     Unity.exe -batchmode -quit -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxPreview.Render
    ///         [-forgeVfxPreviewOut &lt;folder&gt;] -logFile &lt;log&gt;
    ///
    /// Do not pass -nographics: the frames are real camera renders. The default output is the repository's
    /// `artifacts/work/fx/previews/`. For each effect, six frames spaced over the effect's own length (for a sustained
    /// effect: while held, then two after it is let go) are written as `&lt;member&gt;/&lt;member&gt;-&lt;ms&gt;ms.png`, and `&lt;member&gt;-sheet.png` lays them
    /// out with the whole effect in the top row and each particle layer alone in the rows below, so the layering can be
    /// read. The effect plays at the origin facing up (+Z of the prefab mapped to world up) over a dark floor with
    /// colliders, beside a 1.8 m reference post; particles use a fixed random seed, so a rerun gives the same frames.</summary>
    public static class ForgeVfxPreview
    {
        private const int Width = 400, Height = 300;
        private const uint Seed = 20260926;
        private const float Hold = 0.6f;

        [MenuItem("Forge VFX/Render previews")]
        public static void Render() => ForgeVfxProject.RunAndExit("Forge VFX preview", RenderAll);

        private static void RenderAll()
        {
            var output = ForgeVfxProject.Argument("-forgeVfxPreviewOut")
                ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "artifacts", "work", "fx", "previews");
            var prefabs = ForgeVfxProject.EffectPrefabs();
            if (prefabs.Length == 0) throw new InvalidOperationException("No effect prefab in " + ForgeVfxProject.EffectsFolder);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = Stage();
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            camera.targetTexture = target;
            var frame = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                foreach (var path in prefabs) RenderEffect(path, camera, target, frame, output);
            }
            finally
            {
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(frame);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void RenderEffect(string path, Camera camera, RenderTexture target, Texture2D frame, string output)
        {
            var member = Path.GetFileNameWithoutExtension(path);
            var folder = Path.Combine(output, member);
            Directory.CreateDirectory(folder);
            var spec = ForgeVfxProject.Spec(member);
            var instance = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                Place(instance.transform, spec, camera);
                var root = instance.GetComponent<ParticleSystem>();
                var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                var renderers = instance.GetComponentsInChildren<ParticleSystemRenderer>(true);
                foreach (var system in systems)
                {
                    system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    system.useAutoRandomSeed = false;
                    system.randomSeed = Seed;
                }
                // A sustained member is held for Hold seconds and then let go; the last frame shows it dying out.
                var length = spec.Sustained ? Hold + systems.Max(s => s.main.startLifetime.constantMax)
                    : systems.Max(s => s.main.startDelay.constantMax + s.main.duration + s.main.startLifetime.constantMax);
                var times = spec.Sustained
                    ? new[] { 0.016f, 0.08f, Hold * 0.5f, Hold, Hold + (length - Hold) * 0.25f, Hold + (length - Hold) * 0.6f }
                    : new[] { 0.016f, length * 0.04f, length * 0.1f, length * 0.2f, length * 0.4f, length * 0.75f };
                var rows = new List<(string Label, ParticleSystemRenderer Only)> { ("all", null) };
                rows.AddRange(renderers.Select(r => (r.gameObject == instance ? "root" : r.name, r)));
                var sheet = new Texture2D(Width * times.Length, Height * rows.Count, TextureFormat.RGB24, false);
                try
                {
                    for (var row = 0; row < rows.Count; row++)
                    {
                        foreach (var renderer in renderers) renderer.enabled = rows[row].Only == null || renderer == rows[row].Only;
                        for (var column = 0; column < times.Length; column++)
                        {
                            var time = times[column];
                            if (spec.Sustained && time > Hold)
                            {
                                root.Simulate(Hold, true, true, false);
                                root.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                                root.Simulate(time - Hold, true, false, false);
                            }
                            else root.Simulate(time, true, true, false);
                            camera.Render();
                            RenderTexture.active = target;
                            frame.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                            frame.Apply(false);
                            RenderTexture.active = null;
                            if (row == 0)
                                File.WriteAllBytes(Path.Combine(folder, member + "-" + Mathf.RoundToInt(times[column] * 1000f).ToString("0000") + "ms.png"),
                                    frame.EncodeToPNG());
                            // The sheet's first row is at the top: texture rows count up from the bottom.
                            sheet.SetPixels(column * Width, (rows.Count - 1 - row) * Height, Width, Height, frame.GetPixels());
                        }
                    }
                    sheet.Apply(false);
                    File.WriteAllBytes(Path.Combine(output, member + "-sheet.png"), sheet.EncodeToPNG());
                    File.WriteAllText(Path.Combine(output, member + "-sheet.txt"),
                        "columns (s): " + string.Join(", ", times.Select(t => t.ToString("0.000"))) + "\nrows: "
                        + string.Join(", ", rows.Select(r => r.Label)) + "\n");
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
                Debug.Log("Forge VFX preview: " + member + " (" + rows.Count + " rows x " + times.Length + " frames, " + length.ToString("0.00") + " s)");
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        /// <summary>Places the effect the way its player would and frames it: a line member runs across the view from
        /// upper left to lower right; the flame jet fires to the right from the left; a sized member floats at mid
        /// height; any other member plays on the floor facing up. The camera backs off to the effect's extent.</summary>
        internal static void Place(Transform effect, ForgeVfxProject.Member spec, Camera camera)
        {
            var distance = Mathf.Max(3.4f, spec.Extent * 2.3f);
            var height = 0.05f;
            if (spec.Line)
            {
                var start = new Vector3(-1.4f, 1.1f, 0f);
                var end = new Vector3(1.4f, 0.4f, 0f);
                effect.SetPositionAndRotation(start, Quaternion.LookRotation(end - start));
                effect.localScale = new Vector3(1f, 1f, (end - start).magnitude);
                height = 0.75f;
            }
            else if (spec.Name == "forge_flame")
            {
                effect.SetPositionAndRotation(new Vector3(-spec.Extent * 0.9f, 1f, 0f), Quaternion.LookRotation(Vector3.right));
                height = 1f;
            }
            else if (spec.Size > 0f)
            {
                height = Mathf.Min(spec.Extent * 0.45f, 1.8f) + 0.3f;
                effect.SetPositionAndRotation(new Vector3(0f, height, 0f), Quaternion.identity);
            }
            else effect.SetPositionAndRotation(new Vector3(0f, 0.05f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var focus = new Vector3(0f, Mathf.Max(0.6f, height), 0f);
            camera.transform.position = focus + new Vector3(0f, 0.45f * distance / 3.4f, -distance);
            camera.transform.LookAt(focus);
        }

        /// <summary>The fixed stage: a dark floor that particles collide with, a 1.8 m post for scale, and the camera.</summary>
        internal static Camera Stage()
        {
            var unlit = Shader.Find("Unlit/Color");
            // The floor is lit, with a dim flat ambient only, so an effect's particle light shows on it.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.05f, 0.055f, 0.06f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
            floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.35f, 0.36f, 0.38f) };
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.transform.position = new Vector3(-1.4f, 0.9f, 0.6f);
            post.transform.localScale = new Vector3(0.06f, 1.8f, 0.06f);
            post.GetComponent<Renderer>().sharedMaterial = new Material(unlit) { color = new Color(0.16f, 0.17f, 0.19f) };
            var camera = new GameObject("preview camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 1.05f, -3.4f);
            camera.transform.LookAt(new Vector3(0f, 0.6f, 0f));
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.024f, 0.03f);
            camera.allowHDR = false;
            camera.allowMSAA = true;
            return camera;
        }
    }
}
