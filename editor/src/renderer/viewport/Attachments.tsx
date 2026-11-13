import { Edges, useGLTF } from '@react-three/drei';
import { createPortal, useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AdditiveBlending,
  Box3,
  BufferAttribute,
  BufferGeometry,
  CanvasTexture,
  Color,
  DoubleSide,
  Group,
  Matrix4,
  MeshBasicMaterial,
  Quaternion,
  SRGBColorSpace,
  Vector3,
  type Material,
  type Mesh,
  type MeshStandardMaterial,
  type Object3D,
} from 'three';
import { CHARM_DEFAULTS, charmMetallic, charmSmoothness, CharmRope, MAX_BOXES, type CharmSettings } from './charm';
import { borderCanvas, glyphCanvas, GLYPHS, isAmmo, isBounded, isPercent, screenDigits, screenTextCanvas } from '../frames';
import { loadFont } from '../fonts';
import { fileUrl, type Attachment, type Screen, type TriggerEffect } from '../protocol';
import { unityToThree } from '../space';
import { useItem, useStore } from '../store';
import { anchorObject, ATTACHMENT_EVENT, partBox, previewEvents, quiet, SCENE_EVENT, viewRefs } from './refs';
import { t } from '../i18n';

const rgb = (c?: number[] | null) => `rgb(${(c?.[0] ?? 1) * 255},${(c?.[1] ?? 1) * 255},${(c?.[2] ?? 1) * 255})`;
const mix = (a: number[], b: number[], t: number) => a.map((v, i) => v + (b[i] - v) * t);
const PERCENT_SAMPLE = 87; // what a percentage screen shows in the editor

