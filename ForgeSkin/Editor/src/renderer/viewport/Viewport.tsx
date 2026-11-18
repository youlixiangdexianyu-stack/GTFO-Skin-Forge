import { CameraControls, GizmoHelper, GizmoViewcube, OrthographicCamera, PerspectiveCamera, TransformControls } from '@react-three/drei';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { Bloom, EffectComposer, Outline, ToneMapping } from '@react-three/postprocessing';
import { ToneMappingMode, type OutlineEffect } from 'postprocessing';
import CameraControlsImpl from 'camera-controls';
import { Suspense, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Box3, CanvasTexture, Object3D, SRGBColorSpace, Sphere, Vector3, type Mesh } from 'three';
import { unityDirToThree, type Vec3 } from '../space';
import { persistAutosave, sameSelection, useItem, useStore, type Selection } from '../store';
import { attachedOf } from '../protocol';
import { Attachments } from './Attachments';
import { LiveryTool } from './LiveryTool';
import { CodeEffects } from './CodeEffects';
import { DrawLoop } from './DrawLoop';
import { PartMeshes } from './PartMeshes';
import { ModalTransform, modalState, newGesture, placeOnSurface, selectedTarget, type Target } from './ModalTransform';
import { useFinger } from './gameHands';
import { HoldPointMarks } from './HoldPointMarks';
import { ATTACHMENT_EVENT, gearSurfaceAt, Loading, outlinedMeshes, pathChain, pickAt, quiet, SCENE_EVENT, selectedObjects, useSceneVersion, viewRefs, type Pick } from './refs';
import { liveryLayer, placed } from '../livery';
import type { CatalogItem, SkinDocument } from '../protocol';
import { WeaponModel } from './WeaponModel';
import { roomReflection } from './weaponScene';
import { lang, t } from '../i18n';

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
const fit = (box: Box3, animate = true) => void viewRefs.controls?.fitToSphere(box.getBoundingSphere(new Sphere()), animate);

export function setView(name: ViewName, animate = true) {
  const c = viewRefs.controls;
  const box = weaponBounds();
  if (!c || !box) return;
  const d = unityDirToThree(VIEW_DIRS[name]).normalize();
  void c.rotateTo(Math.atan2(d.x, d.z), Math.acos(Math.min(1, Math.max(-1, d.y))), animate);
  fit(box, animate);
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
 * The one lighting rig: a room reflection map whose lamps take the chosen colour, a neutral ambient and fill light, and
 * the key light in the chosen colour. The room's walls and the fill stay neutral, so the colour reads as coloured lamps
 * shining on the gun; tinting everything turns a metal gun that colour all over, which looks like a repaint.
 */
// The lights' share of the stage brightness. The image is tone mapped (ACES, which lifts its input by 1 / 0.6 before its curve),
// so the lights are lowered by that much: the gun's mid-tones stay where they were, white paint keeps its shading, and lit
// paint stays under the bloom threshold while glows (emission does not scale with the lights) still bloom.
const LIGHT = 0.6;

function Environment({ intensity, color, backdrop }: { intensity: number; color: string; backdrop: boolean }) {
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
    scene.environmentIntensity = intensity * LIGHT;
  }, [scene, intensity]);
  // The stage backdrop is drawn by the renderer, so the canvas is opaque: additive reticles and see-through lenses then
  // blend with the backdrop instead of punching black holes into a transparent canvas.
  // A card picture sits on its card's own background instead.
  useEffect(() => {
    if (!backdrop) return;
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
  }, [scene, backdrop]);
  return (
    <>
      <ambientLight intensity={0.25 * intensity * LIGHT} />
      <directionalLight position={[1.5, 2, 1]} intensity={1.6 * intensity * LIGHT} color={color} />
      <directionalLight position={[-2, 0.5, -1.5]} intensity={0.6 * intensity * LIGHT} />
    </>
  );
}

