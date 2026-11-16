// The structured control surface of an editor started with --ai-control: `window.forgeAI` (installAiControl), called through
// the DevTools protocol (Runtime.evaluate with awaitPromise). It goes through the same store actions as the UI, so
// validation, one undo step per command and autosave apply. Every call answers { ok: true, ... } or
// { ok: false, code, message }. Operating guide: Mods/ForgeSkin/AI-GUIDE.md.
import { Raycaster, Vector3, type Mesh, type Material } from 'three';
import { applyToGame, buildPack, checkSkins, importMesh, problems } from './actions';
import { loadImage } from './frames';
import { addLivery, isProjected, liveryLayer, moveLivery, removeLivery, setFinish, updateLivery } from './livery';
import { attachedOf, call, type DynamicLayer, type Methods, type LiveryKind, type SkinDocument, type TextureChannel } from './protocol';
import { allFinite, currentItem, materialOf, uid, useStore, type Selection } from './store';
import { decalPose, meshesOf } from './viewport/liveryGpu';
import { drawnMeshes, gearSurfaceAt, viewRefs } from './viewport/refs';
import { setView, type ViewName } from './viewport/Viewport';
import { addImageHologram, addShape, MENU_SHAPES, PICTURE_TYPES, SHAPES, type ShapeKind } from './shapes';
import { addHoloSet, HOLO_SETS, type HoloSet } from './holoSets';
import { hologramEffects } from './hologram';
import { placeOnSurface } from './viewport/ModalTransform';

class AiError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}
const fail = (code: string, message: string): never => {
  throw new AiError(code, message);
};
const st = () => useStore.getState();
type Args = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const need = <T>(args: Args, key: string, check: (v: unknown) => boolean = (v) => v !== undefined && v !== null): T => {
  if (!check(args[key])) fail('bad-args', `argument "${key}" is missing or has the wrong type`);
  return args[key] as T;
};
const isStr = (v: unknown) => typeof v === 'string' && v !== '';
/** A file the skin names: the document holds absolute paths only (a project file's relative ones are resolved by project.load). */
const isFile = (v: unknown) => typeof v === 'string' && /^([A-Za-z]:[\\/]|\\\\)/.test(v);
const file = (v: unknown): string => (isFile(v) ? (v as string) : fail('bad-path', `${String(v)}: a file must be an absolute path`));
const isNum = (v: unknown) => typeof v === 'number' && Number.isFinite(v);
const isVec = (n: number) => (v: unknown) => Array.isArray(v) && v.length === n && v.every(isNum);

const frames = async (n = 2) => {
  for (let i = 0; i < n; i++) await new Promise((r) => (requestAnimationFrame(r), setTimeout(r, 250))); // a window that draws no frames must not hang the call
};

/** Waits until the open weapon's model and the 3D view are up. */
async function viewReady(ms = 30_000) {
  for (const end = performance.now() + ms; !(viewRefs.root && viewRefs.controls && viewRefs.camera && !st().previewLoading); ) {
    if (performance.now() > end) fail('no-view', 'the 3D view is not ready (open a weapon first)');
    await new Promise((r) => setTimeout(r, 100));
  }
}

// ---- generic document access: paths -----------------------------------------------------------------------------------

