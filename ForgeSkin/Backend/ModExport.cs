using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// A Thunderstore skin mod: the built packs plus the `forgeskin.forge` marker the plugin looks for in each mod folder, and
// the files Thunderstore itself requires (manifest.json, icon.png 256x256, README.md). r2modman unpacks it into
// BepInEx/plugins/<Team>-<Name>/, where the plugin finds the marker. The mod depends on the base library.
// In the Skins format (SkinsFormat) each pack is a skin group under Assets/Skins/, which the mod manager unpacks into
// BepInEx/Assets/Skins/ where the Skins library looks; the mod depends on that library instead.
public static class ModExport
{
    // Thunderstore ids of the libraries the skin mods depend on; the base library is this release of the ForgeSkin plugin.
    const string BaseVersion = "1.0.3"; // package-version: NAinfini-ForgeSkin
    const string BaseLibrary = "NAinfini-ForgeSkin-" + BaseVersion, SkinsLibrary = "food-Skins-0.12.0";
    const string Marker = "forgeskin.forge";

    public static object Export(string[] packs, string output, string name, string version, string description, string icon,
        string format, Func<List<CatalogItem>> catalog)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$")) throw new BackendException("bad-request", "Mod name: letters, digits and _ only");
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new BackendException("bad-request", "Version must look like 1.0.0");
        if (description.Length > 250) throw new BackendException("bad-request", "Description: at most 250 characters");
        if (packs.Length == 0) throw new BackendException("bad-request", "No skin packs to export");
        var docs = packs.Select(p => { using var pack = SkinPack.Open(p); return pack.Manifest; }).ToList();
        bool skins = format == "skins";
        var dropped = new List<string>();

        using var temp = new AtomicFile(output);
        using (var zip = ZipFile.Open(temp.Temp, ZipArchiveMode.Create))
        {
            Text(zip, "manifest.json", JsonSerializer.Serialize(new
            {
                name,
                version_number = version,
                website_url = "",
                description,
                dependencies = new[] { skins ? SkinsLibrary : BaseLibrary },
            }, new JsonSerializerOptions { WriteIndented = true }));
            using (var s = zip.CreateEntry("icon.png").Open()) Icon(icon, s);
            if (skins)
            {
                var items = catalog();
                for (int i = 0; i < packs.Length; i++) SkinsFormat.Write(zip, packs[i], $"Assets/Skins/{Preview.Safe(docs[i].Id)}", items, dropped);
            }
            else
            {
                var files = packs.Select(p => "skins/" + Path.GetFileName(p)).ToList();
                Text(zip, Marker, "# ForgeSkin skin packs in this mod, one per line\n" + string.Join("\n", files) + "\n");
                for (int i = 0; i < packs.Length; i++) zip.CreateEntryFromFile(packs[i], files[i], CompressionLevel.NoCompression);
            }
            Text(zip, "README.md", $"# {name.Replace('_', ' ')}\n\n{description}\n\nSkins for GTFO, applied by {(skins ? "the Skins library" : "ForgeSkin")} (installed automatically as a dependency).\n\n"
                + string.Join("\n", docs.Select(d => $"- {d.Name}")) + "\n");
        }
        temp.Commit();
        return new { mod = output, dropped };
    }

    static void Text(ZipArchive zip, string entry, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(entry).Open());
        w.Write(text);
    }

    // Thunderstore wants a 256x256 PNG; any image is scaled to cover that square.
    static void Icon(string path, Stream output)
    {
        using var source = SKBitmap.Decode(path) ?? throw new BackendException("bad-request", $"Icon {path} is not an image");
        using var surface = SKSurface.Create(new SKImageInfo(256, 256));
        float scale = 256f / Math.Min(source.Width, source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        surface.Canvas.DrawBitmap(source, new SKRect((256 - w) / 2, (256 - h) / 2, (256 + w) / 2, (256 + h) / 2), paint);
        using var image = surface.Snapshot();
        image.Encode(SKEncodedImageFormat.Png, 100).SaveTo(output);
    }
}
