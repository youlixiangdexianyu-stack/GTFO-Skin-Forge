using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeSkin.Format;
using SkiaSharp;

namespace ForgeSkin.Backend;

// glTF attachment (.glb or .gltf) -> .forgeskinmesh (Unity space) plus a GLB written back from it, so the editor shows exactly
// what the game gets, and the model's pictures as PNGs the editor puts on the attachment's material. A .forgeskinmesh (a
// skin's model whose preview is gone) is kept as it is and only gets the GLB.
public static class MeshImport
{
    public static object Import(string glbPath)
    {
        var bytes = File.Exists(glbPath) ? File.ReadAllBytes(glbPath) : throw new BackendException("not-found", $"{glbPath} not found");
        var own = ForgeSkinMesh.IsMesh(bytes);
        var mesh = own ? FromForgeSkinMesh(ForgeSkinMesh.Read(bytes)) : GlbReader.Read(glbPath);
        var look = own ? new GlbLook() : GlbReader.Look(glbPath);
        var written = own ? bytes : ToForgeSkinMesh(mesh).Write();
        // Named by everything read: a .gltf's buffers and pictures are files of their own.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in new[] { bytes, written, look.BaseColor, look.Normal, look.MetallicRoughness, look.Emissive }) if (part != null) sha.AppendData(part);
        var hash = Convert.ToHexString(sha.GetHashAndReset())[..16].ToLowerInvariant();
        var dir = Path.Combine(Paths.Cache, "meshes");
        Directory.CreateDirectory(dir);
        var name = $"{Preview.Safe(Path.GetFileNameWithoutExtension(glbPath))}-{hash}";
        var forgeskinmesh = own ? glbPath : Path.Combine(dir, name + ".forgeskinmesh");
        if (!own) File.WriteAllBytes(forgeskinmesh, written);

        var glb = new GlbWriter();
        int m = glb.Mesh(mesh, ["attachment"]);
        var preview = Path.Combine(dir, name + ".glb");
        glb.Save(preview, glb.Node(mesh.Name, Vector3.Zero, Quaternion.Identity, Vector3.One, m, new()));

        var min = mesh.Positions.Aggregate(Vector3.Min);
        var max = mesh.Positions.Aggregate(Vector3.Max);
        var textures = Pictures(look, Path.Combine(dir, name));
        float[]? emissive = textures.ContainsKey(TextureChannel.Emissive) ? look.EmissiveFactor : null;
        return new
        {
            forgeskinmesh,
            glb = preview,
            bounds = new { min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } },
            textures = textures.ToDictionary(t => JsonNamingPolicy.CamelCase.ConvertName(t.Key.ToString()), t => t.Value),
            // glTF's factors are linear; a skin's colours are sRGB (ForgeVisual.ColourSpace).
            color = look.BaseColorFactor.All(v => v == 1) ? null : look.BaseColorFactor.Select((v, i) => i < 3 ? ForgeVisual.ColourSpace.ToSrgb(v) : v).ToArray(),
            emissive = emissive == null ? null : new { color = emissive.Select(ForgeVisual.ColourSpace.ToSrgb).ToArray(), intensity = look.EmissiveStrength },
            notes = look.Notes,
        };
    }

    // The model's pictures as the attachment's material textures (`<stem>_<channel>.png`): their own size, scaled down only past
    // the side the game can make a texture of (Dds.MaxSide), metallic-roughness turned into the gear shader's metallic (R) and smoothness (G). A picture that cannot be read
    // is left out with a note; every kept one notes its size.
    static Dictionary<TextureChannel, string> Pictures(GlbLook look, string stem)
    {
        var result = new Dictionary<TextureChannel, string>();
        void Keep(byte[]? bytes, TextureChannel channel, Action<byte[]>? convert = null)
        {
            if (bytes == null) return;
            SKBitmap bmp;
            try { bmp = Images.Decode(bytes, channel.ToString()); }
            catch (SkinFormatException) { look.Notes.Add($"{channel}: not a readable picture; left out"); return; }
            using (bmp)
            {
                double k = Math.Min(1, (double)Dds.MaxSide / Math.Max(bmp.Width, bmp.Height));
                int w = Math.Max(1, (int)Math.Round(bmp.Width * k)), h = Math.Max(1, (int)Math.Round(bmp.Height * k));
                look.Notes.Add(k < 1 ? $"{channel}: {bmp.Width}x{bmp.Height} scaled to {w}x{h}" : $"{channel}: {w}x{h}");
                // Named by the model's content: an earlier import's file is the same picture (and may be open in the editor).
                var path = result[channel] = $"{stem}_{channel}.png";
                if (File.Exists(path)) return;
                using var sized = Images.Resize(bmp, w, h);
                if (convert != null)
                {
                    var px = sized.Bytes;
                    convert(px);
                    using var converted = Images.FromRgba(w, h, px);
                    Save(converted, path);
                }
                else Save(sized, path);
            }
        }
        Keep(look.BaseColor, TextureChannel.Albedo);
        Keep(look.Normal, TextureChannel.Normal);
        Keep(look.MetallicRoughness, TextureChannel.MetallicSmoothness, px =>
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                byte metal = (byte)Math.Round(px[i + 2] * look.MetallicFactor), smooth = (byte)Math.Round(255 - px[i + 1] * look.RoughnessFactor);
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (metal, smooth, 0, 255);
            }
        });
        if (look.EmissiveFactor.Any(v => v > 0)) Keep(look.Emissive, TextureChannel.Emissive);
        else if (look.Emissive != null) look.Notes.Add("Emissive: its strength is 0; left out");
        return result;
    }

    static void Save(SKBitmap bmp, string path)
    {
        using var temp = new AtomicFile(path);
        Images.SavePng(bmp, temp.Temp);
        temp.Commit();
    }

    public static ForgeSkinMesh ToForgeSkinMesh(MeshData m) => new()
    {
        Positions = m.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Normals = m.Normals?.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Uv0 = m.Uv0?.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
        Indices = m.SubMeshes.SelectMany(s => s).Select(i => (uint)i).ToArray(),
    };

    static MeshData FromForgeSkinMesh(ForgeSkinMesh m) => new()
    {
        Positions = Enumerable.Range(0, m.Positions.Length / 3).Select(i => new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2])).ToArray(),
        Normals = m.Normals == null ? null : Enumerable.Range(0, m.Normals.Length / 3).Select(i => new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2])).ToArray(),
        Uv0 = m.Uv0 == null ? null : Enumerable.Range(0, m.Uv0.Length / 2).Select(i => new Vector2(m.Uv0[i * 2], m.Uv0[i * 2 + 1])).ToArray(),
        SubMeshes = [m.Indices.Select(i => (int)i).ToArray()],
    };
}
