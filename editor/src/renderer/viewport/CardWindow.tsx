// The card window (main.ts cardWindow): a hidden second window of the editor that draws the card pictures of works and gallery
// items with the 3D view's own stage (Viewport.tsx Stage): the work's materials with their livery, layers and code effects, its
// attachments and charms, replaced parts, the stage lights and backdrop, bloom and tone mapping. It runs in its own process, so
// drawing never holds up the editor window. Each job puts the work into this window's store, waits until everything it shows
// has loaded, frames it as the game's gear icons are framed and hands back the picture.
import { Canvas, type RootState } from '@react-three/fiber';
import { useEffect, useState } from 'react';
import { Box3, Vector3, type Mesh, type PerspectiveCamera } from 'three';
import type { CatalogItem, SkinDocument, WeaponPreview } from '../protocol';
import { unityDirToThree, type Vec3 } from '../space';
import { DEFAULT_PREFS, normalize, useStore } from '../store';
import { setLiveryRenderer } from './liveryGpu';
import { LiveryPreview } from './liveryPreview';
import { loading, shown, viewRefs } from './refs';
import { Stage } from './Viewport';

export type CardJob = { item: CatalogItem | null; preview: WeaponPreview; doc: SkinDocument; width: number; height: number };
/** `missing`: the work's own pictures that could not be read (the game's were drawn in their place). */
export type CardPicture = { url: string; missing: string[] };

// Framing, in Unity weapon space (muzzle +Z, up +Y, right +X; Viewport.tsx VIEW_DIRS): every picture is a side view from the
// weapon's right, a little above, with no turn toward the muzzle, so the barrel lies level with the muzzle to the right as in the
// game's gear icons.
const VIEW: Vec3 = [1, 0.18, 0];
const UP: Vec3 = [0, 1, 0];
/** A melee weapon stands upright (head +Y) in its own space; it is laid along the picture with its head where a muzzle goes. */
const MELEE_UP: Vec3 = [0, 0, -1];
const FOV = 30;
const MARGIN = 0.08; // of the picture on each side
/** How long a work may take to load everything it shows before its card is given up (and asked for again later). */
const LOAD_LIMIT = 30_000;

/** Places `camera` along `view` so the whole box fits the picture with MARGIN to spare. */
function frame(camera: PerspectiveCamera, box: Box3, view: Vec3, up: Vec3) {
  const center = box.getCenter(new Vector3());
  const back = unityDirToThree(view).normalize();
  camera.fov = FOV;
  camera.up.copy(unityDirToThree(up).normalize());
  camera.position.copy(center).add(back);
  camera.lookAt(center);
  camera.updateMatrixWorld();
  const right = new Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
  const upV = new Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
  const tanV = Math.tan((FOV * Math.PI) / 360) * (1 - 2 * MARGIN);
  const tanH = tanV * camera.aspect;
  let distance = 0;
  const p = new Vector3();
  for (let i = 0; i < 8; i++) {
    p.set(i & 1 ? box.max.x : box.min.x, i & 2 ? box.max.y : box.min.y, i & 4 ? box.max.z : box.min.z).sub(center);
    const toward = p.dot(back);
    distance = Math.max(distance, toward + Math.abs(p.dot(right)) / tanH, toward + Math.abs(p.dot(upV)) / tanV);
  }
  camera.position.copy(center).addScaledVector(back, distance);
  camera.near = distance / 100;
  camera.far = distance * 10;
  camera.lookAt(center);
  camera.updateProjectionMatrix();
}

let three: (() => RootState) | null = null;
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
/** Draws `n` frames, a moment apart. */
async function frames(n: number) {
  for (let i = 0; i < n; i++) {
    three!().advance(performance.now());
    await wait(16);
  }
}

/** Draws frames until nothing is loading for a few frames in a row; false when it is still loading after LOAD_LIMIT. */
async function settle(): Promise<boolean> {
  let quiet = 0;
  for (const end = performance.now() + LOAD_LIMIT; performance.now() < end; ) {
    three!().advance(performance.now());
    await wait(50);
    quiet = viewRefs.root && loading.count === 0 && LiveryPreview.busy === 0 ? quiet + 1 : 0;
    if (quiet >= 4) return true;
  }
  return false;
}

