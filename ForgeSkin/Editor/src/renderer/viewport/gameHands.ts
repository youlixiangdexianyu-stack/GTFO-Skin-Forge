// The game's own first-person hands in the 3D view: the glove the backend reads from the player's game files (preview.hands), skinned
// to the hand bone and its 15 finger joints, posed by one of the game's grip animations (the measured table that comes with it) and
// then by a skin's finger turns. A hand is built in the frame of the game's hand bone, which is the frame of the gun's hand point.
import { useEffect, useState } from 'react';
import {
  Bone,
  BufferGeometry,
  Float32BufferAttribute,
  Group,
  Mesh,
  MeshBasicMaterial,
  MeshStandardMaterial,
  Object3D,
  Quaternion,
  Raycaster,
  Skeleton,
  SkinnedMesh,
  SphereGeometry,
  Uint16BufferAttribute,
  Uint32BufferAttribute,
  Vector2,
  Vector3,
} from 'three';
import { create } from 'zustand';
import { call, type GameHand, type GameHands } from '../protocol';
import { threeToUnity, unityToThree } from '../space';
import { useStore } from '../store';
import { viewRefs } from './refs';

/** The finger joint picked in the 3D view (its dot was clicked): the transform gizmo turns it instead of the selected part. */
export const useFinger = create<{ pick: { side: 'left' | 'right'; joint: number; obj: Object3D; commit: (gesture: string) => void } | null }>(() => ({ pick: null }));

let loading: Promise<GameHands | null> | null = null;

/** The game's hands, read once; null while loading and when the game cannot be read (the hand points then show without a hand). */
export function useGameHands(): GameHands | null {
  const [hands, setHands] = useState<GameHands | null>(null);
  useEffect(() => {
    loading ??= call('preview.hands', {}).catch(() => null);
    let live = true;
    void loading.then((h) => live && setHands(h));
    return () => {
      live = false;
    };
  }, []);
  return hands;
}

// Unity -> three.js mirrors Z (space.ts).
const mirror = (q: number[]) => new Quaternion(-q[0], -q[1], q[2], q[3]);

export interface BuiltHand {
  root: Group;
  /** The finger joints, in the table's order. */
  joints: Bone[];
  /** A dot at the far end of each joint's segment (the next joint, or the fingertip), to drag that end about. */
  dots: Mesh<SphereGeometry, MeshBasicMaterial>[];
  material: MeshStandardMaterial;
  dispose(): void;
}

export function buildHand(hand: GameHand): BuiltHand {
  const geometry = new BufferGeometry();
  const flip = (a: number[]) => a.map((v, i) => (i % 3 === 2 ? -v : v));
  geometry.setAttribute('position', new Float32BufferAttribute(flip(hand.positions), 3));
  geometry.setAttribute('normal', new Float32BufferAttribute(flip(hand.normals), 3));
  geometry.setAttribute('skinIndex', new Uint16BufferAttribute(hand.skinIndex, 4));
  geometry.setAttribute('skinWeight', new Float32BufferAttribute(hand.skinWeight, 4));
  // The mirror turns every triangle over.
  const indices = hand.indices.slice();
  for (let i = 0; i + 2 < indices.length; i += 3) [indices[i + 1], indices[i + 2]] = [indices[i + 2], indices[i + 1]];
  geometry.setIndex(new Uint32BufferAttribute(indices, 1));

  const bones = hand.joints.map((j) => {
    const bone = new Bone();
    bone.name = j.bone;
    bone.position.set(j.position[0], j.position[1], -j.position[2]);
    bone.quaternion.copy(mirror(j.rotation));
    return bone;
  });
  hand.joints.forEach((j, i) => j.parent >= 0 && bones[j.parent].add(bones[i]));
  const material = new MeshStandardMaterial({ roughness: 0.75, metalness: 0, transparent: true });
  const mesh = new SkinnedMesh(geometry, material);
  mesh.frustumCulled = false;
  mesh.renderOrder = 10;
  mesh.raycast = () => {};
  const root = new Group();
  root.add(bones[0], mesh);
  root.updateMatrixWorld(true);
  mesh.bind(new Skeleton(bones));
  const joints = bones.slice(1);
  const ball = new SphereGeometry(0.0045, 12, 8);
  const dots = joints.map((bone, i) => {
    // The segment ends at the finger's next joint; the last one at the fingertip: as far along the joint as its skin reaches.
    const end = i % 3 < 2 ? joints[i + 1] : bone.add(new Object3D().translateZ(reach(hand, i + 1, bone))).children.at(-1)!;
    const dot = new Mesh(ball, new MeshBasicMaterial({ depthTest: false, transparent: true, toneMapped: false }));
    dot.renderOrder = 12;
    dot.userData.joint = i;
    end.add(dot);
    return dot;
  });
  return { root, joints, dots, material, dispose: () => (geometry.dispose(), material.dispose(), ball.dispose(), dots.forEach((d) => d.material.dispose())) };
}

