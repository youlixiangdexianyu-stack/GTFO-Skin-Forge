// The livery's UV-space renderer (livery.ts): every mesh that uses a material is drawn at its UVs into an offscreen target
// on the 3D view's own renderer, with a program per layer kind. Decals take each fragment's position into the decal's
// frame (inside its width, height and depth, on a face that looks at the decal, it gets the image); patterns and wear
// (viewport/uvPrograms) are computed from the fragment's position, normal and curvature in the item's root space.
import {
  BufferAttribute,
  Color,
  Euler,
  DoubleSide,
  Matrix3,
  Matrix4,
  Mesh,
  MeshBasicMaterial,
  NoBlending,
  NoColorSpace,
  OrthographicCamera,
  Quaternion,
  Scene,
  ShaderMaterial,
  Texture,
  Vector3,
  Vector4,
  WebGLRenderTarget,
  type BufferGeometry,
  type Material,
  type Object3D,
  type WebGLRenderer,
} from 'three';
import type { UvRenderer } from '../livery';
import { PATTERNS, PATTERN_FRAGMENT, UV_VERTEX, WEAR_FRAGMENT } from './uvPrograms';
import type { LiveryLayer } from '../protocol';
import { unityToThree, type Vec3 } from '../space';
import { anchorObject, viewRefs } from './refs';

let renderer: WebGLRenderer | null = null;
/** The 3D view hands its renderer over when it mounts (viewport/LiveryTool). */
export const setLiveryRenderer = (r: WebGLRenderer | null) => void (renderer = r);

const DEG = Math.PI / 180;

/**
 * The decal volume in the anchor's three.js space: the unit box x, y in [-0.5, 0.5], z in [-1, 1] maps to the decal's
 * width, height and depth. Unity +Z of the frame (the surface normal) is three -Z, so the image faces -Z and projects along +Z.
 */
export function decalMatrix(l: LiveryLayer): Matrix4 {
  const pose = unityToThree({ position: (l.position ?? [0, 0, 0]) as Vec3, rotation: (l.rotation ?? [0, 0, 0]) as Vec3, scale: [1, 1, 1] });
  const q = pose.quaternion.multiply(new Quaternion().setFromAxisAngle(new Vector3(0, 0, 1), (l.angle ?? 0) * DEG));
  const [w, h] = l.size ?? [0.04, 0.04];
  return new Matrix4().compose(pose.position, q, new Vector3(w, h, Math.max(1e-4, l.depth ?? 0.01)));
}

/** Meshes drawn with `material`, with the materials list the bake uses for each (others hidden). */
export function meshesOf(material: string): Mesh[] {
  const out: Mesh[] = [];
  viewRefs.root?.traverse((o) => {
    const m = o as Mesh;
    if (!m.isMesh || o.userData.sfUnmapped) return;
    const list = (Array.isArray(m.material) ? m.material : [m.material]) as Material[];
    if (list.some((x) => x.name === material)) out.push(m);
  });
  return out;
}

/** The UV wireframe of every triangle drawn with `material`, stroked onto `ctx` (w x h, top-left origin like the textures). */
export function drawUvWireframe(material: string, ctx: CanvasRenderingContext2D, w: number, h: number) {
  ctx.beginPath();
  for (const m of meshesOf(material)) {
    const uv = m.geometry.getAttribute('uv');
    if (!uv) continue;
    const index = m.geometry.index;
    const count = index ? index.count : uv.count;
    const list = Array.isArray(m.material) ? (m.material as Material[]) : null;
    const ranges = list
      ? m.geometry.groups.filter((g) => list[g.materialIndex ?? 0]?.name === material).map((g) => [g.start, Math.min(count, g.start + g.count)])
      : [[0, count]];
    const at = (i: number) => (index ? index.getX(i) : i);
    for (const [start, end] of ranges)
      for (let i = start; i + 2 < end; i += 3) {
        const [a, b, c] = [at(i), at(i + 1), at(i + 2)];
        ctx.moveTo(uv.getX(a) * w, uv.getY(a) * h);
        ctx.lineTo(uv.getX(b) * w, uv.getY(b) * h);
        ctx.lineTo(uv.getX(c) * w, uv.getY(c) * h);
        ctx.closePath();
      }
  }
  ctx.stroke();
}

