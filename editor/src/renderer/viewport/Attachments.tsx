import { useGLTF } from '@react-three/drei';
import { createPortal, useFrame } from '@react-three/fiber';
import { Component, Suspense, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
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
  type Texture,
} from 'three';
import { holdTextures, loadTexture, releaseTextures, textureKey } from './textures';
import { CHARM_DEFAULTS, MAX_BOXES, type CharmSettings } from './charm';
import { motionMatrix, MOTION_SECONDS, PREVIEW_FPS, SETTLE_SECONDS } from './motion';
import { baseTexturePath, borderCanvas, glyphCanvas, GLYPHS, isAmmo, isBounded, isPercent, screenDigits, screenTextCanvas } from '../frames';
import { loadFont } from '../fonts';
import { attachmentMaterial, call, CHANNELS, fileUrl, type Attachment, type DynamicLayer, type Methods, type Screen, type TextureChannel, type TriggerEffect } from '../protocol';
import { friendly } from '../errors';
import { unityToThree } from '../space';
import { materialOf, useItem, useStore } from '../store';
import { hasLivery, paintTint } from '../livery';
import { withVariant } from '../variants';
import { LiveryPreview } from './liveryPreview';
import { Compositor } from './WeaponModel';
import { anchorObject, ATTACHMENT_EVENT, Loading, partBox, previewEvents, track, useSceneVersion, viewRefs, type CharmMotion } from './refs';
import { t } from '../i18n';
import { rampTexture } from '../hologram';
import { notify } from '../ui/notify';

const rgb = (c?: number[] | null) => `rgb(${(c?.[0] ?? 1) * 255},${(c?.[1] ?? 1) * 255},${(c?.[2] ?? 1) * 255})`;
const mix = (a: number[], b: number[], t: number) => a.map((v, i) => v + (b[i] - v) * t);
const PERCENT_SAMPLE = 87; // what a percentage screen shows in the editor

/** Screen preview: panel, inner border and a sample value drawn with the glyph strip, or as a bar. */
function ScreenMesh({ screen, hologram }: { screen: Screen; hologram: Attachment['hologram'] }) {
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
    track(loadFont(screen.font, screen.source === 'static' ? screen.staticText || ' ' : GLYPHS)).then(
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
      <mesh renderOrder={2}>
        <planeGeometry args={[w, h]} />
        <meshBasicMaterial
          key={String(!!hologram)}
          map={texture}
          transparent
          opacity={hologram?.opacity ?? 1}
          toneMapped={false}
          side={DoubleSide}
          depthWrite={false}
          blending={hologram ? AdditiveBlending : undefined}
        />
      </mesh>
    </group>
  );
}

type Maps = Partial<Record<TextureChannel, Texture>>;

/**
 * The attachment material's pictures (an imported model's own), held in the shared preview texture cache; the ones shown
 * before are let go once the new ones are on (as WeaponModel's paints), all of them when the model goes.
 */
function useMaps(textures: Partial<Record<TextureChannel, string | null>> | null | undefined): Maps {
  const [maps, setMaps] = useState<Maps>({});
  const held = useRef<Set<string>[]>([]);
  const key = JSON.stringify(textures ?? {});
  useEffect(() => {
    const entries = Object.entries(JSON.parse(key) as Record<string, string | null>).filter((e): e is [TextureChannel, string] => !!e[1]);
    const keys = new Set(entries.map(([ch, p]) => textureKey(p, ch)));
    holdTextures(keys);
    held.current.push(keys);
    let live = true;
    void track(
      Promise.all(
        entries.map(([ch, p]) =>
          loadTexture(p, ch).then(
            (tex) => [ch, tex] as const,
            () => (useStore.getState().log(t('attachments.pictureMissing', { v0: p.split(/[\\/]/).pop() })), null),
          ),
        ),
      ),
    ).then((loaded) => {
      if (!live) return;
      setMaps(Object.fromEntries(loaded.filter((x) => !!x)));
      releaseTextures(...held.current.splice(0, held.current.indexOf(keys)));
    });
    return () => void (live = false);
  }, [key]);
  useEffect(() => {
    const sets = held.current;
    return () => releaseTextures(...sets.splice(0));
  }, []);
  return maps;
}

