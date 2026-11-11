// Texture loading for the preview. Unity packs metallic in R and smoothness in A; three.js reads metalness from B and
// roughness from G, so MetallicSmoothness maps are repacked once on load.
import { CanvasTexture, LinearSRGBColorSpace, NoColorSpace, SRGBColorSpace, Texture, TextureLoader } from 'three';
import { fileUrl, type TextureChannel } from '../protocol';
import { loadImage } from '../frames';

const loader = new TextureLoader();
const cache = new Map<string, Promise<Texture>>();

function finish(t: Texture, channel: TextureChannel): Texture {
  t.flipY = false; // glTF UV convention
  t.colorSpace = channel === 'albedo' || channel === 'emissive' ? SRGBColorSpace : channel === 'normal' ? NoColorSpace : LinearSRGBColorSpace;
  t.anisotropy = 8;
  t.needsUpdate = true;
  return t;
}

async function metallicSmoothness(path: string): Promise<Texture> {
  const img = await loadImage(path);
  const c = document.createElement('canvas');
  c.width = img.naturalWidth;
  c.height = img.naturalHeight;
  const ctx = c.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(img, 0, 0);
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

export function loadTexture(path: string, channel: TextureChannel): Promise<Texture> {
  const key = `${channel}|${path}`;
  let p = cache.get(key);
  if (!p) {
    p = (channel === 'metallicSmoothness' ? metallicSmoothness(path) : loader.loadAsync(fileUrl(path))).then((t) => finish(t, channel));
    p.catch(() => cache.delete(key));
    cache.set(key, p);
  }
  return p;
}
