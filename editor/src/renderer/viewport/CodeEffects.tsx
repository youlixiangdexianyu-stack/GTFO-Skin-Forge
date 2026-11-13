// Code effects (SkinDocument.codeEffects) in the 3D view, drawn as the plugin draws them (Native/CodeFx.cs) and by the same program
// source: ForgeCore's ForgeFxPrograms.hlsl compiled as GLSL with the dispatch built from its catalogue (codeFxValues.ts).
// - On a material: each frame the channel's texture as the view shows it (the skin's texture, its livery and dynamic layers) is copied
//   into a render target, every effect that is on is blended over the UV islands of the meshes that wear the material, and the
//   material shows that target.
// - On an attachment: a copy of its model, 1 % larger about its centre, drawn with the program over the model.
// The preview mirrors the game's mesh space (Z negated, V flipped, Backend Glb.cs) back, so a pattern lands where the game puts it.
import { useFrame, useThree } from '@react-three/fiber';
import { useEffect } from 'react';
import {
  BufferGeometry,
  CustomBlending,
  DoubleSide,
  Float32BufferAttribute,
  GLSL3,
  Mesh,
  MeshBasicMaterial,
  NoBlending,
  OneFactor,
  OneMinusSrcAlphaFactor,
  OrthographicCamera,
  RawShaderMaterial,
  Scene,
  SRGBColorSpace,
  SrcAlphaFactor,
  Texture,
  Vector3,
  Vector4,
  WebGLRenderTarget,
  type Material,
  type MeshStandardMaterial,
  type WebGLRenderer,
} from 'three';
import catalogJson from '../../../../../ForgeCore/Visual/Data/fx-programs.json';
import programSource from '../../../../../ForgeCore/Visual/tools/shaders/ForgeFxPrograms.hlsl?raw';
import { fxDispatch, resolveFx, type FxCatalog, type FxValues } from '../codeFxValues';
import type { CodeEffect, TextureChannel } from '../protocol';
import { useStore } from '../store';
import { meshesOf } from './liveryGpu';
import { previewEvents, viewRefs } from './refs';

export const FX_CATALOG = catalogJson as FxCatalog;
/** How much larger an attachment's effect copy is than its model (Native/CodeFx.cs FxShell.Lift). */
const LIFT = 1.01;
const DEG = Math.PI / 180;

const VERTEX = /* glsl */ `
precision highp float;
in vec3 position;
in vec3 normal;
in vec2 uv;
uniform mat4 modelMatrix;
uniform mat4 modelViewMatrix;
uniform mat4 projectionMatrix;
uniform vec3 cameraPosition;
uniform float uUnwrap;
uniform vec3 uView;
out vec2 vUv;
out vec3 vPos;
out vec3 vNrm;
out vec3 vView;
void main() {
  // The game's mesh space: Z and V mirrored back.
  vUv = vec2(uv.x, 1.0 - uv.y);
  vPos = vec3(position.xy, -position.z);
  vNrm = vec3(normal.xy, -normal.z);
  if (uUnwrap > 0.5) {
    gl_Position = vec4(uv * 2.0 - 1.0, 0.5, 1.0);
    vView = normalize(uView + vec3(0.0, 0.0, 1e-5));
  } else {
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    vec3 eye = (inverse(modelMatrix) * vec4(cameraPosition, 1.0)).xyz - position;
    vView = normalize(vec3(eye.xy, -eye.z));
  }
}`;

const FRAGMENT = /* glsl */ `
precision highp float;
precision highp int;
${programSource}
${fxDispatch(FX_CATALOG)}
uniform int uProgram;
uniform vec4 uP0, uP1, uC0, uC1, uC2, uC3, uXf;
uniform float uTime;
in vec2 vUv;
in vec3 vPos;
in vec3 vNrm;
in vec3 vView;
out vec4 outColor;
void main() {
  FxIn f;
  f.uv = fx_transform(vUv, uXf, uTime);
  f.pos = vPos;
  f.nrm = normalize(vNrm + vec3(0.0, 0.0, 1e-5));
  f.view = normalize(vView);
  f.time = uTime;
  f.p0 = uP0;
  f.p1 = uP1;
  f.c0 = uC0;
  f.c1 = uC1;
  f.c2 = uC2;
  f.c3 = uC3;
  vec4 c = fx_dispatch(uProgram, f);
  c.rgb *= uXf.w;
  c.a = clamp(c.a * min(uXf.w, 1.0), 0.0, 1.0);
  outColor = c;
}`;

const linear = (c: number) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));