/**
 * An imported model drawn as the plugin draws it (Native SkinnedGear.Surface, Holograms.Surface): its pictures from the
 * attachment's material; a hologram shows the base-colour picture (else the ramp) as its glow in its colour, an unlit model
 * the picture as emission, a lit one the picture tinted by its colour (packed with the colour baked in, actions bakeTints)
 * with its normal, metallic-smoothness and emissive pictures.
 */
/**
 * The paint and animated layers on an attachment's model (its override, attachmentMaterial) as the 3D view shows them: the same
 * live livery and layer composite as a game material's (WeaponModel), over the model's picture as paintTint gives it. Null
 * without any.
 */
function usePaint(a: Attachment): Maps | null {
  const edited = useStore((s) => s.doc);
  const doc = useMemo(() => withVariant(edited), [edited]);
  const fileEpoch = useStore((s) => s.fileEpoch);
  const name = attachmentMaterial(a.id ?? '');
  const livery = useRef<LiveryPreview | null>(null);
  const layered = useRef(new Map<TextureChannel, Compositor>());
  const [paint, setPaint] = useState<Maps | null>(null);
  useEffect(() => {
    const m = materialOf(doc, name);
    const tint = paintTint(doc, name);
    const base = (ch: TextureChannel) => baseTexturePath(doc, null, name, ch);
    if (hasLivery(m)) {
      livery.current ??= new LiveryPreview(name);
      // Redrawn when the model moves too: patterns and gradients run in the gear's space.
      const layout = JSON.stringify([doc.parts, a.part, a.transform, a.position, a.rotation, a.scale, fileEpoch]);
      livery.current.update(m!, base('albedo'), base('metallicSmoothness'), base('emissive'), layout, tint);
    } else {
      livery.current?.dispose();
      livery.current = null;
    }
    const layers = (m?.layers ?? []).filter((l): l is DynamicLayer => !!l && l.visible !== false);
    const out: Maps = {};
    for (const ch of CHANNELS) {
      const own = layers.filter((l) => (l.channel ?? 'albedo') === ch);
      const painted = livery.current?.texture(ch) ?? null;
      if (own.length) {
        let c = layered.current.get(ch);
        if (!c) layered.current.set(ch, (c = new Compositor()));
        c.set(ch, ch === 'albedo' && livery.current ? livery.current : base(ch), own, ch === 'albedo' ? tint : null);
        out[ch] = c.texture;
      } else if (painted) out[ch] = painted;
      if (!own.length) layered.current.get(ch)?.dispose();
      if (!own.length) layered.current.delete(ch);
    }
    setPaint(Object.keys(out).length ? out : null);
  }, [doc, fileEpoch, name, a.part, a.transform, a.position, a.rotation, a.scale]);
  useEffect(
    () => () => {
      livery.current?.dispose();
      layered.current.forEach((c) => c.dispose());
    },
    [],
  );
  const last = useRef(0);
  useFrame(() => {
    const now = performance.now();
    if ((!livery.current && !layered.current.size) || now - last.current < 1000 / 30) return;
    last.current = now;
    // As WeaponModel: not while the editor is hidden or another window has the focus.
    if (!document.hidden && document.hasFocus()) livery.current?.tick();
    layered.current.forEach((c) => c.draw());
  });
  return paint;
}

