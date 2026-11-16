// Hologram sets: a few matching hologram shapes added at once in one style (one line width and tick length, the skin's
// colour fading up to a lighter one, the same flicker and lit outline), placed from the open gear's parts. Each element is
// an ordinary shape attachment afterwards; its default name (attachmentName) names its set.
import { Vector3, type Box3 } from 'three';
import { newHologram, skinAccent } from './hologram';
import { t } from './i18n';
import { addShape, type ShapeKind } from './shapes';
import { useStore } from './store';
import { anchorObject, partBox, viewRefs } from './viewport/refs';

export type HoloSet = 'sight' | 'barrel' | 'strip';
export const HOLO_SETS: HoloSet[] = ['sight', 'barrel', 'strip'];

interface Element {
  kind: ShapeKind;
  params: Record<string, number>;
  wire?: boolean;
  /** Unity position and Euler rotation in the part's frame. */
  position: number[];
  rotation?: number[];
}

const LINE = 0.0008; // every element's line width (a wire panel's bars are this wide too)
const TICK = 0.0018; // tick length
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
const unity = (x: number, y: number, z: number) => [x, y, -z]; // three -> Unity position (space.ts)

/** The first of `parts` the gear has a model for, with its box in its anchor's three.js space. */
function partFrame(parts: string[]): { part: string; box: Box3 } | null {
  for (const part of parts) {
    const anchor = anchorObject(part);
    const box = anchor ? partBox(part, anchor) : null;
    if (box && !box.isEmpty()) return { part, box };
  }
  return null;
}

/** Where the part's aim line runs: the sight's align point (`*Align`), in its anchor's three.js space, if it has one. */
function alignPoint(part: string): Vector3 | null {
  const anchor = anchorObject(part);
  const key = [...viewRefs.transforms.keys()].find((k) => k.startsWith(`${part}|`) && /align$/i.test(k));
  const node = key && viewRefs.transforms.get(key);
  if (!anchor || !node) return null;
  anchor.updateWorldMatrix(true, true);
  return anchor.worldToLocal(node.getWorldPosition(new Vector3()));
}

/** 瞄具刻度组: a tick arc open at the bottom and a ring inside it, as wide as the sight, standing just in front of it on its
 * aim line (else above the receiver's front). */
function sightSet(): { part: string; elements: Element[] } | null {
  const f = partFrame(['sight', 'receiver']);
  if (!f) return null;
  const c = f.box.getCenter(new Vector3());
  const size = f.box.getSize(new Vector3());
  const sight = f.part === 'sight';
  const r = sight ? clamp(size.x * 0.45, 0.008, 0.03) : 0.012;
  const aim = (sight && alignPoint(f.part)) || c;
  // The barrel points to three -Z: in front of the sight is below its box's min z.
  const at = sight ? unity(aim.x, aim.y, f.box.min.z - 0.012) : unity(c.x, f.box.max.y + r + 0.004, f.box.min.z + size.z * 0.2);
  return {
    part: f.part,
    elements: [
      { kind: 'tickArc', params: { radius: r, angle: 270, ticks: 27, length: TICK, thickness: LINE }, position: at, rotation: [0, 0, -45] },
      { kind: 'ring', params: { radius: r * 0.6, thickness: LINE }, position: at },
    ],
  };
}

/** 枪管刻度环组: three tick rings on the barrel's axis at its end, the first round the barrel, each next one smaller and a
 * step further out past the muzzle. */
function barrelSet(): { part: string; elements: Element[] } | null {
  const f = partFrame(['front', 'receiver']);
  if (!f) return null;
  const anchor = anchorObject(f.part)!;
  const len = f.box.max.z - f.box.min.z;
  const muzzle = f.box.min.z; // three -Z is forward
  const near = (p: Vector3) => p.z < muzzle + len / 6;
  // The barrel's end: the centre of its last sixth, and how far from that axis its vertices reach.
  const c = partBox(f.part, anchor, near).getCenter(new Vector3());
  let reach = 0;
  partBox(f.part, anchor, (p) => (near(p) && (reach = Math.max(reach, Math.hypot(p.x - c.x, p.y - c.y))), false));
  const big = reach + 0.005;
  const step = clamp(len * 0.05, 0.008, 0.025);
  return {
    part: f.part,
    elements: [0, 1, 2].map((i) => ({
      kind: 'reticle' as const,
      params: { radius: big * (1 - 0.2 * i), ticks: 24, length: TICK, thickness: LINE },
      position: unity(c.x, c.y, muzzle + 0.01 - step * i),
    })),
  };
}

/** 信息条: a wire frame with two data bars, on the receiver's left side (the side a player sees), facing out. */
function stripSet(): { part: string; elements: Element[] } | null {
  const first = useStore.getState().catalog.find((c) => c.id === useStore.getState().weaponId)?.parts[0]?.id;
  const f = partFrame(['receiver', ...(first ? [first] : [])]);
  if (!f) return null;
  const c = f.box.getCenter(new Vector3());
  const size = f.box.getSize(new Vector3());
  const w = clamp(size.z * 0.45, 0.025, 0.05);
  const h = clamp(w * 0.26, 0.007, 0.012);
  // Turned 90° about Y, the frame faces Unity -X; its own +X runs to the stock (Unity -Z), so a bar's offset dx is -dx in z.
  const x = f.box.min.x - 0.0015,
    y = c.y + size.y * 0.15,
    z = -c.z;
  const rot = [0, 90, 0];
  return {
    part: f.part,
    elements: [
      { kind: 'panel', wire: true, params: { width: w, height: h }, position: [x, y, z], rotation: rot },
      { kind: 'line', params: { length: w * 0.78, thickness: LINE * 1.6 }, position: [x, y + h * 0.18, z], rotation: rot },
      { kind: 'line', params: { length: w * 0.42, thickness: LINE }, position: [x, y - h * 0.2, z + w * 0.18], rotation: rot },
    ],
  };
}

const BUILD: Record<HoloSet, () => { part: string; elements: Element[] } | null> = { sight: sightSet, barrel: barrelSet, strip: stripSet };

/**
 * Adds a hologram set on the open gear, each element with the lit outline (one code effect per element). Resolves to the new
 * attachments' ids (empty when the gear has no model to place them on).
 */
export async function addHoloSet(set: HoloSet): Promise<string[]> {
  const s = useStore.getState();
  const plan = viewRefs.root ? BUILD[set]() : null;
  if (!plan) {
    s.toast('error', t('holoSets.noPart'));
    return [];
  }
  const accent = skinAccent(s.doc);
  const top = accent.slice(0, 3).map((v) => v + (1 - v) * 0.6);
  const ids: string[] = [];
  for (const e of plan.elements) {
    const id = await addShape(e.kind, false, {
      params: e.params,
      wire: e.wire,
      set,
      init: (a) => {
        a.part = plan.part;
        a.transform = null;
        a.position = e.position;
        a.rotation = e.rotation ?? [0, 0, 0];
        a.material!.color = accent;
        a.hologram = { ...newHologram(), flicker: 0.12, opacity: 0.9, color2: top, edgeGlow: 0.4 };
      },
    });
    if (id) ids.push(id);
  }
  return ids;
}
