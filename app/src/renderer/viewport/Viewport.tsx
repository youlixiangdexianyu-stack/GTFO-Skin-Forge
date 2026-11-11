import { CameraControls, GizmoHelper, GizmoViewcube, OrthographicCamera, Outlines, PerspectiveCamera, TransformControls } from '@react-three/drei';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { Bloom, EffectComposer } from '@react-three/postprocessing';
import CameraControlsImpl from 'camera-controls';
import { Suspense, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { Box3, CanvasTexture, Object3D, SRGBColorSpace, Sphere, type Mesh } from 'three';
import { unityDirToThree, type Vec3 } from '../space';
import { persistAutosave, useStore } from '../store';
import { Attachments } from './Attachments';
import { ModalTransform, modalState, newGesture, selectedTarget, type Target } from './ModalTransform';
import { selectedObjects, quiet, SCENE_EVENT, viewRefs } from './refs';
import { WeaponModel } from './WeaponModel';
import { roomReflection } from './weaponScene';
import { t } from '../i18n';

export type ViewName = 'front' | 'back' | 'left' | 'right' | 'top' | 'bottom' | 'iso';
// Unity weapon space: muzzle along +Z, up +Y, right +X.
const VIEW_DIRS: Record<ViewName, Vec3> = {
  front: [0, 0, 1],
  back: [0, 0, -1],
  right: [1, 0, 0],
  left: [-1, 0, 0],
  top: [0, 1, 0],
  bottom: [0, -1, 0],
  iso: [0.6, 0.45, 0.66],
};
// Viewcube faces are three.js +X, -X, +Y, -Y, +Z, -Z; three +Z is Unity -Z (the back of the gun).
const cubeFaces = () => [t('viewport.k1'), t('viewport.k2'), t('viewport.k3'), t('viewport.k4'), t('viewport.k5'), t('viewport.k6')];
const { ACTION } = CameraControlsImpl;
const HALF_PI = Math.PI / 2;

const viewStore = {
  ortho: false,
  gizmo: 'translate' as 'translate' | 'rotate' | 'scale',
  version: 0,
  subs: new Set<() => void>(),
  set(p: Partial<{ ortho: boolean; gizmo: 'translate' | 'rotate' | 'scale' }>) {
    Object.assign(viewStore, p);
    viewStore.version++;
    viewStore.subs.forEach((f) => f());
  },
};
export function useViewStore() {
  useSyncExternalStore(
    (f) => (viewStore.subs.add(f), () => viewStore.subs.delete(f)),
    () => viewStore.version,
  );
  return viewStore;
}
export function useModal() {
  return useSyncExternalStore(
    (f) => (modalState.listeners.add(f), () => modalState.listeners.delete(f)),
    () => `${modalState.tool}|${modalState.axis}`,
  );
}

function weaponBounds(): Box3 | null {
  if (!viewRefs.root) return null;
  const b = new Box3();
  viewRefs.root.traverse((o) => {
    if ((o as Mesh).isMesh && o.visible) b.expandByObject(o);
  });
  return b.isEmpty() ? null : b;
}

// fitToSphere keeps the current view direction (fitToBox would snap it to an axis).
const fit = (box: Box3) => void viewRefs.controls?.fitToSphere(box.getBoundingSphere(new Sphere()), true);

export function setView(name: ViewName) {
  const c = viewRefs.controls;
  const box = weaponBounds();
  if (!c || !box) return;
  const d = unityDirToThree(VIEW_DIRS[name]).normalize();
  void c.rotateTo(Math.atan2(d.x, d.z), Math.acos(Math.min(1, Math.max(-1, d.y))), true);
  fit(box);
}

export function focusSelection() {
  const s = useStore.getState().selection;
  const objs = s?.kind === 'attachment' ? [viewRefs.attachments.get(s.id)] : s?.kind === 'part' ? selectedObjects(s.id, s.sub) : [];
  const box = new Box3();
  objs.forEach((o) => o && box.expandByObject(o));
  const target = box.isEmpty() ? weaponBounds() : box;
  if (target) fit(target);
}

/**
 * The one lighting rig: a room reflection map plus three lights, all tinted by the same colour. The reflection map is
 * rebuilt with the tint, otherwise a coloured light shows next to a white reflection of the room's lamps.
 */
function Environment({ intensity, color }: { intensity: number; color: string }) {
  const { gl, scene } = useThree();
  useEffect(() => {
    const env = roomReflection(gl, color);
    scene.environment = env;
    return () => {
      if (scene.environment === env) scene.environment = null;
      env.dispose();
    };
  }, [gl, scene, color]);
  useEffect(() => {
    scene.environmentIntensity = intensity;
  }, [scene, intensity]);
  // The stage backdrop is drawn by the renderer, so the canvas is opaque: additive reticles and see-through lenses then
  // blend with the backdrop instead of punching black holes into a transparent canvas.
  useEffect(() => {
    const c = document.createElement('canvas');
    c.width = c.height = 512;
    const ctx = c.getContext('2d')!;
    const g = ctx.createRadialGradient(256, 195, 0, 256, 195, 420);
    g.addColorStop(0, '#3a3e44');
    g.addColorStop(0.52, '#24272b');
    g.addColorStop(1, '#16181b');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, 512, 512);
    const t = new CanvasTexture(c);
    t.colorSpace = SRGBColorSpace;
    scene.background = t;
    return () => {
      scene.background = null;
      t.dispose();
    };
  }, [scene]);
  return (
    <>
      <ambientLight intensity={0.25 * intensity} color={color} />
      <directionalLight position={[1.5, 2, 1]} intensity={1.6 * intensity} color={color} />
      <directionalLight position={[-2, 0.5, -1.5]} intensity={0.6 * intensity} color={color} />
    </>
  );
}

