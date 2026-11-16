import { applyPatches, current, enablePatches, produceWithPatches, type Patch } from 'immer';
import { create } from 'zustand';
import { AudioError, transcodeToOgg } from './audio';
import {
  attachedOf,
  attachmentMaterial,
  baseName,
  call,
  dirName,
  fileUrl,
  type Attachment,
  type CatalogItem,
  type DynamicLayer,
  type EnemyInfo,
  type GameInfo,
  type InstalledPack,
  type MatchCell,
  type MatchResult,
  type MaterialOverride,
  type MaterialParams,
  type PartTransform,
  type Profile,
  type ProjectEntry,
  type SkinDocument,
  type SkinsNote,
  type SkinSound,
  type TextureChannel,
  type Trigger,
  type WeaponPreview,
} from './protocol';
import { detail, friendly } from './errors';
import { itemThumbnail } from './viewport/thumbnails';
import { CHARM_DEFAULTS } from './viewport/charm';
import { startPoint } from './viewport/refs';
import type { ShapeKind } from './shapes';
import type { HoloSet } from './holoSets';
import { showTab } from './ui/InspectorTabs';
import { syncPlayerColors } from './playerColors';
import { syncCodeEffects } from './codeFx';
import { notify, type NotifyAction, type NotifyKind } from './ui/notify';
import { localName, setLang, systemLang, t, type Lang } from './i18n';

enablePatches();

export type Selection =
  | { kind: 'part'; id: string; sub?: string | null }
  | { kind: 'attachment'; id: string }
  | { kind: 'layer'; material: string; id: string }
  | { kind: 'livery'; material: string; id: string }
  | { kind: 'sound'; id: string }
  | { kind: 'fx'; id: string }
  | null;

const selectionKey = (s: Selection) => JSON.stringify(s?.kind === 'part' ? { ...s, sub: s.sub ?? null } : s);
/** Whether two selections name the same thing (a part without a sub-part either way). */
export const sameSelection = (a: Selection, b: Selection) => selectionKey(a) === selectionKey(b);

export interface Prefs {
  ambient: number;
  ambientColor: string;
  autoRotate: boolean;
  /** While the game runs, apply to the test profile a moment after each change. */
  autoApply: boolean;
  bloom: boolean;
  allowMissing: boolean;
  profilePath: string | null;
  /** UI language; null follows the system. */
  lang: Lang | null;
}

/** Editor-only state persisted in the project's `editor` field. */
interface EditorState {
  enabled: Record<string, boolean>;
  pending: Record<string, MatchCell>; // `${material}|${channel}` -> low-confidence match awaiting confirmation
}

/** One undo step: immer patches forward and back. */
interface Step {
  redo: Patch[];
  undo: Patch[];
}

const PREFS_KEY = 'sf.prefs';
export const DEFAULT_PREFS: Prefs = {
  ambient: 1,
  ambientColor: '#ffffff',
  autoRotate: false,
  autoApply: false,
  bloom: true,
  allowMissing: true,
  profilePath: null,
  lang: null,
};

function loadPrefs(): Prefs {
  try {
    return { ...DEFAULT_PREFS, ...JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}') };
  } catch {
    return DEFAULT_PREFS;
  }
}

export const CONFIDENT = 0.8;
export const pendingKey = (material: string, channel: TextureChannel) => `${material}|${channel}`;

export function editorOf(doc: SkinDocument): EditorState {
  const e = (doc.editor ?? {}) as Partial<EditorState>;
  // The rest of the editor state (model previews, the open variant, ...) stays with it.
  return { ...e, enabled: e.enabled ?? {}, pending: e.pending ?? {} };
}

/** An attachment's name: what the author typed, else its default in the current language (an untouched one stores none). */
export function attachmentName(doc: SkinDocument, a: Attachment): string {
  if (a.name) return a.name;
  const shape = (doc.editor as { shapes?: Record<string, { kind: ShapeKind; set?: HoloSet }> } | null | undefined)?.shapes?.[a.id ?? ''];
  if (shape) return shape.set ? `${t(`holoSets.${shape.set}`)} · ${t(`shapes.${shape.kind}`)}` : t(`shapes.${shape.kind}`);
  const n = (doc.attachments ?? []).findIndex((x) => x?.id === a.id) + 1;
  return t(a.screen ? 'store.k21' : 'store.k22', { v0: n });
}

export function normalize(doc: SkinDocument): SkinDocument {
  doc.materials ??= [];
  doc.attachments ??= [];
  doc.parts ??= [];
  // Every vector the pack check reads (SkinPack.Validate `bad-vector`) gets its length and finite numbers back: an
  // effect colour saved with an alpha, a transform written missing or as NaN by an older build.
  const vec = <T extends number[] | null | undefined>(v: T, n: number, fallback: number[] | null) =>
    (Array.isArray(v) && v.length >= n && v.slice(0, n).every(Number.isFinite) ? v.slice(0, n) : fallback) as T;
  for (const a of doc.attachments) {
    if (!a) continue;
    a.position = vec(a.position, 3, [0, 0, 0]);
    a.rotation = vec(a.rotation, 3, [0, 0, 0]);
    a.scale = vec(a.scale, 3, [1, 1, 1]);
    for (const e of a.effects ?? []) if (e) e.color = vec(e.color, 3, [1, 1, 1]);
  }
  for (const p of doc.parts) {
    if (!p) continue;
    p.position = vec(p.position, 3, null);
    p.rotation = vec(p.rotation, 3, null);
    p.scale = vec(p.scale, 3, null);
  }
  for (const m of doc.materials) {
    if (!m?.params) continue;
    m.params.emissiveColor = vec(m.params.emissiveColor, 3, null);
    m.params.tint = vec(m.params.tint, 4, null);
  }
  doc.sounds ??= [];
  doc.target ??= {};
  // The skin id is made once with the project and kept, so applying again overwrites the same pack in game.
  doc.id ||= newSkinId(doc);
  doc.editor = { ...editorOf(doc) } as unknown as SkinDocument['editor'];
  return doc;
}

// A hole in an array (an undefined from a half-written vector) would save as null and fail the same way.
export const allFinite = (x: unknown): boolean =>
  typeof x === 'number'
    ? Number.isFinite(x)
    : Array.isArray(x)
      ? Array.from(x).every((v) => v !== undefined && allFinite(v))
      : x !== null && typeof x === 'object'
        ? Object.values(x).every(allFinite)
        : true;
// A value an edit writes; written into an array it must be there (an unset index is the hole above).
const writesFinite = (p: Patch): boolean => p.op === 'remove' || ((typeof p.path.at(-1) !== 'number' || p.value !== undefined) && allFinite(p.value));

/** Layout entry of a part root (`path` null) or of the child at `path`. */
export function partTransformOf(doc: SkinDocument, part: string, path?: string | null): PartTransform | undefined {
  return doc.parts?.find((t) => t?.part === part && (t.path ?? null) === (path ?? null)) ?? undefined;
}