/** Screen preview: panel, inner border and a sample value drawn with the glyph strip, or as a bar. */
function ScreenMesh({ screen, hologram, selected, onPick }: { screen: Screen; hologram: boolean; selected: boolean; onPick: (e: ThreeEvent<MouseEvent>) => void }) {
  const [w, h] = screen.size ?? [0.03, 0.015];
  const sample = useRef(0);
  const canvas = useMemo(() => document.createElement('canvas'), []);
  const texture = useMemo(() => {
    const t = new CanvasTexture(canvas);
    t.colorSpace = SRGBColorSpace;
    return t;
  }, [canvas]);
  useEffect(() => () => texture.dispose(), [texture]);
  // Redrawn once the chosen font has loaded (canvas text does not wait for it).
  const [glyphs, setGlyphs] = useState(() => glyphCanvas(screen.font));
  useEffect(() => {
    let live = true;
    loadFont(screen.font, screen.source === 'static' ? screen.staticText || ' ' : GLYPHS).then(
      () => live && setGlyphs(glyphCanvas(screen.font)),
      () => useStore.getState().log(t('attachments.k1', { v0: screen.font })),
    );
    return () => void (live = false);
  }, [screen.font, screen.source, screen.staticText]);

  const { digits, max, grow } = screenDigits(screen, useItem());
  const ammo = isAmmo(screen.source);
  const percent = isPercent(screen.source);
  const bar = screen.style === 'bar' && isBounded(screen.source);
  // Ammo counts down from the largest value the source shows; percentages show a sample, counts count up.
  const top = ammo ? (max ?? 30) : 100;
  const kills = screen.source === 'kills';

  // 0: text colour, 1: low-ammo colour. With the gradient on, the tint blends in from the chosen clip percentage.
  const tint = (v: number) => {
    if (!screen.lowAmmoColor || !ammo || top <= 0) return 0;
    const threshold = screen.lowAmmoFraction ?? 0.2;
    const fraction = Math.min(1, Math.max(0, v / top));
    const from = screen.lowAmmoGradient ? Math.max(threshold, Math.min(1, screen.lowAmmoGradientStart ?? 0.4)) : threshold;
    return from <= threshold ? (fraction <= threshold ? 1 : 0) : Math.min(1, Math.max(0, (from - fraction) / (from - threshold)));
  };

  // Redraws the face as the plugin builds it: an opaque border ring, an opaque panel inside it (the game's gear shader
  // has no transparency, so only fully transparent leaves a part out), the text shrunk to fit; counts down so the
  // low-ammo colour (text and glow) shows too.
  const draw = (shown: number) => {
    const px = 512;
    const height = Math.max(8, Math.round((px * h) / w));
    // A new aspect needs a new GPU texture: an upload into the old allocation leaves stale rows of the previous face.
    if (canvas.height !== height) texture.dispose();
    canvas.width = px;
    canvas.height = height;
    const ctx = canvas.getContext('2d')!;
    const H = canvas.height;
    const unit = px / w; // canvas pixels per metre
    ctx.clearRect(0, 0, px, H);
    const glow = screen.glowInGame !== false ? 6 * (screen.glow ?? 1.5) : 0;
    const bw = Math.min(screen.borderWidth ?? 0.0008, Math.min(w, h) / 2) * unit;
    const panel = screen.panelColor ?? [0, 0, 0, 0.85];
    if ((panel[3] ?? 1) > 0.01) {
      ctx.fillStyle = rgb(panel);
      ctx.fillRect(bw, bw, px - 2 * bw, H - 2 * bw);
    }
    const border = screen.borderColor ?? [1, 1, 1, 1];
    if ((border[3] ?? 1) > 0.01 && bw > 0) {
      const b = borderCanvas(screen, px);
      const g = b.getContext('2d')!;
      g.globalCompositeOperation = 'source-in';
      g.fillStyle = rgb(border);
      g.fillRect(0, 0, b.width, b.height);
      ctx.save();
      ctx.shadowColor = rgb(border);
      ctx.shadowBlur = glow;
      ctx.drawImage(b, 0, 0, px, H);
      ctx.restore();
    }

    const lowAmount = tint(shown);
    const colour = screen.lowAmmoColor && lowAmount > 0 ? mix(screen.textColor ?? [1, 1, 1], screen.lowAmmoColor, lowAmount) : screen.textColor;
    if (bar) {
      // As the plugin builds it: from the border's inner left edge, as high as the text, the value's fraction wide.
      const bh = Math.min(H * (screen.textScale ?? 0.8), H - 2 * bw);
      ctx.shadowColor = rgb(colour);
      ctx.shadowBlur = glow;
      ctx.fillStyle = rgb(colour);
      ctx.fillRect(bw, (H - bh) / 2, (px - 2 * bw) * Math.min(1, Math.max(0, shown / top)), bh);
      texture.needsUpdate = true;
      return;
    }
    const inner = (px - 2 * bw) * 0.95;
    let gh = H * (screen.textScale ?? 0.8);
    let image: HTMLCanvasElement;
    if (screen.source === 'static') {
      image = screenTextCanvas(screen.font, screen.staticText ?? '');
      gh *= Math.min(1, inner / ((gh * image.width) / image.height));
    } else {
      // Cells of the glyph strip; a counter keeps its width (blank cells for missing leading zeros), automatic counts
      // show only their own digits; percentages end in '%'.
      const v = Math.min(Math.max(0, shown), 10 ** digits - 1);
      const text = (grow ? String(v) : String(v).padStart(digits, screen.padZero !== false ? '0' : ' ')) + (percent ? '%' : '');
      const cellW = glyphs.width / GLYPHS.length;
      gh *= Math.min(1, inner / ((gh * cellW * text.length) / glyphs.height));
      image = document.createElement('canvas');
      image.width = cellW * text.length;
      image.height = glyphs.height;
      const g = image.getContext('2d')!;
      [...text].forEach((ch, i) => {
        const gi = GLYPHS.indexOf(ch);
        if (gi >= 0) g.drawImage(glyphs, gi * cellW, 0, cellW, glyphs.height, i * cellW, 0, cellW, glyphs.height);
      });
    }
    const t = image.getContext('2d')!;
    t.globalCompositeOperation = 'source-in';
    t.fillStyle = rgb(colour);
    t.fillRect(0, 0, image.width, image.height);
    const gw = (gh * image.width) / image.height;
    ctx.shadowColor = rgb(colour);
    ctx.shadowBlur = glow;
    ctx.drawImage(image, (px - gw) / 2, (H - gh) / 2, gw, gh);
    texture.needsUpdate = true;
  };

  useEffect(() => draw(kills ? previewEvents.kills : percent ? PERCENT_SAMPLE : ammo ? top : sample.current), [screen, glyphs, top]); // eslint-disable-line react-hooks/exhaustive-deps
  const last = useRef(0);
  useFrame(({ clock }) => {
    if (kills) {
      // The kill count changes only on 模拟击杀.
      if (sample.current !== previewEvents.kills) draw((sample.current = previewEvents.kills));
      return;
    }
    if (screen.source === 'static' || percent || clock.elapsedTime - last.current < 0.35) return;
    last.current = clock.elapsedTime;
    if (!ammo) {
      // Damage counts up by a sample hit.
      sample.current = (sample.current + 37) % 10 ** digits;
      draw(sample.current);
      return;
    }
    sample.current = (sample.current + 1) % (top + 1);
    draw(top - sample.current);
  });

  return (
    <group>
      <mesh onClick={onPick} renderOrder={2}>
        <planeGeometry args={[w, h]} />
        <meshBasicMaterial key={String(hologram)} map={texture} transparent toneMapped={false} side={DoubleSide} depthWrite={false} blending={hologram ? AdditiveBlending : undefined} />
        {selected && <Edges color="#ff7a2f" renderOrder={3} />}
      </mesh>
    </group>
  );
}

