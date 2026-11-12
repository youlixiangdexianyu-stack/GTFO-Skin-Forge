// Livery editing in the 3D view: dragging on the selected layer's part places a decal (it slides along the surface and
// turns with it) or paints brush strokes and masks onto the surface. Strokes are stored in the material's UV space
// (livery.ts), cut where the surface's UVs jump across a seam. Shows the decal's projection box and the brush size.
import { useFrame, useThree } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import {
  BoxGeometry,
  type BufferAttribute,
  DoubleSide,
  EdgesGeometry,
  LineBasicMaterial,
  LineSegments,
  Matrix4,
  Mesh,
  MeshBasicMaterial,
  Quaternion,
  Raycaster,
  RingGeometry,
  Vector2,
  Vector3,
  type Intersection,
  type Material,
  type Object3D,
} from 'three';
import { create } from 'zustand';
import { useShallow } from 'zustand/react/shallow';
import { liveryLayer, updateLivery } from '../livery';
import { materialOf, useStore } from '../store';
import { threeToUnity } from '../space';
import { decalMatrix, setLiveryRenderer } from './liveryGpu';
import { newGesture } from './ModalTransform';
import { anchorObject, shown, viewRefs } from './refs';

export type BrushShape = 'free' | 'line' | 'circle';
/** Brush settings (not part of the skin): shape, width in metres, and whether the brush paints the layer's mask. */
export const useBrush = create<{ shape: BrushShape; width: number; mask: boolean }>(() => ({ shape: 'free', width: 0.003, mask: false }));

/** The selected livery layer and its material and part, when one is selected. */
export function useLiverySelection() {
  const s = useStore(
    useShallow((s) => {
      const sel = s.selection?.kind === 'livery' ? s.selection : null;
      return {
        material: sel?.material ?? '',
        part: sel ? (materialOf(s.doc, sel.material)?.part ?? null) : null,
        layer: sel ? liveryLayer(s.doc, sel.material, sel.id) : undefined,
      };
    }),
  );
  return s.layer ? { material: s.material, part: s.part, layer: s.layer } : null;
}

interface Sample {
  uv: Vector2;
  point: Vector3;
  /** UV units per metre on the hit triangle. */
  density: number;
}

const materialName = (h: Intersection) => {
  const m = (h.object as Mesh).material as Material | Material[];
  return (Array.isArray(m) ? m[h.face?.materialIndex ?? 0] : m)?.name;
};

function density(h: Intersection): number {
  const mesh = h.object as Mesh;
  const pos = mesh.geometry.getAttribute('position') as BufferAttribute;
  const uv = mesh.geometry.getAttribute('uv') as BufferAttribute | undefined;
  const f = h.face;
  if (!f || !uv) return 0;
  const p = [f.a, f.b, f.c].map((i) => new Vector3().fromBufferAttribute(pos, i).applyMatrix4(mesh.matrixWorld));
  const t = [f.a, f.b, f.c].map((i) => new Vector2().fromBufferAttribute(uv, i));
  const area3 = p[1].clone().sub(p[0]).cross(p[2].clone().sub(p[0])).length();
  const areaUv = Math.abs(t[1].clone().sub(t[0]).cross(t[2].clone().sub(t[0])));
  return area3 > 0 ? Math.sqrt(areaUv / area3) : 0;
}

/** Adds brush samples to `strokes`, UV polylines of (u, v, width), in place: they continue the last stroke when `prev` (its
 * last sample) is given; a miss or a jump across a UV seam starts a new one. Returns the last sample added, else null. */
function addSamples(strokes: number[][], prev: Sample | null, samples: (Sample | null)[], width: number): Sample | null {
  for (const s of samples) {
    if (!s) {
      prev = null;
      continue;
    }
    const seam = prev && s.uv.distanceTo(prev.uv) > s.point.distanceTo(prev.point) * Math.max(s.density, prev.density) * 3 + 0.01;
    const p = [s.uv.x, s.uv.y, width * s.density];
    if (prev && !seam) strokes[strokes.length - 1].push(...p);
    else strokes.push(p);
    prev = s;
  }
  return prev;
}

