// The imported character: the file converted to the character format's model by the site's own converter (import-worker.ts), read
// into three.js for the view, its bones matched to the canonical humanoid (from the file's VRM data, else with the site's own matcher),
// the match held to ForgeCore's rig check (character.check), and its spring bones moved by ForgeCore's chain physics (springs.simulate)
// for the preview motions. The skin is a character document autosaved like a weapon skin. Nothing here computes a rig rule or a chain of its own.
import { useEffect, useSyncExternalStore } from 'react';
import {
  AmbientLight,
  Bone,
  Box3,
  DirectionalLight,
  PerspectiveCamera,
  Scene,
  BufferGeometry,
  Float32BufferAttribute,
  Group as GroupObject,
  Matrix4,
  MeshStandardMaterial,
  Object3D,
  Quaternion,
  Skeleton,
  SkinnedMesh as SkinnedMeshObject,
  Uint16BufferAttribute,
  Vector3,
  type Group,
  type Material,
  ACESFilmicToneMapping,
  SRGBColorSpace,
  WebGLRenderer,
  type SkinnedMesh,
  type Texture,
} from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { matchHumanBones, matchHumanFingers } from '../../../../../../Web/src/forge/animation-bone-match';
import { aimFollow, upgradeVrm, vrmJoints, type CharacterImport } from '../../../../../../Web/src/forge/character-import';
import { characterBlend, characterBlendCutoff } from '../../../../../../Web/src/forge/imported-model-glb';
import { readImportedModelDescriptor } from '../../../../../../Web/src/forge/imported-model-format';
import { buildImportedScene } from '../../../../../../Web/src/shared/render/imported-model-build';
import { HumanFingerJoints, HumanJoints } from '../../../../../../Web/src/generated/rig/animation-reference.js';
import { friendly } from '../errors';
import { t } from '../i18n';
import { call, fileUrl, type Methods, type ProjectEntry, type SkinDocument } from '../protocol';
import { autosaveRoot, useStore } from '../store';
import { motionMatrix, MOTION_SECONDS, PREVIEW_FPS, SETTLE_SECONDS } from '../viewport/motion';
import { hash, THUMB_EVENT } from '../viewport/thumbnails';
import type { CharmMotion } from '../viewport/refs';
import type { ImportReply } from './import-worker';
import { COLLIDER_JOINTS, COLLIDER_RADIUS, matchOf, useCharacter, type BoneRow, type Capsule, type ColliderJoint, type RigCheck, type Spring, type SpringKind } from './state';

/** Every canonical joint, body then fingers (the site's rig, the same table as ForgeCore's ClipRig). */
export const BODY_JOINTS = HumanJoints;
export const FINGER_JOINTS = HumanFingerJoints;

const CANONICAL_HIP = 1.04;

let scene: Group | null = null;
const byName = new Map<string, Bone>();
const bindLocal = new Map<Bone, Quaternion>();
const bindPosition = new Map<Bone, Vector3>();
/** Canonical joint to bone name as the loaded file's own humanoid (VRM) says; empty for a file without one. */
let fileJoints: Record<string, string> = {};
let sceneVersion = 0;
const listeners = new Set<() => void>();

/** The loaded model (null before an import), and a number that changes with each import, for useSyncExternalStore. */
export const modelScene = () => scene;
export const modelVersion = () => sceneVersion;
export function onModel(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}
export const boneOf = (name: string | undefined | null) => (name ? byName.get(name) : undefined);

const toast = (kind: 'ok' | 'error' | 'info', text: string) => useStore.getState().toast(kind, text);

/** Reads the model's copy into three.js for the view (turned round when the file faces the other way); its bones, null with a toast
 * when it has no skin or two bones share a name. */
async function loadScene(source: string, turned: boolean): Promise<{ rows: BoneRow[]; vertices: number } | null> {
  const gltf = await new GLTFLoader().loadAsync(fileUrl(source));
  if (!hasSkin(gltf.scene)) return null;
  // A see-through material is drawn as the game draws it (characterBlend), not blended: blended layers in one place draw over each other.
  const json = gltf.parser.json as { materials?: { alphaMode?: string }[] };
  gltf.parser.associations.forEach((a, o) => {
    const material = o as MeshStandardMaterial;
    if (!material.isMaterial || !a || !('materials' in a) || a.materials === undefined || json.materials?.[a.materials]?.alphaMode !== 'BLEND') return;
    const how = characterBlend(!!material.map, material.opacity);
    material.transparent = false;
    material.depthWrite = true;
    material.alphaTest = how === 'mask' ? characterBlendCutoff : 0;
    if (how === 'opaque') material.opacity = 1;
    if (how === 'drop') material.visible = false;
  });
  // A helper bone the file aims by a constraint rides the bone it follows, at its rest pose, as the converter packs it.
  const nodeObject = new Map<number, Object3D>();
  gltf.parser.associations.forEach((a, o) => void (a && 'nodes' in a && a.nodes !== undefined && o instanceof Object3D && nodeObject.set(a.nodes, o)));
  gltf.scene.updateMatrixWorld(true);
  aimFollow(gltf.parser.json).forEach((under, bone) => {
    const o = nodeObject.get(bone),
      to = nodeObject.get(under);
    if (o && to && !o.getObjectById(to.id)) to.attach(o);
  });
  const vrm = upgradeVrm(gltf.parser.json);
  const names = (gltf.parser.json as { nodes?: { name?: string }[] }).nodes ?? [];
  fileJoints = Object.fromEntries(vrm ? vrmJoints(vrm).map(([joint, node]) => [joint, names[node]?.name ?? '']) : []);
  return showScene(gltf.scene, turned);
}

/** The model as the pack holds it (a work moved from a character pack, which has no source file): built straight from its
 * converted files by the site's one way of drawing an imported model. It already faces +Z, with every helper bone where the
 * converter put it. */
async function loadPacked(files: PackedFiles): Promise<{ rows: BoneRow[]; vertices: number } | null> {
  const built = await buildPacked(files);
  fileJoints = {};
  return hasSkin(built) ? showScene(built, false) : null;
}

type PackedFiles = { descriptor: string; mesh: string; textures: string[] };

/** A converted model built from its files by the site's one way of drawing an imported model. */
async function buildPacked(files: PackedFiles): Promise<Object3D> {
  const bytes = async (path: string) => new Uint8Array(await (await fetch(fileUrl(path))).arrayBuffer());
  const descriptor = readImportedModelDescriptor(JSON.parse(new TextDecoder().decode(await bytes(files.descriptor))));
  return buildImportedScene({ name: 'character', descriptor, mesh: await bytes(files.mesh), textures: await Promise.all(files.textures.map(bytes)) });
}

/** Whether the model has a skinned mesh; a toast when not. */
function hasSkin(model: Object3D): boolean {
  let skinned = false;
  model.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && (skinned = true)));
  if (!skinned) toast('error', t('character.notSkinned'));
  return skinned;
}

