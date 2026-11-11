// Weapon-list thumbnails: renders each catalog item's preview GLB offscreen, one at a time while the app is idle,
// and keeps the image in localStorage (per viewer convenience; regenerated if missing).
import { AmbientLight, Box3, DirectionalLight, Mesh, PerspectiveCamera, Scene, SRGBColorSpace, Vector3, WebGLRenderer, type Material, type MeshStandardMaterial } from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { call, fileUrl } from '../protocol';
import { useStore } from '../store';
import { unityDirToThree } from '../space';
import { loadTexture } from './textures';

export const THUMB_EVENT = 'sf:thumbs';
const key = (id: string) => `sf.thumb.v2.${id}`;
export const thumbOf = (id: string) => {
  try {
    return localStorage.getItem(key(id));
  } catch {
    return null;
  }
};

let running = false;

async function render(renderer: WebGLRenderer, id: string): Promise<string | null> {
  const preview = await call('preview.weapon', { id });
  const gltf = await new GLTFLoader().loadAsync(fileUrl(preview.glb));
  const scene = new Scene();
  const sun = new DirectionalLight(0xffffff, 2.2);
  sun.position.set(1, 2, -1);
  scene.add(new AmbientLight(0xffffff, 1.2), sun);
  scene.add(gltf.scene);
  const pending: Promise<void>[] = [];
  gltf.scene.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    for (const m of (Array.isArray(mesh.material) ? mesh.material : [mesh.material]) as Material[]) {
      const albedo = preview.textures[m.name]?.albedo;
      if (albedo)
        pending.push(
          loadTexture(albedo, 'albedo').then((t) => {
            (m as MeshStandardMaterial).map = t;
            (m as MeshStandardMaterial).metalness = 0.3;
            m.needsUpdate = true;
          }, () => undefined),
        );
    }
  });
  await Promise.all(pending);
  const box = new Box3().setFromObject(gltf.scene);
  if (box.isEmpty()) return null;
  const center = box.getCenter(new Vector3());
  const radius = box.getSize(new Vector3()).length() / 2;
  // Side view from the weapon's right, slightly above: reads best at list size.
  const camera = new PerspectiveCamera(30, 2, radius / 100, radius * 20);
  camera.position.copy(center).addScaledVector(unityDirToThree([1, 0.25, 0.15]).normalize(), radius / Math.tan((15 * Math.PI) / 180) / 1.6);
  camera.lookAt(center);
  renderer.render(scene, camera);
  const url = renderer.domElement.toDataURL('image/webp', 0.85);
  gltf.scene.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    mesh.geometry.dispose();
    (Array.isArray(mesh.material) ? mesh.material : [mesh.material]).forEach((m) => m.dispose());
  });
  return url;
}

/** Fill missing thumbnails in the background; yields to anything the user is doing. */
export async function fillThumbnails(): Promise<void> {
  if (running) return;
  running = true;
  const canvas = document.createElement('canvas');
  canvas.width = 256;
  canvas.height = 128;
  const renderer = new WebGLRenderer({ canvas, alpha: true, antialias: true, preserveDrawingBuffer: true });
  renderer.outputColorSpace = SRGBColorSpace;
  try {
    for (const item of useStore.getState().catalog) {
      if (thumbOf(item.id)) continue;
      while (useStore.getState().previewLoading || useStore.getState().busy) await new Promise((r) => setTimeout(r, 1000));
      try {
        const url = await render(renderer, item.id);
        if (url) {
          localStorage.setItem(key(item.id), url);
          window.dispatchEvent(new Event(THUMB_EVENT));
        }
      } catch {
        /* a missing thumbnail only shows the placeholder */
      }
      await new Promise((r) => setTimeout(r, 150));
    }
  } finally {
    renderer.dispose();
    running = false;
  }
}
