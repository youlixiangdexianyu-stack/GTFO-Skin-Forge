import { Edges, useGLTF } from '@react-three/drei';
import { createPortal, useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AdditiveBlending,
  CanvasTexture,
  Color,
  DoubleSide,
  Group,
  MeshBasicMaterial,
  Quaternion,
  SRGBColorSpace,
  Vector3,
  type Camera,
  type Material,
  type Mesh,
  type MeshStandardMaterial,
  type Object3D,
} from 'three';
import { borderCanvas, glyphCanvas, GLYPHS, isAmmo, isBounded, isPercent, screenDigits, screenTextCanvas } from '../frames';
import { loadFont } from '../fonts';
import { fileUrl, type Attachment, type Screen, type TriggerEffect } from '../protocol';
import { unityToThree } from '../space';
import { useItem, useStore } from '../store';
import { anchorObject, previewEvents, quiet, SCENE_EVENT, viewRefs } from './refs';
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
    canvas.width = px;
    canvas.height = Math.max(8, Math.round((px * h) / w));
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

// Charm swing (Attachment.Swing), Native/Effects.cs `Pendulum` with the same formula and constants: a tip SWING_LENGTH
// below the origin, kept velocity and pull back to the rest pose set per frame by looseness, gravity, all in world space;
// the attachment turns from its rest pose to point at the tip. The rest pose is the group's (the inspector's pose); the
// swing turns the body inside it. Driven by the gear's motion in the 3D view (moving the attachment or its part) and by
// 晃一下, which jolts the tip sideways.
const SWING_LENGTH = 0.05; // Pendulum.Length
const GRAVITY = new Vector3(0, -9.81, 0); // Unity's default Physics.gravity
const KICK = 0.01; // 晃一下: metres the tip is thrown sideways in one frame
const DOWN = new Vector3(0, -1, 0);
const lerp = (a: number, b: number, t: number) => a + (b - a) * t;

class Pendulum {
  private tip = new Vector3();
  private last = new Vector3();
  private started = false;
  private shake = previewEvents.shake;
  private pivot = new Vector3();
  private rest = new Quaternion();
  private v = new Vector3();

  update(group: Group, body: Group, looseness: number, delta: number, camera: Camera) {
    const stiffness = lerp(0.2, 0.01, looseness);
    const keep = lerp(0.85, 0.985, looseness);
    group.getWorldPosition(this.pivot);
    group.getWorldQuaternion(this.rest);
    const restDown = DOWN.clone().applyQuaternion(this.rest);
    const restTip = this.pivot.clone().addScaledVector(restDown, SWING_LENGTH);
    if (!this.started) {
      this.tip.copy(restTip);
      this.last.copy(restTip);
      this.started = true;
    }
    if (previewEvents.shake !== this.shake) {
      this.shake = previewEvents.shake;
      this.last.addScaledVector(this.v.setFromMatrixColumn(camera.matrixWorld, 0).normalize(), KICK);
    }
    const dt = Math.min(delta, 0.05);
    const next = this.tip
      .clone()
      .addScaledVector(this.v.subVectors(this.tip, this.last), keep)
      .addScaledVector(GRAVITY, dt * dt)
      .addScaledVector(restTip.sub(this.tip), stiffness);
    this.last.copy(this.tip);
    this.tip.copy(this.pivot).addScaledVector(next.sub(this.pivot).normalize(), SWING_LENGTH);
    // World: FromToRotation(restDown, tip - pivot) * rest; the body sits under the group, so in its space.
    const turn = new Quaternion().setFromUnitVectors(restDown, this.v.subVectors(this.tip, this.pivot).normalize());
    body.quaternion.copy(this.rest).invert().multiply(turn).multiply(this.rest);
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
  const pendulum = useRef<Pendulum | null>(null);
  useFrame(({ camera }, delta) => {
    const g = group.current;
    if (!g) return;
    if (a.faceCamera) g.lookAt(camera.position); // visible face (three +Z) toward camera
    playEffects(g, a);
    if (body.current) {
      if (a.hologram?.flicker) flicker(body.current, a.hologram.flicker, flick.current, performance.now() / 1000);
      else body.current.visible = true;
      if (a.swing) (pendulum.current ??= new Pendulum()).update(g, body.current, a.swing.looseness ?? 0.5, delta, camera);
      else if (pendulum.current) {
        pendulum.current = null;
        body.current.quaternion.identity();
      }
    }
  });
  useEffect(() => {
    const g = group.current;
    if (g) viewRefs.attachments.set(a.id!, g);
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
