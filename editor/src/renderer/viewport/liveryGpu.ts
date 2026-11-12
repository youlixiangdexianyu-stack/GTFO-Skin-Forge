// Decal projection in UV space for the livery (livery.ts): every mesh that uses the material is drawn at its UVs, and
// each fragment's position is taken into the decal's frame; inside the frame's width, height and depth, on a face that
// looks at the decal, it gets the decal image. Runs on the 3D view's own renderer into an offscreen target.
import {
  Color,
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
  WebGLRenderTarget,
  type Material,
  type Object3D,
  type WebGLRenderer,
} from 'three';
import type { DecalRenderer } from '../livery';
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

const shader = new ShaderMaterial({
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
const textures = new Map<HTMLImageElement, Texture>();
let target: WebGLRenderTarget | null = null;
const out = { canvas: document.createElement('canvas') };

function imageTexture(img: HTMLImageElement): Texture {
  let t = textures.get(img);
  if (!t) {
    t = new Texture(img);
    t.flipY = false; // image top row at v = 0, like the game's UVs
    t.colorSpace = NoColorSpace; // sampled and written as stored, no colour conversion
    t.needsUpdate = true;
    textures.set(img, t);
  }
  return t;
}

/**
 * Draws one decal layer for the material of `part` into a w x h canvas in UV space (top-left origin). The returned canvas
 * is reused by the next call, so the caller draws it right away.
 */
export function decalRenderer(material: string, part: string | null | undefined): DecalRenderer {
  return (layer, img, w, h) => {
    const gl = renderer;
    const root = viewRefs.root;
    if (!gl || !root) throw Object.assign(new Error('the 3D view is not ready'), { code: 'no-livery-view' });
    root.updateMatrixWorld(true);
    const anchor: Object3D = (part && anchorObject(part)) || root;
    const frame = anchor.matrixWorld.clone().multiply(decalMatrix(layer));
    const toFrame = frame.clone().invert();
    const rot = new Quaternion();
    frame.decompose(new Vector3(), rot, new Vector3());
    shader.uniforms.toFrame.value = toFrame;
    shader.uniforms.frameRot.value = new Matrix3().setFromMatrix4(new Matrix4().makeRotationFromQuaternion(rot.invert()));
    shader.uniforms.image.value = imageTexture(img);
    const [r, g, b, a] = layer.color ?? [1, 1, 1, 1];
    shader.uniforms.tint.value = new Color(r, g, b);
    shader.uniforms.alpha.value = a ?? 1;
    shader.uniforms.mirror.value = layer.mirror ? -1 : 1;

    const scene = new Scene();
    for (const m of meshesOf(material)) {
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
  };
}
