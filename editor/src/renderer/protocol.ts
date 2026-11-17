// Backend protocol (PLAN §3.3). SkinDocument types come from generated/skin-document.ts; the shapes below are the
// non-document results of the backend methods.
import type { SkinDocument as GenDoc } from './generated/skin-document';
import type { ForgeApi } from '../preload/preload';
import { t } from './i18n';

declare global {
  interface Window {
    forge: ForgeApi;
  }
}

export type SkinDocument = NonNullable<GenDoc>;
export type MaterialOverride = NonNullable<NonNullable<SkinDocument['materials']>[number]>;
export type MaterialParams = NonNullable<MaterialOverride['params']>;
export type Reticle = NonNullable<NonNullable<MaterialParams['reticles']>[string]>;
export type DynamicLayer = NonNullable<NonNullable<MaterialOverride['layers']>[number]>;
export type Attachment = NonNullable<NonNullable<SkinDocument['attachments']>[number]>;
export type PartTransform = NonNullable<NonNullable<SkinDocument['parts']>[number]>;
export type Screen = NonNullable<Attachment['screen']>;
export type TextureChannel = NonNullable<DynamicLayer['channel']>;
export type LayerKind = NonNullable<DynamicLayer['kind']>;
export type ScreenSource = NonNullable<Screen['source']>;
export type Trigger = NonNullable<DynamicLayer['trigger']>;
export type PlayMode = NonNullable<DynamicLayer['play']>;
export type TriggerEffect = NonNullable<NonNullable<Attachment['effects']>[number]>;
export type SkinSound = NonNullable<NonNullable<SkinDocument['sounds']>[number]>;
export type LiveryLayer = NonNullable<NonNullable<MaterialOverride['livery']>[number]>;
export type LiveryKind = NonNullable<LiveryLayer['kind']>;
export type Pattern = NonNullable<LiveryLayer['pattern']>;
export type BlendMode = NonNullable<LiveryLayer['blend']>;
export type Finish = NonNullable<MaterialOverride['finish']>;
export type PlayerColor = NonNullable<NonNullable<SkinDocument['playerColors']>[number]>;
export type ColorSlot = NonNullable<NonNullable<PlayerColor['slots']>[number]>;
export type CodeEffect = NonNullable<NonNullable<SkinDocument['codeEffects']>[number]>;

/** Game events in the order the UI lists them; `simulate` ones get a preview button in the 3D view. */
export const TRIGGERS: { value: Trigger; label: string; simulate?: boolean }[] = [
  {
    value: 'always',
    get label() {
      return t('trigger.always');
    },
  },
  {
    value: 'kill',
    get label() {
      return t('trigger.kill');
    },
    simulate: true,
  },
  {
    value: 'hit',
    get label() {
      return t('trigger.hit');
    },
    simulate: true,
  },
  {
    value: 'miss',
    get label() {
      return t('trigger.miss');
    },
    simulate: true,
  },
  {
    value: 'fire',
    get label() {
      return t('trigger.fire');
    },
    simulate: true,
  },
  {
    value: 'dryFire',
    get label() {
      return t('trigger.dryFire');
    },
  },
  {
    value: 'reloadStart',
    get label() {
      return t('trigger.reloadStart');
    },
    simulate: true,
  },
  {
    value: 'reloadEnd',
    get label() {
      return t('trigger.reloadEnd');
    },
  },
  {
    value: 'equip',
    get label() {
      return t('trigger.equip');
    },
    simulate: true,
  },
  {
    value: 'unequip',
    get label() {
      return t('trigger.unequip');
    },
  },
  {
    value: 'aim',
    get label() {
      return t('trigger.aim');
    },
    simulate: true,
  },
  {
    value: 'aimEnd',
    get label() {
      return t('trigger.aimEnd');
    },
  },
  {
    value: 'lowAmmo',
    get label() {
      return t('trigger.lowAmmo');
    },
    simulate: true,
  },
  {
    value: 'empty',
    get label() {
      return t('trigger.empty');
    },
  },
  {
    value: 'ammoPickup',
    get label() {
      return t('trigger.ammoPickup');
    },
  },
  {
    value: 'flashlightOn',
    get label() {
      return t('trigger.flashlightOn');
    },
  },
  {
    value: 'flashlightOff',
    get label() {
      return t('trigger.flashlightOff');
    },
  },
  {
    value: 'downed',
    get label() {
      return t('trigger.downed');
    },
  },
  {
    value: 'revived',
    get label() {
      return t('trigger.revived');
    },
  },
];

