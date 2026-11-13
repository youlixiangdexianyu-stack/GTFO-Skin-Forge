// The only Unity <-> three.js conversion (PLAN §2).
// Stored poses are Unity weapon space: left-handed, Y up, metres, Euler degrees applied as Quaternion.Euler
// (Z, then X, then Y). The preview GLB already mirrors the scene by negating Z, so a pose maps by mirroring Z too.
import { Euler, MathUtils, Matrix4, Quaternion, Vector3 } from 'three';

export type Vec3 = [number, number, number];

export interface UnityPose {
  position: Vec3;
  rotation: Vec3; // Unity Euler degrees
  scale: Vec3;
}

const DEG = MathUtils.DEG2RAD;

/** Unity Quaternion.Euler(x, y, z) as a Unity-space quaternion: q = qy * qx * qz. */
function unityEulerToQuat(r: Vec3): Quaternion {
  // In three's convention, intrinsic order 'YXZ' equals q = qy * qx * qz.
  return new Quaternion().setFromEuler(new Euler(r[0] * DEG, r[1] * DEG, r[2] * DEG, 'YXZ'));
}

function quatToUnityEuler(q: Quaternion): Vec3 {
  const e = new Euler().setFromQuaternion(q, 'YXZ');
  return [e.x / DEG, e.y / DEG, e.z / DEG];
}

/** Mirror across Z: position (x,y,-z), quaternion (-x,-y,z,w). Self-inverse. */
function mirrorQuat(q: Quaternion): Quaternion {
  return new Quaternion(-q.x, -q.y, q.z, q.w);
}

export function unityToThree(pose: UnityPose): { position: Vector3; quaternion: Quaternion; scale: Vector3 } {
  return {
    position: new Vector3(pose.position[0], pose.position[1], -pose.position[2]),
    quaternion: mirrorQuat(unityEulerToQuat(pose.rotation)),
    scale: new Vector3(...pose.scale),
  };
}

export function threeToUnity(position: Vector3, quaternion: Quaternion, scale: Vector3): UnityPose {
  return {
    position: [position.x, position.y, -position.z],
    rotation: quatToUnityEuler(mirrorQuat(quaternion)),
    scale: [scale.x, scale.y, scale.z],
  };
}

/** Unity local direction (e.g. part-local X axis) expressed in three local space. */
export function unityDirToThree(d: Vec3): Vector3 {
  return new Vector3(d[0], d[1], -d[2]);
}

/** Rotation in three local space that makes a screen's visible face (Unity local -Z) look along `normal` (three space). */
export function quaternionFacing(normal: Vector3): Quaternion {
  // Unity -Z maps to three +Z, so align three +Z with the outward normal.
  const m = new Matrix4().lookAt(new Vector3(), normal.clone().negate(), new Vector3(0, 1, 0));
  if (Math.abs(normal.y) > 0.999) m.lookAt(new Vector3(), normal.clone().negate(), new Vector3(0, 0, 1));
  return new Quaternion().setFromRotationMatrix(m);
}

/** A local pose in three.js space. */
export interface ThreeTRS {
  position: Vector3;
  quaternion: Quaternion;
  scale: Vector3;
}

/** Absolute local values of a part layout entry (PLAN §3.1); null keeps the game's value. */
export interface PoseOverride {
  position?: number[] | null;
  rotation?: number[] | null;
  scale?: number[] | null;
}

/** Unity local pose shown for a part node: the entry's value where set, the game's value elsewhere. */
export function poseWith(vanilla: ThreeTRS, entry: PoseOverride | undefined): UnityPose {
  const v = threeToUnity(vanilla.position, vanilla.quaternion, vanilla.scale);
  return {
    position: (entry?.position as Vec3 | null | undefined) ?? v.position,
    rotation: (entry?.rotation as Vec3 | null | undefined) ?? v.rotation,
    scale: (entry?.scale as Vec3 | null | undefined) ?? v.scale,
  };
}

/** Entry fields for `pose`: each value that differs from the game's, null where it matches. */
export function overridesFor(vanilla: ThreeTRS, pose: UnityPose): { position: Vec3 | null; rotation: Vec3 | null; scale: Vec3 | null } {
  const t = unityToThree(pose);
  const near = (a: Vector3, b: Vector3) => a.distanceTo(b) < 1e-5;
  return {
    position: near(t.position, vanilla.position) ? null : pose.position,
    rotation: t.quaternion.angleTo(vanilla.quaternion) < 1e-4 ? null : pose.rotation,
    scale: near(t.scale, vanilla.scale) ? null : pose.scale,
  };
}

export const IDENTITY: UnityPose = { position: [0, 0, 0], rotation: [0, 0, 0], scale: [1, 1, 1] };