function CameraRig() {
  const view = useViewStore();
  const preview = useStore((s) => s.preview);
  const autoRotate = useStore((s) => s.prefs.autoRotate);
  const camera = useThree((s) => s.camera);
  const gl = useThree((s) => s.gl);

  useEffect(() => {
    viewRefs.camera = camera;
  }, [camera]);

  // Frame the weapon from the iso view once its parts are mapped.
  useEffect(() => {
    const onScene = () => setTimeout(() => setView('iso'), 0);
    window.addEventListener(SCENE_EVENT, onScene);
    if (preview) onScene();
    return () => window.removeEventListener(SCENE_EVENT, onScene);
  }, [preview, view.ortho]);

  // Alt + right drag orbits, then snaps to the nearest axis view on release.
  useEffect(() => {
    const el = gl.domElement;
    let snapping = false;
    const down = (e: PointerEvent) => {
      if (e.button === 2 && e.altKey && viewRefs.controls) {
        snapping = true;
        viewRefs.controls.mouseButtons.right = ACTION.ROTATE;
      }
    };
    const up = () => {
      const c = viewRefs.controls;
      if (!snapping || !c) return;
      snapping = false;
      c.mouseButtons.right = ACTION.TRUCK;
      const polar = Math.round(c.polarAngle / HALF_PI) * HALF_PI;
      void c.rotateTo(Math.round(c.azimuthAngle / HALF_PI) * HALF_PI, Math.min(Math.PI - 1e-4, Math.max(1e-4, polar)), true);
    };
    el.addEventListener('pointerdown', down, { capture: true });
    window.addEventListener('pointerup', up);
    return () => {
      el.removeEventListener('pointerdown', down, { capture: true });
      window.removeEventListener('pointerup', up);
    };
  }, [gl]);

  // Auto-rotate at 30° per second.
  useFrame((_, dt) => {
    if (autoRotate && !viewRefs.modalActive) void viewRefs.controls?.rotate((dt * Math.PI) / 6, 0, false);
  });

  return (
    <>
      {view.ortho ? (
        <OrthographicCamera makeDefault position={[0.4, 0.3, -0.4]} near={-10} far={50} zoom={600} />
      ) : (
        <PerspectiveCamera makeDefault position={[0.4, 0.3, -0.4]} fov={35} near={0.005} far={50} />
      )}
      <CameraControls
        key={view.ortho ? 'o' : 'p'}
        makeDefault
        ref={(c) => void (viewRefs.controls = c)}
        smoothTime={0.15}
        minDistance={0.02}
        maxDistance={10}
        dollyToCursor
      />
      <GizmoHelper alignment="top-right" margin={[64, 64]} renderPriority={2}>
        <GizmoViewcube
          faces={cubeFaces()}
          font="bold 56px 'Microsoft YaHei UI', sans-serif"
          color="#3a3f46"
          textColor="#e9e4d8"
          strokeColor="#565c64"
          hoverColor="#ff7a2f"
          opacity={0.95}
        />
      </GizmoHelper>
    </>
  );
}

const idle = new Object3D();