/** Events a melee weapon raises in the game. */
const MELEE_TRIGGERS = new Set<Trigger>(['always', 'kill', 'hit', 'fire', 'equip', 'unequip', 'downed', 'revived']);
/** The bio tracker never fires or reloads. */
const NO_SHOT = new Set<Trigger>(['fire', 'dryFire', 'reloadStart', 'reloadEnd', 'miss']);

/** Items (packs, consumables, objective items): used (applied, thrown, placed, inserted), taken out, put away or dropped. */
const ITEM_TRIGGERS: { value: Trigger; label: string; simulate?: boolean }[] = [
  {
    value: 'always',
    get label() {
      return t('trigger.always');
    },
  },
  {
    value: 'fire',
    get label() {
      return t('trigger.use');
    },
    simulate: true,
  },
  {
    value: 'equip',
    get label() {
      return t('trigger.takeOut');
    },
    simulate: true,
  },
  {
    value: 'unequip',
    get label() {
      return t('trigger.putAway');
    },
    simulate: true,
  },
  {
    value: 'downed',
    get label() {
      return t('trigger.downed');
    },
  },
  {
    value: 'revived',
    get label() {
      return t('trigger.revived');
    },
  },
];

/** One enemy of the installed game, for kill sounds that play only for it: `id` its data block's persistent id (what the
 *  skin stores), `block` the block's own name, `name` / `nameZh` what players call it (the block name when the game ships no
 *  name for it). */
export interface EnemyInfo {
  id: number;
  block: string;
  name: string;
  nameZh: string;
}

/** The events that can happen on `item` (all of them when unknown). */
export function triggersFor(item: { slot: Slot; id: string; itemId?: number | null } | null | undefined) {
  if (item?.itemId != null) return ITEM_TRIGGERS;
  if (item?.slot === 'melee') return TRIGGERS.filter((x) => MELEE_TRIGGERS.has(x.value));
  if (item?.id === 'bio_tracker') return TRIGGERS.filter((x) => !NO_SHOT.has(x.value));
  // Only a gun's shot can miss.
  if (item?.slot === 'tool') return TRIGGERS.filter((x) => x.value !== 'miss');
  return TRIGGERS;
}

/** The texture slots every material has; a holographic sight's lens also has the three reticle layers (RETICLES). */
export const CHANNELS: TextureChannel[] = ['albedo', 'normal', 'metallicSmoothness', 'emissive'];
export type ReticleChannel = Extract<TextureChannel, 'reticleA' | 'reticleB' | 'reticleC'>;
export const RETICLES: ReticleChannel[] = ['reticleA', 'reticleB', 'reticleC'];

export interface GameInfo {
  gameDir: string | null;
  dataDir: string | null;
  found: boolean;
  running: boolean;
}

export interface Profile {
  name: string;
  path: string;
  hasBepInEx: boolean;
  pluginVersion: string | null;
  /** False when the installed plugin differs from the one this app ships (an update is due). */
  pluginCurrent?: boolean;
  /** The plugin version this app ships, which the profile's is held to; null without a plugin build. */
  pluginShipped?: string | null;
  /** Copies from the store newer than the ones this app ships ("ForgeCore 1.0.3"), which it leaves alone. */
  pluginNewer?: string[] | null;
  /** Added by the player with 「选择文件夹…」; can be removed again. */
  custom?: boolean;
  /** Last change of the profile (ISO time), used to pick the most recently used one. */
  modified?: string | null;
  /** Packs in its skins folder. */
  packs?: number;
}

