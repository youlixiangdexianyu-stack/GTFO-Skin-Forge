// A charm's rope (Attachment.charm): Core/CharmRope.cs line for line (same constants, same order of steps), so the 3D view
// moves it as the game does. FormatTests.CharmMatchesEditor runs both on one input and compares; change the two together.
// No imports: the test runs this file in Node as it is.
//
// Nodes: 0 is the hanging point (the attachment's origin, pinned), 1..S the rope (S = segments; node S is where the pendant
// hangs), S+1 the pendant's centre on a rigid arm below node S. Matrices are 16 numbers, column-major (three's
// Matrix4.elements); a pose is position xyz and quaternion xyzw.

export type CharmStyle = 'cord' | 'chain' | 'beads';

export interface CharmSettings {
  length?: number;
  segments?: number;
  weight?: number;
  damping?: number;
  stiffness?: number;
  style?: CharmStyle;
  thickness?: number;
  color?: number[];
  collide?: boolean;
}

/** Charm defaults (Core/SkinDocument.cs `Charm`): a short silver chain. */
export const CHARM_DEFAULTS = {
  length: 0.05,
  segments: 6,
  weight: 1,
  damping: 0.3,
  stiffness: 0.25,
  style: 'chain' as CharmStyle,
  thickness: 0.002,
  color: [0.8, 0.8, 0.82],
  collide: true,
};

export const MAX_SEGMENTS = 12, MAX_PER_GEAR = 4, MAX_BOXES = 12, MAX_LINKS = 48;
const STEP = 1 / 240;
const MAX_STEPS = 24;
const GAP = 0.25;
const JUMP = 0.5;
const GRAVITY = 9.81;
const BODY_CARRY = 0.85;
const SWING_DRAG = 0.5;
const ITERATIONS = 6, LENGTH_PASSES = 4;
const MIN_PIECE = 0.005;
const MAX_SPEED = 5;
const PENDANT_MASS = 4;
const BEND_RATE = 120, BEND_MAX = 0.25, BEND_DAMP_MAX = 0.5, PENDANT_BEND = 0.5;
const DAMP_SLOW = 0.04, DAMP_FAST = 1;
const CORD_SIDES = 6, CORD_SUB = 3, LINK_RING = 8, LINK_TUBE = 4, BEAD_LAT = 4, BEAD_LON = 6;
const LINK_LENGTH = 1.7, LINK_WIRE = 0.17, LINK_PITCH = 1.1, BEAD_PITCH = 1.2;
const CORD = 0, CHAIN = 1, BEADS = 2;
const STYLES: CharmStyle[] = ['cord', 'chain', 'beads'];

export const charmMetallic = (s: CharmStyle) => (s === 'cord' ? 0 : s === 'chain' ? 0.9 : 0.8);
export const charmSmoothness = (s: CharmStyle) => (s === 'cord' ? 0.2 : s === 'chain' ? 0.65 : 0.7);