/** How far along joint `joint` (its own -Z here, +Z in the game) the skin it carries most goes, at rest. */
function reach(hand: GameHand, joint: number, bone: Bone): number {
  const p = new Vector3();
  let z = 0;
  for (let v = 0; v * 4 < hand.skinIndex.length; v++) {
    if (hand.skinIndex[v * 4] !== joint) continue;
    bone.worldToLocal(p.set(hand.positions[v * 3], hand.positions[v * 3 + 1], -hand.positions[v * 3 + 2]));
    z = Math.min(z, p.z);
  }
  return z * 0.85;
}

const round = (v: number) => Math.round(v * 10) / 10 || 0;

/** Writes joint `joint`'s turn into the skin: what its bone is turned by now, on from the hand shape's pose `from` (Unity space). */
export function storeTurn(side: 'left' | 'right', joint: number, bone: Object3D, from: number[], gesture: string) {
  const turn = threeToUnity(bone.position, mirror(from).invert().multiply(bone.quaternion), bone.scale).rotation.map(round);
  const key = side === 'left' ? 'leftFingers' : 'rightFingers';
  useStore.getState().edit((d) => {
    const rows = (d.hands?.[key] ?? Array.from({ length: 15 }, () => [0, 0, 0])).map((r) => [...(r ?? [0, 0, 0])]);
    rows[joint] = turn;
    const hands = { ...d.hands, [key]: rows };
    if (rows.every((r) => !r[0] && !r[1] && !r[2])) delete hands[key];
    d.hands = Object.keys(hands).length ? hands : null;
  }, gesture);
}

const ray = new Raycaster();

/** Drags a dot: its joint turns so the segment's end follows the pointer. The end can only be on the sphere round the joint, so
 *  it goes where the pointer's ray meets that sphere (the side the end is on), or as near the ray as it gets. `moved` runs after
 *  every step, until the button is let go. */
export function dragDot(canvas: HTMLElement, dot: Object3D, bone: Object3D, moved: () => void) {
  const camera = viewRefs.camera;
  const end = dot.parent;
  if (!camera || !end || !bone.parent) return;
  const parent = bone.parent;
  const controls = viewRefs.controls;
  if (controls) controls.enabled = false;
  const onMove = (e: PointerEvent) => {
    const box = canvas.getBoundingClientRect();
    ray.setFromCamera(new Vector2(((e.clientX - box.left) / box.width) * 2 - 1, 1 - ((e.clientY - box.top) / box.height) * 2), camera);
    const tip = end.getWorldPosition(new Vector3());
    const centre = bone.getWorldPosition(new Vector3());
    // Along the ray: the point nearest the joint, then back or forth to the sphere, whichever is nearer the end now.
    const near = ray.ray.closestPointToPoint(centre, new Vector3());
    const reach = Math.sqrt(Math.max(0, tip.distanceToSquared(centre) - near.distanceToSquared(centre)));
    const a = near.clone().addScaledVector(ray.ray.direction, reach);
    const b = near.clone().addScaledVector(ray.ray.direction, -reach);
    const at = a.distanceToSquared(tip) < b.distanceToSquared(tip) ? a : b;
    // Both directions in the space the joint turns in (its parent's): where the end is now, where the pointer wants it.
    const base = bone.position;
    const now = parent.worldToLocal(tip).sub(base).normalize();
    const want = parent.worldToLocal(at).sub(base).normalize();
    if (!now.lengthSq() || !want.lengthSq()) return;
    bone.quaternion.premultiply(new Quaternion().setFromUnitVectors(now, want));
    moved();
  };
  const onUp = () => {
    window.removeEventListener('pointermove', onMove);
    window.removeEventListener('pointerup', onUp);
    if (controls) controls.enabled = true;
    // The click that ends the drag must not select what is under the pointer.
    viewRefs.quietUntil = performance.now() + 250;
  };
  window.addEventListener('pointermove', onMove);
  window.addEventListener('pointerup', onUp);
}

/** Puts the fingers on a grip animation's pose (15 local rotations, Unity space; none: the rest pose) and a skin's turns on top. */
export function poseHand(hand: BuiltHand, rest: GameHand, shape: number[][] | undefined, turns: (number[] | null)[] | null | undefined) {
  hand.joints.forEach((bone, i) => {
    bone.quaternion.copy(mirror(shape?.[i] ?? rest.joints[i + 1].rotation));
    const turn = turns?.[i];
    if (turn && (turn[0] || turn[1] || turn[2])) bone.quaternion.multiply(unityToThree({ position: [0, 0, 0], rotation: [turn[0], turn[1], turn[2]], scale: [1, 1, 1] }).quaternion);
  });
}
