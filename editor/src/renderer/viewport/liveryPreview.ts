// Live livery of one material in the 3D view: the same composeLivery the pack bakes with, at the size the pack bakes it
// (bakeSize: the material's texture size in game), redrawn when the document changes (the newest change wins; changes while a
// redraw runs are folded into the next one). A material with an animated decal is drawn at most PREVIEW_SIZE on its longest
// side, and a new frame redraws only from the first animated decal up (LiveryKeep), the albedo alone.
import { CanvasTexture, LinearSRGBColorSpace, SRGBColorSpace, type Texture } from 'three';
import {
  bakeSize,
  cachedUv,
  composeLivery,
  framedUv,
  isGifDecal,
  layersOf,
  scaledTo,
  tintedPicture,
  writesEmissive,
  writesMs,
  type FrameOf,
  type FrameRenders,
  type LiveryKeep,
  type UvRenderer,
} from '../livery';
import { loadImage, sourceFrames } from '../frames';
import type { LiveryLayer, MaterialOverride, TextureChannel } from '../protocol';
import { uvRenderer } from './liveryGpu';
import { playTime } from './refs';
import { repackMetallicSmoothness } from './textures';

function texture(c: HTMLCanvasElement, srgb: boolean): CanvasTexture {
  const t = new CanvasTexture(c);
  t.flipY = false;
  t.colorSpace = srgb ? SRGBColorSpace : LinearSRGBColorSpace;
  t.anisotropy = 8;
  return t;
}

const canvas = () => Object.assign(document.createElement('canvas'), { width: 1024, height: 1024 });

/** Longest side of an animated livery's preview: about what the 3D view shows of one material at its largest. */
const PREVIEW_SIZE = 1024;
/** Bytes of projected decal frames one material's preview keeps. */
const FRAME_BUDGET = 192 * 1024 * 1024;

/** Whether one of `m`'s visible decals has more than one frame. */
const animated = async (m: MaterialOverride) =>
  (
    await Promise.all(
      layersOf(m)
        .filter((l) => l.visible !== false && isGifDecal(l))
        .map((l) =>
          sourceFrames(l.image!).then(
            (s) => s.frames.length > 1,
            () => false,
          ),
        ),
    )
  ).some(Boolean);

/** The frame an animated decal shows now, as the game plays its layer (its first frame while it does not play). */
function frameNow(l: LiveryLayer, count: number, fps: number, since: number) {
  const time = playTime(l.trigger, l.play, l.playSeconds, count / fps, since);
  return time === null ? 0 : Math.min(count - 1, Math.floor(time * fps));
}

export class LiveryPreview {
  readonly albedo = canvas();
  private ms = canvas();
  private msShown = canvas();
  private albedoTex = texture(this.albedo, true);
  private msTex = texture(this.msShown, false);
  private glow = canvas();
  private glowTex = texture(this.glow, true);
  private next: { m: MaterialOverride; base: string | null; baseMs: string | null; baseGlow: string | null; key: string; tint: number[] | null } | null = null;
  /** The last redraw asked for, drawn again when an animated decal moves to another frame. */
  private last: LiveryPreview['next'] = null;
  private running = false;
  /** The next redraw only shows animated decals at another frame (tick); an edit clears it. */
  private frameOnly = false;
  /** What a frame-only redraw keeps from the last full one; null until a full redraw with an animated decal. */
  private keep: LiveryKeep | null = null;
  private finish: MaterialOverride['finish'] = null;
  private writesMs = false;
  private writesGlow = false;
  /** Decal renders by layer id, reused while the decal, its frame and the part layout stay the same. */
  private decals = new Map<string, { key: string; image: CanvasImageSource | null; canvas: HTMLCanvasElement }>();
  /** Every frame of the animated decals as projected, so a playing decal reads nothing back from the GPU after one loop. */
  private frames: FrameRenders = new Map();
  private uv = (m: MaterialOverride, key: string): UvRenderer => cachedUv(framedUv(uvRenderer(this.material, m.part), this.frames, FRAME_BUDGET, key), this.decals, key);
  /** Animated decals of the last redraw: their frame count, rate and the frame drawn. */
  private playing = new Map<string, { l: LiveryLayer; count: number; fps: number; shown: number }>();
  private start = performance.now();
  /** What the last redraw was asked for; an edit elsewhere in the document leaves the same objects (immer). */
  private inputs: unknown[] = [];
  /** Counts the redraws of `albedo`, for what draws over it. */
  version = 0;
  /** Redraws running in any livery (the skin thumbnail waits for them). */
  static busy = 0;
  /** Builds running (actions.ts build): animated decals hold their frame meanwhile, so the preview leaves the main thread
   *  to the bake. */
  static held = 0;