function MeshModel({ glb, attachment }: { glb: string; attachment: Attachment }) {
  const gltf = useGLTF(fileUrl(glb));
  const scene = useMemo(() => gltf.scene.clone(true), [gltf.scene]);
  const own = useMaps(attachment.material?.textures);
  const paint = usePaint(attachment);
  // Paint is drawn over the picture with a lit model's colour already in it (paintTint).
  const maps = useMemo(() => (paint ? { ...own, ...paint } : own), [own, paint]);
  useEffect(() => {
    const m = attachment.material;
    const c = m?.color ?? [1, 1, 1, 1];
    const made: Material[] = [];
    scene.traverse((o) => {
      const mesh = o as Mesh;
      if (!mesh.isMesh || mesh.userData.sfFxShell) return; // a code effect's copy (CodeEffects.tsx) keeps its program
      const src = mesh.material as MeshStandardMaterial;
      const original = (mesh.userData.original ??= src) as MeshStandardMaterial;
      // A hologram glows additively in its colour, dimmed by its opacity; a second colour is a ramp up its UVs (hologram.ts),
      // packed only when it has no picture of its own.
      const h = attachment.hologram;
      const ramp = h && !maps.albedo ? rampTexture(attachment) : null;
      let next: MeshBasicMaterial | MeshStandardMaterial;
      if (h) next = new MeshBasicMaterial({ map: maps.albedo ?? ramp, toneMapped: false, transparent: true, opacity: h.opacity ?? 1, blending: AdditiveBlending, depthWrite: false, side: DoubleSide });
      else if (m?.unlit) next = new MeshBasicMaterial({ map: maps.albedo ?? null, toneMapped: false });
      else {
        const lit = original.clone();
        lit.map = maps.albedo ?? null;
        lit.normalMap = maps.normal ?? null;
        if (maps.metallicSmoothness) Object.assign(lit, { metalnessMap: maps.metallicSmoothness, roughnessMap: maps.metallicSmoothness, metalness: 1, roughness: 1 });
        if (maps.emissive) {
          const e = m?.params?.emissiveColor ?? [1, 1, 1];
          Object.assign(lit, { emissiveMap: maps.emissive, emissiveIntensity: m?.params?.emissiveIntensity ?? 1 });
          lit.emissive.setRGB(e[0], e[1], e[2], SRGBColorSpace);
        }
        next = lit;
      }
      // A lit model's paint already holds its colour (paintTint).
      if (ramp || (paint?.albedo && !h && !m?.unlit)) next.color.setRGB(1, 1, 1);
      else next.color.setRGB(c[0], c[1], c[2], SRGBColorSpace);
      // Named as its paint's material, so the livery tools find and draw this model (liveryGpu meshesOf, LiveryTool).
      next.name = attachmentMaterial(attachment.id ?? '');
      mesh.material = next;
      made.push(next);
    });
    // The next look (or leaving) replaces these copies; the loaded originals stay with the glTF.
    return () => made.forEach((x) => x.dispose());
  }, [scene, maps, paint, attachment.material, attachment.hologram]); // eslint-disable-line react-hooks/exhaustive-deps -- the attachment is read through these two
  return <primitive object={scene} />;
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
// Each material's own emission (or colour) that a flash returns to. Kept beside the material, not in its userData: a
// material cloned from one (MeshModel's look) would carry a JSON copy whose colour is a number, and copying that in gives
// NaN, which the bloom spreads over the whole view.
const unflashed = new WeakMap<Material, { color: Color; k: number } | Color>();

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
    peak.setRGB(c[0], c[1], c[2], SRGBColorSpace).multiplyScalar(e.intensity ?? 1);
    k = 1 - Math.abs(2 * t - 1);
  }
  g.traverse((o) => {
    const m = (o as Mesh).material as (MeshStandardMaterial & MeshBasicMaterial) | undefined;
    if (!m || Array.isArray(m)) return;
    if (m.isMeshStandardMaterial) {
      let base = unflashed.get(m) as { color: Color; k: number } | undefined;
      if (!base) unflashed.set(m, (base = { color: m.emissive.clone(), k: m.emissiveIntensity }));
      if (k > 0) {
        m.emissive.copy(base.color).multiplyScalar(base.k).lerp(peak, k);
        m.emissiveIntensity = 1;
      } else {
        m.emissive.copy(base.color);
        m.emissiveIntensity = base.k;
      }
    } else if (m.isMeshBasicMaterial) {
      // No emission here: the colour is what glows.
      let base = unflashed.get(m) as Color | undefined;
      if (!base) unflashed.set(m, (base = m.color.clone()));
      m.color.copy(base).lerp(peak, k);
    }
  });
}