function MeshModel({ glb, attachment, onPick }: { glb: string; attachment: Attachment; onPick: (e: ThreeEvent<MouseEvent>) => void }) {
  const gltf = useGLTF(fileUrl(glb));
  const scene = useMemo(() => gltf.scene.clone(true), [gltf.scene]);
  useEffect(() => {
    const m = attachment.material;
    const c = m?.color ?? [1, 1, 1, 1];
    const made: Material[] = [];
    scene.traverse((o) => {
      const mesh = o as Mesh;
      if (!mesh.isMesh) return;
      const src = mesh.material as MeshStandardMaterial;
      const original = (mesh.userData.original ??= src) as MeshStandardMaterial;
      // A hologram glows additively in its colour (the game's hologram shader is checked in game, PLAN §6).
      const next = attachment.hologram
        ? new MeshBasicMaterial({ map: original.map, toneMapped: false, transparent: true, blending: AdditiveBlending, depthWrite: false, side: DoubleSide })
        : m?.unlit ? new MeshBasicMaterial({ map: original.map, toneMapped: false }) : original.clone();
      next.color.setRGB(c[0], c[1], c[2]);
      mesh.material = next;
      made.push(next);
    });
    // The next look (or leaving) replaces these copies; the loaded originals stay with the glTF.
    return () => made.forEach((x) => x.dispose());
  }, [scene, attachment.material, attachment.hologram]);
  return <primitive object={scene} onClick={onPick} />;
}

const since = (e: TriggerEffect) => {
  const at = previewEvents.at[e.trigger ?? 'kill'];
  return at === undefined ? Infinity : (performance.now() - at) / 1000;
};

/**
 * Attachment effects as the plugin plays them (Native/Effects.cs `Show`, `Flash`): `show` hides it except for Seconds
 * after its event; `flash` takes the emission from its own colour to Color x Intensity and back, k = 1 - |2t - 1| over
 * Seconds. Each running flash writes the colour in turn, so the last one in the list wins.
 */
function playEffects(g: Group, a: Attachment) {
  const effects = (a.effects ?? []).filter((e): e is TriggerEffect => !!e);
  const shows = effects.filter((e) => e.action === 'show');
  g.visible = !shows.length || shows.some((e) => since(e) < (e.seconds ?? 0.3));
  let k = 0;
  const peak = new Color();
  for (const e of effects) {
    const t = since(e) / Math.max(e.seconds ?? 0.3, 0.01); // AttachmentEffect defaults: Seconds 0.3, Intensity 1
    if (e.action === 'show' || t >= 1) continue;
    const c = e.color ?? [1, 1, 1];
    peak.setRGB(c[0], c[1], c[2]).multiplyScalar(e.intensity ?? 1);
    k = 1 - Math.abs(2 * t - 1);
  }
  g.traverse((o) => {
    const m = (o as Mesh).material as (MeshStandardMaterial & MeshBasicMaterial) | undefined;
    if (!m || Array.isArray(m)) return;
    if (m.isMeshStandardMaterial) {
      const base = (m.userData.sfEmissive ??= {
        color: m.emissive.clone(),
        k: m.emissiveIntensity,
      }) as { color: Color; k: number };
      if (k > 0) {
        m.emissive.copy(base.color).multiplyScalar(base.k).lerp(peak, k);
        m.emissiveIntensity = 1;
      } else {
        m.emissive.copy(base.color);
        m.emissiveIntensity = base.k;
      }
    } else if (m.isMeshBasicMaterial) {
      // No emission here: the colour is what glows.
      const base = (m.userData.sfColor ??= m.color.clone()) as Color;
      m.color.copy(base).lerp(peak, k);
    }
  });
}