const decalProgram = new ShaderMaterial({
  uniforms: { image: { value: null }, tint: { value: new Color() }, alpha: { value: 1 }, toFrame: { value: new Matrix4() }, frameRot: { value: new Matrix3() }, mirror: { value: 1 } },
  vertexShader: /* glsl */ `
    uniform mat4 toFrame;
    uniform mat3 frameRot;
    varying vec3 vFrame;
    varying vec3 vNormal;
    void main() {
      vFrame = (toFrame * modelMatrix * vec4(position, 1.0)).xyz;
      vNormal = frameRot * mat3(modelMatrix) * normal;
      // UV space: v = 0 (the image's top row) at the bottom, which is row 0 of readPixels.
      gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
    }`,
  fragmentShader: /* glsl */ `
    uniform sampler2D image;
    uniform vec3 tint;
    uniform float alpha;
    uniform float mirror;
    varying vec3 vFrame;
    varying vec3 vNormal;
    void main() {
      if (abs(vFrame.x) > 0.5 || abs(vFrame.y) > 0.5 || abs(vFrame.z) > 1.0 || vNormal.z >= 0.0) discard;
      vec4 c = texture2D(image, vec2(0.5 + vFrame.x * mirror, 0.5 + vFrame.y));
      gl_FragColor = vec4(c.rgb * tint, c.a * alpha);
    }`,
  side: DoubleSide,
  depthTest: false,
  depthWrite: false,
  blending: NoBlending,
});
const hidden = new MeshBasicMaterial({ visible: false });
const camera = new OrthographicCamera();
// Uploaded decal images (a GIF's frames each), most recent last; the oldest are freed past the cap.
const textures = new Map<CanvasImageSource, Texture>();
const MAX_TEXTURES = 256;
let target: WebGLRenderTarget | null = null;
const out = { canvas: document.createElement('canvas') };

function imageTexture(img: CanvasImageSource): Texture {
  let t = textures.get(img);
  if (t) textures.delete(img);
  else {
    t = new Texture(img as HTMLImageElement);
    t.flipY = false; // image top row at v = 0, like the game's UVs
    t.colorSpace = NoColorSpace; // sampled and written as stored, no colour conversion
    t.needsUpdate = true;
  }
  textures.set(img, t);
  if (textures.size > MAX_TEXTURES) {
    const [oldest, old] = textures.entries().next().value!;
    old.dispose();
    textures.delete(oldest);
  }
  return t;
}

/**
 * Per-vertex curvature of a geometry, cached on it as the `curvature` attribute: vertices at the same position are one
 * point; for each, minus the mean cosine between its normal and the directions to its neighbours (positive on outer
 * edges, negative in cavities, 0 on flat faces).
 */
