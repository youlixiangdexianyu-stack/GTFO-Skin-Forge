// Livery painting (PLAN §6 item 3): a finish and paint layers per material, composited in the material's UV space. One
// implementation serves the live 3D preview and the pack: `composeLivery` draws into the canvases it is given, at preview
// size or at the base texture's own size; `bakeLivery` writes the result as ordinary textures before pack.build, so the
// pack and game-file replacement carry plain Albedo / MetallicSmoothness / Emissive images and no livery data. An animated
// GIF decal is baked at its first frame, and its frames over the region it covers become a dynamic layer of the material.
import { baseTexturePath, isGif, loadImage, MAX_ATLAS, MAX_FRAMES, sourceFrames, writeLayerFrames, writePng, type Rect } from './frames';
import { dirName, type BlendMode, type DynamicLayer, type Finish, type LiveryKind, type LiveryLayer, type MaterialOverride, type MaterialParams, type SkinDocument, type WeaponPreview } from './protocol';
import { ensureMaterial, materialOf, uid, useStore } from './store';
import { t } from './i18n';

type Pattern = 'brushed' | 'carbon' | 'worn';
/** Unity MetallicSmoothness of each finish (metallic in R, smoothness in A) and the pattern it adds. */
export const FINISHES: Record<Finish, { metal: number; smooth: number; pattern?: Pattern; swatch: string }> = {
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

/** Renders a decal layer in UV space (viewport/liveryGpu): transparent where it does not land. */
export type DecalRenderer = (layer: LiveryLayer, image: CanvasImageSource, w: number, h: number) => CanvasImageSource;

/** Which frame an animated decal (`count` frames at `fps`) shows; a still decal has one frame. */
export type FrameOf = (layer: LiveryLayer, count: number, fps: number) => number;

/** A decal whose image is a GIF: animated when it has more than one frame. */
export const isGifDecal = (l: LiveryLayer) => l.kind === 'decal' && !!l.image && isGif(l.image);

/** Texels a decal is grown into the empty gutter around UV islands, so filtering at island edges shows no seam. */
const GUTTER = 2;

/**
 * `render` with its last output per layer kept while the layer, the image (a GIF frame), the size and `extra` stay the
 * same; `cache` outlives the returned renderer.
 */
export function cachedDecal(render: DecalRenderer, cache: Map<string, { key: string; image: CanvasImageSource; canvas: HTMLCanvasElement }>, extra = ''): DecalRenderer {
  return (l, image, w, h) => {
    const key = JSON.stringify([l.image, l.position, l.rotation, l.angle, l.size, l.depth, l.mirror, l.color, w, h, extra]);
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

/** A finish's MetallicSmoothness (Unity layout) at a size, and the look it puts under the paint (carbon weave). Cached. */
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
      ms.data[i] = ms.data[i + 1] = ms.data[i + 2] = Math.round(Math.min(1, Math.max(0, metal)) * 255);
      ms.data[i + 3] = Math.round(Math.min(1, Math.max(0, smooth)) * 255);
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
  decal: DecalRenderer,
  frameOf: FrameOf = () => 0,
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
  if (m.finish) {
    const f = finishPixels(m.finish, w, h);
    if (f.look) ctx.putImageData(f.look, 0, 0);
    ms?.getContext('2d', { willReadFrequently: true })!.putImageData(f.ms, 0, 0);
  }
  const lc = canvas(w, h, scratch.layer);
  const l2 = lc.getContext('2d')!;
  layers.forEach((l, i) => {
    l2.globalCompositeOperation = 'source-over';
    l2.clearRect(0, 0, w, h);
    l2.fillStyle = l2.strokeStyle = rgba(l.color);
    if (l.kind === 'fill') l2.fillRect(0, 0, w, h);
    else if (l.kind === 'brush') drawStrokes(l2, l.strokes ?? [], w, h);
    else if (images[i]) {
      const src = decal(l, images[i]!, w, h);
      l2.drawImage(src, 0, 0);
      l2.globalCompositeOperation = 'destination-over';
      for (let r = 1; r <= GUTTER; r++) for (const [dx, dy] of [[r, 0], [-r, 0], [0, r], [0, -r]]) l2.drawImage(src, dx, dy);
    }
    if (l.mask?.length) {
      l2.globalCompositeOperation = 'destination-out';
      l2.fillStyle = l2.strokeStyle = '#000';
      drawStrokes(l2, l.mask, w, h);
    }
    ctx.globalAlpha = Math.min(1, Math.max(0, l.opacity ?? 1));
    ctx.globalCompositeOperation = COMPOSITE[l.blend ?? 'normal'];
    ctx.drawImage(lc, 0, 0);
  });
  ctx.globalAlpha = 1;
  ctx.globalCompositeOperation = 'source-over';
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
async function bakeAnimatedDecals(m: MaterialOverride, base: CanvasImageSource | null, albedo: HTMLCanvasElement, render: DecalRenderer, projectPath: string, onProgress: (text: string) => void) {
  const { width: w, height: h } = albedo;
  const decal = cachedDecal(render, new Map()); // the other decals are rendered once for all frames
  const out: DynamicLayer[] = [];
  for (const l of layersOf(m)) {
    if (l.visible === false || !isGifDecal(l)) continue;
    const s = await sourceFrames(l.image!).catch(() => null);
    if (!s || s.frames.length < 2) continue;
    // Where the decal lands whatever its frames hold: the projection of an opaque image.
    const rect = covered(render(l, OPAQUE, w, h), w, h);
    if (!rect) continue;
    const count = Math.min(MAX_FRAMES, s.frames.length);
    const columns = Math.floor(MAX_ATLAS / rect.w);
    if (!columns || Math.ceil(count / columns) * rect.h > MAX_ATLAS)
      throw Object.assign(
        new Error(`${m.material}/${l.name}: the animated decal covers ${rect.w}x${rect.h} texels; ${count} frames do not fit a ${MAX_ATLAS}x${MAX_ATLAS} atlas. Make the decal smaller or use a GIF with fewer frames.`),
        { code: 'bad-atlas' },
      );
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
  decalFor: (m: MaterialOverride) => DecalRenderer,
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
      const albedo = canvas(w, h);
      const ms = m.finish ? canvas(w, h) : null;
      const render = decalFor(m);
      await composeLivery(m, base, albedo, ms, render);
      const dir = `${dirName(projectPath)}\\.frames\\livery`;
      const safe = (m.material ?? '').replace(/[^\w.-]/g, '_');
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
    });
  });
  s.select({ kind: 'livery', material, id });
}

export async function addDecal(material: string, part: string) {
  const [file] = await window.forge.openDialog('files', [{ name: t('livery.decal'), extensions: ['png', 'jpg', 'jpeg', 'webp', 'bmp', 'gif'] }]);
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
    if (l) fn(l);
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