export interface CatalogPart {
  id: string;
  role: string;
  label: string;
  materials: string[];
  defaultEnabled: boolean;
}

export type Slot = 'main' | 'special' | 'melee' | 'tool' | 'pack' | 'consumable' | 'carry' | 'attachment' | 'ammo';

export interface CatalogItem {
  id: string;
  name: string;
  nameZh: string;
  category: string;
  categoryZh?: string | null;
  slot: Slot;
  /** PlayerOfflineGearDataBlock id (guns, melee weapons and tools); null for attachments, lenses and ammo. */
  gearId: number | null;
  /** ItemDataBlock id for items with their own prefab (packs, consumables, objective items, hacking tool); null otherwise. */
  itemId?: number | null;
  calibre?: string | null;
  /** Guns: magazine size and the most bullets the reserve holds (from the game data); null otherwise. */
  clipSize?: number | null;
  maxReserve?: number | null;
  parts: CatalogPart[];
}

export interface PreviewTransform {
  name: string;
  /** Transform.Find path from the part root; null for the root. Together with the part id this is the PartTransform key. */
  path: string | null;
  /** GLB node name. */
  node: string;
  parent: string | null;
}

export interface VerifyResult {
  file: string;
  gearId: number;
  weapon: string | null;
  pass: boolean;
  compared: number;
  worst: { part: string; path: string | null; positionMm: number; rotationDeg: number; scale: number; problem: string | null }[];
}

/** How the game draws a material (see the backend's PreviewMaterial). */
export interface PreviewMaterial extends MaterialParams {
  shader: string;
  renderQueue: number;
  /** opaque; cutout (alpha test); glass (blended lens, tint alpha = opacity); reticle (holo sight: only its reticle layers, additive). */
  kind: 'opaque' | 'cutout' | 'glass' | 'reticle';
  /* `reticles`: a sight's reticle layers as the game has them, every value set (their pictures are in `textures`). */
}

export interface WeaponPreview {
  glb: string;
  /** Mesh nodes per part, and the part's transforms (first = the part root, path null) in Unity space, vanilla local pose.
   * Ammo parts say how they are shown: the node moved by a world offset (Unity space) and the nodes hidden. */
  parts: { id: string; nodes: string[]; transforms: PreviewTransform[]; ammo: { node: string; offset: [number, number, number]; hidden: string[] } | null }[];
  textures: Record<string, Partial<Record<TextureChannel, string>>>;
  params: Record<string, PreviewMaterial>;
  /** Some game textures could not be read (a game file was rewritten outside the game); those show as untextured. */
  damaged: boolean;
  /** Some game textures are not the game's own (another mod rewrote the game files); they show and bake as rewritten. */
  modified: boolean;
}

export interface MatchCell {
  /** null when the folder had no image for this slot. */
  file: string | null;
  confidence: number;
  reason: string;
  needsConfirmation: boolean;
}

export interface MatchResult {
  parts: { partId: string; material: string; channels: Partial<Record<TextureChannel, MatchCell>> }[];
  unmatched: string[];
}

export interface MeshImport {
  forgeskinmesh: string;
  glb: string;
  bounds: { min: number[]; max: number[] };
  /** The model's own pictures as PNGs for the attachment's material (at most 2048 on the longest side). */
  textures: Partial<Record<TextureChannel, string>>;
  /** Its base colour factor when not white; its glow (factor, strength) when it has an emissive picture. */
  color: number[] | null;
  emissive: { color: number[]; intensity: number } | null;
  /** Sizes of the pictures kept, and what was left out. */
  notes: string[];
}

