// Renders dynamic-layer frames and screen glyph strips in the renderer and writes them as PNGs for pack.build.
// Contract with the backend/plugin: each frame in <projectDir>/.frames/<layerId>/NNNN.png is the final pixels of the
// target region (base texture + layer, opacity baked; the plugin copies without blending); rect.json gives the region in
// base-texture pixels with a top-left origin. The glyph strip is GLYPHS (Screen.Glyphs) in equal cells, white on transparent;
// a static screen's text is one image <attachmentId>-text.png. The backend traces both into meshes.
import { cssFont, isFontFile, loadFont } from './fonts';
import { attachedOf, dirName, fileUrl, type CatalogItem, type DynamicLayer, type Screen, type SkinDocument, type WeaponPreview } from './protocol';
import { t } from './i18n';
import { drawTrim } from './trim';

/** A dynamic layer's name: what the author typed, else the default in the current language (an untouched layer stores none). */
export const layerName = (l: DynamicLayer) => l.name || t('store.k26');

export const GLYPHS = '0123456789 -%';
export const MAX_FRAMES = 240;
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

export const naturalSort = (paths: string[]) => [...paths].sort(collator.compare);

// The images used last, most recent last; a file the app writes again (writePng) is dropped and loads fresh under a new
// URL, which the page's own image cache has not seen.
const images = new Map<string, Promise<HTMLImageElement>>();
const MAX_IMAGES = 32;
let generation = 0;
export function loadImage(path: string): Promise<HTMLImageElement> {
  let p = images.get(path);
  if (p) images.delete(path);
  else
    p = new Promise((resolve, reject) => {
      const img = new Image();
      img.crossOrigin = 'anonymous';
      img.onload = () => resolve(img);
      // A failure is not kept: the file may be written again (preview cache rebuilt) and the next view retries.
      img.onerror = () => (images.delete(path), reject(new Error(`cannot load ${path}`)));
      img.src = `${fileUrl(path)}?v=${generation}`;
    });
  images.set(path, p);
  if (images.size > MAX_IMAGES) images.delete(images.keys().next().value!);
  return p;
}

function toPng(c: HTMLCanvasElement): Promise<Uint8Array> {
  return new Promise((resolve, reject) => c.toBlob(async (b) => (b ? resolve(new Uint8Array(await b.arrayBuffer())) : reject(new Error('png'))), 'image/png'));
}

/** Writes a canvas as a PNG file; a later loadImage of the path reads the new pixels. */
export async function writePng(path: string, c: HTMLCanvasElement): Promise<void> {
  await window.forge.writeFile(path, await toPng(c));
  images.delete(path);
  generation++;
}

/** Drops the decoded copies of a file written outside the editor; the next load reads it again. */
export function forgetImage(path: string) {
  images.delete(path);
  gifs.delete(path);
  generation++;
}

export const isGif = (path: string) => /\.gif$/i.test(path);

export interface SourceFrames {
  frames: CanvasImageSource[];
  /** A GIF's own rate from its frame delays; 0 for a still image. */
  fps: number;
}

// Decoded GIFs, most recent last (the live preview asks for a decal's frames on every redraw).
const gifs = new Map<string, Promise<SourceFrames>>();

async function decodeGif(path: string): Promise<SourceFrames> {
  const r = await fetch(fileUrl(path));
  if (!r.ok || !r.body) throw new Error(`cannot load ${path}`);
  const decoder = new ImageDecoder({ data: r.body, type: 'image/gif' });
  await decoder.completed;
  const frames: HTMLCanvasElement[] = [];
  let micros = 0;
  for (let i = 0; i < decoder.tracks.selectedTrack!.frameCount; i++) {
    const { image } = await decoder.decode({ frameIndex: i });
    const c = document.createElement('canvas');
    c.width = image.displayWidth;
    c.height = image.displayHeight;
    c.getContext('2d')!.drawImage(image, 0, 0);
    micros += image.duration || 100_000; // a frame without a delay shows 0.1 s, as browsers play it
    image.close();
    frames.push(c);
  }
  decoder.close();
  return { frames, fps: Math.min(60, Math.max(1, frames.length / (micros / 1e6))) };
}

/** The frames of one source file: every frame of a GIF (decoded with WebCodecs, with its own rate), else the image itself. */
export async function sourceFrames(path: string): Promise<SourceFrames> {
  if (!isGif(path)) return { frames: [await loadImage(path)], fps: 0 };
  let p = gifs.get(path);
  if (p) gifs.delete(path);
  else p = decodeGif(path).catch((e) => (gifs.delete(path), Promise.reject(e)));
  gifs.set(path, p);
  if (gifs.size > 4) gifs.delete(gifs.keys().next().value!);
  return p;
}

