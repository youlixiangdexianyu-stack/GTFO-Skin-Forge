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
        Picture = "picture.png",
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
        Assert.Equal(2 + 4 + 1 + 4 + 4 + 8 + 1 + gear.Pack.Length + 1 + 1 + (1 + 3) + (1 + 2) + (1 + 4), bytes.Length);
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
        { "bad-color-scheme", (d, _) => d.ColorSchemes![0].Colors["gone"] = [0, 0, 1] },
        { "too-many-player-colors", (d, _) => d.PlayerColors!.AddRange(Enumerable.Range(1, SkinPack.MaxPlayerColors).Select(i => new PlayerColor { Id = "c" + i, Slots = d.PlayerColors[0].Slots })) },
        // Code effects, checked against ForgeCore's program catalogue: a program it lacks, a value outside the catalogue's range, an
        // attachment without a model to draw on.
        { "bad-fx-program", (d, _) => d.CodeEffects![0].Program = "lava" },
        { "bad-fx-value", (d, _) => d.CodeEffects![0].Params["speed"] = 99 },
        { "bad-fx-target", (d, _) => d.CodeEffects![1].Attachment = "s1" },
        // A layer target on an attachment without a model (a screen).
        { "bad-material-target", (d, _) => d.Materials[1].Material = MaterialOverride.ForAttachment("s1") },
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

            // The skeleton with its sides swapped, and a mesh other than the descriptor's: both caught.
            var swapped = SkinJson.Read(SkinJson.Write(pack.Manifest)).Character!;
            (swapped.Bones["leftUpperLeg"], swapped.Bones["rightUpperLeg"]) = (swapped.Bones["rightUpperLeg"], swapped.Bones["leftUpperLeg"]);
            (swapped.Bones["leftLowerLeg"], swapped.Bones["rightLowerLeg"]) = (swapped.Bones["rightLowerLeg"], swapped.Bones["leftLowerLeg"]);
            (swapped.Bones["leftFoot"], swapped.Bones["rightFoot"]) = (swapped.Bones["rightFoot"], swapped.Bones["leftFoot"]);
            Assert.Contains("swapped", CharacterRig.Check(swapped, model));
            Assert.NotNull(CharacterRig.Matches(model, [.. files["character/model.forgemesh"].Take(16), .. Enumerable.Repeat((byte)1, 180)], [Png4]));
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
            ("bad-character-bones", d => d.Character!.Bones.Remove("leftHand")),
            ("bad-character-spring", d => d.Character!.Springs.Chains[0].Weight = 9),
            ("bad-character-target", d => d.Target.GearId = 45),
        })
        {
            var (broken, brokenFiles) = Character();
            breakIt(broken);
            Assert.Equal(code, Assert.Throws<SkinFormatException>(() => SkinPack.Open(Write(broken, brokenFiles)).Dispose()).Code);
        }
    }
}