function CameraRig() {
  const view = useViewStore();
  const preview = useStore((s) => s.preview);
  const autoRotate = useStore((s) => s.prefs.autoRotate);
  const camera = useThree((s) => s.camera);
  const gl = useThree((s) => s.gl);
  const get = useThree((s) => s.get);

  useEffect(() => {
    viewRefs.camera = camera;
    viewRefs.three = get;
  }, [camera, get]);

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
      <CameraControls key={view.ortho ? 'o' : 'p'} makeDefault ref={(c) => void (viewRefs.controls = c)} smoothTime={0.15} minDistance={0.02} maxDistance={10} dollyToCursor />
      <GizmoHelper alignment="top-right" margin={[64, 64]} renderPriority={2}>
        <GizmoViewcube
          faces={cubeFaces()}
          font={`bold ${lang() === 'zh' ? 56 : 30}px 'Microsoft YaHei UI', sans-serif`}
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
  // A new attachment's object appears only once its model has loaded, after the selection already changed.
  useEffect(() => {
    const f = () => setTarget(selectedTarget());
    window.addEventListener(ATTACHMENT_EVENT, f);
    return () => window.removeEventListener(ATTACHMENT_EVENT, f);
  }, []);
  // One gizmo stays mounted and is pointed at the selection: mounting one per selection leaks its handle geometry.
  // A picked finger joint takes the gizmo from the selected part, to be turned only.
  const finger = useFinger((s) => s.pick);
  const aim = finger ?? target;
  const on = !!aim && modal === 'null|null';
  return (
    <>
      {/* Parked target while nothing is selected; it must be in the scene or the controls warn every frame. */}
      <primitive object={idle} />
      <TransformControls
        object={aim?.obj ?? idle}
        enabled={on}
        showX={on}
        showY={on}
        showZ={on}
        mode={finger ? 'rotate' : view.gizmo}
        space="local"
        size={finger ? 0.22 : 0.8}
        ref={(c) => void (viewRefs.gizmo = c as unknown as typeof viewRefs.gizmo)}
        onMouseDown={() => (gesture.current = newGesture())}
        onObjectChange={() => aim?.commit(gesture.current)}
      />
    </>
  );
}

/**
 * Clicking and hovering in the 3D view (refs.pickAt): a click selects what is under the pointer (clicking the selected
 * part again drills into the child transform there; with 贴到表面 on, clicking the gear lays the selected attachment flush
 * on it), empty space clears the selection; hovering marks the part or attachment, in the view and in the
 * parts tree.
 */
function Picking() {
  const gl = useThree((s) => s.gl);
  useEffect(() => {
    const el = gl.domElement;
    const gizmoBusy = () => !!viewRefs.gizmo && (viewRefs.gizmo.dragging || viewRefs.gizmo.axis !== null);
    let down: { x: number; y: number } | null = null;
    const onDown = (e: PointerEvent) => void (down = e.button === 0 && !gizmoBusy() ? { x: e.clientX, y: e.clientY } : null);
    const onClick = (e: MouseEvent) => {
      if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4 || quiet()) return;
      const s = useStore.getState();
      if (s.snapToSurface && s.selection?.kind === 'attachment') {
        // 贴到表面: the click lays the selected attachment on the gear there, upright as the camera sees it.
        const at = gearSurfaceAt(el, e.clientX, e.clientY);
        const cam = viewRefs.camera!.matrixWorld;
        if (at) placeOnSurface(s.selection.id, at.point, at.normal, new Vector3().setFromMatrixColumn(cam, 0), new Vector3().setFromMatrixColumn(cam, 1));
        return;
      }
      s.select(target(e.clientX, e.clientY));
    };
    // What a click there selects: an attachment, a part, or (within the selected part) the next piece down; hovering
    // marks the same thing.
    const target = (x: number, y: number): Selection => {
      const p = pickAt(el, x, y);
      if (!p || p.pick.kind === 'attachment') return p?.pick ?? null;
      const chain = pathChain(p.pick.id, p.hit.object);
      const sel = useStore.getState().selection;
      let sub: string | null = null;
      if (sel?.kind === 'part' && sel.id === p.pick.id && chain.length) sub = chain[Math.min(chain.indexOf(sel.sub ?? null) + 1, chain.length - 1)];
      return { kind: 'part', id: p.pick.id, sub };
    };
    // One pick per frame at most, at the last pointer position.
    let at: { x: number; y: number; buttons: number } | null = null;
    const onMove = (e: PointerEvent) => {
      const first = !at;
      at = { x: e.clientX, y: e.clientY, buttons: e.buttons };
      if (first)
        requestAnimationFrame(() => {
          const p = at!;
          at = null;
          if (!p.buttons && !quiet() && !gizmoBusy()) useStore.getState().setHover(target(p.x, p.y));
        });
    };
    const onLeave = () => useStore.getState().setHover(null);
    el.addEventListener('pointerdown', onDown);
    el.addEventListener('click', onClick);
    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerleave', onLeave);
    return () => {
      el.removeEventListener('pointerdown', onDown);
      el.removeEventListener('click', onClick);
      el.removeEventListener('pointermove', onMove);
      el.removeEventListener('pointerleave', onLeave);
    };
  }, [gl]);
  return null;
}

/**
 * Outline of the selection, or a lighter one of what is hovered, drawn in screen space from a mask: only the silhouette
 * gets an edge, whatever the mesh (an open mesh or a flat lens would fill solid with an outline hull). Each outline draws
 * its mask on its own layer.
 */
