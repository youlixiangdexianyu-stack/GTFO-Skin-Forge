// Mutable handles shared by the viewport components (not React state: they change every frame or hold three objects).
import type CameraControls from 'camera-controls';
import { Vector3, type Camera, type Group, type Object3D } from 'three';
import type { PlayMode, Trigger } from '../protocol';
import type { ThreeTRS } from '../space';

export const viewRefs = {
  root: null as Object3D | null,
  partNodes: new Map<string, Object3D[]>(),
  /** Part transforms by `part|path` (preview.weapon's keys, root path empty), and the reverse. */
  transforms: new Map<string, Object3D>(),
  transformKey: new Map<Object3D, { part: string; path: string | null }>(),
  nodeToPart: new Map<Object3D, string>(),
  attachments: new Map<string, Group>(),
  /** Vanilla local pose of every node of the weapon model, captured at load. */
  vanilla: new WeakMap<Object3D, ThreeTRS>(),
  /** Pose the preview starts from: vanilla plus the preview-only ammo display offset (a translation in parent space). */
  display: new WeakMap<Object3D, ThreeTRS>(),
  camera: null as Camera | null,
  controls: null as CameraControls | null,
  modalActive: false,
  /** Clicks right after a modal confirm must not change the selection. */
  quietUntil: 0,
};

/** Preview of game events: when each last happened (performance.now ms), and the kill count for kill screens. */
export const previewEvents = {
  at: {} as Partial<Record<Trigger, number>>,
  kills: 0,
};

/** Plays an event in the 3D view exactly as the game would trigger it. */
export function simulate(t: Trigger) {
  previewEvents.at[t] = performance.now();
  if (t === 'kill') previewEvents.kills++;
}

/**
 * Playback time in seconds of something started by `trigger`, or null while it should not show.
 * `always` runs from `since`; an event-started clip shows the base until its event and restarts on a repeat event.
 * Play: loop forever, once (an `always` clip then holds its last frame), or loop for `seconds`.
 */
export function playTime(
  trigger: Trigger | null | undefined,
  play: PlayMode | null | undefined,
  seconds: number | null | undefined,
  duration: number,
  since: number,
): number | null {
  const always = !trigger || trigger === 'always';
  const start = always ? since : previewEvents.at[trigger];
  if (start === undefined) return null;
  const t = (performance.now() - start) / 1000;
  const d = Math.max(duration, 1e-3);
  if (play === 'once') return t < d ? t : always ? d - 1e-3 : null;
  if (play === 'loopFor') return t < (seconds ?? 3) ? t % d : null;
  return t % d;
}

export const quiet = () => viewRefs.modalActive || performance.now() < viewRefs.quietUntil;

/** Fired when the weapon's part nodes are mapped (anchors become available). */
export const SCENE_EVENT = 'sf:scene';

const key = (part: string, path?: string | null) => `${part}|${path ?? ''}`;

/** The transform a part layout entry or an attachment anchor names: (part, path) exactly as preview.weapon lists it. */
export function partTarget(part: string, path?: string | null): Object3D | null {
  return viewRefs.transforms.get(key(part, path)) ?? null;
}

/**
 * Layout address of a selection: the part's own transform, or for a part with no transforms of its own (bullets inside a
 * mag) the nearest transform above its first mesh, which belongs to another part.
 */
export function layoutTarget(part: string, path?: string | null): { part: string; path: string | null; obj: Object3D } | null {
  const own = partTarget(part, path);
  if (own) return { part, path: path ?? null, obj: own };
  if (path) return null;
  for (let o = viewRefs.partNodes.get(part)?.[0] ?? null; o; o = o.parent) {
    const k = viewRefs.transformKey.get(o);
    if (k) return { ...k, obj: o };
  }
  return null;
}

/** Anchor transform of an attachment: the transform at (part, path), else the part's layout target. */
export function anchorObject(part: string, path?: string | null): Object3D | null {
  return partTarget(part, path) ?? layoutTarget(part)?.obj ?? null;
}

/** What a part selection highlights: its transform, or its own meshes when it has no transform (bullets in a mag). */
export function selectedObjects(part: string, path?: string | null): Object3D[] {
  const own = partTarget(part, path);
  return own ? [own] : path ? [] : (viewRefs.partNodes.get(part) ?? []);
}

/** Registers the part transforms of a loaded model. */
export function registerTransforms(part: string, list: { path: string | null; obj: Object3D }[]) {
  for (const t of list) {
    viewRefs.transforms.set(key(part, t.path), t.obj);
    viewRefs.transformKey.set(t.obj, { part, path: t.path });
  }
}

/** Preview-only translation (parent space) that moves ammo out of the gun; zero for every other node. */
export function displayOffset(o: Object3D): Vector3 {
  const d = viewRefs.display.get(o);
  const v = viewRefs.vanilla.get(o);
  return d && v ? d.position.clone().sub(v.position) : new Vector3();
}

/** Paths of `part`'s transforms from its root down to `obj` (root first, as null). */
export function pathChain(part: string, obj: Object3D): (string | null)[] {
  const chain: (string | null)[] = [];
  for (let o: Object3D | null = obj; o; o = o.parent) {
    const k = viewRefs.transformKey.get(o);
    if (k?.part === part) chain.unshift(k.path);
  }
  return chain;
}

export function partOf(obj: Object3D | null): string | null {
  for (let o = obj; o; o = o.parent) {
    const p = viewRefs.nodeToPart.get(o);
    if (p) return p;
  }
  return null;
}
