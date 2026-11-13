"""Writes the kit's symbol fonts: ForgeUISymbols-Medium.ttf and ForgeUISymbols-SemiBold.ttf, one glyph each, the infinity sign
(U+221E), drawn here to sit with Rajdhani's digits (same em, the digits' middle, stroke between Rajdhani's stems). Rajdhani and Glow
Sans SC Condensed (common) have no infinity sign; these fonts are the next fallback after Rajdhani. Run by build_fonts.ps1:
    python Mods/ForgeCore/UI/tools/fonts/make_symbols.py <output folder>
Needs fontTools."""
import math
import sys
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

EM, ASCENT, DESCENT = 1000, 930, -346  # Rajdhani's em and line metrics
ADVANCE, HALF_WIDTH, HALF_HEIGHT, MIDDLE = 760, 335, 190, 322  # MIDDLE: half of Rajdhani's digit height (643)
WEIGHTS = (("Medium", 500, 66), ("SemiBold", 600, 86))  # name, weight class, stroke
STEPS = 40  # straight pieces per quarter of the curve


def centre(t):
    """The sign's centre line: a lemniscate of Gerono, x = cos t, y = sin t cos t."""
    x, y = math.cos(t), math.sin(t) * math.cos(t)
    return ADVANCE / 2 + HALF_WIDTH * x, MIDDLE + HALF_HEIGHT * 2 * y


def offsets(t0, t1, stroke):
    """One quarter of the curve as a closed strip: its left edge forward, its right edge back."""
    left, right = [], []
    for i in range(STEPS + 1):
        t = t0 + (t1 - t0) * i / STEPS
        x, y = centre(t)
        x1, y1 = centre(t + 1e-4)
        x0, y0 = centre(t - 1e-4)
        dx, dy = x1 - x0, y1 - y0
        n = math.hypot(dx, dy)
        nx, ny = -dy / n * stroke / 2, dx / n * stroke / 2
        left.append((x + nx, y + ny))
        right.append((x - nx, y - ny))
    return left + right[::-1]


def clockwise(points):
    area = sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(points, points[1:] + points[:1]))
    return points if area < 0 else points[::-1]


def glyph(stroke):
    """Four strips (centre to each loop end and back) that overlap where the curve crosses itself; TrueType fills the union."""
    pen = TTGlyphPen(None)
    quarter = math.pi / 2
    for k in range(4):
        points = clockwise(offsets(-quarter + k * quarter, k * quarter, stroke))
        pen.moveTo(tuple(round(v) for v in points[0]))
        for p in points[1:]:
            pen.lineTo(tuple(round(v) for v in p))
        pen.closePath()
    return pen.glyph()


def build(folder, name, weight, stroke):
    fb = FontBuilder(EM, isTTF=True)
    fb.setupGlyphOrder([".notdef", "infinity"])
    fb.setupCharacterMap({0x221E: "infinity"})
    fb.setupGlyf({".notdef": TTGlyphPen(None).glyph(), "infinity": glyph(stroke)})
    fb.setupHorizontalMetrics({".notdef": (500, 0), "infinity": (ADVANCE, 0)})
    fb.setupHorizontalHeader(ascent=ASCENT, descent=DESCENT)
    family = "ForgeUI Symbols"
    fb.setupNameTable({"familyName": family, "styleName": name, "uniqueFontIdentifier": family + " " + name,
                       "fullName": family + " " + name, "psName": "ForgeUISymbols-" + name, "version": "Version 1.000"})
    fb.setupOS2(usWeightClass=weight, sTypoAscender=ASCENT, sTypoDescender=DESCENT, usWinAscent=ASCENT, usWinDescent=-DESCENT,
                sCapHeight=643, sxHeight=510)
    fb.setupPost()
    fb.setupHead(unitsPerEm=EM)
    path = Path(folder) / ("ForgeUISymbols-" + name + ".ttf")
    fb.save(str(path))
    return path


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: make_symbols.py <output folder>")
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    for name, weight, stroke in WEIGHTS:
        print(build(out, name, weight, stroke))
