// Blender-style modal transform for the selected attachment: G move, R rotate, S scale; X/Y/Z lock to the anchor
// part's local axes; left click / Enter confirms, right click / Esc cancels. Also handles snap-to-surface clicks.
import { useHotkeys } from '@mantine/hooks';
import { useThree } from '@react-three/fiber';
import { useEffect, useRef } from 'react';
import { Matrix3, Quaternion, Vector2, Vector3, type Intersection, type Object3D, type PerspectiveCamera } from 'three';
import { deltaFrom, quaternionFacing, threeToUnity, unityDirToThree, type Vec3 } from '../space';
import { useStore } from '../store';
import { partTargets, viewRefs } from './refs';

type Tool = 'move' | 'rotate' | 'scale';
type Axis = 'x' | 'y' | 'z' | null;

const AXES: Record<'x' | 'y' | 'z', Vec3> = { x: [1, 0, 0], y: [0, 1, 0], z: [0, 0, 1] };

export const modalState = { tool: null as Tool | null, axis: null as Axis, listeners: new Set<() => void>() };
const notify = () => modalState.listeners.forEach((f) => f());


export interface Target {
  obj: Object3D;
  /** Writes the object's current pose into the document (attachment pose, or part layout delta). */
  commit(key?: string): void;
}

/** What the gizmo and the G/R/S keys act on: the selected attachment, part or sub-part. */
export function selectedTarget(): Target | null {
  const sel = useStore.getState().selection;
  if (sel?.kind === 'attachment') {
    const g = viewRefs.attachments.get(sel.id);
    if (!g) return null;
    return {
      obj: g,
      commit: (key) => {
        const p = threeToUnity(g.position, g.quaternion, g.scale);
        useStore.getState().updateAttachment(sel.id, (a) => {
          a.position = p.position;
          a.rotation = p.rotation;
          a.scale = p.scale;
        }, key);
      },
    };
  }
  if (sel?.kind === 'part') {
    const obj = partTargets(sel.id, sel.sub)[0];
    const base = obj && viewRefs.display.get(obj);
    if (!obj || !base) return null;
    return {
      obj,
      commit: (key) => {
        const d = deltaFrom(base, obj);
        useStore.getState().setPartTransform(sel.id, sel.sub, (t) => {
          t.position = d.position;
          t.rotation = d.rotation;
          t.scale = d.scale;
        }, key);
      },
    };
  }
  return null;
}

export const newGesture = () => `gesture:${Math.random().toString(36).slice(2)}`;