/** A program material: laid out by UV into a texture (`unwrap`) or drawn on geometry; alpha or additive as the game blends. */
export function fxMaterial(unwrap: boolean, additive: boolean): RawShaderMaterial {
  return new RawShaderMaterial({
    glslVersion: GLSL3,
    vertexShader: VERTEX,
    fragmentShader: FRAGMENT,
    uniforms: {
      uUnwrap: { value: unwrap ? 1 : 0 },
      uView: { value: new Vector3(0, 0, 1) }, // the painter's default eye: object +Z (ProceduralPainter.ViewDirection)
      uProgram: { value: 0 },
      uTime: { value: 0 },
      ...Object.fromEntries(['uP0', 'uP1', 'uC0', 'uC1', 'uC2', 'uC3', 'uXf'].map((k) => [k, { value: new Vector4() }])),
    },
    side: DoubleSide,
    transparent: !unwrap,
    depthTest: !unwrap,
    depthWrite: false,
    blending: CustomBlending,
    blendSrc: SrcAlphaFactor,
    blendDst: additive ? OneFactor : OneMinusSrcAlphaFactor,
    blendSrcAlpha: SrcAlphaFactor,
    blendDstAlpha: additive ? OneFactor : OneMinusSrcAlphaFactor,
  });
}

/** Writes `v` into a program material: what ProceduralLook.Apply writes in game (colours to linear as Unity's SetColor does). */
export function applyFx(m: RawShaderMaterial, v: FxValues, time: number) {
  const u = m.uniforms;
  u.uProgram.value = v.program;
  (u.uP0.value as Vector4).fromArray(v.params, 0);
  (u.uP1.value as Vector4).fromArray(v.params, 4);
  v.colors.forEach((c, i) => (u[`uC${i}`].value as Vector4).set(linear(c[0]), linear(c[1]), linear(c[2]), c[3]));
  (u.uXf.value as Vector4).set(v.transform[0], v.transform[1] * DEG, v.transform[2] * DEG, v.transform[3]);
  u.uTime.value = time;
}

/** Whether an effect draws now and its clock (seconds): always by the view's clock, an event for `seconds` after it fired. */
function clockOf(e: CodeEffect, start: number): number | null {
  const now = performance.now();
  if (!e.trigger || e.trigger === 'always') return (now - start) / 1000;
  const at = previewEvents.at[e.trigger];
  const t = at === undefined ? Infinity : (now - at) / 1000;
  return t < (e.seconds ?? 1) ? t : null;
}

const SLOT: Partial<Record<TextureChannel, 'map' | 'emissiveMap'>> = { albedo: 'map', emissive: 'emissiveMap' };

/** One material channel the effects paint over (Native/CodeFx.cs FxCanvas): base copied in, effects blended over, shown. */
class Canvas {
  readonly target: WebGLRenderTarget;
  private base: Texture | null = null;
  private readonly copy = new RawShaderMaterial({
    glslVersion: GLSL3,
    vertexShader: `in vec3 position; in vec2 uv; out vec2 vUv; void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }`,
    fragmentShader: `precision highp float; uniform sampler2D uBase; uniform vec4 uFill; uniform float uHas; in vec2 vUv; out vec4 o;
      void main() { o = uHas > 0.5 ? texture(uBase, vUv) : uFill; }`,
    uniforms: { uBase: { value: null }, uFill: { value: new Vector4(1, 1, 1, 1) }, uHas: { value: 0 } },
    depthTest: false,
    depthWrite: false,
    blending: NoBlending,
  });
  readonly passes = new Map<string, RawShaderMaterial>();

  constructor(
    readonly mat: MeshStandardMaterial,
    readonly channel: TextureChannel,
  ) {
    this.target = new WebGLRenderTarget(1024, 1024, { depthBuffer: false });
    this.target.texture.colorSpace = SRGBColorSpace;
    this.target.texture.flipY = false;
  }

  draw(gl: WebGLRenderer, effects: { e: CodeEffect; v: FxValues; start: number }[]) {
    const slot = SLOT[this.channel]!;
    const shown = this.mat[slot];
    if (shown !== this.target.texture) this.base = shown; // paintMaterial put the channel's texture back after an edit
    const img = this.base?.image as { width?: number; height?: number } | undefined;
    const size = Math.min(2048, Math.max(256, img?.width ?? 1024));
    if (this.target.width !== size) this.target.setSize(size, Math.min(2048, Math.max(256, img?.height ?? size)));
    const u = this.copy.uniforms;
    u.uBase.value = this.base;
    u.uHas.value = this.base ? 1 : 0;
    (u.uFill.value as Vector4).set(this.channel === 'albedo' ? 1 : 0, this.channel === 'albedo' ? 1 : 0, this.channel === 'albedo' ? 1 : 0, 1);
    const scene = new Scene();
    const quad = new Mesh(quadGeometry, this.copy);
    quad.frustumCulled = false;
    scene.add(quad);
    for (const { e, v, start } of effects) {
      const time = clockOf(e, start);
      if (time === null) continue;
      let pass = this.passes.get(e.id!);
      const additive = e.blend === 'additive';
      if (!pass || pass.userData.additive !== additive) {
        pass?.dispose();
        this.passes.set(e.id!, (pass = fxMaterial(true, additive)));
        pass.userData.additive = additive;
      }
      applyFx(pass, v, time);
      for (const m of meshesOf(this.mat.name)) {
        const list = (Array.isArray(m.material) ? m.material : [m.material]) as Material[];
        const mats = list.map((x) => (x.name === this.mat.name ? pass! : hidden));
        const copy = new Mesh(m.geometry, Array.isArray(m.material) ? mats : mats[0]);
        copy.frustumCulled = false;
        copy.renderOrder = 1;
        scene.add(copy);
      }
    }
    const prev = gl.getRenderTarget();
    const autoClear = gl.autoClear;
    gl.autoClear = false;
    gl.setRenderTarget(this.target);
    gl.render(scene, camera);
    gl.setRenderTarget(prev);
    gl.autoClear = autoClear;
    if (shown !== this.target.texture) {
      if (!shown) this.mat.needsUpdate = true;
      this.mat[slot] = this.target.texture;
    }
    // The plugin switches a material's glow on for an effect on its emission (SkinnedGear.SetEmissive).
    if (this.channel === 'emissive' && this.mat.emissiveIntensity === 0) this.mat.emissiveIntensity = 1;
  }