/** Makes `model` the view's character: its bones listed and kept by name (null with a toast when two share one). */
function showScene(model: Object3D, turned: boolean): { rows: BoneRow[]; vertices: number } | null {
  const meshes: SkinnedMesh[] = [];
  model.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && meshes.push(o as SkinnedMesh)));
  // Bones in the order a walk from the scene meets them: parents first, as the format lists them.
  const rows: BoneRow[] = [];
  const index = new Map<Object3D, number>();
  byName.clear();
  bindLocal.clear();
  bindPosition.clear();
  model.traverse((o) => {
    if (!(o as Bone).isBone) return;
    // three.js drops '.' and other characters from node names; the skeleton's bones keep the file's own names.
    if (typeof o.userData.name === 'string') o.name = o.userData.name;
    const parent = o.parent && index.has(o.parent) ? index.get(o.parent)! : -1;
    index.set(o, rows.length);
    rows.push({ name: o.name, parent, depth: parent < 0 ? 0 : rows[parent].depth + 1 });
    byName.set(o.name, o as Bone);
    bindLocal.set(o as Bone, o.quaternion.clone());
    bindPosition.set(o as Bone, o.position.clone());
  });
  if (byName.size !== rows.length) return (toast('error', t('character.sameBoneNames')), null);
  model.rotation.y = turned ? Math.PI : 0;
  // The model's root faces +Z as the packed model does (a turned file turned inside it), so the pose copy reads it as the game does.
  const root = new GroupObject();
  root.add(model);
  root.updateMatrixWorld(true);
  scene = root;
  sceneVersion++;
  listeners.forEach((l) => l());
  return { rows, vertices: meshes.reduce((n, m) => n + m.geometry.getAttribute('position').count, 0) };
}

/** The file through the site's converter in a worker: the model files and what the file says about bones, springs and colliders. */
function convert(bytes: Uint8Array): Promise<CharacterImport> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./import-worker.ts', import.meta.url), { type: 'module', name: 'character-import' });
    worker.onmessage = ({ data }: MessageEvent<ImportReply>) => {
      worker.terminate();
      if ('result' in data) resolve(data.result);
      else reject(Object.assign(new Error(data.message), { code: data.code, params: data.params }));
    };
    worker.onerror = (e) => (worker.terminate(), reject(new Error(e.message || 'character import failed')));
    worker.postMessage(bytes);
  });
}

/** A refusal in the author's words: the converter's reason when it has one here, else its code. */
function refusal(e: unknown): string {
  const code = (e as { code?: string }).code;
  const known = code && t(`character.refused.${code}`, { ...(e as { params?: Record<string, unknown> }).params, defaultValue: '' });
  return known || t('character.unreadable', { v0: code && code !== 'unreadable' ? code : friendly(e) });
}

/** Where character skins live: one autosaved project each, its converted model in the folder beside it. */
export const characterDir = () => `${autosaveRoot()}\\character`;
const projectOf = (id: string) => `${characterDir()}\\${id}.forgeskinproj`;
const encode = (text: string) => new TextEncoder().encode(text);

/** Imports a .glb or .vrm as a new character skin: converted once (refused by name when the format cannot hold it), its files
 * written into the skin's folder, the view loaded, and bones, springs and colliders filled from the file's VRM data where it has some. */
export async function importModel(path: string) {
  await saveCharacter();
  useCharacter.setState({ loading: true });
  try {
    const bytes = new Uint8Array(await (await fetch(fileUrl(path))).arrayBuffer());
    let r: CharacterImport;
    try {
      r = await convert(bytes);
    } catch (e) {
      return toast('error', refusal(e));
    }
    if (r.model.descriptor.format !== 2) return toast('error', t('character.notSkinned'));
    editedAt = new Date().toISOString();
    adopted = false;
    const skinId = `char-${crypto.randomUUID().replace(/-/g, '').slice(0, 12)}`;
    const dir = `${characterDir()}\\${skinId}.character`;
    const files = {
      descriptor: `${dir}\\model.json`,
      mesh: `${dir}\\model.forgemesh`,
      textures: r.model.textures.map((_, i) => `${dir}\\t${i}.png`),
      source: `${dir}\\source.${/\.vrm$/i.test(path) ? 'vrm' : 'glb'}`,
    };
    await window.forge.writeFile(files.descriptor, encode(JSON.stringify(r.model.descriptor)));
    await window.forge.writeFile(files.mesh, r.model.mesh);
    for (const [i, png] of r.model.textures.entries()) await window.forge.writeFile(files.textures[i], png);
    await window.forge.writeFile(files.source, bytes);
    const loaded = await loadScene(files.source, r.turned);
    if (!loaded) return;
    const materials = r.model.descriptor.materials.map((m) => m.name);
    useCharacter.setState({
      skinId,
      name: path
        .split(/[\\/]/)
        .pop()!
        .replace(/\.(glb|vrm)$/i, ''),
      files,
      turned: r.turned,
      capsules: r.capsules.length ? r.capsules : null,
      mtoon: r.mtoon,
      credit: r.credit,
      model: files.source,
      bones: loaded.rows,
      materials,
      vertices: r.model.stats.vertices,
      auto: autoMatch(),
      picked: {},
      joint: null,
      springs: r.springs.map((x) => ({
        id: x.id,
        kind: x.kind,
        strands: x.strands,
        anchor: x.anchor,
        linkSide: x.linkSide,
        loop: x.loop,
        stiffness: x.stiffness,
        damping: x.damping,
        gravity: x.gravity,
        radius: x.radius,
      })),
      spring: null,
      check: null,
      shaders: Object.fromEntries(materials.map((m) => [m, 'lit' as const])),
      hide: [],
      height: 1,
      view: 'body',
      step: 'bones',
      clip: null,
      playing: false,
    });
    toast('ok', t(r.vrm ? 'character.importedVrm' : 'character.imported', { v0: loaded.rows.length, v1: r.springs.length, v2: r.capsules.length }));
    await checkMatch();
    await saveCharacter();
    void stageIcon();
  } catch (e) {
    toast('error', t('character.unreadable', { v0: friendly(e) }));
  } finally {
    useCharacter.setState({ loading: false });
  }
}

/** Asks for a model file and imports it; false when the player cancelled. */
export async function chooseModel(): Promise<boolean> {
  const [file] = await window.forge.openDialog('file', [{ name: t('character.glbFiles'), extensions: ['glb', 'vrm'] }], 'character');
  if (file) await importModel(file);
  return !!file;
}

/** Whether a .glb/.gltf is a character: it carries VRM data, or the site's matcher finds hips on one of its root bones (the one joint
 * a character needs; a creature without fingers or an arm is one too). Null when the file cannot be read. */