type Seg = string | number;
/** "materials/id=ab12/livery/0/opacity" or an array of segments. `key=value` picks the array element whose key has that value. */
function segments(path: unknown): Seg[] {
  const list = Array.isArray(path) ? path : typeof path === 'string' ? path.split('/') : fail('bad-path', 'path must be a string or an array');
  return (list as Seg[]).filter((s) => s !== '').map((s) => (typeof s === 'string' && /^\d+$/.test(s) ? Number(s) : s));
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function step(node: any, seg: Seg, where: string): Seg {
  if (node === null || typeof node !== 'object') return fail('bad-path', `${where}: not an object or array`);
  if (typeof seg === 'string' && seg.includes('=') && Array.isArray(node)) {
    const [k, ...v] = seg.split('=');
    const i = node.findIndex((e: any) => e && String(e[k]) === v.join('=')); // eslint-disable-line @typescript-eslint/no-explicit-any
    return i < 0 ? fail('not-found', `${where}: no element with ${seg}`) : i;
  }
  return seg;
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function walk(root: any, segs: Seg[]): { parent: any; key: Seg } {
  if (!segs.length) fail('bad-path', 'empty path');
  if (segs[0] === 'id') fail('bad-path', 'the skin id cannot be changed');
  let node = root;
  for (let i = 0; i < segs.length - 1; i++) {
    const k = step(node, segs[i], segs.slice(0, i + 1).join('/'));
    node = node[k];
    if (node === undefined) fail('not-found', `${segs.slice(0, i + 1).join('/')} does not exist`);
  }
  const last = segs.at(-1)!;
  return { parent: node, key: Array.isArray(node) && last === '-' ? '-' : step(node, last, segs.join('/')) };
}
function readPath(doc: unknown, path: unknown) {
  let node: any = doc; // eslint-disable-line @typescript-eslint/no-explicit-any
  for (const s of segments(path)) {
    const k = step(node, s, String(path));
    node = node[k];
    if (node === undefined) fail('not-found', `${String(path)} does not exist`);
  }
  return node;
}

interface Op {
  op: 'set' | 'add' | 'remove' | 'merge';
  path: string | Seg[];
  value?: unknown;
}
function applyOp(doc: SkinDocument, o: Op) {
  if (o.op !== 'remove' && !allFinite(o.value)) fail('bad-value', `${o.op} ${String(o.path)}: the value holds NaN or Infinity`);
  const { parent, key } = walk(doc, segments(o.path));
  switch (o.op) {
    case 'set':
      if (key === '-') fail('bad-path', '"-" only works with add');
      parent[key] = structuredClone(o.value);
      break;
    case 'add':
      if (!Array.isArray(parent)) parent[key] = structuredClone(o.value);
      else if (key === '-') parent.push(structuredClone(o.value));
      else parent.splice(key as number, 0, structuredClone(o.value));
      break;
    case 'remove':
      if (key === '-') fail('bad-path', '"-" only works with add');
      if (Array.isArray(parent)) parent.splice(key as number, 1);
      else delete parent[key];
      break;
    case 'merge':
      if (parent[key] === null || typeof parent[key] !== 'object' || typeof o.value !== 'object') fail('bad-path', `${String(o.path)}: merge needs an object on both sides`);
      Object.assign(parent[key], structuredClone(o.value));
      break;
    default:
      fail('bad-args', `unknown op "${String((o as Op).op)}" (set, add, remove, merge)`);
  }
}

/** All ops in one edit: one undo step, one autosave; any bad op leaves the document untouched. The backend first checks the
 * result as it checks every document it gets, so a file named by a relative path (a project file's text) never gets in. */
async function patch(ops: Op[], key?: string) {
  if (!Array.isArray(ops) || !ops.length) fail('bad-args', 'ops must be a non-empty array');
  const next = structuredClone(st().doc);
  ops.forEach((o) => applyOp(next, o));
  await call('project.files', { project: next });
  const before = st().doc;
  st().edit((d) => ops.forEach((o) => applyOp(d, o)), key);
  return { changed: st().doc !== before };
}

// ---- UV space and decal placement ---------------------------------------------------------------------------------------

/** Triangles of the meshes drawn with `material`: three vertex indices each, with the mesh. */
function* triangles(material: string): Generator<{ mesh: Mesh; a: number; b: number; c: number }> {
  for (const mesh of meshesOf(material)) {
    const uv = mesh.geometry.getAttribute('uv');
    if (!uv) continue;
    const index = mesh.geometry.index;
    const count = index ? index.count : uv.count;
    const list = Array.isArray(mesh.material) ? (mesh.material as Material[]) : null;
    const ranges = list ? mesh.geometry.groups.filter((g) => list[g.materialIndex ?? 0]?.name === material).map((g) => [g.start, Math.min(count, g.start + g.count)]) : [[0, count]];
    const at = (i: number) => (index ? index.getX(i) : i);
    for (const [s, e] of ranges) for (let i = s; i + 2 < e; i += 3) yield { mesh, a: at(i), b: at(i + 1), c: at(i + 2) };
  }
}

/** Where the UV point lies on the model: world point and outward surface normal (three.js world space), or null. UVs may overlap
 * (mirrored or hidden faces): the first spot nothing covers wins, else the first spot, with `covered` set. */
function surfaceAtUv(material: string, u: number, v: number) {
  const spots = [...spotsAtUv(material, u, v)];
  const meshes = drawnMeshes();
  const ray = new Raycaster();
  const hits = (from: Vector3, dir: Vector3, far: number) => ((ray.far = far), ray.set(from, dir), ray.intersectObjects(meshes, false));
  // A mesh whose winding is mirrored gives an inward normal; the outward side is the one from which the surface is seen.
  for (const s of spots) if (!hits(s.point.clone().addScaledVector(s.normal, 0.003), s.normal.clone().negate(), 0.0045).length) s.normal.negate();
  const free = spots.find((s) => !hits(s.point.clone().addScaledVector(s.normal, 0.002), s.normal, 100).length);
  const spot = free ?? spots[0];
  return spot && { ...spot, covered: !free };
}
function* spotsAtUv(material: string, u: number, v: number) {
  for (const { mesh, a, b, c } of triangles(material)) {
    const uv = mesh.geometry.getAttribute('uv');
    const [ax, ay, bx, by, cx, cy] = [uv.getX(a), uv.getY(a), uv.getX(b), uv.getY(b), uv.getX(c), uv.getY(c)];
    const den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
    if (Math.abs(den) < 1e-12) continue;
    const l1 = ((by - cy) * (u - cx) + (cx - bx) * (v - cy)) / den;
    const l2 = ((cy - ay) * (u - cx) + (ax - cx) * (v - cy)) / den;
    const l3 = 1 - l1 - l2;
    const eps = -1e-6;
    if (l1 < eps || l2 < eps || l3 < eps) continue;
    mesh.updateWorldMatrix(true, false);
    const pos = mesh.geometry.getAttribute('position');
    const [pa, pb, pc] = [a, b, c].map((i) => new Vector3().fromBufferAttribute(pos, i).applyMatrix4(mesh.matrixWorld));
    const point = pa.clone().multiplyScalar(l1).addScaledVector(pb, l2).addScaledVector(pc, l3);
    // The normal as three.js gives a ray hit's face ((c - b) x (a - b) in the mesh's space), turned into world space the way the
    // 3D view's drag does (a mirrored node would flip a normal computed from the world positions).
    const local = [a, b, c].map((i) => new Vector3().fromBufferAttribute(pos, i));
    const normal = local[2].clone().sub(local[1]).cross(local[0].clone().sub(local[1])).normalize().transformDirection(mesh.matrixWorld);
    yield { point, normal };
  }
}

/** Connected pieces of the material's UV layout (triangles sharing a UV corner), each with its outline loops. */
function uvIslands(material: string) {
  const key = (u: number, v: number) => `${Math.round(u * 1e5)},${Math.round(v * 1e5)}`;
  const tris: { k: string[]; p: number[][] }[] = [];
  for (const { mesh, a, b, c } of triangles(material)) {
    const uv = mesh.geometry.getAttribute('uv');
    const p = [a, b, c].map((i) => [uv.getX(i), uv.getY(i)]);
    tris.push({ k: p.map(([u, v]) => key(u, v)), p });
  }
  const parent = new Map<string, string>();
  const find = (k: string): string => {
    let r = k;
    while ((parent.get(r) ?? r) !== r) r = parent.get(r)!;
    parent.set(k, r);
    return r;
  };
  for (const t of tris) for (const k of t.k) if (!parent.has(k)) parent.set(k, k);
  for (const t of tris) {
    t.k.slice(1).forEach((k) => parent.set(find(k), find(t.k[0])));
  }
  const groups = new Map<string, typeof tris>();
  for (const t of tris) groups.set(find(t.k[0]), [...(groups.get(find(t.k[0])) ?? []), t]);
  const r4 = (n: number) => Math.round(n * 1e4) / 1e4;
  return [...groups.values()]
    .map((list, id) => {
      // Boundary edges are the ones one triangle owns; chain them into loops.
      const edges = new Map<string, { a: number[]; b: number[]; n: number }>();
      let area = 0;
      const box = [Infinity, Infinity, -Infinity, -Infinity];
      for (const t of list) {
        area += Math.abs((t.p[1][0] - t.p[0][0]) * (t.p[2][1] - t.p[0][1]) - (t.p[2][0] - t.p[0][0]) * (t.p[1][1] - t.p[0][1])) / 2;
        for (let i = 0; i < 3; i++) {
          const [ka, kb] = [t.k[i], t.k[(i + 1) % 3]];
          const e = edges.get(ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`);
          if (e) e.n++;
          else edges.set(ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`, { a: t.p[i], b: t.p[(i + 1) % 3], n: 1 });
          box[0] = Math.min(box[0], t.p[i][0]);
          box[1] = Math.min(box[1], t.p[i][1]);
          box[2] = Math.max(box[2], t.p[i][0]);
          box[3] = Math.max(box[3], t.p[i][1]);
        }
      }
      const open = [...edges.values()].filter((e) => e.n === 1);
      const byStart = new Map<string, typeof open>();
      for (const e of open) for (const p of [e.a, e.b]) byStart.set(key(p[0], p[1]), [...(byStart.get(key(p[0], p[1])) ?? []), e]);
      const used = new Set<(typeof open)[number]>();
      const outlines: number[][] = [];
      for (const first of open) {
        if (used.has(first)) continue;
        used.add(first);
        const line = [...first.a, ...first.b];
        for (let again = true; again; ) {
          again = false;
          const end = key(line[line.length - 2], line[line.length - 1]);
          const next = byStart.get(end)?.find((e) => !used.has(e));
          if (next) {
            used.add(next);
            const far = key(next.a[0], next.a[1]) === end ? next.b : next.a;
            line.push(...far);
            again = true;
          }
        }
        outlines.push(line.map(r4));
      }
      // A point certainly inside the island (the middle of its first triangle), for placeDecal.
      const sample = [0, 1].map((k) => r4((list[0].p[0][k] + list[0].p[1][k] + list[0].p[2][k]) / 3));
      return { id, triangles: list.length, area: r4(area), bbox: box.map(r4), sample, outlines };
    })
    .sort((x, y) => y.area - x.area)
    .map((x, i) => ({ ...x, id: i }));
}

// ---- state and camera ------------------------------------------------------------------------------------------------------

const cameraState = () => {
  const c = viewRefs.controls;
  if (!c) return null;
  const pos = c.getPosition(new Vector3());
  const target = c.getTarget(new Vector3());
  const r4 = (n: number) => Math.round(n * 1e4) / 1e4;
  return {
    yaw: r4((c.azimuthAngle * 180) / Math.PI),
    pitch: r4(90 - (c.polarAngle * 180) / Math.PI),
    distance: r4(c.distance),
    target: target.toArray().map(r4),
    position: pos.toArray().map(r4),
    orthographic: !!(viewRefs.camera as { isOrthographicCamera?: boolean } | null)?.isOrthographicCamera,
  };
};

function snapshot() {
  const s = st();
  const item = currentItem();
  return {
    weaponId: s.weaponId,
    skinId: s.doc.id,
    projectPath: s.projectPath,
    selection: s.selection,
    busy: s.busy,
    ready: s.ready && !s.previewLoading,
    autosaveError: s.autosaveError,
    undoSteps: s.undo.length,
    redoSteps: s.redo.length,
    item: item && { id: item.id, name: item.name, slot: item.slot, parts: item.parts },
    skins: s.skins.map((k) => ({ id: k.id, name: k.name, path: k.path })),
    problems: item ? problems().map((p) => p.text) : [],
    camera: cameraState(),
    doc: s.doc,
  };
}

// ---- commands ----------------------------------------------------------------------------------------------------------------

type Handler = (a: Args) => unknown | Promise<unknown>;

const partOfMaterial = (material: string): string => {
  const own = materialOf(st().doc, material)?.part;
  if (own) return own;
  if (attachedOf(material)) return material;
  return currentItem()?.parts.find((p) => p.materials.includes(material))?.id ?? fail('not-found', `material "${material}" is not a material of the open item`);
};
const selectionOf = (a: Args): Selection => {
  const kind = need<string>(a, 'kind', isStr);
  const id = need<string>(a, 'id', isStr);
  return (kind === 'layer' || kind === 'livery' ? { kind, id, material: need<string>(a, 'material', isStr) } : kind === 'part' ? { kind, id, sub: a.sub ?? null } : { kind, id }) as Selection;
};
/** Id of what the last add selected. */
const picked = () => (st().selection as { id?: string } | null)?.id ?? null;

const COMMANDS: Record<string, { args: string; run: Handler }> = {
  select: {
    args: '{kind: part|attachment|layer|livery|sound|fx, id, material? (layer, livery), sub? (part)} or {clear: true}',
    run: (a) => (st().select(a.clear ? null : selectionOf(a)), { selection: st().selection }),
  },
  listWeapons: {
    args: '{query?}: id, name, slot of the catalog',
    run: (a) => ({
      weapons: st()
        .catalog.filter((c) => !a.query || `${c.id} ${c.name} ${c.nameZh}`.toLowerCase().includes(String(a.query).toLowerCase()))
        .map((c) => ({ id: c.id, name: c.name, slot: c.slot })),
    }),
  },
  selectWeapon: {
    args: '{id}: opens an item (waits for its 3D view)',
    run: async (a) => {
      const id = need<string>(a, 'id', isStr);
      if (!st().catalog.some((c) => c.id === id)) fail('not-found', `no item "${id}" (listWeapons)`);
      await st().selectWeapon(id);
      await viewReady();
      return { weaponId: st().weaponId, skinId: st().doc.id };
    },
  },
  switchSkin: { args: '{id}', run: async (a) => (await st().switchSkin(need(a, 'id', isStr)), { skinId: st().doc.id }) },
  newSkin: { args: '{copy?: bool}', run: async (a) => (await st().newSkin(!!a.copy), { skinId: st().doc.id }) },
  pickNext: {
    args: '{paths: absolute paths}: the answer of the next file dialog (open, folder or save), which then does not open',
    run: async (a) => {
      const paths = need<unknown[]>(a, 'paths', (v) => Array.isArray(v) && v.length > 0).map(file);
      await window.forge.presetPaths(paths);
      return {};
    },
  },
  undo: { args: '{}', run: () => (st().undoEdit(), { undoSteps: st().undo.length }) },
  redo: { args: '{}', run: () => (st().redoEdit(), { redoSteps: st().redo.length }) },
  get: { args: '{path}: value at a document path', run: (a) => ({ value: readPath(st().doc, need(a, 'path')) }) },
  patch: { args: '{ops: [{op: set|add|remove|merge, path, value?}], key?}: any document field, one undo step', run: (a) => patch(need<Op[]>(a, 'ops', Array.isArray), a.key) },
  addLivery: {
    args: '{material, kind: fill|decal|brush|pattern|wear|trim|text|gradient|tile, part?, image?}: a paint layer (a decal needs image, an absolute path)',
    run: async (a) => {
      const material = need<string>(a, 'material', isStr);
      const kind = need<LiveryKind>(a, 'kind', isStr);
      const part = a.part ?? partOfMaterial(material);
      let aspect = 1;
      if (a.image !== undefined) file(a.image);
      if (kind === 'decal') {
        const img = await loadImage(need<string>(a, 'image', isStr)).catch(() => fail('bad-image', `cannot load ${a.image}`));
        aspect = img.naturalHeight / Math.max(1, img.naturalWidth);
      }
      addLivery(material, part, kind, a.image, aspect);
      return { id: picked() };
    },
  },
  removeLivery: { args: '{material, id}', run: (a) => (liveryOf(a), removeLivery(a.material, a.id), {}) },
  moveLivery: { args: '{material, id, by: 1|-1}', run: (a) => (liveryOf(a), moveLivery(a.material, a.id, need<1 | -1>(a, 'by', isNum)), {}) },
  setFinish: { args: '{material, finish|null, part?}', run: (a) => (setFinish(need(a, 'material', isStr), a.part ?? partOfMaterial(a.material), a.finish ?? null), {}) },
  addLayer: {
    args: '{material, part?, layer: DynamicLayer fields (kind, sources (absolute paths), text, ...)}: an animated layer',
    run: (a) => ((a.layer?.sources ?? []).forEach(file), st().addLayer(need(a, 'material', isStr), a.part ?? partOfMaterial(a.material), (a.layer ?? {}) as Partial<DynamicLayer>), { id: picked() }),
  },
  removeLayer: { args: '{material, id}', run: (a) => (st().removeLayer(need(a, 'material', isStr), need(a, 'id', isStr)), {}) },
  duplicate: {
    args: '{kind: livery|layer|attachment, id, material? (livery, layer), toMaterials?: [material]}: copies; toMaterials copies a paint or animated layer onto other materials',
    run: (a) => {
      const id = need<string>(a, 'id', isStr);
      if (a.kind === 'attachment') {
        if (!st().doc.attachments?.some((x) => x?.id === id)) fail('not-found', `no attachment ${id}`);
        st().duplicateAttachment(id);
        return { id: picked() };
      }
      const field = a.kind === 'livery' ? 'livery' : a.kind === 'layer' ? 'layers' : fail('bad-args', 'kind must be livery, layer or attachment');
      const from = need<string>(a, 'material', isStr);
      const src = (materialOf(st().doc, from) as any)?.[field]?.find((l: any) => l?.id === id); // eslint-disable-line @typescript-eslint/no-explicit-any
      if (!src) fail('not-found', `no ${a.kind} ${id} on ${from}`);
      const ids: Record<string, string> = {};
      st().edit((d) => {
        for (const to of (a.toMaterials as string[] | undefined) ?? [from]) {
          const m =
            d.materials?.find((x) => x?.material === to) ?? (to === from ? undefined : (d.materials!.push({ material: to, part: partOfMaterial(to), textures: {} } as never), d.materials!.at(-1)));
          if (!m) fail('not-found', `no material ${to}`);
          const list = ((m as any)[field] ??= []); // eslint-disable-line @typescript-eslint/no-explicit-any
          list.push({ ...structuredClone(src), id: (ids[to] = uid()) });
        }
      });
      return { ids };
    },
  },
  addAttachment: {
    args: `{kind: screen|mesh|shape|image, glb? (mesh: absolute path of a .glb/.gltf), shape? (shape: ${MENU_SHAPES.join('|')}), params?: {name: number} (shape, metres, counts, degrees), wire?: bool (shape), charm?: bool, image? (image: absolute path of a ${PICTURE_TYPES.join('/')} picture), width? (image, metres, default 0.03; the height follows the picture)}`,
    run: async (a) => {
      const before = st().doc.attachments?.length ?? 0;
      if (a.kind === 'screen') st().addAttachment('screen');
      else if (a.kind === 'mesh') await importMesh(!!a.charm, file(a.glb));
      else if (a.kind === 'image') {
        const pic = file(need<string>(a, 'image', (v) => isStr(v) && PICTURE_TYPES.some((x) => (v as string).toLowerCase().endsWith(`.${x}`))));
        if (a.width !== undefined && !(isNum(a.width) && a.width >= 0.001)) fail('bad-value', 'width must be a number of metres, at least 0.001');
        if (!(await addImageHologram(pic, a.width ?? 0.03))) fail('bad-image', `${pic} could not be read as a picture`);
      } else if (a.kind === 'shape') {
        const kind = need<ShapeKind>(a, 'shape', (v) => typeof v === 'string' && MENU_SHAPES.includes(v as ShapeKind));
        const params = (a.params ?? {}) as Record<string, number>;
        for (const [k, v] of Object.entries(params)) {
          const p = SHAPES[kind].params.find((x) => x.key === k) ?? fail('bad-args', `${kind} has no parameter "${k}" (${SHAPES[kind].params.map((x) => x.key).join(', ')})`);
          if (!isNum(v) || v < p.min || (p.max !== undefined && v > p.max)) fail('bad-value', `${k} must be a number from ${p.min}${p.max !== undefined ? ` to ${p.max}` : ''}`);
        }
        await addShape(kind, !!a.charm, { params, wire: a.wire === undefined ? undefined : !!a.wire });
      } else fail('bad-args', 'kind must be screen, mesh, shape or image');
      if ((st().doc.attachments?.length ?? 0) === before) fail('import-failed', 'the model could not be made or imported (see the editor log)');
      // An imported model's own pictures (PNG per channel, at most 2048) are on its material.
      return { id: picked(), textures: st().doc.attachments?.at(-1)?.material?.textures ?? {} };
    },
  },
  addHoloSet: {
    args: `{set: ${HOLO_SETS.join('|')}}: matching hologram shapes placed on the open item's parts (sight ticks, barrel tick rings, info strip); without the lit outline when the skin has too few code effects left`,
    run: async (a) => {
      await viewReady();
      const set = need<HoloSet>(a, 'set', (v) => HOLO_SETS.includes(v as HoloSet));
      const used = (st().doc.codeEffects?.length ?? 0) + hologramEffects(st().doc).length;
      const ids = await addHoloSet(set);
      if (!ids.length) fail('no-view', 'the open item has no model to place the set on');
      return { ids, outline: hologramEffects(st().doc).length + (st().doc.codeEffects?.length ?? 0) > used };
    },
  },
  placeOnSurface: {
    args: '{id (an attachment), view: [x, y] (0..1 across and down the 3D view) | point: [x,y,z] + normal: [x,y,z] (world), up?: [x,y,z] (default world +Y)}: lays the attachment flush on the gear like a click in the Snap to surface mode',
    run: async (a) => {
      await viewReady();
      const id = need<string>(a, 'id', isStr);
      if (!st().doc.attachments?.some((x) => x?.id === id)) fail('not-found', `no attachment ${id}`);
      let point: Vector3;
      let normal: Vector3;
      let part: string | null = null;
      let right: Vector3;
      let fallback: Vector3;
      if (a.view) {
        const [x, y] = need<number[]>(a, 'view', isVec(2));
        const canvas = viewRefs.three!().gl.domElement;
        const r = canvas.getBoundingClientRect();
        const at = gearSurfaceAt(canvas, r.left + x * r.width, r.top + y * r.height) ?? fail('no-hit', `nothing of the gear at view (${x}, ${y})`);
        ({ point, normal, part } = at);
        right = new Vector3().setFromMatrixColumn(viewRefs.camera!.matrixWorld, 0);
        fallback = new Vector3().setFromMatrixColumn(viewRefs.camera!.matrixWorld, 1);
      } else {
        point = new Vector3(...need<number[]>(a, 'point', isVec(3)));
        normal = new Vector3(...need<number[]>(a, 'normal', isVec(3))).normalize();
        right = new Vector3(...(a.up ? need<number[]>(a, 'up', isVec(3)) : [0, 1, 0])).cross(normal);
        fallback = new Vector3(1, 0, 0).cross(normal);
      }
      const pose = placeOnSurface(id, point, normal, right, fallback) ?? fail('no-view', `attachment ${id} is not in the 3D view (hidden, or its model is still loading)`);
      return { position: pose.position, rotation: pose.rotation, worldPoint: point.toArray(), worldNormal: normal.toArray(), part };
    },
  },
  removeAttachment: { args: '{id}', run: (a) => (st().removeAttachment(need(a, 'id', isStr)), {}) },
  setTexture: {
    args: '{material, part?, channel: albedo|normal|metalSmooth|emissive, path (absolute)|null}',
    run: (a) => (st().setTexture(need(a, 'material', isStr), a.part, need<TextureChannel>(a, 'channel', isStr), a.path == null ? null : file(a.path)), {}),
  },
  setPartEnabled: { args: '{part, on}', run: (a) => (st().setPartEnabled(need(a, 'part', isStr), !!a.on), {}) },
  setPartTransform: {
    args: "{part, path?, position?, rotation?, scale?, visible?}: a part's layout (Unity space)",
    run: (a) => (
      st().setPartTransform(
        need(a, 'part', isStr),
        a.path ?? null,
        (t) => void Object.assign(t, Object.fromEntries(['position', 'rotation', 'scale', 'visible'].filter((k) => k in a).map((k) => [k, a[k]]))),
      ),
      {}
    ),
  },
  placeDecal: {
    args: '{material, id (a decal, panel-lines or text layer), uv: [u, v] | point: [x,y,z] + normal: [x,y,z] (world), up?: [x,y,z] (default world +Y), size?: [w,h], angle?, depth?}: puts the decal on the surface like a drag in the 3D view',
    run: async (a) => {
      await viewReady();
      const layer = liveryOf(a);
      if (!isProjected(layer)) fail('bad-args', `layer ${a.id} is a ${layer.kind}, not a projected layer (decal, panel lines or text)`);
      let point: Vector3;
      let normal: Vector3;
      let covered = false;
      if (a.uv) {
        const [u, v] = need<number[]>(a, 'uv', isVec(2));
        const hit = surfaceAtUv(a.material, u, v) ?? fail('no-hit', `no surface of ${a.material} at uv (${u}, ${v}); uvIslands lists where the material has any`);
        ({ point, normal } = hit);
        covered = hit.covered;
      } else {
        point = new Vector3(...need<number[]>(a, 'point', isVec(3)));
        normal = new Vector3(...need<number[]>(a, 'normal', isVec(3))).normalize();
      }
      const up = new Vector3(...(a.up ? need<number[]>(a, 'up', isVec(3)) : [0, 1, 0]));
      const right = up.clone().cross(normal);
      const pose = decalPose(materialOf(st().doc, a.material)?.part, point, normal, right, new Vector3(1, 0, 0).cross(normal));
      updateLivery(a.material, a.id, (l) => {
        l.position = pose.position;
        l.rotation = pose.rotation;
        if (a.size) l.size = need<number[]>(a, 'size', isVec(2));
        if (isNum(a.angle)) l.angle = a.angle;
        if (isNum(a.depth)) l.depth = a.depth;
      });
      return { position: pose.position, rotation: pose.rotation, worldPoint: point.toArray(), worldNormal: normal.toArray(), covered };
    },
  },
  importStrokes: {
    args: '{material, id (a brush layer, or any layer for a mask), strokes?, mask?, fills?, maskFills?, append?}: strokes are polylines of (u, v, width) triples in UV space, fills are [u, v] island seeds',
    run: (a) => {
      liveryOf(a);
      const lists = ['strokes', 'mask'] as const;
      for (const k of lists) {
        if (a[k] === undefined) continue;
        if (!Array.isArray(a[k]) || !a[k].every((s: unknown) => Array.isArray(s) && s.length >= 3 && s.length % 3 === 0 && s.every(isNum)))
          fail('bad-args', `${k} must be a list of polylines of (u, v, width) triples`);
      }
      for (const k of ['fills', 'maskFills'] as const) if (a[k] !== undefined && !(Array.isArray(a[k]) && a[k].every(isVec(2)))) fail('bad-args', `${k} must be a list of [u, v]`);
      updateLivery(a.material, a.id, (l) => {
        for (const k of [...lists, 'fills', 'maskFills'] as const) if (a[k] !== undefined) (l as any)[k] = a.append ? [...((l as any)[k] ?? []), ...a[k]] : a[k]; // eslint-disable-line @typescript-eslint/no-explicit-any
      });
      return {};
    },
  },
  uvIslands: {
    args: '{material}: the UV pieces of a material: id, triangles, area, bbox [u0,v0,u1,v1], outlines (polylines of u,v pairs); uv v runs as in the texture files (v 0 = top row)',
    run: async (a) => (await viewReady(), { islands: uvIslands(need(a, 'material', isStr)) }),
  },
  camera: {
    args: '{view?: front|back|left|right|top|bottom|iso, yaw?, pitch? (degrees), distance?, target?: [x,y,z] (three.js world)}: sets (no argument reads) the 3D camera without animation',
    run: async (a) => {
      await viewReady();
      const c = viewRefs.controls!;
      if (a.view) setView(a.view as ViewName, false);
      if (a.yaw !== undefined || a.pitch !== undefined)
        await c.rotateTo(((a.yaw ?? (c.azimuthAngle * 180) / Math.PI) * Math.PI) / 180, ((90 - (a.pitch ?? 90 - (c.polarAngle * 180) / Math.PI)) * Math.PI) / 180, false);
      if (a.target) await c.setTarget(...need<[number, number, number]>(a, 'target', isVec(3)), false);
      if (a.distance !== undefined) await c.dollyTo(need<number>(a, 'distance', isNum), false);
      await frames();
      return { camera: cameraState() };
    },
  },
  render: {
    args: '{width?, height?, path?}: the 3D view as a PNG data URL (width alone scales the view; width and height render it at that size and shape; path saves it inside the data folder)',
    run: async (a) => {
      await viewReady();
      const src = document.querySelector<HTMLCanvasElement>('canvas[data-engine]') ?? fail('no-view', 'the 3D canvas is not on screen');
      const width = isNum(a.width) ? a.width : src.width;
      const height = isNum(a.height) ? a.height : Math.round((width * src.height) / src.width);
      const out = Object.assign(document.createElement('canvas'), { width, height });
      // Both sides given: the view itself (its camera, effects and drawing buffer) takes that size for the shot, then goes back.
      const three = isNum(a.width) && isNum(a.height) ? viewRefs.three?.() : undefined;
      const was = three && { ...three.size };
      if (three) three.setSize(width / three.viewport.dpr, height / three.viewport.dpr, was!.top, was!.left);
      try {
        await frames(3);
        out.getContext('2d')!.drawImage(src, 0, 0, width, height);
      } finally {
        if (was) three!.setSize(was.width, was.height, was.top, was.left);
      }
      const dataUrl = out.toDataURL('image/png');
      if (a.path)
        await window.forge.writeFile(
          a.path,
          Uint8Array.from(atob(dataUrl.split(',')[1]), (ch) => ch.charCodeAt(0)),
        );
      return { width, height, dataUrl, camera: cameraState() };
    },
  },
  call: { args: '{method, params?}: a backend method (see protocol.ts)', run: async (a) => ({ result: await call(need<keyof Methods>(a, 'method', isStr), a.params ?? {}) }) },
  check: { args: '{}: what must be fixed before a build', run: async () => ({ skins: await checkSkins() }) },
  buildPack: { args: '{out}: builds the open skin into a .forgeskin at an absolute path', run: async (a) => ({ built: await buildPack(need(a, 'out', isStr)) }) },
  applyToGame: { args: '{}: builds and installs into the chosen test profile', run: async () => (await applyToGame(), { busy: st().busy }) },
  state: { args: '{}: open item, skin, selection, parts, problems, camera and the whole skin document', run: () => snapshot() },
  help: { args: '{}', run: () => ({ commands: Object.fromEntries(Object.entries(COMMANDS).map(([k, v]) => [k, v.args])) }) },
};

/** The livery layer an argument pair names (material, id), or an error. */
function liveryOf(a: Args) {
  return liveryLayer(st().doc, need(a, 'material', isStr), need(a, 'id', isStr)) ?? fail('not-found', `no paint layer ${a.id} on ${a.material}`);
}

async function run(name: string, args: Args = {}): Promise<Record<string, unknown>> {
  try {
    if (name === 'batch') {
      const results: Record<string, unknown>[] = [];
      for (const c of need<{ command: string; args?: Args }[]>(args, 'commands', Array.isArray)) {
        const r = await run(c.command, c.args);
        results.push(r);
        if (!r.ok) return { ok: false, code: 'batch-stopped', message: `command ${results.length} (${c.command}) failed: ${r.message}`, results };
      }
      return { ok: true, results };
    }
    const cmd = COMMANDS[name] ?? fail('unknown-command', `no command "${name}"; command('help') lists them`);
    if (!st().ready && name !== 'help') fail('not-ready', 'the editor is still starting');
    const before = st().undo.length;
    const out = (await cmd.run(args)) as Record<string, unknown> | undefined;
    return { ok: true, ...out, undoSteps: Math.max(0, st().undo.length - before) };
  } catch (e) {
    if (e instanceof AiError) return { ok: false, code: e.code, message: e.message };
    return { ok: false, code: 'failed', message: e instanceof Error ? e.message : JSON.stringify(e) };
  }
}

export function installAiControl() {
  Object.assign(window, {
    forgeAI: {
      state: () => run('state'),
      command: run,
      patch: (ops: Op[], key?: string) => run('patch', { ops, key }),
      get: (path: string) => run('get', { path }),
    },
  });
}