/** What a layer of `material` belongs to, for going back from it: the attachment whose model it paints, else its part. */
export function ownerOf(doc: SkinDocument, material: string): Selection {
  const attached = attachedOf(material);
  return attached ? { kind: 'attachment', id: attached } : { kind: 'part', id: materialOf(doc, material)?.part ?? '' };
}

export function materialOf(doc: SkinDocument, material: string): MaterialOverride | undefined {
  return doc.materials?.find((m) => m?.material === material) ?? undefined;
}

/**
 * A material's glow as the game shows it, in the skin's units (1 = the colour as itself): the skin's colour times its
 * intensity, or without a colour the game material's own glow (the backend gives it in these units) times the
 * intensity, as the plugin applies it (SkinnedGear.Params).
 */
export function glowOf(game: MaterialParams | undefined, own: MaterialParams): { color: number[]; intensity: number } {
  if (own.emissiveColor) return { color: own.emissiveColor, intensity: own.emissiveIntensity ?? 1 };
  return { color: game?.emissiveColor ?? [1, 1, 1], intensity: (game?.emissiveIntensity ?? 1) * (own.emissiveIntensity ?? 1) };
}

export function ensureMaterial(doc: SkinDocument, material: string, part?: string): MaterialOverride {
  let m = materialOf(doc, material);
  if (!m) {
    m = { material, part: part ?? null, textures: {}, params: {}, layers: [] };
    doc.materials!.push(m);
  }
  m.textures ??= {};
  m.params ??= {};
  m.layers ??= [];
  return m;
}

export const uid = () => Math.random().toString(36).slice(2, 10);
const newSkinId = (doc: SkinDocument) => `${doc.target?.weapon ?? 'skin'}-${uid()}`;

/** One entry of the log panel: the same text again counts up instead of adding a line. */
export interface LogLine {
  time: string;
  text: string;
  count: number;
}
export const logText = (l: LogLine) => `[${l.time}] ${l.text}${l.count > 1 ? ` ×${l.count}` : ''}`;

interface State {
  ready: boolean;
  backendDown: boolean;
  dataDir: string;
  game: GameInfo | null;
  profiles: Profile[];
  catalog: CatalogItem[];
  /** Every enemy of the installed game (its enemy data blocks), for the kill sounds' enemy choice. */
  enemies: EnemyInfo[];
  weaponId: string | null;
  preview: WeaponPreview | null;
  previewLoading: boolean;
  selection: Selection;
  /** What the pointer is over: a row of the parts tree, or a part or attachment in the 3D view; both show it. */
  hover: Selection;
  doc: SkinDocument;
  projectPath: string | null;
  undo: Step[];
  redo: Step[];
  lastEditKey: string | null;
  lastEditAt: number;
  prefs: Prefs;
  logs: LogLine[];
  progress: { text: string; done: number; total: number } | null;
  /** A new version downloading in the background (total 0 when its size is unknown). */
  update: { received: number; total: number } | null;
  /** A new version is downloaded; restarting installs it. */
  updateReady: boolean;
  /** Bumped when a file the skin uses is written outside the editor (the 3D view repaints, 自动应用 applies again). */
  fileEpoch: number;
  /** The skin's own pictures the 3D view could not read (marked on their slots); one leaves when its file changes. */
  unreadable: string[];
  /** Video memory the last built pack of a skin takes in game (pack.build's estimate). */
  skinMemory: { id: string; bytes: number } | null;
  /** Plain message when the last autosave failed; null when it worked. */
  autosaveError: string | null;
  /** Files the last texture match could not place, shown in the 换皮 overview. */
  unmatched: string[];
  busy: string | null;
  snapToSurface: boolean;
  /** Sub-transforms of each part from the preview model, in tree order. */
  /** Child transforms of each part that carry geometry: Transform.Find path under the part root, display name, depth. */
  subParts: Record<string, { path: string; name: string; depth: number }[]>;

  init(): Promise<void>;
  loadAll(): Promise<void>;
  log(line: string): void;
  toast(kind: NotifyKind, text: string, action?: NotifyAction): void;
  fail(e: unknown, fallback?: string): void;
  setPrefs(p: Partial<Prefs>): void;
  select(s: Selection): void;
  setHover(h: Selection): void;
  refreshGame(): Promise<void>;
  chooseGameDir(): Promise<void>;
  refreshProfiles(): Promise<void>;
  /** Profile whose plugin update waits for the game to close. */
  pluginPending: string | null;
  selectWeapon(id: string): Promise<void>;
  /** The Thunderstore export dialog is open. */
  exportModOpen: boolean;
  /** The 导出前体检 dialog is open. */
  checkOpen: boolean;
  /** What a Skins import or export left out or changed, shown in ui/SkinsReport; null when closed. */
  skinsReport: { title: string; notes: SkinsNote[]; folder?: string; weapons?: string[] } | null;
  /** A Skins skin group that names no gear the editor knows, waiting for the author to pick one (ui/SkinsReport). */
  skinsTarget: { manifest: string; notes: SkinsNote[] } | null;
  /** The sticker / decal image picker is open: it answers here (stickers.pickDecalImage). */
  stickerPick: ((file: string | null) => void) | null;
  /** The tutorial chapter being walked through (tour/), null when none is open. */
  tour: string | null;
  /** The current item's skins (each its own autosaved project), newest first. */
  skins: ProjectEntry[];
  /** Every item's skins by item id (the work list), and the character skins (character/model.ts refreshCharacters). */
  works: Record<string, ProjectEntry[]>;
  characters: ProjectEntry[];
  /** When the open skin was last changed in this session (ms; 0: not since it opened); saved as editor.editedAt. */
  editedAt: number;
  refreshWorks(): Promise<void>;
  /** Opens a skin of any item. */
  openWork(weaponId: string, skinId: string): Promise<void>;
  /** A new blank skin for an item (an item with no skins yet gets its first one). */
  newWork(weaponId: string): Promise<void>;
  /** Skins installed in the chosen profile. */
  installed: InstalledPack[];
  refreshSkins(): Promise<ProjectEntry[]>;
  refreshInstalled(): Promise<void>;
  switchSkin(id: string): Promise<void>;
  /** A new skin for the current item: blank, or a copy of the one being edited. */
  newSkin(copy: boolean): Promise<void>;
  /** Moves a skin's project (of the open item, or of `weaponId`) to the Recycle Bin and takes it out of the chosen profile. */
  deleteSkin(id: string, weaponId?: string): Promise<void>;
  edit(fn: (d: SkinDocument) => void, coalesceKey?: string): void;
  undoEdit(): void;
  redoEdit(): void;
  newProject(): Promise<void>;
  loadProject(path: string): Promise<void>;
  saveProject(saveAs?: boolean): Promise<void>;
  autosave(): void;