export async function isCharacterModel(path: string): Promise<boolean | null> {
  try {
    const gltf = await new GLTFLoader().loadAsync(fileUrl(path));
    if ((gltf.parser.json.extensionsUsed as string[] | undefined)?.some((name) => /^VRM/.test(name))) return true;
    let humanoid = false;
    gltf.scene.traverse((o) => {
      if (!humanoid && (o as Bone).isBone && !(o.parent as Bone | null)?.isBone) humanoid = matchHumanBones<Object3D>(o).bones.has('hip');
    });
    return humanoid;
  } catch {
    return null;
  }
}

// ---- the character document: built, autosaved and reopened like a weapon skin's project ----

/** What only the editor reads back (the pack drops it): the author's match and spring sets as the panels hold them. */
interface Saved {
  source: string;
  turned: boolean;
  auto: Record<string, string>;
  picked: Record<string, string | null>;
  springs: Spring[];
  colliders: ReturnType<typeof useCharacter.getState>['colliders'];
  capsules: Capsule[] | null;
  mtoon: Record<string, unknown>;
  vertices: number;
}

/** The colliders as the document holds them: the file's own, else the automatic ones switched on, each on its joint's bone. */
function capsuleEntries(): Capsule[] {
  const s = useCharacter.getState();
  const match = oneJointPerBone(matchOf(s));
  if (s.capsules) return s.capsules.filter((c) => match[c.joint]);
  scene?.updateMatrixWorld(true);
  const local = (bone: Bone, w: Vector3): [number, number, number] => {
    const p = bone.worldToLocal(w.clone());
    return [-p.x, p.y, p.z];
  };
  return colliders().flatMap((c) => {
    const bone = byName.get(match[c.joint] ?? '');
    return bone ? [{ joint: c.joint, a: local(bone, c.a), b: local(bone, c.b), radius: Math.min(0.5, Math.max(0.005, c.radius)) }] : [];
  });
}

/** The character skin as a document (Core/Character.cs): what the pack builds from, and what the autosave holds. */
export function characterDoc(): SkinDocument | null {
  const s = useCharacter.getState();
  if (!s.skinId || !s.files) return null;
  const saved: Saved = {
    source: s.files.source.split(/[\\/]/).pop()!,
    turned: s.turned,
    auto: s.auto,
    picked: s.picked,
    springs: s.springs,
    colliders: s.colliders,
    capsules: s.capsules,
    mtoon: s.mtoon,
    vertices: s.vertices,
  };
  return {
    schema: 1,
    id: s.skinId,
    name: s.name,
    author: '',
    version: '1.0.0',
    target: { weapon: 'character' },
    materials: [],
    attachments: [],
    parts: [],
    sounds: [],
    character: {
      model: { descriptor: s.files.descriptor, mesh: s.files.mesh, textures: s.files.textures },
      bones: oneJointPerBone(matchOf(s)),
      springs: { chains: s.springs.map(springEntry), capsules: capsuleEntries() },
      materials: s.materials.map((name) => ({ name, shader: 'lit' })),
      ...(s.hide.length ? { hide: s.hide } : {}),
      height: s.height,
    },
    editor: { character: saved, ...(s.credit ? { credit: s.credit } : {}), ...(editedAt ? { editedAt } : {}), ...(adopted ? { adopted } : {}) } as SkinDocument['editor'],
  };
}

/** The skin's project file (its autosave). */
export const characterProject = () => {
  const id = useCharacter.getState().skinId;
  return id ? projectOf(id) : null;
};

/** Writes the autosave now; a failure goes to the log and the top bar's save notice like a weapon skin's. */
export async function saveCharacter() {
  clearTimeout(saveTimer);
  const doc = characterDoc();
  const path = characterProject();
  if (!doc || !path) return;
  try {
    await call('project.save', { path, project: doc });
    useStore.setState({ autosaveError: null });
    if (!useStore.getState().characters.some((k) => k.path === path && k.name === doc.name)) void refreshCharacters();
  } catch (e) {
    useStore.getState().log(`project.save ${path}: ${friendly(e)}`);
    useStore.setState({ autosaveError: friendly(e, t('store.k28')) });
  }
}

let saveTimer: ReturnType<typeof setTimeout> | undefined;
/** When the author last changed the open character skin (ISO), saved as editor.editedAt. */
let editedAt: string | null = null;
/** Whether the open character skin was made from someone else's pack (editor.adopted), kept through its saves. */
let adopted = false;
// Every change the author makes autosaves after a pause, as a weapon skin does.
useCharacter.subscribe((s, before) => {
  if (!s.skinId || s.loading) return;
  if (
    s.name === before.name &&
    s.auto === before.auto &&
    s.picked === before.picked &&
    s.springs === before.springs &&
    s.colliders === before.colliders &&
    s.capsules === before.capsules &&
    s.hide === before.hide &&
    s.height === before.height
  )
    return;
  editedAt = new Date().toISOString();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => void saveCharacter(), 1500);
});

/** The character skins (their autosaves), newest first, into the store's work list. */
export async function refreshCharacters(): Promise<ProjectEntry[]> {
  const characters: ProjectEntry[] = await call('project.list', { dir: characterDir() }).catch(() => []);
  useStore.setState({ characters });
  return characters;
}

/** Opens the character skin edited last (its autosave), once, when the tab first shows with nothing imported. */
let reopened = false;
export async function reopenCharacter() {
  if (reopened || useCharacter.getState().skinId) return;
  reopened = true;
  const [last] = await refreshCharacters();
  if (last) await openCharacter(last.path);
}

/** Moves a character skin and its converted model to the Recycle Bin and takes it out of the chosen profile, as a weapon skin's
 * delete does; deleting the open one opens the next, else none. */
export async function deleteCharacter(path: string) {
  const open = characterProject() === path;
  if (open) clearTimeout(saveTimer);
  const s = useStore.getState();
  const entry = s.characters.find((k) => k.path === path);
  const id = entry?.id;
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  try {
    await call('project.delete', { path });
    // An adopted work shares its id with the pack it was made from, which stays installed.
    if (profile && id && !entry?.adopted && s.installed.some((p) => p.id === id && p.source !== 'mod')) {
      await call('pack.remove', { profile: profile.path, skinId: id });
      void s.refreshInstalled();
    }
  } catch (e) {
    return useStore.getState().fail(e, t('store.skinDeleteFailed'));
  }
  const [next] = await refreshCharacters();
  if (!open) return;
  useCharacter.setState({ skinId: null, name: '', files: null, model: null, bones: [], materials: [], check: null, step: 'model' });
  if (next) await openCharacter(next.path);
}