function curvatureOf(g: BufferGeometry): void {
  if (g.getAttribute('curvature')) return;
  const pos = g.getAttribute('position');
  const index = g.index;
  const count = index ? index.count : pos.count;
  const id = new Int32Array(pos.count);
  const keys = new Map<string, number>();
  for (let i = 0; i < pos.count; i++) {
    const k = `${Math.round(pos.getX(i) * 1e4)},${Math.round(pos.getY(i) * 1e4)},${Math.round(pos.getZ(i) * 1e4)}`;
    let v = keys.get(k);
    if (v === undefined) keys.set(k, (v = keys.size));
    id[i] = v;
  }
  const n = keys.size;
  const P = new Float32Array(n * 3);
  const N = new Float32Array(n * 3);
  for (let i = 0; i < pos.count; i++) P.set([pos.getX(i), pos.getY(i), pos.getZ(i)], id[i] * 3);
  const at = (i: number) => id[index ? index.getX(i) : i];
  const [a, b, c, e, face] = [new Vector3(), new Vector3(), new Vector3(), new Vector3(), new Vector3()];
  const neighbours: Set<number>[] = Array.from({ length: n }, () => new Set());
  for (let i = 0; i + 2 < count; i += 3) {
    const t = [at(i), at(i + 1), at(i + 2)];
    a.fromArray(P, t[0] * 3);
    b.fromArray(P, t[1] * 3);
    c.fromArray(P, t[2] * 3);
    face.subVectors(b, a).cross(e.subVectors(c, a)); // area-weighted
    for (let k = 0; k < 3; k++) {
      N[t[k] * 3] += face.x;
      N[t[k] * 3 + 1] += face.y;
      N[t[k] * 3 + 2] += face.z;
      neighbours[t[k]].add(t[(k + 1) % 3]).add(t[(k + 2) % 3]);
    }
  }
  const curve = new Float32Array(n);
  const [nv, d, p] = [new Vector3(), new Vector3(), new Vector3()];
  for (let v = 0; v < n; v++) {
    nv.fromArray(N, v * 3).normalize();
    p.fromArray(P, v * 3);
    let sum = 0;
    for (const u of neighbours[v]) sum += nv.dot(d.fromArray(P, u * 3).sub(p).normalize());
    curve[v] = neighbours[v].size ? -sum / neighbours[v].size : 0;
  }
  const out = new Float32Array(pos.count);
  for (let i = 0; i < pos.count; i++) out[i] = curve[id[i]];
  g.setAttribute('curvature', new BufferAttribute(out, 1));
}

const program = (fragmentShader: string, uniforms: Record<string, { value: unknown }>) =>
  new ShaderMaterial({
    uniforms: { toRoot: { value: new Matrix4() }, ...uniforms },
    vertexShader: UV_VERTEX,
    fragmentShader,
    side: DoubleSide,
    depthTest: false,
    depthWrite: false,
    blending: NoBlending,
  });
const patternProgram = program(PATTERN_FRAGMENT, {
  pattern: { value: 0 },
  colors: { value: [0, 1, 2, 3].map(() => new Vector4()) },
  scale: { value: 0.05 },
  rot: { value: new Matrix3() },
  seed: { value: 0 },
});
const wearProgram = program(WEAR_FRAGMENT, { amount: { value: 0 }, edgeWeight: { value: 0 }, scratches: { value: 0 }, scale: { value: 0.05 }, seed: { value: 0 } });

/** Colours of a pattern layer that has fewer than four of its own (an olive woodland). */
const DEFAULT_COLORS = [
  [0.33, 0.36, 0.24, 1],
  [0.2, 0.22, 0.14, 1],
  [0.45, 0.38, 0.26, 1],
  [0.08, 0.08, 0.07, 1],
];

/** Draws the meshes of `material` at their UVs with `shader` into a w x h canvas (top-left origin), reused by the next pass. */
function uvPass(material: string, shader: ShaderMaterial, w: number, h: number): HTMLCanvasElement {
  const gl = renderer!;
  const scene = new Scene();
  for (const m of meshesOf(material)) {
    if (shader !== decalProgram) curvatureOf(m.geometry);
    const list = (Array.isArray(m.material) ? m.material : [m.material]) as Material[];
    const mats = list.map((x) => (x.name === material ? shader : hidden));
    const copy = new Mesh(m.geometry, Array.isArray(m.material) ? mats : mats[0]);
    copy.matrixAutoUpdate = false;
    copy.matrix.copy(m.matrixWorld);
    copy.frustumCulled = false;
    scene.add(copy);
  }
  if (target?.width !== w || target.height !== h) {
    target?.dispose();
    target = new WebGLRenderTarget(w, h, { depthBuffer: false });
  }
  const prev = gl.getRenderTarget();
  const clear = gl.getClearColor(new Color());
  const clearAlpha = gl.getClearAlpha();
  gl.setRenderTarget(target);
  gl.setClearColor(0x000000, 0);
  gl.clear(true, false, false);
  gl.render(scene, camera);
  const px = new Uint8ClampedArray(w * h * 4);
  gl.readRenderTargetPixels(target, 0, 0, w, h, px);
  gl.setRenderTarget(prev);
  gl.setClearColor(clear, clearAlpha);
  const c = out.canvas;
  if (c.width !== w || c.height !== h) [c.width, c.height] = [w, h];
  c.getContext('2d')!.putImageData(new ImageData(px, w, h), 0, 0);
  return c;
}

