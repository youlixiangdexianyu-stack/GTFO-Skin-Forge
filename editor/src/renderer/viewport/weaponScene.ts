// A weapon's preview scene as the game draws it, for the 3D view (and the card window, which draws with the view's stage): Unity node
// names, one editable material per game material, and each material's textures, parameters and blend mode.
import {
  AdditiveBlending,
  Color,
  DataTexture,
  Mesh,
  type MeshLambertMaterial,
  MeshStandardMaterial,
  PMREMGenerator,
  PropertyBinding,
  SRGBColorSpace,
  type Material,
  type Object3D,
  type Texture,
  type WebGLRenderer,
} from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import { baseTexturePath } from '../frames';
import { liveryParams } from '../livery';
import { CHANNELS, RETICLES, type DynamicLayer, type MaterialOverride, type SkinDocument, type TextureChannel, type WeaponPreview } from '../protocol';
import { glowOf, materialOf, reticleOf } from '../store';
import { walkGear } from './refs';
import { loadTexture, textureKey } from './textures';

const SLOT: Partial<Record<TextureChannel, 'map' | 'normalMap' | 'metalnessMap' | 'emissiveMap'>> = {
  albedo: 'map',
  normal: 'normalMap',
  metallicSmoothness: 'metalnessMap',
  emissive: 'emissiveMap',
};

/**
 * Marks real glTF nodes (Unity transforms), as opposed to the extra meshes the loader makes for multi-material
 * primitives, and restores the Unity names the loader rewrote for animation binding ("Shell (2)" -> "Shell_(2)"); the
 * backend's part nodes and the document's sub-transform names use them. Mapped by name: meshes that share geometry
 * share one association entry, so it cannot say which node is which.
 */
export function restoreUnityNames(gltf: {
  scene: Object3D;
  parser: {
    json: { nodes?: { name?: string }[] };
    associations: { get(o: Object3D): { nodes?: number } | undefined };
  };
}) {
  const unity = new Map<string, string>();
  for (const n of gltf.parser.json.nodes ?? []) if (n.name) unity.set(PropertyBinding.sanitizeNodeName(n.name), n.name);
  gltf.scene.traverse((o) => {
    o.userData.sfNode = gltf.parser.associations.get(o)?.nodes !== undefined;
    o.name = unity.get(o.name) ?? o.name;
  });
}

/** The room reflection map the metal surfaces need, tinted by the light colour (a white room next to coloured lights reads as two lights). */
export function roomReflection(renderer: WebGLRenderer, lamp = '#ffffff'): Texture {
  const room = new RoomEnvironment();
  // Only the room's lamps (its emissive Lambert panels) take the colour; the walls stay grey, so a metal gun reflects
  // coloured lights in a neutral room instead of turning that colour all over.
  const tint = new Color(lamp);
  room.traverse((o) => {
    const m = (o as Mesh).material as MeshLambertMaterial | undefined;
    if (m?.type === 'MeshLambertMaterial') m.emissive.multiply(tint);
  });
  const pmrem = new PMREMGenerator(renderer);
  const env = pmrem.fromScene(room, 0.04).texture;
  room.dispose();
  pmrem.dispose();
  return env;
}

/** Gives every mesh one editable material per game material name, kept in `mats`. */
export function editableMaterials(scene: Object3D, mats: Map<string, MeshStandardMaterial>) {
  walkGear(scene, (o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    const list = (Array.isArray(mesh.material) ? mesh.material : [mesh.material]) as Material[];
    const mine = list.map((m) => {
      let mat = mats.get(m.name);
      if (!mat) {
        mat = new MeshStandardMaterial({
          name: m.name,
          color: 0xffffff,
          metalness: 1,
          roughness: 1,
        });
        mats.set(m.name, mat);
      }
      return mat;
    });
    mesh.material = Array.isArray(mesh.material) ? mine : mine[0];
    mesh.castShadow = mesh.receiveShadow = false;
  });
}

/**
 * Applies one material from the document over the game's own: base textures, parameters and blend mode. A channel
 * with dynamic layers gets the texture `layered` returns (the 3D view's live composite); without `layered` the layers
 * are left out. `livery` gives the textures a livery paints (the 3D view's live livery), over which those layers then play.
 * Adds the cache keys of the textures it uses to `keep` and returns the pending loads.
 */