  constructor(private material: string) {}

  /** What the livery paints for a channel; null leaves the channel to the material. */
  texture(ch: TextureChannel): Texture | null {
    if (ch === 'albedo') return this.albedoTex;
    if (ch === 'metallicSmoothness') return this.writesMs ? this.msTex : null;
    if (ch === 'emissive') return this.finish === 'glow' ? this.albedoTex : this.writesGlow ? this.glowTex : null;
    return null;
  }

  /** `tint`: an attachment's model, whose picture shows under the paint as livery.paintTint gives it. */
  update(m: MaterialOverride, base: string | null, baseMs: string | null, baseGlow: string | null, layout: string, tint: number[] | null = null) {
    const inputs = [m.livery, m.finish, m.textureSize, m.part, base, baseMs, baseGlow, layout, tint?.join()];
    if (inputs.every((x, i) => x === this.inputs[i])) return;
    this.inputs = inputs;
    this.finish = m.finish ?? null;
    this.writesMs = writesMs(m);
    this.writesGlow = writesEmissive(m) && m.finish !== 'glow';
    this.next = this.last = { m, base, baseMs, baseGlow, key: layout, tint };
    this.frameOnly = false;
    if (!this.running) void this.run();
  }

  /** Redraws when an animated decal is due to show another frame (called each tick of the 3D view while it is seen). */
  tick() {
    if (this.running || !this.last || LiveryPreview.held) return;
    for (const p of this.playing.values())
      if (frameNow(p.l, p.count, p.fps, this.start) !== p.shown) {
        this.next = this.last;
        this.frameOnly = true;
        void this.run();
        return;
      }
  }

  private frameOf: FrameOf = (l, count, fps) => {
    const shown = frameNow(l, count, fps, this.start);
    this.playing.set(l.id!, { l, count, fps, shown });
    return shown;
  };

  private async run() {
    this.running = true;
    LiveryPreview.busy++;
    while (this.next) {
      const { m, base, baseMs, baseGlow, key, tint } = this.next;
      this.next = null;
      // A glowing animated decal changes the emissive too: that takes a full redraw.
      const frameOnly = this.frameOnly && !!this.keep && !layersOf(m).some((l) => l.glow && isGifDecal(l));
      this.frameOnly = false;
      this.playing.clear();
      try {
        const raw = base ? await loadImage(base).catch(() => null) : null;
        const img = tint ? tintedPicture(raw, tint) : raw;
        if (frameOnly) {
          await composeLivery(m, img, this.albedo, null, this.uv(m, key), this.frameOf, null, null, null, this.keep);
          this.albedoTex.needsUpdate = true;
          this.version++;
          continue;
        }
        const size = bakeSize(m, raw);
        const moving = await animated(m);
        this.resize(...(moving ? scaledTo(size, Math.min(PREVIEW_SIZE, Math.max(...size))) : size));
        this.keep = moving ? { below: null, layers: new Map() } : null;
        const ms = writesMs(m);
        const msImg = ms && baseMs ? await loadImage(baseMs).catch(() => null) : null;
        const lit = writesEmissive(m) && m.finish !== 'glow';
        const glowImg = lit && baseGlow ? await loadImage(baseGlow).catch(() => null) : null;
        await composeLivery(m, img, this.albedo, ms ? this.ms : null, this.uv(m, key), this.frameOf, msImg, lit ? this.glow : null, glowImg, this.keep);
        if (lit) this.glowTex.needsUpdate = true;
        if (ms) {
          const px = this.ms.getContext('2d', { willReadFrequently: true })!.getImageData(0, 0, this.ms.width, this.ms.height);
          this.msShown.getContext('2d', { willReadFrequently: true })!.putImageData(px, 0, 0);
          repackMetallicSmoothness(this.msShown);
          this.msTex.needsUpdate = true;
        }
        this.albedoTex.needsUpdate = true;
        this.version++;
      } catch {
        // The 3D view is not ready yet (no renderer or model): the next document change draws it.
        this.inputs = [];
        this.keep = null;
      }
    }
    this.running = false;
    LiveryPreview.busy--;
  }

  /** Gives every canvas the size to draw at; a texture whose canvas changed size is uploaded anew. */
  private resize(w: number, h: number) {
    if (this.albedo.width === w && this.albedo.height === h) return;
    for (const c of [this.albedo, this.ms, this.msShown, this.glow]) [c.width, c.height] = [w, h];
    this.decals.clear();
    this.dispose();
  }

  dispose() {
    this.frames.clear();
    this.albedoTex.dispose();
    this.msTex.dispose();
    this.glowTex.dispose();
  }
}
