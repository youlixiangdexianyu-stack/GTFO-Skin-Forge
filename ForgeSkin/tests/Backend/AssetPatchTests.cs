using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SkiaSharp;
using ForgeSkin.Backend;
using ForgeSkin.Format;
using Xunit;

namespace ForgeSkin.Tests;

// Local-resource layer. Asset replacement runs on a copy of the game files the HEL needs (copied on first use under the test
// data dir and refreshed when the game's file differs); the real install is only read. The copy has the real build id, so
// it reads the catalog and prefab index the real install wrote (preview.weapon and project.load run first).
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
            Materials = { new MaterialOverride { Material = "Front_UZI_2", Textures = { [TextureChannel.Albedo] = Path.Combine(dir, "front.png") } } },
        };
        var projectPath = Path.Combine(dir, "assets.forgeskinproj");
        Call("project.save", new { project, path = projectPath });
        var pack = (string)Call("pack.build", new { project = Call("project.load", new { path = projectPath }), projectPath, @out = Path.Combine(dir, "out") })["pack"]!;

        var file = (string)OnCopy("assets.apply", new { pack, gameDir = copy })["files"]![0]!;
        // Applying again (as after an edit) reads the patched file from its backup and rebuilds it, never stacking edits.
        Assert.Equal(file, (string)Call("assets.apply", new { pack, gameDir = copy })["files"]![0]!);
        var target = Path.Combine(copy, "GTFO_Data", file);
        var original = Path.Combine(real, "GTFO_Data", file);
        Assert.Equal("patched", (string)Call("assets.status", new { gameDir = copy })["files"]![0]!["state"]!);

        long added;
        (int, int, byte[], string) decoded;
        string addedKey;
        AssetsManager Manager() => new();
        var (am, bm) = (Manager(), Manager());
        try
        {
            var (a, b) = (am.LoadAssetsFile(original, false), bm.LoadAssetsFile(target, false));
            byte[] Raw(AssetsFileInstance f, AssetFileInfo i) { f.file.Reader.Position = i.GetAbsoluteByteOffset(f.file); return f.file.Reader.ReadBytes((int)i.ByteSize); }
            var changed = a.file.AssetInfos.Where(i => b.file.GetAssetInfo(i.PathId) is not { } j || !Raw(a, i).AsSpan().SequenceEqual(Raw(b, j))).ToList();
            Assert.Equal([(int)AssetClassID.Material], changed.Select(i => i.TypeId));
            added = b.file.AssetInfos.Single(j => a.file.GetAssetInfo(j.PathId) == null && j.TypeId == (int)AssetClassID.Texture2D).PathId;
            using (var tpk = typeof(GameAssets).Assembly.GetManifestResourceStream("classdata.tpk")!) bm.LoadClassPackage(tpk);
            bm.LoadClassDatabaseFromPackage(b.file.Metadata.UnityVersion);
            var tex = TextureFile.ReadTextureFile(bm.GetBaseField(b, b.file.GetAssetInfo(added)));
            var loaded = (tex, tex.FillPictureData(b));
            (decoded, addedKey) = (Unity.DecodeTexture(loaded), Preview.TextureKey(loaded));
        }
        finally
        {
            am.UnloadAll(true);
            bm.UnloadAll(true);
        }
        using (var g = new GameAssets(copy))
        {
            // Readers take the intact backup of a file this tool rewrote, so they see the game's own content; a texture that
            // is not the game's own (here the one the patch added) is caught by the vanilla list.
            Assert.Null(g.Load(file).file.GetAssetInfo(added));
            Assert.False(g.IsVanilla(addedKey));
            var (w, h, rgba, _) = decoded;
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
