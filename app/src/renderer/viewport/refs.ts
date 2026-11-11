// Mutable handles shared by the viewport components (not React state: they change every frame or hold three objects).
import type CameraControls from 'camera-controls';
import type { Camera, Group, Object3D } from 'three';
import type { ThreeTRS } from '../space';

export const viewRefs = {
  root: null as Object3D | null,
  partNodes: new Map<string, Object3D[]>(),
  /** Root transform of each part: what a part layout entry without a sub-transform moves. */
  partRoots: new Map<string, Object3D>(),
  nodeToPart: new Map<Object3D, string>(),
  attachments: new Map<string, Group>(),
  /** Vanilla local pose of every node of the weapon model, captured at load. */
  vanilla: new WeakMap<Object3D, ThreeTRS>(),
  /** Pose the preview starts from: vanilla plus the preview-only ammo display offset. Layout deltas apply on top. */
  display: new WeakMap<Object3D, ThreeTRS>(),
  camera: null as Camera | null,
  controls: null as CameraControls | null,
  modalActive: false,
  /** Clicks right after a modal confirm must not change the selection. */
  quietUntil: 0,
};

export const quiet = () => viewRefs.modalActive || performance.now() < viewRefs.quietUntil;

/** Fired when the weapon's part nodes are mapped (anchors become available). */
export const SCENE_EVENT = 'sf:scene';

/** Anchor transform of an attachment: the named transform under the part, else the part's first node. */
export function anchorObject(part: string, transform?: string | null): Object3D | null {
  const nodes = viewRefs.partNodes.get(part) ?? [];
  if (transform) {
    for (const n of nodes) {
      const hit = n.getObjectByName(transform);
      if (hit) return hit;
    }
  }
  return nodes[0] ?? null;
}

/** Nodes a part layout entry moves: the named sub-transform, or every root node of the part. */
export function partTargets(part: string, sub?: string | null): Object3D[] {
  const root = viewRefs.partRoots.get(part);
  if (!root) return [];
  if (!sub) return [root];
  const hit = root.getObjectByName(sub);
  return hit ? [hit] : [];
}

/** Lowest common ancestor of the part's mesh nodes. */
export function commonAncestor(nodes: Object3D[]): Object3D | null {
  if (!nodes.length) return null;
  const chain = (o: Object3D) => {
    const c: Object3D[] = [];
    for (let x: Object3D | null = o; x; x = x.parent) c.unshift(x);
    return c;
  };
  let common = chain(nodes[0]);
  for (const n of nodes.slice(1)) {
    const c = chain(n);
    let i = 0;
    while (i < common.length && common[i] === c[i]) i++;
    common = common.slice(0, i);
  }
  // A single mesh node is its own root; otherwise the deepest shared transform.
  return nodes.length === 1 ? nodes[0] : (common.at(-1) ?? null);
}

export function partOf(obj: Object3D | null): string | null {
  for (let o = obj; o; o = o.parent) {
    const p = viewRefs.nodeToPart.get(o);
    if (p) return p;
  }
  return null;
}