/** Opens the character skin saved at `path`, the one open now saved first. */
export async function openCharacter(path: string) {
  if (characterProject() === path) return;
  await saveCharacter();
  useCharacter.setState({ loading: true });
  try {
    const doc = await call('project.load', { path });
    const c = doc.character;
    if (!c?.model?.descriptor || !c.model.mesh) return;
    // A work moved from a character pack has no notes of the editor's yet: the pack's match and springs are its own.
    const saved: Saved = (doc.editor as { character?: Saved } | null)?.character ?? {
      source: '',
      turned: false,
      auto: Object.fromEntries(Object.entries(c.bones ?? {}).filter((e): e is [string, string] => !!e[1])),
      picked: {},
      springs: (c.springs?.chains ?? []).flatMap((x) => (x ? [{ ...x, anchor: x.anchor ?? undefined } as Spring] : [])),
      colliders: Object.fromEntries(COLLIDER_JOINTS.map((j) => [j, { on: true, scale: 1 }])) as Saved['colliders'],
      capsules: (c.springs?.capsules ?? []) as Capsule[],
      mtoon: {},
      vertices: 0,
    };
    const files = {
      descriptor: c.model.descriptor,
      mesh: c.model.mesh,
      textures: (c.model.textures ?? []).filter((x): x is string => !!x),
      source: saved.source ? `${c.model.descriptor.replace(/[\\/][^\\/]*$/, '')}\\${saved.source}` : '',
    };
    const loaded = files.source ? await loadScene(files.source, saved.turned) : await loadPacked(files);
    if (!loaded) return;
    editedAt = (doc.editor as { editedAt?: string } | null)?.editedAt ?? null;
    adopted = (doc.editor as { adopted?: boolean } | null)?.adopted === true;
    useCharacter.setState({
      skinId: doc.id ?? null,
      name: doc.name ?? '',
      files,
      turned: saved.turned,
      capsules: saved.capsules,
      mtoon: saved.mtoon ?? {},
      credit: (doc.editor as { credit?: string } | null)?.credit ?? null,
      model: files.source || files.mesh,
      bones: loaded.rows,
      materials: (c.materials ?? []).map((m) => m?.name ?? ''),
      vertices: saved.vertices || loaded.vertices,
      auto: saved.auto,
      picked: saved.picked,
      springs: saved.springs,
      colliders: saved.colliders,
      shaders: Object.fromEntries((c.materials ?? []).map((m) => [m?.name ?? '', 'lit' as const])),
      hide: (c.hide ?? []).filter((x): x is number => typeof x === 'number'),
      height: c.height ?? 1,
      view: 'body',
      step: 'bones',
      clip: null,
      playing: false,
    });
    await checkMatch();
    // A card without its picture, or with one drawn from something else, gets it now.
    if (!(await pictureCurrent(doc.id ?? '', c))) void stageIcon();
  } catch (e) {
    toast('error', t('character.reopenFailed', { v0: friendly(e) }));
  } finally {
    useCharacter.setState({ loading: false });
  }
}

/** Builds the character skin's pack at `out` (the backend copies the model files in and reads the pack back as the plugin does). */
export async function buildCharacterPack(out: string): Promise<string> {
  await saveCharacter();
  const doc = characterDoc();
  const projectPath = characterProject();
  if (!doc?.character || !projectPath) throw new Error(t('character.needModel'));
  // The 3D view goes in as the card picture: the game's skin page shows it without loading the model.
  const picture = await stageIcon();
  if (picture) doc.character.picture = picture;
  return (await call('pack.build', { project: doc, projectPath, out })).pack;
}

// The character's picture (PICTURE wide by tall, the pack's and the mod icon's), drawn offscreen with only the model in it: no bones,
// joints, colliders or grid, on a clear background. The model stands with its arms hanging (whatever its rest pose, T or A), seen from
// the front a little to its left and slightly above, framed full length by its own bounds; every model the same way.
const PICTURE = [512, 768] as const;

/** Swings `bone` so the way from it to `next` points along `toward` (world space); its children follow. */
function aim(bone: Object3D, next: Object3D, toward: Vector3) {
  bone.updateWorldMatrix(true, false);
  const start = bone.getWorldPosition(new Vector3());
  const now = next.getWorldPosition(new Vector3()).sub(start).normalize();
  const turn = new Quaternion().setFromUnitVectors(now, toward.clone().normalize());
  const parent = bone.parent!.getWorldQuaternion(new Quaternion());
  const world = bone.getWorldQuaternion(new Quaternion());
  bone.quaternion.copy(parent.invert().multiply(turn.multiply(world)));
  bone.updateWorldMatrix(false, true);
}

/** The natural stance's arm directions against straight down (-Y), per unit of the drop; the model's front is +Z. The upper arm hangs a
 *  little out from the body, the forearm a little further out and forward, so the elbow bends slightly as a relaxed person's does (set by
 *  eye on the bundled characters). */
const UPPER_ARM_OUT = 0.22;
const UPPER_ARM_FORWARD = 0.02;
const FOREARM_OUT = 0.12;
const FOREARM_FORWARD = 0.22;

/** Where a character's picture is taken from, as a direction from the model's centre: in front (+Z), a little to its side and a little
 *  above. The plugin photographs a pack without a picture from the same direction (Previews.PictureSide / PictureRise in
 *  Mods/ForgeSkin/Native/Previews.cs); change both together. */
const PICTURE_SIDE = 0.35;
const PICTURE_RISE = 0.15;

/** The arms let down beside the body, elbows a little bent; `joint` finds the model's bone for a canonical joint. */
function standing(joint: (name: string) => Object3D | undefined) {
  for (const side of ['left', 'right'] as const) {
    const [upper, lower, hand] = (['UpperArm', 'LowerArm', 'Hand'] as const).map((j) => joint(`${side}${j}`));
    if (!upper || !lower || !hand) continue;
    const out = Math.sign(lower.getWorldPosition(new Vector3()).x - upper.getWorldPosition(new Vector3()).x) || (side === 'left' ? 1 : -1);
    aim(upper, lower, new Vector3(UPPER_ARM_OUT * out, -1, UPPER_ARM_FORWARD));
    aim(lower, hand, new Vector3(FOREARM_OUT * out, -1, FOREARM_FORWARD));
  }
}

/** The character stage's lights: CharacterStage.tsx lights the 3D view with them and the picture is lit the same way. */
export const CHARACTER_LIGHTS = {
  ambient: 0.6,
  key: { intensity: 2.2, position: [2, 4, 3] as [number, number, number] },
  fill: { intensity: 0.8, position: [-3, 2, -2] as [number, number, number] },
};

let pictureRenderer: WebGLRenderer | null = null;
let pictures: Promise<unknown> = Promise.resolve();

/** Draws `model` as the picture, one at a time, as the character stage draws it (its lights, ACES tone mapping as the 3D view's
 *  renderer applies by default, no reflection map); `joint` finds its bone for a canonical joint, `reset` puts its bones back. */
function drawPicture(model: Object3D, joint: (name: string) => Object3D | undefined, reset: () => void): Promise<string | null> {
  const run = pictures.then(() => {
    pictureRenderer ??= Object.assign(new WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true }), { outputColorSpace: SRGBColorSpace, toneMapping: ACESFilmicToneMapping });
    pictureRenderer.setSize(PICTURE[0], PICTURE[1], false);
    return photograph(pictureRenderer, model, joint, reset);
  });
  pictures = run.catch(() => undefined);
  return run;
}