export function ModalTransform() {
  const { camera, gl, size } = useThree();
  const mouse = useRef(new Vector2());
  const keys = useRef<{ begin(t: Tool): void; axis(a: 'x' | 'y' | 'z'): void; confirm(): void; cancel(): void } | null>(null);
  const k = (fn: () => void) => () => fn();
  useHotkeys([
    ['g', k(() => keys.current?.begin('move'))],
    ['r', k(() => keys.current?.begin('rotate'))],
    ['s', k(() => keys.current?.begin('scale'))],
    ['x', k(() => keys.current?.axis('x'))],
    ['y', k(() => keys.current?.axis('y'))],
    ['z', k(() => keys.current?.axis('z'))],
    ['Enter', k(() => keys.current?.confirm())],
    ['Escape', k(() => keys.current?.cancel())],
  ]);
  const session = useRef<{
    target: Target;
    group: Object3D;
    tool: Tool;
    start: { p: Vector3; q: Quaternion; s: Vector3 };
    mouse0: Vector2;
    center: Vector2;
  } | null>(null);

  useEffect(() => {
    const el = gl.domElement;
    const toScreen = (g: Object3D) => {
      const w = g.getWorldPosition(new Vector3()).project(camera);
      return new Vector2(((w.x + 1) / 2) * size.width, ((1 - w.y) / 2) * size.height);
    };

    const apply = () => {
      const s = session.current;
      if (!s) return;
      const { group: g, start } = s;
      g.position.copy(start.p);
      g.quaternion.copy(start.q);
      g.scale.copy(start.s);
      const axis = modalState.axis;
      const axisLocal = axis ? unityDirToThree(AXES[axis]) : null; // in the anchor's (parent) space
      const parent = g.parent!;
      const d = mouse.current.clone().sub(s.mouse0);

      if (s.tool === 'move') {
        // Screen delta -> world delta on the plane through the object facing the camera.
        const world = g.getWorldPosition(new Vector3());
        const dist = world.distanceTo(camera.position);
        const persp = (camera as PerspectiveCamera).isPerspectiveCamera;
        const unitsPerPx = persp
          ? (2 * dist * Math.tan(((camera as PerspectiveCamera).fov * Math.PI) / 360)) / size.height
          : (((camera as unknown as { top: number; bottom: number; zoom: number }).top -
              (camera as unknown as { bottom: number }).bottom) /
              (camera as unknown as { zoom: number }).zoom) /
            size.height;
        const right = new Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
        const up = new Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
        const dWorld = right.multiplyScalar(d.x * unitsPerPx).add(up.multiplyScalar(-d.y * unitsPerPx));
        const toLocal = new Matrix3().setFromMatrix4(parent.matrixWorld).invert();
        let dLocal = dWorld.applyMatrix3(toLocal);
        if (axisLocal) dLocal = axisLocal.clone().multiplyScalar(dLocal.dot(axisLocal) / axisLocal.lengthSq());
        g.position.add(dLocal);
      } else if (s.tool === 'rotate') {
        const a0 = Math.atan2(s.mouse0.y - s.center.y, s.mouse0.x - s.center.x);
        const a1 = Math.atan2(mouse.current.y - s.center.y, mouse.current.x - s.center.x);
        let angle = a0 - a1;
        const viewDir = camera.getWorldDirection(new Vector3());
        let axisWorld: Vector3;
        if (axisLocal) {
          axisWorld = axisLocal.clone().transformDirection(parent.matrixWorld);
          if (axisWorld.dot(viewDir) > 0) angle = -angle;
        } else axisWorld = viewDir.negate();
        const pq = parent.getWorldQuaternion(new Quaternion());
        const axisParent = axisWorld.applyQuaternion(pq.clone().invert()).normalize();
        g.quaternion.premultiply(new Quaternion().setFromAxisAngle(axisParent, angle));
      } else {
        const r0 = Math.max(4, s.mouse0.distanceTo(s.center));
        const k = Math.max(0.01, mouse.current.distanceTo(s.center) / r0);
        if (!axis) g.scale.multiplyScalar(k);
        else g.scale[axis] *= k;
      }
    };

    const begin = (tool: Tool) => {
      const target = selectedTarget();
      if (!target) return;
      const g = target.obj;
      if (session.current) cancel();
      session.current = {
        target,
        group: g,
        tool,
        start: { p: g.position.clone(), q: g.quaternion.clone(), s: g.scale.clone() },
        mouse0: mouse.current.clone(),
        center: toScreen(g),
      };
      viewRefs.modalActive = true;
      if (viewRefs.controls) viewRefs.controls.enabled = false;
      modalState.tool = tool;
      modalState.axis = null;
      notify();
    };
    const end = () => {
      session.current = null;
      viewRefs.modalActive = false;
      if (viewRefs.controls) viewRefs.controls.enabled = true;
      modalState.tool = null;
      modalState.axis = null;
      notify();
    };
    const confirm = () => {
      const s = session.current;
      if (!s) return;
      s.target.commit();
      viewRefs.quietUntil = performance.now() + 400;
      end();
    };
    const cancel = () => {
      const s = session.current;
      if (!s) return;
      s.group.position.copy(s.start.p);
      s.group.quaternion.copy(s.start.q);
      s.group.scale.copy(s.start.s);
      viewRefs.quietUntil = performance.now() + 400;
      end();
    };

    const onMove = (e: PointerEvent) => {
      const r = el.getBoundingClientRect();
      mouse.current.set(e.clientX - r.left, e.clientY - r.top);
      apply();
    };
    const onDown = (e: PointerEvent) => {
      if (!session.current) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.button === 0) confirm();
      else if (e.button === 2) cancel();
    };
    keys.current = {
      begin,
      confirm,
      cancel,
      axis(a) {
        if (!session.current) return;
        modalState.axis = modalState.axis === a ? null : a;
        notify();
        apply();
      },
    };
    const onContext = (e: MouseEvent) => session.current && e.preventDefault();

    // Snap to surface: put the selected attachment on the clicked triangle, facing out of it.
    const onSurface = (ev: Event) => {
      const hit = (ev as CustomEvent<Intersection>).detail;
      const sel = useStore.getState().selection;
      if (sel?.kind !== 'attachment' || !hit.face) return;
      const target = selectedTarget();
      const g = target?.obj;
      if (!target || !g?.parent) return;
      const a = useStore.getState().doc.attachments?.find((x) => x?.id === sel.id);
      const gap = a?.screen?.surfaceGap ?? 0.0005;
      const normalWorld = hit.face.normal.clone().transformDirection(hit.object.matrixWorld).normalize();
      const parent = g.parent;
      const pointLocal = parent.worldToLocal(hit.point.clone().add(normalWorld.clone().multiplyScalar(gap)));
      const pq = parent.getWorldQuaternion(new Quaternion());
      const normalLocal = normalWorld.applyQuaternion(pq.invert()).normalize();
      g.position.copy(pointLocal);
      g.quaternion.copy(quaternionFacing(normalLocal));
      target.commit();
    };

    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerdown', onDown, { capture: true });
    el.addEventListener('contextmenu', onContext);
    window.addEventListener('sf:surface', onSurface);
    return () => {
      el.removeEventListener('pointermove', onMove);
      el.removeEventListener('pointerdown', onDown, { capture: true });
      el.removeEventListener('contextmenu', onContext);
      window.removeEventListener('sf:surface', onSurface);
    };
  }, [camera, gl, size]);

  return null;
}
