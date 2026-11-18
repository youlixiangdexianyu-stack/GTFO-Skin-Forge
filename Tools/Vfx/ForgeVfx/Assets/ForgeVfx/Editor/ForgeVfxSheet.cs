using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVfx.EditorTools
{
    /// <summary>What an effect really measures and how it really plays, the way ForgeVisual plays it (every layer on Hierarchy scaling, the root placed
    /// and scaled by the slot table, emission stopped at the end of the slot's window). Batch entries, driven by `Tools/Vfx/FxLibrary/fx_slots.py` and `fx_sheet.py`:
    ///
    ///     ... ForgeVfxSheet.Measure  -forgeVfxOut &lt;folder&gt; -forgeVfxEffects slug,slug     (&lt;slug&gt;.measure.json from the converted prefabs at scale 1)
    ///     ... ForgeVfxSheet.Sheet    -forgeVfxOut &lt;folder&gt; -forgeVfxPlan &lt;plan.json&gt;      (&lt;slot&gt;.cells.png + &lt;slot&gt;.cells.json from the built bundles)
    /// </summary>
    public static class ForgeVfxSheet
    {
        private const float Step = 1f / 30f;
        private const int Cell = 300;
        private static string Out => ForgeVfxProject.Argument("-forgeVfxOut") ?? throw new InvalidOperationException("-forgeVfxOut is required");

        public static void Measure() => ForgeVfxProject.RunAndExit("Forge VFX measure", MeasureAll);
        public static void Sheet() => ForgeVfxProject.RunAndExit("Forge VFX sheet", SheetAll);

        /// <summary>The same thing the converter does to every library prefab (ForgeVfxConvert.TakeScaleFromTransforms).</summary>
        private static void Normalize(GameObject o)
        {
            foreach (var layer in o.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = layer.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
        }

        // ---- measure ----

        [Serializable]
        private sealed class Measured
        {
            public string slug;
            public bool loop;
            public float seconds, maxSize, extent, flat, duration;
            public float[] centre, drift;
            /// <summary>The box of what is seen (min x, y, z then max x, y, z, m from the root): like the extent's box, but each particle only as large as
            /// the part of its texture that shows (<see cref="SeenShare"/>). Empty when nothing shows.</summary>
            public float[] box;
            public int peak;
        }

        private static void MeasureAll()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            var filter = ForgeVfxProject.Argument("-forgeVfxEffects");
            if (filter == null) throw new InvalidOperationException("-forgeVfxEffects is required");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var slug in filter.Split(','))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForgeVfxConvert.ConvertedRoot + slug + ".prefab");
                if (prefab == null) throw new InvalidOperationException("no converted prefab " + slug);
                var instance = UnityEngine.Object.Instantiate(prefab);
                try { File.WriteAllText(Path.Combine(output, slug + ".measure.json"), JsonUtility.ToJson(MeasureOne(slug, instance), true), new UTF8Encoding(false)); }
                finally { UnityEngine.Object.DestroyImmediate(instance); }
            }
        }

        /// <summary>How much of a particle's quad shows: the share of its half size (0..1) out to the furthest texel of its texture that is not clear (alpha above 0.1;
        /// for an added layer alpha times brightness), from the texture's centre. 1 for a mesh particle or a layer drawn without a texture, 0 for a renderer that draws nothing.</summary>
        private static float SeenShare(ParticleSystemRenderer renderer)
        {
            if (renderer == null || !renderer.enabled) return 0f;
            var material = renderer.sharedMaterial;
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh || material == null) return 1f;
            var texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            if (texture == null) return 1f;
            var added = material.HasProperty("_DstBlend") ? Mathf.Approximately(material.GetFloat("_DstBlend"), (float)BlendMode.One) : material.shader.name.Contains("Additive");
            var fromRgb = material.IsKeywordEnabled("_ALPHA_FROM_RGB");
            const int side = 64;
            var target = RenderTexture.GetTemporary(side, side, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            var read = new Texture2D(side, side, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, side, side), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            var pixels = read.GetPixels();
            UnityEngine.Object.DestroyImmediate(read);
            var share = 0f;
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                {
                    var c = pixels[y * side + x];
                    var bright = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    var alpha = fromRgb ? bright : c.a;
                    if ((added ? alpha * bright : alpha) > 0.1f)
                        share = Mathf.Max(share, Mathf.Max(Mathf.Abs(x + 0.5f - side * 0.5f), Mathf.Abs(y + 0.5f - side * 0.5f)) / (side * 0.5f));
                }
            return share;
        }

        /// <summary>One run of the prefab at scale 1, root at the origin, unrotated: at most 3 s of emission, then what is left finishes. The extent is the largest box side of the
        /// particles of body size (a quarter of the largest particle and up), so a few far sparks do not count; flat is the same over x and y only.</summary>
        private static Measured MeasureOne(string slug, GameObject instance)
        {
            Normalize(instance);
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            var root = instance.GetComponent<ParticleSystem>();
            var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            var m = new Measured { slug = slug };
            foreach (var s in systems)
            {
                // A fixed seed: the Scale fx_slots works out from this measure must come out the same on every run, or the size table moves by itself.
                s.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                s.useAutoRandomSeed = false;
                s.randomSeed = 20261105;
                m.loop |= s.main.loop;
                m.duration = Mathf.Max(m.duration, s.main.duration);
            }
            var samples = new List<(Vector3 Position, float Size, Vector3 Velocity, float Seen)>();
            var seen = new Dictionary<ParticleSystem, float>();
            foreach (var s in systems) seen[s] = SeenShare(s.GetComponent<ParticleSystemRenderer>());
            var buffer = new ParticleSystem.Particle[1024];
            root.Simulate(0f, true, true, false);
            root.Play(true);
            var stopped = false;
            var t = 0f;
            for (var frame = 0; t < 8f; frame++, t += Step)
            {
                if (!stopped && t >= 3f) { root.Stop(true, ParticleSystemStopBehavior.StopEmitting); stopped = true; }
                root.Simulate(Step, true, false, false);
                var alive = 0;
                foreach (var s in systems)
                {
                    var count = s.particleCount;
                    alive += count;
                    if (count == 0) continue;
                    if (buffer.Length < count) buffer = new ParticleSystem.Particle[count];
                    s.GetParticles(buffer);
                    var local = s.main.simulationSpace != ParticleSystemSimulationSpace.World;
                    if (frame % 3 != 0) continue;
                    var lossy = s.transform.lossyScale;
                    for (var k = 0; k < count; k++)
                    {
                        var size3 = buffer[k].GetCurrentSize3D(s);
                        var size = Mathf.Max(size3.x * lossy.x, size3.y * lossy.y);
                        if (float.IsInfinity(size) || float.IsNaN(size)) continue;
                        samples.Add((local ? s.transform.TransformPoint(buffer[k].position) : buffer[k].position, size, buffer[k].velocity, seen[s]));
                    }
                }
                m.peak = Mathf.Max(m.peak, alive);
                if (alive > 0) m.seconds = t;
            }
            if (samples.Count == 0) { m.centre = new[] { 0f, 0f, 0f }; m.drift = new[] { 0f, 0f, 0f }; return m; }
            m.maxSize = samples.Max(s => s.Size);
            var body = samples.Where(s => s.Size >= m.maxSize * 0.25f).ToList();
            var box = new Bounds(body[0].Position, Vector3.one * body[0].Size);
            foreach (var s in body) box.Encapsulate(new Bounds(s.Position, Vector3.one * s.Size));
            m.extent = Mathf.Max(box.size.x, box.size.y, box.size.z);
            m.flat = Mathf.Max(box.size.x, box.size.y);
            m.centre = new[] { box.center.x, box.center.y, box.center.z };
            // what is seen of it: each particle only as large as the part of its texture that shows (a soft glow's quad is mostly clear), the faint ones left out
            var drawn = samples.Where(s => s.Seen > 0f).Select(s => (s.Position, Size: s.Size * s.Seen)).ToList();
            if (drawn.Count > 0)
            {
                var largest = drawn.Max(s => s.Size);
                var shown = drawn.Where(s => s.Size >= largest * 0.25f).ToList();
                var seenBox = new Bounds(shown[0].Position, Vector3.one * shown[0].Size);
                foreach (var s in shown) seenBox.Encapsulate(new Bounds(s.Position, Vector3.one * s.Size));
                m.box = new[] { seenBox.min.x, seenBox.min.y, seenBox.min.z, seenBox.max.x, seenBox.max.y, seenBox.max.z };
            }
            var drift = Vector3.zero;
            foreach (var s in samples) drift += s.Velocity;
            drift /= samples.Count;
            m.drift = new[] { drift.x, drift.y, drift.z };
            return m;
        }

        // ---- the sheet ----

