// The only Unity <-> three.js conversion (PLAN §2).
// Stored poses are Unity weapon space: left-handed, Y up, metres, Euler degrees applied as Quaternion.Euler
// (Z, then X, then Y). The preview GLB already mirrors the scene by negating Z, so a pose maps by mirroring Z too.
import { Euler, Matrix4, Quaternion, Vector3 } from 'three';

export type Vec3 = [number, number, number];

export interface UnityPose {
  position: Vec3;
  rotation: Vec3; // Unity Euler degrees
  scale: Vec3;
}

const DEG = Math.PI / 180;

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

/**
 * Part layout delta (PLAN §3.1) on a vanilla local pose: position = vanilla + delta, rotation = vanilla * Euler(delta),
 * scale = vanilla * delta. The mirror across Z is linear and a quaternion homomorphism, so the same rules hold in three space.
 */
export function applyDelta(vanilla: ThreeTRS, delta: UnityPose): ThreeTRS {
  const d = unityToThree(delta);
  return {
    position: vanilla.position.clone().add(d.position),
    quaternion: vanilla.quaternion.clone().multiply(d.quaternion),
    scale: vanilla.scale.clone().multiply(d.scale),
  };
}

/** Inverse of applyDelta: the delta that turns `vanilla` into `current`. */
export function deltaFrom(vanilla: ThreeTRS, current: ThreeTRS): UnityPose {
  const safe = (v: number) => (Math.abs(v) < 1e-9 ? 1e-9 : v);
  return threeToUnity(
    current.position.clone().sub(vanilla.position),
    vanilla.quaternion.clone().invert().multiply(current.quaternion),
    new Vector3(current.scale.x / safe(vanilla.scale.x), current.scale.y / safe(vanilla.scale.y), current.scale.z / safe(vanilla.scale.z)),
  );
}

/** Resulting Unity local pose (what Unity's Transform panel shows) for a vanilla pose plus a delta. */
export function resultingPose(vanilla: ThreeTRS, delta: UnityPose): UnityPose {
  const r = applyDelta(vanilla, delta);
  return threeToUnity(r.position, r.quaternion, r.scale);
}

/** Delta that produces the given resulting Unity local pose. */
export function deltaForPose(vanilla: ThreeTRS, pose: UnityPose): UnityPose {
  return deltaFrom(vanilla, unityToThree(pose));
}

export const IDENTITY: UnityPose = { position: [0, 0, 0], rotation: [0, 0, 0], scale: [1, 1, 1] };