  // skin
  applyMatch(r: MatchResult): void;
  matchPaths(paths: string[]): Promise<void>;
  setTexture(material: string, part: string | undefined, channel: TextureChannel, path: string | null): void;
  confirmPending(material: string, channel: TextureChannel): void;
  confirmAllPending(): void;
  setParams(material: string, p: Partial<MaterialParams>, coalesceKey?: string): void;
  copyEmissiveToAll(fromMaterial: string): void;
  setPartEnabled(partId: string, on: boolean): void;

  // attachments
  /** `init` finishes the new attachment in the same undo step (a shape's name, look and parameters). */
  addAttachment(kind: 'screen' | 'mesh', mesh?: { forgeskinmesh: string; glb: string }, init?: (a: Attachment, editor: Record<string, unknown>) => void, id?: string): void;
  updateAttachment(id: string, fn: (a: Attachment) => void, coalesceKey?: string): void;
  removeAttachment(id: string): void;
  /** Converts the picked audio file to Ogg next to the project and adds it as a sound of the event (and, for a kill, the
   *  enemy: its data block id), joining that event's group; the new sound opens (one undo step). */
  addSound(file: string, trigger?: Trigger, enemyId?: number | null): Promise<void>;
  /** Moves a sound to another event (and enemy): an enemy stays only on a kill, and a low-ammo sound takes the
   *  threshold its new group already has. */
  setSoundEvent(id: string, trigger: Trigger, enemyId?: number | null): void;
  updateSound(id: string, fn: (s: SkinSound) => void, coalesceKey?: string): void;
  removeSound(id: string): void;
  duplicateAttachment(id: string): void;

  // layers
  addLayer(material: string, part: string, layer: Partial<DynamicLayer>): void;
  updateLayer(material: string, id: string, fn: (l: DynamicLayer) => void, coalesceKey?: string): void;
  removeLayer(material: string, id: string): void;

  // part layout
  setPartTransform(part: string, path: string | null | undefined, fn: (t: PartTransform) => void, coalesceKey?: string): void;
  resetPartTransform(part: string, path: string | null | undefined, row?: 'position' | 'rotation' | 'scale'): void;
  /** Replaces a node's own model (null puts the game's back); the 3D view shows `glb` in its place. */
  setPartMesh(part: string, path: string | null | undefined, mesh: { forgeskinmesh: string; glb: string } | null): void;
}

let initialised = false;
/** The init run in flight: StrictMode's double effect and the backend-ready retry share it instead of loading twice. */
let initRun: Promise<void> | undefined;

type Set = (p: Partial<State>) => void;
function subscribe(f: Window['forge'], set: Set, get: () => State) {
  f.onLog((line) => get().log(line));
  f.onUpdateProgress((received, total) => {
    if (!get().update) get().toast('info', t('topBar.update.downloading'));
    set({ update: received < 0 || (total > 0 && received >= total) ? null : { received, total } });
  });
  f.onUpdateReady(() => {
    set({ update: null, updateReady: true });
    get().toast('ok', t('app.updateReady'), { label: t('app.restartUpdate'), run: () => void f.restartToUpdate() });
  });
  f.onEvent((event, data) => {
    if (event === 'progress') {
      const p = data as { done: number; total: number; text: string };
      set({ progress: p.done >= p.total ? null : p });
    }
  });
  f.onStatus((s) => {
    set({ backendDown: s !== 'ready' });
    // The backend came back after a crash before anything loaded: load again.
    if (s === 'ready' && !get().catalog.length) setTimeout(() => void get().init(), 500);
    if (s === 'down') get().toast('error', t('store.k1'));
  });
}
let autosaveTimer: ReturnType<typeof setTimeout> | undefined;
/** What a data folder move reported (actions.moveDataFolder), shown once the reloaded window starts. */
export const DATA_NOTICE = 'sf.dataNotice';