function photograph(renderer: WebGLRenderer, model: Object3D, joint: (name: string) => Object3D | undefined, reset: () => void): string | null {
  const home = model.parent;
  const stage = new Scene();
  const key = new DirectionalLight(0xffffff, CHARACTER_LIGHTS.key.intensity);
  key.position.set(...CHARACTER_LIGHTS.key.position);
  const fill = new DirectionalLight(0xffffff, CHARACTER_LIGHTS.fill.intensity);
  fill.position.set(...CHARACTER_LIGHTS.fill.position);
  stage.add(new AmbientLight(0xffffff, CHARACTER_LIGHTS.ambient), key, fill, model);
  try {
    reset();
    standing(joint);
    model.updateMatrixWorld(true);
    const box = new Box3().setFromObject(model, true);
    if (box.isEmpty()) return null;
    const center = box.getCenter(new Vector3());
    const size = box.getSize(new Vector3());
    const fov = 30;
    const half = Math.tan(((fov / 2) * Math.PI) / 180);
    const aspect = PICTURE[0] / PICTURE[1];
    const distance = (Math.max(size.y / 2, Math.max(size.x, size.z) / 2 / aspect) / half) * 1.12 + size.z / 2;
    const camera = new PerspectiveCamera(fov, aspect, distance / 100, distance * 10);
    camera.position.copy(center).addScaledVector(new Vector3(PICTURE_SIDE, PICTURE_RISE, 1).normalize(), distance);
    camera.lookAt(center);
    renderer.render(stage, camera);
    return renderer.domElement.toDataURL('image/png');
  } finally {
    if (home) home.add(model);
    else stage.remove(model);
    reset();
  }
}

/** The character's picture as a PNG in the data dir: the pack's picture, and the mod icon until the author picks one. */
export async function stageIcon(): Promise<string | null> {
  const model = modelScene();
  const id = useCharacter.getState().skinId;
  if (!model || !id) return null;
  const match = matchOf(useCharacter.getState());
  const url = await drawPicture(model, (j) => boneOf(match[j]), restPose);
  return url ? keepPicture(url, characterPicture(id), id, characterDoc()?.character) : null;
}

// A kept picture is noted in localStorage with what it was drawn from (the model's files, the bone match and PICTURE_LOOK), so
// one drawn from something else is drawn again; a picture that came in a pack is noted as such and kept.
/** How pictures are drawn: bumping it draws every kept picture again. */
const PICTURE_LOOK = 2;
const pictureNote = (id: string) => `sf.charPicture.${id}`;
const lookOf = (c: SkinDocument['character'] | undefined) => {
  const m = c?.model;
  const bones = Object.entries(c?.bones ?? {}).sort(([a], [b]) => (a < b ? -1 : 1));
  return `${PICTURE_LOOK}|${hash(JSON.stringify([m?.descriptor, m?.mesh, m?.textures, bones]))}`;
};
const note = (id: string, value: string) => {
  try {
    localStorage.setItem(pictureNote(id), value);
  } catch {
    /* without storage the picture is drawn again next time */
  }
};
/** Whether the kept picture of character work `id` is still the one its document draws (or its pack's own). */
async function pictureCurrent(id: string, c: SkinDocument['character'] | undefined): Promise<boolean> {
  let v: string | null = null;
  try {
    v = localStorage.getItem(pictureNote(id));
  } catch {
    /* no storage: drawn again */
  }
  return (v === 'pack' || v === lookOf(c)) && (await window.forge.exists(characterPicture(id)));
}
/** Notes that work `id`'s kept picture is its pack's own card picture. */
export const notePackPicture = (id: string) => note(id, 'pack');

async function keepPicture(url: string, path: string, id: string, c: SkinDocument['character'] | undefined): Promise<string> {
  await window.forge.writeFile(path, new Uint8Array(await (await fetch(url)).arrayBuffer()));
  note(id, lookOf(c));
  pictureDrawn.set(id, Date.now());
  window.dispatchEvent(new Event(THUMB_EVENT));
  return path;
}

/** A character's picture drawn at `out` without opening it, from its converted model files and bone match (a work moved from a
 * pack, or a pack without a picture of its own): the picture an open character gets. `id` keys it for the cards; null without a model. */
export async function drawPackedPicture(character: NonNullable<SkinDocument['character']>, out: string, id: string): Promise<string | null> {
  const m = character.model;
  if (!m?.descriptor || !m.mesh) return null;
  const model = new GroupObject().add(await buildPacked({ descriptor: m.descriptor, mesh: m.mesh, textures: (m.textures ?? []).filter((x): x is string => !!x) }));
  const bones = new Map<string, Object3D>();
  const rest = new Map<Object3D, [Quaternion, Vector3]>();
  model.traverse((o) => {
    if (!(o as Bone).isBone) return;
    bones.set(typeof o.userData.name === 'string' ? o.userData.name : o.name, o);
    rest.set(o, [o.quaternion.clone(), o.position.clone()]);
  });
  const match = character.bones ?? {};
  const reset = () => {
    rest.forEach(([q, p], o) => void (o.quaternion.copy(q), o.position.copy(p)));
    model.updateMatrixWorld(true);
  };
  try {
    const url = await drawPicture(model, (j) => bones.get(match[j] ?? ''), reset);
    return url ? keepPicture(url, out, id, character) : null;
  } finally {
    model.traverse((o) => {
      const mesh = o as SkinnedMesh;
      if (!mesh.geometry) return;
      mesh.geometry.dispose();
      for (const mat of ([] as Material[]).concat(mesh.material)) {
        for (const v of Object.values(mat)) if ((v as Texture | null)?.isTexture) (v as Texture).dispose();
        mat.dispose();
      }
    });
  }
}

/** Draws the picture of character work `k` in the background when it has none or one drawn from something else, once a session. */
const drawing = new Set<string>();
export function ensureCharacterPicture(k: { id: string; path: string }) {
  if (drawing.has(k.id)) return;
  drawing.add(k.id);
  void (async () => {
    const doc = await call('project.load', { path: k.path });
    if (doc.character && !(await pictureCurrent(k.id, doc.character))) await drawPackedPicture(doc.character, characterPicture(k.id), k.id);
  })().catch(() => undefined);
}

/** Where a character skin's picture is kept (its card in the work list shows it), and when it was last drawn in this session. */
export const characterPicture = (id: string) => `${useStore.getState().dataDir}\\export\\${id}-icon.png`;
export const pictureDrawn = new Map<string, number>();