function Gizmo() {
  const selection = useStore((s) => s.selection);
  const doc = useStore((s) => s.doc);
  const view = useViewStore();
  const modal = useModal();
  const [target, setTarget] = useState<Target | null>(null);
  const gesture = useRef('');
  useEffect(() => {
    const id = setTimeout(() => setTarget(selectedTarget()), 0);
    return () => clearTimeout(id);
  }, [selection, doc.attachments]);
  // One gizmo stays mounted and is pointed at the selection: mounting one per selection leaks its handle geometry.
  const on = !!target && modal === 'null|null';
  return (
    <TransformControls
      object={target?.obj ?? idle}
      enabled={on}
      showX={on}
      showY={on}
      showZ={on}
      mode={view.gizmo}
      space="local"
      size={0.8}
      onMouseDown={() => (gesture.current = newGesture())}
      onObjectChange={() => target?.commit(gesture.current)}
    />
  );
}

const SEE_THROUGH = new Set(['glass', 'reticle']);

/** Outline on the selected part's meshes (drei Outlines portalled into each mesh). */
function SelectionOutline() {
  const selection = useStore((s) => s.selection);
  const preview = useStore((s) => s.preview);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    const f = () => setVersion((v) => v + 1);
    window.addEventListener(SCENE_EVENT, f);
    return () => window.removeEventListener(SCENE_EVENT, f);
  }, []);
  const meshes = useMemo(() => {
    void preview;
    void version;
    const out: Mesh[] = [];
    if (selection?.kind === 'part')
      selectedObjects(selection.id, selection.sub).forEach((n) =>
        n.traverse((o) => {
          // A lens or holo surface is a flat sheet: its outline hull would fill it as a solid plate, so it gets none.
          const mesh = o as Mesh;
          if (
            mesh.isMesh &&
            !(Array.isArray(mesh.material) ? mesh.material : [mesh.material]).every((m) => SEE_THROUGH.has(preview?.params[m.name]?.kind ?? ''))
          )
            out.push(mesh);
        }),
      );
    return out;
  }, [selection, preview, version]);
  // A depth-only twin of each mesh carries the outline, so the game mesh and its material stay untouched. angle 0 reuses
  // the mesh's own geometry: with creased normals Outlines makes a copy per mesh that it fails to free on unmount.
  return (
    <>
      {meshes.map((m) => (
        <mesh key={m.uuid} geometry={m.geometry} matrixAutoUpdate={false} matrix={m.matrixWorld} renderOrder={-1}>
          <meshBasicMaterial colorWrite={false} depthWrite={false} />
          <Outlines thickness={3} color="#ff7a2f" angle={0} />
        </mesh>
      ))}
    </>
  );
}

export function Viewport() {
  const preview = useStore((s) => s.preview);
  const prefs = useStore((s) => s.prefs);
  return (
    <Canvas
      gl={{ preserveDrawingBuffer: true, antialias: true, alpha: true }}
      dpr={[1, 2]}
      onCreated={({ gl }) => {
        if (import.meta.env.DEV) Object.assign(window, { sfGl: gl });
        // A lost GPU context does not come back on its own here: log it, save, and reload (the autosave reopens).
        gl.domElement.addEventListener('webglcontextlost', () => {
          const info = (performance as { memory?: { usedJSHeapSize: number } }).memory;
          window.forge.appLog(
            'webglcontextlost',
            `textures=${gl.info.memory.textures} geometries=${gl.info.memory.geometries} jsHeapMB=${info ? Math.round(info.usedJSHeapSize / 1048576) : '?'}`,
          );
          void persistAutosave().finally(() => window.location.reload());
        });
      }}
      onPointerMissed={(e) => {
        if (e.button === 0 && !quiet() && !useStore.getState().snapToSurface) useStore.getState().select(null);
      }}
    >
      <CameraRig />
      <Environment intensity={prefs.ambient} color={prefs.ambientColor} />
      <Suspense fallback={null}>
        {preview && <WeaponModel key={preview.glb} preview={preview} />}
        {preview && <Attachments />}
      </Suspense>
      <SelectionOutline />
      <Gizmo />
      <ModalTransform />
      <EffectComposer multisampling={4} renderPriority={1}>
        <Bloom luminanceThreshold={prefs.bloom ? 0.85 : 10} luminanceSmoothing={0.1} intensity={0.9} mipmapBlur />
      </EffectComposer>
    </Canvas>
  );
}