export const useStore = create<State>((set, get) => ({
  ready: false,
  backendDown: false,
  dataDir: '',
  game: null,
  profiles: [],
  catalog: [],
  enemies: [],
  weaponId: null,
  preview: null,
  previewLoading: false,
  selection: null,
  hover: null,
  doc: normalize({ name: '', materials: [], attachments: [] }),
  projectPath: null,
  undo: [],
  redo: [],
  lastEditKey: null,
  lastEditAt: 0,
  prefs: loadPrefs(),
  pluginPending: null,
  exportModOpen: false,
  checkOpen: false,
  skinsReport: null,
  skinsTarget: null,
  stickerPick: null,
  tour: null,
  skins: [],
  works: {},
  characters: [],
  editedAt: 0,
  installed: [],
  logs: [],
  progress: null,
  update: null,
  updateReady: false,
  fileEpoch: 0,
  unreadable: [],
  skinMemory: null,
  autosaveError: null,
  unmatched: [],
  busy: null,
  snapToSurface: false,
  subParts: {},

  init() {
    initRun ??= get()
      .loadAll()
      .finally(() => (initRun = undefined));
    return initRun;
  },
  async loadAll() {
    const f = window.forge;
    if (!initialised) {
      initialised = true;
      subscribe(f, set, get);
    }
    set({ dataDir: await f.dataDir() });
    const notice = sessionStorage.getItem(DATA_NOTICE);
    if (notice) {
      sessionStorage.removeItem(DATA_NOTICE);
      get().toast('ok', notice);
    }
    await get().refreshGame();
    try {
      const catalog = await call('catalog.get', {});
      set({ catalog: catalog.items });
    } catch (e) {
      get().log(`catalog.get: ${detail(e)}`);
    }
    await loadEnemies();
    await get().refreshProfiles();
    const last = localStorage.getItem('sf.weapon');
    await get().refreshWorks();
    // A practice work left by a run that ended inside the tutorial.
    await removePractice();
    set({ ready: true });
    if (get().weaponId) return;
    // The gear edited last, else the newest skin of any gear; with no skin at all the window offers to make one.
    if (last && get().works[last] && get().catalog.some((c) => c.id === last)) return get().selectWeapon(last);
    const newest = Object.entries(get().works)
      .flatMap(([w, list]) => list.map((k) => ({ w, k })))
      .sort((a, b) => b.k.modified.localeCompare(a.k.modified))[0];
    if (newest) await get().openWork(newest.w, newest.k.id);
  },
  log(line) {
    const time = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    // A line already in the panel is one entry: it moves to the end with the latest time and how often it came.
    set((s) => {
      const old = s.logs.find((l) => l.text === line);
      return { logs: [...s.logs.filter((l) => l !== old).slice(-499), { time, text: line, count: (old?.count ?? 0) + 1 }] };
    });
  },

  toast(kind, text, action) {
    notify(kind, text, action);
  },
  // The log panel keeps the plain words; the raw error (code and backend text) goes only to the log file.
  fail(e, fallback) {
    const text = friendly(e, fallback);
    window.forge.appLog('error', detail(e));
    get().log(text);
    get().toast('error', text);
  },

  setPrefs(p) {
    const prefs = { ...get().prefs, ...p };
    localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    set({ prefs });
    setLang(prefs.lang ?? systemLang());
  },
  setHover(hover) {
    if (!sameSelection(hover, get().hover)) set({ hover });
  },
  select(selection) {
    set({ selection });
  },

  async refreshGame() {
    try {
      const game = await call('game.locate', {});
      set({ game });
      if (game.found) get().log(t('store.k3', { v0: game.gameDir }));
    } catch (e) {
      get().log(`game.locate: ${detail(e)}`);
      set({ game: { found: false, running: false, gameDir: null, dataDir: null } });
    }
  },
  async chooseGameDir() {
    const [dir] = await window.forge.openDialog('folder', undefined, 'game');
    if (!dir) return;
    try {
      const game = await call('game.setDir', { dir });
      set({ game });
      if (!game.found) get().toast('error', t('store.k4'));
      else {
        get().toast('ok', t('store.k5'));
        set({ catalog: (await call('catalog.get', {})).items });
        await loadEnemies();
        await get().refreshProfiles();
      }
    } catch (e) {
      get().fail(e);
    }
  },
  async refreshProfiles() {
    try {
      const profiles = await call('profiles.list', {});
      const before = get().prefs.profilePath;
      set({ profiles });
      // The last one used while it still has BepInEx; else the most recently changed one with
      // BepInEx; else none (the top bar then says what to do).
      const last = profiles.find((p) => p.path === get().prefs.profilePath && p.hasBepInEx);
      const ready = profiles.filter((p) => p.hasBepInEx).sort((a, b) => (b.modified ?? '').localeCompare(a.modified ?? ''));
      const pick = last ?? ready[0];
      get().setPrefs({ profilePath: pick?.path ?? null });
      if (before && !profiles.some((p) => p.path === before)) get().toast('info', t('store.profileGone', { v0: before }));
    } catch (e) {
      get().log(`profiles.list: ${detail(e)}`);
    }
  },

  async selectWeapon(id) {
    const s = get();
    if (s.weaponId === id) return;
    const ticket = ++opening;
    if (s.weaponId) await persistAutosave();
    if (ticket !== opening) return;
    localStorage.setItem('sf.weapon', id);
    set({ weaponId: id, preview: null, previewLoading: true, selection: null, undo: [], redo: [], unmatched: [] });
    // Each weapon keeps its skins as autosaved projects; open the one edited last, else the newest, else a new one.
    const skins = await get().refreshSkins();
    const pick = skins.find((k) => k.id === localStorage.getItem(`sf.skin.${id}`)) ?? skins[0];
    const doc =
      (pick && (await loadSkin(pick.path))) ||
      (await blankSkin(
        id,
        skins.map((k) => k.name),
      ));
    if (!openSkin(doc, id, ticket)) return;
    if (!pick) await persistAutosave();
    try {
      const preview = await call('preview.weapon', { id });
      if (get().weaponId === id) set({ preview, previewLoading: false, progress: null });
      void itemThumbnail(id, preview);
      if (preview.damaged) {
        const item = get().catalog.find((c) => c.id === id);
        get().toast('info', t('store.damagedTextures', { v0: item ? localName(item) : id }));
      }
      if (preview.modified) get().toast('error', t('store.modifiedGame'));
    } catch (e) {
      if (get().weaponId === id) set({ previewLoading: false, progress: null });
      get().fail(e, t('store.k7'));
    }
  },

  async refreshSkins() {
    const id = get().weaponId;
    if (!id) return [];
    const skins: ProjectEntry[] = await call('project.list', { dir: skinDir(id) }).catch((e) => (get().log(`project.list: ${detail(e)}`), []));
    const works = { ...get().works, [id]: skins };
    if (!skins.length) delete works[id];
    set({ works });
    if (get().weaponId === id) set({ skins });
    return skins;
  },
  async refreshWorks() {
    const all = await call('project.listAll', { root: autosaveRoot() }).catch((e) => (get().log(`project.listAll: ${detail(e)}`), {}));
    // The character skins have their own folder there.
    const works = { ...(all as Record<string, ProjectEntry[]>) };
    delete works.character;
    set({ works });
  },
  async openWork(weaponId, skinId) {
    if (get().weaponId !== weaponId) {
      localStorage.setItem(`sf.skin.${weaponId}`, skinId);
      return get().selectWeapon(weaponId);
    }
    await get().switchSkin(skinId);
  },
  async newWork(weaponId) {
    const had = !!get().works[weaponId]?.length;
    await get().selectWeapon(weaponId);
    if (had) await get().newSkin(false);
  },
  async refreshInstalled() {
    const s = get();
    const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
    // Copies of the same skin go to the Recycle Bin, said in the log.
    const installed = profile?.hasBepInEx ? await call('pack.list', { profile: profile.path, tidy: true }).catch((e) => (s.log(`pack.list: ${detail(e)}`), [])) : [];
    for (const p of installed) if (p.dropped?.length) s.log(t('gameLog.dropped', { v0: p.name, v1: p.dropped.join('、') }));
    // A profile chosen meanwhile has its own read under way.
    if (get().prefs.profilePath === s.prefs.profilePath) set({ installed });
  },
  async switchSkin(id) {
    const entry = get().skins.find((k) => k.id === id);
    const weaponId = get().weaponId;
    if (!entry || !weaponId || get().doc.id === id) return;
    const ticket = ++opening;
    await persistAutosave();
    const doc = await loadSkin(entry.path);
    if (doc) openSkin(doc, weaponId, ticket);
  },
  async newSkin(copy) {
    const s = get();
    if (!s.weaponId) return;
    const ticket = ++opening;
    await persistAutosave();
    let doc: SkinDocument;
    if (copy) {
      doc = structuredClone(s.doc);
      doc.id = newSkinId(doc);
      doc.name = t('store.skinCopy', { v0: s.doc.name });
    } else
      doc = await blankSkin(
        s.weaponId,
        s.skins.map((k) => k.name),
      );
    if (openSkin(doc, s.weaponId, ticket)) await persistAutosave();
  },
  async deleteSkin(id, weaponId = get().weaponId ?? '') {
    const s = get();
    const entry = (weaponId === s.weaponId ? s.skins : (s.works[weaponId] ?? [])).find((k) => k.id === id);
    const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
    if (!entry) return;
    const rewatch = () =>
      void call('project.files', { project: get().doc }).then(
        ({ files }) => window.forge.watchFiles(files),
        () => undefined,
      );
    // The editor's own watch on the open work's folders would keep them from moving to the Recycle Bin: it is let go, and not
    // taken again by a change meanwhile (useWatchedFiles), until the delete is over.
    const remove = async () => {
      watchHeld = true;
      try {
        await window.forge.watchFiles([]);
        await call('project.delete', { path: entry.path });
        if (profile && s.installed.some((p) => p.id === id)) await call('pack.remove', { profile: profile.path, skinId: id });
      } finally {
        watchHeld = false;
      }
    };
    if (weaponId !== s.weaponId) {
      try {
        await remove();
      } catch (e) {
        rewatch();
        return get().fail(e, t('store.skinDeleteFailed'));
      }
      rewatch();
      await get().refreshWorks();
      return void get().refreshInstalled();
    }
    if (!s.weaponId) return;
    // The edited skin must not be written back by a pending autosave.
    if (s.doc.id === id) clearTimeout(autosaveTimer);
    const ticket = ++opening;
    try {
      await remove();
    } catch (e) {
      rewatch();
      return get().fail(e, t('store.skinDeleteFailed'));
    }
    const rest = await get().refreshSkins();
    void get().refreshInstalled();
    if (get().doc.id !== id) return rewatch();
    // The last skin of this gear gone: another gear's newest skin opens, else a new one here.
    const other =
      !rest.length &&
      Object.entries(get().works)
        .flatMap(([w, list]) => list.map((k) => ({ w, k })))
        .sort((a, b) => b.k.modified.localeCompare(a.k.modified))[0];
    // No gear open while it loads, so the deleted skin is not saved back on the way.
    if (other) return void (set({ weaponId: null }), await get().openWork(other.w, other.k.id));
    const next = (rest[0] && (await loadSkin(rest[0].path))) || (await blankSkin(s.weaponId, []));
    if (openSkin(next, s.weaponId, ticket) && !rest.length) await persistAutosave();
  },

  edit(fn, coalesceKey) {
    const s = get();
    const [doc, redo, undo] = produceWithPatches(s.doc, (d) => {
      fn(d as SkinDocument);
      syncPlayerColors(d as SkinDocument); // linked colours move together (playerColors.ts)
      syncCodeEffects(d as SkinDocument); // code effects follow their material or attachment (codeFx.ts)
    });
    if (!redo.length) return;
    // JSON has no NaN/Infinity: such a value would make the whole document fail to save and pack, so the edit is refused.
    // Only what the edit wrote is checked (a brush stroke's move adds a few numbers to a long list).
    if (!redo.every(writesFinite)) {
      s.log(t('store.refusedEdit'));
      s.toast('error', t('store.refusedEdit'));
      window.forge.appLog('refused edit', `${JSON.stringify(redo)}\n${new Error().stack}`);
      return;
    }
    const now = Date.now();
    const last = s.undo.at(-1);
    // Same key merges: a gesture key (`gesture:<id>`) for its whole drag, other keys only within 0.8 s.
    const merge = last && coalesceKey && coalesceKey === s.lastEditKey && (coalesceKey.startsWith('gesture:') || now - s.lastEditAt < 800);
    set({
      doc,
      undo: merge ? [...s.undo.slice(0, -1), { redo: [...last.redo, ...redo], undo: [...undo, ...last.undo] }] : [...s.undo.slice(-199), { redo, undo }],
      redo: [],
      lastEditKey: coalesceKey ?? null,
      lastEditAt: now,
      editedAt: now,
    });
    get().autosave();
  },
  undoEdit() {
    const s = get();
    const step = s.undo.at(-1);
    if (!step) return;
    set({ doc: applyPatches(s.doc, keepId(step.undo)), undo: s.undo.slice(0, -1), redo: [...s.redo, step], lastEditKey: null, editedAt: Date.now() });
    get().autosave();
  },
  redoEdit() {
    const s = get();
    const step = s.redo.at(-1);
    if (!step) return;
    set({ doc: applyPatches(s.doc, keepId(step.redo)), redo: s.redo.slice(0, -1), undo: [...s.undo, step], lastEditKey: null, editedAt: Date.now() });
    get().autosave();
  },

  async newProject() {
    const id = get().weaponId;
    if (!id) return;
    get().edit((d) => {
      d.materials = [];
      d.attachments = [];
      d.editor = { ...editorOf(d), pending: {} } as unknown as SkinDocument['editor'];
    });
    set({ projectPath: null, selection: null });
    get().toast('info', t('store.k8'));
  },
  async loadProject(path) {
    try {
      const doc = normalize(await call('project.load', { path }));
      // An old preset names no item: it goes on the one being edited, so it autosaves there.
      const weapon = doc.target!.weapon || get().weaponId;
      if (!weapon) return get().toast('info', t('actions.k2'));
      doc.target!.weapon = weapon;
      if (weapon !== get().weaponId) {
        await persistAutosave();
        set({ weaponId: null });
        await get().selectWeapon(weapon);
        if (get().weaponId !== weapon) return;
      }
      ++opening; // a skin still loading for this item must not replace the opened project
      set({ doc, projectPath: path.endsWith('.forgeskinproj') ? path : null, selection: null, unmatched: [], undo: [], redo: [], lastEditKey: null });
      void restorePreviews(doc);
      get().autosave();
      get().toast('ok', t('store.k9', { v0: doc.name }));
    } catch (e) {
      get().fail(e, t('store.k10'));
    }
  },
  async saveProject(saveAs) {
    let path = get().projectPath;
    if (!path || saveAs) {
      path = await window.forge.saveDialog(`${get().doc.name || 'skin'}.forgeskinproj`, [{ name: t('store.k11'), extensions: ['forgeskinproj'] }]);
      if (!path) return;
    }
    // Saving as a new skin is the one way to get a new skin id (a second pack in game instead of overwriting).
    // Not an edit: undo never changes which skin this is.
    if (saveAs) {
      set({ doc: { ...get().doc, id: newSkinId(get().doc) } });
      get().autosave();
    }
    try {
      await call('project.save', { path, project: get().doc });
      set({ projectPath: path });
      get().toast('ok', t('store.k12'));
    } catch (e) {
      get().fail(e, t('store.k13'));
    }
  },
  autosave() {
    clearTimeout(autosaveTimer);
    autosaveTimer = setTimeout(() => void persistAutosave(), 1500);
  },

  applyMatch(r) {
    const item = get().catalog.find((c) => c.id === get().weaponId);
    let auto = 0;
    let unsure = 0;
    get().edit((d) => {
      const ed = editorOf(d);
      for (const p of r.parts) {
        // Slots the folder had no image for come back with no file; they keep what they have.
        const found = (Object.entries(p.channels) as [TextureChannel, MatchCell][]).filter(([, c]) => c?.file);
        if (!found.length) continue;
        const m = ensureMaterial(d, p.material, p.partId);
        for (const [ch, cell] of found) {
          m.textures![ch] = cell.file;
          const key = pendingKey(p.material, ch);
          if (cell.confidence < CONFIDENT) {
            ed.pending[key] = cell;
            unsure++;
          } else {
            delete ed.pending[key];
            auto++;
          }
          if (ch === 'emissive') m.params!.emissiveEnabled ??= true;
        }
      }
      d.editor = ed as unknown as SkinDocument['editor'];
    });
    const parts = new Set(r.parts.filter((p) => Object.values(p.channels).some((c) => c?.file)).map((p) => p.partId)).size;
    const total = item?.parts.length ?? parts;
    let text = t('store.k14', { v0: auto + unsure, v1: parts, v2: total });
    if (unsure) text += t('store.k15', { v0: unsure });
    if (r.unmatched.length) text += t('store.k16', { v0: r.unmatched.length });
    get().toast(unsure ? 'info' : 'ok', text);
    set({ unmatched: r.unmatched });
  },
  async matchPaths(paths) {
    const weaponId = get().weaponId;
    if (!weaponId) {
      get().toast('info', t('store.k17'));
      return;
    }
    set({ busy: t('store.k18') });
    try {
      const single = paths.length === 1 && !/\.(png|jpe?g|tga|tiff?|bmp|exr|webp)$/i.test(paths[0]);
      const r = single ? await call('match.folder', { weaponId, folder: paths[0] }) : await call('match.files', { weaponId, files: paths });
      get().applyMatch(r);
    } catch (e) {
      get().fail(e, t('store.k19'));
    } finally {
      set({ busy: null });
    }
  },
  setTexture(material, part, channel, path) {
    get().edit((d) => {
      const m = ensureMaterial(d, material, part);
      if (path) m.textures![channel] = path;
      else delete m.textures![channel];
      if (channel === 'emissive' && path) m.params!.emissiveEnabled ??= true;
      const ed = editorOf(d);
      delete ed.pending[pendingKey(material, channel)];
      d.editor = ed as unknown as SkinDocument['editor'];
    });
  },
  confirmPending(material, channel) {
    get().edit((d) => {
      const ed = editorOf(d);
      delete ed.pending[pendingKey(material, channel)];
      d.editor = ed as unknown as SkinDocument['editor'];
    });
  },
  confirmAllPending() {
    get().edit((d) => {
      d.editor = { ...editorOf(d), pending: {} } as unknown as SkinDocument['editor'];
    });
  },
  setParams(material, p, coalesceKey) {
    get().edit((d) => {
      const m = ensureMaterial(d, material);
      Object.assign(m.params!, p);
    }, coalesceKey);
  },
  copyEmissiveToAll(fromMaterial) {
    const item = get().catalog.find((c) => c.id === get().weaponId);
    get().edit((d) => {
      const src = ensureMaterial(d, fromMaterial);
      for (const part of item?.parts ?? [])
        for (const mat of part.materials) {
          if (mat === fromMaterial) continue;
          const m = ensureMaterial(d, mat, part.id);
          m.params!.emissiveEnabled = src.params!.emissiveEnabled;
          m.params!.emissiveColor = src.params!.emissiveColor;
          m.params!.emissiveIntensity = src.params!.emissiveIntensity;
          if (src.textures!.emissive && !m.textures!.emissive) m.textures!.emissive = src.textures!.emissive;
        }
    });
    get().toast('ok', t('store.k20'));
  },
  setPartEnabled(partId, on) {
    get().edit((d) => {
      const ed = editorOf(d);
      ed.enabled[partId] = on;
      d.editor = ed as unknown as SkinDocument['editor'];
    });
  },

  addAttachment(kind, mesh, init, givenId) {
    const item = get().catalog.find((c) => c.id === get().weaponId);
    const sel = get().selection;
    const anchor = sel?.kind === 'part' ? sel.id : (item?.parts[0]?.id ?? '');
    const transform = sel?.kind === 'part' ? (sel.sub ?? null) : null;
    const id = givenId ?? uid();
    get().edit((d) => {
      d.attachments!.push({
        id,
        visible: true,
        part: anchor,
        transform,
        position: startPoint(anchor, transform, false) ?? [0, 0.03, 0],
        rotation: [0, 0, 0],
        scale: [1, 1, 1],
        faceCamera: false,
        mesh: mesh?.forgeskinmesh ?? null,
        screen:
          kind === 'screen'
            ? {
                source: 'ammoClip',
                digits: 0, // automatic
                padZero: true,
                size: [0.03, 0.015],
                surfaceGap: 0.0005,
                panelColor: [0, 0, 0, 0.85],
                borderColor: [0.73, 0.98, 1, 1],
                borderWidth: 0.0008,
                textColor: [0.73, 0.98, 1, 1],
                textScale: 0.8,
                glow: 1.5,
                glowInGame: true,
                lowAmmoFraction: 0.2,
                lowAmmoColor: [1, 0.35, 0.25, 1],
              }
            : null,
        material: mesh ? { textures: {}, color: [1, 1, 1, 1], unlit: false, params: {} } : null,
      });
      if (mesh) {
        const ed = d.editor as Record<string, unknown>;
        ed.meshPreviews = { ...((ed.meshPreviews as Record<string, string>) ?? {}), [id]: mesh.glb };
      }
      init?.(d.attachments!.at(-1)!, d.editor as Record<string, unknown>);
    });
    set({ selection: { kind: 'attachment', id } });
  },
  updateAttachment(id, fn, coalesceKey) {
    get().edit((d) => {
      const a = d.attachments!.find((x) => x?.id === id);
      if (a) fn(a);
    }, coalesceKey);
  },
  removeAttachment(id) {
    get().edit((d) => {
      d.attachments = d.attachments!.filter((x) => x?.id !== id);
      d.materials = d.materials!.filter((m) => m?.material !== attachmentMaterial(id)); // the paint on its model
    });
    set({ selection: null });
  },
  async addSound(file, trigger = 'kill', enemyId = null) {
    const s = get();
    const id = uid();
    set({ busy: t('store.k23') });
    try {
      const ogg = await transcodeToOgg(await (await fetch(fileUrl(file))).arrayBuffer());
      const out = `${dirName(s.projectPath ?? autosavePath(s.weaponId!))}\\sounds\\${id}.ogg`;
      await window.forge.writeFile(out, ogg);
      get().edit((d) => {
        const low = d.sounds?.find((v) => v?.trigger === 'lowAmmo')?.lowAmmoFraction ?? 0.2;
        const sound = { id, name: baseName(file).replace(/\.[^.]+$/, ''), trigger, file: out, volume: 1, lowAmmoFraction: low, weight: 1 };
        d.sounds = [...(d.sounds ?? []), trigger === 'kill' && enemyId != null ? { ...sound, enemyId } : sound];
      });
      get().select({ kind: 'sound', id });
    } catch (e) {
      if (e instanceof AudioError) get().toast('error', e.message);
      else get().fail(e, t('store.k24'));
    } finally {
      set({ busy: null });
    }
  },
  updateSound(id, fn, coalesceKey) {
    get().edit(
      (d) => {
        const x = d.sounds?.find((v) => v?.id === id);
        if (x) fn(x);
      },
      coalesceKey && `sound-${id}-${coalesceKey}`,
    );
  },
  setSoundEvent(id, trigger, enemyId) {
    get().edit((d) => {
      const x = d.sounds?.find((v) => v?.id === id);
      if (!x) return;
      x.trigger = trigger;
      if (trigger === 'kill' && enemyId != null) x.enemyId = enemyId;
      else delete x.enemyId;
      const low = d.sounds?.find((v) => v !== x && v?.trigger === 'lowAmmo')?.lowAmmoFraction;
      if (trigger === 'lowAmmo' && low != null) x.lowAmmoFraction = low;
    });
  },
  removeSound(id) {
    get().edit((d) => void (d.sounds = (d.sounds ?? []).filter((v) => v?.id !== id)));
  },
  duplicateAttachment(id) {
    const copy = uid();
    get().edit((d) => {
      const a = d.attachments!.find((x) => x?.id === id);
      if (!a) return;
      d.attachments!.push({ ...structuredClone(current(a)), id: copy, ...(a.name ? { name: t('store.k25', { v0: a.name }) } : {}) });
      const ed = d.editor as Record<string, Record<string, unknown> | undefined>;
      // The copy keeps the model, its preview and, for a built-in shape, its parameters (shapes.ts rebuilds its own model on the next change).
      for (const k of ['meshPreviews', 'shapes']) if (ed[k]?.[id]) ed[k]![copy] = ed[k]![id];
      // The paint on its model comes along, every layer under a new id (layers bake into folders named by id).
      const paint = materialOf(d, attachmentMaterial(id));
      if (paint) {
        const p = structuredClone(current(paint));
        p.livery = p.livery?.map((l) => l && { ...l, id: uid() });
        p.layers = p.layers?.map((l) => l && { ...l, id: uid() });
        d.materials!.push({ ...p, material: attachmentMaterial(copy), part: attachmentMaterial(copy) });
      }
    });
    set({ selection: { kind: 'attachment', id: copy } });
  },

  addLayer(material, part, layer) {
    const id = uid();
    get().edit((d) => {
      const m = ensureMaterial(d, material, part);
      m.layers!.push({
        id,
        name: layer.name ?? '',
        kind: layer.kind ?? 'image',
        channel: 'albedo',
        visible: true,
        sources: layer.sources ?? [],
        text: layer.text ?? null,
        fps: 12,
        trigger: 'always',
        play: 'loop',
        opacity: 1,
        crop: [0, 0, 1, 1],
        uvCenter: [0.5, 0.5],
        uvSize: [0.25, 0.25],
        rotation: 0,
        ...layer,
      });
    });
    set({ selection: { kind: 'layer', material, id } });
  },
  updateLayer(material, id, fn, coalesceKey) {
    get().edit((d) => {
      const l = materialOf(d, material)?.layers?.find((x) => x?.id === id);
      if (l) fn(l);
    }, coalesceKey);
  },
  removeLayer(material, id) {
    get().edit((d) => {
      const m = materialOf(d, material);
      if (m) m.layers = m.layers!.filter((x) => x?.id !== id);
    });
    set({ selection: null });
  },

  setPartTransform(part, path, fn, coalesceKey) {
    get().edit((d) => {
      let t = partTransformOf(d, part, path);
      if (!t) {
        t = { part, path: path ?? null, position: null, rotation: null, scale: null, visible: null };
        d.parts!.push(t);
      }
      fn(t);
      dropEmptyParts(d);
    }, coalesceKey);
  },
  resetPartTransform(part, path, row) {
    get().edit((d) => {
      const t = partTransformOf(d, part, path);
      if (!t) return;
      if (row) t[row] = null;
      else t.position = t.rotation = t.scale = null;
      dropEmptyParts(d);
    });
  },
  setPartMesh(part, path, mesh) {
    get().edit((d) => {
      let t = partTransformOf(d, part, path);
      if (!t) {
        t = { part, path: path ?? null, position: null, rotation: null, scale: null, visible: null };
        d.parts!.push(t);
      }
      t.mesh = mesh?.forgeskinmesh ?? null;
      const ed = d.editor as Record<string, unknown>;
      const previews = { ...((ed.partMeshPreviews as Record<string, string>) ?? {}) };
      if (mesh) previews[partMeshKey(part, path)] = mesh.glb;
      else delete previews[partMeshKey(part, path)];
      ed.partMeshPreviews = previews;
      dropEmptyParts(d);
    });
  },
}));

