// Livery painting (PLAN §6 item 3): a finish and paint layers per material, composited in the material's UV space. One
// implementation serves the live 3D preview and the pack: `composeLivery` draws into the canvases it is given, at preview
// size or at the base texture's own size; `bakeLivery` writes the result as ordinary textures before pack.build, so the
// pack and game-file replacement carry plain Albedo / MetallicSmoothness / Emissive images and no livery data. An animated
// GIF decal is baked at its first frame, and its frames over the region it covers become a dynamic layer of the material.
import { baseTexturePath, isGif, loadImage, MAX_FRAMES, sourceFrames, writeLayerFrames, writePng, type Rect } from './frames';
import { dirName, type BlendMode, type DynamicLayer, type Finish, type LiveryKind, type LiveryLayer, type MaterialOverride, type MaterialParams, type Pattern, type SkinDocument, type WeaponPreview } from './protocol';
import { ensureMaterial, materialOf, uid, useStore } from './store';
import { t } from './i18n';
import { editColors } from './variants';
import { pickDecalImage } from './stickers';

type FinishPattern = 'brushed' | 'carbon' | 'worn';
/** MetallicSmoothness of each finish (as the game's gear shader reads it: metallic in R, smoothness in G) and the pattern it adds. */
export const FINISHES: Record<Finish, { metal: number; smooth: number; pattern?: FinishPattern; swatch: string }> = {
  matte: { metal: 0, smooth: 0.08, swatch: '#7c7f84' },
  gloss: { metal: 0, smooth: 0.88, swatch: '#c9ced6' },
  satin: { metal: 0, smooth: 0.45, swatch: '#a3a7ad' },
  metal: { metal: 1, smooth: 0.62, swatch: '#b8bcc2' },
  brushedMetal: { metal: 1, smooth: 0.5, pattern: 'brushed', swatch: '#a9adb3' },
  chrome: { metal: 1, smooth: 0.97, swatch: '#eef2f8' },
  rubber: { metal: 0, smooth: 0.04, swatch: '#3b3d40' },
  plastic: { metal: 0, smooth: 0.6, swatch: '#8e939a' },
  carbonFibre: { metal: 0, smooth: 0.78, pattern: 'carbon', swatch: '#26282b' },
  paintedSteel: { metal: 0.35, smooth: 0.55, swatch: '#8a9098' },
  wornMetal: { metal: 0.75, smooth: 0.4, pattern: 'worn', swatch: '#8f877c' },
  glow: { metal: 0, smooth: 0.5, swatch: '#ffd48a' },
};
export const FINISH_IDS = Object.keys(FINISHES) as Finish[];