/** Saves the character skin as a .forgeskin pack where the author picks. */
export async function exportCharacterPack() {
  const s = useCharacter.getState();
  const out = await window.forge.saveDialog(`${s.name || s.skinId}.forgeskin`, [{ name: t('actions.k16'), extensions: ['forgeskin'] }]);
  if (!out) return;
  try {
    const pack = await buildCharacterPack(out);
    useStore.getState().toast('ok', t('actions.k17'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(pack) });
  } catch (e) {
    useStore.getState().fail(e, t('actions.k19'));
  }
}

/** Packs the character skin as a Thunderstore mod through the same export as weapon skins (ForgeSkin format). */
export async function exportCharacterMod(info: { name: string; version: string; description: string; icon: string }): Promise<boolean> {
  const st = useStore.getState();
  const out = await window.forge.saveDialog(`${info.name}-${info.version}.zip`, [{ name: 'Thunderstore', extensions: ['zip'] }]);
  if (!out) return false;
  try {
    const pack = await buildCharacterPack(`${st.dataDir}\\packs\\${useCharacter.getState().skinId}.forgeskin`);
    const r = await call('pack.exportMod', { packs: [pack], out, ...info, format: 'forgeskin', gunPack: false });
    st.toast('ok', t('exportMod.done'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(r.mod) });
    return true;
  } catch (e) {
    st.fail(e, t('exportMod.failed'));
    return false;
  }
}

/** The values each preset starts from, read once from ForgeCore's ChainSpec presets (springs.presets). */
export type PresetValues = Record<SpringKind, { stiffness: number; damping: number; weight: number; gravity: number; radius: number }>;
let presets: PresetValues | null = null;
const presetListeners = new Set<() => void>();
export function useSpringPresets(): PresetValues | null {
  const value = useSyncExternalStore(
    (l) => (presetListeners.add(l), () => void presetListeners.delete(l)),
    () => presets,
  );
  useEffect(() => {
    if (!presets)
      void call('springs.presets', {}).then(
        (r) => ((presets = r as PresetValues), presetListeners.forEach((l) => l())),
        (e) => toast('error', friendly(e)),
      );
  }, []);
  return value;
}

/** A match with each bone on one joint: the first joint in the rig's order keeps it, the later ones are left without a bone (a
 * matcher that found no bone of its own for a joint is not to stand in another joint's; a bone takes one joint's pose). */
function oneJointPerBone(match: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  const taken = new Set<string>();
  for (const joint of [...BODY_JOINTS, ...FINGER_JOINTS]) {
    const bone = match[joint];
    if (!bone || taken.has(bone)) continue;
    taken.add(bone);
    out[joint] = bone;
  }
  return out;
}

/** The automatic match, on import and on 重新自动匹配 alike: what the file's own humanoid (VRM) says first; the joints it does not name
 * come from the site's matcher (run on every root bone of the model, the root it matches best wins, with the fingers under each hand),
 * which never takes a bone the file gave a joint. A part neither finds is left without a bone. */
function autoMatch(): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [joint, bone] of Object.entries(fileJoints)) if (byName.has(bone)) out[joint] = bone;
  const named = new Set(Object.values(out));
  const add = (joint: string, bone: Object3D) => void (joint in out || named.has(bone.name) || (out[joint] = bone.name));
  let best: { found: Map<string, Object3D>; missing: number } | null = null;
  for (const bone of byName.values()) {
    if ((bone.parent as Bone | null)?.isBone) continue;
    const { bones: found, missing } = matchHumanBones<Object3D>(bone);
    if (!best || missing.length < best.missing) best = { found, missing: missing.length };
  }
  for (const [joint, bone] of best?.found ?? []) if (BODY_JOINTS.includes(joint)) add(joint, bone);
  for (const side of ['left', 'right'] as const) {
    const hand = byName.get(out[side + 'Hand']);
    if (hand) for (const [joint, bone] of matchHumanFingers<Object3D>(hand, side)) add(joint, bone);
  }
  return oneJointPerBone(out);
}

/** Auto-matches again, dropping the author's picks. */
export function rematch() {
  useCharacter.setState({ auto: autoMatch(), picked: {} });
  void checkMatch();
}

const v = new Vector3();

/** Holds the current match to ForgeCore's rig check: rest positions in the game's axes (X mirrored), the model's own space. */
export async function checkMatch() {
  if (!scene) return;
  scene.updateMatrixWorld(true);
  const joints: Record<string, number[]> = {};
  for (const [joint, name] of Object.entries(matchOf(useCharacter.getState()))) {
    const bone = byName.get(name);
    if (!bone) continue;
    bone.getWorldPosition(v);
    joints[joint] = [-v.x, v.y, v.z];
  }
  try {
    const check = (await call('character.check', { joints })) as RigCheck;
    useCharacter.setState({ check });
  } catch (e) {
    toast('error', friendly(e));
  }
}

// ---- the first-person arms ----

/** The model's own meshes shown or not (the arms view hides them). */
export function showBody(visible: boolean) {
  scene?.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && !o.userData.arms && (o.visible = visible)));
}

/** The first-person arms the game wears for this match (backend character.arms: ForgeCore's own cut, `hide` left out), as a mesh on
 * the model's own skeleton so the moves play on it; null (with a toast) when the backend refuses. Its vertices come in Unity's axes and
 * winding, turned back into the view's (X negated, triangles reversed). The caller removes it from the scene and disposes it. */
export async function buildArms(): Promise<SkinnedMesh | null> {
  const s = useCharacter.getState();
  if (!scene || !s.files) return null;
  let r: Methods['character.arms'][1];
  try {
    r = await call('character.arms', { descriptor: s.files.descriptor, mesh: s.files.mesh, bones: matchOf(s), hide: s.hide });
  } catch (e) {
    toast('error', friendly(e));
    return null;
  }
  const bytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0)).buffer;
  const positions = new Float32Array(bytes(r.positions));
  const normals = new Float32Array(bytes(r.normals));
  for (let i = 0; i < positions.length; i += 3) {
    positions[i] = -positions[i];
    normals[i] = -normals[i];
  }
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new Float32BufferAttribute(positions, 3));
  geometry.setAttribute('normal', new Float32BufferAttribute(normals, 3));
  geometry.setAttribute('uv', new Float32BufferAttribute(new Float32Array(bytes(r.uvs)), 2));
  geometry.setAttribute('skinIndex', new Uint16BufferAttribute(new Uint16Array(bytes(r.index)), 4));
  geometry.setAttribute('skinWeight', new Float32BufferAttribute(new Float32Array(bytes(r.weight)), 4));
  const index: number[] = [];
  const materials: Material[] = [];
  const named = new Map<string, Material>();
  scene.traverse((o) => {
    const m = (o as SkinnedMesh).material;
    if (!(o as SkinnedMesh).isSkinnedMesh || !m) return;
    for (const x of Array.isArray(m) ? m : [m]) if (!named.has(x.name)) named.set(x.name, x);
  });
  for (const sub of r.submeshes) {
    const tris = new Uint32Array(bytes(sub.triangles));
    const start = index.length;
    // The mesh file's triangles wind the other way (Unity's); X negated back, they are reversed back.
    for (let i = 0; i + 2 < tris.length; i += 3) index.push(tris[i], tris[i + 2], tris[i + 1]);
    geometry.addGroup(start, tris.length, materials.length);
    materials.push(named.get(s.materials[sub.material] ?? '') ?? new MeshStandardMaterial({ color: '#9aa4b0' }));
  }
  geometry.setIndex(index);
  if (!index.length) {
    toast('info', t('character.armsEmpty'));
    return null;
  }
  // Bound at rest, on the view's own bones by name (the converter keeps the file's names).
  restPose();
  scene.updateMatrixWorld(true);
  const bones = r.bones.map((name) => byName.get(name) ?? new Bone());
  const mesh = new SkinnedMeshObject(geometry, materials);
  mesh.userData.arms = true;
  mesh.frustumCulled = false;
  scene.add(mesh);
  mesh.updateMatrixWorld(true);
  mesh.bind(new Skeleton(bones));
  return mesh;
}