export function paintMaterial(
  mat: MeshStandardMaterial,
  name: string,
  doc: SkinDocument,
  preview: WeaponPreview,
  keep: Set<string>,
  cancelled: () => boolean,
  onError: (path: string) => void,
  layered?: (ch: TextureChannel, base: string | null, layers: DynamicLayer[]) => Texture,
  livery?: (ch: TextureChannel) => Texture | null,
): Promise<unknown>[] {
  const info = preview.params[name];
  const override = materialOf(doc, name);
  if (info?.kind === 'reticle') return showReticle(mat, name, override, preview, keep, cancelled, onError);
  const loads: Promise<unknown>[] = [];
  const own = {
    ...(livery ? liveryParams(override) : {}),
    ...Object.fromEntries(Object.entries(override?.params ?? {}).filter(([, v]) => v != null)),
  };
  const params = { ...info, ...own };
  const layers = (override?.layers ?? []).filter((l): l is DynamicLayer => !!l && l.visible !== false);
  for (const ch of CHANNELS) {
    const path = baseTexturePath(doc, preview, name, ch);
    const chLayers = layers.filter((l) => (l.channel ?? 'albedo') === ch);
    if (chLayers.length && layered) {
      setSlot(mat, ch, layered(ch, path, chLayers));
      continue;
    }
    const painted = livery?.(ch);
    if (painted) {
      setSlot(mat, ch, painted);
      continue;
    }
    if (!path) {
      setSlot(mat, ch, null);
      continue;
    }
    // The skin's own texture at the size it has in game; a game texture stays as the game has it.
    const size = override?.textures?.[ch] === path ? override.textureSize : null;
    keep.add(textureKey(path, ch, size));
    // A picture of the skin's own that cannot be read shows the game's in its place (the caller marks it).
    const vanilla = preview.textures[name]?.[ch];
    loads.push(
      loadTexture(path, ch, size).then(
        (t) => !cancelled() && setSlot(mat, ch, t),
        () => {
          onError(path);
          if (!vanilla || vanilla === path) return;
          keep.add(textureKey(vanilla, ch));
          return loadTexture(vanilla, ch).then(
            (t) => !cancelled() && setSlot(mat, ch, t),
            () => undefined,
          );
        },
      ),
    );
  }
  const tint = params.tint ?? [1, 1, 1, 1];
  mat.color.setRGB(tint[0], tint[1], tint[2], SRGBColorSpace); // a skin's colours are sRGB (ForgeCore ColourSpace)
  // With a MetallicSmoothness map the scalars multiply it; without one, fall back to a neutral finish.
  const hasMs = !!baseTexturePath(doc, preview, name, 'metallicSmoothness') || !!livery?.('metallicSmoothness');
  mat.metalness = params.metallic ?? (hasMs ? 1 : 0.4);
  mat.roughness = params.smoothness != null ? 1 - params.smoothness : hasMs ? 1 : 0.55;
  const on = params.emissiveEnabled ?? !!mat.emissiveMap;
  const glow = glowOf(info, own);
  mat.emissive = new Color().setRGB(glow.color[0], glow.color[1], glow.color[2], SRGBColorSpace);
  mat.emissiveIntensity = on ? glow.intensity : 0;
  // Blend mode as the game draws it: lenses see-through with a slight reflection, cutouts alpha-tested.
  const glass = info?.kind === 'glass';
  // Transparency is part of the shader (alphaTest rebuilds it by itself).
  if (mat.transparent !== glass) mat.needsUpdate = true;
  mat.transparent = glass;
  mat.opacity = glass ? (tint[3] ?? 0.2) : 1;
  mat.depthWrite = !glass;
  mat.alphaTest = info?.kind === 'cutout' ? 0.5 : 0;
  if (glass) {
    mat.metalness = 0;
    mat.roughness = 0.05;
  }
  return loads;
}

/** A layer without a picture adds nothing. */
const NO_RETICLE = Object.assign(new DataTexture(new Uint8Array([0, 0, 0, 255]), 1, 1), { needsUpdate: true });

type ReticleUniforms = Record<`reticleMap${0 | 1 | 2}`, { value: Texture }> & { reticleColor: { value: Color[] }; reticleScale: { value: number[] } };

