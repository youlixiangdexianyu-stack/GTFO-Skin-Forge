// Mutable handles shared by the viewport components (not React state: they change every frame or hold three objects).
import type CameraControls from 'camera-controls';
import { useEffect, useState } from 'react';
import { Vector3, type Camera, type Group, type Mesh, type Object3D } from 'three';
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

/** Re-renders when the weapon's part nodes are mapped; the value changes with each mapping. */
export function useSceneVersion(): number {
  const [version, setVersion] = useState(0);
  useEffect(() => {
    const f = () => setVersion((v) => v + 1);
    window.addEventListener(SCENE_EVENT, f);
    return () => window.removeEventListener(SCENE_EVENT, f);
  }, []);
  return version;
}

/** Whether `o` starts another part than `part`: one of its nodes or its root transform. */
function otherPart(part: string, o: Object3D): boolean {
  const p = viewRefs.nodeToPart.get(o);
  const k = viewRefs.transformKey.get(o);
  return (!!p && p !== part) || (!!k && k.part !== part && k.path === null);
}

/**
 * Meshes that hiding `obj` (a transform of `part`) hides, as the plugin does: every mesh under it, attachments included,
 * down to another part's root (a sight on the receiver's mount belongs to the sight and hides only from its own row).
 */
export function hiddenMeshes(part: string, obj: Object3D): Mesh[] {
  const out: Mesh[] = [];
  const walk = (o: Object3D) => {
    if (o !== obj && otherPart(part, o)) return;
    if ((o as Mesh).isMesh && !o.userData.sfUnmapped && !o.userData.sfExtraAmmo) out.push(o as Mesh);
    o.children.forEach(walk);
  };
  walk(obj);
  return out;
}

/** A node's own model (what a replacement swaps): the node itself when it is a mesh, else the meshes glTF split its
 * mesh into (direct children that are not nodes of their own). Children that are transforms keep their models. */
export function ownMeshes(obj: Object3D): Mesh[] {
  if ((obj as Mesh).isMesh) return [obj as Mesh];
  return obj.children.filter((c): c is Mesh => (c as Mesh).isMesh && !viewRefs.transformKey.has(c) && !viewRefs.nodeToPart.has(c));
}

/** Whether `o` is drawn: it and every parent are visible. */
export function shown(o: Object3D): boolean {
  for (let x: Object3D | null = o; x; x = x.parent) if (!x.visible) return false;
  return true;
}

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
    // A part can list a second root (the stock's Default_GripAlign); the game lays out the first, its prefab root.
    if (viewRefs.transforms.has(key(part, t.path))) continue;
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
