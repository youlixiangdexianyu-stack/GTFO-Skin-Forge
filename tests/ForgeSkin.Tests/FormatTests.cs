using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;
using Xunit;

namespace ForgeSkin.Tests;

public class FormatTests
{
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    static SkinDocument Doc() => new()
    {
        Id = "skin-test",
        Name = "Test",
        Target = new SkinTarget { Weapon = "auto_pistol", GearId = 45 },
        Materials =
        {
            new MaterialOverride
            {
                Material = "Front_UZI_2",
                Textures = { [TextureChannel.Albedo] = "textures/Front_UZI_2_Albedo.png" },
                Params = new MaterialParams { EmissiveColor = [1, 0.5f, 0], Tint = [1, 1, 1, 1] },
                Layers =
                {
                    new DynamicLayer
                    {
                        Id = "l1", Name = "Logo", Kind = LayerKind.Sequence,
                        Atlas = new LayerAtlas { Path = "layers/l1.png", FrameCount = 4, Columns = 2, FrameWidth = 64, FrameHeight = 32, TargetRect = [10, 20, 64, 32] },
                    },
                },
            },
        },
        Attachments =
        {
            new Attachment { Id = "m1", Name = "Model", Part = "receiver", Mesh = "meshes/m1.forgeskinmesh", Charm = new Charm { Style = CharmStyle.Beads } },
            new Attachment { Id = "s1", Name = "Ammo", Part = "receiver", Transform = "a_mag", Screen = new Screen { Digits = 3, GlyphAspect = 0.67f, GlyphMeshes = [.. Enumerable.Repeat("glyphs/s1.forgeskinmesh", 10), null, "glyphs/s1.forgeskinmesh"] } },
        },
        Parts = { new PartTransform { Part = "mag", Position = [0, 0.01f, 0] } },
        // A plain kill, a weighted kill-of-one-enemy group sharing the file, and a player-state event.
        Sounds =
        {
            new SkinSound { Id = "k1", Name = "Kill", Trigger = Trigger.Kill, File = "sounds/k1.ogg" },
            new SkinSound { Id = "k2", Name = "Tank", Trigger = Trigger.Kill, EnemyId = 29, Weight = 3, File = "sounds/k1.ogg" },
            new SkinSound { Id = "d1", Name = "Downed", Trigger = Trigger.Downed, File = "sounds/k1.ogg" },
        },
        PlayerColors =
        [
            new PlayerColor
            {
                Id = "body", Name = "Body", Default = [1, 1, 1], Palette = [[1, 0, 0]],
                Slots = { new ColorSlot { Kind = ColorSlotKind.Tint, Target = "Front_UZI_2" }, new ColorSlot { Kind = ColorSlotKind.ScreenText, Target = "s1" } },
            },
        ],
        // A code effect painted into a material's emission with player changes, and one on an attachment's model at an event.
        CodeEffects =
        [
            new CodeEffect
            {
                Id = "fx1", Name = "Flow", Material = "Front_UZI_2", Channel = TextureChannel.Emissive, Program = "flowLines",
                Params = { ["speed"] = 1.5f }, Colors = { ["line"] = [1, 0.4f, 0.1f, 1] }, Spin = 30, Blend = CodeEffectBlend.Additive,
                PlayerTweaks = new PlayerTweaks { Params = { "speed" }, Colors = { "line" }, Transform = { "scale" } },
            },
            new CodeEffect { Id = "fx2", Name = "Rim", Attachment = "m1", Program = "fresnel", Trigger = Trigger.Kill, Seconds = 2 },
        ],
    };

    static byte[] Mesh() => new ForgeSkinMesh { Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Indices = [0, 1, 2] }.Write();

    static Dictionary<string, byte[]> Files() => new()
    {
        ["textures/Front_UZI_2_Albedo.png"] = Png,
        ["layers/l1.png"] = Png,
        ["glyphs/s1.forgeskinmesh"] = Mesh(),
        ["meshes/m1.forgeskinmesh"] = Mesh(),
        ["sounds/k1.ogg"] = "OggS"u8.ToArray(),
    };