/** Undo and redo never change which skin is edited (its id names its autosave and its pack in game). */
const keepId = (patches: Patch[]) => patches.filter((p) => p.path[0] !== 'id');

/** Key of a node's replacement model preview in the editor state. */
export const partMeshKey = (part: string, path?: string | null) => `${part}/${path ?? ''}`;

setLang(useStore.getState().prefs.lang ?? systemLang());

/** An entry that keeps every game value says nothing; leave it out. */
function dropEmptyParts(d: SkinDocument) {
  d.parts = d.parts!.filter((t) => t && (t.position != null || t.rotation != null || t.scale != null || t.visible != null || t.mesh != null));
}

/** Folder of every item's skins, one folder per item id. */
/** True while a work is being deleted: the editor then keeps no watch on files (a watched folder cannot go to the Recycle Bin). */
export let watchHeld = false;

export const autosaveRoot = (): string => `${useStore.getState().dataDir}\\autosave`;

/** A new charm's settings. */
export const newCharm = (): NonNullable<Attachment['charm']> => ({ ...CHARM_DEFAULTS, color: [...CHARM_DEFAULTS.color] });

/** Makes a new attachment a charm: its origin, the hanging point, on the middle of its part's underside, upright. */
export function hangAsCharm(a: Attachment) {
  a.charm = newCharm();
  a.hologram = null;
  a.position = startPoint(a.part ?? '', a.transform ?? null, true) ?? a.position;
  a.rotation = [0, 0, 0];
  showTab('attachment', 'place'); // the tab with the charm settings
}

