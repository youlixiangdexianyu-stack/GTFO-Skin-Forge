// Texture loading for the preview: downscaled for display, MetallicSmoothness repacked for three.js, and freed when no
// material uses them any more (replacing large textures over and over otherwise fills GPU memory until the page dies).
import { CanvasTexture, LinearSRGBColorSpace, NoColorSpace, SRGBColorSpace, Texture } from 'three';
import { fileUrl, type TextureChannel } from '../protocol';

const cache = new Map<string, Promise<Texture>>();

/** Preview textures are at most this size; the pack is always built from the original files. */
const MAX = 2048;

/** Decodes an image file to a bitmap no larger than MAX on its longest side, with its pixels left as they are. */
async function decode(path: string, premultiply = false): Promise<ImageBitmap> {
  const blob = await (await fetch(fileUrl(path))).blob();
  const opts: ImageBitmapOptions = {
    premultiplyAlpha: premultiply ? 'premultiply' : 'none',
    colorSpaceConversion: 'none',
  };
  const full = await createImageBitmap(blob, opts);
  const k = MAX / Math.max(full.width, full.height);
  if (k >= 1) return full;
  const small = await createImageBitmap(full, {
    ...opts,
    resizeWidth: Math.round(full.width * k),
    resizeHeight: Math.round(full.height * k),
    resizeQuality: 'high',
  });
  full.close();
  return small;
}

function finish(t: Texture, channel: TextureChannel): Texture {
  t.flipY = false; // glTF UV convention
  t.colorSpace = channel === 'albedo' || channel === 'emissive' ? SRGBColorSpace : channel === 'normal' ? NoColorSpace : LinearSRGBColorSpace;
  t.anisotropy = 8;
  t.needsUpdate = true;
  return t;
}

// Unity packs metallic in R and smoothness in A; three.js reads metalness from B and roughness from G.
function metallicSmoothness(img: ImageBitmap): Texture {
  const c = document.createElement('canvas');
  c.width = img.width;
  c.height = img.height;
  const ctx = c.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(img, 0, 0);
  img.close();
  const data = ctx.getImageData(0, 0, c.width, c.height);
  const p = data.data;
  for (let i = 0; i < p.length; i += 4) {
    const metal = p[i];
    const rough = 255 - p[i + 3];
    p[i] = 0;
    p[i + 1] = rough;
    p[i + 2] = metal;
    p[i + 3] = 255;
  }
  ctx.putImageData(data, 0, 0);
  return new CanvasTexture(c);
}

export function textureKey(path: string, channel: TextureChannel | 'reticle'): string {
  return `${channel}|${path}`;
}

/**
 * A sight reticle is added onto the view by colour, so its transparent pixels must be black: it is decoded with the
 * colour multiplied by alpha (`reticle` key in the same cache).
 */
export function loadTexture(path: string, channel: TextureChannel | 'reticle'): Promise<Texture> {
  const key = textureKey(path, channel);
  let p = cache.get(key);
  if (!p) {
    p = decode(path, channel === 'reticle').then((img) => {
      if (channel === 'reticle') {
        const t = new Texture(img);
        t.addEventListener('dispose', () => img.close());
        return finish(t, 'emissive');
      }
      if (channel === 'metallicSmoothness') return finish(metallicSmoothness(img), channel);
      const t = new Texture(img);
      t.addEventListener('dispose', () => img.close());
      return finish(t, channel);
    });
    p.catch(() => cache.delete(key));
    cache.set(key, p);
  }
  return p;
}

/**
 * Frees every cached texture whose key is not in `keep` (GPU copy and decoded pixels). Called once the textures in use
 * have been put on the materials, so nothing drawn still points at a freed one.
 */
export function releaseTextures(keep: Set<string>) {
  for (const [key, p] of cache) {
    if (keep.has(key)) continue;
    cache.delete(key);
    void p.then(
      (t) => t.dispose(),
      () => undefined,
    );
  }
}