/** A skin project on disk (`project.list`). */
export interface ProjectEntry {
  path: string;
  id: string;
  name: string;
  modified: string;
  /** When the author last changed it (ISO; editor.editedAt), null for a work not changed since this was kept. */
  edited?: string | null;
  /** Made from someone else's installed pack (editor.adopted): deleting it leaves that pack installed. */
  adopted?: boolean;
}

/** Something a Skins import or export left out or changed; worded by `skinsNote.<kind>` (ui/SkinsReport). */
export type SkinsNoteKind =
  // import
  | 'desc'
  | 'meshTarget'
  | 'transformTarget'
  | 'fpsTarget'
  | 'noGear'
  | 'conflict'
  | 'shader'
  | 'emptyGear'
  | 'inverted'
  | 'rundown'
  | 'filterType'
  | 'contradiction'
  | 'unknownGear'
  | 'noMaterial'
  | 'textureOptions'
  | 'badMacro'
  | 'missingFile'
  | 'vector'
  | 'property'
  // export
  | 'itemSkin'
  | 'unusedMaterials'
  | 'global'
  | 'sameMaterial'
  | 'sharedMaterial'
  | 'layers'
  | 'wear'
  | 'reticleIntensity'
  | 'attachments'
  | 'parts'
  | 'sounds'
  | 'playerColors'
  | 'codeEffects'
  | 'variants';
export interface SkinsNote {
  kind: SkinsNoteKind;
  skin: string;
  what: string | null;
  n: number;
}

/** What `skins.import` made: one skin per gear, or nothing yet when the group names no gear (`needsTarget`). */
export interface SkinsImport {
  skins: { path: string; weapon: string; id: string; name: string }[];
  notes: SkinsNote[];
  needsTarget: boolean;
}

export interface InstalledPack {
  id: string;
  name: string;
  file: string;
  weapon?: string | null;
  active: boolean;
  /** Other copies of this skin the listing just moved to the Recycle Bin (file names). */
  dropped?: string[] | null;
  /** editor: the editor's own install; hand: put in the skins folder another way; mod: a skin mod in the plugins folder (read only). */
  source: 'editor' | 'hand' | 'mod';
  /** The skin mod's folder for a mod's pack. */
  mod?: string | null;
  /** The skin the game shows for its target (a pending apply, else the in-game picker's choice, else active.json, else a mod's). */
  inUse: boolean;
  /** The pack file's last write (ISO). */
  modified?: string | null;
  error?: string | null;
}

export interface BackendError {
  code: string;
  message: string;
}

export interface Progress {
  id: number;
  done: number;
  total: number;
  text: string;
}

/** Skins written into the game's own files (直接改游戏文件), per game build. */
export interface StaleAssets {
  buildId: string;
  files: string[];
  skins: string[];
}
export interface AssetStatus {
  gameDir: string;
  buildId: string;
  running: boolean;
  files: { file: string; state: 'patched' | 'original' | 'changed' | 'missing'; backupOk: boolean; skins: string[] }[];
  skins: { id: string; name: string; version: string; appliedAt: string; materials: string[]; warnings: string[] }[];
  stale: StaleAssets[];
}