/** The sum of the three layers, each picture scaled around the lens centre (nothing outside it) times its colour. */
const RETICLE_PARS = /* glsl */ `
uniform sampler2D reticleMap0;
uniform sampler2D reticleMap1;
uniform sampler2D reticleMap2;
uniform vec3 reticleColor[3];
uniform float reticleScale[3];
vec3 reticleLayer(sampler2D map, vec3 colour, float scale) {
  vec2 uv = (vEmissiveMapUv - 0.5) * scale + 0.5;
  if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec3(0.0);
  return texture2D(map, uv).rgb * colour;
}`;
const RETICLE_SUM = /* glsl */ `totalEmissiveRadiance = reticleLayer(reticleMap0, reticleColor[0], reticleScale[0])
  + reticleLayer(reticleMap1, reticleColor[1], reticleScale[1]) + reticleLayer(reticleMap2, reticleColor[2], reticleScale[2]);`;

/** The material's reticle uniforms, made (and its shader extended to draw them) the first time. */
function reticleUniforms(mat: MeshStandardMaterial): ReticleUniforms {
  let u = mat.userData.reticle as ReticleUniforms | undefined;
  if (u) return u;
  const made: ReticleUniforms = {
    reticleMap0: { value: NO_RETICLE },
    reticleMap1: { value: NO_RETICLE },
    reticleMap2: { value: NO_RETICLE },
    reticleColor: { value: [new Color(0, 0, 0), new Color(0, 0, 0), new Color(0, 0, 0)] },
    reticleScale: { value: [1, 1, 1] },
  };
  u = mat.userData.reticle = made;
  mat.onBeforeCompile = (s) => {
    Object.assign(s.uniforms, made);
    s.fragmentShader = s.fragmentShader
      .replace('#include <emissivemap_pars_fragment>', `#include <emissivemap_pars_fragment>\n${RETICLE_PARS}`)
      .replace('#include <emissivemap_fragment>', RETICLE_SUM);
  };
  mat.customProgramCacheKey = () => 'forgeskin-reticle';
  // The emission map only switches on the UVs the layers are read at.
  mat.emissiveMap = NO_RETICLE;
  mat.needsUpdate = true;
  return u;
}

/**
 * Holographic sight surface: invisible except its reticle layers, added on top as picture x colour with no lighting, each
 * as the game has it with the skin's picture and values over it.
 */
function showReticle(
  mat: MeshStandardMaterial,
  name: string,
  override: MaterialOverride | undefined,
  preview: WeaponPreview,
  keep: Set<string>,
  cancelled: () => boolean,
  onError: (path: string) => void,
): Promise<unknown>[] {
  Object.assign(mat, {
    map: null,
    metalness: 0,
    roughness: 1,
    envMapIntensity: 0,
    transparent: true,
    depthWrite: false,
    blending: AdditiveBlending,
  });
  mat.color.setRGB(0, 0, 0);
  mat.emissive.setRGB(1, 1, 1);
  mat.emissiveIntensity = 1;
  const u = reticleUniforms(mat);
  const game = preview.params[name]?.reticles;
  return RETICLES.map((ch, i) => {
    const r = reticleOf(game?.[ch], override?.params?.reticles?.[ch]);
    const path = override?.textures?.[ch] ?? preview.textures[name]?.[ch];
    // HDR reticle colours (some above 100) are capped so the preview's bloom does not flood the screen.
    const c = new Color().setRGB(r.color[0], r.color[1], r.color[2], SRGBColorSpace).multiplyScalar(r.intensity);
    u.reticleColor.value[i].copy(r.visible && path ? c.multiplyScalar(1 / Math.max(1, Math.max(c.r, c.g, c.b) / 4)) : new Color(0, 0, 0));
    // The layer's offset is its projection depth in the game shader, not a shift; the preview draws it centred.
    u.reticleScale.value[i] = r.scale || 1;
    const map = u[`reticleMap${i as 0 | 1 | 2}`];
    if (!path) {
      map.value = NO_RETICLE;
      return Promise.resolve();
    }
    keep.add(textureKey(path, 'reticle'));
    return loadTexture(path, 'reticle').then(
      (t) => {
        if (!cancelled()) map.value = t;
      },
      () => onError(path),
    );
  });
}

/** Only a map coming or going changes the shader; another texture in the same slot needs no rebuild. */
function setSlot(mat: MeshStandardMaterial, ch: TextureChannel, t: Texture | null) {
  const slot = SLOT[ch]!;
  if (!mat[slot] !== !t) mat.needsUpdate = true;
  mat[slot] = t;
  if (ch === 'metallicSmoothness') mat.roughnessMap = t;
}