/** Pattern presets: a pattern with colours that suit it (the add button uses the first). */
export type PatternPresetId = 'woodland' | 'woodlandDigital' | 'desertDigital' | 'snowDigital' | 'urbanDigital' | 'navyDigital' | 'desert' | 'tigerJungle' | 'tigerOrange' | 'splinter' | 'flecktarn' | 'multiTerrain' | 'hydroOcean' | 'hydroSunset' | 'anodisedRainbow' | 'anodisedPurple' | 'marbleWhite' | 'marbleGold' | 'damascus' | 'honeycombGold' | 'hexTech' | 'contour' | 'circuit' | 'zebra' | 'leopard' | 'dragonScales' | 'racingStripes' | 'plaidRed' | 'popDots' | 'rust' | 'hotRod' | 'starfield' | 'lava';
export const PATTERN_PRESETS: { id: PatternPresetId; pattern: Pattern; colors: number[][] }[] = [
  { id: 'woodland', pattern: 'woodland', colors: [[0.294, 0.325, 0.125, 1], [0.184, 0.165, 0.106, 1], [0.42, 0.49, 0.227, 1], [0.106, 0.106, 0.086, 1]] },
  { id: 'woodlandDigital', pattern: 'digital', colors: [[0.365, 0.42, 0.227, 1], [0.227, 0.267, 0.145, 1], [0.49, 0.478, 0.322, 1], [0.118, 0.133, 0.09, 1]] },
  { id: 'desertDigital', pattern: 'digital', colors: [[0.788, 0.698, 0.541, 1], [0.651, 0.545, 0.357, 1], [0.49, 0.416, 0.282, 1], [0.886, 0.827, 0.69, 1]] },
  { id: 'snowDigital', pattern: 'digital', colors: [[0.949, 0.957, 0.961, 1], [0.788, 0.808, 0.827, 1], [0.557, 0.588, 0.62, 1], [0.353, 0.38, 0.408, 1]] },
  { id: 'urbanDigital', pattern: 'digital', colors: [[0.541, 0.553, 0.569, 1], [0.361, 0.376, 0.392, 1], [0.2, 0.212, 0.227, 1], [0.71, 0.722, 0.733, 1]] },
  { id: 'navyDigital', pattern: 'digital', colors: [[0.122, 0.165, 0.267, 1], [0.184, 0.255, 0.4, 1], [0.29, 0.365, 0.51, 1], [0.071, 0.094, 0.149, 1]] },
  { id: 'desert', pattern: 'woodland', colors: [[0.824, 0.706, 0.549, 1], [0.627, 0.51, 0.353, 1], [0.784, 0.659, 0.455, 1], [0.431, 0.353, 0.235, 1]] },
  { id: 'tigerJungle', pattern: 'tiger', colors: [[0.42, 0.478, 0.227, 1], [0.114, 0.133, 0.078, 1], [0.247, 0.29, 0.141, 1], [0.114, 0.133, 0.078, 1]] },
  { id: 'tigerOrange', pattern: 'tiger', colors: [[0.878, 0.478, 0.122, 1], [0.067, 0.067, 0.067, 1], [0.949, 0.761, 0.482, 1], [0.067, 0.067, 0.067, 1]] },
  { id: 'splinter', pattern: 'splinter', colors: [[0.647, 0.604, 0.451, 1], [0.369, 0.42, 0.227, 1], [0.478, 0.353, 0.227, 1], [0.231, 0.29, 0.165, 1]] },
  { id: 'flecktarn', pattern: 'flecktarn', colors: [[0.42, 0.416, 0.271, 1], [0.18, 0.227, 0.133, 1], [0.31, 0.353, 0.196, 1], [0.227, 0.165, 0.118, 1]] },
  { id: 'multiTerrain', pattern: 'multiTerrain', colors: [[0.639, 0.604, 0.478, 1], [0.435, 0.416, 0.29, 1], [0.29, 0.29, 0.196, 1], [0.184, 0.165, 0.125, 1]] },
  { id: 'hydroOcean', pattern: 'hydro', colors: [[0.043, 0.239, 0.569, 1], [0.122, 0.71, 0.788, 1], [0.91, 0.969, 0.98, 1], [0.027, 0.188, 0.259, 1]] },
  { id: 'hydroSunset', pattern: 'hydro', colors: [[1.0, 0.42, 0.208, 1], [0.969, 0.773, 0.624, 1], [0.608, 0.173, 0.369, 1], [0.18, 0.102, 0.278, 1]] },
  { id: 'anodisedRainbow', pattern: 'anodised', colors: [[1.0, 0.231, 0.231, 1], [1.0, 0.824, 0.231, 1], [0.231, 1.0, 0.69, 1], [0.231, 0.42, 1.0, 1]] },
  { id: 'anodisedPurple', pattern: 'anodised', colors: [[0.357, 0.165, 0.525, 1], [0.616, 0.306, 0.867, 1], [0.235, 0.035, 0.424, 1], [0.78, 0.49, 1.0, 1]] },
  { id: 'marbleWhite', pattern: 'marble', colors: [[0.933, 0.933, 0.918, 1], [0.227, 0.227, 0.227, 1], [0.812, 0.812, 0.784, 1], [1.0, 1.0, 1.0, 1]] },
  { id: 'marbleGold', pattern: 'marble', colors: [[0.078, 0.078, 0.078, 1], [0.831, 0.686, 0.216, 1], [0.165, 0.165, 0.165, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'damascus', pattern: 'damascus', colors: [[0.29, 0.31, 0.337, 1], [0.788, 0.808, 0.831, 1], [0.0, 0.0, 0.0, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'honeycombGold', pattern: 'honeycomb', colors: [[0.831, 0.627, 0.09, 1], [0.722, 0.525, 0.043, 1], [0.902, 0.765, 0.361, 1], [0.227, 0.165, 0.02, 1]] },
  { id: 'hexTech', pattern: 'hexGrid', colors: [[0.102, 0.114, 0.133, 1], [0.133, 0.827, 0.933, 1], [0.0, 0.0, 0.0, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'contour', pattern: 'contour', colors: [[0.851, 0.812, 0.69, 1], [0.478, 0.416, 0.282, 1], [0.231, 0.184, 0.118, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'circuit', pattern: 'circuit', colors: [[0.059, 0.239, 0.18, 1], [0.831, 0.686, 0.216, 1], [0.898, 0.78, 0.42, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'zebra', pattern: 'zebra', colors: [[0.949, 0.949, 0.949, 1], [0.067, 0.067, 0.067, 1], [0.0, 0.0, 0.0, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'leopard', pattern: 'leopard', colors: [[0.851, 0.663, 0.357, 1], [0.541, 0.353, 0.169, 1], [0.11, 0.078, 0.051, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'dragonScales', pattern: 'scales', colors: [[0.122, 0.42, 0.227, 1], [0.247, 0.639, 0.365, 1], [0.047, 0.141, 0.086, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'racingStripes', pattern: 'stripes', colors: [[0.839, 0.157, 0.157, 1], [0.945, 0.98, 0.933, 1], [0.114, 0.208, 0.341, 1], [0.945, 0.98, 0.933, 1]] },
  { id: 'plaidRed', pattern: 'plaid', colors: [[0.643, 0.086, 0.102, 1], [0.086, 0.102, 0.114, 1], [0.729, 0.094, 0.106, 1], [0.898, 0.898, 0.898, 1]] },
  { id: 'popDots', pattern: 'dots', colors: [[1.0, 0.839, 0.039, 1], [0.816, 0.0, 0.0, 1], [0.0, 0.0, 0.0, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'rust', pattern: 'rust', colors: [[0.357, 0.357, 0.357, 1], [0.545, 0.227, 0.078, 1], [0.227, 0.114, 0.047, 1], [0.769, 0.416, 0.173, 1]] },
  { id: 'hotRod', pattern: 'flame', colors: [[0.082, 0.082, 0.082, 1], [1.0, 0.482, 0.0, 1], [1.0, 0.878, 0.4, 1], [0.816, 0.0, 0.0, 1]] },
  { id: 'starfield', pattern: 'starfield', colors: [[0.043, 0.051, 0.102, 1], [1.0, 1.0, 1.0, 1], [0.353, 0.094, 0.604, 1], [0.0, 0.0, 0.0, 1]] },
  { id: 'lava', pattern: 'crackle', colors: [[0.102, 0.102, 0.102, 1], [1.0, 0.302, 0.0, 1], [0.227, 0.102, 0.063, 1], [0.0, 0.0, 0.0, 1]] },
];


const COMPOSITE: Record<BlendMode, GlobalCompositeOperation> = {
  normal: 'source-over',
  multiply: 'multiply',
  screen: 'screen',
  overlay: 'overlay',
  add: 'lighter',
};
export const BLENDS = Object.keys(COMPOSITE) as BlendMode[];

export const layersOf = (m: MaterialOverride | undefined): LiveryLayer[] => (m?.livery ?? []).filter((l): l is LiveryLayer => !!l);
export const hasLivery = (m: MaterialOverride | undefined) => !!m && (!!m.finish || layersOf(m).some((l) => l.visible !== false));

/** Parameters a finish needs on top of the game's (the user's own values win): glow lights the paint itself. */
export function liveryParams(m: MaterialOverride | undefined): Partial<MaterialParams> {
  return m?.finish === 'glow' ? { emissiveEnabled: true, emissiveColor: [1, 1, 1], emissiveIntensity: 1 } : {};
}

/**
 * Renders a layer that needs the model in UV space (viewport/liveryGpu): a decal's image (transparent where it does not
 * land; `image` is its current frame), a pattern (opaque on the material), a wear mask (alpha) or a brush layer's island
 * fills (white; `image` null for the last three).
 */
export type UvRenderer = (layer: LiveryLayer, image: CanvasImageSource | null, w: number, h: number) => CanvasImageSource;

/** A material whose livery writes MetallicSmoothness: a finish, or wear (bare metal where worn). */
export const writesMs = (m: MaterialOverride) => !!m.finish || layersOf(m).some((l) => l.kind === 'wear' && l.visible !== false);

/** Which frame an animated decal (`count` frames at `fps`) shows; a still decal has one frame. */
export type FrameOf = (layer: LiveryLayer, count: number, fps: number) => number;

/** A decal whose image is a GIF: animated when it has more than one frame. */
export const isGifDecal = (l: LiveryLayer) => l.kind === 'decal' && !!l.image && isGif(l.image);

/** Texels a decal is grown into the empty gutter around UV islands, so filtering at island edges shows no seam. */
const GUTTER = 2;

/** Layer fields that do not change the rendered texture, so they stay out of the cache key. */
const NOT_SHAPE = ['name', 'visible', 'opacity', 'blend', 'strokes', 'mask'];

/**
 * `render` with its last output per layer kept while the layer, the image (a GIF frame), the size and `extra` stay the
 * same; `cache` outlives the returned renderer.
 */
export function cachedUv(render: UvRenderer, cache: Map<string, { key: string; image: CanvasImageSource | null; canvas: HTMLCanvasElement }>, extra = ''): UvRenderer {
  return (l, image, w, h) => {
    const shape: Record<string, unknown> = { ...l };
    for (const k of NOT_SHAPE) delete shape[k];
    const key = JSON.stringify([shape, w, h, extra]);
    const hit = cache.get(l.id!);
    if (hit?.key === key && hit.image === image) return hit.canvas;
    const c = hit?.canvas ?? document.createElement('canvas');
    [c.width, c.height] = [w, h];
    c.getContext('2d')!.drawImage(render(l, image, w, h), 0, 0);
    cache.set(l.id!, { key, image, canvas: c });
    return c;
  };
}

const rgba = (c: number[] | null | undefined) => {
  const [r, g, b, a] = c ?? [1, 1, 1, 1];
  return `rgba(${Math.round(r * 255)},${Math.round(g * 255)},${Math.round(b * 255)},${a ?? 1})`;
};

function canvas(w: number, h: number, c = document.createElement('canvas')) {
  if (c.width !== w || c.height !== h) [c.width, c.height] = [w, h];
  return c;
}

/** Strokes as UV polylines of (u, v, width) triples; widths are UV units of the texture's mean side. */
function drawStrokes(ctx: CanvasRenderingContext2D, strokes: (number[] | null)[], w: number, h: number) {
  const k = Math.sqrt(w * h);
  ctx.lineCap = ctx.lineJoin = 'round';
  for (const s of strokes) {
    if (!s || s.length < 3) continue;
    if (s.length === 3) {
      ctx.beginPath();
      ctx.arc(s[0] * w, s[1] * h, (s[2] * k) / 2, 0, Math.PI * 2);
      ctx.fill();
      continue;
    }
    for (let i = 0; i + 5 < s.length; i += 3) {
      ctx.lineWidth = ((s[i + 2] + s[i + 5]) / 2) * k;
      ctx.beginPath();
      ctx.moveTo(s[i] * w, s[i + 1] * h);
      ctx.lineTo(s[i + 3] * w, s[i + 4] * h);
      ctx.stroke();
    }
  }
}

// Deterministic noise, so the preview and every bake of a finish look the same: random cells stretched with smoothing.
function noise(w: number, h: number, cellsX: number, cellsY: number, seed: number): Uint8ClampedArray {
  const small = canvas(cellsX, cellsY);
  const sctx = small.getContext('2d')!;
  const img = sctx.createImageData(cellsX, cellsY);
  let x = seed;
  for (let i = 0; i < img.data.length; i += 4) {
    x = (x * 1664525 + 1013904223) >>> 0;
    img.data[i] = img.data[i + 1] = img.data[i + 2] = x >>> 24;
    img.data[i + 3] = 255;
  }
  sctx.putImageData(img, 0, 0);
  const big = canvas(w, h).getContext('2d', { willReadFrequently: true })!;
  big.imageSmoothingQuality = 'high';
  big.drawImage(small, 0, 0, w, h);
  return big.getImageData(0, 0, w, h).data;
}

/** Twill weave: 0..1 shade across each tow (tows alternate between running along u and along v). */
function weave(x: number, y: number, cell: number): number {
  const along = (Math.floor(x / cell) + Math.floor(y / cell)) % 4 < 2;
  return Math.sin(Math.PI * (((along ? y : x) % cell) / cell));
}

const finishCache = new Map<string, { ms: ImageData; look: ImageData | null }>();

/** A finish's MetallicSmoothness (the game's layout) at a size, and the look it puts under the paint (carbon weave). Cached. */
function finishPixels(finish: Finish, w: number, h: number) {
  const key = `${finish}|${w}x${h}`;
  const hit = finishCache.get(key);
  if (hit) return hit;
  const f = FINISHES[finish];
  const ms = new ImageData(w, h);
  const look = f.pattern === 'carbon' ? new ImageData(w, h) : null;
  const n = f.pattern === 'brushed' ? noise(w, h, 1, 512, 7) : f.pattern === 'worn' ? noise(w, h, 24, 24, 11) : null;
  const grain = f.pattern === 'worn' ? noise(w, h, 160, 160, 3) : null;
  const cell = Math.max(4, Math.round(Math.max(w, h) / 64));
  for (let y = 0, i = 0; y < h; y++)
    for (let x = 0; x < w; x++, i += 4) {
      let metal = f.metal;
      let smooth = f.smooth;
      if (f.pattern === 'brushed') smooth += (n![i] / 255 - 0.5) * 0.3;
      else if (f.pattern === 'worn') {
        // Bare metal where the low-frequency noise is high, scratched paint elsewhere.
        const bare = Math.min(1, Math.max(0, (n![i] / 255 - 0.55) * 8));
        metal = 0.15 + 0.85 * bare;
        smooth = 0.3 + 0.35 * bare + (grain![i] / 255 - 0.5) * 0.15;
      } else if (look) {
        const s = weave(x, y, cell);
        smooth = 0.6 + 0.3 * s;
        look.data[i] = look.data[i + 1] = look.data[i + 2] = 22 + 48 * s;
        look.data[i + 3] = 255;
      }
      ms.data[i] = Math.round(Math.min(1, Math.max(0, metal)) * 255);
      ms.data[i + 1] = Math.round(Math.min(1, Math.max(0, smooth)) * 255);
      ms.data[i + 3] = 255;
    }
  // A bake-size finish is tens of MB: keep only the last few (the preview's and the one being baked).
  if (finishCache.size >= 3) finishCache.delete(finishCache.keys().next().value!);
  const out = { ms, look };
  finishCache.set(key, out);
  return out;
}

const scratch = { layer: document.createElement('canvas') };

/**
 * Paints `m`'s livery: `albedo` gets the base texture, the finish's look and every visible layer bottom first (each with
 * its mask, opacity and blend); `ms` (when given and the material has a finish) the finish's MetallicSmoothness. Both
 * canvases must have the same size, which sets the bake resolution. An animated decal shows the frame `frameOf` picks.
 */
export async function composeLivery(
  m: MaterialOverride,
  base: CanvasImageSource | null,
  albedo: HTMLCanvasElement,
  ms: HTMLCanvasElement | null,
  uv: UvRenderer,
  frameOf: FrameOf = () => 0,
  baseMs: CanvasImageSource | null = null,
) {
  const { width: w, height: h } = albedo;
  const layers = layersOf(m).filter((l) => l.visible !== false);
  const images = await Promise.all(
    layers.map(async (l) => {
      if (l.kind !== 'decal' || !l.image) return null;
      const s = await sourceFrames(l.image).catch(() => null);
      if (!s) return null;
      const n = s.frames.length;
      return s.frames[n > 1 ? Math.min(n - 1, Math.max(0, frameOf(l, n, l.fps || s.fps))) : 0];
    }),
  );
  const ctx = albedo.getContext('2d', { willReadFrequently: true })!;
  ctx.globalCompositeOperation = 'source-over';
  ctx.globalAlpha = 1;
  ctx.fillStyle = '#808080';
  ctx.fillRect(0, 0, w, h);
  if (base) ctx.drawImage(base, 0, 0, w, h);
  const msCtx = ms?.getContext('2d', { willReadFrequently: true }) ?? null;
  if (m.finish) {
    const f = finishPixels(m.finish, w, h);
    if (f.look) ctx.putImageData(f.look, 0, 0);
    msCtx?.putImageData(f.ms, 0, 0);
  } else if (msCtx) {
    // Wear without a finish: the game's own metallic and smoothness, else plain paint.
    msCtx.globalCompositeOperation = 'copy';
    if (baseMs) msCtx.drawImage(baseMs, 0, 0, w, h);
    else {
      msCtx.fillStyle = 'rgb(0,89,0)';
      msCtx.fillRect(0, 0, w, h);
    }
    msCtx.globalCompositeOperation = 'source-over';
  }
  const lc = canvas(w, h, scratch.layer);
  const l2 = lc.getContext('2d')!;
  layers.forEach((l, i) => {
    l2.globalCompositeOperation = 'source-over';
    l2.clearRect(0, 0, w, h);
    l2.fillStyle = l2.strokeStyle = rgba(l.color);
    if (l.kind === 'fill') l2.fillRect(0, 0, w, h);
    else if (l.kind === 'brush') {
      if (l.fills?.length) {
        l2.drawImage(uv(l, null, w, h), 0, 0);
        l2.globalCompositeOperation = 'source-in';
        l2.fillRect(0, 0, w, h);
        l2.globalCompositeOperation = 'source-over';
      }
      drawStrokes(l2, l.strokes ?? [], w, h);
    }
    else if (l.kind === 'wear') wearThrough(l, uv(l, null, w, h), base, l2, msCtx, w, h);
    else if (l.kind === 'pattern' || images[i]) {
      const src = uv(l, images[i], w, h);
      l2.drawImage(src, 0, 0);
      l2.globalCompositeOperation = 'destination-over';
      for (let r = 1; r <= GUTTER; r++) for (const [dx, dy] of [[r, 0], [-r, 0], [0, r], [0, -r]]) l2.drawImage(src, dx, dy);
    }
    if (l.mask?.length || l.maskFills?.length) {
      l2.globalCompositeOperation = 'destination-out';
      l2.fillStyle = l2.strokeStyle = '#000';
      drawStrokes(l2, l.mask ?? [], w, h);
      if (l.maskFills?.length) l2.drawImage(uv({ ...l, id: `${l.id}:mask`, kind: 'brush', fills: l.maskFills }, null, w, h), 0, 0);
    }
    ctx.globalAlpha = Math.min(1, Math.max(0, l.opacity ?? 1));
    ctx.globalCompositeOperation = COMPOSITE[l.blend ?? 'normal'];
    ctx.drawImage(lc, 0, 0);
  });
  ctx.globalAlpha = 1;
  ctx.globalCompositeOperation = 'source-over';
}

/**
 * Wear: into `l2`, the game's own texture (else plain metal) tinted by the layer colour where `mask` shows, so the layers
 * below are worn through to it; `ms` turns towards the layer's metallic and smoothness by the same mask.
 */
function wearThrough(l: LiveryLayer, mask: CanvasImageSource, base: CanvasImageSource | null, l2: CanvasRenderingContext2D, ms: CanvasRenderingContext2D | null, w: number, h: number) {
  if (base) l2.drawImage(base, 0, 0, w, h);
  else l2.fillRect(0, 0, w, h);
  l2.globalCompositeOperation = 'multiply';
  l2.fillStyle = rgba([...(l.color ?? [1, 1, 1]).slice(0, 3), 1]);
  l2.fillRect(0, 0, w, h);
  l2.globalCompositeOperation = 'destination-in';
  l2.drawImage(mask, 0, 0);
  if (!ms) return;
  const a = l2.getImageData(0, 0, w, h).data;
  const img = ms.getImageData(0, 0, w, h);
  const p = img.data;
  const k = Math.min(1, Math.max(0, l.opacity ?? 1)) / 255;
  const [metal, smooth] = [(l.metallic ?? 0.85) * 255, (l.smoothness ?? 0.55) * 255];
  for (let i = 0; i < p.length; i += 4) {
    const t = a[i + 3] * k;
    if (!t) continue;
    p[i] = p[i] + (metal - p[i]) * t;
    p[i + 1] = p[i + 1] + (smooth - p[i + 1]) * t;
  }
  ms.putImageData(img, 0, 0);
}

/** The texels a decal render covers (any alpha), grown by the gutter and kept inside the texture; null when none. */
function covered(src: CanvasImageSource, w: number, h: number): Rect | null {
  const ctx = canvas(w, h, scratch.layer).getContext('2d', { willReadFrequently: true })!;
  ctx.globalCompositeOperation = 'copy';
  ctx.drawImage(src, 0, 0);
  ctx.globalCompositeOperation = 'source-over';
  const px = ctx.getImageData(0, 0, w, h).data;
  let [x0, y0, x1, y1] = [w, h, -1, -1];
  for (let y = 0, i = 3; y < h; y++)
    for (let x = 0; x < w; x++, i += 4)
      if (px[i]) {
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        y1 = y;
      }
  if (x1 < 0) return null;
  [x0, y0] = [Math.max(0, x0 - GUTTER), Math.max(0, y0 - GUTTER)];
  return { x: x0, y: y0, w: Math.min(w, x1 + 1 + GUTTER) - x0, h: Math.min(h, y1 + 1 + GUTTER) - y0 };
}

/**
 * Bakes the animated GIF decals of `m` (its livery already composed into `albedo` with every decal at its first frame):
 * per decal, the region its projection covers, and for every frame the whole livery with that decal at that frame, cut
 * to the region. Frames are copied in game without blending, so the paint above and below the decal is in each of them.
 * Returns the dynamic layers that play them.
 */
async function bakeAnimatedDecals(m: MaterialOverride, base: CanvasImageSource | null, albedo: HTMLCanvasElement, render: UvRenderer, projectPath: string, onProgress: (text: string) => void) {
  const { width: w, height: h } = albedo;
  const decal = cachedUv(render, new Map()); // the other layers are rendered once for all frames
  const out: DynamicLayer[] = [];
  for (const l of layersOf(m)) {
    if (l.visible === false || !isGifDecal(l)) continue;
    const s = await sourceFrames(l.image!).catch(() => null);
    if (!s || s.frames.length < 2) continue;
    // Where the decal lands whatever its frames hold: the projection of an opaque image.
    const rect = covered(render(l, OPAQUE, w, h), w, h);
    if (!rect) continue;
    // Frames at the region's full size; pack.build stores them smaller where they do not fit one atlas.
    const count = Math.min(MAX_FRAMES, s.frames.length);
    onProgress(t('livery.bakingFrames', { v0: l.name, v1: count }));
    await writeLayerFrames(projectPath, l.id!, rect, w, h, async (frame) => {
      for (let i = 0; i < count; i++) {
        await composeLivery(m, base, albedo, null, decal, (x) => (x.id === l.id ? i : 0));
        await frame((ctx) => ctx.drawImage(albedo, 0, 0));
      }
    });
    const fps = l.fps || s.fps;
    out.push({ id: l.id, name: l.name, kind: 'sequence', channel: 'albedo', visible: true, sources: [], fps, trigger: l.trigger ?? 'always', play: l.play ?? 'loop', playSeconds: l.playSeconds || 3, lowAmmoFraction: l.lowAmmoFraction ?? 0.2, opacity: 1 });
  }
  return out;
}

const OPAQUE = Object.assign(document.createElement('canvas'), { width: 1, height: 1 });
OPAQUE.getContext('2d')!.fillRect(0, 0, 1, 1);

/**
 * Bakes every livery of `doc` (the packed copy) into its textures at the base albedo's size and drops the livery data:
 * Albedo, plus MetallicSmoothness for a finish and Emissive (the paint itself) for glow. Runs before the dynamic layers
 * are baked, so they play over the paint. Returns, per material, the dynamic layers of its animated decals, whose frames
 * it wrote; the caller adds them once the material's own layers are baked.
 */
export async function bakeLivery(
  doc: SkinDocument,
  preview: WeaponPreview | null,
  projectPath: string,
  uvFor: (m: MaterialOverride) => UvRenderer,
  onProgress: (text: string) => void,
): Promise<{ m: MaterialOverride; layers: DynamicLayer[] }[]> {
  const animated: { m: MaterialOverride; layers: DynamicLayer[] }[] = [];
  for (const m of doc.materials ?? []) {
    if (!m) continue;
    if (hasLivery(m)) {
      onProgress(t('livery.baking', { v0: m.material }));
      const basePath = baseTexturePath(doc, preview, m.material!, 'albedo');
      const base = basePath ? await loadImage(basePath) : null;
      const [w, h] = base ? [base.naturalWidth, base.naturalHeight] : [1024, 1024];
      const msPath = writesMs(m) ? baseTexturePath(doc, preview, m.material!, 'metallicSmoothness') : null;
      const baseMs = msPath ? await loadImage(msPath).catch(() => null) : null;
      const albedo = canvas(w, h);
      const ms = writesMs(m) ? canvas(w, h) : null;
      const render = uvFor(m);
      const dir = `${dirName(projectPath)}\\.frames\\livery`;
      const safe = (m.material ?? '').replace(/[^\w.-]/g, '_');
      // Wear that grows with kills: the livery at none, a third, two thirds and all of each such layer's amount.
      const growing = layersOf(m).filter((l) => l.kind === 'wear' && l.visible !== false && (l.killsToFull ?? 0) > 0);
      if (growing.length) {
        const full = Math.max(...growing.map((l) => l.killsToFull!));
        const amounts = new Map(growing.map((l) => [l, l.amount ?? 0.4]));
        m.wear = [];
        for (const [i, k] of [0, 1 / 3, 2 / 3, 1].entries()) {
          onProgress(t('livery.bakingWear', { v0: m.material, v1: i + 1 }));
          growing.forEach((l) => (l.amount = amounts.get(l)! * k));
          await composeLivery(m, base, albedo, ms, render, undefined, baseMs);
          const textures: Record<string, string> = { albedo: `${dir}\\${safe}_wear${i}_albedo.png` };
          await writePng(textures.albedo, albedo);
          if (ms) await writePng((textures.metallicSmoothness = `${dir}\\${safe}_wear${i}_ms.png`), ms);
          m.wear.push({ kills: Math.round(full * k), textures });
        }
        growing.forEach((l) => (l.amount = amounts.get(l)!));
      }
      await composeLivery(m, base, albedo, ms, render, undefined, baseMs);
      m.textures ??= {};
      m.textures.albedo = `${dir}\\${safe}_albedo.png`;
      await writePng(m.textures.albedo, albedo);
      if (ms) {
        m.textures.metallicSmoothness = `${dir}\\${safe}_ms.png`;
        await writePng(m.textures.metallicSmoothness, ms);
      }
      if (m.finish === 'glow') m.textures.emissive = m.textures.albedo;
      const own = Object.fromEntries(Object.entries(m.params ?? {}).filter(([, v]) => v != null));
      m.params = { ...liveryParams(m), ...own };
      const layers = await bakeAnimatedDecals(m, base, albedo, render, projectPath, onProgress);
      if (layers.length) animated.push({ m, layers });
    }
    m.finish = null;
    m.livery = null;
  }
  return animated;
}

// Document edits. Layers are stored bottom first; the tree and panels list them top first.

const NAMES: Record<LiveryKind, () => string> = {
  fill: () => t('livery.fill'),
  decal: () => t('livery.decal'),
  brush: () => t('livery.brush'),
  pattern: () => t('livery.pattern'),
  wear: () => t('livery.wear'),
};

export function addLivery(material: string, part: string, kind: LiveryKind, image?: string, aspect = 1) {
  const s = useStore.getState();
  const id = uid();
  s.edit((d) => {
    const m = ensureMaterial(d, material, part);
    m.livery ??= [];
    m.livery.push({
      id,
      name: NAMES[kind](),
      kind,
      visible: true,
      color: kind === 'fill' ? [0.75, 0.2, 0.15, 1] : [1, 1, 1, 1],
      opacity: 1,
      blend: 'normal',
      image: image ?? null,
      angle: 0,
      size: [0.04, 0.04 * aspect],
      depth: 0.01,
      mirror: false,
      strokes: [],
      mask: [],
      ...(kind === 'pattern' && { pattern: PATTERN_PRESETS[0].pattern, colors: PATTERN_PRESETS[0].colors, scale: 0.05, seed: Math.floor(Math.random() * 1000) }),
      ...(kind === 'wear' && { color: [0.78, 0.78, 0.8, 1], amount: 0.4, edgeWeight: 0.7, scratches: 0.3, scale: 0.03, metallic: 0.85, smoothness: 0.55, seed: Math.floor(Math.random() * 1000) }),
    });
  });
  s.select({ kind: 'livery', material, id });
}

export async function addDecal(material: string, part: string) {
  const file = await pickDecalImage();
  if (!file) return;
  const img = await loadImage(file).catch(() => null);
  if (!img) return useStore.getState().toast('error', t('livery.badImage'));
  addLivery(material, part, 'decal', file, img.naturalHeight / Math.max(1, img.naturalWidth));
}

export function liveryLayer(doc: SkinDocument, material: string, id: string): LiveryLayer | undefined {
  return layersOf(materialOf(doc, material)).find((l) => l.id === id);
}

export function updateLivery(material: string, id: string, fn: (l: LiveryLayer) => void, coalesceKey?: string) {
  useStore.getState().edit((d) => {
    const l = liveryLayer(d, material, id);
    if (l) editColors(d, l, fn);
  }, coalesceKey && (coalesceKey.startsWith('gesture:') ? coalesceKey : `${id}-${coalesceKey}`));
}

export function removeLivery(material: string, id: string) {
  const s = useStore.getState();
  s.edit((d) => {
    const m = materialOf(d, material);
    if (m) m.livery = layersOf(m).filter((l) => l.id !== id);
  });
  s.select({ kind: 'part', id: materialOf(s.doc, material)?.part ?? '' });
}

/** Moves a layer one step up (towards the top, +1) or down. */
export function moveLivery(material: string, id: string, by: 1 | -1) {
  useStore.getState().edit((d) => {
    const m = materialOf(d, material);
    const list = layersOf(m);
    const i = list.findIndex((l) => l.id === id);
    const j = i + by;
    if (!m || i < 0 || j < 0 || j >= list.length) return;
    [list[i], list[j]] = [list[j], list[i]];
    m.livery = list;
  });
}

export function setFinish(material: string, part: string, finish: Finish | null) {
  useStore.getState().edit((d) => void (ensureMaterial(d, material, part).finish = finish));
}