async function draw(job: CardJob, resize: (size: [number, number]) => void): Promise<CardPicture | null> {
  // The last work goes first, so nothing of it is left in this one.
  useStore.setState({ preview: null });
  resize([job.width, job.height]);
  // The canvas takes a new size only once it has measured its box again.
  for (let i = 0; i < 40 && (three!().size.width !== job.width || three!().size.height !== job.height); i++) await wait(25);
  await frames(2);
  useStore.setState({
    catalog: job.item ? [job.item] : [],
    weaponId: job.item?.id ?? null,
    preview: job.preview,
    doc: normalize(job.doc),
    unreadable: [],
    prefs: DEFAULT_PREFS,
    selection: null,
    hover: null,
  });
  if (!(await settle())) return null;
  const root = viewRefs.root!;
  // The loose round and casing the 3D view lays beside the gun are left out, so the gun alone fills the picture.
  for (const p of job.preview.parts) if (p.ammo) viewRefs.partNodes.get(p.id)?.forEach((n) => (n.visible = false));
  root.updateMatrixWorld(true);
  const box = new Box3();
  root.traverse((o) => (o as Mesh).isMesh && shown(o) && box.expandByObject(o));
  if (box.isEmpty()) return null;
  const state = three!();
  frame(state.camera as PerspectiveCamera, box, VIEW, job.item?.slot === 'melee' ? MELEE_UP : UP);
  await frames(2);
  const failed = new Set(useStore.getState().unreadable);
  const missing = [...new Set((job.doc.materials ?? []).flatMap((m) => Object.values(m?.textures ?? {}).filter((p): p is string => !!p && failed.has(p))))];
  return { url: trimmed(state.gl.domElement), missing };
}

/** The picture without its clear border (a pixel of margin left), so the card scales the work itself up to fill its picture area. */
function trimmed(canvas: HTMLCanvasElement): string {
  const flat = Object.assign(document.createElement('canvas'), { width: canvas.width, height: canvas.height });
  const ctx = flat.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(canvas, 0, 0);
  const { data, width, height } = ctx.getImageData(0, 0, flat.width, flat.height);
  let [x0, y0, x1, y1] = [width, height, -1, -1];
  for (let y = 0; y < height; y++)
    for (let x = 0; x < width; x++)
      if (data[(y * width + x) * 4 + 3] > 4) {
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        if (y > y1) y1 = y;
      }
  if (x1 < 0) return flat.toDataURL('image/webp', 0.85);
  [x0, y0, x1, y1] = [Math.max(0, x0 - 1), Math.max(0, y0 - 1), Math.min(width - 1, x1 + 1), Math.min(height - 1, y1 + 1)];
  const out = Object.assign(document.createElement('canvas'), { width: x1 - x0 + 1, height: y1 - y0 + 1 });
  out.getContext('2d')!.drawImage(flat, x0, y0, out.width, out.height, 0, 0, out.width, out.height);
  return out.toDataURL('image/webp', 0.85);
}

export function CardWindow() {
  const [size, setSize] = useState<[number, number]>([512, 256]);
  useEffect(() => {
    let queue: Promise<unknown> = Promise.resolve();
    return window.forge.onCardJob((id, job) => {
      queue = queue.then(async () => {
        for (let i = 0; !three && i < 200; i++) await wait(50);
        const picture = await draw(job as CardJob, setSize).catch(() => null);
        window.forge.cardDone(id, picture);
      });
    });
  }, []);
  return (
    <div style={{ width: size[0], height: size[1] }}>
      <Canvas
        frameloop="never"
        dpr={1}
        gl={{ preserveDrawingBuffer: true, antialias: true, alpha: true }}
        onCreated={(state) => {
          three = state.get;
          setLiveryRenderer(state.gl);
        }}
      >
        <Stage card />
      </Canvas>
    </div>
  );
}
