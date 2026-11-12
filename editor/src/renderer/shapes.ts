// Built-in shapes (PLAN §3.1 Shapes): three.js geometry from a few plain parameters, written as a GLB and imported like any
// GLB attachment, so the pack and the plugin see an ordinary mesh. A wireframe is thin tubes along the shape's edges.
import {
  BoxGeometry,
  BufferGeometry,
  ConeGeometry,
  CylinderGeometry,
  EdgesGeometry,
  Mesh,
  MeshStandardMaterial,
  Quaternion,
  SphereGeometry,
  TorusGeometry,
  Vector3,
} from 'three';
import { GLTFExporter } from 'three/examples/jsm/exporters/GLTFExporter.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { call } from './protocol';
import { t } from './i18n';
import { uid, useStore } from './store';

export type ShapeKind = 'line' | 'sphere' | 'ring' | 'arc' | 'box' | 'grid' | 'cylinder' | 'cone' | 'panel';

/** A parameter: `mm` lengths are stored in metres and shown in millimetres. */
export type ParamKey = 'length' | 'thickness' | 'radius' | 'segments' | 'angle' | 'width' | 'height' | 'depth' | 'cells';

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
};

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

/** The shape's geometry in metres, three.js space (Y up), centred on the origin. */
export function shapeGeometry(s: Shape): BufferGeometry {
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

const shapeLabel = (kind: ShapeKind) => t(`shapes.${kind}`);

/** Adds the shape on the selected part as a cyan hologram (the look most people want from a shape). */
export async function addShape(kind: ShapeKind): Promise<void> {
  const s = useStore.getState();
  const id = uid();
  const shape = newShape(kind);
  try {
    const mesh = await buildShape(id, shape);
    s.addAttachment('mesh', mesh, (a, editor) => {
      a.name = shapeLabel(kind);
      a.position = [0, 0.03, 0];
      a.material!.color = [0.45, 0.9, 1, 1];
      a.hologram = { flicker: 0.3 };
      editor.shapes = { ...((editor.shapes as Record<string, Shape>) ?? {}), [id]: shape };
    }, id);
  } catch (e) {
    s.fail(e);
  }
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
