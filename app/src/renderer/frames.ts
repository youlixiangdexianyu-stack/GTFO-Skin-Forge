// Renders dynamic-layer frames and screen glyph strips in the renderer and writes them as PNGs for pack.build.
// Contract with the backend/plugin: each frame in <projectDir>/.frames/<layerId>/NNNN.png is the final pixels of the
// target region (base texture + layer, opacity baked; the plugin copies without blending); rect.json gives the region in
// base-texture pixels with a top-left origin. The glyph strip is "0123456789 -" in 12 equal cells, white on transparent;
// a static screen's text is one image <attachmentId>-text.png. The backend traces both into meshes.
import { cssFont, loadFont } from './fonts';
import { dirName, fileUrl, type CatalogItem, type DynamicLayer, type Screen, type SkinDocument, type WeaponPreview } from './protocol';
import { t } from './i18n';

export const GLYPHS = '0123456789 -';
const MAX_FRAMES = 240;
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

export const naturalSort = (paths: string[]) => [...paths].sort(collator.compare);

const images = new Map<string, Promise<HTMLImageElement>>();
export function loadImage(path: string): Promise<HTMLImageElement> {
  let p = images.get(path);
  if (!p) {
    p = new Promise((resolve, reject) => {
      const img = new Image();
      img.crossOrigin = 'anonymous';
      img.onload = () => resolve(img);
      img.onerror = () => reject(new Error(`cannot load ${path}`));
      img.src = fileUrl(path);
    });
    images.set(path, p);
  }
  return p;
}

export function textCanvas(layer: DynamicLayer): HTMLCanvasElement {
  const t = layer.text ?? { content: '' };
  const size = t.sizePx ?? 64;
  const font = `600 ${size}px ${t.font ? `"${t.font}", ` : ''}"Microsoft YaHei UI", sans-serif`;
  const c = document.createElement('canvas');
  const ctx = c.getContext('2d')!;
  ctx.font = font;
  const lines = (t.content || ' ').split('\n');
  const w = Math.max(...lines.map((l) => ctx.measureText(l).width), 1);
  c.width = Math.ceil(w + size * 0.2);
  c.height = Math.ceil(lines.length * size * 1.25);
  ctx.font = font;
  const [r, g, b, a] = t.color ?? [1, 1, 1, 1];
  ctx.fillStyle = `rgba(${r * 255},${g * 255},${b * 255},${a})`;
  ctx.textBaseline = 'top';
  lines.forEach((l, i) => ctx.fillText(l, size * 0.1, i * size * 1.25 + size * 0.1));
  return c;
}

/** Live source for preview: returns the drawable for a time in seconds. */
export class LayerSource {
  private frames: CanvasImageSource[] = [];
  private video: HTMLVideoElement | null = null;
  ready: Promise<void>;

  constructor(private layer: DynamicLayer) {
    this.ready = this.load();
  }

  private async load() {
    const l = this.layer;
    const sources = (l.sources ?? []).filter(Boolean) as string[];
    if (l.kind === 'text') this.frames = [textCanvas(l)];
    else if (l.kind === 'video' && sources[0]) {
      const v = document.createElement('video');
      v.src = fileUrl(sources[0]);
      v.muted = true;
      v.loop = true;
      v.crossOrigin = 'anonymous';
      v.playsInline = true;
      await v.play().catch(() => undefined);
      this.video = v;
    } else if (l.kind === 'sequence') this.frames = await Promise.all(naturalSort(sources).map(loadImage));
    else if (sources[0]) this.frames = [await loadImage(sources[0])];
  }

  /** Length of one pass in seconds; a still image counts as one second. */
  get duration(): number {
    if (this.video) return this.video.duration || 1;
    return this.frames.length > 1 ? this.frames.length / (this.layer.fps || 12) : 1;
  }

  /** Frame at a playback time within one pass (see `playTime`). */
  at(time: number): CanvasImageSource | null {
    if (this.video) {
      if (Math.abs(this.video.currentTime - time) > 0.25) this.video.currentTime = time;
      return this.video.readyState >= 2 ? this.video : null;
    }
    if (!this.frames.length) return null;
    return this.frames[Math.min(Math.floor(time * (this.layer.fps || 12)), this.frames.length - 1)];
  }

  dispose() {
    this.video?.pause();
    this.video?.removeAttribute('src');
  }
}

export function sourceSize(src: CanvasImageSource): [number, number] {
  if (src instanceof HTMLVideoElement) return [src.videoWidth, src.videoHeight];
  if (src instanceof HTMLImageElement) return [src.naturalWidth, src.naturalHeight];
  const s = src as HTMLCanvasElement;
  return [s.width, s.height];
}

