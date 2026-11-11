using SkiaSharp;
using SkinForge.Backend;
using SkinForge.Format;
using Xunit;

namespace SkinForge.Tests;

// Matching on a small generated fixture: recognition by file name, by structure against the game texture, and leftovers.
public class MatchingTests
{
    static string Image(string dir, string name, Action<SKCanvas> draw, SKColor background)
    {
        using var bmp = new SKBitmap(128, 128);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(background);
            draw(c);
        }
        var path = Path.Combine(dir, name);
        Images.SavePng(bmp, path);
        return path;
    }

    static readonly SKPaint Light = new() { Color = new SKColor(220, 220, 220) };
    static readonly SKPaint Dark = new() { Color = new SKColor(30, 30, 30) };

    static void Bars(SKCanvas c) { for (int x = 0; x < 128; x += 32) c.DrawRect(x, 0, 12, 128, Light); }
    static void Rings(SKCanvas c) { c.DrawCircle(64, 64, 50, Light); c.DrawCircle(64, 64, 30, Dark); c.DrawRect(0, 100, 128, 10, Light); }

    [Fact]
    public void MatchesByNameAndStructure()
    {
        var dir = Directory.CreateTempSubdirectory("sf-match-").FullName;
        var game = Directory.CreateDirectory(Path.Combine(dir, "game")).FullName;
        var user = Directory.CreateDirectory(Path.Combine(dir, "user")).FullName;
        var grey = new SKColor(90, 90, 90);
        var baseline = new Dictionary<string, Dictionary<TextureChannel, string>>
        {
            ["Front_UZI_2"] = new() { [TextureChannel.Albedo] = Image(game, "front.png", Bars, grey) },
            ["Receiver_UZI_1"] = new() { [TextureChannel.Albedo] = Image(game, "receiver.png", Rings, grey) },
        };
        var item = new CatalogItem("auto_pistol", "Raptus Steigro", null, "Pistol", "special", 66, null,
        [
            new CatalogPart("front", "front", "front", ["Front_UZI_2"], true),
            new CatalogPart("receiver", "receiver", "receiver", ["Receiver_UZI_1"], true),
            new CatalogPart("stock", "stock", "stock", ["Stock_Pistol_1"], true),
        ]);
        var files = new List<string>
        {
            Image(user, "front_UZI_2_basecolor.png", Bars, new SKColor(140, 60, 60)),
            Image(user, "front_uzi_2_normal.png", c => { }, new SKColor(128, 128, 255)),
            Image(user, "mystery1.png", Rings, new SKColor(60, 90, 140)), // receiver albedo recoloured, no useful name
            Image(user, "MyGun_stock_BaseColor.png", Rings, new SKColor(200, 200, 40)), // Substance-style: <weapon>_<part>_<channel>
        };

        var result = Matching.Match(item, [item], baseline, files);
        ChannelMatch Cell(string material, TextureChannel c) => result.Parts.Single(p => p.Material == material).Channels[c];

        Assert.Equal(files[0], Cell("Front_UZI_2", TextureChannel.Albedo).File);
        Assert.Equal(files[1], Cell("Front_UZI_2", TextureChannel.Normal).File);
        Assert.Equal(files[2], Cell("Receiver_UZI_1", TextureChannel.Albedo).File);
        Assert.Equal(files[3], Cell("Stock_Pistol_1", TextureChannel.Albedo).File);
        Assert.Null(Cell("Stock_Pistol_1", TextureChannel.Normal).File);
        Assert.Empty(result.Unmatched);
    }
}
