// The preview motions (晃一下, 跑动, 转身) that charms and a character's spring bones are shown with: one motion matrix per moment,
// moving the thing through a space the view does not show. The chain's frames come back from the backend and are drawn in the
// still space, so the gun or the character stays put on screen while what hangs on it swings as it would in the game.
import { Matrix4, Vector3, type Object3D } from 'three';
import type { CharmMotion } from './refs';

const SHAKE_SECONDS = 0.6,
  RUN_SECONDS = 2.6,
  TURN_SECONDS = 2.0;
export const MOTION_SECONDS: Record<CharmMotion, number> = { shake: SHAKE_SECONDS, run: RUN_SECONDS, turn: TURN_SECONDS };
/** Held still after a motion, so what hangs comes to rest on screen. */
export const SETTLE_SECONDS = 1.5,
  PREVIEW_FPS = 60;
const RUN_SPEED = 4,
  RUN_RAMP = 0.3; // m/s, s to reach it and to stop
const TURN_RADIUS = 0.4; // m from the turning player's body to the gun
const smooth = (x: number) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));

/** Distance run after `t` seconds: speed up over RUN_RAMP, hold, slow down over RUN_RAMP before RUN_SECONDS - 0.3. */
function runDistance(t: number) {
  const a = RUN_SPEED / RUN_RAMP,
    stop = RUN_SECONDS - 0.3,
    hold = stop - RUN_RAMP;
  const d1 = 0.5 * a * RUN_RAMP * RUN_RAMP,
    d2 = d1 + RUN_SPEED * (hold - RUN_RAMP);
  if (t < RUN_RAMP) return 0.5 * a * t * t;
  if (t < hold) return d1 + RUN_SPEED * (t - RUN_RAMP);
  const u = Math.min(t, stop) - hold;
  return d2 + RUN_SPEED * u - 0.5 * a * u * u;
}

const mx = new Matrix4(),
  my = new Matrix4(),
  fwd = new Vector3(),
  side = new Vector3(),
  up = new Vector3(0, 1, 0),
  pivot = new Vector3();

/** The motion matrix of preview motion `motion` `t` seconds in (identity past its end), around `root`, whose -Z (flattened) is
 * where the player runs; `body` gets the player's own move in it (the run's, without the step bob and sway; a turn and a jolt move
 * only what is held). A turn goes about a point `turnRadius` behind the root: the player behind a gun, or 0 for a body itself. */
export function motionMatrix(out: Matrix4, root: Object3D | null, motion: CharmMotion, t: number, body: number[], turnRadius = TURN_RADIUS) {
  out.identity();
  body.fill(0);
  if (!root || t < 0) return out;
  root.updateWorldMatrix(true, false);
  fwd.setFromMatrixColumn(root.matrixWorld, 2).negate().setY(0);
  if (fwd.lengthSq() < 1e-8) fwd.set(0, 0, -1);
  fwd.normalize();
  side.crossVectors(fwd, up).normalize();
  if (motion === 'shake' && t < SHAKE_SECONDS) {
    // A jolt to the side and back, then a smaller one.
    const x = 0.05 * Math.sin((Math.PI * t) / 0.12) * (t < 0.12 ? 1 : t < 0.24 ? -0.4 : 0);
    out.makeTranslation(side.x * x, side.y * x, side.z * x);
  } else if (motion === 'run' && t < RUN_SECONDS) {
    const env = smooth(t / RUN_RAMP) * smooth((RUN_SECONDS - 0.3 - t) / RUN_RAMP);
    const d = runDistance(t),
      bob = 0.02 * Math.sin(2 * Math.PI * 2.6 * t) * env,
      sway = 0.012 * Math.sin(2 * Math.PI * 1.3 * t) * env;
    out.makeTranslation(fwd.x * d + side.x * sway, bob, fwd.z * d + side.z * sway);
    body[0] = fwd.x * d;
    body[2] = fwd.z * d;
  } else if (motion === 'turn' && t < TURN_SECONDS) {
    // Half a turn about the player, a pause, and back.
    const angle = Math.PI * (smooth(t / 0.5) - smooth((t - 1.1) / 0.5));
    root.getWorldPosition(pivot).addScaledVector(fwd, -turnRadius);
    out
      .makeTranslation(pivot.x, pivot.y, pivot.z)
      .multiply(my.makeRotationY(angle))
      .multiply(mx.makeTranslation(-pivot.x, -pivot.y, -pivot.z));
  }
  return out;
}