// Charm (Attachment.charm): the rope of charm.ts, the game's algorithm, in world space. The preview motions move the whole
// gear through a motion matrix the view does not show: the rope is simulated in that moving space and drawn back in the
// still one, so the gun stays put on screen while the charm swings as it does in a first-person view.
const SHAKE_SECONDS = 0.6, RUN_SECONDS = 2.6, TURN_SECONDS = 2.0;
const RUN_SPEED = 4, RUN_RAMP = 0.3; // m/s, s to reach it and to stop
const TURN_RADIUS = 0.4; // m from the turning player's body to the gun
const smooth = (x: number) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));

/** Distance run after `t` seconds: speed up over RUN_RAMP, hold, slow down over RUN_RAMP before RUN_SECONDS - 0.3. */
function runDistance(t: number) {
  const a = RUN_SPEED / RUN_RAMP, stop = RUN_SECONDS - 0.3, hold = stop - RUN_RAMP;
  const d1 = 0.5 * a * RUN_RAMP * RUN_RAMP, d2 = d1 + RUN_SPEED * (hold - RUN_RAMP);
  if (t < RUN_RAMP) return 0.5 * a * t * t;
  if (t < hold) return d1 + RUN_SPEED * (t - RUN_RAMP);
  const u = Math.min(t, stop) - hold;
  return d2 + RUN_SPEED * u - 0.5 * a * u * u;
}

const mx = new Matrix4(), my = new Matrix4(), fwd = new Vector3(), side = new Vector3(), up = new Vector3(0, 1, 0), pivot = new Vector3();

/** The motion matrix of the playing preview motion (identity when none plays), around the gear's root; `body` gets the
 * player's own move in it (the run's, without the step bob and sway; a turn and a jolt move only the gun). */
function motionMatrix(out: Matrix4, root: Object3D | null, body: Float64Array) {
  out.identity();
  body.fill(0);
  const { motion, at } = previewEvents.charm;
  const t = (performance.now() - at) / 1000;
  if (!root || t < 0) return out;
  // The gun's barrel is Unity +Z, three -Z (space.ts); flattened, it is where the player runs.
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
    const d = runDistance(t), bob = 0.02 * Math.sin(2 * Math.PI * 2.6 * t) * env, sway = 0.012 * Math.sin(2 * Math.PI * 1.3 * t) * env;
    out.makeTranslation(fwd.x * d + side.x * sway, bob, fwd.z * d + side.z * sway);
    body[0] = fwd.x * d;
    body[2] = fwd.z * d;
  } else if (motion === 'turn' && t < TURN_SECONDS) {
    // Half a turn about the player behind the gun, a pause, and back.
    const angle = Math.PI * (smooth(t / 0.5) - smooth((t - 1.1) / 0.5));
    root.getWorldPosition(pivot).addScaledVector(fwd, -TURN_RADIUS);
    out.makeTranslation(pivot.x, pivot.y, pivot.z).multiply(my.makeRotationY(angle)).multiply(mx.makeTranslation(-pivot.x, -pivot.y, -pivot.z));
  }
  return out;
}

/** The gear's body as charm.ts takes it: per part, the box (in the gear root's frame) around its models' geometry, leaving
 * out other parts' nodes, attachments and models the game does not show; at most MAX_BOXES, largest first. */
function bodyBoxes(): { min: number[]; max: number[] }[] {
  const root = viewRefs.root;
  if (!root) return [];
  const out: { min: number[]; max: number[] }[] = [];
  for (const part of viewRefs.partNodes.keys()) {
    const box = partBox(part, root);
    if (!box.isEmpty()) out.push({ min: box.min.toArray(), max: box.max.toArray() });
  }
  const volume = (b: { min: number[]; max: number[] }) => (b.max[0] - b.min[0]) * (b.max[1] - b.min[1]) * (b.max[2] - b.min[2]);
  return out.sort((a, b) => volume(b) - volume(a)).slice(0, MAX_BOXES);
}

