// Live livery of one material in the 3D view: the same composeLivery the pack bakes with, at preview size, redrawn when
// the document changes (the newest change wins; changes while a redraw runs are folded into the next one).
import { CanvasTexture, LinearSRGBColorSpace, SRGBColorSpace, type Texture } from 'three';
import { cachedUv, composeLivery, writesMs, type FrameOf } from '../livery';
import { loadImage } from '../frames';
import type { LiveryLayer, MaterialOverride, TextureChannel } from '../protocol';
import { uvRenderer } from './liveryGpu';
import { playTime } from './refs';
import { repackMetallicSmoothness } from './textures';

const SIZE = 1024;

function texture(c: HTMLCanvasElement, srgb: boolean): CanvasTexture {
  const t = new CanvasTexture(c);
  t.flipY = false;
  t.colorSpace = srgb ? SRGBColorSpace : LinearSRGBColorSpace;
  t.anisotropy = 8;
  return t;
}

const canvas = () => Object.assign(document.createElement('canvas'), { width: SIZE, height: SIZE });

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
  private next: { m: MaterialOverride; base: string | null; baseMs: string | null; key: string } | null = null;
  /** The last redraw asked for, drawn again when an animated decal moves to another frame. */
  private last: LiveryPreview['next'] = null;
  private running = false;
  private finish: MaterialOverride['finish'] = null;
  private writesMs = false;
  /** Decal renders by layer id, reused while the decal, its frame and the part layout stay the same. */
  private decals = new Map<string, { key: string; image: CanvasImageSource | null; canvas: HTMLCanvasElement }>();
  /** Animated decals of the last redraw: their frame count, rate and the frame drawn. */
  private playing = new Map<string, { l: LiveryLayer; count: number; fps: number; shown: number }>();
  private start = performance.now();
  /** What the last redraw was asked for; an edit elsewhere in the document leaves the same objects (immer). */
  private inputs: unknown[] = [];
  /** Counts the redraws of `albedo`, for what draws over it. */
  version = 0;

  constructor(private material: string) {}

  /** What the livery paints for a channel; null leaves the channel to the material. */
  texture(ch: TextureChannel): Texture | null {
    if (ch === 'albedo') return this.albedoTex;
    if (ch === 'metallicSmoothness') return this.writesMs ? this.msTex : null;
    if (ch === 'emissive') return this.finish === 'glow' ? this.albedoTex : null;
    return null;
  }

  update(m: MaterialOverride, base: string | null, baseMs: string | null, layout: string) {
    const inputs = [m.livery, m.finish, m.part, base, baseMs, layout];
    if (inputs.every((x, i) => x === this.inputs[i])) return;
    this.inputs = inputs;
    this.finish = m.finish ?? null;
    this.writesMs = writesMs(m);
    this.next = this.last = { m, base, baseMs, key: layout };
    if (!this.running) void this.run();
  }

  /** Redraws when an animated decal is due to show another frame (called each tick of the 3D view). */
  tick() {
    if (this.running || !this.last) return;
    for (const p of this.playing.values())
      if (frameNow(p.l, p.count, p.fps, this.start) !== p.shown) {
        this.next = this.last;
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
    while (this.next) {
      const { m, base, baseMs, key } = this.next;
      this.next = null;
      this.playing.clear();
      try {
        const img = base ? await loadImage(base).catch(() => null) : null;
        const ms = writesMs(m);
        const msImg = ms && baseMs ? await loadImage(baseMs).catch(() => null) : null;
        await composeLivery(m, img, this.albedo, ms ? this.ms : null, cachedUv(uvRenderer(this.material, m.part), this.decals, key), this.frameOf, msImg);
        if (ms) {
          const px = this.ms.getContext('2d', { willReadFrequently: true })!.getImageData(0, 0, SIZE, SIZE);
          this.msShown.getContext('2d', { willReadFrequently: true })!.putImageData(px, 0, 0);
          repackMetallicSmoothness(this.msShown);
          this.msTex.needsUpdate = true;
        }
        this.albedoTex.needsUpdate = true;
        this.version++;
      } catch {
        // The 3D view is not ready yet (no renderer or model): the next document change draws it.
        this.inputs = [];
      }
    }
    this.running = false;
  }

  dispose() {
    this.albedoTex.dispose();
    this.msTex.dispose();
  }
}