// Charm (Attachment.charm): the game's rope, moved by the backend (charm.simulate) and played back here. The preview motions
// move the whole gear through a motion matrix the view does not show: the hanging point's path in that moving space goes to the
// backend, whose frames are drawn back in the still one, so the gun stays put on screen while the charm swings as it does in a
// first-person view. A motion is followed by SETTLE_SECONDS held still, so the charm comes to rest on screen.
/** The gear's body as the rope takes it: per part, the box (in the gear root's frame) around its models' geometry, leaving
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
  const box = new Box3(),
    m = new Matrix4(),
    inv = new Matrix4().copy(body.matrixWorld).invert();
  body.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh || !mesh.geometry) return;
    if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
    box.union(mesh.geometry.boundingBox!.clone().applyMatrix4(m.multiplyMatrices(inv, mesh.matrixWorld)));
  });
  const arm = box.isEmpty() ? 0 : (-(box.min.y + box.max.y) / 2) * Math.abs(scaleY);
  return Math.min(Math.max(arm, 0.004), 0.1);
}

type CharmFrames = Methods['charm.simulate'][1];

const floats = (base64: string) => {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  return new Float32Array(bytes.buffer);
};

/** One charm's preview: the backend's frames for the motion last asked for (at rest until one is), the geometry they fill and
 * the key it was built for. */
class CharmPreview {
  readonly geometry = new BufferGeometry();
  readonly key: string;
  private readonly charm: CharmSettings;
  private readonly arm: number;
  private frames: CharmFrames | null = null;
  private sims: Matrix4[] = [];
  private shown = -1;
  private started = 0;
  private asked = Number.NaN; // previewEvents.charm.at of the frames asked for last
  private request = 0;
  private gone = false;
  private readonly v = new Vector3();
  private readonly q = new Quaternion();
  private readonly s = new Vector3();
  private readonly aq = new Quaternion();
  private readonly inverse = new Matrix4();

  constructor(key: string, charm: CharmSettings, arm: number) {
    this.key = key;
    this.charm = charm;
    this.arm = arm;
  }

  dispose() {
    this.gone = true;
    this.geometry.dispose();
  }

  /** Asks the backend for the hanging point's path: `motion` and SETTLE_SECONDS held still, or one frame at rest. */
  private ask(group: Group, motion: CharmMotion | null) {
    group.updateWorldMatrix(true, false);
    const root = viewRefs.root;
    const count = motion ? Math.ceil((MOTION_SECONDS[motion] + SETTLE_SECONDS) * PREVIEW_FPS) + 1 : 1;
    const sims: Matrix4[] = [];
    const frames: { time: number; m: number[]; body: number[] }[] = [];
    const m = new Matrix4();
    for (let i = 0; i < count; i++) {
      const time = i / PREVIEW_FPS,
        body = [0, 0, 0];
      // Past its end a motion holds its last pose (a run stops where it stopped).
      if (motion) motionMatrix(m, root, motion, Math.min(time, MOTION_SECONDS[motion] - 1e-6), body);
      else m.identity();
      const sim = new Matrix4().multiplyMatrices(m, group.matrixWorld);
      sims.push(sim);
      frames.push({ time, m: sim.elements.slice(), body });
    }
    const collide = this.charm.collide ?? CHARM_DEFAULTS.collide;
    const id = ++this.request;
    track(
      call('charm.simulate', {
        charm: this.charm,
        arm: this.arm,
        gear: collide ? root?.matrixWorld.elements.slice() : undefined,
        boxes: collide ? bodyBoxes() : undefined,
        frames,
      }),
    ).then(
      (r) => {
        if (this.gone || id !== this.request) return;
        if (!this.frames) this.geometry.setIndex(r.triangles);
        this.frames = r;
        this.sims = sims;
        this.shown = -1;
        this.started = performance.now();
      },
      (e) => {
        if (!this.gone && id === this.request) useStore.getState().toast('error', friendly(e));
      },
    );
  }

