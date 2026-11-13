using System.IO.Compression;
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
            new Attachment { Id = "m1", Name = "Model", Part = "receiver", Mesh = "meshes/m1.forgeskinmesh" },
            new Attachment { Id = "s1", Name = "Ammo", Part = "receiver", Transform = "a_mag", Screen = new Screen { Digits = 3, GlyphAspect = 0.67f, GlyphMeshes = [.. Enumerable.Repeat("glyphs/s1.forgeskinmesh", 10), null, "glyphs/s1.forgeskinmesh"] } },
        },
        Parts = { new PartTransform { Part = "mag", Position = [0, 0.01f, 0] } },
        Sounds = { new SkinSound { Id = "k1", Name = "Kill", Trigger = Trigger.Kill, File = "sounds/k1.ogg" } },
        PlayerColors =
        [
            new PlayerColor
            {
                Id = "body", Name = "Body", Default = [1, 1, 1], Palette = [[1, 0, 0]],
                Slots = { new ColorSlot { Kind = ColorSlotKind.Tint, Target = "Front_UZI_2" }, new ColorSlot { Kind = ColorSlotKind.ScreenText, Target = "s1" } },
            },
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
        var path = Write(doc, Files());
        using var pack = SkinPack.Open(path);
        Assert.Equal(SkinJson.Write(doc), SkinJson.Write(pack.Manifest));
        Assert.Equal(Mesh(), pack.ReadBytes("meshes/m1.forgeskinmesh"));
        Assert.Equal(3, ForgeSkinMesh.Read(pack.ReadBytes("meshes/m1.forgeskinmesh")).Positions.Length / 3);
    }

    // A deliberately broken pack per error-code family (document, path, resource, mesh content, screen, audio, player
    // colour) must be caught.
    public static TheoryData<string, Action<SkinDocument, Dictionary<string, byte[]>>> Broken => new()
    {
        { "schema", (d, _) => d.Schema = 99 },
        { "bad-path", (d, _) => d.Materials[0].Textures[TextureChannel.Albedo] = "../evil.png" },
        { "missing-file", (_, f) => f.Remove("textures/Front_UZI_2_Albedo.png") },
        // A valid header whose counts claim far more than the data holds: caught before anything is allocated.
        { "bad-mesh", (_, f) => f["meshes/m1.forgeskinmesh"] = [.. Mesh().Take(4), .. BitConverter.GetBytes(int.MaxValue / 3), .. BitConverter.GetBytes(3), 0, 0, 0, 0] },
        { "bad-screen", (d, _) => d.Attachments[1].Screen!.Digits = 9 },
        { "bad-audio", (_, f) => f["sounds/k1.ogg"] = "RIFF0000WAVE"u8.ToArray() },
        { "bad-player-color", (d, _) => d.PlayerColors![0].Slots[1].Target = "gone" },
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
        });
        Assert.Equal(code, e.Code);
    }

    [Fact]
    public void PackWithoutManifestIsRejected() =>
        Assert.Equal("no-manifest", Assert.Throws<SkinFormatException>(() => SkinPack.Open(Write(Doc(), Files(), manifest: false))).Code);
}