  dispose() {
    const slot = SLOT[this.channel]!;
    if (this.mat[slot] === this.target.texture) {
      if (!this.base) this.mat.needsUpdate = true;
      this.mat[slot] = this.base;
    }
    this.target.dispose();
    this.copy.dispose();
    this.passes.forEach((p) => p.dispose());
  }
}

const quadGeometry = new BufferGeometry();
quadGeometry.setAttribute('position', new Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
quadGeometry.setAttribute('uv', new Float32BufferAttribute([0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1], 2));
const hidden = new MeshBasicMaterial({ visible: false });
const camera = new OrthographicCamera();

/** The effect copies of attachment models, by model mesh. */
const shells = new WeakMap<Mesh, Mesh>();

function shellOf(model: Mesh, e: CodeEffect): Mesh {
  let shell = shells.get(model);
  const additive = e.blend === 'additive';
  if (shell && (shell.userData.fx !== e.id || shell.userData.additive !== additive)) {
    (shell.material as Material).dispose();
    shell.removeFromParent();
    shell = undefined;
  }
  if (!shell) {
    shell = new Mesh(model.geometry, fxMaterial(false, additive));
    shell.userData = { fx: e.id, additive, sfFxShell: true };
    shell.raycast = () => undefined; // clicks go to the model
    shell.renderOrder = 3;
    if (!model.geometry.boundingBox) model.geometry.computeBoundingBox();
    const c = model.geometry.boundingBox!.getCenter(new Vector3());
    shell.position.copy(c.multiplyScalar(1 - LIFT));
    shell.scale.setScalar(LIFT);
    model.add(shell);
    shells.set(model, shell);
  }
  return shell;
}

/** Draws the skin's code effects in the 3D view (mounted in the viewport's canvas). */
export function CodeEffects() {
  const gl = useThree((s) => s.gl);
  const doc = useStore((s) => s.doc);
  const effects = (doc.codeEffects ?? []).filter((e): e is CodeEffect => !!e && e.visible !== false);

  useEffect(() => () => {
    canvases.forEach((c) => c.dispose());
    canvases.clear();
  }, []);

  useFrame(() => {
    const start = startedAt;
    const resolved = effects.flatMap((e) => {
      const v = resolveFx(FX_CATALOG, e);
      return v ? [{ e, v, start }] : [];
    });
    // Material channels.
    const used = new Set<string>();
    for (const r of resolved) {
      const { e } = r;
      if (!e.material) continue;
      const mat = viewRefs.materials?.get(e.material);
      const channel = e.channel === 'emissive' ? 'emissive' : 'albedo';
      if (!mat) continue;
      const key = `${e.material}|${channel}`;
      used.add(key);
      let c = canvases.get(key);
      if (c && c.mat !== mat) {
        c.dispose();
        c = undefined;
      }
      if (!c) canvases.set(key, (c = new Canvas(mat, channel)));
    }
    for (const [key, c] of canvases)
      if (!used.has(key)) {
        c.dispose();
        canvases.delete(key);
      }
    for (const [key, c] of canvases) c.draw(gl, resolved.filter((r) => `${r.e.material}|${r.e.channel === 'emissive' ? 'emissive' : 'albedo'}` === key));
    // Attachment models.
    const live = new Set<Mesh>();
    for (const r of resolved) {
      if (!r.e.attachment) continue;
      viewRefs.attachments.get(r.e.attachment)?.traverse((o) => {
        const m = o as Mesh;
        if (!m.isMesh || !m.userData.original || m.userData.sfFxShell) return;
        const shell = shellOf(m, r.e);
        const time = clockOf(r.e, r.start);
        shell.visible = time !== null;
        if (time !== null) applyFx(shell.material as RawShaderMaterial, r.v, time);
        live.add(m);
      });
    }
    for (const g of viewRefs.attachments.values())
      g.traverse((o) => {
        const m = o as Mesh;
        const shell = m.isMesh && !live.has(m) ? shells.get(m) : undefined;
        if (!shell) return;
        (shell.material as Material).dispose();
        shell.removeFromParent();
        shells.delete(m);
      });
  });
  return null;
}

const canvases = new Map<string, Canvas>();
const startedAt = performance.now();
