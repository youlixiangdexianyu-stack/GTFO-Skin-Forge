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
export type BlendMode = NonNullable<LiveryLayer['blend']>;
export type Finish = NonNullable<MaterialOverride['finish']>;

/** Game events in the order the UI lists them; `simulate` ones get a preview button in the 3D view. */
export const TRIGGERS: { value: Trigger; label: string; simulate?: boolean }[] = [
  { value: 'always', get label() { return t('trigger.always'); } },
  { value: 'kill', get label() { return t('trigger.kill'); }, simulate: true },
  { value: 'hit', get label() { return t('trigger.hit'); }, simulate: true },
  { value: 'miss', get label() { return t('trigger.miss'); }, simulate: true },
  { value: 'fire', get label() { return t('trigger.fire'); }, simulate: true },
  { value: 'dryFire', get label() { return t('trigger.dryFire'); } },
  { value: 'reloadStart', get label() { return t('trigger.reloadStart'); }, simulate: true },
  { value: 'reloadEnd', get label() { return t('trigger.reloadEnd'); } },
  { value: 'equip', get label() { return t('trigger.equip'); }, simulate: true },
  { value: 'unequip', get label() { return t('trigger.unequip'); } },
  { value: 'aim', get label() { return t('trigger.aim'); }, simulate: true },
  { value: 'aimEnd', get label() { return t('trigger.aimEnd'); } },
  { value: 'lowAmmo', get label() { return t('trigger.lowAmmo'); }, simulate: true },
  { value: 'empty', get label() { return t('trigger.empty'); } },
  { value: 'ammoPickup', get label() { return t('trigger.ammoPickup'); } },
  { value: 'flashlightOn', get label() { return t('trigger.flashlightOn'); } },
  { value: 'flashlightOff', get label() { return t('trigger.flashlightOff'); } },
];

/** Events a melee weapon raises in the game. */
const MELEE_TRIGGERS = new Set<Trigger>(['always', 'kill', 'hit', 'fire', 'equip', 'unequip']);
/** The bio tracker never fires or reloads. */
const NO_SHOT = new Set<Trigger>(['fire', 'dryFire', 'reloadStart', 'reloadEnd', 'miss']);

/** Items (packs, consumables, objective items): used (applied, thrown, placed, inserted), taken out, put away or dropped. */
const ITEM_TRIGGERS: { value: Trigger; label: string; simulate?: boolean }[] = [
  { value: 'always', get label() { return t('trigger.always'); } },
  { value: 'fire', get label() { return t('trigger.use'); }, simulate: true },
  { value: 'equip', get label() { return t('trigger.takeOut'); }, simulate: true },
  { value: 'unequip', get label() { return t('trigger.putAway'); }, simulate: true },
];

/** The events that can happen on `item` (all of them when unknown). */
export function triggersFor(item: { slot: Slot; id: string; itemId?: number | null } | null | undefined) {
  if (item?.itemId != null) return ITEM_TRIGGERS;
  if (item?.slot === 'melee') return TRIGGERS.filter((x) => MELEE_TRIGGERS.has(x.value));
  if (item?.id === 'bio_tracker') return TRIGGERS.filter((x) => !NO_SHOT.has(x.value));
  // Only a gun's shot can miss.
  if (item?.slot === 'tool') return TRIGGERS.filter((x) => x.value !== 'miss');
  return TRIGGERS;
}

export const CHANNELS: TextureChannel[] = ['albedo', 'normal', 'metallicSmoothness', 'emissive'];

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
  /** The plugin's development switch: skins reload in game on every apply; off, they load once at game start. */
  development?: boolean;
  /** Added by the player with 「选择文件夹…」; can be removed again. */
  custom?: boolean;
  /** Last change of the profile (ISO time), used to pick the most recently used one. */
  modified?: string | null;
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
  reticles: { layer: string; texture: string; color: number[]; scale: number; offset: number }[];
}

export interface WeaponPreview {
  glb: string;
  /** Mesh nodes per part, and the part's transforms (first = the part root, path null) in Unity space, vanilla local pose.
   * Ammo parts say how they are shown: the node moved by a world offset (Unity space) and the nodes hidden. */
  parts: { id: string; nodes: string[]; transforms: PreviewTransform[]; ammo: { node: string; offset: [number, number, number]; hidden: string[] } | null }[];
  textures: Record<string, Partial<Record<TextureChannel, string>>>;
  params: Record<string, PreviewMaterial>;
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
}

/** A skin project on disk (`project.list`). */
export interface ProjectEntry {
  path: string;
  id: string;
  name: string;
  modified: string;
}

export interface InstalledPack {
  id: string;
  name: string;
  file: string;
  weapon?: string | null;
  active: boolean;
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

export type SizeMode = 'keep' | 'original' | '1k' | '2k' | '4k';

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
  'app.info': [Record<string, never>, { version: string; dataDir: string }];
  'game.locate': [Record<string, never>, GameInfo];
  'game.setDir': [{ dir: string }, GameInfo];
  'profiles.list': [Record<string, never>, Profile[]];
  'profiles.add': [{ path: string }, Profile];
  'profiles.remove': [{ path: string }, unknown];
  'catalog.get': [Record<string, never>, { items: CatalogItem[] }];
  'preview.weapon': [{ id: string }, WeaponPreview];
  'match.folder': [{ weaponId: string; folder: string }, MatchResult];
  'match.files': [{ weaponId: string; files: string[] }, MatchResult];
  'project.new': [{ weaponId?: string }, SkinDocument];
  'project.load': [{ path: string }, SkinDocument];
  'project.save': [{ path: string; project: SkinDocument }, { path: string }];
  'project.list': [{ dir: string }, ProjectEntry[]];
  'project.delete': [{ path: string }, unknown];
  'project.missing': [{ project: SkinDocument }, { files: string[] }];
  'mesh.import': [{ glb: string }, MeshImport];
  'pack.build': [{ project: SkinDocument; projectPath: string; out: string; sizeMode: SizeMode }, { pack: string; warnings: string[] }];
  'pack.install': [{ pack: string; profile: string; activate: boolean }, unknown];
  'pack.list': [{ profile: string }, InstalledPack[]];
  'assembly.verify': [{ profile: string }, VerifyResult[]];
  'pack.remove': [{ profile: string; skinId: string }, unknown];
  'pack.activate': [{ profile: string; skinId: string; active: boolean }, unknown];
  'pack.exportMod': [{ packs: string[]; out: string; name: string; version: string; description: string; icon: string }, { mod: string }];
  'assets.apply': [{ pack: string }, { skin: string; buildId: string; files: string[]; materials: string[]; warnings: string[] }];
  'assets.restore': [{ skinId?: string; discardStale?: boolean }, { restored: string[]; skipped: { file: string; reason: string }[]; stale: StaleAssets[] }];
  'assets.status': [Record<string, never>, AssetStatus];
  'assets.export': [{ pack: string; out: string }, { zip: string; buildId: string; files: string[]; warnings: string[] }];
  'plugin.install': [{ profile: string }, unknown];
  'plugin.log': [{ profile: string; since?: string }, { lines: { time: string; level: 'info' | 'warn'; message: string }[] }];
  'plugin.uninstall': [{ profile: string }, unknown];
  'plugin.setDevelopment': [{ profile: string; on: boolean }, { development: boolean; restartNeeded: boolean }];
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