  update(group: Group, body: Group, rope: Mesh) {
    const { motion, at } = previewEvents.charm;
    if (at !== this.asked) {
      // A motion still playing when the charm is rebuilt (a setting changed) starts over with the new setting.
      this.asked = at;
      const playing = (performance.now() - at) / 1000 < MOTION_SECONDS[motion] + SETTLE_SECONDS;
      this.ask(group, playing ? motion : null);
    }
    const r = this.frames;
    if (!r) return;
    const f = Math.min(Math.floor(((performance.now() - this.started) / 1000) * PREVIEW_FPS), r.frames.length - 1);
    if (f === this.shown) return;
    this.shown = f;
    const frame = r.frames[f];
    // Vertices relative to the moving hanging point are relative to the still group too.
    this.geometry.setAttribute('position', new BufferAttribute(floats(frame.vertices), 3));
    this.geometry.setAttribute('normal', new BufferAttribute(floats(frame.normals), 3));
    this.geometry.computeBoundingSphere(); // the chain is picked by its bounds first
    const material = rope.material as MeshStandardMaterial;
    material.metalness = r.metallic;
    material.roughness = 1 - r.smoothness;
    const p = frame.pendant,
      sim = this.sims[f];
    sim.decompose(this.v, this.aq, this.s);
    body.position.set(p[0], p[1], p[2]).applyMatrix4(this.inverse.copy(sim).invert());
    body.quaternion.copy(this.aq.invert()).multiply(this.q.set(p[3], p[4], p[5], p[6]));
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

/**
 * One attachment that cannot be drawn (its model unreadable, its screen failing) is left out with a notice instead of taking
 * the whole view down; its own Suspense keeps the gun on screen while its model loads. Tried again when its source changes.
 */
class Isolated extends Component<{ name: string; source: string; children: ReactNode }, { failed: string | null }> {
  state = { failed: null as string | null };
  static getDerivedStateFromError() {
    return { failed: '' };
  }
  componentDidCatch(error: unknown) {
    this.setState({ failed: this.props.source });
    notify('error', t('attachments.unreadable', { v0: this.props.name }));
    console.error(error);
  }
  componentDidUpdate(prev: { source: string }) {
    if (this.state.failed !== null && prev.source !== this.props.source) this.setState({ failed: null });
  }
  render() {
    return this.state.failed !== null ? null : <Suspense fallback={<Loading />}>{this.props.children}</Suspense>;
  }
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
  useEffect(() => () => charm.current?.dispose(), []);
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
          charm.current?.dispose();
          charm.current = new CharmPreview(key, a.charm, arm);
          rope.current.geometry = charm.current.geometry;
        }
        charm.current.update(g, body.current, rope.current);
      } else if (charm.current) {
        charm.current.dispose();
        charm.current = null;
        body.current.position.set(0, 0, 0);
        body.current.quaternion.identity();
      }
    }
  });
  const c = a.charm;
  const ropeColor = useMemo(() => new Color().setRGB(c?.color?.[0] ?? 0.8, c?.color?.[1] ?? 0.8, c?.color?.[2] ?? 0.82, SRGBColorSpace), [c?.color]);
  // A hidden attachment has no group: registered again when it shows.
  const drawn = a.visible !== false;
  useEffect(() => {
    const g = group.current;
    if (g) viewRefs.attachments.set(a.id!, g);
    window.dispatchEvent(new Event(ATTACHMENT_EVENT));
    return () => void viewRefs.attachments.delete(a.id!);
  }, [a.id, drawn]);

  if (a.visible === false) return null;
  const glb = meshPreviews?.[a.id!];
  return createPortal(
    <group ref={group} name={`attachment:${a.id}`}>
      {/* Clicks are picked by the view (Viewport.tsx Picking); a flicker drop-out still counts as drawn there. */}
      <group ref={body} userData={{ sfFlicker: true }}>
        <Isolated name={a.name || a.id || ''} source={`${glb ?? ''}|${JSON.stringify(a.screen ?? null)}`}>
          {a.screen && <ScreenMesh screen={a.screen} hologram={a.hologram} />}
          {glb && <MeshModel glb={glb} attachment={a} />}
        </Isolated>
      </group>
      {c && (
        <mesh ref={rope} frustumCulled={false}>
          {/* Metalness and roughness per style come with the backend's frames (CharmPreview). */}
          <meshStandardMaterial color={ropeColor} />
        </mesh>
      )}
      {c && (
        // The hanging point, drawn over the gun (editor only): select it and drag the gizmo to move where the rope starts.
        <mesh renderOrder={10}>
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
  useSceneVersion(); // anchors exist only after the weapon model mapped its nodes
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