/** Every frame of an image or sequence layer's sources, in order (a GIF counts as its frames). */
async function stillFrames(l: DynamicLayer): Promise<CanvasImageSource[]> {
  const sources = (l.sources ?? []).filter(Boolean) as string[];
  const list = l.kind === 'sequence' ? naturalSort(sources) : sources.slice(0, 1);
  return (await Promise.all(list.map(sourceFrames))).flatMap((s) => s.frames);
}

export type TextStyle = NonNullable<DynamicLayer['text']>;

/** Text in its font and colour, `sizePx` high per line, `tracking` letter heights between letters (dynamic and livery text layers). */
export function textCanvas(t: TextStyle): HTMLCanvasElement {
  const size = t.sizePx ?? 64;
  const font = cssFont(t.font, size, 600);
  const c = document.createElement('canvas');
  const ctx = c.getContext('2d')!;
  const style = () => {
    ctx.font = font;
    ctx.letterSpacing = `${(t.tracking ?? 0) * size}px`;
  };
  style();
  const lines = (t.content || ' ').split('\n');
  const w = Math.max(...lines.map((l) => ctx.measureText(l).width), 1);
  c.width = Math.ceil(w + size * 0.2);
  c.height = Math.ceil(lines.length * size * 1.25);
  style(); // resizing the canvas reset it
  const [r, g, b, a] = t.color ?? [1, 1, 1, 1];
  ctx.fillStyle = `rgba(${r * 255},${g * 255},${b * 255},${a})`;
  ctx.textBaseline = 'top';
  lines.forEach((l, i) => ctx.fillText(l, size * 0.1, i * size * 1.25 + size * 0.1));
  return c;
}

/** textCanvas once its font has the text's characters (a font that fails to load draws with the fallback). */
export async function loadedTextCanvas(t: TextStyle | null | undefined): Promise<HTMLCanvasElement> {
  await loadFont(t?.font, t?.content || ' ').catch(() => undefined);
  return textCanvas(t ?? { content: '' });
}

/** Live source for preview: returns the drawable for a time in seconds. */
export class LayerSource {
  private frames: CanvasImageSource[] = [];
  private video: HTMLVideoElement | null = null;
  ready: Promise<void>;

  /** The layer it plays; may be replaced by a newer copy with the same sources (placement and timing edits). */
  constructor(public layer: DynamicLayer) {
    this.ready = this.load();
  }