/** Each triangle's UV island in `g` (vertices joined where position and UV match), cached on the geometry. */
function islandsOf(g: BufferGeometry): Int32Array {
  const cached = g.userData.sfIslands as Int32Array | undefined;
  if (cached) return cached;
  const pos = g.getAttribute('position');
  const uv = g.getAttribute('uv');
  const index = g.index;
  const id = new Int32Array(pos.count);
  const keys = new Map<string, number>();
  const r = (x: number) => Math.round(x * 1e4);
  for (let i = 0; i < pos.count; i++) {
    const k = `${r(pos.getX(i))},${r(pos.getY(i))},${r(pos.getZ(i))},${r(uv.getX(i))},${r(uv.getY(i))}`;
    let v = keys.get(k);
    if (v === undefined) keys.set(k, (v = keys.size));
    id[i] = v;
  }
  const parent = Int32Array.from({ length: keys.size }, (_, i) => i);
  const find = (x: number) => {
    while (parent[x] !== x) x = parent[x] = parent[parent[x]];
    return x;
  };
  const at = (i: number) => id[index ? index.getX(i) : i];
  const tris = (index ? index.count : pos.count) / 3;
  for (let t = 0; t < tris; t++) {
    const a = find(at(t * 3));
    parent[find(at(t * 3 + 1))] = a;
    parent[find(at(t * 3 + 2))] = a;
  }
  const out = new Int32Array(tris);
  for (let t = 0; t < tris; t++) out[t] = find(at(t * 3));
  return (g.userData.sfIslands = out);
}

const fillOut = { canvas: document.createElement('canvas') };

/** White where the UV islands of `material` under the `seeds` (u, v points) are, in a w x h canvas (reused next call). */
function islandFill(material: string, seeds: number[][], w: number, h: number): HTMLCanvasElement {
  const c = fillOut.canvas;
  if (c.width !== w || c.height !== h) [c.width, c.height] = [w, h];
  const ctx = c.getContext('2d')!;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = ctx.strokeStyle = '#fff';
  ctx.lineWidth = 2; // over the seam between islands, as decals' gutter
  ctx.lineJoin = 'round';
  const inside = (u: number, v: number, a: number[]) => {
    const d = (i: number, j: number) => (a[j] - a[i]) * (v - a[i + 1]) - (a[j + 1] - a[i + 1]) * (u - a[i]);
    const [x, y, z] = [d(0, 2), d(2, 4), d(4, 0)];
    return (x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0);
  };
  for (const m of meshesOf(material)) {
    const g = m.geometry;
    const uv = g.getAttribute('uv');
    if (!uv) continue;
    const islands = islandsOf(g);
    const index = g.index;
    const vertex = (i: number) => (index ? index.getX(i) : i);
    // Triangles drawn with this material (all of them, or its groups when the mesh has several materials).
    const list = (Array.isArray(m.material) ? m.material : [m.material]) as Material[];
    const ranges = Array.isArray(m.material)
      ? g.groups.filter((gr) => list[gr.materialIndex ?? 0]?.name === material).map((gr) => [gr.start / 3, Math.min(islands.length, (gr.start + gr.count) / 3)])
      : [[0, islands.length]];
    const tri = (t: number) => [0, 1, 2].flatMap((k) => [uv.getX(vertex(t * 3 + k)), uv.getY(vertex(t * 3 + k))]);
    const picked = new Set<number>();
    for (const [u, v] of seeds) for (const [a, b] of ranges) for (let t = a; t < b; t++) if (!picked.has(islands[t]) && inside(u, v, tri(t))) picked.add(islands[t]);
    if (!picked.size) continue;
    ctx.beginPath();
    for (const [a, b] of ranges)
      for (let t = a; t < b; t++) {
        if (!picked.has(islands[t])) continue;
        const p = tri(t);
        ctx.moveTo(p[0] * w, p[1] * h);
        ctx.lineTo(p[2] * w, p[3] * h);
        ctx.lineTo(p[4] * w, p[5] * h);
        ctx.closePath();
      }
    ctx.fill();
    ctx.stroke();
  }
  return c;
}