export function LiveryTool() {
  const gl = useThree((s) => s.gl);
  const sel = useLiverySelection();
  const selRef = useRef(sel);
  selRef.current = sel;
  const outline = useMemo(
    () => new LineSegments(new EdgesGeometry(new BoxGeometry(1, 1, 2)), new LineBasicMaterial({ color: 0xff7a2f, depthTest: false, transparent: true, opacity: 0.85 })),
    [],
  );
  const ring = useMemo(() => {
    const m = new Mesh(new RingGeometry(0.42, 0.5, 40), new MeshBasicMaterial({ color: 0xff7a2f, side: DoubleSide, depthTest: false, transparent: true, opacity: 0.9 }));
    m.visible = false;
    return m;
  }, []);

  useEffect(() => {
    setLiveryRenderer(gl);
    return () => setLiveryRenderer(null);
  }, [gl]);

  useEffect(() => {
    const el = gl.domElement;
    const ray = new Raycaster();
    // A brush gesture appends to the layer's strokes (one undo step): `base` strokes were there before it, `prev` is the
    // last sample of its open stroke.
    let drag: { gesture: string; start: Vector2; last: Vector2; meshes: Object3D[]; mask: boolean; base: number; prev: Sample | null } | null = null;

    const visibleMeshes = () => {
      const out: Object3D[] = [];
      viewRefs.root?.traverse((o) => void ((o as Mesh).isMesh && shown(o) && out.push(o)));
      return out;
    };
    /** First surface under a screen point when it is the selected layer's material. */
    const hit = (p: Vector2, meshes: Object3D[]): Intersection | null => {
      const s = selRef.current;
      const cam = viewRefs.camera;
      if (!s || !cam) return null;
      const r = el.getBoundingClientRect();
      ray.setFromCamera(new Vector2(((p.x - r.left) / r.width) * 2 - 1, -((p.y - r.top) / r.height) * 2 + 1), cam);
      const h = ray.intersectObjects(meshes, false)[0];
      return h && h.uv && materialName(h) === s.material ? h : null;
    };
    const sample = (h: Intersection | null): Sample | null => (h ? { uv: h.uv!.clone(), point: h.point.clone(), density: density(h) } : null);
    /** Samples every few pixels along a screen segment, so strokes follow the surface between pointer events. */
    const along = (a: Vector2, b: Vector2, meshes: Object3D[], first = true) => {
      const n = Math.max(1, Math.ceil(a.distanceTo(b) / 4));
      const out: (Sample | null)[] = [];
      for (let i = first ? 0 : 1; i <= n; i++) out.push(sample(hit(a.clone().lerp(b, i / n), meshes)));
      return out;
    };
    const circle = (c: Vector2, edge: Vector2, meshes: Object3D[]) => {
      const r = c.distanceTo(edge);
      const n = Math.max(16, Math.ceil((2 * Math.PI * r) / 4));
      return Array.from({ length: n + 1 }, (_, i) => sample(hit(new Vector2(c.x + r * Math.cos((2 * Math.PI * i) / n), c.y + r * Math.sin((2 * Math.PI * i) / n)), meshes)));
    };
    const painting = () => {
      const s = selRef.current;
      return !!s && (useBrush.getState().mask || s.layer.kind === 'brush');
    };

    const placeDecal = (h: Intersection) => {
      const s = selRef.current!;
      const anchor = (s.part && anchorObject(s.part)) || viewRefs.root!;
      anchor.updateWorldMatrix(true, false);
      const inv = anchor.matrixWorld.clone().invert();
      const rotInv = new Quaternion().setFromRotationMatrix(new Matrix4().extractRotation(anchor.matrixWorld)).invert();
      const n = h.face!.normal.clone().transformDirection(h.object.matrixWorld).applyQuaternion(rotInv).normalize();
      // The image's right is the camera's right laid onto the surface, so it reads upright from the current view.
      const right = new Vector3().setFromMatrixColumn(viewRefs.camera!.matrixWorld, 0).applyQuaternion(rotInv);
      let x = right.sub(n.clone().multiplyScalar(right.dot(n)));
      if (x.lengthSq() < 1e-8) x = new Vector3().setFromMatrixColumn(viewRefs.camera!.matrixWorld, 1).applyQuaternion(rotInv);
      x.normalize();
      const z = n.clone().negate();
      const y = z.clone().cross(x).normalize();
      x = y.clone().cross(z);
      const q = new Quaternion().setFromRotationMatrix(new Matrix4().makeBasis(x, y, z));
      const pose = threeToUnity(h.point.clone().applyMatrix4(inv), q, new Vector3(1, 1, 1));
      updateLivery(s.material, s.layer.id!, (l) => {
        l.position = pose.position;
        l.rotation = pose.rotation;
      }, drag!.gesture);
    };

    /** Adds samples to the gesture's strokes; `restart` first drops what the gesture drew (a line or circle redrawn). */
    const apply = (samples: (Sample | null)[], restart = false) => {
      const s = selRef.current!;
      const d = drag!;
      const width = useBrush.getState().width;
      updateLivery(s.material, s.layer.id!, (l) => {
        const list = (d.mask ? (l.mask ??= []) : (l.strokes ??= [])) as number[][];
        if (restart) {
          list.splice(d.base);
          d.prev = null;
        }
        d.prev = addSamples(list, d.prev, samples, width);
      }, d.gesture);
    };

    const down = (e: PointerEvent) => {
      const s = selRef.current;
      if (!s || e.button !== 0 || e.altKey || viewRefs.modalActive) return;
      if (!painting() && s.layer.kind !== 'decal') return;
      const meshes = visibleMeshes();
      const p = new Vector2(e.clientX, e.clientY);
      const h = hit(p, meshes);
      if (!h) return;
      if (viewRefs.controls) viewRefs.controls.enabled = false;
      el.setPointerCapture(e.pointerId);
      const mask = useBrush.getState().mask;
      drag = { gesture: newGesture(), start: p, last: p, meshes, mask, base: ((mask ? s.layer.mask : s.layer.strokes) ?? []).length, prev: null };
      if (painting()) apply([sample(h)]);
      else placeDecal(h);
    };
    const move = (e: PointerEvent) => {
      const p = new Vector2(e.clientX, e.clientY);
      if (!drag) {
        // Brush size under the cursor.
        const h = painting() ? hit(p, visibleMeshes()) : null;
        ring.visible = !!h;
        if (h) {
          const n = h.face!.normal.clone().transformDirection(h.object.matrixWorld);
          ring.position.copy(h.point).addScaledVector(n, 0.0002);
          ring.quaternion.setFromUnitVectors(new Vector3(0, 0, 1), n);
          ring.scale.setScalar(useBrush.getState().width);
        }
        return;
      }
      if (!painting()) {
        const h = hit(p, drag.meshes);
        if (h) placeDecal(h);
        return;
      }
      const shape = useBrush.getState().shape;
      if (shape === 'free') apply(along(drag.last, p, drag.meshes, false));
      else apply(shape === 'line' ? along(drag.start, p, drag.meshes) : circle(drag.start, p, drag.meshes), true);
      drag.last = p;
    };
    const up = () => {
      if (!drag) return;
      drag = null;
      if (viewRefs.controls) viewRefs.controls.enabled = true;
      // The click that ends a stroke must not select the part under it.
      viewRefs.quietUntil = performance.now() + 300;
    };
    el.addEventListener('pointerdown', down, { capture: true });
    el.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    return () => {
      el.removeEventListener('pointerdown', down, { capture: true });
      el.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      up();
    };
  }, [gl, ring]);

  useFrame(() => {
    const decal = sel?.layer.kind === 'decal' && sel.layer.position ? sel.layer : null;
    outline.visible = !!decal;
    if (!sel) ring.visible = false;
    if (!decal) return;
    const anchor = (sel!.part && anchorObject(sel!.part)) || viewRefs.root;
    if (!anchor) return;
    outline.matrixAutoUpdate = false;
    outline.matrix.multiplyMatrices(anchor.matrixWorld, decalMatrix(decal));
  });

  return (
    <>
      <primitive object={outline} renderOrder={10} />
      <primitive object={ring} renderOrder={10} />
    </>
  );
}