/** Layer quad on the texture in image pixels (top-left origin; UV v points up). */
export function layerQuad(l: DynamicLayer, w: number, h: number) {
  const [u, v] = l.uvCenter ?? [0.5, 0.5];
  const [su, sv] = l.uvSize ?? [0.25, 0.25];
  return { cx: u * w, cy: (1 - v) * h, w: su * w, h: sv * h, rot: ((l.rotation ?? 0) * Math.PI) / 180 };
}

/** Axis-aligned bounding box of the rotated layer quad, clamped to the texture. */
export function layerRect(l: DynamicLayer, w: number, h: number) {
  const q = layerQuad(l, w, h);
  const c = Math.abs(Math.cos(q.rot));
  const s = Math.abs(Math.sin(q.rot));
  const bw = q.w * c + q.h * s;
  const bh = q.w * s + q.h * c;
  const x = Math.max(0, Math.floor(q.cx - bw / 2));
  const y = Math.max(0, Math.floor(q.cy - bh / 2));
  return { x, y, w: Math.min(w, Math.ceil(q.cx + bw / 2)) - x, h: Math.min(h, Math.ceil(q.cy + bh / 2)) - y };
}

/** Draw one layer frame into a context whose coordinates are texture pixels. */
export function drawLayer(ctx: CanvasRenderingContext2D, src: CanvasImageSource, l: DynamicLayer, texW: number, texH: number) {
  const [sw, sh] = sourceSize(src);
  if (!sw || !sh) return;
  const [cx0, cy0, cw, ch] = l.crop ?? [0, 0, 1, 1];
  const q = layerQuad(l, texW, texH);
  ctx.save();
  ctx.globalAlpha = l.opacity ?? 1;
  ctx.translate(q.cx, q.cy);
  ctx.rotate(-q.rot);
  ctx.drawImage(src, cx0 * sw, cy0 * sh, cw * sw, ch * sh, -q.w / 2, -q.h / 2, q.w, q.h);
  ctx.restore();
}

function toPng(c: HTMLCanvasElement): Promise<Uint8Array> {
  return new Promise((resolve, reject) =>
    c.toBlob(async (b) => (b ? resolve(new Uint8Array(await b.arrayBuffer())) : reject(new Error('png'))), 'image/png'),
  );
}

async function videoFrames(path: string, fps: number, onFrame: (v: HTMLVideoElement, i: number) => Promise<void>) {
  const v = document.createElement('video');
  v.muted = true;
  v.crossOrigin = 'anonymous';
  v.src = fileUrl(path);
  await new Promise<void>((res, rej) => {
    v.onloadeddata = () => res();
    v.onerror = () => rej(new Error(`cannot open video ${path}`));
  });
  const count = Math.min(MAX_FRAMES, Math.max(1, Math.floor(v.duration * fps)));
  for (let i = 0; i < count; i++) {
    await new Promise<void>((res) => {
      v.onseeked = () => res();
      v.currentTime = Math.min(v.duration - 0.001, i / fps);
    });
    await onFrame(v, i);
  }
  v.removeAttribute('src');
  return count;
}

/** Base texture of a material channel: the user's override, else the game texture from the preview. */
export function baseTexturePath(doc: SkinDocument, preview: WeaponPreview | null, material: string, channel: string) {
  const m = doc.materials?.find((x) => x?.material === material);
  return (m?.textures?.[channel] as string | undefined) ?? preview?.textures[material]?.[channel as 'albedo'] ?? null;
}