/** Folder of one item's skins. */
const skinDir = (weaponId: string): string => `${autosaveRoot()}\\${weaponId}`;

/** Autosave of one skin; by default the skin being edited. */
export function autosavePath(weaponId: string, skinId = useStore.getState().doc.id): string {
  return `${skinDir(weaponId)}\\${skinId}.forgeskinproj`;
}

/** A skin's autosave, or null (with a plain message) when it does not open. */
async function loadSkin(path: string): Promise<SkinDocument | null> {
  try {
    return await call('project.load', { path });
  } catch (e) {
    // The backend's code would blame the app version.
    window.forge.appLog('error', detail(e));
    useStore.getState().log(t('store.autosaveLoad'));
    useStore.getState().toast('error', t('store.autosaveLoad'));
    return null;
  }
}

/** The game's enemies; without the game the kill sounds offer only "any enemy". */
async function loadEnemies() {
  try {
    useStore.setState({ enemies: (await call('enemies.list', {})).enemies });
  } catch (e) {
    useStore.getState().log(`enemies.list: ${detail(e)}`);
  }
}

/** A new skin for the item; the second and later ones get a number in their name. */
/** A new skin named after its gear, numbered from 2 past the names its gear's skins already have. */
async function blankSkin(weaponId: string, taken: string[]): Promise<SkinDocument> {
  const s = useStore.getState();
  const item = s.catalog.find((c) => c.id === weaponId);
  const doc = await call('project.new', { weaponId }).catch((e) => {
    s.log(`project.new: ${detail(e)}`);
    return { materials: [], attachments: [], target: { weapon: weaponId } } as SkinDocument;
  });
  const base = t('store.k6', { v0: item ? localName(item) : weaponId });
  let n = 1;
  const named = (k: number) => (k > 1 ? `${base} ${k}` : base);
  while (taken.includes(named(n))) n++;
  doc.name = named(n);
  return doc;
}

