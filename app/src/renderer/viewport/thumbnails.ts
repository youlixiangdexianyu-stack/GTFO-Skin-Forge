// Weapon-list thumbnails: renders each catalog item's preview offscreen with the 3D view's materials, one at a time
// while the app is idle, and keeps the image in localStorage (per viewer convenience). Each image is stored with a hash
// of the preview it came from, so a new preview build (new cache, materials or textures) renders it again.
import {
  AmbientLight,
  Box3,
  DirectionalLight,
  Mesh,
  PerspectiveCamera,
  Scene,
  SRGBColorSpace,
  Vector3,
  WebGLRenderer,
  type MeshStandardMaterial,
  type Object3D,
  type Texture,
} from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { call, fileUrl, type SkinDocument, type WeaponPreview } from '../protocol';
import { useStore } from '../store';
import { unityDirToThree } from '../space';
import { editableMaterials, paintMaterial, restoreUnityNames, roomReflection } from './weaponScene';

export const THUMB_EVENT = 'sf:thumbs';
const key = (id: string) => `sf.thumb.v3.${id}`;
const read = (k: string) => {
  try {
    return localStorage.getItem(k);
  } catch {
    return null;
  }
};
export const thumbOf = (id: string) => read(key(id));
const hash = (text: string) => [...text].reduce((h, c) => Math.imul(h ^ c.charCodeAt(0), 16777619), 2166136261 >>> 0) >>> 0;

let running = false;

async function render(renderer: WebGLRenderer, env: Texture, preview: WeaponPreview): Promise<string | null> {
  const gltf = await new GLTFLoader().loadAsync(fileUrl(preview.glb));
  restoreUnityNames(gltf);
  const scene = new Scene();
  scene.environment = env;
  const sun = new DirectionalLight(0xffffff, 2.2);
  sun.position.set(1, 2, -1);
  scene.add(new AmbientLight(0xffffff, 1.2), sun);
  scene.add(gltf.scene);
  // Only what the 3D view draws: meshes that belong to a part.
  const partNodes = new Set(preview.parts.flatMap((p) => p.nodes));
  const inPart = (o: Object3D | null): boolean => !!o && (partNodes.has(o.name) || inPart(o.parent));
  gltf.scene.traverse((o) => {
    if ((o as Mesh).isMesh && !inPart(o)) o.visible = false;
  });
  const mats = new Map<string, MeshStandardMaterial>();
  editableMaterials(gltf.scene, mats);
  const empty = {} as SkinDocument;
  const keep = new Set<string>();
  await Promise.allSettled(
    [...mats].flatMap(([name, mat]) =>
      paintMaterial(
        mat,
        name,
        empty,
        preview,
        keep,
        () => false,
        () => undefined,
      ),
    ),
  );
  gltf.scene.updateMatrixWorld(true);
  const box = new Box3();
  gltf.scene.traverse((o) => o.visible && (o as Mesh).isMesh && box.expandByObject(o));
  if (box.isEmpty()) return null;
  const center = box.getCenter(new Vector3());
  const size = box.getSize(new Vector3());
  const radius = size.length() / 2;
  // Side view from the weapon's right, slightly above: reads best at list size.
  const camera = new PerspectiveCamera(30, 2, radius / 100, radius * 20);
  camera.position.copy(center).addScaledVector(unityDirToThree([1, 0.25, 0.15]).normalize(), radius / Math.tan((15 * Math.PI) / 180) / 1.6);
  // Upright items (melee weapons) are turned to lie along the wide image, muzzle or head forward.
  if (size.y > Math.max(size.x, size.z)) camera.up.set(0, 0, -1);
  camera.lookAt(center);
  renderer.render(scene, camera);
  const url = renderer.domElement.toDataURL('image/webp', 0.85);
  gltf.scene.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    mesh.geometry.dispose();
  });
  mats.forEach((m) => m.dispose());
  return url;
}

/** Fill missing thumbnails in the background; yields to anything the user is doing. */
export async function fillThumbnails(): Promise<void> {
  if (running) return;
  running = true;
  const canvas = document.createElement('canvas');
  canvas.width = 256;
  canvas.height = 128;
  const renderer = new WebGLRenderer({
    canvas,
    alpha: true,
    antialias: true,
    preserveDrawingBuffer: true,
  });
  renderer.outputColorSpace = SRGBColorSpace;
  const env = roomReflection(renderer, '#ffffff');
  try {
    // Images from earlier thumbnail formats are never read again.
    for (const k of Object.keys(localStorage)) if (k.startsWith('sf.thumb.') && !k.startsWith('sf.thumb.v3.')) localStorage.removeItem(k);
    for (const item of useStore.getState().catalog) {
      while (useStore.getState().previewLoading || useStore.getState().busy) await new Promise((r) => setTimeout(r, 1000));
      try {
        const preview = await call('preview.weapon', { id: item.id });
        const source = String(hash(JSON.stringify(preview)));
        if (thumbOf(item.id) && read(`${key(item.id)}.src`) === source) continue;
        const url = await render(renderer, env, preview);
        if (url) {
          localStorage.setItem(key(item.id), url);
          localStorage.setItem(`${key(item.id)}.src`, source);
          window.dispatchEvent(new Event(THUMB_EVENT));
        }
      } catch {
        /* a missing thumbnail only shows the placeholder */
      }
      await new Promise((r) => setTimeout(r, 150));
    }
  } finally {
    env.dispose();
    renderer.dispose();
    running = false;
  }
}
