using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkinForge.Format;

namespace SkinForge.Backend;

// A Thunderstore skin mod: the built packs plus the `forgeskin.forge` marker the plugin looks for in each mod folder, and
// the files Thunderstore itself requires (manifest.json, icon.png 256x256, README.md). r2modman unpacks it into
// BepInEx/plugins/<Team>-<Name>/, where the plugin finds the marker. The mod depends on the base library.
public static class ModExport
{
    // Thunderstore id of the base library the skin mods depend on.
    const string BaseLibrary = "NAinfini-ForgeSkin-1.0.0";
    const string Marker = "forgeskin.forge";

    public static object Export(string[] packs, string output, string name, string version, string description, string icon)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$")) throw new BackendException("bad-request", "Mod name: letters, digits and _ only");
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new BackendException("bad-request", "Version must look like 1.0.0");
        if (description.Length > 250) throw new BackendException("bad-request", "Description: at most 250 characters");
        if (packs.Length == 0) throw new BackendException("bad-request", "No skin packs to export");
        var docs = packs.Select(p => { using var pack = SkinPack.Open(p); return pack.Manifest; }).ToList();

        var temp = output + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Text(zip, "manifest.json", JsonSerializer.Serialize(new
            {
                name, version_number = version, website_url = "", description, dependencies = new[] { BaseLibrary },
            }, new JsonSerializerOptions { WriteIndented = true }));
            using (var s = zip.CreateEntry("icon.png").Open()) Icon(icon, s);
            var files = packs.Select(p => "skins/" + Path.GetFileName(p)).ToList();
            Text(zip, Marker, "# ForgeSkin skin packs in this mod, one per line\n" + string.Join("\n", files) + "\n");
            for (int i = 0; i < packs.Length; i++) zip.CreateEntryFromFile(packs[i], files[i], CompressionLevel.NoCompression);
            Text(zip, "README.md", $"# {name.Replace('_', ' ')}\n\n{description}\n\nSkins for GTFO, applied by ForgeSkin (installed automatically as a dependency).\n\n"
                + string.Join("\n", docs.Select(d => $"- {d.Name}")) + "\n");
        }
        File.Move(temp, output, true);
        return new { mod = output };
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