/** Bumped by every request to open a skin; a request that is no longer the latest does not open its skin. */
let opening = 0;

/** Makes a skin of item `id` the edited one, unless the user moved on since request `ticket` (another item or skin was
 * asked for meanwhile); false then. */
function openSkin(loaded: SkinDocument, id: string, ticket: number): boolean {
  const s = useStore.getState();
  if (ticket !== opening || s.weaponId !== id) return false;
  const item = s.catalog.find((c) => c.id === id);
  const doc = normalize({ ...loaded, target: { ...loaded.target, weapon: id } });
  if (!doc.name) doc.name = t('store.k6', { v0: item ? localName(item) : id });
  const ed = editorOf(doc);
  for (const p of item?.parts ?? []) if (ed.enabled[p.id] === undefined) ed.enabled[p.id] = p.defaultEnabled;
  doc.editor = ed as unknown as SkinDocument['editor'];
  localStorage.setItem(`sf.skin.${id}`, doc.id!);
  useStore.setState({ doc, projectPath: null, selection: null, undo: [], redo: [], unmatched: [], lastEditKey: null, editedAt: 0 });
  void restorePreviews(doc);
  return true;
}

/** The 3D view draws an imported model from the preview its import wrote (editor meshPreviews, partMeshPreviews). A model
 * without one (a project copied or written elsewhere, a cleared cache) gets it made again from its file; a model file that
 * is gone or unreadable is said, so the view never just leaves the model out. Not an edit: undo does not see it. */
