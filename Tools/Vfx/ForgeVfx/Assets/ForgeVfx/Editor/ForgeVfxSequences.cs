using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ForgeVfx.EditorTools
{
    /// <summary>Renders the energy weapons' effect sequences the way the energy mod plays them, for review outside the
    /// game: every phase of a shot (muzzle, charge, flight, hit, explosion, lingering, fade) in one timeline per weapon,
    /// stepped at 60 Hz with the effects moved, switched and stopped as the mod's presentation does.
    ///
    ///     Unity.exe -batchmode -quit -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxSequences.Render
    ///         [-forgeVfxSequenceOut &lt;folder&gt;] -logFile &lt;log&gt;
    ///
    /// Do not pass -nographics. The stage is a dark industrial bay (floor, back and side wall, all colliders, a dim flat
    /// ambient) seen from the side and, for the held and charged weapons, from the shooter's eye. Frames go to
    /// `&lt;out&gt;/&lt;sequence&gt;/&lt;view&gt;-&lt;ms&gt;ms.png` (default `artifacts/work/energy-vfx/sequences/`). A state child the prefab
    /// does not carry is skipped, so older prefabs render the same timelines.</summary>
    public static class ForgeVfxSequences
    {
        private const int Width = 512, Height = 288;
        private const float Step = 1f / 60f;
        private const uint Seed = 20261002;
        private static readonly Vector3 Muzzle = new Vector3(-2.6f, 1.3f, 0f);
        /// <summary>`-forgeVfxSequenceAfter` renders the timelines as the current presentation plays them (full-length
        /// blast tracer, disc trail); without it, as the previous presentation did (1.8 m tracer, orb glow on the disc).</summary>
        private static readonly bool After = Array.IndexOf(Environment.GetCommandLineArgs(), "-forgeVfxSequenceAfter") >= 0;

        [MenuItem("Forge VFX/Render energy weapon sequences")]
        public static void Render() => ForgeVfxProject.RunAndExit("Forge VFX sequences", RenderAll);

        private sealed class View
        {
            internal string Name;
            internal Vector3 Position, Target;
            internal float Fov;
        }

        private static readonly View Side = new View { Name = "side", Position = new Vector3(0f, 1.55f, -5.2f), Target = new Vector3(0f, 1.05f, 0.3f), Fov = 52f };
        private static readonly View Eye = new View { Name = "eye", Position = new Vector3(-3.05f, 1.48f, -0.22f), Target = new Vector3(3f, 1.1f, 0.1f), Fov = 70f };

        private sealed class Sequence
        {
            internal string Name;
            internal float Length;
            internal float[] Side, Eye;
            internal Action<float, Stage> Drive;
        }

        private static void RenderAll()
        {
            var output = ForgeVfxProject.Argument("-forgeVfxSequenceOut")
                ?? Path.Combine(ForgeVfxProject.RepositoryRoot, "artifacts", "work", "energy-vfx", "sequences");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var pixelLights = QualitySettings.pixelLightCount;
            BuildStage();
            var camera = new GameObject("review camera").AddComponent<Camera>();
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 60f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.006f, 0.007f, 0.009f);
            camera.allowHDR = false;
            camera.allowMSAA = true;
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            camera.targetTexture = target;
            var frame = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                foreach (var sequence in Sequences())
                {
                    var folder = Path.Combine(output, sequence.Name);
                    Directory.CreateDirectory(folder);
                    var stage = new Stage();
                    try
                    {
                        var steps = Mathf.CeilToInt(sequence.Length / Step);
                        for (var index = 0; index <= steps; index++)
                        {
                            var time = index * Step;
                            sequence.Drive(time, stage);
                            stage.Advance(time);
                            Capture(sequence.Side, Side, time, camera, target, frame, folder);
                            Capture(sequence.Eye, Eye, time, camera, target, frame, folder);
                        }
                    }
                    finally { stage.Dispose(); }
                    Debug.Log("Forge VFX sequence: " + sequence.Name);
                }
            }
            finally
            {
                // The light count is a project setting Unity would save; the project keeps its own.
                QualitySettings.pixelLightCount = pixelLights;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(frame);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void Capture(float[] times, View view, float time, Camera camera, RenderTexture target, Texture2D frame, string folder)
        {
            if (times == null) return;
            foreach (var at in times)
            {
                if (Mathf.Abs(at - time) >= Step * 0.5f) continue;
                camera.transform.position = view.Position;
                camera.transform.LookAt(view.Target);
                camera.fieldOfView = view.Fov;
                camera.Render();
                RenderTexture.active = target;
                frame.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                frame.Apply(false);
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(folder, view.Name + "-" + Mathf.RoundToInt(at * 1000f).ToString("0000") + "ms.png"), frame.EncodeToPNG());
            }
        }

        /// <summary>A dark bay: floor, a back wall at z = 2.5, a right wall at x = 2.6 that shots hit, all colliders.</summary>
        private static void BuildStage()
        {
            // Every effect light lights the stage per pixel, as the game's effect lights do.
            QualitySettings.pixelLightCount = 32;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.035f, 0.038f, 0.045f);
            var concrete = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.31f, 0.32f) };
            concrete.SetFloat("_Glossiness", 0.15f);
            var metal = new Material(Shader.Find("Standard")) { color = new Color(0.22f, 0.24f, 0.26f) };
            metal.SetFloat("_Metallic", 0.6f);
            metal.SetFloat("_Glossiness", 0.35f);
            Block("floor", new Vector3(0f, -0.05f, 0f), new Vector3(14f, 0.1f, 8f), concrete);
            Block("back wall", new Vector3(0f, 2f, 2.55f), new Vector3(14f, 4f, 0.1f), concrete);
            Block("right wall", new Vector3(2.65f, 2f, 0f), new Vector3(0.1f, 4f, 5f), metal);
            for (var x = -4f; x <= 2f; x += 1.5f) Block("rib", new Vector3(x, 2f, 2.45f), new Vector3(0.12f, 4f, 0.12f), metal);
        }

        private static void Block(string name, Vector3 position, Vector3 size, Material material)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.position = position;
            block.transform.localScale = size;
            block.GetComponent<Renderer>().sharedMaterial = material;
        }

        // ---- the stage's live effects -----------------------------------------------------------------------------

        private sealed class Stage : IDisposable
        {
            private readonly List<Fx> _effects = new List<Fx>();
            private readonly Dictionary<string, Fx> _named = new Dictionary<string, Fx>(StringComparer.Ordinal);
            private readonly List<GameObject> _props = new List<GameObject>();
            private uint _seed = Seed;

            internal Fx Spawn(string member, string name = null)
            {
                var fx = new Fx(member, _seed++);
                _effects.Add(fx);
                if (name != null) _named[name] = fx;
                return fx;
            }

            internal Fx Named(string name) => _named.TryGetValue(name, out var fx) ? fx : null;

            internal T Prop<T>(T prop) where T : UnityEngine.Object
            {
                _props.Add(prop is Component component ? component.gameObject : prop as GameObject);
                return prop;
            }

            internal void Advance(float time)
            {
                foreach (var fx in _effects) fx.Step(Step);
            }

            public void Dispose()
            {
                foreach (var fx in _effects) fx.Dispose();
                foreach (var prop in _props) if (prop != null) UnityEngine.Object.DestroyImmediate(prop);
            }
        }

        private sealed class Fx : IDisposable
        {
            private readonly GameObject _object;
            private readonly ParticleSystem _root;
            private readonly ParticleSystem[] _layers;
            private bool _started;

            internal Fx(string member, uint seed)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForgeVfxProject.EffectsFolder + "/" + member + ".prefab")
                    ?? throw new InvalidOperationException("No prefab " + member);
                _object = (GameObject)UnityEngine.Object.Instantiate(prefab);
                _root = _object.GetComponent<ParticleSystem>();
                _layers = _object.GetComponentsInChildren<ParticleSystem>(true);
                foreach (var layer in _layers)
                {
                    layer.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    layer.useAutoRandomSeed = false;
                    layer.randomSeed = seed++;
                }
                _object.SetActive(false);
            }

            internal Fx Place(Vector3 position, Quaternion rotation, float scale = 1f)
            {
                _object.transform.SetPositionAndRotation(position, rotation);
                _object.transform.localScale = Vector3.one * scale;
                return this;
            }

            internal Fx Stretch(Vector3 start, Vector3 end)
            {
                var travel = end - start;
                _object.transform.SetPositionAndRotation(start, Quaternion.LookRotation(travel));
                _object.transform.localScale = new Vector3(1f, 1f, travel.magnitude);
                return this;
            }

            internal Fx Tint(Color tint)
            {
                foreach (var layer in _layers)
                {
                    var main = layer.main;
                    var color = main.startColor;
                    if (color.mode == ParticleSystemGradientMode.Color) color.color *= tint;
                    else { color.colorMin *= tint; color.colorMax *= tint; }
                    main.startColor = color;
                }
                return this;
            }

            internal Fx Emit(string prefix, bool on)
            {
                foreach (var layer in _layers)
                {
                    if (!layer.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var emission = layer.emission;
                    emission.enabled = on;
                }
                return this;
            }

            internal Fx Start()
            {
                _object.SetActive(true);
                _root.Simulate(0f, true, true, false);
                _started = true;
                return this;
            }

            /// <summary>Activates and restarts an inactive state child, as the mod's PlayState does; absent is skipped.</summary>
            internal Fx State(string child)
            {
                var state = _object.transform.Find(child);
                if (state == null) return this;
                state.gameObject.SetActive(true);
                state.GetComponent<ParticleSystem>().Simulate(0f, true, true, false);
                return this;
            }

            internal void Stop() => _root.Stop(true, ParticleSystemStopBehavior.StopEmitting);

            internal void Step(float delta)
            {
                if (_started) _root.Simulate(delta, true, false, false);
            }

            public void Dispose() => UnityEngine.Object.DestroyImmediate(_object);
        }

        // ---- the sequences ----------------------------------------------------------------------------------------

        private static bool At(float time, float at) => time >= at - Step * 0.5f && time < at + Step * 0.5f;

        private static bool Is(float time, float from, float to) => time >= from - Step * 0.5f && time < to - Step * 0.5f;

        private static IEnumerable<Sequence> Sequences()
        {
            var wallHit = new Vector3(2.58f, 1.05f, 0.1f);
            yield return new Sequence
            {
                Name = "beam", Length = 1.4f,
                Side = new[] { 0.033f, 0.1f, 0.4f, 0.95f, 1.05f, 1.2f },
                Eye = new[] { 0.1f, 0.6f },
                Drive = (time, stage) =>
                {
                    if (At(time, 0f)) stage.Spawn("forge_energy_beam", "beam").Stretch(Muzzle, wallHit).Emit("impact", true).Start().State("ignite");
                    var beam = stage.Named("beam");
                    if (Is(time, 0f, 1f))
                    {
                        // A held beam sweeps a little as the shooter's aim drifts.
                        var sweep = wallHit + new Vector3(0f, Mathf.Sin(time * 3f) * 0.15f, Mathf.Sin(time * 2.1f) * 0.25f);
                        beam.Stretch(Muzzle, sweep);
                    }
                    if (At(time, 1f)) beam.Stop();
                }
            };

            yield return new Sequence
            {
                Name = "chain-arc", Length = 0.8f,
                Side = new[] { 0.017f, 0.05f, 0.083f, 0.133f, 0.2f, 0.3f, 0.5f },
                Eye = new[] { 0.05f },
                Drive = (time, stage) =>
                {
                    var hops = new[] { Muzzle, new Vector3(-0.6f, 0.9f, 0.6f), new Vector3(0.9f, 1.25f, -0.4f), new Vector3(2.2f, 0.75f, 0.5f) };
                    for (var hop = 0; hop < hops.Length - 1; hop++)
                        if (At(time, hop * 0.025f + 0.0001f) || (hop == 0 && At(time, 0f)))
                            stage.Spawn("forge_lightning_arc").Stretch(hops[hop], hops[hop + 1]).Emit("end", true).Start();
                }
            };

            yield return new Sequence
            {
                Name = "flamethrower", Length = 2.6f,
                Side = new[] { 0.05f, 0.15f, 0.4f, 0.8f, 1.2f, 1.3f, 1.6f, 2.4f },
                Eye = new[] { 0.4f, 1.0f },
                Drive = (time, stage) =>
                {
                    var start = Muzzle + new Vector3(0f, -0.1f, 0f);
                    if (At(time, 0f)) stage.Spawn("forge_flame", "flame").Place(start, Quaternion.LookRotation(Vector3.right)).Start().State("ignite");
                    if (Is(time, 0.05f, 1.2f) && Mathf.Repeat(time, 0.08f) < Step)
                        stage.Spawn("forge_flame_impact").Place(new Vector3(2.55f, 1.15f, 0f), Quaternion.LookRotation(Vector3.up)).Start();
                    if (At(time, 1.2f)) stage.Named("flame").Stop();
                }
            };

            yield return ChargeOrb(electric: false);
            yield return ChargeOrb(electric: true);

            yield return new Sequence
            {
                Name = "blast-gun", Length = 2.2f,
                Side = new[] { 0.017f, 0.033f, 0.067f, 0.133f, 0.3f, 0.6f, 1.0f, 1.8f },
                Eye = new[] { 0.033f, 0.133f },
                Drive = (time, stage) =>
                {
                    var hit = new Vector3(2.5f, 1.0f, 0.1f);
                    if (!At(time, 0f)) return;
                    var tracerEnd = After ? hit : Muzzle + (hit - Muzzle).normalized * 1.8f;
                    stage.Spawn("forge_blast_tracer").Stretch(Muzzle, tracerEnd).Start();
                    stage.Spawn("forge_explosion").Place(hit, Quaternion.identity, 1f).Start();
                }
            };

            yield return new Sequence
            {
                Name = "ricochet-disc", Length = 1.2f,
                Side = new[] { 0.05f, 0.1f, 0.18f, 0.22f, 0.27f, 0.35f, 0.6f, 1.0f },
                Drive = Disc
            };

            yield return new Sequence
            {
                Name = "gravity-core", Length = 3.4f,
                Side = new[] { 0.1f, 0.2f, 0.3f, 0.45f, 0.8f, 1.5f, 2.0f, 2.15f, 2.3f, 2.5f, 3.0f },
                Drive = GravityCore
            };
        }

        /// <summary>The charge orb (or, electric, the shock orb): charged at the muzzle for 1.5 s, launched at 1.6 s at
        /// full power, then a detonation at the wall (charge orb) or a fade there (shock orb, discharging every 0.4 s).</summary>
        private static Sequence ChargeOrb(bool electric)
        {
            const float launch = 1.6f;
            var tint = new Color(1f, 0.7f, 1f);
            var targets = new[] { new Vector3(-1f, 0.15f, 0.9f), new Vector3(0.4f, 2.3f, -0.6f), new Vector3(1.6f, 0.2f, -0.8f) };
            float impact = 0f;
            Vector3 position = Muzzle;
            return new Sequence
            {
                Name = electric ? "shock-orb" : "charge-orb",
                Length = electric ? launch + 1.2f : launch + 2.4f,
                Side = electric
                    ? new[] { 0.3f, 1.0f, 1.45f, launch + 0.033f, launch + 0.1f, launch + 0.2f, launch + 0.32f, launch + 0.38f, launch + 0.45f, launch + 0.8f }
                    : new[] { 0.1f, 0.6f, 1.2f, 1.45f, launch - 0.02f, launch + 0.033f, launch + 0.15f, launch + 0.3f, launch + 0.36f, launch + 0.43f, launch + 0.6f, launch + 1.0f, launch + 2.0f },
                Eye = new[] { 0.6f, 1.45f, launch + 0.05f },
                Drive = (time, stage) =>
                {
                    var muzzle = Muzzle;
                    if (time < launch - Step * 0.5f)
                    {
                        var power = Mathf.Clamp01((time - 0.3f) / 1.1f);
                        var radius = 0.12f + power * 0.18f;
                        if (At(time, 0f))
                        {
                            var charging = stage.Spawn("forge_plasma_orb", "charging");
                            if (electric) charging.Tint(tint);
                            charging.Start().State("charging");
                            if (electric) charging.State("storm");
                        }
                        stage.Named("charging").Place(muzzle, Quaternion.identity, radius * 0.55f / 0.3f);
                        if (At(time, 1.4f)) stage.Named("charging").State("charged");
                        return;
                    }
                    if (At(time, launch))
                    {
                        stage.Named("charging").Stop();
                        position = muzzle;
                        var orb = stage.Spawn("forge_plasma_orb", "orb");
                        if (electric) orb.Tint(tint);
                        orb.Place(position, Quaternion.identity, 1f).Start().State("launch");
                        if (electric) orb.State("storm");
                    }
                    var flight = stage.Named("orb");
                    if (impact == 0f)
                    {
                        position += Vector3.right * 14f * Step;
                        if (position.x >= 2.3f)
                        {
                            position.x = 2.3f;
                            impact = time;
                            if (electric) flight.State("fizzle");
                            else
                            {
                                flight.Stop();
                                stage.Spawn("forge_plasma_explosion").Place(position, Quaternion.identity, 4f / 3f).Start();
                            }
                        }
                        flight.Place(position, Quaternion.identity, 1f);
                        if (electric && (At(time, launch) || At(time, launch + 0.4f)))
                            foreach (var target in targets)
                                stage.Spawn("forge_plasma_discharge").Stretch(position, target).Start();
                    }
                    else if (electric)
                    {
                        var fade = Mathf.Clamp01((time - impact) / 0.18f);
                        flight.Place(position, Quaternion.identity, 1f - fade * 0.9f);
                        if (fade >= 1f && At(time, impact + 0.183f)) flight.Stop();
                    }
                }
            };
        }

        private static void Disc(float time, Stage stage)
        {
            // Launched at 24 m/s, it bounces off the right wall at 0.2 s and leaves the view; its glow (before) or trail
            // (after) rides it.
            var start = Muzzle + new Vector3(0.2f, -0.1f, 0f);
            var bounce = new Vector3(2.45f, 1.15f, 0.05f);
            var away = new Vector3(-0.75f, 0.02f, 0.66f).normalized;
            var position = time < 0.2f ? Vector3.Lerp(start, bounce, time / 0.2f) : bounce + away * 24f * (time - 0.2f);
            if (At(time, 0f))
            {
                var blade = stage.Prop(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
                blade.name = "disc";
                blade.transform.localScale = new Vector3(0.6f, 0.03f, 0.6f);
                blade.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.4f, 0.47f, 0.5f) };
                UnityEngine.Object.DestroyImmediate(blade.GetComponent<Collider>());
                if (After) stage.Prop(DiscTrail.Create(blade));
                else stage.Spawn("forge_plasma_orb", "glow").Start();
            }
            var disc = GameObject.Find("disc");
            if (disc == null) return;
            if (time > 0.9f) { disc.SetActive(false); stage.Named("glow")?.Stop(); return; }
            disc.transform.position = position;
            disc.transform.rotation = Quaternion.Euler(10f, time * 720f, 0f);
            stage.Named("glow")?.Place(position, Quaternion.identity, 0.07f / 0.3f);
            if (After) DiscTrail.Record(position, time);
            if (At(time, 0.2f)) stage.Spawn("forge_sparks").Place(bounce, Quaternion.LookRotation(Vector3.left)).Start();
        }

        private static void GravityCore(float time, Stage stage)
        {
            // The core flies 0.25 s at 0.15 scale, opens over 0.2 s, holds (2 s here; 4.5 s in the game), collapses in
            // its last 0.35 s and is let go.
            const float open = 0.25f, until = open + 2f;
            var start = Muzzle;
            var land = new Vector3(0.2f, 1.25f, 0.2f);
            if (At(time, 0f)) stage.Spawn("forge_black_hole", "hole").Start();
            var hole = stage.Named("hole");
            if (time < open - Step * 0.5f)
            {
                var t = time / open;
                hole.Place(Vector3.Lerp(start, land, t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * 0.25f, Quaternion.identity, 0.15f);
                return;
            }
            if (At(time, open)) hole.State("open");
            var remaining = until - time;
            if (At(time, until - 0.35f)) hole.State("collapse");
            if (remaining <= 0f)
            {
                if (At(time, until)) hole.Stop();
                return;
            }
            var scale = Mathf.Min(Mathf.Clamp01((time - open) / 0.2f), Mathf.Clamp01(remaining / 0.35f));
            hole.Place(land, Quaternion.identity, Mathf.Max(0.02f, Mathf.Max(scale, At(time, open) ? 0.1f : 0f)));
        }

        /// <summary>The disc's streak, as the mod draws it with a TrailRenderer: a line through the positions of the last
        /// 0.12 s, full width at the disc and none at the tail, in the bundle's additive trail material.</summary>
        private static class DiscTrail
        {
            private static LineRenderer _line;
            private static readonly List<(Vector3 Position, float Time)> Points = new List<(Vector3, float)>();

            internal static LineRenderer Create(GameObject disc)
            {
                Points.Clear();
                var holder = new GameObject("disc trail");
                _line = holder.AddComponent<LineRenderer>();
                _line.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(ForgeVfxProject.Root + "Own/Materials/trail_additive.mat")
                    ?? throw new InvalidOperationException("No trail_additive material.");
                _line.textureMode = LineTextureMode.Stretch;
                _line.widthCurve = AnimationCurve.Linear(0f, 0.06f, 1f, 0.005f);
                var gradient = new Gradient();
                gradient.SetKeys(new[] { new GradientColorKey(new Color(0.8f, 0.95f, 1f), 0f), new GradientColorKey(new Color(0.15f, 0.55f, 1f), 1f) },
                    new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
                _line.colorGradient = gradient;
                _line.useWorldSpace = true;
                _line.positionCount = 0;
                return _line;
            }

            internal static void Record(Vector3 position, float time)
            {
                Points.Insert(0, (position, time));
                Points.RemoveAll(point => time - point.Time > 0.12f);
                _line.positionCount = Points.Count;
                for (var index = 0; index < Points.Count; index++) _line.SetPosition(index, Points[index].Position);
            }
        }
    }
}
