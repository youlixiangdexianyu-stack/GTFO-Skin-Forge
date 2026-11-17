using System.IO.Compression;
using System.Numerics;
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
        Picture = "picture.png",
        Materials =
        {
            new MaterialOverride
            {
                Material = "Front_UZI_2",
                Textures = { [TextureChannel.Albedo] = "textures/Front_UZI_2_Albedo.png" },
                Params = new MaterialParams
                {
                    EmissiveColor = [1, 0.5f, 0], Tint = [1, 1, 1, 1],
                    Reticles = new() { [TextureChannel.ReticleB] = new Reticle { Color = [0, 1, 0.5f], Intensity = 4, Scale = 2.2f, Offset = 0.4f, Visible = true } },
                },
                Layers =
                {
                    new DynamicLayer
                    {
                        Id = "l1", Name = "Logo", Kind = LayerKind.Sequence,
                        Atlas = new LayerAtlas { Path = "layers/l1.png", FrameCount = 4, Columns = 2, FrameWidth = 64, FrameHeight = 32, TargetRect = [10, 20, 64, 32] },
                    },
                },
            },
            // An attachment's model as a layer target: layers only, its pictures stay the attachment material's.
            new MaterialOverride
            {
                Material = MaterialOverride.ForAttachment("m1"),
                Layers = { new DynamicLayer { Id = "l2", Name = "Sticker", Kind = LayerKind.Sequence, Atlas = new LayerAtlas { Path = "layers/l1.png", FrameCount = 2, Columns = 2, FrameWidth = 16, FrameHeight = 16, TargetRect = [0, 0, 16, 16] } } },
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
                Id = "body", Name = "Body", Default = [1, 1, 1],
                Slots = { new ColorSlot { Kind = ColorSlotKind.Tint, Target = "Front_UZI_2" }, new ColorSlot { Kind = ColorSlotKind.ScreenText, Target = "s1" } },
            },
        ],
        ColorSchemes = [new ColorScheme { Id = "red", Name = "Red", Colors = { ["body"] = [1, 0, 0] } }],
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
        ["picture.png"] = Png,
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

        // The pack as a teammate's look (Core Look): the player's colour and code-effect changes travel and come back as the same picks
        // (to the byte the wire keeps); a value over the size limit is dropped whole, and a pack whose content differs shows the original.
        var picks = new Dictionary<string, float[]>
        {
            ["body"] = [1, 0, 0],
            [CodeEffects.TweakKey("fx1", TweakKind.Param, "speed")] = [2.5f],
            [CodeEffects.TweakKey("fx1", TweakKind.Color, "line")] = [0, 1, 0, 1],
            [CodeEffects.TweakKey("fx1", TweakKind.Transform, "scale")] = [1], // the author's own value: not sent
        };
        var gear = new LookGear { GearId = 45, Skin = Look.SkinNumber(doc.Id), Hash = 0x1234_5678_9abc_def0, Pack = "author-skins", Wear = 1 };
        Look.Tweaks(doc, picks, gear.Tweaks);
        Assert.Equal(3, gear.Tweaks.Count);
        var look = new Look { Character = null };
        look.Gear.Add(gear);
        var bytes = look.Encode();
        Assert.Equal(2 + 4 + 1 + 4 + 4 + 8 + 1 + gear.Pack.Length + 1 + 2 + (5 + 3) + (5 + 2) + (5 + 4), bytes.Length);
        var back = Look.Decode(bytes)!;
        Assert.Equal(bytes, back.Encode());
        Assert.Equal(LookFit.Fits, Look.Fit(back.Gear[0], doc, gear.Hash, out var shown, out _));
        Assert.Equal(new[] { 1f, 0, 0 }, shown!["body"]);
        Assert.Equal(2.5f, shown[CodeEffects.TweakKey("fx1", TweakKind.Param, "speed")][0], 3);
        Assert.Null(Look.Decode([.. bytes, .. new byte[Look.MaxBytes]]));
        Assert.Null(Look.Decode(bytes[..^1]));
        Assert.Equal(LookFit.Mismatch, Look.Fit(back.Gear[0], doc, gear.Hash + 1, out _, out _));
        back.Gear[0].Tweaks.Add(LookTweak.Of(0, LookChange.PlayerColor, 5, 0)); // a player colour the skin does not have
        Assert.Equal(LookFit.BadValue, Look.Fit(back.Gear[0], doc, gear.Hash, out _, out _));
    }

    // A deliberately broken pack per error-code family (document, path, resource, mesh content, screen, audio, sound event, player
    // colour, code effect, reticle) must be caught.
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
        { "bad-color-scheme", (d, _) => d.ColorSchemes![0].Colors["gone"] = [0, 0, 1] },
        // Code effects, checked against ForgeCore's program catalogue: a program it lacks, a value outside the catalogue's range, an
        // attachment without a model to draw on.
        { "bad-fx-program", (d, _) => d.CodeEffects![0].Program = "lava" },
        { "bad-fx-value", (d, _) => d.CodeEffects![0].Params["speed"] = 99 },
        { "bad-fx-target", (d, _) => d.CodeEffects![1].Attachment = "s1" },
        // A layer target on an attachment without a model (a screen).
        { "bad-material-target", (d, _) => d.Materials[1].Material = MaterialOverride.ForAttachment("s1") },
        // Reticle values under a channel that is no reticle layer.
        { "bad-reticle", (d, _) => d.Materials[0].Params.Reticles![TextureChannel.Albedo] = new Reticle { Scale = 2 } },
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

    // A standing humanoid skeleton (game axes: +X is the body's right), canonical joint -> (bone, position).
    static readonly (string Joint, string Bone, float X, float Y)[] Skeleton =
    {
        ("hip", "Hips", 0, 1), ("spine0", "Spine", 0, 1.1f), ("spine1", "Chest", 0, 1.25f), ("neck", "Neck", 0, 1.5f), ("head", "Head", 0, 1.6f),
        ("leftShoulder", "L_Shoulder", -0.05f, 1.45f), ("leftUpperArm", "L_Arm", -0.2f, 1.45f), ("leftLowerArm", "L_Forearm", -0.45f, 1.45f), ("leftHand", "L_Hand", -0.7f, 1.45f),
        ("rightShoulder", "R_Shoulder", 0.05f, 1.45f), ("rightUpperArm", "R_Arm", 0.2f, 1.45f), ("rightLowerArm", "R_Forearm", 0.45f, 1.45f), ("rightHand", "R_Hand", 0.7f, 1.45f),
        ("leftUpperLeg", "L_Thigh", -0.1f, 1), ("leftLowerLeg", "L_Shin", -0.1f, 0.55f), ("leftFoot", "L_Foot", -0.1f, 0.1f),
        ("rightUpperLeg", "R_Thigh", 0.1f, 1), ("rightLowerLeg", "R_Shin", 0.1f, 0.55f), ("rightFoot", "R_Foot", 0.1f, 0.1f),
    };

    static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    // A backend method as the editor calls it.
    static System.Text.Json.Nodes.JsonNode Backend(string method, object args) =>
        JsonSerializer.SerializeToNode(ForgeSkin.Backend.Methods.Call(method, JsonSerializer.SerializeToNode(args, ForgeSkin.Backend.Program.Json)!.AsObject(), 1), ForgeSkin.Backend.Program.Json)!;

    // A character pack: the descriptor (ImportedModels format 2) with a hair strand under the head, its mesh and texture.
    // A PNG header of 4x4 (what ForgeCore's file check reads; decoding is the game's).
    static readonly byte[] Png4 = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 4, 0, 0, 0, 4, 8, 6, 0, 0, 0];

    // A format-2 mesh of one triangle (normals up, every vertex on bone 0) whose indices are `indices`.
    static byte[] CharacterMesh(params uint[] indices)
    {
        var mesh = new byte[16 + 3 * 32 + 3 * 4 + 3 * 24];
        BitConverter.GetBytes(0x48534D46u).CopyTo(mesh, 0); // FMSH
        BitConverter.GetBytes(2u).CopyTo(mesh, 4);
        BitConverter.GetBytes(3u).CopyTo(mesh, 8);
        BitConverter.GetBytes(3u).CopyTo(mesh, 12);
        for (int v = 0; v < 3; v++) BitConverter.GetBytes(1f).CopyTo(mesh, 16 + v * 32 + 16);
        for (int i = 0; i < 3; i++) BitConverter.GetBytes(indices[i]).CopyTo(mesh, 16 + 3 * 32 + i * 4);
        for (int v = 0; v < 3; v++) BitConverter.GetBytes(1f).CopyTo(mesh, 16 + 3 * 32 + 3 * 4 + v * 24 + 8);
        return mesh;
    }

    static (SkinDocument, Dictionary<string, byte[]>) Character(byte[]? mesh = null)
    {
        mesh ??= CharacterMesh(0, 1, 2);
        var bones = Skeleton.Select(s => (s.Bone, s.X, s.Y)).Concat(new[] { ("Hair1", 0f, 1.65f), ("Hair2", 0f, 1.5f) })
            .Select((b, i) => new { name = b.Item1, parent = i == 0 ? -1 : 0, rest = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, b.Item2, b.Item3, 0, 1 } });
        var descriptor = JsonSerializer.Serialize(new
        {
            format = 2,
            mesh = new { sha256 = Sha(mesh), vertices = 3, indices = 3 },
            submeshes = new[] { new { start = 0, count = 3, material = 0 } },
            materials = new[] { new { name = "Body", baseColor = new[] { 1f, 1, 1, 1 }, texture = 0 } },
            textures = new[] { new { sha256 = Sha(Png4), width = 4, height = 4 } },
            skin = new { bones },
        });
        var doc = new SkinDocument
        {
            Id = "person-test",
            Target = new SkinTarget { Weapon = CharacterSkin.Target },
            Character = new CharacterSkin
            {
                Model = new CharacterModelFiles { Descriptor = "character/model.json", Mesh = "character/model.forgemesh", Textures = { "character/t0.png" } },
                Bones = Skeleton.ToDictionary(s => s.Joint, s => s.Bone),
                Springs = new CharacterSprings
                {
                    Chains = { new SpringChain { Id = "hair", Kind = SpringKind.LongHair, Anchor = "Head", Strands = { new() { "Hair1", "Hair2" } }, Damping = 0.5f } },
                    Capsules = { new SpringCapsule { Joint = "head", Radius = 0.1f } },
                },
                Materials = { new CharacterMaterial { Name = "Body", Shader = CharacterMaterial.Toon } },
            },
            PlayerColors = [new PlayerColor { Id = "suit", Default = [1, 1, 1], Slots = { new ColorSlot { Kind = ColorSlotKind.Tint, Target = "Body" } } }],
        };
        return (doc, new Dictionary<string, byte[]> { ["character/model.json"] = System.Text.Encoding.UTF8.GetBytes(descriptor), ["character/model.forgemesh"] = mesh, ["character/t0.png"] = Png4 });
    }

    // The character format (Core/Character.cs) through the pack, the descriptor and its files, the bone map against the skeleton and the
    // pose links ForgeCore copies with, and the character slot of a teammate's look; a deliberately broken one per check is caught.
    [Fact]
    public void CharacterPack()
    {
        var (doc, files) = Character();
        using (var pack = SkinPack.Open(Write(doc, files)))
        {
            var person = pack.Manifest.Character!;
            Assert.Equal(CharacterSkin.Target, pack.Manifest.Target.Key());
            var model = ForgeVisual.ImportedModelDescriptor.Parse(System.Text.Encoding.UTF8.GetString(pack.ReadBytes(person.Model.Descriptor)), out var failure)!;
            Assert.True(model != null, failure.ToString());
            Assert.Null(CharacterRig.Matches(model, pack.ReadBytes(person.Model.Mesh), [pack.ReadBytes("character/t0.png")]));
            Assert.Equal("", CharacterRig.Check(person, model));
            var links = ForgeVisual.HumanRig.Links(CharacterRig.Joints(person));
            Assert.Equal(ForgeVisual.ClipRig.Count, links.Length);
            Assert.Equal("L_Hand", links[13].Model);
            Assert.Equal(0.5, person.Springs.Chains[0].Spec().Damping, 6);

            // The skeleton with its sides swapped is no longer refused (a creature's sides need not match), but the editor's check still
            // notes it for the author; a mesh other than the descriptor's is caught.
            var swapped = SkinJson.Read(SkinJson.Write(pack.Manifest)).Character!;
            (swapped.Bones["leftUpperLeg"], swapped.Bones["rightUpperLeg"]) = (swapped.Bones["rightUpperLeg"], swapped.Bones["leftUpperLeg"]);
            (swapped.Bones["leftLowerLeg"], swapped.Bones["rightLowerLeg"]) = (swapped.Bones["rightLowerLeg"], swapped.Bones["leftLowerLeg"]);
            (swapped.Bones["leftFoot"], swapped.Bones["rightFoot"]) = (swapped.Bones["rightFoot"], swapped.Bones["leftFoot"]);
            Assert.Equal("", CharacterRig.Check(swapped, model));
            var at = Skeleton.ToDictionary(s => s.Bone, s => new double[] { s.X, s.Y, 0 });
            var noted = Backend("character.check", new { joints = swapped.Bones.ToDictionary(b => b.Key, b => at[b.Value]) });
            Assert.Contains(noted["notes"]!.AsArray(), n => (string)n!["kind"]! == "sides");
            Assert.Empty(noted["missing"]!.AsArray());
            Assert.NotNull(CharacterRig.Matches(model, [.. files["character/model.forgemesh"].Take(16), .. Enumerable.Repeat((byte)1, 180)], [Png4]));

            // A creature with one arm, no neck, one shin and no fingers is a character all the same: only the hip is required. The plugin's
            // links leave the parts without a bone out and point past them (the thigh at the foot, the chest at the head), the arm that
            // is whole still reaches, and a model without both upper arms wears no first-person arms.
            var creature = SkinJson.Read(SkinJson.Write(pack.Manifest)).Character!;
            foreach (var joint in new[] { "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand", "neck", "rightLowerLeg" }) creature.Bones.Remove(joint);
            Assert.Equal("", CharacterRig.Check(creature, model));
            var pose = CharacterRig.Links(creature, model);
            var unposed = ForgeVisual.HumanRig.Unposed(pose);
            Assert.Equal(7 + ForgeVisual.PoseFingers.Count, unposed.Length); // the six, the upper chest the skeleton never had, every finger
            Assert.Contains("leftHand", unposed);
            Assert.DoesNotContain("rightHand", unposed);
            Assert.Equal((6, 19), (pose[4].Toward, pose[8].Toward));
            Assert.Empty(CharacterRig.Arms(creature, model));
            Assert.NotEmpty(CharacterRig.Arms(person, model));
            var taken = Backend("character.check", new { joints = creature.Bones.ToDictionary(b => b.Key, b => at[b.Value]) });
            Assert.Equal((false, true, false), ((bool)taken["reach"]!["left"]!, (bool)taken["reach"]!["right"]!, (bool)taken["arms"]!));
            // Two joints on one point give each other no direction: the forearm resting on the upper arm, that one points at the hand.
            var folded = Skeleton.ToDictionary(s => s.Joint, s => s.Joint == "rightLowerArm" ? new System.Numerics.Vector3(0.2f, 1.45f, 0) : new System.Numerics.Vector3(s.X, s.Y, 0));
            var rest = ForgeVisual.ClipRig.Names.Select(n => folded.GetValueOrDefault(n)).ToArray();
            Assert.Equal(17, ForgeVisual.HumanRig.Links(CharacterRig.Joints(person), rest)[15].Toward);
        }

        // The editor's export: the converted model's files on disk built into a pack (PackBuilder) and a skin mod (ModExport), the pack
        // read back as the plugin reads it, the model licence's credit carried into the mod's README; a bone map the skeleton lacks is refused.
        var dir = Directory.CreateTempSubdirectory("forgeskin-character-").FullName;
        try
        {
            SkinDocument OnDisk()
            {
                var (project, _) = Character();
                foreach (var (name, bytes) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
                    File.WriteAllBytes(Path.Combine(dir, name), bytes);
                }
                var m = project.Character!.Model;
                (m.Descriptor, m.Mesh, m.Textures) = (Path.Combine(dir, m.Descriptor), Path.Combine(dir, m.Mesh), m.Textures.Select(x => Path.Combine(dir, x)).ToList());
                project.Character.Picture = Path.Combine(dir, "character/t0.png");
                project.Editor = new() { ["credit"] = JsonSerializer.SerializeToElement("\"Person\" by Someone (https://creativecommons.org/licenses/by/4.0/)") };
                return project;
            }
            var packPath = Path.Combine(dir, "person.forgeskin");
            ForgeSkin.Backend.PackBuilder.Build(OnDisk(), Path.Combine(dir, "person.forgeskinproj"), packPath, [], [], -1);
            using (var pack = SkinPack.Open(packPath))
            {
                ForgeSkin.Backend.PackBuilder.CheckCharacter(pack);
                Assert.Equal(("character/model.json", "character/t0.png", "character/picture.png"), (pack.Manifest.Character!.Model.Descriptor, pack.Manifest.Character.Model.Textures[0], pack.Manifest.Character.Picture));
                Assert.Equal(Png4, pack.ReadBytes("character/picture.png"));
                Assert.Null(pack.Manifest.Editor);
            }
            // 搬到我的作品: the pack back as a work, its converted model unpacked beside it with the match kept, that builds a pack the
            // plugin reads again; a name the author's works already use gets a number.
            string name;
            using (var pack = SkinPack.Open(packPath)) name = pack.Manifest.Name;
            var moved = JsonSerializer.SerializeToNode(ForgeSkin.Backend.Projects.Adopt(packPath, Path.Combine(dir, "works"), [name], (_, _, _) => null))!;
            Assert.Equal(["source"], moved["lost"]!.AsArray().Select(n => (string)n!));
            Assert.Equal($"{name} 2", (string)moved["name"]!);
            Assert.Equal(Png4, File.ReadAllBytes((string)moved["picture"]!));
            var work = ForgeSkin.Backend.Projects.Load((string)moved["path"]!, []);
            Assert.Equal(OnDisk().Character!.Bones, work.Character!.Bones);
            ForgeSkin.Backend.PackBuilder.Build(work, (string)moved["path"]!, Path.Combine(dir, "again.forgeskin"), [], [], -1);
            using (var again = SkinPack.Open(Path.Combine(dir, "again.forgeskin"))) ForgeSkin.Backend.PackBuilder.CheckCharacter(again);
            var icon = Path.Combine(dir, "icon.png");
            using (var image = SkiaSharp.SKImage.FromBitmap(new SkiaSharp.SKBitmap(8, 8))) File.WriteAllBytes(icon, image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());
            var modPath = Path.Combine(dir, "Person_Test.zip");
            ForgeSkin.Backend.ModExport.Export([packPath], modPath, "Person_Test", "1.0.0", "", icon, "forgeskin", false, () => [], _ => new());
            using (var mod = ZipFile.OpenRead(modPath))
            {
                Assert.NotNull(mod.GetEntry("person.forgeskin"));
                using var readme = new StreamReader(mod.GetEntry("README.md")!.Open());
                Assert.Contains("- \"Person\" by Someone (https://creativecommons.org/licenses/by/4.0/)", readme.ReadToEnd());
            }
            var misfit = OnDisk();
            misfit.Character!.Bones["head"] = "NoSuchBone";
            Assert.Equal("bad-character-rig", Assert.Throws<SkinFormatException>(() => ForgeSkin.Backend.PackBuilder.Build(misfit, Path.Combine(dir, "misfit.forgeskinproj"), Path.Combine(dir, "misfit.forgeskin"), [], [], -1)).Code);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        // A mesh the descriptor names by its own hash but that the game could not build (an index past the vertices): the export
        // read-back refuses it as the game's build would (ForgeCore's one file check), not only on a hash.
        var (_, past) = Character(CharacterMesh(0, 1, 7));
        var pastModel = ForgeVisual.ImportedModelDescriptor.Parse(System.Text.Encoding.UTF8.GetString(past["character/model.json"]), out _)!;
        Assert.Contains("imported-model-mesh-invalid", CharacterRig.Matches(pastModel, past["character/model.forgemesh"], [Png4]));

        // The character's slot of a look travels and comes back.
        var look = new Look { Character = new LookSkin { Skin = Look.SkinNumber(doc.Id), Hash = 42, Pack = "author-people" } };
        Look.Tweaks(doc, new Dictionary<string, float[]> { ["suit"] = [0, 0, 1] }, look.Character.Tweaks);
        var back = Look.Decode(look.Encode())!;
        Assert.Equal(LookFit.Fits, Look.Fit(back.Character!, doc, 42, out var shown, out _));
        Assert.Equal(new[] { 0f, 0, 1 }, shown!["suit"]);

        foreach (var (code, breakIt) in new (string, Action<SkinDocument>)[]
        {
            ("bad-character-bones", d => d.Character!.Bones.Remove("hip")),
            ("bad-character-spring", d => d.Character!.Springs.Chains[0].Weight = 9),
            ("bad-character-target", d => d.Target.GearId = 45),
        })
        {
            var (broken, brokenFiles) = Character();
            breakIt(broken);
            Assert.Equal(code, Assert.Throws<SkinFormatException>(() => SkinPack.Open(Write(broken, brokenFiles)).Dispose()).Code);
        }
    }

    // The inspect motion table and its solve (core algorithm, PLAN §4.14): the shipped table reads; a gun it names plays its own list,
    // one it does not name gets the fallback for its length; a later table replaces an entry by id; a broken motion is refused with the
    // table left as it was. Solved for two rigs read in game (a pistol in a 36-degree item camera, a 0.8 m rifle in a 33-degree one):
    // every frame of every motion such a gun plays, sampled at 60 a second, keeps the frame rules (InspectShot: most of the gun in the
    // view, not filling it, not at the eye or through the near plane, its centre and its shown part in the view, both hands within reach),
    // and a motion that turns the gun's muzzle at the eye is caught, as is one whose off hand would flip round its wrist; at the rifle's keys the grip hand is within the arm's reach with its
    // wrist within its limits and a rest key is the game's pose; the heavy motion's off hand under the gun is under it and within reach;
    // the revolver's twirl turns the gun about its bounds centre while the hand stays at the held pose. A wrist wrung 90 degrees is
    // turned back to its limit.
    [Fact]
    public void InspectMotionTable()
    {
        var table = new InspectTable();
        table.Add(File.ReadAllText(Path.Combine(Repo(), "Mods/ForgeSkin/Core/inspect-motions.json")));
        Assert.Equal(new[] { "pistol" }, table.For(3, 0.26f, out var fallback).Select(m => m.Id));
        Assert.False(fallback);
        Assert.Equal("long", table.For(14, 0.3f, out _)[0].Id); // the table's entry wins over the length
        Assert.Equal("compact", table.For(9999, 0.5f, out fallback)[0].Id);
        Assert.True(fallback);
        Assert.Equal("long", table.For(9999, 1.4f, out _)[0].Id);

        table.Add("""{ "motions": { "spin": { "gun": [{ "t": 0 }, { "t": 1, "roll": 90 }, { "t": 2 }] } }, "gear": { "3": ["spin"] } }""");
        var spin = table.For(3, 0.26f, out _).Single();
        spin.Intent(1f, out _, out var roll);
        Assert.Equal(90f, roll);
        Assert.Throws<FormatException>(() => table.Add("""{ "motions": { "spin": { "gun": [{ "t": 0 }, { "t": 1, "roll": 90 }] } } }"""));
        Assert.Throws<FormatException>(() => table.Add("""{ "motions": { "spin": { "gun": [{ "t": 0 }, { "t": 2 }], "hand": [{ "t": 0, "on": "top" }] } } }"""));
        Assert.Throws<FormatException>(() => table.Add("""{ "gear": { "3": ["nothing"] } }"""));
        Assert.Equal(2f, table.For(3, 0.26f, out _).Single().Seconds);

        // The rig as the game had it for an assault rifle (Malatack LX), read in game: item camera, the gun's bounds and pose, both arms.
        var gunAt = new Vector3(0.0845f, -0.1053f, 0.3599f);
        var rig = new InspectRig
        {
            TanHalf = MathF.Tan(16.5f * MathF.PI / 180f),
            Aspect = 16f / 9f,
            Near = 0.05f,
            Centre = new Vector3(0.0007f, -0.0457f, 0.0096f),
            Extents = new Vector3(0.0737f, 0.176f, 0.3945f),
            GunAt = gunAt,
            Gun = Quaternion.Identity,
            GripAt = new Vector3(0.1381f, -0.191f, 0.1578f),
            Grip = new Quaternion(-0.57192f, 0.48235f, 0.41794f, 0.51534f),
            OffAt = new Vector3(0.006f, -0.1911f, 0.4167f),
            Off = new Quaternion(-0.48466f, 0.57127f, 0.51665f, 0.41453f),
        };
        rig.GripArm = new InspectArm(new(0.1913f, -0.3233f, -0.0943f), new(0.2577f, -0.4512f, 0.2026f), new(0.138f, -0.1909f, 0.1578f), rig.GripAt, rig.Grip);
        rig.OffArm = new InspectArm(new(-0.1897f, -0.3125f, 0.0822f), new(-0.1444f, -0.4368f, 0.3845f), new(0.0061f, -0.191f, 0.4168f), rig.OffAt, rig.Off);
        // The pistol as the game had it (Shelling S49).
        var pistol = new InspectRig
        {
            TanHalf = MathF.Tan(18f * MathF.PI / 180f),
            Aspect = 16f / 9f,
            Near = 0.05f,
            Centre = new Vector3(0f, -0.0398f, -0.0149f),
            Extents = new Vector3(0.0246f, 0.0984f, 0.1252f),
            GunAt = new Vector3(0.0481f, -0.064f, 0.5306f),
            Gun = new Quaternion(0.01521f, 0.03143f, 0.00185f, -0.99939f),
            GripAt = new Vector3(0.09f, -0.1816f, 0.3451f),
            Grip = new Quaternion(-0.67208f, 0.53353f, 0.48703f, 0.16264f),
            OffAt = new Vector3(0.0132f, -0.1852f, 0.3445f),
            Off = new Quaternion(0.55337f, -0.43514f, -0.57868f, -0.41178f),
        };
        pistol.GripArm = new InspectArm(new(0.2048f, -0.4233f, 0.0154f), new(0.1879f, -0.4544f, 0.3435f), new(0.0899f, -0.1814f, 0.3452f), pistol.GripAt, pistol.Grip);
        pistol.OffArm = new InspectArm(new(-0.201f, -0.4125f, 0.123f), new(-0.051f, -0.4594f, 0.4132f), new(0.0133f, -0.1851f, 0.3445f), pistol.OffAt, pistol.Off);
        foreach (var motion in table.Motions)
        {
            var onPistol = new InspectLook(motion, pistol);
            Assert.True(float.IsNaN(onPistol.Fault), $"{motion.Id} on the pistol: at {onPistol.Fault} {onPistol.FaultRule}");
            if (motion.Id is "pistol" or "revolver") continue; // one-handed motions a rifle never plays
            var onRifle = new InspectLook(motion, rig);
            Assert.True(float.IsNaN(onRifle.Fault), $"{motion.Id} on the rifle: at {onRifle.Fault} {onRifle.FaultRule}");
        }
        var broken = new InspectTable();
        broken.Add("""{ "motions": { "face": { "gun": [{ "t": 0 }, { "t": 1, "turn": -170, "tilt": 10 }, { "t": 2 }] } } }""");
        Assert.False(float.IsNaN(new InspectLook(broken.Motions.Single(), rig).Fault));
        // A rifle rolled over in both hands: the off hand under it turns past its wrist's half turn and would flip round.
        broken.Add("""{ "motions": { "face": { "gun": [{ "t": 0 }, { "t": 0.6, "turn": -45, "roll": 30 }, { "t": 1.3, "turn": -43, "tilt": 2, "roll": 32 }, { "t": 2.0, "turn": -45, "roll": -170 }, { "t": 2.7, "turn": -43, "roll": -173 }, { "t": 3.35 }], "hand": [{ "t": 0, "on": "grip" }, { "t": 0.4, "on": "under" }, { "t": 1.4, "on": "under" }, { "t": 1.9, "on": "under", "along": 0.45 }, { "t": 2.8, "on": "under", "along": 0.45 }, { "t": 3.2, "on": "grip" }] } } }""");
        var wrapped = new InspectLook(broken.Motions.Single(), rig);
        Assert.True(wrapped.FaultRule?.Contains("wrist") == true || wrapped.FaultRule?.Contains("forearm") == true, $"a wrapping wrist was not caught: {wrapped.Fault} {wrapped.FaultRule}");

        var look = new InspectLook(table.Motions.Single(m => m.Id == "rifle"), rig);
        var keys = look.Motion.Gun;
        for (var k = 0; k < keys.Length; k++)
        {
            look.GunPose(keys[k].Time, 1f, gunAt, Quaternion.Identity, rig.TanHalf, out var at, out var turn, out var view);
            if (keys[k].Rest) { Assert.True(Vector3.Distance(gunAt, at) < 1e-4f && view == rig.TanHalf); continue; }
            Assert.True(view >= rig.TanHalf && view <= MathF.Tan(InspectLook.WideDegrees * MathF.PI / 360f) + 1e-5f, $"key {k}: view {view}");
            var hand = at + Vector3.Transform(rig.GripAt - gunAt, turn);
            Assert.True(Vector3.Distance(hand, rig.GripArm.Shoulder) <= rig.GripArm.Reach + 1e-4f);
            var held = turn * rig.Grip;
            var bent = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(rig.GripArm.Comfort(hand, held, look.Swivel[k]), held)))) * 180f / MathF.PI;
            Assert.True(bent < 2f, $"key {k}: the wrist is {bent} degrees outside its limits");
        }
        look = new InspectLook(table.Motions.Single(m => m.Id == "heavy"), rig);
        look.GunPose(1.0f, 1f, gunAt, Quaternion.Identity, rig.TanHalf, out var gunNow, out var gunTurn, out _);
        Assert.True(look.HandOff(1.0f));
        look.HandPose(1.0f, 1f, gunNow, gunTurn, rig.OffAt, rig.Off, out var under, out _);
        Assert.True(Vector3.Transform(under - gunNow, Quaternion.Inverse(gunTurn)).Y < rig.Centre.Y);
        Assert.True(Vector3.Distance(under, rig.OffArm.Shoulder) <= rig.OffArm.Reach + 1e-4f);

        // A free gun (the revolver's twirl) turns about its bounds centre while the grip hand stays at the held pose.
        look = new InspectLook(table.Motions.Single(m => m.Id == "revolver"), rig);
        look.GunPose(3.0f, 1f, gunAt, Quaternion.Identity, rig.TanHalf, out var spunAt, out var spun, out _);
        Assert.True(look.Free);
        Assert.True(Vector3.Distance(spunAt + Vector3.Transform(rig.Centre, spun), look.HeldAt + Vector3.Transform(rig.Centre, look.HeldTurn)) < 1e-4f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(spun, look.HeldTurn)), 0f, 0.99f);

        var wrung = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rig.GripAt - rig.GripArm.Elbow(rig.GripAt)), MathF.PI / 2f) * rig.Grip;
        var back = rig.GripArm.Comfort(rig.GripAt, wrung);
        Assert.InRange(2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(back, rig.Grip)))) * 180f / MathF.PI, InspectArm.Twist - 1f, InspectArm.Twist + 1f);
    }
}
