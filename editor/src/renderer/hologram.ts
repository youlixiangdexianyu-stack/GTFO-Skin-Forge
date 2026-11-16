// Hologram looks beyond the game's hologram shader (SkinDocument Hologram). The shader has no property for a colour ramp,
// scanlines or a lit outline (its properties are logged by Native/Holograms.cs), so the editor makes them from what the
// plugin already draws: the ramp is a picture on the attachment (baked here, shown on the model's UVs; shapes have
// UVs running up the shape), scanlines and the outline are two code effects on the attachment's model (ForgeCore's
// `scanlines` and `fresnel` programs). The 3D view and the pack use the same functions.
import { CanvasTexture, SRGBColorSpace } from 'three';
import { dirName, type Attachment, type CodeEffect, type SkinDocument } from './protocol';
import { writePng } from './frames';

type Hologram = NonNullable<Attachment['hologram']>;

/** A new hologram's settings. */
export const newHologram = (): Hologram => ({ flicker: 0.3, opacity: 1, color2: null, scanlines: 0, edgeGlow: 0 });

const toSrgbA = (c: number[] | null | undefined, a = 1) => [...(c ?? [1, 1, 1]).slice(0, 3).map((x) => x ?? 1), a];

/** The code effects a hologram adds on its attachment's model: ids `<attachment>~scan` and `<attachment>~edge`. */
export function hologramEffects(doc: SkinDocument): CodeEffect[] {
  const out: CodeEffect[] = [];
  for (const a of doc.attachments ?? []) {
    const h = a?.hologram;
    if (!a?.mesh || !h || a.visible === false) continue;
    const c = a.material?.color ?? [0.45, 0.9, 1, 1];
    const top = h.color2 ?? c;
    if ((h.scanlines ?? 0) > 0)
      out.push({
        id: `${a.id}~scan`,
        name: 'scanlines',
        attachment: a.id,
        program: 'scanlines',
        blend: 'additive',
        intensity: 2 * h.scanlines!,
        params: { count: 140, speed: 0.25, thickness: 0.35, sweepWidth: 0.12, sweepSpeed: 0.4 },
        colors: { line: toSrgbA(c), sweep: toSrgbA(top) },
      });
    if ((h.edgeGlow ?? 0) > 0)
      out.push({
        id: `${a.id}~edge`,
        name: 'edge glow',
        attachment: a.id,
        program: 'fresnel',
        blend: 'additive',
        intensity: h.edgeGlow!,
        params: { power: 2.5, bias: 0 },
        colors: { rim: toSrgbA(top), core: [0, 0, 0, 0] },
      });
  }
  return out;
}

/** The ramp from the attachment's colour (bottom, v = 0 in the model's UVs) to `color2` (top), 4 x 256. */
export function rampCanvas(bottom: number[], top: number[]): HTMLCanvasElement {
  const c = Object.assign(document.createElement('canvas'), { width: 4, height: 256 });
  const ctx = c.getContext('2d')!;
  const css = (x: number[]) =>
    `rgb(${x
      .slice(0, 3)
      .map((v) => Math.round(Math.min(1, Math.max(0, v ?? 0)) * 255))
      .join(',')})`;
  const g = ctx.createLinearGradient(0, 0, 0, 256); // glTF UVs: row 0 at v = 0
  g.addColorStop(0, css(bottom));
  g.addColorStop(1, css(top));
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, 4, 256);
  return c;
}

const ramps = new Map<string, CanvasTexture>();
/** The 3D view's copy of a hologram's ramp (as the model's UVs read it), or null without a second colour. */
export function rampTexture(a: Attachment): CanvasTexture | null {
  const h = a.hologram;
  if (!h?.color2) return null;
  const bottom = a.material?.color ?? [1, 1, 1, 1];
  const key = JSON.stringify([bottom, h.color2]);
  let t = ramps.get(key);
  if (!t) {
    t = new CanvasTexture(rampCanvas(bottom, h.color2));
    t.flipY = false;
    t.colorSpace = SRGBColorSpace;
    ramps.set(key, t);
    if (ramps.size > 16) {
      const [k, old] = ramps.entries().next().value!;
      old.dispose();
      ramps.delete(k);
    }
  }
  return t;
}

/**
 * Into the packed copy: each hologram's ramp as its picture (unless it has one) and its colour white, and the code effects
 * of every hologram after the skin's own.
 */
export async function bakeHolograms(doc: SkinDocument, projectPath: string) {
  for (const a of doc.attachments ?? []) {
    const h = a?.hologram;
    if (!a?.mesh || !h?.color2 || a.material?.textures?.albedo) continue;
    const path = `${dirName(projectPath)}\\.frames\\${a.id}-hologram.png`;
    await writePng(path, rampCanvas(a.material?.color ?? [1, 1, 1, 1], h.color2));
    a.material = { ...a.material, textures: { ...a.material?.textures, albedo: path }, color: [1, 1, 1, a.material?.color?.[3] ?? 1] };
  }
  const extra = hologramEffects(doc);
  if (extra.length) doc.codeEffects = [...(doc.codeEffects ?? []), ...extra];
}

/** The colour a new hologram or shape takes from the skin: its first player colour, else its first coloured livery layer or glow, else cyan. */
export function skinAccent(doc: SkinDocument): number[] {
  const player = doc.playerColors?.find((p) => p?.default)?.default;
  if (player) return [...player.slice(0, 3), 1];
  const vivid = (c: number[] | null | undefined) => !!c && Math.max(c[0], c[1], c[2]) - Math.min(c[0], c[1], c[2]) > 0.25;
  for (const m of doc.materials ?? []) {
    for (const l of m?.livery ?? []) if (l && l.kind !== 'wear' && vivid(l.color)) return [...l.color!.slice(0, 3), 1];
    if (vivid(m?.params?.emissiveColor)) return [...m!.params!.emissiveColor!.slice(0, 3), 1];
  }
  return [0.45, 0.9, 1, 1];
}