/**
 * Draws one livery layer that needs the model, for the material of `part`, into a w x h canvas in UV space (top-left
 * origin): a decal's projected image, a pattern's colours (opaque where the material is), wear's mask (in alpha) or a brush
 * layer's island fills (white). The
 * returned canvas is reused by the next call, so the caller draws it right away.
 */
export function uvRenderer(material: string, part: string | null | undefined): UvRenderer {
  return (layer, img, w, h) => {
    const root = viewRefs.root;
    if (!renderer || !root) throw Object.assign(new Error('the 3D view is not ready'), { code: 'no-livery-view' });
    root.updateMatrixWorld(true);
    const toRoot = root.matrixWorld.clone().invert();
    if (layer.kind === 'brush') return islandFill(material, (layer.fills ?? []).filter((p): p is number[] => !!p), w, h);
    if (layer.kind === 'pattern') {
      const u = patternProgram.uniforms;
      u.toRoot.value = toRoot;
      u.pattern.value = Math.max(0, PATTERNS.indexOf(layer.pattern ?? 'woodland'));
      const colors = (layer.colors ?? []).filter((c): c is number[] => !!c);
      (u.colors.value as Vector4[]).forEach((v, i) => v.fromArray(colors[i] ?? DEFAULT_COLORS[i]));
      u.scale.value = layer.scale ?? 0.05;
      const [rx, ry, rz] = (layer.patternRotation ?? [0, 0, 0]).map((x) => (x ?? 0) * DEG);
      u.rot.value = new Matrix3().setFromMatrix4(new Matrix4().makeRotationFromEuler(new Euler(rx, ry, rz)));
      u.seed.value = layer.seed ?? 0;
      return uvPass(material, patternProgram, w, h);
    }
    if (layer.kind === 'wear') {
      const u = wearProgram.uniforms;
      u.toRoot.value = toRoot;
      u.amount.value = layer.amount ?? 0.4;
      u.edgeWeight.value = layer.edgeWeight ?? 0.7;
      u.scratches.value = layer.scratches ?? 0.3;
      u.scale.value = layer.scale ?? 0.05;
      u.seed.value = layer.seed ?? 0;
      return uvPass(material, wearProgram, w, h);
    }
    const anchor: Object3D = (part && anchorObject(part)) || root;
    const frame = anchor.matrixWorld.clone().multiply(decalMatrix(layer));
    const toFrame = frame.clone().invert();
    const rot = new Quaternion();
    frame.decompose(new Vector3(), rot, new Vector3());
    const u = decalProgram.uniforms;
    u.toFrame.value = toFrame;
    u.frameRot.value = new Matrix3().setFromMatrix4(new Matrix4().makeRotationFromQuaternion(rot.invert()));
    u.image.value = imageTexture(img!);
    const [r, g, b, a] = layer.color ?? [1, 1, 1, 1];
    u.tint.value = new Color(r, g, b);
    u.alpha.value = a ?? 1;
    u.mirror.value = layer.mirror ? -1 : 1;
    return uvPass(material, decalProgram, w, h);
  };
}
