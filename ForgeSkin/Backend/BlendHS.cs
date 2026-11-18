using System.Numerics;
using SkiaSharp;

namespace ForgeSkin.Backend;

// GTFO/BlendHS materials have no albedo texture: the shader picks one of up to 16 colour layers per vertex
// (vertex colour R * 16 = layer index), blends the layer's two colours _L<i>P1 and _L<i>P2 by vertex colour G, and shades
// with the baked AO/curvature map _AOCurveTex (G channel). This bakes that into UV-space Albedo and MetallicSmoothness
// (the gear shader's layout: metallic R, smoothness G) textures so the editor shows the game's look and users have a base to paint on. Detail textures, dust, grime and damage
// the shader adds on top are not reproduced.
public static class BlendHS
{
    public const string Shader = "GTFO/BlendHS";

    public static void Bake(GameAssets g, MaterialData m, List<(MeshData Mesh, int SubMesh)> uses, string albedoPath, string msPath)
    {
        var (w, h, ao, _) = Unity.ReadTexture(g, m.Textures["_AOCurveTex"]);
        float[] Layer(Vector4 c, int p) =>
            m.Colors.TryGetValue($"_L{Math.Clamp((int)MathF.Round(c.X * 16), 0, 15)}P{p}", out var v) ? v : [1, 1, 1, 0.5f];
        (Vector3 Rgb, float Smooth) Surface(Vector4 c)
        {
            float[] a = Layer(c, 1), b = Layer(c, 2);
            float t = c.Y;
            return (Vector3.Lerp(new(a[0], a[1], a[2]), new(b[0], b[1], b[2]), t), a[3] + (b[3] - a[3]) * t);
        }
        static SKColor Srgb(Vector3 linear, float alpha = 1) => new(
            (byte)Math.Round(255 * MathF.Pow(Math.Clamp(linear.X, 0, 1), 1 / 2.2f)), (byte)Math.Round(255 * MathF.Pow(Math.Clamp(linear.Y, 0, 1), 1 / 2.2f)),
            (byte)Math.Round(255 * MathF.Pow(Math.Clamp(linear.Z, 0, 1), 1 / 2.2f)), (byte)Math.Round(255 * alpha));

        // Layer colours per vertex, rasterised in UV space (Unity V up -> image rows top first).
        using var albedo = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var ms = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var background = uses.Where(u => u.Mesh.Colors != null).SelectMany(u => u.Mesh.Colors!)
            .GroupBy(c => (int)MathF.Round(c.X * 16) * 2 + (c.Y > 0.5f ? 1 : 0)).OrderByDescending(x => x.Count()).Select(x => x.First()).FirstOrDefault();
        using (var ca = new SKCanvas(albedo))
        using (var cm = new SKCanvas(ms))
        using (var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src })
        {
            var bg = Surface(background);
            ca.Clear(Srgb(bg.Rgb));
            cm.Clear(new SKColor(0, (byte)Math.Round(255 * bg.Smooth), 0, 255));
            foreach (var (mesh, sub) in uses)
            {
                if (mesh.Uv0 == null || mesh.Colors == null || sub >= mesh.SubMeshes.Count) continue;
                var idx = mesh.SubMeshes[sub];
                var points = idx.Select(i => new SKPoint(mesh.Uv0[i].X * w, (1 - mesh.Uv0[i].Y) * h)).ToArray();
                var surfaces = idx.Select(i => Surface(mesh.Colors[i])).ToArray();
                ca.DrawVertices(SKVertexMode.Triangles, points, surfaces.Select(s => Srgb(s.Rgb)).ToArray(), paint);
                cm.DrawVertices(SKVertexMode.Triangles, points, surfaces.Select(s => new SKColor(0, (byte)Math.Round(255 * s.Smooth), 0, 255)).ToArray(), paint);
            }
        }

        // Cavities from the AO/curvature map darken the colour (mean G ~0.57 maps to ~1).
        var px = albedo.Pixels;
        for (int i = 0; i < px.Length; i++)
        {
            float k = Math.Clamp(ao[i * 4 + 1] / 255f * 1.75f, 0.15f, 1f);
            px[i] = new SKColor((byte)(px[i].Red * k), (byte)(px[i].Green * k), (byte)(px[i].Blue * k), 255);
        }
        albedo.Pixels = px;
        Images.SavePng(albedo, albedoPath);
        Images.SavePng(ms, msPath);
    }
}
