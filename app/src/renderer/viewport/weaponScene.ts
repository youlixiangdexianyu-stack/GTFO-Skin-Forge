// A weapon's preview scene as the game draws it, shared by the 3D view and the weapon-list thumbnails: Unity node
// names, one editable material per game material, and each material's textures, parameters and blend mode.
import {
  AdditiveBlending,
  ClampToEdgeWrapping,
  Color,
  Mesh,
  type MeshLambertMaterial,
  MeshStandardMaterial,
  PMREMGenerator,
  PropertyBinding,
  type Material,
  type Object3D,
  type Texture,
  type WebGLRenderer,
} from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import { baseTexturePath } from '../frames';
import { liveryParams } from '../livery';
import { CHANNELS, type DynamicLayer, type PreviewMaterial, type SkinDocument, type TextureChannel, type WeaponPreview } from '../protocol';
import { materialOf } from '../store';
import { loadTexture, textureKey } from './textures';

const SLOT: Record<TextureChannel, 'map' | 'normalMap' | 'metalnessMap' | 'emissiveMap'> = {
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
  scene.traverse((o) => {
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
  if (info?.kind === 'reticle') {
    const r = info.reticles.find((x) => (x.color[3] ?? 1) > 0.01);
    if (r) keep.add(textureKey(r.texture, 'reticle'));
    return [showReticle(mat, r, cancelled)];
  }
  const loads: Promise<unknown>[] = [];
  const override = materialOf(doc, name);
  const params = {
    ...info,
    ...(livery ? liveryParams(override) : {}),
    ...Object.fromEntries(Object.entries(override?.params ?? {}).filter(([, v]) => v != null)),
  };
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
    keep.add(textureKey(path, ch));
    loads.push(
      loadTexture(path, ch).then(
        (t) => !cancelled() && setSlot(mat, ch, t),
        () => onError(path),
      ),
    );
  }
  const tint = params.tint ?? [1, 1, 1, 1];
  mat.color.setRGB(tint[0], tint[1], tint[2]);
  // With a MetallicSmoothness map the scalars multiply it; without one, fall back to a neutral finish.
  const hasMs = !!baseTexturePath(doc, preview, name, 'metallicSmoothness') || !!livery?.('metallicSmoothness');
  mat.metalness = params.metallic ?? (hasMs ? 1 : 0.4);
  mat.roughness = params.smoothness != null ? 1 - params.smoothness : hasMs ? 1 : 0.55;
  const on = params.emissiveEnabled ?? !!mat.emissiveMap;
  const c = params.emissiveColor ?? [1, 1, 1];
  mat.emissive = new Color(c[0], c[1], c[2]);
  mat.emissiveIntensity = on ? (params.emissiveIntensity ?? 1) : 0;
  // Blend mode as the game draws it: lenses see-through with a slight reflection, cutouts alpha-tested.
  const glass = info?.kind === 'glass';
  mat.transparent = glass;
  mat.opacity = glass ? (tint[3] ?? 0.2) : 1;
  mat.depthWrite = !glass;
  mat.alphaTest = info?.kind === 'cutout' ? 0.5 : 0;
  if (glass) {
    mat.metalness = 0;
    mat.roughness = 0.05;
  }
  mat.needsUpdate = true;
  return loads;
}

/**
 * Holographic sight surface: invisible except its reticle, added on top as texture x colour with no lighting. Only the
 * first visible layer is drawn, scaled around the lens centre.
 */
function showReticle(mat: MeshStandardMaterial, r: PreviewMaterial['reticles'][number] | undefined, cancelled: () => boolean): Promise<unknown> {
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
  mat.visible = !!r;
  if (!r) return Promise.resolve();
  // HDR reticle colours (some above 100) are capped so the preview's bloom does not flood the screen.
  const a = r.color[3] ?? 1;
  const k = a / Math.max(1, Math.max(r.color[0], r.color[1], r.color[2]) / 4);
  mat.emissive.setRGB(r.color[0] * k, r.color[1] * k, r.color[2] * k);
  mat.emissiveIntensity = 1;
  return loadTexture(r.texture, 'reticle').then((t) => {
    if (cancelled()) return;
    const s = r.scale || 1;
    // A view of the shared image with this reticle's own scale and offset.
    const tex = t.clone();
    tex.repeat.set(s, s);
    // The layer's offset is the reticle's projection depth in the game shader, not a UV shift; the preview draws it centred.
    tex.offset.set(0.5 - 0.5 * s, 0.5 - 0.5 * s);
    tex.wrapS = tex.wrapT = ClampToEdgeWrapping;
    tex.needsUpdate = true;
    mat.emissiveMap?.dispose();
    mat.emissiveMap = tex;
    mat.needsUpdate = true;
  });
}

function setSlot(mat: MeshStandardMaterial, ch: TextureChannel, t: Texture | null) {
  const slot = SLOT[ch];
  mat[slot] = t;
  if (ch === 'metallicSmoothness') mat.roughnessMap = t;
  mat.needsUpdate = true;
}