/** Gives `joint` the bone the author picked, none (null: the part is left out), or the automatic match's again (undefined). A bone
 * takes one joint's pose, so a bone another joint had moves here and that joint is left without one. */
export function pickBone(joint: string, bone: string | null | undefined) {
  useCharacter.setState((s) => {
    const picked = Object.fromEntries(Object.entries(s.picked).filter(([k]) => k !== joint));
    if (bone !== undefined) picked[joint] = bone;
    const now = bone === undefined ? s.auto[joint] : bone;
    if (now) for (const [other, used] of Object.entries(matchOf({ auto: s.auto, picked }))) if (other !== joint && used === now) picked[other] = null;
    return { picked };
  });
  void checkMatch();
}

// ---- spring bones ----

const isBone = (o: Object3D): o is Bone => (o as Bone).isBone === true;

/** The strands a spring set starts at `name`: a bone with two or more child bones (a skirt's ring) starts one strand per child, any
 * other one strand of its own; each strand follows the first child bone down to its end. */
export function strandsFrom(name: string): string[][] {
  const bone = byName.get(name);
  if (!bone) return [];
  const down = (b: Bone) => {
    const out: string[] = [];
    for (let at: Bone | undefined = b; at; at = at.children.find(isBone)) out.push(at.name);
    return out;
  };
  const kids = bone.children.filter(isBone);
  return kids.length >= 2 ? kids.map(down) : [down(bone)];
}

/** A starting preset from the bones' names and length. */
export function guessKind(name: string, strands: string[][]): SpringKind {
  const n = name.toLowerCase();
  if (/skirt|dress|cloth|cape/.test(n) || strands.length >= 3) return 'skirt';
  if (/tail|ribbon|ear/.test(n)) return 'tail';
  return Math.max(...strands.map((s) => s.length)) >= 3 ? 'longHair' : 'shortHair';
}

/** The bone a set hangs from: the one the file named, else its first strand's root's parent. */
const anchorOf = (spring: Spring): Object3D | null => (spring.anchor ? byName.get(spring.anchor) : undefined) ?? byName.get(spring.strands[0]?.[0] ?? '')?.parent ?? null;

/** A set as the character document holds it (Core/Character.cs SpringChain): a skirt's strands are linked side by side, and round
 * when there are three or more, unless the file said how. */
export function springEntry(spring: Spring) {
  const linkSide = spring.kind === 'skirt' && spring.strands.length > 1;
  return {
    id: spring.id,
    kind: spring.kind,
    anchor: anchorOf(spring)?.name ?? '',
    strands: spring.strands,
    linkSide: spring.linkSide ?? linkSide,
    loop: spring.loop ?? (linkSide && spring.strands.length > 2),
    weight: spring.weight,
    damping: spring.damping,
    stiffness: spring.stiffness,
    gravity: spring.gravity,
    radius: spring.radius,
  };
}

/** The model's size against the canonical body (its hip height over 1.04 m), for the colliders' radii. */
function sizeRatio(): number {
  const hip = byName.get(matchOf(useCharacter.getState()).hip ?? '');
  if (!scene || !hip) return 1;
  const box = new Box3().setFromObject(scene);
  return Math.max((hip.getWorldPosition(v).y - box.min.y) / CANONICAL_HIP, 0.1);
}

const NEXT: Record<ColliderJoint, string | null> = {
  head: null,
  spine2: 'neck',
  hip: 'spine0',
  leftUpperLeg: 'leftLowerLeg',
  rightUpperLeg: 'rightLowerLeg',
  leftLowerLeg: 'leftFoot',
  rightLowerLeg: 'rightFoot',
};

/** The colliders where the bones are now: two ends and a radius each. The file's own when it set them (on their bones, the game's
 * X mirrored back), else the automatic ones switched on (`ratio`: the model's size, measured now when not given). */
export function colliders(ratio?: number): { joint: string; a: Vector3; b: Vector3; radius: number }[] {
  const s = useCharacter.getState();
  const match = matchOf(s);
  if (s.capsules) {
    scene?.updateMatrixWorld(true);
    return s.capsules.flatMap((c) => {
      const bone = byName.get(match[c.joint] ?? '');
      const world = (p: number[]) => new Vector3(-p[0], p[1], p[2]).applyMatrix4(bone!.matrixWorld);
      return bone ? [{ joint: c.joint, a: world(c.a), b: world(c.b), radius: c.radius }] : [];
    });
  }
  ratio ??= sizeRatio();
  const out: { joint: ColliderJoint; a: Vector3; b: Vector3; radius: number }[] = [];
  for (const joint of COLLIDER_JOINTS) {
    const c = s.colliders[joint];
    const bone = byName.get(match[joint] ?? '');
    if (!c.on || !bone) continue;
    const a = bone.getWorldPosition(new Vector3());
    const nextBone = byName.get(match[NEXT[joint] ?? ''] ?? '');
    const neck = byName.get(match.neck ?? '');
    const b = nextBone ? nextBone.getWorldPosition(new Vector3()) : neck ? a.clone().add(a.clone().sub(neck.getWorldPosition(new Vector3()))) : a.clone().add(new Vector3(0, 0.2 * ratio, 0));
    out.push({ joint, a, b, radius: COLLIDER_RADIUS[joint] * c.scale * ratio });
  }
  return out;
}

/** One set's chain at rest: per strand its bones and a tip past the last one, as nodes in the anchor's frame, and the tip's place
 * in the last bone's frame (where the drawn bone points). */
interface Chain {
  spring: Spring;
  anchor: Object3D;
  strands: { bones: Bone[]; nodes: number[]; tip: Vector3 }[];
  nodes: { parent: number; at: number[] }[];
}