async function restorePreviews(doc: SkinDocument) {
  const s = useStore.getState();
  const models = [
    ...(doc.attachments ?? []).filter((a) => a?.mesh).map((a) => ({ table: 'meshPreviews', key: a!.id!, file: a!.mesh!, name: attachmentName(doc, a!) })),
    ...(doc.parts ?? []).filter((p) => p?.mesh && p.part).map((p) => ({ table: 'partMeshPreviews', key: partMeshKey(p!.part!, p!.path), file: p!.mesh!, name: p!.part! })),
  ];
  for (const m of models) {
    const shown = (doc.editor as Record<string, Record<string, string> | undefined> | undefined)?.[m.table]?.[m.key];
    if (shown && (await window.forge.exists(shown))) continue;
    if (!(await window.forge.exists(m.file))) {
      s.toast('error', t('store.modelMissing', { v0: m.name, v1: m.file.split(/[\\/]/).pop() }));
      continue;
    }
    try {
      const { glb } = await call('mesh.import', { glb: m.file });
      if (useStore.getState().doc.id !== doc.id) return;
      useStore.setState((st) => {
        const ed = (st.doc.editor ?? {}) as Record<string, Record<string, string> | undefined>;
        return { doc: { ...st.doc, editor: { ...ed, [m.table]: { ...(ed[m.table] ?? {}), [m.key]: glb } } as unknown as SkinDocument['editor'] } };
      });
    } catch (e) {
      s.fail(e, t('errors.k6'));
    }
  }
}

/** The document as saved: with when it was last changed (editor.editedAt), the time kept from before when not changed since. */
export function withEditedAt(doc: SkinDocument, editedAt: number): SkinDocument {
  return editedAt ? { ...doc, editor: { ...(doc.editor ?? {}), editedAt: new Date(editedAt).toISOString() } as SkinDocument['editor'] } : doc;
}

export async function persistAutosave() {
  clearTimeout(autosaveTimer); // this save covers the pending one
  const { weaponId, doc, skins } = useStore.getState();
  // Only a skin opened for this item is saved: while the item's skins load, the edited document is still the start-up
  // blank or the previous item's skin, and saving it would add it to this item as a new skin.
  if (!weaponId || doc.target?.weapon !== weaponId) return;
  try {
    await call('project.save', { path: autosavePath(weaponId), project: withEditedAt(doc, useStore.getState().editedAt) });
    useStore.setState({ autosaveError: null });
    // A new skin or a new name shows in the skin list.
    if (!skins.some((k) => k.id === doc.id && k.name === doc.name)) await useStore.getState().refreshSkins();
  } catch (e) {
    useStore.getState().log(t('store.k27', { v0: detail(e) }));
    useStore.setState({ autosaveError: friendly(e, t('store.k28')) });
  }
}

/** Where the work the first tutorial had the player make is noted until it is deleted (tour/Tour.tsx). */
const PRACTICE = 'sf.tourPractice';

/** Marks the open work as the tutorial's practice work: in its document (editor.practice) and here, so it can be found again. */
export function markPractice() {
  const s = useStore.getState();
  if (!s.weaponId || !s.doc.id) return;
  localStorage.setItem(PRACTICE, JSON.stringify({ id: s.doc.id, weapon: s.weaponId }));
  useStore.setState({ doc: { ...s.doc, editor: { ...(s.doc.editor ?? {}), practice: true } as SkinDocument['editor'] } });
  void persistAutosave();
}

/** Deletes the tutorial's practice work, with its pack in the chosen profile, when its document still carries the mark. */
export async function removePractice() {
  const p = (() => {
    try {
      return JSON.parse(localStorage.getItem(PRACTICE) ?? 'null') as { id: string; weapon: string } | null;
    } catch {
      return null; // an unreadable note names nothing
    }
  })();
  localStorage.removeItem(PRACTICE);
  if (!p) return;
  const s = useStore.getState();
  const entry = (p.weapon === s.weaponId ? s.skins : (s.works[p.weapon] ?? [])).find((k) => k.id === p.id);
  if (!entry) return;
  const doc = s.doc.id === p.id ? s.doc : await call('project.load', { path: entry.path }).catch(() => null);
  if (!(doc?.editor as { practice?: boolean } | null | undefined)?.practice) return;
  await s.refreshInstalled();
  await useStore.getState().deleteSkin(p.id, p.weapon);
}

export function currentItem(): CatalogItem | undefined {
  const s = useStore.getState();
  return s.catalog.find((c) => c.id === s.weaponId);
}

export function useItem(): CatalogItem | undefined {
  return useStore((s) => s.catalog.find((c) => c.id === s.weaponId));
}
