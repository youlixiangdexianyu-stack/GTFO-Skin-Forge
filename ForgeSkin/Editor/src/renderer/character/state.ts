// The 人物模型 tab's state: which tab the window shows, the step the author is on, the imported model and what the author set on it
// (bone match, spring bones, colliders, shaders). The model's three.js scene is not state; model.ts keeps it.
import { create } from 'zustand';
import type { CharmMotion } from '../viewport/refs';

export type Mode = 'weapon' | 'character';
export type Step = 'model' | 'bones' | 'arms' | 'springs' | 'materials' | 'check';
export const STEPS: Step[] = ['model', 'bones', 'arms', 'springs', 'materials', 'check'];

/** The starting points ForgeCore's chain physics offers for spring bones (ChainSpec presets). */
export type SpringKind = 'longHair' | 'shortHair' | 'skirt' | 'tail';
export const SPRING_KINDS: SpringKind[] = ['longHair', 'shortHair', 'skirt', 'tail'];

/** One spring-bone set: strands of bones (root first) hung from `anchor` (unset: their first root's parent bone), moved together. A
 * skirt's strands are linked side by side (and round with three or more) unless the file said otherwise. The values left unset are the preset's. */
export interface Spring {
  id: string;
  kind: SpringKind;
  strands: string[][];
  anchor?: string;
  linkSide?: boolean;
  loop?: boolean;
  stiffness?: number;
  damping?: number;
  weight?: number;
  gravity?: number;
  radius?: number;
}

/** The body colliders spring bones are kept off: one capsule per joint, from it to the next joint up or down the body. */
export type ColliderJoint = 'head' | 'spine2' | 'hip' | 'leftUpperLeg' | 'rightUpperLeg' | 'leftLowerLeg' | 'rightLowerLeg';
export const COLLIDER_JOINTS: ColliderJoint[] = ['head', 'spine2', 'hip', 'leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg'];
/** Metres on the canonical body (hip 1.04 m high); a model's own size scales them. */
export const COLLIDER_RADIUS: Record<ColliderJoint, number> = {
  head: 0.11,
  spine2: 0.14,
  hip: 0.14,
  leftUpperLeg: 0.085,
  rightUpperLeg: 0.085,
  leftLowerLeg: 0.06,
  rightLowerLeg: 0.06,
};
export interface Collider {
  on: boolean;
  /** Times the default radius. */
  scale: number;
}
/** A collider the file itself set (VRM spring-bone colliders): on a joint's bone, its ends in that bone's own space in the game's
 * axes (X mirrored), as the character document holds it (Core/Character.cs SpringCapsule). */
export interface Capsule {
  joint: string;
  a: [number, number, number];
  b: [number, number, number];
  radius: number;
}

export interface BoneRow {
  name: string;
  parent: number;
  depth: number;
}

/** What ForgeCore's rig check notes about the match's rest pose (character.check): for the author to look at, nothing the game refuses. */
export interface RigNote {
  kind: 'standing' | 'sides' | 'other';
  joints: string[];
  degrees?: number | null;
  text: string;
}
/** How the game will take the match (character.check). */
export interface RigCheck {
  /** The joints a character cannot do without (Core/Character.cs CharacterRig.Required: the hip), and those of them without a bone. */
  required: string[];
  missing: string[];
  /** The joints the game leaves in their rest pose: no bone, or a finger joint after one. */
  unposed: string[];
  notes: RigNote[];
  backwards: boolean;
  /** The model wears its own first-person arms (both upper arms have a bone). */
  arms: boolean;
  /** Each arm is bent so its hand lands on the grip (its upper arm, forearm and hand have bones). */
  reach: { left: boolean; right: boolean };
}

export interface CharacterState {
  mode: Mode;
  step: Step;
  /** The character skin's id (its autosave and pack) and name. */
  skinId: string | null;
  name: string;
  /** The model as the character format holds it (converted on import, in the skin's own folder) and the imported file's copy. */
  files: { descriptor: string; mesh: string; textures: string[]; source: string } | null;
  /** The file was built facing the other way (VRM 0.x) and is shown and packed turned round. */
  turned: boolean;
  /** The colliders the file set; null: the automatic ones below. */
  capsules: Capsule[] | null;
  /** Material name to its MToon settings as the file had them (kept for a toon shader later). */
  mtoon: Record<string, unknown>;
  /** The credit the model's licence asks for, null when it asks none. */
  credit: string | null;
  /** The imported model's copy (three.js reads it), its bones (parents first) and materials. */
  model: string | null;
  loading: boolean;
  bones: BoneRow[];
  materials: string[];
  vertices: number;
  /** Canonical joint to bone name: the automatic match, and the author's picks over it (null: leave the joint out). */
  auto: Record<string, string>;
  picked: Record<string, string | null>;
  check: RigCheck | null;
  /** The joint picked on the figure (its bone is chosen in the list below it). */
  joint: string | null;
  springs: Spring[];
  spring: string | null;
  /** What the pointer is on, in the list or on the figure: a spring set's id, or a loose bone's name. */
  hover: string | null;
  colliders: Record<ColliderJoint, Collider>;
  shaders: Record<string, 'lit'>;
  /** The preview motion last asked for, and when (performance.now()). */
  motion: { kind: CharmMotion; at: number } | null;
  /** The clip the motion preview plays (clips.ts MOTIONS), whether it runs, loops, and how fast. */
  clip: string | null;
  playing: boolean;
  loop: boolean;
  speed: number;
  showBones: boolean;
  /** What the 3D view shows: the whole model, or the first-person arms the game cuts from it. */
  view: 'body' | 'arms';
  /** The materials (indices into `materials`) not drawn in first person (the document's `hide`). */
  hide: number[];
  /** The author's size on top of the game's fit to the player's height (the document's `height`; 1 is the player's height). */
  height: number;
}

export const useCharacter = create<CharacterState>(() => ({
  mode: 'weapon',
  step: 'model',
  skinId: null,
  name: '',
  files: null,
  turned: false,
  capsules: null,
  mtoon: {},
  credit: null,
  model: null,
  loading: false,
  bones: [],
  materials: [],
  vertices: 0,
  auto: {},
  picked: {},
  check: null,
  joint: null,
  springs: [],
  spring: null,
  hover: null,
  colliders: Object.fromEntries(COLLIDER_JOINTS.map((j) => [j, { on: true, scale: 1 }])) as Record<ColliderJoint, Collider>,
  shaders: {},
  motion: null,
  clip: null,
  playing: false,
  loop: true,
  speed: 1,
  showBones: true,
  view: 'body',
  hide: [],
  height: 1,
}));

/** The match the author ends up with: the automatic one with their picks laid over it. */
export function matchOf(s: Pick<CharacterState, 'auto' | 'picked'>): Record<string, string> {
  const out: Record<string, string> = { ...s.auto };
  for (const [joint, bone] of Object.entries(s.picked)) {
    if (bone) out[joint] = bone;
    else delete out[joint];
  }
  return out;
}

/** Whether the match can be used: the required joint (the hip) has a bone. Nothing else stops a character. */
export const matchReady = (check: RigCheck | null) => !!check && !check.missing.length;

export function updateSpring(id: string, change: (s: Spring) => void) {
  useCharacter.setState((s) => ({
    springs: s.springs.map((x) => {
      if (x.id !== id) return x;
      const copy = { ...x, strands: x.strands.map((strand) => [...strand]) };
      change(copy);
      return copy;
    }),
  }));
}