function chainOf(spring: Spring): Chain | null {
  const anchor = anchorOf(spring);
  if (!scene || !anchor) return null;
  scene.updateMatrixWorld(true);
  const pos = new Vector3(),
    rot = new Quaternion(),
    scl = new Vector3();
  anchor.matrixWorld.decompose(pos, rot, scl);
  const inverse = rot.clone().invert();
  const local = (w: Vector3) => w.clone().sub(pos).applyQuaternion(inverse).toArray();
  const nodes: Chain['nodes'] = [];
  const strands: Chain['strands'] = [];
  for (const names of spring.strands) {
    const bones = names.map((n) => byName.get(n)).filter((b): b is Bone => !!b);
    if (!bones.length) continue;
    const at = bones.map((b) => b.getWorldPosition(new Vector3()));
    const last = at[at.length - 1];
    const before = at.length > 1 ? at[at.length - 2] : (bones[0].parent?.getWorldPosition(new Vector3()) ?? last.clone().add(new Vector3(0, 0.05, 0)));
    const step = last.clone().sub(before);
    const tip = last.clone().add(step.lengthSq() > 1e-10 ? step : new Vector3(0, -0.05, 0));
    const ids: number[] = [];
    [...at, tip].forEach((w, i) => {
      ids.push(nodes.length);
      nodes.push({ parent: i === 0 ? -1 : nodes.length - 1, at: local(w) });
    });
    strands.push({ bones, nodes: ids, tip: bones[bones.length - 1].worldToLocal(tip.clone()) });
  }
  return strands.length ? { spring, anchor, strands, nodes } : null;
}

/** A set's preview: node positions per frame in the still pose's space, played from `started`. */
export interface SpringFrames {
  chain: Chain;
  frames: Float32Array<ArrayBuffer>[];
  started: number;
}

const floats = (base64: string) => {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  return new Float32Array(bytes.buffer);
};

/** Held still from the bound pose with no clip or motion: hair and skirts drop under gravity and come to rest as in the game. */
const STILL_SECONDS = 8;

/** Asks the backend to move every set through `motion` (and SETTLE_SECONDS held still), through the `clip` frames (`at` poses the
 * model at frame i, PREVIEW_FPS frames a second), or held still for STILL_SECONDS. A motion drives the model through a moving space
 * the view does not show; the frames come back in the still one, so the model stays put on screen. */
export async function simulate(motion: CharmMotion | null, clip?: { count: number; at(frame: number): void }): Promise<SpringFrames[]> {
  const s = useCharacter.getState();
  if (!scene || !s.springs.length) return [];
  restPose();
  const chains = s.springs.map(chainOf);
  const ratio = sizeRatio();
  // The body faces +Z (glTF); the motions run along a root's -Z, so a stand-in root turned round leads them.
  const lead = new Object3D();
  lead.rotation.y = Math.PI;
  lead.position.copy(
    byName
      .get(matchOf(s).hip ?? '')
      ?.getWorldPosition(new Vector3())
      .setY(0) ?? new Vector3(),
  );
  lead.updateMatrixWorld(true);
  const count = clip ? clip.count : Math.ceil((motion ? MOTION_SECONDS[motion] + SETTLE_SECONDS : STILL_SECONDS) * PREVIEW_FPS) + 1;
  const motions: Matrix4[] = [];
  // Per set, per frame: its anchor bone's frame and the colliders, both where this frame puts them.
  const sampled = chains.map(() => [] as { time: number; m: number[]; body: number[]; capsules: number[][] }[]);
  for (let i = 0; i < count; i++) {
    const m = new Matrix4(),
      body = [0, 0, 0];
    if (clip) clip.at(i);
    else if (motion) motionMatrix(m, lead, motion, Math.min(i / PREVIEW_FPS, MOTION_SECONDS[motion] - 1e-6), body, 0);
    scene.updateMatrixWorld(true);
    motions.push(m);
    const capsules = colliders(ratio).map((c) => [...c.a.applyMatrix4(m).toArray(), ...c.b.applyMatrix4(m).toArray(), c.radius]);
    chains.forEach((chain, k) => chain && sampled[k].push({ time: i / PREVIEW_FPS, m: new Matrix4().multiplyMatrices(m, chain.anchor.matrixWorld).elements.slice(), body, capsules }));
  }
  const results = await Promise.all(
    s.springs.map(async (spring, k) => {
      const chain = chains[k];
      if (!chain) return null;
      const frames = sampled[k];
      const r = await call('springs.simulate', {
        chain: springEntry(spring),
        nodes: chain.nodes,
        strands: chain.strands.length > 1 ? chain.strands.map((x) => x.nodes) : undefined,
        frames,
      });
      const back = new Matrix4();
      const still = r.frames.map((b64, i) => {
        const p = floats(b64);
        back.copy(motions[i]).invert();
        for (let k = 0; k < p.length; k += 3) {
          v.set(p[k], p[k + 1], p[k + 2]).applyMatrix4(back);
          p[k] = v.x;
          p[k + 1] = v.y;
          p[k + 2] = v.z;
        }
        return p;
      });
      return { chain, frames: still, started: performance.now() };
    }),
  ).catch((e) => {
    toast('error', friendly(e));
    return [];
  });
  return results.flatMap((x): SpringFrames[] => (x ? [x] : []));
}

/** Every spring bone back to the pose it was imported in. */
export function restPose() {
  for (const [bone, q] of bindLocal) bone.quaternion.copy(q);
  for (const [bone, p] of bindPosition) bone.position.copy(p);
  scene?.updateMatrixWorld(true);
}

const from = new Vector3(),
  to = new Vector3(),
  here = new Vector3(),
  next = new Vector3(),
  turn = new Quaternion(),
  world = new Quaternion(),
  parentWorld = new Quaternion();

/** Turns each bone of a set so it points at the next node of frame `f`, root first. */
export function pose(set: SpringFrames, f: number) {
  const p = set.frames[Math.min(f, set.frames.length - 1)];
  set.chain.anchor.updateWorldMatrix(true, false);
  for (const strand of set.chain.strands) {
    strand.bones.forEach((bone, i) => {
      bone.quaternion.copy(bindLocal.get(bone)!);
      bone.updateMatrixWorld(true);
      bone.getWorldPosition(here);
      if (i + 1 < strand.bones.length) strand.bones[i + 1].getWorldPosition(next);
      else next.copy(strand.tip).applyMatrix4(bone.matrixWorld);
      from.subVectors(next, here).normalize();
      const a = strand.nodes[i] * 3,
        b = strand.nodes[i + 1] * 3;
      to.set(p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]);
      if (to.lengthSq() < 1e-12) return;
      turn.setFromUnitVectors(from, to.normalize());
      bone.getWorldQuaternion(world);
      (bone.parent ?? bone).getWorldQuaternion(parentWorld);
      bone.quaternion.copy(parentWorld.invert().multiply(turn.multiply(world)));
      bone.updateMatrixWorld(true);
    });
  }
}