/** The pendant's centre below its origin, as the plugin takes it from the model's bounds (a screen hangs by its middle). */
function pendantArm(body: Group, scaleY: number): number {
  const box = new Box3(), m = new Matrix4(), inv = new Matrix4().copy(body.matrixWorld).invert();
  body.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh || !mesh.geometry) return;
    if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
    box.union(mesh.geometry.boundingBox!.clone().applyMatrix4(m.multiplyMatrices(inv, mesh.matrixWorld)));
  });
  const arm = box.isEmpty() ? 0 : (-(box.min.y + box.max.y) / 2) * Math.abs(scaleY);
  return Math.min(Math.max(arm, 0.004), 0.1);
}

/** One charm's preview: its rope, the geometry it fills and the key it was built for. */
class CharmPreview {
  readonly rope: CharmRope;
  readonly geometry = new BufferGeometry();
  private readonly positions: Float32Array;
  private readonly normals: Float32Array;
  private readonly pose = new Float64Array(7);
  private readonly sim = new Matrix4();
  private readonly motion = new Matrix4();
  private readonly body = new Float64Array(3);
  private readonly v = new Vector3();
  private readonly q = new Quaternion();
  private readonly s = new Vector3();
  private readonly aq = new Quaternion();

  readonly key: string;

  constructor(key: string, charm: CharmSettings, arm: number, group: Group) {
    this.key = key;
    this.rope = new CharmRope(charm, arm);
    if (charm.collide ?? CHARM_DEFAULTS.collide) {
      group.updateWorldMatrix(true, false);
      const gear = viewRefs.root?.matrixWorld.elements;
      if (gear) for (const b of bodyBoxes()) this.rope.addBox(group.matrixWorld.elements, gear, b.min, b.max);
    }
    const n = this.rope.vertexCount * 3;
    this.positions = new Float32Array(n);
    this.normals = new Float32Array(n);
    this.geometry.setAttribute('position', new BufferAttribute(this.positions, 3));
    this.geometry.setAttribute('normal', new BufferAttribute(this.normals, 3));
    this.geometry.setIndex(this.rope.triangles());
  }

  update(group: Group, body: Group) {
    group.updateWorldMatrix(true, false);
    this.sim.multiplyMatrices(motionMatrix(this.motion, viewRefs.root, this.body), group.matrixWorld);
    const e = this.sim.elements;
    this.rope.update(performance.now() / 1000, e, this.body);
    // Vertices relative to the moving hanging point are relative to the still group too.
    this.rope.mesh(e, this.positions, this.normals);
    this.geometry.attributes.position.needsUpdate = true;
    this.geometry.attributes.normal.needsUpdate = true;
    this.rope.pendant(this.pose);
    this.sim.decompose(this.v, this.aq, this.s);
    body.position.set(this.pose[0], this.pose[1], this.pose[2]).applyMatrix4(this.sim.invert());
    body.quaternion.copy(this.aq.invert()).multiply(this.q.set(this.pose[3], this.pose[4], this.pose[5], this.pose[6]));
  }
}

/** Hologram flicker as the plugin plays it: brief random drop-outs, more often with a higher amount. */
function flicker(g: Group, amount: number, at: { next: number; until: number }, now: number) {
  if (at.until >= 0) {
    if (now < at.until) return;
    at.until = -1;
    g.visible = true;
    at.next = now + (Math.random() < 0.3 ? 0.06 : (0.3 + Math.random() * 1.4) * (2.2 - 2 * amount));
    return;
  }
  if (now < at.next) return;
  at.until = now + 0.02 + Math.random() * 0.06;
  g.visible = false;
}

