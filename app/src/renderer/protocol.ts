// Backend protocol (PLAN §3.3). SkinDocument types come from generated/skin-document.ts; the shapes below are the
// non-document results of the backend methods.
import type { SkinDocument as GenDoc } from './generated/skin-document';
import type { ForgeApi } from '../preload/preload';

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
}

export interface CatalogPart {
  id: string;
  role: string;
  label: string;
  materials: string[];
  defaultEnabled: boolean;
}

export type Slot = 'main' | 'special' | 'attachment' | 'ammo';

export interface CatalogItem {
  id: string;
  name: string;
  nameZh: string;
  category: string;
  categoryZh?: string | null;
  slot: Slot;
  archetypeId: number | null;
  calibre?: string | null;
  parts: CatalogPart[];
}

export interface WeaponPreview {
  glb: string;
  parts: { id: string; nodes: string[] }[];
  textures: Record<string, Partial<Record<TextureChannel, string>>>;
  params: Record<string, MaterialParams>;
}

export interface MatchCell {
  file: string;
  confidence: number;
  reason: string;
}

export interface MatchResult {
  parts: { partId: string; material: string; channels: Partial<Record<TextureChannel, MatchCell>> }[];
  unmatched: string[];
}

export interface MeshImport {
  sfmesh: string;
  glb: string;
  bounds: { min: number[]; max: number[] };
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

/** Methods, params and results. */
export interface Methods {
  'app.info': [Record<string, never>, { version: string; dataDir: string }];
  'game.locate': [Record<string, never>, GameInfo];
  'game.setDir': [{ dir: string }, GameInfo];
  'profiles.list': [Record<string, never>, Profile[]];
  'catalog.get': [Record<string, never>, { items: CatalogItem[] }];
  'preview.weapon': [{ id: string }, WeaponPreview];
  'match.folder': [{ weaponId: string; folder: string }, MatchResult];
  'match.files': [{ weaponId: string; files: string[] }, MatchResult];
  'project.new': [{ weaponId?: string }, SkinDocument];
  'project.load': [{ path: string }, SkinDocument];
  'project.save': [{ path: string; project: SkinDocument }, { path: string }];
  'mesh.import': [{ glb: string }, MeshImport];
  'pack.build': [{ project: SkinDocument; projectPath: string; out: string; sizeMode: SizeMode }, { pack: string; warnings: string[] }];
  'pack.install': [{ pack: string; profile: string; activate: boolean }, unknown];
  'pack.list': [{ profile: string }, InstalledPack[]];
  'pack.remove': [{ profile: string; skinId: string }, unknown];
  'pack.activate': [{ profile: string; skinId: string; active: boolean }, unknown];
  'plugin.install': [{ profile: string }, unknown];
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
