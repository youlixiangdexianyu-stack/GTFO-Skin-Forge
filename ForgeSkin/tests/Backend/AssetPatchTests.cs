using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using SkiaSharp;
using ForgeSkin.Backend;
using ForgeSkin.Format;
using Xunit;

namespace ForgeSkin.Tests;

// Local-resource layer. Asset replacement runs on a copy of the game files the HEL needs (copied on first use under the test
// data dir and refreshed when the game's file differs); the real install is only read.
public class AssetPatchTests
{
    static JsonNode Call(string method, object? args = null) =>
        JsonSerializer.SerializeToNode(Methods.Call(method, JsonSerializer.SerializeToNode(args ?? new { }, Program.Json)!.AsObject(), 1), Program.Json)!;

    static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

    // Core round trip: apply an albedo override, reopen the patched file (every original object byte-identical except the
    // Material, the added texture decodes to the pack image within DXT1 tolerance), restore the original bytes.
    [GameFact]
    public void ApplyReadBackRestore()
    {
        var real = Game.Locate().GameDir!;
        var copy = Path.Combine(Paths.Data, "assets-test-game");
        void Need(string rel)
        {
            var (from, to) = (Path.Combine(real, rel), Path.Combine(copy, rel));
            if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length && File.GetLastWriteTimeUtc(to) == File.GetLastWriteTimeUtc(from)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
        JsonNode OnCopy(string method, object args)
        {
            while (true)
                try { return Call(method, args); }
                catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException
                    && Regex.Match(e.Message, "'([^']+)'") is { Success: true } m && m.Groups[1].Value.StartsWith(copy, StringComparison.OrdinalIgnoreCase))
                {
                    Need(Path.Combine("GTFO_Data", Path.GetFileName(m.Groups[1].Value))); // GameAssets also looks in GTFO_Data/Resources
                }
        }
        foreach (var rel in new[] { "revision.txt", "GTFO_Data/globalgamemanagers", "GTFO_Data/resources.assets" }) Need(rel);
        Call("assets.restore", new { gameDir = copy }); // a record left by an aborted run

        var dir = Directory.CreateTempSubdirectory("sf-assets-").FullName;
        using (var bmp = Images.Load((string)Call("preview.weapon", new { id = "auto_pistol" })["textures"]!["Front_UZI_2"]!["albedo"]!))
        {
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); bmp.SetPixel(x, y, new SKColor((byte)~c.Red, (byte)~c.Green, (byte)~c.Blue)); }
            Images.SavePng(bmp, Path.Combine(dir, "front.png"));
        }
        var project = new SkinDocument
        {
            Id = "skin-assets-test",
            Name = "Assets test",
            Target = new SkinTarget { Weapon = "auto_pistol" },
            Materials = { new MaterialOverride { Material = "Front_UZI_2", Textures = { [TextureChannel.Albedo] = "front.png" } } },
        };
        var projectPath = Path.Combine(dir, "assets.forgeskinproj");
        Call("project.save", new { project, path = projectPath });
        var pack = (string)Call("pack.build", new { project = Call("project.load", new { path = projectPath }), projectPath, @out = Path.Combine(dir, "out") })["pack"]!;

        var file = (string)OnCopy("assets.apply", new { pack, gameDir = copy })["files"]![0]!;
        var target = Path.Combine(copy, "GTFO_Data", file);
        var original = Path.Combine(real, "GTFO_Data", file);
        Assert.Equal("patched", (string)Call("assets.status", new { gameDir = copy })["files"]![0]!["state"]!);

        long added;
        AssetsManager Manager() => new();
        var (am, bm) = (Manager(), Manager());
        try
        {
            var (a, b) = (am.LoadAssetsFile(original, false), bm.LoadAssetsFile(target, false));
            byte[] Raw(AssetsFileInstance f, AssetFileInfo i) { f.file.Reader.Position = i.GetAbsoluteByteOffset(f.file); return f.file.Reader.ReadBytes((int)i.ByteSize); }
            var changed = a.file.AssetInfos.Where(i => b.file.GetAssetInfo(i.PathId) is not { } j || !Raw(a, i).AsSpan().SequenceEqual(Raw(b, j))).ToList();
            Assert.Equal([(int)AssetClassID.Material], changed.Select(i => i.TypeId));
            added = b.file.AssetInfos.Single(j => a.file.GetAssetInfo(j.PathId) == null && j.TypeId == (int)AssetClassID.Texture2D).PathId;
        }
        finally
        {
            am.UnloadAll(true);
            bm.UnloadAll(true);
        }
        using (var g = new GameAssets(copy))
        {
            var (w, h, rgba, _) = Unity.ReadTexture(g, new TexRef(g.Load(file), added, [1, 1], [0, 0]));
            using var expected = Images.Load(Path.Combine(dir, "front.png"));
            Assert.Equal((expected.Width, expected.Height), (w, h));
            var want = expected.Bytes;
            double error = 0;
            for (int i = 0; i < want.Length; i += 4)
                for (int c = 0; c < 3; c++) error += Math.Abs(want[i + c] - rgba[i + c]);
            Assert.True(error / (want.Length / 4 * 3) < 3, $"mean error {error / (want.Length / 4 * 3):F2}");
        }

        Assert.Equal([file], Call("assets.restore", new { gameDir = copy })["restored"]!.AsArray().Select(f => (string)f!));
        Assert.Equal(Sha(original), Sha(target));
    }
}