#pragma warning disable 0649 // JsonUtility fills these fields.
        [Serializable]
        private sealed class PlanSlot
        {
            public string slot, item, bundle;
            public float[] aim;          // the direction the caller passes
            public float scale = 1f;     // BaseScale
            public float callerSize;     // the size the caller passes (0: none)
            public float size;           // Size of the table (0: none)
            public bool line, axisUp;
            public float seconds;        // emission window (a hold: how long the caller keeps holding)
            public float length;         // a line's length
            public float authored = 1f;  // a line's authored length
            public float view = 6f;      // metres across a cell
            public string laserHead;     // LaserHead of the table (empty: not a laser)
            public string[] laserBodies; // LaserBodies of the table
            public float laserTile;      // LaserTile of the table
            public string eye = "off";   // the first-person cell: gun (the effect at the muzzle), follow (flying away along the aim), off (seen from eyeDistance)
            public float eyeDistance = 5f, eyeHeight = 1.6f;
            public float[] colour;       // the caller's colour (linear 0..1 RGB; empty: white), as Fx.Play passes it
            public float move;           // a flying round's speed (m/s): the copy travels along the aim and the camera with it, so a world-space tail streams out behind
            public float[] light;        // the slot's light: r, g, b, range, peak, life, delay (empty: none), drawn on the floor (Hidden/ForgeVfx/Sheet Floor)
            public bool held;            // a held slot: its light stays on while held; a one-shot's flashes and fades over its life
            public float repeat;         // a slot its caller plays again and again (fx_sheet --repeat): played again every this many seconds while it runs
            public float endFade;        // EndFade of the table: the main cells draw the line as one that hit nothing, its last metres faded
            public float spacing;        // Spacing of the table: an area slot, drawn as ForgeVisual's Fx.Field lays it, copies this far apart over the caller's size
            public int copies;           // Copies of the table: the most copies such an area takes
            public float[] body;         // Body of the table (min xyz, max xyz at scale 1): the copy stands clear of the floor, as ForgeVisual's Fx.Seat seats it under open sky
        }

        [Serializable] private sealed class Plan { public PlanSlot[] slots; }