    static string Write(SkinDocument doc, Dictionary<string, byte[]> files, bool manifest = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}{SkinPack.Extension}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        if (manifest)
            using (var w = new StreamWriter(zip.CreateEntry(SkinPack.ManifestName).Open())) w.Write(SkinJson.Write(doc));
        return path;
    }

    [Fact]
    public void PackRoundTrip()
    {
        var doc = Doc();
        // A file name with two dots in it is legal; only a `..` segment is not.
        var files = Files();
        files["textures/a..b.png"] = files["textures/Front_UZI_2_Albedo.png"];
        doc.Materials[0].Textures[TextureChannel.Albedo] = "textures/a..b.png";
        var path = Write(doc, files);
        using var pack = SkinPack.Open(path);
        Assert.Equal(SkinJson.Write(doc), SkinJson.Write(pack.Manifest));
        Assert.Equal(Mesh(), pack.ReadBytes("meshes/m1.forgeskinmesh"));
        Assert.Equal(3, ForgeSkinMesh.Read(pack.ReadBytes("meshes/m1.forgeskinmesh")).Positions.Length / 3);
    }

    // A deliberately broken pack per error-code family (document, path, resource, mesh content, screen, audio, sound event, player
    // colour, code effect) must be caught.
    public static TheoryData<string, Action<SkinDocument, Dictionary<string, byte[]>>> Broken => new()
    {
        { "schema", (d, _) => d.Schema = 99 },
        { "bad-path", (d, _) => d.Materials[0].Textures[TextureChannel.Albedo] = "../evil.png" },
        { "bad-path", (d, _) => d.Materials[0].Textures[TextureChannel.Albedo] = "textures\\..\\evil.png" }, // a backslash is never a separator here
        { "missing-file", (_, f) => f.Remove("textures/Front_UZI_2_Albedo.png") },
        // A valid header whose counts claim far more than the data holds: caught before anything is allocated.
        { "bad-mesh", (_, f) => f["meshes/m1.forgeskinmesh"] = [.. Mesh().Take(4), .. BitConverter.GetBytes(int.MaxValue / 3), .. BitConverter.GetBytes(3), 0, 0, 0, 0] },
        { "bad-image", (_, f) => f["layers/l1.png"] = [.. "DDS "u8, 124, 0, 0, 0] }, // a layer atlas must stay a picture, never a Dds
        { "bad-screen", (d, _) => d.Attachments[1].Screen!.Digits = 9 },
        // A frame may be stored smaller than its region (stretched back in game), never larger.
        { "bad-atlas", (d, _) => d.Materials[0].Layers[0].Atlas!.FrameWidth = 65 },
        { "bad-audio", (_, f) => f["sounds/k1.ogg"] = "RIFF0000WAVE"u8.ToArray() },
        // An Ogg whose first stream is Opus: the manifest check passes it, the game's audio kit refuses it, so the build does too.
        { "bad-audio", (_, f) => f["sounds/k1.ogg"] = [.. "OggS"u8, 0, 2, .. new byte[20], 1, 30, .. "OpusHead"u8, .. new byte[22]] },
        { "bad-trigger", (d, _) => d.Sounds[2].EnemyId = 29 }, // an enemy on a sound that is not a kill
        { "bad-player-color", (d, _) => d.PlayerColors![0].Slots[1].Target = "gone" },
        // Code effects, checked against ForgeCore's program catalogue: a program it lacks, a value outside the catalogue's range, an
        // attachment without a model to draw on.
        { "bad-fx-program", (d, _) => d.CodeEffects![0].Program = "lava" },
        { "bad-fx-value", (d, _) => d.CodeEffects![0].Params["speed"] = 99 },
        { "bad-fx-target", (d, _) => d.CodeEffects![1].Attachment = "s1" },
    };
    [Theory]
    [MemberData(nameof(Broken))]
    public void BrokenPackIsRejected(string code, Action<SkinDocument, Dictionary<string, byte[]>> breakIt)
    {
        var (doc, files) = (Doc(), Files());
        breakIt(doc, files);
        var path = Write(doc, files);
        var e = Assert.Throws<SkinFormatException>(() =>
        {
            using var pack = SkinPack.Open(path);
            foreach (var a in pack.Manifest.Attachments.Where(a => a.Mesh != null)) ForgeSkinMesh.Read(pack.ReadBytes(a.Mesh!));
            ForgeSkin.Backend.PackBuilder.CheckSounds(pack); // what pack.build and pack.install add
        });
        Assert.Equal(code, e.Code);
    }

    // Core CharmRope (the game) and the editor's charm.ts (the 3D view) are one algorithm written twice: the same input
    // (each style and an extreme short, stiff, light rope; body boxes one of which holds the hanging point; a moving and
    // turning hanging point and a player's body on part of its path at uneven frame times with a stall that puts the rope at
    // rest, then held still) must give the same nodes every frame, pendant pose, mesh and triangles. The rope stays stable:
    // no node moves faster than the speed cap allows relative to the hanging point, and a damped rope held still for two
    // seconds hangs straight again. Carried at a steady speed (walking), the rope hangs straight: drag is the rope's own.
    [Fact]
    public async Task CharmMatchesEditor()
    {
        var charms = new[]
        {
            new Charm { Style = CharmStyle.Cord, Segments = 5, Length = 0.08f, Stiffness = 0.6f, Damping = 0.1f, Thickness = 0.003f },
            new Charm(),
            new Charm { Style = CharmStyle.Beads, Segments = 12, Weight = 2.5f, Damping = 0.9f, Stiffness = 0, Collide = false },
            new Charm { Style = CharmStyle.Cord, Segments = 12, Length = 0.005f, Stiffness = 1, Weight = 0.1f, Collide = false },
        };
        const double arm = 0.012;
        double[] gear = [0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0.3, 1.1, -0.2, 1]; // turned a quarter about Y
        var boxes = new[] { (Min: new[] { -0.15, -0.02, -0.02 }, Max: new[] { 0.15, 0.03, 0.02 }), (Min: new[] { 0.02, -0.12, -0.015 }, Max: new[] { 0.06, -0.03, 0.015 }) };
        var frames = new List<(double Time, double[] M, double[] Body)>();
        double time = 0;
        double[] dts = [1 / 60.0, 1 / 144.0, 1 / 30.0, 0.011];
        for (int i = 0; i < 240; i++)
        {
            time += i == 120 ? 0.3 : dts[i % dts.Length];
            double yaw = Math.Sin(time * 3.1) * 1.4, pitch = Math.Sin(time * 1.7) * 0.5, s = 1.2;
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            // Rotation Y(yaw) * X(pitch), column-major, scaled; the gear's own move as a Lissajous path.
            frames.Add((time, [cy * s, 0, -sy * s, 0, sy * sp * s, cp * s, cy * sp * s, 0, sy * cp * s, -sp * s, cy * cp * s, 0,
                0.3 + Math.Sin(time * 2.3) * 0.25, 1.1 + Math.Sin(time * 4.1) * 0.08, -0.2 + Math.Cos(time * 1.9) * 0.3, 1],
                [0.3 + Math.Sin(time * 2.3) * 0.25, 1.6, -0.5 + Math.Cos(time * 1.9) * 0.2]));
        }
        int moving = frames.Count;
        // Then held still and upright (its -Y straight down), 2.1 s.
        var m = frames[moving - 1].M;
        double[] still = [1.2, 0, 0, 0, 0, 1.2, 0, 0, 0, 0, 1.2, 0, m[12], m[13], m[14], 1];
        for (int i = 0; i < 120; i++) frames.Add((time += dts[i % dts.Length] * 1.2, still, frames[moving - 1].Body));
        var anchor0 = frames[0].M;

        var mine = charms.Select(c =>
        {
            var rope = new CharmRope(c, arm);
            if (c.Collide) foreach (var (min, max) in boxes) rope.AddBox(anchor0, gear, min, max);
            var nodes = frames.Select(f => { rope.Update(f.Time, f.M, f.Body); return rope.Nodes.ToArray(); }).ToList();
            var pose = new double[7];
            rope.Pendant(pose);
            float[] v = new float[rope.VertexCount * 3], n = new float[rope.VertexCount * 3];
            rope.Mesh(frames[^1].M, v, n);
            return (Nodes: nodes, Pose: pose, Vertices: v, Normals: n, Triangles: rope.Triangles(), rope.Boxes);
        }).ToList();

        var input = JsonSerializer.Serialize(new
        {
            charms = charms.Select(c => new
            {
                length = (double)c.Length,
                segments = c.Segments,
                weight = (double)c.Weight,
                damping = (double)c.Damping,
                stiffness = (double)c.Stiffness,
                style = c.Style.ToString().ToLowerInvariant(),
                thickness = (double)c.Thickness,
                collide = c.Collide,
            }),
            arm,
            gear,
            anchor0,
            boxes = boxes.Select(b => new { min = b.Min, max = b.Max }),
            frames = frames.Select(f => new { time = f.Time, m = f.M, body = f.Body }),
        });
        var script = Path.Combine(Repo(), "Mods/ForgeSkin/Editor/src/renderer/viewport/charm.ts");
        const string driver = """
            import { readFileSync } from 'node:fs';
            const { CharmRope } = await import(process.argv[1]);
            const input = JSON.parse(readFileSync(0, 'utf8'));
            const out = input.charms.map((c) => {
              const rope = new CharmRope(c, input.arm);
              if (c.collide) for (const b of input.boxes) rope.addBox(input.anchor0, input.gear, b.min, b.max);
              const nodes = input.frames.map((f) => { rope.update(f.time, f.m, f.body); return Array.from(rope.nodes); });
              const pose = new Array(7);
              rope.pendant(pose);
              const v = new Float32Array(rope.vertexCount * 3), n = new Float32Array(rope.vertexCount * 3);
              rope.mesh(input.frames[input.frames.length - 1].m, v, n);
              return { nodes, pose, vertices: Array.from(v), normals: Array.from(n), triangles: rope.triangles(), boxes: rope.boxCount };
            });
            process.stdout.write(JSON.stringify(out));
            """;
        var editor = (await Node(script, driver, input)).AsArray();

        for (int c = 0; c < charms.Length; c++)
        {
            var e = editor[c]!;
            Assert.Equal(mine[c].Boxes, (int)e["boxes"]!);
            Assert.Equal(mine[c].Triangles, e["triangles"]!.AsArray().Select(x => (int)x!).ToArray());
            for (int f = 0; f < frames.Count; f++)
                Near(mine[c].Nodes[f], e["nodes"]![f]!, 1e-6, $"charm {c} frame {f} nodes");
            Near(mine[c].Pose, e["pose"]!, 1e-6, $"charm {c} pendant");
            Near(mine[c].Vertices.Select(x => (double)x).ToArray(), e["vertices"]!, 1e-5, $"charm {c} vertices");
            Near(mine[c].Normals.Select(x => (double)x).ToArray(), e["normals"]!, 1e-4, $"charm {c} normals");
        }
        // The rope moved (not a pair of agreeing rests): while moving, the pendant swings off the straight hang.
        Assert.True(mine[1].Nodes.Take(moving).Max(x => Math.Abs(x[^3] - x[0])) > 1e-2);
        for (int c = 0; c < charms.Length; c++)
        {
            var nodes = mine[c].Nodes;
            for (int f = 1; f < frames.Count; f++)
            {
                double dt = frames[f].Time - frames[f - 1].Time;
                for (int i = 3; i < nodes[f].Length; i += 3)
                {
                    // Each node's move relative to the hanging point's, per second (a rest frame starts over).
                    double s = Math.Sqrt(Enumerable.Range(0, 3).Sum(j => Math.Pow(nodes[f][i + j] - nodes[f][j] - nodes[f - 1][i + j] + nodes[f - 1][j], 2))) / dt;
                    Assert.True(dt > CharmRope.Gap || s <= 3 * CharmRope.MaxSpeed, $"charm {c} frame {f} node {i / 3}: {s:F2} m/s");
                }
            }
            if (charms[c].Damping < 0.3 || charms[c].Collide) continue;
            // Held still with damping and nothing in the way: back to hanging straight down, within a millimetre.
            var last = nodes[^1];
            double drop = charms[c].Length + arm;
            Assert.True(Math.Abs(last[^3] - last[0]) < 1e-3 && Math.Abs(last[^1] - last[2]) < 1e-3 && Math.Abs(last[0 + 1] - last[^2] - drop) < 1e-3,
                $"charm {c} does not settle: pendant at {last[^3] - last[0]:F4}, {last[^2] - last[1]:F4}, {last[^1] - last[2]:F4}");
        }
        // Walking (3.5 m/s reached over a second, then two seconds at it), gun and body together: no drag from the air, so the
        // default rope hangs straight again.
        var walker = new CharmRope(new Charm { Collide = false }, arm);
        for (int i = 0; i <= 180; i++)
        {
            double t = i / 60.0, z = t < 1 ? 3.5 * (t * t * t - t * t * t * t / 2) : 3.5 * (t - 0.5);
            walker.Update(t, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1.1, z, 1], [0, 1.6, z - 0.4]);
        }
        var w = walker.Nodes;
        Assert.True(Math.Abs(w[^1] - w[2]) < 2e-3 && Math.Abs(w[^3] - w[0]) < 2e-3, $"walking drags the charm: pendant {w[^1] - w[2]:F4} m behind");
    }

    // Core CodeEffects.Resolve (the game) and the editor's codeFxValues.ts (the 3D view) turn a code effect into the same
    // program index, numbers, colours and transform: defaults, out-of-range values, a partial colour, player changes where
    // allowed and ignored where not. The editor compiles the game's own program source and catalogue, and its dispatch names
    // a program function of that source for each catalogue entry in catalogue order.
    [Fact]
    public async Task CodeEffectsMatchEditor()
    {
        var effects = new[]
        {
            Doc().CodeEffects![0],
            Doc().CodeEffects![1],
            new CodeEffect
            {
                Id = "fx3", Material = "m", Program = "polar", Params = { ["petals"] = 999, ["drift"] = -50 }, Colors = { ["ring"] = [2, 0.5f, -1, 0.3f] },
                Scale = 40, Rotation = 90, Spin = -5, Intensity = 0.4f, PlayerTweaks = new PlayerTweaks { Colors = { "ring" }, Transform = { "rotation", "intensity" } },
            },
        };
        var tweaks = new Dictionary<string, float[]>
        {
            [CodeEffects.TweakKey("fx1", TweakKind.Param, "speed")] = [0.25f],
            [CodeEffects.TweakKey("fx1", TweakKind.Color, "line")] = [0, 1, 0.5f, 1],
            [CodeEffects.TweakKey("fx1", TweakKind.Transform, "rotation")] = [45], // not allowed: rotation stays the document's
            [CodeEffects.TweakKey("fx3", TweakKind.Color, "ring")] = [0.1f, 0.2f, 0.3f], // wrong length: ignored
            [CodeEffects.TweakKey("fx3", TweakKind.Transform, "intensity")] = [9], // allowed, kept in range
        };
        var catalog = CodeEffects.Catalog;
        var mine = effects.Select(e =>
        {
            var v = new CodeEffectValues();
            CodeEffects.Resolve(e, tweaks, v);
            return new { program = v.Program, @params = v.Params, colors = v.Colors, transform = v.Transform };
        }).ToList();
        Assert.Equal(0.25f, mine[0].@params[catalog.Find("flowLines")!.Param("speed")!.Index]);

        var root = Repo();
        var visual = Path.Combine(root, "Mods/ForgeCore/Visual");
        const string driver = """
            import { readFileSync } from 'node:fs';
            const { resolveFx, fxDispatch } = await import(process.argv[1]);
            const input = JSON.parse(readFileSync(0, 'utf8'));
            const catalog = JSON.parse(readFileSync(input.catalog, 'utf8'));
            process.stdout.write(JSON.stringify({ values: input.effects.map((e) => resolveFx(catalog, e, input.tweaks)), dispatch: fxDispatch(catalog) }));
            """;
        var input = JsonSerializer.Serialize(new { effects, tweaks, catalog = Path.Combine(visual, "Data/fx-programs.json") }, SkinJson.Options);
        var editor = await Node(Path.Combine(root, "Mods/ForgeSkin/Editor/src/renderer/codeFxValues.ts"), driver, input);
        for (int i = 0; i < effects.Length; i++)
        {
            var e = editor["values"]![i]!;
            Assert.Equal(mine[i].program!.Index, (int)e["program"]!);
            Near(mine[i].@params.Select(x => (double)x).ToArray(), e["params"]!, 1e-6, $"fx {i} params");
            for (int c = 0; c < 4; c++) Near(mine[i].colors[c].Select(x => (double)x).ToArray(), e["colors"]![c]!, 1e-6, $"fx {i} colour {c}");
            Near(mine[i].transform.Select(x => (double)x).ToArray(), e["transform"]!, 1e-6, $"fx {i} transform");
        }

        var source = File.ReadAllText(Path.Combine(visual, "tools/shaders/ForgeFxPrograms.hlsl"));
        var dispatch = (string)editor["dispatch"]!;
        var program = 0;
        foreach (var p in catalog.Programs)
        {
            Assert.Contains($"program == {program++}) return fx_{p.Name}(i);", dispatch);
            Assert.Matches($@"\bfx_{p.Name}\s*\(\s*FxIn\b", source);
        }
        var preview = File.ReadAllText(Path.Combine(root, "Mods/ForgeSkin/Editor/src/renderer/viewport/CodeEffects.tsx"));
        Assert.Contains("ForgeCore/Visual/Data/fx-programs.json'", preview);
        Assert.Contains("ForgeCore/Visual/tools/shaders/ForgeFxPrograms.hlsl?raw'", preview);
    }

    /// <summary>Runs `driver` (an ES module) under node with `script`'s file URL as its argument and `input` on stdin; its stdout as JSON.</summary>
    static async Task<JsonNode> Node(string script, string driver, string input)
    {
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--input-type=module", "-e", driver, new Uri(script).AbsoluteUri }) start.ArgumentList.Add(arg);
        using var node = System.Diagnostics.Process.Start(start)!;
        var error = node.StandardError.ReadToEndAsync();
        var output = node.StandardOutput.ReadToEndAsync();
        node.StandardInput.Write(input);
        node.StandardInput.Close();
        await node.WaitForExitAsync();
        Assert.True(node.ExitCode == 0, "node: " + await error);
        return JsonNode.Parse(await output)!;
    }

    static void Near(double[] mine, JsonNode editor, double tolerance, string what)
    {
        var e = editor.AsArray();
        Assert.Equal(mine.Length, e.Count);
        for (int i = 0; i < mine.Length; i++)
            Assert.True(Math.Abs(mine[i] - (double)e[i]!) <= tolerance, $"{what}[{i}]: game {mine[i]}, editor {(double)e[i]!}");
    }

    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Mods/ForgeSkin/Core/CharmRope.cs"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root above " + AppContext.BaseDirectory);
    }

    // A texture the editor compressed for the pack (Core Dds), one per kind: named by the manifest in place of a PNG, read
    // back out of the pack and accepted for its channel, BC1 for opaque colour and BC3 for alpha and normal maps, the full
    // mip chain, and its top level decodes back to the picture (a normal map's x and y; the backend's readers of packs).
    [Fact]
    public void PrecompressedTextureRoundTrip()
    {
        const int w = 64, h = 32;
        byte[] Picture(bool alpha)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)(x * 4), (byte)(y * 8), (byte)(128 + (x ^ y) % 8), (byte)(alpha ? x * 4 : 255));
                }
            return px;
        }
        foreach (var (channel, alpha, format) in new[] { (TextureChannel.Albedo, false, "bc1"), (TextureChannel.Emissive, true, "bc3"), (TextureChannel.Normal, false, "bc3") })
        {
            var source = Picture(alpha);
            using var bmp = ForgeSkin.Backend.Images.FromRgba(w, h, source);
            var png = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
            var name = $"textures/t_{channel}.dds";
            var (doc, files) = (Doc(), Files());
            doc.Materials[0].Textures[channel] = name;
            files[name] = ForgeSkin.Backend.Precompress.Encode(png, channel, name)!;
            using var pack = SkinPack.Open(Write(doc, files));
            var bytes = pack.ReadBytes(pack.Manifest.Materials[0].Textures[channel]);
            var dds = Dds.Read(bytes, channel, name);
            Assert.Equal((w, h, Dds.Mips(w, h), format == "bc3", !Dds.Linear(channel)), (dds.Width, dds.Height, dds.MipCount, dds.Alpha, dds.Srgb));

            using var back = ForgeSkin.Backend.Precompress.Pixels(bytes, channel, name);
            var top = back.Bytes;
            double error = 0;
            int compared = channel == TextureChannel.Normal ? 2 : 4; // a normal map keeps x and y
            for (int i = 0; i < source.Length; i += 4)
                for (int k = 0; k < compared; k++) error += Math.Pow(top[i + k] - source[i + k], 2);
            var psnr = 10 * Math.Log10(255.0 * 255 / (error / (w * h * compared)));
            Assert.True(psnr > 30, $"{channel}: PSNR {psnr:F1} dB");
        }
    }

    // A precompressed texture the plugin could not upload as it is must be refused before the game sees it.
    public static TheoryData<string, Func<byte[], byte[]>> BrokenDds => new()
    {
        { "truncated", d => d[..^16] },
        { "mip count", d => { var c = (byte[])d.Clone(); c[28]--; return c; } },
        { "sRGB data for a linear channel", d => { var c = (byte[])d.Clone(); c[128] = 72; return c; } },
    };
    [Theory]
    [MemberData(nameof(BrokenDds))]
    public void BrokenDdsIsRejected(string what, Func<byte[], byte[]> breakIt)
    {
        var levels = Enumerable.Range(0, Dds.Mips(8, 8)).Select(i => new byte[Math.Max(1, (8 >> i) / 4) * Math.Max(1, (8 >> i) / 4) * 8]).ToArray();
        var good = Dds.Write(8, 8, false, false, levels);
        Dds.Read(good, TextureChannel.MetallicSmoothness, "good");
        Assert.Equal("bad-image", Assert.Throws<SkinFormatException>(() => Dds.Read(breakIt(good), TextureChannel.MetallicSmoothness, what)).Code);
    }

    [Fact]
    public void PackWithoutManifestIsRejected() =>
        Assert.Equal("no-manifest", Assert.Throws<SkinFormatException>(() => SkinPack.Open(Write(Doc(), Files(), manifest: false))).Code);
}
