// Live livery of one material in the 3D view: the same composeLivery the pack bakes with, at preview size, redrawn when
// the document changes (the newest change wins; changes while a redraw runs are folded into the next one).
import { CanvasTexture, LinearSRGBColorSpace, SRGBColorSpace, type Texture } from 'three';
import { composeLivery, type DecalRenderer } from '../livery';
import { loadImage } from '../frames';
import type { LiveryLayer, MaterialOverride, TextureChannel } from '../protocol';
import { decalRenderer } from './liveryGpu';
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

export class LiveryPreview {
  readonly albedo = canvas();
  private ms = canvas();
  private msShown = canvas();
  private albedoTex = texture(this.albedo, true);
  private msTex = texture(this.msShown, false);
  private next: { m: MaterialOverride; base: string | null; key: string } | null = null;
  private running = false;
  private finish: MaterialOverride['finish'] = null;
  /** Decal renders by layer id, reused while the decal and the part layout stay the same. */
  private decals = new Map<string, { key: string; canvas: HTMLCanvasElement }>();
  /** What the last redraw was asked for; an edit elsewhere in the document leaves the same objects (immer). */
  private inputs: unknown[] = [];
  /** Counts the redraws of `albedo`, for what draws over it. */
  version = 0;

  constructor(private material: string) {}

  /** What the livery paints for a channel; null leaves the channel to the material. */
  texture(ch: TextureChannel): Texture | null {
    if (ch === 'albedo') return this.albedoTex;
    if (ch === 'metallicSmoothness') return this.finish ? this.msTex : null;
    if (ch === 'emissive') return this.finish === 'glow' ? this.albedoTex : null;
    return null;
  }

  update(m: MaterialOverride, base: string | null, layout: string) {
    const inputs = [m.livery, m.finish, m.part, base, layout];
    if (inputs.every((x, i) => x === this.inputs[i])) return;
    this.inputs = inputs;
    this.finish = m.finish ?? null;
    this.next = { m, base, key: layout };
    if (!this.running) void this.run();
  }

  private async run() {
    this.running = true;
    while (this.next) {
      const { m, base, key } = this.next;
      this.next = null;
      try {
        const img = base ? await loadImage(base).catch(() => null) : null;
        await composeLivery(m, img, this.albedo, m.finish ? this.ms : null, this.cachedDecal(m, key));
        if (m.finish) {
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

  private cachedDecal(m: MaterialOverride, layout: string): DecalRenderer {
    const render = decalRenderer(this.material, m.part);
    return (l: LiveryLayer, img, w, h) => {
      const key = JSON.stringify([l.image, l.position, l.rotation, l.angle, l.size, l.depth, l.mirror, l.color, w, h, layout]);
      const hit = this.decals.get(l.id!);
      if (hit?.key === key) return hit.canvas;
      const c = hit?.canvas ?? document.createElement('canvas');
      [c.width, c.height] = [w, h];
      c.getContext('2d')!.drawImage(render(l, img, w, h), 0, 0);
      this.decals.set(l.id!, { key, canvas: c });
      return c;
    };
  }

  dispose() {
    this.albedoTex.dispose();
    this.msTex.dispose();
  }
}