  private async load() {
    const l = this.layer;
    const sources = (l.sources ?? []).filter(Boolean) as string[];
    if (l.kind === 'text') this.frames = [await loadedTextCanvas(l.text)];
    else if (l.kind === 'video' && sources[0]) {
      const v = document.createElement('video');
      v.src = fileUrl(sources[0]);
      v.muted = true;
      v.loop = true;
      v.crossOrigin = 'anonymous';
      v.playsInline = true;
      await v.play().catch(() => undefined);
      this.video = v;
    } else this.frames = await stillFrames(l);
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
  // At least one pixel inside the texture, also for a layer moved off it or shrunk to nothing.
  const x = Math.min(w - 1, Math.max(0, Math.floor(q.cx - bw / 2)));
  const y = Math.min(h - 1, Math.max(0, Math.floor(q.cy - bh / 2)));
  return { x, y, w: Math.max(1, Math.min(w, Math.ceil(q.cx + bw / 2)) - x), h: Math.max(1, Math.min(h, Math.ceil(q.cy + bh / 2)) - y) };
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

/** Base texture of a material channel: the user's override, else the game texture from the preview; an attachment's model
 * (attachmentMaterial) has its own pictures. */
export function baseTexturePath(doc: SkinDocument, preview: WeaponPreview | null, material: string, channel: string) {
  const m = doc.materials?.find((x) => x?.material === material);
  const attached = attachedOf(material);
  const own = attached ? doc.attachments?.find((a) => a?.id === attached)?.material?.textures?.[channel as 'albedo'] : null;
  return (m?.textures?.[channel] as string | undefined) ?? own ?? preview?.textures[material]?.[channel as 'albedo'] ?? null;
}

export type Rect = { x: number; y: number; w: number; h: number };

/** Raise when what a bake draws from the same inputs changes: every earlier bake is then redone. */
const BAKE_VERSION = 1;

/**
 * The input key of a bake: `inputs` (the document part it draws) and each named file's size and write time, hashed. A
 * bake that finds its last output under the same key skips the drawing; a file written again with other bytes, or a
 * change to the document part, gives another key.
 */
export async function bakeKey(inputs: unknown, files: (string | null | undefined)[]): Promise<string> {
  const named = files.filter((f): f is string => !!f);
  const stamps = await Promise.all(named.map((f) => window.forge.stat(f)));
  const text = JSON.stringify([BAKE_VERSION, inputs, named, stamps]);
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(hash)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** A JSON file the editor wrote (a bake's record), or null when it is missing or unreadable. */
export async function readJson<T>(path: string): Promise<T | null> {
  try {
    const text = await window.forge.readText(path);
    return text == null ? null : (JSON.parse(text) as T);
  } catch {
    return null;
  }
}

/** Empties a bake record before its outputs are written again. */
export const forgetRecord = (path: string) => window.forge.writeFile(path, new TextEncoder().encode('{}'));

/** Whether every file is there (the outputs a bake record names). */
export const allExist = async (files: string[]) => (await Promise.all(files.map((f) => window.forge.exists(f)))).every(Boolean);

/**
 * Writes one layer's frames and rect.json (the contract at the top). `produce` calls `frame` once per frame, in order,
 * with a function that draws the frame in texture pixels (the canvas shows only `rect`). Returns the frames written.
 * With `key` (bakeKey of everything the frames are drawn from), frames already written under that key are kept as they are.
 */
export async function writeLayerFrames(
  projectPath: string,
  id: string,
  rect: Rect,
  W: number,
  H: number,
  produce: (frame: (draw: (ctx: CanvasRenderingContext2D) => void) => Promise<void>) => Promise<unknown>,
  key?: string,
): Promise<number> {
  const dir = `${dirName(projectPath)}\\.frames\\${id}`;
  if (key) {
    const done = await readJson<{ key?: string; frames?: number }>(`${dir}\\rect.json`);
    if (done?.key === key && done.frames && (await window.forge.exists(`${dir}\\${String(done.frames - 1).padStart(4, '0')}.png`))) return done.frames;
    // A bake stopped half way must not leave the old key over new frames.
    if (done) await forgetRecord(`${dir}\\rect.json`);
  }
  const canvas = document.createElement('canvas');
  canvas.width = rect.w;
  canvas.height = rect.h;
  const ctx = canvas.getContext('2d')!;
  let written = 0;
  // A frame's PNG is encoded and written while the next is drawn (toBlob copies the canvas when called); at most a few
  // wait at once, each a copy of the region.
  const writing: Promise<void>[] = [];
  try {
    await produce(async (draw) => {
      ctx.setTransform(1, 0, 0, 1, -rect.x, -rect.y);
      ctx.clearRect(rect.x, rect.y, rect.w, rect.h);
      draw(ctx);
      writing.push(writePng(`${dir}\\${String(written).padStart(4, '0')}.png`, canvas));
      written++;
      if (writing.length >= 4) await writing.shift();
    });
  } finally {
    await Promise.allSettled(writing);
  }
  await Promise.all(writing);
  // `frames`: how many of the folder's PNGs are this bake's (an earlier, longer bake leaves more behind).
  const json = JSON.stringify({ ...rect, textureWidth: W, textureHeight: H, frames: written, key });
  await window.forge.writeFile(`${dir}\\rect.json`, new TextEncoder().encode(json));
  return written;
}

/** Bake every visible layer of the document. Returns the number of frames written. */
export async function bakeLayers(doc: SkinDocument, preview: WeaponPreview | null, projectPath: string, onProgress: (text: string) => void): Promise<number> {
  let written = 0;
  for (const m of doc.materials ?? []) {
    for (const l of m?.layers ?? []) {
      if (!m || !l || l.visible === false) continue;
      const basePath = baseTexturePath(doc, preview, m.material!, l.channel ?? 'albedo');
      if (!basePath) throw Object.assign(new Error(layerName(l)), { code: 'no-base-texture' });
      const base = await loadImage(basePath);
      const W = base.naturalWidth;
      const H = base.naturalHeight;
      onProgress(t('frames.k1', { v0: layerName(l) }));
      const sources = (l.sources ?? []).filter(Boolean) as string[];
      const font = l.text?.font && isFontFile(l.text.font) ? l.text.font : null;
      const key = await bakeKey([l, W, H], [basePath, ...sources, font]);
      const layerFrames = await writeLayerFrames(
        projectPath,
        l.id!,
        layerRect(l, W, H),
        W,
        H,
        async (frame) => {
          const writeFrame = (src: CanvasImageSource) =>
            frame((ctx) => {
              ctx.drawImage(base, 0, 0);
              drawLayer(ctx, src, l, W, H);
            });
          if (l.kind === 'video') await videoFrames(sources[0], l.fps || 12, writeFrame);
          else if (l.kind === 'text') await writeFrame(await loadedTextCanvas(l.text));
          else for (const src of (await stillFrames(l)).slice(0, MAX_FRAMES)) await writeFrame(src);
        },
        key,
      );
      // A layer with no picture, video or text to draw gives no frames; said by name instead of failing in the build.
      if (!layerFrames) throw Object.assign(new Error(layerName(l)), { code: 'empty-layer' });
      written += layerFrames;
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
  // The size is set by the digits and '-': a wide font is shrunk until they fit their cells. A wider glyph ('%' is in
  // most fonts) is only narrowed to its cell, so adding it never shrinks the digits.
  const px = Math.round(cellH * 0.82);
  const fit = cellW * 0.92;
  ctx.font = cssFont(font, px);
  const widest = Math.max(...[...GLYPHS].filter((g) => g !== '%').map((g) => ctx.measureText(g).width));
  if (widest > fit) ctx.font = cssFont(font, Math.floor((px * fit) / widest));
  [...GLYPHS].forEach((g, i) => {
    const w = ctx.measureText(g).width;
    ctx.save();
    ctx.translate(i * cellW + cellW / 2, cellH / 2 + cellH * 0.04);
    if (w > fit) ctx.scale(fit / w, 1);
    ctx.fillText(g, 0, 0);
    ctx.restore();
  });
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
 * A screen's border in white on transparent, `px` wide at the screen's aspect, BorderWidth thick (PLAN §3.1
 * Screen.BorderStyle): the 3D view tints it, packing traces a styled one into the border mesh.
 */
export function borderCanvas(s: Screen, px: number): HTMLCanvasElement {
  const [w, h] = s.size ?? [0.03, 0.015];
  const c = document.createElement('canvas');
  c.width = px;
  c.height = Math.max(8, Math.round((px * h) / w));
  const W = c.width;
  const H = c.height;
  const b = (Math.min(s.borderWidth ?? 0.0008, Math.min(w, h) / 2) * px) / w;
  if (b <= 0) return c;
  // A screen's styles are trims (trim.ts) with fixed proportions: brackets a third and cuts a fifth of the shorter side.
  const short = Math.min(W, H);
  const style = ({ solid: 'frame', corners: 'brackets', double: 'double', notched: 'notched', ticks: 'ticks' } as const)[s.borderStyle ?? 'solid'];
  drawTrim(c.getContext('2d')!, W, H, { style, width: b, corner: style === 'brackets' ? short * 0.3 : style === 'notched' ? short * 0.2 : 0, inset: 0, dash: 0, gap: 0, count: 12 });
  return c;
}

type Source = Screen['source'];
/** Ammo counts (the low-ammo colour applies); percentages (shown with '%'); sources with a maximum (can be a bar). */
export const isAmmo = (s: Source) => !s || s === 'ammoClip' || s === 'ammoReserve' || s === 'ammoTotal';
export const isPercent = (s: Source) => s === 'accuracy' || s === 'health' || s === 'infection';
export const isBounded = (s: Source) => isAmmo(s) || isPercent(s);

/**
 * How many digits a counter screen shows, as the plugin decides it: a set number, or automatically the digits of the
 * largest value its source can show (magazine: clip size; reserve: its maximum; total: both; percentages: 3, then '%').
 * Counts have no maximum: automatically they show the value's own digits (kills up to 4, damage up to 6). `max` is the
 * ammo maximum, null where the editor does not know the weapon's ammo or the source is not ammo.
 */
export function screenDigits(s: Screen, item: CatalogItem | undefined): { digits: number; max: number | null; grow: boolean } {
  const clip = item?.clipSize ?? null;
  const reserve = item?.maxReserve ?? null;
  const max = !isAmmo(s.source) ? null : s.source === 'ammoReserve' ? reserve : s.source === 'ammoTotal' ? (clip != null && reserve != null ? clip + reserve : null) : clip;
  const grow = !s.digits && !isBounded(s.source) && s.source !== 'static';
  const digits = s.digits ? s.digits : grow ? (s.source === 'kills' ? 4 : 6) : isPercent(s.source) ? 3 : max != null ? Math.min(6, String(Math.max(0, max)).length) : 2;
  return { digits, max, grow };
}

export async function bakeGlyphs(doc: SkinDocument, projectPath: string): Promise<void> {
  const dir = `${dirName(projectPath)}\\.frames`;
  for (const a of doc.attachments ?? []) {
    if (!a?.screen) continue;
    await loadFont(a.screen.font, a.screen.source === 'static' ? a.screen.staticText || ' ' : GLYPHS);
    // Twice the preview resolution: the backend traces the outlines, and finer pixels give smoother strokes.
    if (a.screen.source === 'static') await writePng(`${dir}\\${a.id}-text.png`, screenTextCanvas(a.screen.font, a.screen.staticText ?? '', 192));
    else await writePng(`${dir}\\${a.id}-glyphs.png`, glyphCanvas(a.screen.font, 128, 192));
    if ((a.screen.borderStyle ?? 'solid') !== 'solid') await writePng(`${dir}\\${a.id}-border.png`, borderCanvas(a.screen, 1024));
  }
}

export type { Screen };