/** Methods, params and results. */
export interface Methods {
  /** `profile`: `running` then says whether the game runs from that profile (its plugin log is newer than the game's start). */
  'game.locate': [{ profile?: string | null }, GameInfo];
  'game.setDir': [{ dir: string }, GameInfo];
  'profiles.list': [Record<string, never>, Profile[]];
  'profiles.get': [{ path: string }, Profile];
  'profiles.add': [{ path: string }, Profile];
  'profiles.remove': [{ path: string }, unknown];
  'catalog.get': [Record<string, never>, { items: CatalogItem[] }];
  'enemies.list': [Record<string, never>, { enemies: EnemyInfo[] }];
  'preview.weapon': [{ id: string }, WeaponPreview];
  'match.folder': [{ weaponId: string; folder: string }, MatchResult];
  'match.files': [{ weaponId: string; files: string[] }, MatchResult];
  'project.new': [{ weaponId?: string }, SkinDocument];
  'project.load': [{ path: string }, SkinDocument];
  'project.save': [{ path: string; project: SkinDocument }, { path: string }];
  'project.list': [{ dir: string }, ProjectEntry[]];
  'project.listAll': [{ root: string }, Record<string, ProjectEntry[]>];
  'skins.import': [{ manifest: string; root: string; weapon?: string; lang: string }, SkinsImport];
  'project.delete': [{ path: string }, unknown];
  /** `project` (the work saved at `from`) as a copy saved at `to`, with the files kept beside the work copied into the copy's own folders. */
  'project.copy': [{ project: SkinDocument; from: string; to: string }, SkinDocument];
  'project.missing': [{ project: SkinDocument }, { files: string[] }];
  'project.files': [{ project: SkinDocument }, { files: string[] }];
  'mesh.import': [{ glb: string }, MeshImport];
  /** A charm moved by the game's own rope along the hanging point's path, one result frame per input frame; vertices and normals
   * are base64 little-endian float32 in the hanging point's frame, the pendant a world pose (xyz, quaternion xyzw). */
  'charm.simulate': [
    {
      charm: NonNullable<Attachment['charm']>;
      arm: number;
      gear?: number[];
      boxes?: { min: number[]; max: number[] }[];
      frames: { time: number; m: number[]; body: number[] }[];
    },
    { triangles: number[]; metallic: number; smoothness: number; frames: { pendant: number[]; vertices: string; normals: string }[] },
  ];
  /** The values each spring-bone preset starts from (ForgeCore ChainSpec). */
  'springs.presets': [Record<string, never>, Record<'longHair' | 'shortHair' | 'skirt' | 'tail', { stiffness: number; damping: number; weight: number; gravity: number; radius: number }>];
  /** One spring-bone set moved by ForgeCore's chain physics along the anchor bone's path: nodes at rest in the anchor's frame (roots
   * first in each strand), one result frame per input frame of node world positions, base64 little-endian float32. */
  'springs.simulate': [
    {
      /** The set as the character document holds it (Core/Character.cs SpringChain). */
      chain: {
        id: string;
        kind: 'longHair' | 'shortHair' | 'skirt' | 'tail';
        anchor: string;
        strands: string[][];
        linkSide: boolean;
        loop: boolean;
        stiffness?: number;
        damping?: number;
        weight?: number;
        gravity?: number;
        radius?: number;
      };
      nodes: { parent: number; at: number[] }[];
      strands?: number[][];
      frames: { time: number; m: number[]; body: number[]; capsules: number[][] }[];
    },
    { frames: string[] },
  ];
  /** A bone match as the game will take it: `joints` is canonical name to rest position in the game's axes, root space. `missing`
   * alone stops a character; `unposed` are the joints the game leaves in their rest pose, `notes` what looks mismatched. */
  'character.check': [
    { joints: Record<string, number[]> },
    {
      required: string[];
      missing: string[];
      unposed: string[];
      notes: { kind: 'standing' | 'sides' | 'other'; joints: string[]; degrees?: number | null; text: string }[];
      backwards: boolean;
      arms: boolean;
      reach: { left: boolean; right: boolean };
    },
  ];
  /** The game's pose copy for a bone match on the model `descriptor` names (ForgeCore HumanRig): per canonical joint its link and
   * whether the copy leaves it out; `fingers`: the playing body has finger joints. `reach`: upper, lower and end joint of each limb that reaches. */
  'character.links': [
    { descriptor: string; bones: Record<string, string>; fingers: boolean },
    { links: { joint: string; toward: number; from: number; skip: boolean }[]; reach: [number, number, number][] },
  ];
  /** The first-person arms the game cuts from the model for a bone match (ForgeCore HumanRig.ArmWear and MeshCut), the materials in
   * `hide` left out: the kept vertices in Unity's axes (base64 little-endian: positions, normals float32 ×3, uvs ×2, bone indices
   * uint16 ×4 into `bones`, weights float32 ×4) and per submesh its material and triangles (uint32). */
  'character.arms': [
    { descriptor: string; mesh: string; bones: Record<string, string>; hide: number[] },
    { vertices: number; bones: string[]; positions: string; normals: string; uvs: string; index: string; weight: string; submeshes: { material: number; triangles: string }[] },
  ];
  'pack.build': [{ project: SkinDocument; projectPath: string; out: string }, { pack: string; warnings: string[]; memory: number }];
  /** `pick`: 应用到游戏 — the skin also goes before the player's in-game picker choice for its gear. */
  'pack.install': [{ pack: string; profile: string; activate: boolean; pick?: boolean }, { installed: string; active: boolean; dropped: string[] }];
  /** `tidy`: the installed-skins window — extra copies of one skin are moved to the Recycle Bin first (each row's `dropped`). */
  'pack.list': [{ profile: string; tidy?: boolean }, InstalledPack[]];
  'assembly.verify': [{ profile: string }, VerifyResult[]];
  'pack.remove': [{ profile: string; skinId: string }, unknown];
  /** A character pack's card picture (base64 PNG), null without one. */
  'pack.picture': [{ pack: string; cache: string }, { png: string | null; character: SkinDocument['character'] | null }];
  /** An installed pack made into one of the author's works under `dir`; `lost`: what the pack no longer holds (Backend Projects.Adopt). */
  /** `taken`: names of the author's works of that gear; a pack named the same comes back as "<name> 2" and so on (`name`). */
  'pack.adopt': [
    { pack: string; dir: string; taken?: string[] },
    {
      path: string;
      weapon?: string | null;
      name: string;
      lost: ('editor' | 'source' | 'compressed' | 'frames' | 'layers' | 'meshes' | 'screens' | 'missing')[];
      /** A character pack's card picture as unpacked. */
      picture?: string | null;
    },
  ];
  'pack.activate': [{ profile: string; skinId: string }, unknown];
  'pack.exportMod': [
    { packs: string[]; out: string; name: string; version: string; description: string; icon: string; format: 'forgeskin' | 'skins'; gunPack: boolean },
    { mod: string; dropped: SkinsNote[] },
  ];
  'assets.apply': [{ pack: string }, { skin: string; buildId: string; files: string[]; materials: string[]; warnings: string[] }];
  'assets.restore': [{ skinId?: string; discardStale?: boolean }, { restored: string[]; skipped: { file: string; reason: string }[]; stale: StaleAssets[] }];
  'assets.status': [Record<string, never>, AssetStatus];
  'assets.export': [{ pack: string; out: string }, { zip: string; buildId: string; files: string[]; warnings: string[] }];
  'plugin.install': [{ profile: string }, unknown];
  'plugin.log': [{ profile: string; since?: string; from?: number; head?: string }, { lines: { time: string; level: 'info' | 'warn'; message: string }[]; next: number; head: string; reset: boolean }];
  'plugin.uninstall': [{ profile: string }, unknown];
  'export.obj': [{ weaponId: string; parts: string[]; mode: 'separate' | 'merged'; outDir: string }, { dir: string; files: string[] }];
}

export function call<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]> {
  return window.forge.call(method, params) as Promise<Methods[M][1]>;
}

/** URL the renderer can load for an absolute file path. */
export function fileUrl(path: string): string {
  return 'sf://local/' + encodeURIComponent(path.replace(/\\/g, '/'));
}

export function baseName(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export function dirName(path: string): string {
  const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  return i < 0 ? '' : path.slice(0, i);
}

/** The material name of an attachment's model (Core MaterialOverride.ForAttachment): its paint and animated layers go on an
 * override of this name, as on a game material. */
export const attachmentMaterial = (id: string) => `attachment:${id}`;
/** The attachment whose model `material` names, or null for a game material. */
export const attachedOf = (material: string | null | undefined) => (material?.startsWith('attachment:') ? material.slice('attachment:'.length) : null);