/** Bake every visible layer of the document. Returns the number of frames written. */
export async function bakeLayers(
  doc: SkinDocument,
  preview: WeaponPreview | null,
  projectPath: string,
  onProgress: (text: string) => void,
): Promise<number> {
  const framesDir = `${dirName(projectPath)}\\.frames`;
  let written = 0;
  for (const m of doc.materials ?? []) {
    for (const l of m?.layers ?? []) {
      if (!m || !l || l.visible === false) continue;
      const basePath = baseTexturePath(doc, preview, m.material!, l.channel ?? 'albedo');
      if (!basePath) throw Object.assign(new Error('no base texture'), { code: 'no-atlas' });
      const base = await loadImage(basePath);
      const W = base.naturalWidth;
      const H = base.naturalHeight;
      const rect = layerRect(l, W, H);
      const dir = `${framesDir}\\${l.id}`;
      const canvas = document.createElement('canvas');
      canvas.width = rect.w;
      canvas.height = rect.h;
      const ctx = canvas.getContext('2d')!;
      const writeFrame = async (src: CanvasImageSource, i: number) => {
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, rect.w, rect.h);
        ctx.drawImage(base, rect.x, rect.y, rect.w, rect.h, 0, 0, rect.w, rect.h);
        ctx.translate(-rect.x, -rect.y);
        drawLayer(ctx, src, l, W, H);
        await window.forge.writeFile(`${dir}\\${String(i).padStart(4, '0')}.png`, await toPng(canvas));
        written++;
      };
      onProgress(t('frames.k1', { v0: l.name }));
      const sources = (l.sources ?? []).filter(Boolean) as string[];
      if (l.kind === 'video') await videoFrames(sources[0], l.fps || 12, writeFrame);
      else if (l.kind === 'text') await writeFrame(textCanvas(l), 0);
      else {
        const list = l.kind === 'sequence' ? naturalSort(sources).slice(0, MAX_FRAMES) : sources.slice(0, 1);
        for (let i = 0; i < list.length; i++) await writeFrame(await loadImage(list[i]), i);
      }
      const json = JSON.stringify({ ...rect, textureWidth: W, textureHeight: H });
      await window.forge.writeFile(`${dir}\\rect.json`, new TextEncoder().encode(json));
    }
  }
  return written;
}

export function glyphCanvas(font: string | null | undefined, cellW = 64, cellH = 96): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = cellW * GLYPHS.length;
  c.height = cellH;
  const ctx = c.getContext('2d')!;
  ctx.fillStyle = '#fff';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  // Wide fonts are shrunk until every glyph fits its cell.
  const px = Math.round(cellH * 0.82);
  ctx.font = cssFont(font, px);
  const widest = Math.max(...[...GLYPHS].map((g) => ctx.measureText(g).width));
  if (widest > cellW * 0.92) ctx.font = cssFont(font, Math.floor((px * cellW * 0.92) / widest));
  [...GLYPHS].forEach((g, i) => ctx.fillText(g, i * cellW + cellW / 2, cellH / 2 + cellH * 0.04));
  return c;
}

/** A static screen's whole text in its font, white on transparent, `cellH` high (any characters). */
export function screenTextCanvas(font: string | null | undefined, text: string, cellH = 96): HTMLCanvasElement {
  const c = document.createElement('canvas');
  const px = Math.round(cellH * 0.82);
  const ctx = c.getContext('2d')!;
  ctx.font = cssFont(font, px);
  c.width = Math.max(1, Math.ceil(ctx.measureText(text).width + cellH * 0.1));
  c.height = cellH;
  ctx.font = cssFont(font, px); // resizing the canvas reset it
  ctx.fillStyle = '#fff';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, c.width / 2, cellH / 2 + cellH * 0.04);
  return c;
}

/**
 * How many digits a counter screen shows, as the plugin decides it: a set number, or automatically the digits of the
 * largest value its source can show (magazine: clip size; reserve: its maximum; total: both). Kills have no maximum:
 * automatic kills show the value's own digits (up to 4). `max` is null where the editor does not know the weapon's ammo.
 */
export function screenDigits(s: Screen, item: CatalogItem | undefined): { digits: number; max: number | null; grow: boolean } {
  const clip = item?.clipSize ?? null;
  const reserve = item?.maxReserve ?? null;
  const max =
    s.source === 'ammoReserve' ? reserve
    : s.source === 'ammoTotal' ? (clip != null && reserve != null ? clip + reserve : null)
    : s.source === 'kills' || s.source === 'static' ? null
    : clip;
  const grow = !s.digits && s.source === 'kills';
  const digits = s.digits ? s.digits : grow ? 4 : max != null ? Math.min(6, String(Math.max(0, max)).length) : 2;
  return { digits, max, grow };
}

export async function bakeGlyphs(doc: SkinDocument, projectPath: string): Promise<void> {
  const dir = `${dirName(projectPath)}\\.frames`;
  for (const a of doc.attachments ?? []) {
    if (!a?.screen) continue;
    await loadFont(a.screen.font);
    // Twice the preview resolution: the backend traces the outlines, and finer pixels give smoother strokes.
    if (a.screen.source === 'static') await window.forge.writeFile(`${dir}\\${a.id}-text.png`, await toPng(screenTextCanvas(a.screen.font, a.screen.staticText ?? '', 192)));
    else await window.forge.writeFile(`${dir}\\${a.id}-glyphs.png`, await toPng(glyphCanvas(a.screen.font, 128, 192)));
  }
}

export type { Screen };