function AttachmentNode({ a, anchor }: { a: Attachment; anchor: Object3D }) {
  const group = useRef<Group>(null);
  const body = useRef<Group>(null);
  const flick = useRef({ next: 0, until: -1 });
  const selection = useStore((s) => s.selection);
  const meshPreviews = useStore((s) => (s.doc.editor as Record<string, Record<string, string>> | undefined)?.meshPreviews);
  const selected = selection?.kind === 'attachment' && selection.id === a.id;
  const pose = unityToThree({
    position: (a.position ?? [0, 0, 0]) as [number, number, number],
    rotation: (a.rotation ?? [0, 0, 0]) as [number, number, number],
    scale: (a.scale ?? [1, 1, 1]) as [number, number, number],
  });

  useEffect(() => {
    const g = group.current;
    if (!g) return;
    if (viewRefs.modalActive && selected) return; // the modal transform drives the group directly
    g.position.copy(pose.position);
    g.quaternion.copy(pose.quaternion);
    g.scale.copy(pose.scale);
  });
  const charm = useRef<CharmPreview | null>(null);
  const rope = useRef<Mesh>(null);
  useEffect(() => () => charm.current?.geometry.dispose(), []);
  useFrame(({ camera }) => {
    const g = group.current;
    if (!g) return;
    if (a.faceCamera) g.lookAt(camera.position); // visible face (three +Z) toward camera
    playEffects(g, a);
    if (body.current) {
      if (a.hologram?.flicker) flicker(body.current, a.hologram.flicker, flick.current, performance.now() / 1000);
      else body.current.visible = true;
      if (a.charm && rope.current) {
        // Built again when its settings, the pendant's model or the body it keeps out of change.
        const arm = pendantArm(body.current, a.scale?.[1] ?? 1);
        const key = JSON.stringify([a.charm, arm.toFixed(4), a.part, a.transform, a.position, a.rotation, a.scale, viewRefs.partNodes.size]);
        if (charm.current?.key !== key) {
          charm.current?.geometry.dispose();
          charm.current = new CharmPreview(key, a.charm, arm, g);
          rope.current.geometry = charm.current.geometry;
        }
        charm.current.update(g, body.current);
      } else if (charm.current) {
        charm.current.geometry.dispose();
        charm.current = null;
        body.current.position.set(0, 0, 0);
        body.current.quaternion.identity();
      }
    }
  });
  const c = a.charm;
  const ropeColor = useMemo(() => new Color().setRGB(c?.color?.[0] ?? 0.8, c?.color?.[1] ?? 0.8, c?.color?.[2] ?? 0.82), [c?.color]);
  const style = c?.style ?? CHARM_DEFAULTS.style;
  useEffect(() => {
    const g = group.current;
    if (g) viewRefs.attachments.set(a.id!, g);
    window.dispatchEvent(new Event(ATTACHMENT_EVENT));
    return () => void viewRefs.attachments.delete(a.id!);
  }, [a.id]);

  const onPick = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || quiet()) return;
    e.stopPropagation();
    useStore.getState().select({ kind: 'attachment', id: a.id! });
  };

  if (a.visible === false) return null;
  const glb = meshPreviews?.[a.id!];
  return createPortal(
    <group ref={group} name={`attachment:${a.id}`}>
      <group ref={body}>
        {a.screen && <ScreenMesh screen={a.screen} hologram={!!a.hologram} selected={selected} onPick={onPick} />}
        {glb && <MeshModel glb={glb} attachment={a} onPick={onPick} />}
      </group>
      {c && (
        <mesh ref={rope} frustumCulled={false}>
          <meshStandardMaterial color={ropeColor} metalness={charmMetallic(style)} roughness={1 - charmSmoothness(style)} />
        </mesh>
      )}
      {c && (
        // The hanging point, drawn over the gun (editor only): select it and drag the gizmo to move where the rope starts.
        <mesh renderOrder={10} onClick={onPick}>
          <sphereGeometry args={[selected ? 0.003 : 0.002, 16, 8]} />
          <meshBasicMaterial color="#ff7a2f" depthTest={false} depthWrite={false} transparent toneMapped={false} />
        </mesh>
      )}
    </group>,
    anchor,
  );
}

export function Attachments() {
  const attachments = useStore((s) => s.doc.attachments);
  const [, force] = useState(0);
  // Anchors exist only after the weapon model mounted.
  useEffect(() => {
    const f = () => force((n) => n + 1);
    window.addEventListener(SCENE_EVENT, f);
    return () => window.removeEventListener(SCENE_EVENT, f);
  }, []);
  return (
    <>
      {(attachments ?? []).map((a) => {
        if (!a) return null;
        const anchor = anchorObject(a.part ?? '', a.transform);
        return anchor ? <AttachmentNode key={a.id} a={a} anchor={anchor} /> : null;
      })}
    </>
  );
}