function Highlight({ target, hover = false }: { target: Pick | null; hover?: boolean }) {
  const preview = useStore((s) => s.preview);
  const doc = useStore((s) => s.doc);
  const version = useSceneVersion();
  // After the model applied the document's visibility (its effect runs first): only meshes that are drawn get an edge.
  const [meshes, setMeshes] = useState<Object3D[]>([]);
  useEffect(() => {
    void preview;
    void version;
    void doc;
    setMeshes(outlinedMeshes(target));
  }, [target, preview, version, doc]);
  // The composer sizes its effects only when the canvas size changes, so an effect it gains later keeps a 1x1 mask.
  const effect = useRef<OutlineEffect>(null);
  const width = useThree((s) => s.size.width * s.viewport.dpr);
  const height = useThree((s) => s.size.height * s.viewport.dpr);
  useEffect(() => effect.current?.setSize(width, height), [width, height, meshes]);
  return hover ? (
    <Outline ref={effect} selection={meshes} selectionLayer={11} visibleEdgeColor={0xffc49a} hiddenEdgeColor={0x5a4232} edgeStrength={2.5} xRay />
  ) : (
    <Outline ref={effect} selection={meshes} visibleEdgeColor={0xff7a2f} hiddenEdgeColor={0x7a3a16} edgeStrength={4} xRay />
  );
}

/** The part or attachment a hovered tree row shows in the 3D view: a paint layer or animated layer marks its part, a
 * code effect its part or attachment (a placed paint layer shows its projection box instead, LiveryTool). */
function hoverPick(h: Selection, doc: SkinDocument, item: CatalogItem | undefined): Pick | null {
  const partOfMaterial = (m?: string | null): Pick | null => {
    const attached = attachedOf(m);
    if (attached) return { kind: 'attachment', id: attached };
    const id = item?.parts.find((p) => m && p.materials.includes(m))?.id;
    return id ? { kind: 'part', id } : null;
  };
  if (h?.kind === 'part' || h?.kind === 'attachment') return h;
  if (h?.kind === 'livery') return placed(liveryLayer(doc, h.material, h.id)) ? null : partOfMaterial(h.material);
  if (h?.kind === 'layer') return partOfMaterial(h.material);
  const fx = h?.kind === 'fx' ? doc.codeEffects?.find((e) => e?.id === h.id) : undefined;
  return fx?.attachment ? { kind: 'attachment', id: fx.attachment } : partOfMaterial(fx?.material);
}

function Outlines() {
  const selection = useStore((s) => s.selection);
  const hover = useStore((s) => s.hover);
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const shown = hoverPick(hover, doc, item);
  return (
    <>
      <Highlight target={selection?.kind === 'part' || selection?.kind === 'attachment' ? selection : null} />
      <Highlight target={shown && !sameSelection(shown, selection) ? shown : null} hover />
    </>
  );
}

export function Viewport() {
  return (
    <Canvas
      gl={{ preserveDrawingBuffer: true, antialias: true, alpha: true }}
      dpr={[1, 2]}
      frameloop="demand"
      onCreated={({ gl }) => {
        if (import.meta.env.DEV) Object.assign(window, { sfGl: gl });
        // A lost GPU context does not come back on its own here: log it, save, and reload (the autosave reopens). A canvas
        // already taken off the page (the view closed, as when switching to the character tab) lost it on purpose.
        gl.domElement.addEventListener('webglcontextlost', () => {
          if (!gl.domElement.isConnected) return;
          const info = (performance as { memory?: { usedJSHeapSize: number } }).memory;
          window.forge.appLog('webglcontextlost', `textures=${gl.info.memory.textures} geometries=${gl.info.memory.geometries} jsHeapMB=${info ? Math.round(info.usedJSHeapSize / 1048576) : '?'}`);
          void persistAutosave().finally(() => window.location.reload());
        });
      }}
    >
      <DrawLoop />
      <CameraRig />
      <Gizmo />
      <Picking />
      <ModalTransform />
      <LiveryTool />
      <Stage tools />
    </Canvas>
  );
}

/**
 * The open work as the 3D view draws it: stage lights and backdrop, the gear with its materials, attachments, replaced parts
 * and code effects, bloom and tone mapping. The card window (CardWindow.tsx) draws a work's card picture with this same stage,
 * on a clear background (`card`); `tools` adds the editing outlines.
 */
export function Stage({ tools = false, card = false }: { tools?: boolean; card?: boolean }) {
  const preview = useStore((s) => s.preview);
  const prefs = useStore((s) => s.prefs);
  return (
    <>
      <Environment intensity={prefs.ambient} color={prefs.ambientColor} backdrop={!card} />
      <Suspense fallback={<Loading />}>
        {preview && <WeaponModel key={preview.glb} preview={preview} />}
        {preview && <Attachments />}
        {preview && <PartMeshes />}
        {preview && <CodeEffects />}
        {tools && preview && <HoldPointMarks />}
      </Suspense>
      <EffectComposer multisampling={4} renderPriority={1} autoClear={false}>
        {tools ? <Outlines /> : <></>}
        <Bloom luminanceThreshold={prefs.bloom ? 1 : 10} luminanceSmoothing={0.1} intensity={0.9} mipmapBlur />
        {/* The composer turns the renderer's tone mapping off: without this the lit HDR image is clipped, so white paint burns out. */}
        <ToneMapping mode={ToneMappingMode.ACES_FILMIC} />
      </EffectComposer>
    </>
  );
}
