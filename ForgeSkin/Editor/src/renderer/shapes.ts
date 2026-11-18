// Built-in shapes (PLAN §3.1 Shapes): three.js geometry from a few plain parameters, written as a GLB and imported like any
// GLB attachment, so the pack and the plugin see an ordinary mesh. A wireframe is thin tubes along the shape's edges.
import { BoxGeometry, BufferAttribute, BufferGeometry, ConeGeometry, CylinderGeometry, EdgesGeometry, Mesh, MeshStandardMaterial, Quaternion, SphereGeometry, TorusGeometry, Vector3 } from 'three';
import { GLTFExporter } from 'three/examples/jsm/exporters/GLTFExporter.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { baseName, call, dirName, type Attachment } from './protocol';
import { loadImage, writePng } from './frames';
import { t } from './i18n';
import { autosavePath, hangAsCharm, uid, useStore } from './store';
import { newHologram, skinAccent } from './hologram';
import type { HoloSet } from './holoSets';

export type ShapeKind = 'line' | 'sphere' | 'ring' | 'arc' | 'box' | 'grid' | 'cylinder' | 'cone' | 'panel' | 'hexPanel' | 'reticle' | 'tickArc' | 'image';

/** A parameter: `mm` lengths are stored in metres and shown in millimetres. */
export type ParamKey = 'length' | 'thickness' | 'radius' | 'segments' | 'angle' | 'width' | 'height' | 'depth' | 'cells' | 'ticks';

export interface ShapeParam {
  key: ParamKey;
  unit: 'mm' | 'count' | 'deg';
  value: number;
  min: number;
  max?: number;
}

export interface Shape {
  kind: ShapeKind;
  wire: boolean;
  params: Record<string, number>;
  /** The hologram set (holoSets.ts) it was added with: its default name says so. */
  set?: HoloSet;
}

const mm = (key: ParamKey, value: number, min = 0.0001): ShapeParam => ({ key, unit: 'mm', value, min });
const count = (key: ParamKey, value: number, min: number, max: number): ShapeParam => ({ key, unit: 'count', value, min, max });
const THICK = mm('thickness', 0.0008);

/** Each kind's parameters with their defaults (gun scale), and whether it can be a wireframe. */
export const SHAPES: Record<ShapeKind, { params: ShapeParam[]; wire: boolean }> = {
  line: { params: [mm('length', 0.05), THICK], wire: false },
  sphere: { params: [mm('radius', 0.01), count('segments', 12, 4, 48), THICK], wire: true },
  ring: { params: [mm('radius', 0.012), THICK], wire: false },
  arc: { params: [mm('radius', 0.012), { key: 'angle', unit: 'deg', value: 120, min: 5, max: 360 }, THICK], wire: false },
  box: { params: [mm('width', 0.02), mm('height', 0.02), mm('depth', 0.02), THICK], wire: true },
  grid: { params: [mm('width', 0.04), mm('height', 0.03), count('cells', 6, 1, 32), mm('thickness', 0.0004)], wire: false },
  cylinder: { params: [mm('radius', 0.008), mm('height', 0.02), count('segments', 16, 3, 64), THICK], wire: true },
  cone: { params: [mm('radius', 0.008), mm('height', 0.02), count('segments', 16, 3, 64), THICK], wire: true },
  panel: { params: [mm('width', 0.03), mm('height', 0.02)], wire: true },
  hexPanel: { params: [mm('radius', 0.004), count('cells', 2, 1, 6), mm('thickness', 0.0004)], wire: false },
  reticle: { params: [mm('radius', 0.012), count('ticks', 24, 4, 72), mm('length', 0.002), THICK], wire: false },
  tickArc: { params: [mm('radius', 0.015), { key: 'angle', unit: 'deg', value: 90, min: 10, max: 360 }, count('ticks', 18, 2, 72), mm('length', 0.002), THICK], wire: false },
  // A picture's plane (addImageHologram): a panel whose UVs run top-down like the picture's rows.
  image: { params: [mm('width', 0.03), mm('height', 0.02)], wire: false },
};

/** The shapes the Shapes menus offer; an image plane comes with its picture (addImageHologram). */
export const MENU_SHAPES: ShapeKind[] = (Object.keys(SHAPES) as ShapeKind[]).filter((k) => k !== 'image');