#pragma warning restore 0649

        [Serializable]
        private sealed class Cells
        {
            public string slot;
            public float[] times;
            public int[] particles;
            public float gone;     // seconds until nothing is alive (-1: still alive at the end)
            public float magenta;
            public float cover, coverAt, peak; // the largest share of the first-person view it ever changes, when, and how bright the brightest change is (0..1)
        }

        private static readonly float[] Times = { 0.03f, 0.08f, 0.3f, 0.8f, 3f };

        private static void SheetAll()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(ForgeVfxProject.Argument("-forgeVfxPlan") ?? throw new InvalidOperationException("-forgeVfxPlan is required")));
            AssetBundle.UnloadAllAssetBundles(true);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = Stage(out var bars);
            var target = new RenderTexture(Cell, Cell, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            camera.targetTexture = target;
            var frame = new Texture2D(Cell, Cell, TextureFormat.RGB24, false);
            var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            try
            {
                foreach (var slot in plan.slots)
                {
                    if (File.Exists(Path.Combine(output, slot.slot + ".cells.png"))) continue;
                    if (!bundles.TryGetValue(slot.bundle, out var bundle))
                    {
                        bundles[slot.bundle] = bundle = AssetBundle.LoadFromFile(slot.bundle);
                        if (bundle == null) throw new InvalidOperationException("Unity could not load " + slot.bundle);
                    }
                    var prefab = bundle.LoadAsset<GameObject>(slot.item);
                    if (prefab == null) throw new InvalidOperationException(slot.slot + ": " + slot.item + " is not in " + slot.bundle);
                    File.WriteAllText(Path.Combine(output, "rendering.txt"), slot.slot);
                    RenderSlot(slot, prefab, camera, target, frame, bars, output);
                    File.Delete(Path.Combine(output, "rendering.txt"));
                }
            }
            finally
            {
                foreach (var b in bundles.Values) if (b != null) b.Unload(true);
                camera.targetTexture = null;
                target.Release();
            }
        }

        private static void RenderSlot(PlanSlot slot, GameObject prefab, Camera camera, RenderTexture target, Texture2D frame, Transform bars, string output)
        {
            var instance = slot.spacing > 0f ? Field(prefab, slot) : UnityEngine.Object.Instantiate(prefab);
            try
            {
                Normalize(instance);
                var aim = new Vector3(slot.aim[0], slot.aim[1], slot.aim[2]);
                var origin = aim.y > 0.9f ? Vector3.up * 0.05f : Vector3.up * 1.2f;
                // a round falling the whole sheet long starts high enough that it never meets the floor
                if (slot.move > 0f && aim.normalized.y < -0.1f) origin = Vector3.up * (1.2f + slot.move * 8f * -aim.normalized.y);
                var reach = slot.line ? 1f : slot.size > 0f && slot.callerSize > 0f ? slot.callerSize / slot.size : 1f;
                var uniform = (slot.size > 0f && slot.callerSize > 0f ? slot.callerSize / slot.size : 1f) * slot.scale;
                Vector3 scale;
                Quaternion rotation;
                if (slot.line)
                {
                    rotation = Quaternion.LookRotation(aim);
                    scale = !string.IsNullOrEmpty(slot.laserHead) ? Vector3.one * slot.scale : new Vector3(slot.scale, slot.scale, slot.length / Mathf.Max(0.01f, slot.authored));
                    if (!string.IsNullOrEmpty(slot.laserHead)) new LaserCopy(instance, slot).Stretch(slot.length);
                }
                else
                {
                    rotation = slot.axisUp ? Quaternion.FromToRotation(Vector3.up, aim) : Quaternion.LookRotation(aim);
                    scale = Vector3.one * uniform;
                    if (slot.body != null && slot.body.Length == 6)
                    {
                        var low = 0f;
                        for (var corner = 0; corner < 8; corner++)
                            low = Mathf.Min(low, (rotation * new Vector3(slot.body[(corner & 1) == 0 ? 0 : 3], slot.body[(corner & 2) == 0 ? 1 : 4], slot.body[(corner & 4) == 0 ? 2 : 5])).y * uniform);
                        origin.y = Mathf.Max(origin.y, -low);
                    }
                }
                instance.transform.SetPositionAndRotation(origin, rotation);
                instance.transform.localScale = scale;
                var hasColour = slot.colour != null && slot.colour.Length >= 3;
                if (hasColour || slot.line && slot.endFade > 0f)
                {
                    // The caller's colour as ForgeVisual's Fx gives it: _Color on every renderer of the copy (an @tint layer takes its hue); a line with an EndFade
                    // is drawn as one that hit nothing, faded over its last metres (_LineEnd, _LineFade).
                    var block = new MaterialPropertyBlock();
                    foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        renderer.GetPropertyBlock(block); // a laser body keeps its tiling
                        block.SetColor("_Color", hasColour ? new Color(slot.colour[0], slot.colour[1], slot.colour[2], 1f) : Color.white);
                        if (slot.line && slot.endFade > 0f) { block.SetVector("_LineEnd", origin + aim.normalized * slot.length); block.SetFloat("_LineFade", slot.endFade); }
                        renderer.SetPropertyBlock(block);
                    }
                }
                var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                var root = instance.GetComponent<ParticleSystem>();
                // One fixed seed so a sheet draws the same again; an area's copies each their own, as the game's auto seeds differ (one seed would draw every copy alike, a lattice).
                var seed = 20261105u;
                foreach (var s in systems) { s.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); s.useAutoRandomSeed = false; s.randomSeed = slot.spacing > 0f ? seed++ : seed; }
                var centre = slot.line ? origin + aim.normalized * (slot.length / 2f) : origin + Vector3.up * (slot.view * 0.3f);
                var view = slot.line ? slot.length * 1.15f : slot.view;
                camera.orthographic = true;
                camera.orthographicSize = view / 2f;
                var side = new Vector3(-0.35f, 0.32f, -0.88f).normalized;
                if (slot.line) side = Vector3.Cross(aim.normalized, Vector3.up).normalized * -1f + Vector3.up * 0.2f;
                camera.transform.position = centre + side * 80f;
                camera.transform.LookAt(centre);
                var cameraStart = camera.transform.position;
                bars.position = origin + camera.transform.right * (-view * 0.45f) + Vector3.up * 0.01f;
                bars.rotation = Quaternion.LookRotation(camera.transform.forward, Vector3.up) ;
                var cells = new Cells { slot = slot.slot, times = Times, particles = new int[Times.Length + 1], gone = -1f };
                var sheet = new Texture2D(Cell * (Times.Length + 2), Cell, TextureFormat.RGB24, false);
                var buffer = new ParticleSystem.Particle[256];
                root.Simulate(0f, true, true, false);
                root.Play(true);
                var stopped = false;
                var next = 0;
                var magenta = 0L;
                for (var t = 0f; t < 8f; t += Step)
                {
                    if (!stopped && t >= slot.seconds) { root.Stop(true, ParticleSystemStopBehavior.StopEmitting); stopped = true; }
                    Fly(root, instance.transform, camera.transform, origin, cameraStart, slot, aim, t);
                    var alive = 0;
                    // Reading the particles back waits for the simulation jobs; drawing before they finish crashes the 2019.4 editor in batch mode.
                    foreach (var s in systems)
                    {
                        var count = s.particleCount;
                        alive += count;
                        if (count == 0) continue;
                        if (buffer.Length < count) buffer = new ParticleSystem.Particle[count];
                        s.GetParticles(buffer);
                    }
                    if (alive == 0 && (stopped || t > 0.6f) && cells.gone < 0f) cells.gone = t;
                    if (next < Times.Length && t >= Times[next])
                    {
                        SheetLight(slot, instance.transform.position, reach, t);
                        magenta += Draw(camera, target, frame, sheet, next);
                        cells.particles[next] = alive;
                        next++;
                    }
                }
                // A line's last cell is a close-up of its start: the width that matters at the muzzle.
                if (slot.line)
                {
                    root.Simulate(0f, true, true, false);
                    root.Play(true);
                    for (var t = 0f; t < 0.15f; t += Step) root.Simulate(Step, true, false, false);
                    foreach (var s in systems) if (s.particleCount > 0) { if (buffer.Length < s.particleCount) buffer = new ParticleSystem.Particle[s.particleCount]; s.GetParticles(buffer); }
                    camera.orthographicSize = 2f;
                    camera.transform.position = origin + side * 80f;
                    camera.transform.LookAt(origin);
                    bars.position = origin + camera.transform.right * -1.8f + Vector3.up * -1.2f;
                }
                else
                {
                    // The last cell is the look at its fullest, closer: size against the bars at a third of the view.
                    root.Simulate(0f, true, true, false);
                    root.Play(true);
                    instance.transform.position = origin;
                    for (var t = 0f; t < 0.3f; t += Step) Fly(root, instance.transform, null, origin, Vector3.zero, slot, aim, t);
                    foreach (var s in systems) if (s.particleCount > 0) { if (buffer.Length < s.particleCount) buffer = new ParticleSystem.Particle[s.particleCount]; s.GetParticles(buffer); }
                    camera.orthographicSize = view / 6f;
                    camera.transform.position = instance.transform.position + side * 80f;
                    camera.transform.LookAt(instance.transform.position);
                    bars.position = origin + camera.transform.right * (-view * 0.15f) + Vector3.up * 0.01f;
                }
                SheetLight(slot, instance.transform.position, reach, slot.line ? 0.15f : 0.3f);
                magenta += Draw(camera, target, frame, sheet, Times.Length);
                if (!slot.line) FirstPerson(slot, root, systems, origin, aim, reach, camera, target, frame, sheet, bars, cells, ref buffer);
                SheetLight(null, Vector3.zero, 0f, 0f);
                sheet.Apply(false);
                File.WriteAllBytes(Path.Combine(output, slot.slot + ".cells.png"), sheet.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(sheet);
                cells.magenta = magenta / (float)(Cell * Cell * (Times.Length + 2));
                File.WriteAllText(Path.Combine(output, slot.slot + ".cells.json"), JsonUtility.ToJson(cells), new UTF8Encoding(false));
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        /// <summary>An area slot as ForgeVisual's Fx.Field lays it in the game: copies of the prefab on a hexagonal grid <c>spacing</c> metres apart over the caller's
        /// size, nearest the centre first, at most <c>copies</c> (the spacing widens until they fit), under a silent root the sheet plays and scales as one effect.
        /// The grid is ForgeVisual's FieldPoints.Disc, which this editor cannot load (the mod runs on the game's runtime): change both together.</summary>
        private static GameObject Field(GameObject prefab, PlanSlot slot)
        {
            var root = new GameObject(prefab.name + " field");
            var system = root.AddComponent<ParticleSystem>();
            var emission = system.emission;
            emission.enabled = false;
            root.GetComponent<ParticleSystemRenderer>().enabled = false;
            var radius = Mathf.Max(slot.callerSize, 0.1f) / 2f;
            var cap = Mathf.Max(1, slot.copies);
            var s = Mathf.Max(slot.spacing, Mathf.Sqrt(Mathf.PI * radius * radius / (0.866f * cap)));
            var rows = (int)(radius / (s * 0.866f)) + 1;
            var columns = (int)(radius / s) + 1;
            var found = new List<Vector2>();
            for (var r = -rows; r <= rows; r++)
                for (var c = -columns - 1; c <= columns + 1; c++)
                {
                    var p = new Vector2((c + (r & 1) * 0.5f) * s, r * s * 0.866f);
                    if (p.sqrMagnitude <= radius * radius) found.Add(p);
                }
            // The root is turned and scaled as one copy is (RenderSlot): each copy's place on the floor is given in the root's frame.
            var aim = new Vector3(slot.aim[0], slot.aim[1], slot.aim[2]);
            var turn = Quaternion.Inverse(slot.axisUp ? Quaternion.FromToRotation(Vector3.up, aim) : Quaternion.LookRotation(aim));
            foreach (var p in found.OrderBy(p => p.sqrMagnitude).Take(cap))
            {
                var copy = UnityEngine.Object.Instantiate(prefab, root.transform);
                copy.transform.localPosition = turn * new Vector3(p.x, 0f, p.y) / Mathf.Max(slot.scale, 0.0001f);
            }
            return root;
        }

        /// <summary>The last cell: the effect from where a player sees it (vertical field of view 70), at the moment it covers the most of the view; that share and the brightness of the change are written down.</summary>
        private static void FirstPerson(PlanSlot slot, ParticleSystem root, ParticleSystem[] systems, Vector3 origin, Vector3 aim, float reach, Camera camera, RenderTexture target, Texture2D frame, Texture2D sheet, Transform bars, Cells cells, ref ParticleSystem.Particle[] buffer)
        {
            bars.gameObject.SetActive(false);
            camera.orthographic = false;
            camera.fieldOfView = 70f;
            var flat = Vector3.ProjectOnPlane(aim, Vector3.up);
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.right;
            flat.Normalize();
            var lookUp = Vector3.up;
            if (slot.eye == "follow")
            {
                // A round flying away from the shooter: its tail points back, its direction does not say which way the eye looks; one falling or
                // rising is followed from behind along its own line (looking down at a falling pod), whatever way it goes.
                flat = Mathf.Abs(aim.normalized.y) > 0.7f ? aim.normalized : Vector3.right;
                if (Mathf.Abs(flat.y) > 0.7f) lookUp = Vector3.forward;
            }
            var right = Vector3.Cross(lookUp, flat).normalized;
            if (slot.eye == "gun" || slot.eye == "follow")
            {
                camera.transform.position = origin - flat * (slot.eye == "gun" ? 0.5f : slot.eyeDistance) + lookUp * 0.12f - right * 0.18f;
                camera.transform.rotation = Quaternion.LookRotation(flat, lookUp);
            }
            else
            {
                var away = new Vector3(-0.37f, 0f, -0.93f);
                camera.transform.position = origin + away * slot.eyeDistance + Vector3.up * slot.eyeHeight;
                camera.transform.rotation = Quaternion.LookRotation(-away + Vector3.up * (slot.eyeHeight < 0f ? 0.5f : -0.08f));
            }
            var empty = Grab(camera, target, frame, systems, root);
            var eye = camera.transform.position;
            root.transform.position = origin;
            root.Simulate(0f, true, true, false);
            root.Play(true);
            var stopped = false;
            var step = 0;
            Color[] fullest = null;
            for (var t = 0f; t < 2.4f; t += Step, step++)
            {
                if (!stopped && t >= slot.seconds) { root.Stop(true, ParticleSystemStopBehavior.StopEmitting); stopped = true; }
                Fly(root, root.transform, camera.transform, origin, eye, slot, aim, t);
                foreach (var s in systems)
                {
                    var count = s.particleCount;
                    if (count == 0) continue;
                    if (buffer.Length < count) buffer = new ParticleSystem.Particle[count];
                    s.GetParticles(buffer);
                }
                if (t > 0.5f && step % 2 != 0) continue;
                SheetLight(slot, root.transform.position, reach, t);
                camera.Render();
                RenderTexture.active = target;
                frame.ReadPixels(new Rect(0, 0, Cell, Cell), 0, 0);
                frame.Apply(false);
                RenderTexture.active = null;
                var now = frame.GetPixels32();
                var changed = 0;
                var peak = 0;
                for (var i = 0; i < now.Length; i++)
                {
                    var d = Mathf.Abs(now[i].r - empty[i].r) + Mathf.Abs(now[i].g - empty[i].g) + Mathf.Abs(now[i].b - empty[i].b);
                    if (d > 36) changed++;
                    if (d > peak) peak = d;
                }
                cells.peak = Mathf.Max(cells.peak, peak / 765f);
                if (fullest == null || changed / (float)now.Length > cells.cover)
                {
                    cells.cover = changed / (float)now.Length;
                    cells.coverAt = t;
                    fullest = frame.GetPixels();
                }
            }
            sheet.SetPixels((Times.Length + 1) * Cell, 0, Cell, Cell, fullest);
            bars.gameObject.SetActive(true);
        }

        /// <summary>How far a flying round (<c>move</c>) has travelled along its aim after <paramref name="t"/> seconds; zero for an effect that stands.</summary>
        private static Vector3 Travel(PlanSlot slot, Vector3 aim, float t) => slot.move > 0f ? aim.normalized * (slot.move * t) : Vector3.zero;

        /// <summary>One sheet step from <paramref name="t"/>: the copy (and the camera, if given) moves with a flying round (<c>move</c>) and the effect simulates. A flying
        /// round moves in eight sub-steps, about the game's frame rate, so a world-space tail is laid along its path as the game lays it, not in puffs a sheet step apart.</summary>
        private static void Fly(ParticleSystem root, Transform moved, Transform camera, Vector3 origin, Vector3 cameraStart, PlanSlot slot, Vector3 aim, float t)
        {
            // a slot played again and again (`repeat`) starts again each period over the whole sheet, its earlier particles left to live out as the game's earlier copies do
            if (slot.repeat > 0f && t > 0f && Mathf.Floor((t + Step) / slot.repeat) > Mathf.Floor(t / slot.repeat))
            {
                root.Stop(true, ParticleSystemStopBehavior.StopEmitting); // Play on a system still playing does nothing
                root.Play(true);
            }
            var parts = slot.move > 0f ? 8 : 1;
            for (var i = 1; i <= parts; i++)
            {
                var travel = Travel(slot, aim, t + Step * i / parts);
                moved.position = origin + travel;
                if (camera != null) camera.position = cameraStart + travel;
                root.Simulate(Step / parts, true, false, false);
            }
        }

        /// <summary>The view with no particle in it.</summary>
        private static Color32[] Grab(Camera camera, RenderTexture target, Texture2D frame, ParticleSystem[] systems, ParticleSystem root)
        {
            root.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            camera.Render();
            RenderTexture.active = target;
            frame.ReadPixels(new Rect(0, 0, Cell, Cell), 0, 0);
            frame.Apply(false);
            RenderTexture.active = null;
            return frame.GetPixels32();
        }

        /// <summary>What ForgeVisual's <c>FxPrefab</c> does to a laser copy on every <c>Fx.Line</c>: the head goes to the end, a stretched-billboard body takes the length through its renderer's length scale
        /// (live particles change at once), a flat one through its transform's own X. The copy itself stays at the slot's uniform scale.</summary>
        private sealed class LaserCopy
        {
            private readonly Transform _copy, _head;
            private readonly ParticleSystemRenderer[] _bodies;
            private readonly float[] _lengthScale;
            private readonly float _scale, _authored, _tile;
            private readonly MaterialPropertyBlock _tiling = new MaterialPropertyBlock();

            internal LaserCopy(GameObject copy, PlanSlot slot)
            {
                _scale = slot.scale;
                _authored = Mathf.Max(0.01f, slot.authored);
                _tile = slot.laserTile;
                _copy = copy.transform;
                // A line without a head and bodies is stretched as a whole, its +Z from the point to the end (Fx.Line without LaserBodies).
                if (string.IsNullOrEmpty(slot.laserHead)) { _bodies = new ParticleSystemRenderer[0]; _lengthScale = new float[0]; return; }
                _head = copy.transform.Find(slot.laserHead) ?? throw new InvalidOperationException(slot.slot + " has no head " + slot.laserHead);
                _bodies = new ParticleSystemRenderer[slot.laserBodies.Length];
                _lengthScale = new float[_bodies.Length];
                for (var i = 0; i < _bodies.Length; i++)
                {
                    var body = copy.transform.Find(slot.laserBodies[i]) ?? throw new InvalidOperationException(slot.slot + " has no body " + slot.laserBodies[i]);
                    _bodies[i] = body.GetComponent<ParticleSystemRenderer>();
                    _lengthScale[i] = _bodies[i].lengthScale;
                }
            }

            internal void Stretch(float length)
            {
                if (_head == null) { _copy.localScale = new Vector3(_scale, _scale, length / _authored); return; }
                var along = length / _scale;
                _head.localPosition = new Vector3(0f, 0f, along);
                for (var i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i].renderMode == ParticleSystemRenderMode.Stretch) _bodies[i].lengthScale = _lengthScale[i] * along / _authored;
                    else
                    {
                        var t = _bodies[i].transform;
                        var kept = t.localScale;
                        t.localScale = new Vector3(along / _authored, kept.y, kept.z);
                    }
                    if (_tile > 0f)
                    {
                        _bodies[i].GetPropertyBlock(_tiling);
                        _tiling.SetVector("_MainTex_ST", new Vector4(Mathf.Max(1f, Mathf.Round(length / _tile)), 1f, i * 0.37f, 0f));
                        _bodies[i].SetPropertyBlock(_tiling);
                    }
                }
            }
        }

        public static void Lasers() => ForgeVfxProject.RunAndExit("Forge VFX laser sheet", LasersAll);

        /// <summary>Every laser slot of the plan, one row (`&lt;slot&gt;.laser.png`), cells 320 px: side view at 5, 15 and 40 m; held at 15 m then set to 40 m and drawn on the very next frame; held at 40 m then
        /// set to 5 m likewise; 3 m wide views of the muzzle and of the head at 15 m; and first person at 15 m: 1 m beside the muzzle, then from the eye half a metre behind and just above it, and from there with the hit at 5 m.</summary>
        private static void LasersAll()
        {
            var output = Out;
            Directory.CreateDirectory(output);
            var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(ForgeVfxProject.Argument("-forgeVfxPlan") ?? throw new InvalidOperationException("-forgeVfxPlan is required")));
            AssetBundle.UnloadAllAssetBundles(true);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = Stage(out var bars);
            const int cell = 320;
            var target = new RenderTexture(cell, cell, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            camera.targetTexture = target;
            var frame = new Texture2D(cell, cell, TextureFormat.RGB24, false);
            var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            var aim = new Vector3(1f, 0f, 0.001f).normalized;
            var from = Vector3.up * 1.2f;
            var side = Vector3.Cross(aim, Vector3.up).normalized * -1f + Vector3.up * 0.2f;
            var buffer = new ParticleSystem.Particle[1024];
            try
            {
                foreach (var slot in plan.slots)
                {
                    if (!slot.line) continue;
                    if (!bundles.TryGetValue(slot.bundle, out var bundle)) bundles[slot.bundle] = bundle = AssetBundle.LoadFromFile(slot.bundle);
                    var instance = UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>(slot.item));
                    Normalize(instance);
                    if (slot.colour != null && slot.colour.Length >= 3)
                    {
                        var block = new MaterialPropertyBlock();
                        block.SetColor("_Color", new Color(slot.colour[0], slot.colour[1], slot.colour[2], 1f));
                        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) renderer.SetPropertyBlock(block);
                    }
                    instance.transform.SetPositionAndRotation(from, Quaternion.LookRotation(aim));
                    instance.transform.localScale = Vector3.one * slot.scale;
                    var laser = new LaserCopy(instance, slot);
                    var root = instance.GetComponent<ParticleSystem>();
                    var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                    void Run(float seconds)
                    {
                        // Reading the particles back waits for the simulation jobs; drawing before they finish crashes the 2019.4 editor in batch mode.
                        for (var t = 0f; t < seconds; t += Step) root.Simulate(Step, true, false, false);
                        foreach (var s in systems)
                        {
                            if (s.particleCount == 0) continue;
                            if (buffer.Length < s.particleCount) buffer = new ParticleSystem.Particle[s.particleCount];
                            s.GetParticles(buffer);
                        }
                    }
                    var settle = slot.seconds < 1f || !Array.Exists(systems, s => s.main.loop) ? 0.08f : 0.5f; // a one-shot (or a line of bursts, like an arc) is over by half a second
                    void Restart(float length)
                    {
                        laser.Stretch(length);
                        root.Simulate(0f, true, true, false);
                        root.Play(true);
                        Run(settle);
                    }
                    var sheet = new Texture2D(cell * 10, cell, TextureFormat.RGB24, false);
                    var renderers = instance.GetComponentsInChildren<Renderer>(true);
                    Color32[] Shot(int index)
                    {
                        camera.Render();
                        RenderTexture.active = target;
                        frame.ReadPixels(new Rect(0, 0, cell, cell), 0, 0);
                        frame.Apply(false);
                        RenderTexture.active = null;
                        sheet.SetPixels(index * cell, 0, cell, cell, frame.GetPixels());
                        return frame.GetPixels32();
                    }
                    // The share of the first-person view the line changes against the same view drawn with its renderers off.
                    float Cover(int index)
                    {
                        var seen = Shot(index);
                        foreach (var r in renderers) r.enabled = false;
                        camera.Render();
                        RenderTexture.active = target;
                        frame.ReadPixels(new Rect(0, 0, cell, cell), 0, 0);
                        frame.Apply(false);
                        RenderTexture.active = null;
                        foreach (var r in renderers) r.enabled = true;
                        var empty = frame.GetPixels32();
                        var changed = 0;
                        for (var i = 0; i < seen.Length; i++)
                            if (Mathf.Abs(seen[i].r - empty[i].r) + Mathf.Abs(seen[i].g - empty[i].g) + Mathf.Abs(seen[i].b - empty[i].b) > 36) changed++;
                        return changed / (float)seen.Length;
                    }
                    void Side(float view, Vector3 centre)
                    {
                        camera.orthographic = true;
                        camera.orthographicSize = view / 2f;
                        camera.transform.position = centre + side * 80f;
                        camera.transform.LookAt(centre);
                        bars.rotation = Quaternion.LookRotation(camera.transform.forward, Vector3.up);
                        bars.position = centre + camera.transform.right * (-view * 0.45f) + camera.transform.up * (-view * 0.3f);
                    }
                    var column = 0;
                    foreach (var length in new[] { 5f, 15f, 40f })
                    {
                        Restart(length);
                        Side(length * 1.15f, from + aim * (length / 2f));
                        Shot(column++);
                    }
                    Restart(15f);
                    laser.Stretch(40f);
                    Run(Step);
                    Side(46f, from + aim * 20f);
                    Shot(column++);
                    Restart(40f);
                    laser.Stretch(5f);
                    Run(Step);
                    Side(5.75f, from + aim * 2.5f);
                    Shot(column++);
                    Restart(15f);
                    Side(3f, from);
                    Shot(column++);
                    Side(3f, from + aim * 15f);
                    Shot(column++);
                    camera.orthographic = false;
                    camera.fieldOfView = 70f;
                    var right = Vector3.Cross(Vector3.up, aim).normalized;
                    camera.transform.position = from + right * 1f;
                    camera.transform.LookAt(from + aim * 0.5f);
                    bars.rotation = Quaternion.LookRotation(aim, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
                    bars.position = from + Vector3.down * 0.3f;
                    Shot(column++);
                    bars.gameObject.SetActive(false);
                    camera.transform.position = from - aim * 0.5f + Vector3.up * 0.15f - right * 0.2f;
                    camera.transform.LookAt(from + aim * 15f);
                    var cover15 = Cover(column++);
                    Restart(5f);
                    camera.transform.LookAt(from + aim * 5f);
                    var cover5 = Cover(column);
                    File.WriteAllText(Path.Combine(output, slot.slot + ".laser.json"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{{\"cover15\": {0:0.0000}, \"cover5\": {1:0.0000}}}", cover15, cover5), new UTF8Encoding(false));
                    bars.gameObject.SetActive(true);
                    sheet.Apply(false);
                    File.WriteAllBytes(Path.Combine(output, slot.slot + ".laser.png"), sheet.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(sheet);
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            finally
            {
                foreach (var b in bundles.Values) if (b != null) b.Unload(true);
                camera.targetTexture = null;
                target.Release();
            }
        }

        private static long Draw(Camera camera, RenderTexture target, Texture2D frame, Texture2D sheet, int index)
        {
            camera.Render();
            RenderTexture.active = target;
            frame.ReadPixels(new Rect(0, 0, Cell, Cell), 0, 0);
            frame.Apply(false);
            RenderTexture.active = null;
            var magenta = 0L;
            foreach (var pixel in frame.GetPixels32()) if (pixel.r > 200 && pixel.b > 200 && pixel.g < 60) magenta++;
            sheet.SetPixels(index * Cell, 0, Cell, Cell, frame.GetPixels());
            return magenta;
        }

        /// <summary>A dark floor, a camera and the scale: a white bar 1 m long, a yellow bar 2 m long and a 2 m pole, parented so they move together.</summary>
        private static Camera Stage(out Transform bars)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.52f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(40f, 1f, 40f);
            floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Hidden/ForgeVfx/Sheet Floor") ?? throw new InvalidOperationException("Hidden/ForgeVfx/Sheet Floor is not in the project"));
            SheetLight(null, Vector3.zero, 0f, 0f);
            var root = new GameObject("scale");
            Bar(root.transform, new Vector3(0.5f, 0.06f, 0f), new Vector3(1f, 0.05f, 0.05f), Color.white);
            Bar(root.transform, new Vector3(1f, 0.0f, 0f), new Vector3(2f, 0.05f, 0.05f), new Color(1f, 0.8f, 0.1f));
            Bar(root.transform, new Vector3(0f, 1f, 0f), new Vector3(0.04f, 2f, 0.04f), new Color(0.4f, 0.9f, 1f));
            bars = root.transform;
            var camera = new GameObject("sheet camera").AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 300f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.105f, 0.12f);
            camera.allowHDR = false;
            camera.allowMSAA = true;
            // a depth texture as the game's deferred player camera has, so soft particles (ForgeSoft.cginc) fade on the floor as they do in the game
            camera.depthTextureMode = DepthTextureMode.Depth;
            Shader.SetGlobalFloat("_ForgeSoftDepth", 1f);
            return camera;
        }

        /// <summary>The slot's light on the floor at <paramref name="t"/> seconds into a play, as ForgeVisual's FxLights gives it: a held slot's while it is held, a one-shot's flashing
        /// <c>delay</c> late and fading as (1 - age / life)^2; its range times <paramref name="reach"/> (the size it plays at). Null: no light.</summary>
        private static void SheetLight(PlanSlot slot, Vector3 at, float reach, float t)
        {
            var l = slot?.light;
            var on = 0f;
            if (l != null && l.Length >= 7)
            {
                if (slot.held) on = t < slot.seconds ? 1f : 0f;
                else
                {
                    var age = t - l[6];
                    var left = age < 0f || age >= l[5] ? 0f : 1f - age / l[5];
                    on = left * left;
                }
            }
            if (on <= 0f) { Shader.SetGlobalVector("_SheetLight", Vector4.zero); return; }
            Shader.SetGlobalVector("_SheetLight", new Vector4(at.x, at.y, at.z, l[3] * reach));
            Shader.SetGlobalColor("_SheetLightColour", new Color(l[0], l[1], l[2], 1f) * CallerHue(slot.colour) * (l[4] * on));
        }

        /// <summary>The caller's hue a slot's light takes, the algorithm of ForgeVisual's FxLights.Coloured (Mods/ForgeCore/Visual/Native/Fx.cs; that one is built
        /// against the game's IL2CPP interop, so this editor cannot call it): the colour over its luma, at least 0.05; white with no caller's colour.</summary>
        private static Color CallerHue(float[] colour)
        {
            if (colour == null || colour.Length < 3) return Color.white;
            var luma = Mathf.Max(colour[0] * 0.299f + colour[1] * 0.587f + colour[2] * 0.114f, 0.05f);
            return new Color(colour[0] / luma, colour[1] / luma, colour[2] / luma, 1f);
        }

        private static void Bar(Transform parent, Vector3 at, Vector3 size, Color colour)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = at;
            bar.transform.localScale = size;
            bar.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = colour };
        }
    }
}
