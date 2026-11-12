using SkiaSharp;
using ForgeSkin.Backend;
using Xunit;

namespace ForgeSkin.Tests;

// Core algorithm: screen glyphs traced into geometry cover the strokes and leave the counters (inside of 0 and 8) empty.
public class GlyphMeshTests
{
    const int CellW = 128, CellH = 192;

    // The strip the app bakes: "0123456789 -", white on transparent, one cell per glyph.
    public static SKBitmap Strip()
    {
        var bmp = new SKBitmap(new SKImageInfo(CellW * GlyphMesh.Count, CellH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = CellH * 0.8f, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) };
        for (int g = 0; g < GlyphMesh.Count; g++) c.DrawText("0123456789 -"[g].ToString(), g * CellW + CellW / 2f, CellH * 0.78f, paint);
        return bmp;
    }

    [Fact]
    public void GlyphMeshCoversStrokesAndLeavesCountersEmpty()
    {
        using var strip = Strip();
        var (glyphs, aspect) = GlyphMesh.Build(strip);
        Assert.Equal(CellW / (float)CellH, aspect, 3);
        Assert.Null(glyphs[10]); // blank
        foreach (int g in new[] { 0, 8 })
        {
            var mesh = glyphs[g]!;
            // Cell pixel (x, y) -> mesh units: height 1, centred, Y up.
            bool Covered(int x, int y) => Inside(mesh, (x + 0.5f - CellW / 2f) / CellH, (CellH / 2f - (y + 0.5f)) / CellH);
            int strokes = 0, holes = 0;
            for (int y = 2; y < CellH; y += 4)
                for (int x = 2; x < CellW; x += 4)
                {
                    byte a = strip.GetPixel(g * CellW + x, y).Alpha;
                    if (a > 240) { Assert.True(Covered(x, y), $"'{g}' stroke pixel {x},{y} not covered"); strokes++; }
                    else if (a < 15) { Assert.False(Covered(x, y), $"'{g}' empty pixel {x},{y} covered"); holes++; }
                }
            Assert.True(strokes > 50 && holes > 50);
            // The counter: the middle of 0, a third of the way down in 8, is transparent and stays uncovered.
            int cy = g == 0 ? CellH / 2 : (int)(CellH * 0.36f);
            Assert.True(strip.GetPixel(g * CellW + CellW / 2, cy).Alpha < 15);
            Assert.False(Covered(CellW / 2, cy));
        }
    }

    static bool Inside(ForgeSkin.Format.ForgeSkinMesh m, float x, float y)
    {
        var p = m.Positions;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            int a = (int)m.Indices[t] * 3, b = (int)m.Indices[t + 1] * 3, c = (int)m.Indices[t + 2] * 3;
            float d1 = Cross(p[a], p[a + 1], p[b], p[b + 1], x, y), d2 = Cross(p[b], p[b + 1], p[c], p[c + 1], x, y), d3 = Cross(p[c], p[c + 1], p[a], p[a + 1], x, y);
            // Every triangle is clockwise as seen from -Z (X right, Y up): inside means all three on the right.
            if (d1 <= 0 && d2 <= 0 && d3 <= 0) return true;
        }
        return false;
    }

    static float Cross(float ax, float ay, float bx, float by, float x, float y) => (bx - ax) * (y - ay) - (by - ay) * (x - ax);
}