export function newShape(kind: ShapeKind): Shape {
  const { params, wire } = SHAPES[kind];
  // A solid starts as a wireframe (the hologram look); a panel starts solid.
  return { kind, wire: wire && kind !== 'panel', params: Object.fromEntries(params.map((p) => [p.key, p.value])) };
}

/** A thin round bar from a to b. */
function bar(a: Vector3, b: Vector3, thickness: number, sides = 6): BufferGeometry {
  const dir = new Vector3().subVectors(b, a);
  const g = new CylinderGeometry(thickness / 2, thickness / 2, dir.length(), sides, 1);
  g.applyQuaternion(new Quaternion().setFromUnitVectors(new Vector3(0, 1, 0), dir.normalize()));
  g.translate((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2);
  return g;
}

/** The edges between faces that meet at an angle (a sphere's lat-long quads are flat, so their diagonals drop out). */
function wireframe(solid: BufferGeometry, thickness: number): BufferGeometry {
  const p = new EdgesGeometry(solid, 1).getAttribute('position');
  const bars: BufferGeometry[] = [];
  for (let i = 0; i < p.count; i += 2) {
    const a = new Vector3().fromBufferAttribute(p, i);
    const b = new Vector3().fromBufferAttribute(p, i + 1);
    if (a.distanceTo(b) > 1e-6) bars.push(bar(a, b, thickness, 4));
  }
  return mergeGeometries(bars)!;
}

/** Bars from the centre's `radius` inwards by `len`, `n` of them over `angle` degrees starting at +X (every third longer). */
function ticks(radius: number, len: number, n: number, angle: number, thickness: number): BufferGeometry[] {
  const out: BufferGeometry[] = [];
  const full = angle >= 359.5;
  for (let i = 0; i < n + (full ? 0 : 1); i++) {
    const a = ((angle * i) / n) * (Math.PI / 180);
    const l = i % 3 === 0 ? len * 1.8 : len;
    const d = new Vector3(Math.cos(a), Math.sin(a), 0);
    out.push(bar(d.clone().multiplyScalar(radius), d.clone().multiplyScalar(radius - l), thickness, 4));
  }
  return out;
}

/** A honeycomb of hexagon outlines `rings` deep around the centre one, each `r` across its corners. */
function honeycomb(r: number, rings: number, thickness: number): BufferGeometry {
  const bars: BufferGeometry[] = [];
  const seen = new Set<string>();
  const key = (v: Vector3) => `${v.x.toFixed(6)},${v.y.toFixed(6)}`;
  for (let q = -rings; q <= rings; q++)
    for (let p = Math.max(-rings, -q - rings); p <= Math.min(rings, -q + rings); p++) {
      // Pointy-top axial coordinates.
      const c = new Vector3(r * Math.sqrt(3) * (q + p / 2), r * 1.5 * p, 0);
      const corners = Array.from({ length: 6 }, (_, i) => c.clone().add(new Vector3(Math.cos((Math.PI / 3) * i + Math.PI / 6) * r, Math.sin((Math.PI / 3) * i + Math.PI / 6) * r, 0)));
      corners.forEach((a, i) => {
        const b = corners[(i + 1) % 6];
        const k = [key(a), key(b)].sort().join('|'); // an edge two cells share is drawn once
        if (seen.has(k)) return;
        seen.add(k);
        bars.push(bar(a, b, thickness, 4));
      });
    }
  return mergeGeometries(bars)!;
}

/** UVs laid flat from the front (x across, y up, 0..1 over the shape), so a hologram's colour ramp runs up the shape;
 * `down` runs v from the top instead, as a picture's rows (glTF v 0 is a picture's top row). */
function frontUvs(g: BufferGeometry, down = false): BufferGeometry {
  g.computeBoundingBox();
  const { min, max } = g.boundingBox!;
  const p = g.getAttribute('position');
  const uv = new Float32Array(p.count * 2);
  const [w, h] = [Math.max(1e-6, max.x - min.x), Math.max(1e-6, max.y - min.y)];
  for (let i = 0; i < p.count; i++) [uv[i * 2], uv[i * 2 + 1]] = [(p.getX(i) - min.x) / w, down ? (max.y - p.getY(i)) / h : (p.getY(i) - min.y) / h];
  g.setAttribute('uv', new BufferAttribute(uv, 2));
  return g;
}

/** The shape's geometry in metres, three.js space (Y up), centred on the origin, with front-facing UVs (frontUvs). */
export function shapeGeometry(s: Shape): BufferGeometry {
  return frontUvs(shapeSolid(s), s.kind === 'image');
}

function shapeSolid(s: Shape): BufferGeometry {
  const p = { ...newShape(s.kind).params, ...s.params };
  const t = p.thickness ?? 0.0008;
  const seg = Math.round(p.segments ?? 16);
  let solid: BufferGeometry;
  switch (s.kind) {
    case 'line':
      return bar(new Vector3(-p.length / 2, 0, 0), new Vector3(p.length / 2, 0, 0), t, 8);
    case 'ring':
      return new TorusGeometry(p.radius, t / 2, 8, 64);
    case 'arc':
      return new TorusGeometry(p.radius, t / 2, 8, Math.max(4, Math.round((64 * p.angle) / 360)), (p.angle * Math.PI) / 180);
    case 'hexPanel':
      return honeycomb(p.radius, Math.round(p.cells), t);
    case 'reticle':
      return mergeGeometries([new TorusGeometry(p.radius, t / 2, 8, 96), ...ticks(p.radius, p.length, Math.round(p.ticks), 360, t * 0.8)])!;
    case 'tickArc':
      return mergeGeometries([
        new TorusGeometry(p.radius, t / 2, 8, Math.max(4, Math.round((96 * p.angle) / 360)), (p.angle * Math.PI) / 180),
        ...ticks(p.radius, p.length, Math.round(p.ticks), p.angle, t * 0.8),
      ])!;
    case 'grid': {
      const n = Math.round(p.cells);
      const bars: BufferGeometry[] = [];
      for (let i = 0; i <= n; i++) {
        const x = -p.width / 2 + (p.width * i) / n;
        const y = -p.height / 2 + (p.height * i) / n;
        bars.push(bar(new Vector3(x, -p.height / 2, 0), new Vector3(x, p.height / 2, 0), t, 4));
        bars.push(bar(new Vector3(-p.width / 2, y, 0), new Vector3(p.width / 2, y, 0), t, 4));
      }
      return mergeGeometries(bars)!;
    }
    case 'sphere':
      solid = new SphereGeometry(p.radius, seg, Math.max(2, Math.round(seg / 2)));
      break;
    case 'box':
      solid = new BoxGeometry(p.width, p.height, p.depth);
      break;
    case 'cylinder':
      solid = new CylinderGeometry(p.radius, p.radius, p.height, seg);
      break;
    case 'cone':
      solid = new ConeGeometry(p.radius, p.height, seg);
      break;
    case 'panel':
    case 'image':
      // A thin slab, so it shows from both sides (the game culls back faces).
      solid = new BoxGeometry(p.width, p.height, 0.0002);
      break;
  }
  return s.wire && SHAPES[s.kind].wire ? wireframe(solid, s.kind === 'panel' ? 0.0008 : t) : solid;
}

/** Builds the shape as a GLB under the data dir and imports it: the attachment's mesh and its 3D-view model. */
export async function buildShape(id: string, s: Shape): Promise<{ forgeskinmesh: string; glb: string }> {
  const mesh = new Mesh(shapeGeometry(s), new MeshStandardMaterial({ color: 0xffffff }));
  const glb = (await new GLTFExporter().parseAsync(mesh, { binary: true })) as ArrayBuffer;
  mesh.geometry.dispose();
  const path = `${useStore.getState().dataDir}\\shapes\\${id}.glb`;
  await window.forge.writeFile(path, new Uint8Array(glb));
  return call('mesh.import', { glb: path });
}

/**
 * Adds the shape on the selected part as a hologram in the skin's own colour (hologram.skinAccent), or as a solid charm.
 * `params` and `wire` change the defaults of newShape; `init` finishes the attachment in the same undo step (its name,
 * part, pose, look). Resolves to the new attachment's id, or null when the model could not be made.
 */
export async function addShape(kind: ShapeKind, charm = false, over: { params?: Record<string, number>; wire?: boolean; set?: HoloSet; init?: (a: Attachment) => void } = {}): Promise<string | null> {
  const s = useStore.getState();
  const id = uid();
  const base = newShape(kind);
  const shape: Shape = { ...base, wire: charm ? false : (over.wire ?? base.wire), params: { ...base.params, ...over.params }, ...(over.set && { set: over.set }) };
  try {
    const mesh = await buildShape(id, shape);
    s.addAttachment(
      'mesh',
      mesh,
      (a, editor) => {
        a.material!.color = skinAccent(s.doc);
        if (charm) hangAsCharm(a);
        else a.hologram = newHologram();
        editor.shapes = { ...((editor.shapes as Record<string, Shape>) ?? {}), [id]: shape };
        over.init?.(a);
      },
      id,
    );
    return id;
  } catch (e) {
    s.fail(e);
    return null;
  }
}

/** Longest side of a picture made into a hologram: the side the game can make a texture of (Core Dds.MaxSide). */
const MAX_PICTURE = 16384;
export const PICTURE_TYPES = ['png', 'jpg', 'jpeg', 'webp'];

/**
 * A picture as a hologram screen: an image plane (a shape, so its size stays editable) with the picture, copied as a PNG
 * beside the skin, as its base colour; a hologram shows that as its glow, in the attachment colour (white keeps the
 * picture's own colours). `width` in metres; the height follows the picture. Resolves to the attachment's id.
 */
export async function addImageHologram(file?: string, width = 0.03): Promise<string | null> {
  const [path] = file ? [file] : await window.forge.openDialog('file', [{ name: t('shapes.pictures'), extensions: PICTURE_TYPES }]);
  if (!path) return null;
  const s = useStore.getState();
  let out: string;
  let aspect: number;
  try {
    const img = await loadImage(path);
    const k = Math.min(1, MAX_PICTURE / Math.max(img.naturalWidth, img.naturalHeight));
    const c = Object.assign(document.createElement('canvas'), { width: Math.max(1, Math.round(img.naturalWidth * k)), height: Math.max(1, Math.round(img.naturalHeight * k)) });
    c.getContext('2d')!.drawImage(img, 0, 0, c.width, c.height);
    out = `${dirName(s.projectPath ?? autosavePath(s.weaponId!))}\\images\\${uid()}.png`;
    await writePng(out, c);
    aspect = c.height / c.width;
  } catch (e) {
    s.fail(e, t('shapes.pictureFailed', { v0: baseName(path) }));
    return null;
  }
  return addShape('image', false, {
    params: { width, height: width * aspect },
    init: (a) => {
      a.name = baseName(path).replace(/\.[^.]+$/, '');
      a.material = { ...a.material, textures: { albedo: out }, color: [1, 1, 1, 1] };
    },
  });
}

export const shapeOf = (editor: unknown, id: string): Shape | undefined => (editor as { shapes?: Record<string, Shape> } | undefined)?.shapes?.[id];

const pending = new Map<string, number>();

/** Changes a shape's parameters at once and rebuilds its model shortly after the last change. */
export function setShape(id: string, shape: Shape, key: string): void {
  const s = useStore.getState();
  s.edit((d) => {
    const ed = d.editor as Record<string, unknown>;
    ed.shapes = { ...((ed.shapes as Record<string, Shape>) ?? {}), [id]: shape };
  }, `${id}-shape-${key}`);
  clearTimeout(pending.get(id));
  pending.set(
    id,
    window.setTimeout(async () => {
      pending.delete(id);
      try {
        const mesh = await buildShape(id, shape);
        useStore.getState().edit((d) => {
          const a = d.attachments?.find((x) => x?.id === id);
          if (!a) return;
          a.mesh = mesh.forgeskinmesh;
          const ed = d.editor as Record<string, unknown>;
          ed.meshPreviews = { ...((ed.meshPreviews as Record<string, string>) ?? {}), [id]: mesh.glb };
        }, `${id}-shape-${key}`);
      } catch (e) {
        useStore.getState().fail(e);
      }
    }, 200),
  );
}