const clamp = (v: number, lo: number, hi: number) => Math.min(Math.max(v, lo), hi);
// C#'s Math.Round rounds halves to even.
const roundEven = (v: number) => {
  const r = Math.round(v);
  return Math.abs(v % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
};

export class CharmRope {
  private readonly s: number;
  private readonly n: number;
  private readonly style: number;
  private readonly elements: number;
  private readonly curvePoints: number;
  private readonly template: number;
  private readonly seg: number;
  private readonly arm: number;
  private readonly gravity: number;
  private readonly keep: number;
  private readonly swingDrag: number;
  private readonly bend: number;
  private readonly bendDamp: number;
  private readonly radius: number;
  private readonly pendantRadius: number;
  private readonly thickness: number;
  private readonly p: Float64Array;
  private readonly q: Float64Array;
  private readonly w: Float64Array;
  private readonly box = new Float64Array(MAX_BOXES * 15);
  private readonly world = new Float64Array(MAX_BOXES * 15);
  private boxes = 0;
  private readonly cur = new Float64Array(7);
  private readonly from = new Float64Array(7);
  private readonly sub = new Float64Array(7);
  private readonly last = new Float64Array(7);
  private readonly body = new Float64Array(12); // the player's body: at the frame's start, now, this sub-step, the last one
  private readonly t = new Float64Array(16);
  private readonly curve: Float64Array;
  private readonly arc: Float64Array;
  private readonly frame: Float64Array;
  private readonly tplPos: Float64Array;
  private readonly tplNrm: Float64Array;
  private time = 0;
  private acc = 0;
  private started = false;

  constructor(c: CharmSettings, pendantArm: number) {
    const length = c.length ?? CHARM_DEFAULTS.length, thickness = c.thickness ?? CHARM_DEFAULTS.thickness;
    const style = STYLES.indexOf(c.style ?? CHARM_DEFAULTS.style);
    const damping = c.damping ?? CHARM_DEFAULTS.damping, stiffness = c.stiffness ?? CHARM_DEFAULTS.stiffness;
    this.s = clamp(Math.min(c.segments ?? CHARM_DEFAULTS.segments, Math.floor(length / MIN_PIECE)), 1, MAX_SEGMENTS);
    this.n = this.s + 2;
    this.seg = length / this.s;
    this.arm = pendantArm;
    const mass = c.weight ?? CHARM_DEFAULTS.weight;
    this.gravity = GRAVITY * STEP * STEP;
    // Drag on a node's move against the hanging point: a share of critical damping for the rope's own swing (sqrt(g / reach)),
    // plus a part growing with that speed (SWING_DRAG per reach), so a large swing loses more; both less on a heavier charm.
    this.swingDrag = SWING_DRAG / ((length + pendantArm) * mass);
    const zeta = Math.min((DAMP_SLOW * Math.pow(DAMP_FAST / DAMP_SLOW, damping)) / mass, 1);
    this.keep = Math.exp(-2 * zeta * Math.sqrt(GRAVITY / (length + pendantArm)) * STEP);
    // Bending: a spring of BEND_RATE x stiffness² rad/s, damped toward critical as the stiffness nears 1.
    const bend = BEND_RATE * stiffness * stiffness * STEP;
    this.bend = Math.min((bend * bend) / mass, BEND_MAX);
    this.bendDamp = Math.min((2 * bend * stiffness * stiffness) / mass, BEND_DAMP_MAX);
    this.thickness = thickness;
    this.radius = thickness * 0.5;
    this.pendantRadius = pendantArm * 0.8;
    this.style = style;
    this.p = new Float64Array(this.n * 3);
    this.q = new Float64Array(this.n * 3);
    this.w = new Float64Array(this.n);
    for (let i = 1; i < this.n; i++) this.w[i] = i === this.n - 1 ? 1 / PENDANT_MASS : 1;
    this.curvePoints = this.s * CORD_SUB + 1;
    this.curve = new Float64Array(this.curvePoints * 3);
    this.arc = new Float64Array(this.curvePoints);
    this.frame = new Float64Array(this.curvePoints * 6);
    const pitch = thickness * (style === CHAIN ? LINK_PITCH : BEAD_PITCH);
    this.elements = style === CORD ? 0 : clamp(roundEven(length / pitch), 1, MAX_LINKS);
    this.template = style === CHAIN ? LINK_RING * LINK_TUBE : style === BEADS ? (BEAD_LAT + 1) * BEAD_LON : 0;
    this.tplPos = new Float64Array(this.template * 3);
    this.tplNrm = new Float64Array(this.template * 3);
    if (style === CHAIN) this.linkTemplate();
    else if (style === BEADS) this.beadTemplate();
  }

  /** World positions of the nodes, three per node. */
  get nodes(): Float64Array {
    return this.p;
  }
  get boxCount(): number {
    return this.boxes;
  }
  get vertexCount(): number {
    return this.style === CORD ? this.curvePoints * CORD_SIDES : this.elements * this.template;
  }

  // ---- the gear's body ----

  /** A box of the gear's body (min, max in the gear's frame `gear`) into the hanging point's frame; cut back to the hanging point. */
  addBox(anchor: ArrayLike<number>, gear: ArrayLike<number>, min: ArrayLike<number>, max: ArrayLike<number>) {
    if (this.boxes >= MAX_BOXES) return;
    const b = this.box, s = this.sub;
    pose(anchor, s);
    const o = this.boxes * 15;
    const cx = (min[0] + max[0]) / 2, cy = (min[1] + max[1]) / 2, cz = (min[2] + max[2]) / 2;
    const wx = gear[0] * cx + gear[4] * cy + gear[8] * cz + gear[12] - s[0];
    const wy = gear[1] * cx + gear[5] * cy + gear[9] * cz + gear[13] - s[1];
    const wz = gear[2] * cx + gear[6] * cy + gear[10] * cz + gear[14] - s[2];
    rotate(-s[3], -s[4], -s[5], s[6], wx, wy, wz, b, o);
    for (let j = 0; j < 3; j++) {
      let ax = gear[j * 4], ay = gear[j * 4 + 1], az = gear[j * 4 + 2];
      const len = Math.sqrt(ax * ax + ay * ay + az * az);
      b[o + 12 + j] = ((max[j] - min[j]) / 2) * len;
      if (len > 0) {
        ax /= len;
        ay /= len;
        az /= len;
      }
      rotate(-s[3], -s[4], -s[5], s[6], ax, ay, az, b, o + 3 + j * 3);
    }
    let nearest = -1;
    let nearestGap = Number.MAX_VALUE;
    for (let j = 0; j < 3; j++) {
      const l = -(b[o] * b[o + 3 + j * 3] + b[o + 1] * b[o + 4 + j * 3] + b[o + 2] * b[o + 5 + j * 3]);
      const gap = b[o + 12 + j] - Math.abs(l);
      if (gap <= 0) {
        this.boxes++;
        return;
      }
      if (gap < nearestGap) {
        nearestGap = gap;
        nearest = j;
      }
    }
    {
      const j = nearest;
      const l = -(b[o] * b[o + 3 + j * 3] + b[o + 1] * b[o + 4 + j * 3] + b[o + 2] * b[o + 5 + j * 3]);
      const h = b[o + 12 + j];
      const lo = l >= 0 ? -h : l + this.thickness, hi = l >= 0 ? l - this.thickness : h;
      if (hi <= lo) return;
      const shift = (lo + hi) / 2;
      b[o] += b[o + 3 + j * 3] * shift;
      b[o + 1] += b[o + 4 + j * 3] * shift;
      b[o + 2] += b[o + 5 + j * 3] * shift;
      b[o + 12 + j] = (hi - lo) / 2;
    }
    this.boxes++;
  }

  // ---- simulation ----

  /** At rest: straight along the attachment's -Y from the hanging point. */
  rest(anchor: ArrayLike<number>) {
    const c = this.cur, t = this.t, p = this.p, q = this.q;
    pose(anchor, c);
    rotate(c[3], c[4], c[5], c[6], 0, -1, 0, t, 0);
    for (let i = 0; i < this.n; i++) {
      const d = Math.min(i, this.s) * this.seg + (i > this.s ? this.arm : 0);
      p[i * 3] = q[i * 3] = c[0] + t[0] * d;
      p[i * 3 + 1] = q[i * 3 + 1] = c[1] + t[1] * d;
      p[i * 3 + 2] = q[i * 3 + 2] = c[2] + t[2] * d;
    }
    this.from.set(c);
    this.last.set(c);
    this.body.fill(NaN); // not known until the next update
    this.acc = 0;
    this.started = true;
  }

  /** Advances to `time` (seconds) with the hanging point at `anchor` and the player's body (camera) at `body` (xyz). */
  update(time: number, anchor: ArrayLike<number>, body: ArrayLike<number>) {
    const c = this.cur, from = this.from, sub = this.sub, b = this.body;
    pose(anchor, c);
    const dt = time - this.time;
    const jx = c[0] - from[0], jy = c[1] - from[1], jz = c[2] - from[2];
    const bx = body[0] - b[0], by = body[1] - b[1], bz = body[2] - b[2];
    if (!this.started || dt > GAP || dt < 0 || !(jx * jx + jy * jy + jz * jz <= JUMP * JUMP) || !(bx * bx + by * by + bz * bz <= JUMP * JUMP)) {
      this.time = time;
      this.rest(anchor);
      for (let i = 0; i < 12; i++) b[i] = body[i % 3];
      return;
    }
    b[3] = body[0];
    b[4] = body[1];
    b[5] = body[2];
    this.time = time;
    this.acc += dt;
    const steps = Math.floor(this.acc / STEP);
    this.acc -= steps * STEP;
    // Each sub-step takes the hanging point where it was at that sub-step's own time in this frame (the clock runs `acc`
    // behind), so it moves at its real speed whatever the frame times. Past MaxSteps, the frame's start is only followed.
    const skip = Math.max(0, steps - MAX_STEPS);
    for (let k = Math.max(skip, 1); k <= steps; k++) {
      const f = 1 - (this.acc + (steps - k) * STEP) / dt;
      sub[0] = from[0] + (c[0] - from[0]) * f;
      sub[1] = from[1] + (c[1] - from[1]) * f;
      sub[2] = from[2] + (c[2] - from[2]) * f;
      slerp(from, c, f, sub);
      for (let i = 0; i < 3; i++) b[6 + i] = b[i] + (b[3 + i] - b[i]) * f;
      if (k === skip) this.follow();
      else this.substep();
      this.last.set(sub);
      b[9] = b[6];
      b[10] = b[7];
      b[11] = b[8];
    }
    from.set(c);
    b[0] = b[3];
    b[1] = b[4];
    b[2] = b[5];
  }

  /** Moves every node rigidly with the hanging point's move from `last` to `sub`. */
  private follow() {
    const p = this.p, q = this.q, last = this.last, sub = this.sub;
    const ax = last[3], ay = last[4], az = last[5], aw = last[6];
    const bx = sub[3], by = sub[4], bz = sub[5], bw = sub[6];
    const dx = bw * -ax + bx * aw + by * -az - bz * -ay;
    const dy = bw * -ay - bx * -az + by * aw + bz * -ax;
    const dz = bw * -az + bx * -ay - by * -ax + bz * aw;
    const dw = bw * aw - bx * -ax - by * -ay - bz * -az;
    for (let i = 1; i < this.n; i++) {
      this.carried(p, i * 3, dx, dy, dz, dw);
      this.carried(q, i * 3, dx, dy, dz, dw);
    }
  }

  private substep() {
    const p = this.p, q = this.q, w = this.w, t = this.t, last = this.last, sub = this.sub, box = this.box, world = this.world, n = this.n;
    // The rope rides along with BODY_CARRY of the player's body move, so it feels only the rest of the body's speeding up
    // and slowing down; the gear's own move against the body (a turn, a flick, the walking bob) reaches it whole.
    const bd = this.body, cx = (bd[6] - bd[9]) * BODY_CARRY, cy = (bd[7] - bd[10]) * BODY_CARRY, cz = (bd[8] - bd[11]) * BODY_CARRY;
    // The hanging point's move this sub-step in that riding frame. Verlet there: a node's move against the hanging point's
    // loses drag (the chain's own friction, so walking at a steady speed drags nothing) and is capped at MAX_SPEED.
    const mx = sub[0] - last[0] - cx, my = sub[1] - last[1] - cy, mz = sub[2] - last[2] - cz;
    for (let i = 1; i < n; i++) {
      const o = i * 3;
      let rx = p[o] - q[o] - mx, ry = p[o + 1] - q[o + 1] - my, rz = p[o + 2] - q[o + 2] - mz;
      let r = Math.sqrt(rx * rx + ry * ry + rz * rz);
      const keep = this.keep * Math.exp(-this.swingDrag * r);
      rx *= keep;
      ry *= keep;
      rz *= keep;
      r *= keep;
      if (r > MAX_SPEED * STEP) {
        const k = (MAX_SPEED * STEP) / r;
        rx *= k;
        ry *= k;
        rz *= k;
      }
      p[o] += cx;
      p[o + 1] += cy;
      p[o + 2] += cz;
      q[o] = p[o];
      q[o + 1] = p[o + 1];
      q[o + 2] = p[o + 2];
      p[o] += mx + rx;
      p[o + 1] += my + ry - this.gravity;
      p[o + 2] += mz + rz;
    }
    p[0] = q[0] = sub[0];
    p[1] = q[1] = sub[1];
    p[2] = q[2] = sub[2];
    rotate(sub[3], sub[4], sub[5], sub[6], 0, -1, 0, t, 0);
    for (let b = 0; b < this.boxes; b++) {
      const o = b * 15;
      rotate(sub[3], sub[4], sub[5], sub[6], box[o], box[o + 1], box[o + 2], world, o);
      world[o] += sub[0];
      world[o + 1] += sub[1];
      world[o + 2] += sub[2];
      for (let j = 0; j < 3; j++) rotate(sub[3], sub[4], sub[5], sub[6], box[o + 3 + j * 3], box[o + 4 + j * 3], box[o + 5 + j * 3], world, o + 3 + j * 3);
      world[o + 12] = box[o + 12];
      world[o + 13] = box[o + 13];
      world[o + 14] = box[o + 14];
    }
    // The bend once: a damped spring toward the straight line, the first piece toward the attachment's -Y. Then per
    // iteration the lengths (LENGTH_PASSES sweeps, so the rope does not stretch and swing fast like a spring) and the body.
    if (this.bend > 0)
      for (let i = 1; i < n; i++) {
        const o = i * 3, a = o - 3;
        let tx = t[0], ty = t[1], tz = t[2];
        if (i > 1) {
          tx = p[a] - p[a - 3];
          ty = p[a + 1] - p[a - 2];
          tz = p[a + 2] - p[a - 1];
          const len = Math.sqrt(tx * tx + ty * ty + tz * tz);
          if (len < 1e-9) continue;
          tx /= len;
          ty /= len;
          tz /= len;
        }
        const rest = i - 1 < this.s ? this.seg : this.arm;
        const k = i === n - 1 ? this.bend * PENDANT_BEND : this.bend, d = i === n - 1 ? this.bendDamp * PENDANT_BEND : this.bendDamp;
        // The node's move this sub-step against the node above's (for the first, the hanging point's move with gravity's).
        const ux = i > 1 ? p[a] - q[a] : mx, uy = i > 1 ? p[a + 1] - q[a + 1] : my - this.gravity, uz = i > 1 ? p[a + 2] - q[a + 2] : mz;
        p[o] += (p[a] + tx * rest - p[o]) * k - (p[o] - q[o] - ux) * d;
        p[o + 1] += (p[a + 1] + ty * rest - p[o + 1]) * k - (p[o + 1] - q[o + 1] - uy) * d;
        p[o + 2] += (p[a + 2] + tz * rest - p[o + 2]) * k - (p[o + 2] - q[o + 2] - uz) * d;
      }
    for (let it = 0; it < ITERATIONS; it++) {
      for (let pass = 0; pass < LENGTH_PASSES; pass++)
        for (let i = 0; i < n - 1; i++) {
          const a = i * 3, b = a + 3;
          const rest = i < this.s ? this.seg : this.arm;
          const ex = p[b] - p[a], ey = p[b + 1] - p[a + 1], ez = p[b + 2] - p[a + 2];
          const len = Math.sqrt(ex * ex + ey * ey + ez * ez);
          const sum = w[i] + w[i + 1];
          if (len < 1e-9 || sum <= 0) continue;
          const diff = (len - rest) / (len * sum);
          p[a] += ex * diff * w[i];
          p[a + 1] += ey * diff * w[i];
          p[a + 2] += ez * diff * w[i];
          p[b] -= ex * diff * w[i + 1];
          p[b + 1] -= ey * diff * w[i + 1];
          p[b + 2] -= ez * diff * w[i + 1];
        }
      for (let b = 0; b < this.boxes; b++) {
        const o = b * 15;
        for (let i = 1; i < n; i++) {
          const v = i * 3;
          const r = i === n - 1 ? this.pendantRadius : this.radius;
          const rx = p[v] - world[o], ry = p[v + 1] - world[o + 1], rz = p[v + 2] - world[o + 2];
          let axis = -1;
          let best = Number.MAX_VALUE, push = 0;
          for (let j = 0; j < 3; j++) {
            const l = rx * world[o + 3 + j * 3] + ry * world[o + 4 + j * 3] + rz * world[o + 5 + j * 3];
            const e = world[o + 12 + j] + r;
            const gap = e - Math.abs(l);
            if (gap <= 0) {
              axis = -1;
              break;
            }
            if (gap < best) {
              best = gap;
              axis = j;
              push = (l >= 0 ? e : -e) - l;
            }
          }
          if (axis < 0) continue;
          p[v] += world[o + 3 + axis * 3] * push;
          p[v + 1] += world[o + 4 + axis * 3] * push;
          p[v + 2] += world[o + 5 + axis * 3] * push;
        }
      }
    }
    // Last, down from the hanging point: no rope piece longer than its length and the pendant's arm exactly its length.
    // Like every correction this becomes speed (the rope's pull); the length passes leave little to take out.
    for (let i = 1; i < n; i++) {
      const o = i * 3, a = o - 3;
      const rest = i - 1 < this.s ? this.seg : this.arm;
      const ex = p[o] - p[a], ey = p[o + 1] - p[a + 1], ez = p[o + 2] - p[a + 2];
      const len = Math.sqrt(ex * ex + ey * ey + ez * ez);
      if (len < 1e-9 || (len <= rest && i - 1 < this.s)) continue;
      const k = rest / len;
      const sx = p[a] + ex * k - p[o], sy = p[a + 1] + ey * k - p[o + 1], sz = p[a + 2] + ez * k - p[o + 2];
      p[o] += sx;
      p[o + 1] += sy;
      p[o + 2] += sz;
    }
  }

  private carried(x: Float64Array, o: number, dx: number, dy: number, dz: number, dw: number) {
    const t = this.t, last = this.last, sub = this.sub;
    rotate(dx, dy, dz, dw, x[o] - last[0], x[o + 1] - last[1], x[o + 2] - last[2], t, 0);
    x[o] = sub[0] + t[0];
    x[o + 1] = sub[1] + t[1];
    x[o + 2] = sub[2] + t[2];
  }

  // ---- output ----

  /** The pendant's world pose (position xyz, quaternion xyzw). */
  pendant(out: Float64Array | number[]) {
    const p = this.p, t = this.t, c = this.cur;
    const s = this.s * 3, e = s + 3;
    let ax = p[e] - p[s], ay = p[e + 1] - p[s + 1], az = p[e + 2] - p[s + 2];
    const len = Math.sqrt(ax * ax + ay * ay + az * az);
    rotate(c[3], c[4], c[5], c[6], 0, -1, 0, t, 0);
    let fx = 0, fy = 0, fz = 0, fw = 1;
    if (len > 1e-9) {
      ax /= len;
      ay /= len;
      az /= len;
      const d = t[0] * ax + t[1] * ay + t[2] * az;
      if (d < -0.999999) {
        rotate(c[3], c[4], c[5], c[6], 1, 0, 0, t, 3);
        fx = t[3];
        fy = t[4];
        fz = t[5];
        fw = 0;
      } else {
        fx = t[1] * az - t[2] * ay;
        fy = t[2] * ax - t[0] * az;
        fz = t[0] * ay - t[1] * ax;
        fw = 1 + d;
        const l = Math.sqrt(fx * fx + fy * fy + fz * fz + fw * fw);
        fx /= l;
        fy /= l;
        fz /= l;
        fw /= l;
      }
    }
    const qx = c[3], qy = c[4], qz = c[5], qw = c[6];
    out[0] = p[s];
    out[1] = p[s + 1];
    out[2] = p[s + 2];
    out[3] = fw * qx + fx * qw + fy * qz - fz * qy;
    out[4] = fw * qy - fx * qz + fy * qw + fz * qx;
    out[5] = fw * qz + fx * qy - fy * qx + fz * qw;
    out[6] = fw * qw - fx * qx - fy * qy - fz * qz;
  }

  /** The rope's triangles. */
  triangles(): number[] {
    if (this.style === CORD) return grid(this.curvePoints, CORD_SIDES, false, 1, 0);
    const rings = this.style === CHAIN ? LINK_RING : BEAD_LAT + 1, sides = this.style === CHAIN ? LINK_TUBE : BEAD_LON;
    return grid(rings, sides, this.style === CHAIN, this.elements, this.template);
  }

  /** The rope's vertices and normals (vertexCount each, xyz) in the local space of `anchor`. */
  mesh(anchor: ArrayLike<number>, vertices: Float32Array, normals: Float32Array) {
    this.computeCurve();
    const t = this.t, curve = this.curve, frame = this.frame, arc = this.arc;
    const a = anchor[0], b = anchor[4], c = anchor[8], d = anchor[1], e = anchor[5], f = anchor[9], g = anchor[2], h = anchor[6], k = anchor[10];
    const A = e * k - f * h, B = -(d * k - f * g), C = d * h - e * g;
    const det = a * A + b * B + c * C;
    if (Math.abs(det) < 1e-18) return;
    const id = 1 / det;
    t[0] = A * id;
    t[1] = -(b * k - c * h) * id;
    t[2] = (b * f - c * e) * id;
    t[3] = B * id;
    t[4] = (a * k - c * g) * id;
    t[5] = -(a * f - c * d) * id;
    t[6] = C * id;
    t[7] = -(a * h - b * g) * id;
    t[8] = (a * e - b * d) * id;
    const tx = anchor[12], ty = anchor[13], tz = anchor[14];
    let n = 0;
    if (this.style === CORD) {
      for (let j = 0; j < this.curvePoints; j++)
        for (let s = 0; s < CORD_SIDES; s++) {
          const th = (2 * Math.PI * s) / CORD_SIDES, cs = Math.cos(th), sn = Math.sin(th);
          const f6 = j * 6;
          const nx = frame[f6] * cs + frame[f6 + 3] * sn, ny = frame[f6 + 1] * cs + frame[f6 + 4] * sn, nz = frame[f6 + 2] * cs + frame[f6 + 5] * sn;
          this.emit(vertices, normals, n++, curve[j * 3] + nx * this.radius, curve[j * 3 + 1] + ny * this.radius, curve[j * 3 + 2] + nz * this.radius, nx, ny, nz, tx, ty, tz, anchor);
        }
      return;
    }
    const total = arc[this.curvePoints - 1];
    let seg = 0;
    for (let el = 0; el < this.elements; el++) {
      const at = ((el + 0.5) / this.elements) * total;
      while (seg < this.curvePoints - 2 && arc[seg + 1] < at) seg++;
      const span = arc[seg + 1] - arc[seg], f0 = span > 1e-12 ? (at - arc[seg]) / span : 0;
      const p0 = seg * 3, p1 = p0 + 3;
      const cx = curve[p0] + (curve[p1] - curve[p0]) * f0, cy = curve[p0 + 1] + (curve[p1 + 1] - curve[p0 + 1]) * f0, cz = curve[p0 + 2] + (curve[p1 + 2] - curve[p0 + 2]) * f0;
      let lx = curve[p1] - curve[p0], ly = curve[p1 + 1] - curve[p0 + 1], lz = curve[p1 + 2] - curve[p0 + 2];
      const ll = Math.sqrt(lx * lx + ly * ly + lz * lz);
      if (ll < 1e-12) {
        lx = 0;
        ly = -1;
        lz = 0;
      } else {
        lx /= ll;
        ly /= ll;
        lz /= ll;
      }
      const f6 = seg * 6;
      let ux = frame[f6], uy = frame[f6 + 1], uz = frame[f6 + 2];
      const dot = ux * lx + uy * ly + uz * lz;
      ux -= lx * dot;
      uy -= ly * dot;
      uz -= lz * dot;
      const ul = Math.sqrt(ux * ux + uy * uy + uz * uz);
      ux /= ul;
      uy /= ul;
      uz /= ul;
      let vx = ly * uz - lz * uy, vy = lz * ux - lx * uz, vz = lx * uy - ly * ux;
      if (this.style === CHAIN && (el & 1) === 1) {
        const ox = ux, oy = uy, oz = uz;
        ux = vx;
        uy = vy;
        uz = vz;
        vx = -ox;
        vy = -oy;
        vz = -oz;
      }
      for (let i = 0; i < this.template; i++) {
        const o = i * 3;
        const pa = this.tplPos[o], pu = this.tplPos[o + 1], pv = this.tplPos[o + 2];
        const na = this.tplNrm[o], nu = this.tplNrm[o + 1], nv = this.tplNrm[o + 2];
        this.emit(
          vertices,
          normals,
          n++,
          cx + lx * pa + ux * pu + vx * pv,
          cy + ly * pa + uy * pu + vy * pv,
          cz + lz * pa + uz * pu + vz * pv,
          lx * na + ux * nu + vx * nv,
          ly * na + uy * nu + vy * nv,
          lz * na + uz * nu + vz * nv,
          tx,
          ty,
          tz,
          anchor,
        );
      }
    }
  }

  private emit(vertices: Float32Array, normals: Float32Array, i: number, x: number, y: number, z: number, nx: number, ny: number, nz: number, tx: number, ty: number, tz: number, m: ArrayLike<number>) {
    const t = this.t;
    x -= tx;
    y -= ty;
    z -= tz;
    const o = i * 3;
    vertices[o] = t[0] * x + t[1] * y + t[2] * z;
    vertices[o + 1] = t[3] * x + t[4] * y + t[5] * z;
    vertices[o + 2] = t[6] * x + t[7] * y + t[8] * z;
    const ax = m[0] * nx + m[1] * ny + m[2] * nz, ay = m[4] * nx + m[5] * ny + m[6] * nz, az = m[8] * nx + m[9] * ny + m[10] * nz;
    let l = Math.sqrt(ax * ax + ay * ay + az * az);
    if (l < 1e-12) l = 1;
    normals[o] = ax / l;
    normals[o + 1] = ay / l;
    normals[o + 2] = az / l;
  }

  private computeCurve() {
    const p = this.p, curve = this.curve, arc = this.arc, frame = this.frame, s = this.s, cp = this.curvePoints;
    let m = 0;
    for (let i = 0; i < s; i++) {
      const i0 = Math.max(i - 1, 0) * 3, i1 = i * 3, i2 = (i + 1) * 3, i3 = Math.min(i + 2, s) * 3;
      for (let j = 0; j < CORD_SUB; j++) {
        const t = j / CORD_SUB, t2 = t * t, t3 = t2 * t;
        for (let c = 0; c < 3; c++)
          curve[m * 3 + c] =
            0.5 *
            (2 * p[i1 + c] +
              (-p[i0 + c] + p[i2 + c]) * t +
              (2 * p[i0 + c] - 5 * p[i1 + c] + 4 * p[i2 + c] - p[i3 + c]) * t2 +
              (-p[i0 + c] + 3 * p[i1 + c] - 3 * p[i2 + c] + p[i3 + c]) * t3);
        m++;
      }
    }
    curve[m * 3] = p[s * 3];
    curve[m * 3 + 1] = p[s * 3 + 1];
    curve[m * 3 + 2] = p[s * 3 + 2];
    arc[0] = 0;
    let ux = 0, uy = 0, uz = 0;
    for (let j = 0; j < cp; j++) {
      const a = Math.max(j - 1, 0) * 3, b = Math.min(j + 1, cp - 1) * 3;
      let fx = curve[b] - curve[a], fy = curve[b + 1] - curve[a + 1], fz = curve[b + 2] - curve[a + 2];
      const fl = Math.sqrt(fx * fx + fy * fy + fz * fz);
      if (fl < 1e-12) {
        fx = 0;
        fy = -1;
        fz = 0;
      } else {
        fx /= fl;
        fy /= fl;
        fz /= fl;
      }
      if (j > 0) {
        const sx = curve[j * 3] - curve[j * 3 - 3], sy = curve[j * 3 + 1] - curve[j * 3 - 2], sz = curve[j * 3 + 2] - curve[j * 3 - 1];
        arc[j] = arc[j - 1] + Math.sqrt(sx * sx + sy * sy + sz * sz);
      }
      if (j === 0) {
        const rx = Math.abs(fx) < 0.9 ? 1 : 0, ry = Math.abs(fx) < 0.9 ? 0 : 1;
        ux = ry * fz;
        uy = -rx * fz;
        uz = rx * fy - ry * fx;
      } else {
        const dot = ux * fx + uy * fy + uz * fz;
        ux -= fx * dot;
        uy -= fy * dot;
        uz -= fz * dot;
      }
      let ul = Math.sqrt(ux * ux + uy * uy + uz * uz);
      if (ul < 1e-12) {
        const rx = Math.abs(fx) < 0.9 ? 1 : 0, ry = Math.abs(fx) < 0.9 ? 0 : 1;
        ux = ry * fz;
        uy = -rx * fz;
        uz = rx * fy - ry * fx;
        ul = Math.sqrt(ux * ux + uy * uy + uz * uz);
      }
      ux /= ul;
      uy /= ul;
      uz /= ul;
      const o = j * 6;
      frame[o] = ux;
      frame[o + 1] = uy;
      frame[o + 2] = uz;
      frame[o + 3] = fy * uz - fz * uy;
      frame[o + 4] = fz * ux - fx * uz;
      frame[o + 5] = fx * uy - fy * ux;
    }
  }

  private linkTemplate() {
    const w = this.thickness, r = w * LINK_WIRE, A = (w * LINK_LENGTH) / 2 - r, B = w / 2 - r;
    for (let i = 0; i < LINK_RING; i++) {
      const phi = (2 * Math.PI * i) / LINK_RING, ca = Math.cos(phi), sa = Math.sin(phi);
      let mx = ca / A, mu = sa / B;
      const ml = Math.sqrt(mx * mx + mu * mu);
      mx /= ml;
      mu /= ml;
      for (let k = 0; k < LINK_TUBE; k++) {
        const psi = (2 * Math.PI * k) / LINK_TUBE, cp = Math.cos(psi), sp = Math.sin(psi);
        const o = (i * LINK_TUBE + k) * 3;
        const na = mx * cp, nu = mu * cp, nv = -sp;
        this.tplNrm[o] = na;
        this.tplNrm[o + 1] = nu;
        this.tplNrm[o + 2] = nv;
        this.tplPos[o] = A * ca + na * r;
        this.tplPos[o + 1] = B * sa + nu * r;
        this.tplPos[o + 2] = nv * r;
      }
    }
  }

  private beadTemplate() {
    const r = this.thickness / 2;
    for (let i = 0; i <= BEAD_LAT; i++) {
      const a = (Math.PI * i) / BEAD_LAT, ca = Math.cos(a), sa = Math.sin(a);
      for (let k = 0; k < BEAD_LON; k++) {
        const b = (2 * Math.PI * k) / BEAD_LON;
        const o = (i * BEAD_LON + k) * 3;
        const na = ca, nu = Math.cos(b) * sa, nv = -Math.sin(b) * sa;
        this.tplNrm[o] = na;
        this.tplNrm[o + 1] = nu;
        this.tplNrm[o + 2] = nv;
        this.tplPos[o] = na * r;
        this.tplPos[o + 1] = nu * r;
        this.tplPos[o + 2] = nv * r;
      }
    }
  }
}

function grid(rings: number, sides: number, closed: boolean, copies: number, stride: number): number[] {
  const t: number[] = [];
  for (let c = 0; c < copies; c++)
    for (let i = 0; i < (closed ? rings : rings - 1); i++)
      for (let k = 0; k < sides; k++) {
        const a = c * stride + i * sides + k, b = c * stride + i * sides + ((k + 1) % sides);
        const d = c * stride + ((i + 1) % rings) * sides + k, e = c * stride + ((i + 1) % rings) * sides + ((k + 1) % sides);
        t.push(a, b, d, b, e, d);
      }
  return t;
}

function pose(m: ArrayLike<number>, out: Float64Array) {
  out[0] = m[12];
  out[1] = m[13];
  out[2] = m[14];
  const sx = Math.sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
  const sy = Math.sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
  const sz = Math.sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
  const m00 = m[0] / sx, m10 = m[1] / sx, m20 = m[2] / sx;
  const m01 = m[4] / sy, m11 = m[5] / sy, m21 = m[6] / sy;
  const m02 = m[8] / sz, m12 = m[9] / sz, m22 = m[10] / sz;
  const tr = m00 + m11 + m22;
  let x: number, y: number, z: number, w: number;
  if (tr > 0) {
    const s = 0.5 / Math.sqrt(tr + 1);
    w = 0.25 / s;
    x = (m21 - m12) * s;
    y = (m02 - m20) * s;
    z = (m10 - m01) * s;
  } else if (m00 > m11 && m00 > m22) {
    const s = 2 * Math.sqrt(1 + m00 - m11 - m22);
    w = (m21 - m12) / s;
    x = 0.25 * s;
    y = (m01 + m10) / s;
    z = (m02 + m20) / s;
  } else if (m11 > m22) {
    const s = 2 * Math.sqrt(1 + m11 - m00 - m22);
    w = (m02 - m20) / s;
    x = (m01 + m10) / s;
    y = 0.25 * s;
    z = (m12 + m21) / s;
  } else {
    const s = 2 * Math.sqrt(1 + m22 - m00 - m11);
    w = (m10 - m01) / s;
    x = (m02 + m20) / s;
    y = (m12 + m21) / s;
    z = 0.25 * s;
  }
  const l = Math.sqrt(x * x + y * y + z * z + w * w);
  out[3] = x / l;
  out[4] = y / l;
  out[5] = z / l;
  out[6] = w / l;
}

function rotate(x: number, y: number, z: number, w: number, vx: number, vy: number, vz: number, o: Float64Array, at: number) {
  const cx = y * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - y * vx;
  const ex = y * cz - z * cy, ey = z * cx - x * cz, ez = x * cy - y * cx;
  o[at] = vx + 2 * (w * cx + ex);
  o[at + 1] = vy + 2 * (w * cy + ey);
  o[at + 2] = vz + 2 * (w * cz + ez);
}

function slerp(a: Float64Array, b: Float64Array, f: number, o: Float64Array) {
  let bx = b[3], by = b[4], bz = b[5], bw = b[6];
  let d = a[3] * bx + a[4] * by + a[5] * bz + a[6] * bw;
  if (d < 0) {
    d = -d;
    bx = -bx;
    by = -by;
    bz = -bz;
    bw = -bw;
  }
  let ka: number, kb: number;
  if (d > 0.9995) {
    ka = 1 - f;
    kb = f;
  } else {
    const th = Math.acos(d), sn = Math.sin(th);
    ka = Math.sin((1 - f) * th) / sn;
    kb = Math.sin(f * th) / sn;
  }
  const x = a[3] * ka + bx * kb, y = a[4] * ka + by * kb, z = a[5] * ka + bz * kb, w = a[6] * ka + bw * kb;
  const l = Math.sqrt(x * x + y * y + z * z + w * w);
  o[3] = x / l;
  o[4] = y / l;
  o[5] = z / l;
  o[6] = w / l;
}
