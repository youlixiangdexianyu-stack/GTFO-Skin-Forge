import { applyPatches, current, enablePatches, produceWithPatches, type Patch } from 'immer';
import { create } from 'zustand';
import { AudioError, transcodeToOgg } from './audio';
import {
  baseName,
  call,
  dirName,
  fileUrl,
  type Attachment,
  type CatalogItem,
  type DynamicLayer,
  type GameInfo,
  type MatchCell,
  type MatchResult,
  type MaterialOverride,
  type MaterialParams,
  type PartTransform,
  type Profile,
  type SizeMode,
  type SkinDocument,
  type SkinSound,
  type TextureChannel,
  type WeaponPreview,
} from './protocol';
import { detail, friendly } from './errors';
import { fillThumbnails } from './viewport/thumbnails';
import { notify, type NotifyAction, type NotifyKind } from './ui/notify';
import { localName, setLang, systemLang, t, type Lang } from './i18n';

enablePatches();

export type Mode = 'skin' | 'attach' | 'dynamic';
export type Selection =
  | { kind: 'part'; id: string; sub?: string | null }
  | { kind: 'attachment'; id: string }
  | { kind: 'layer'; material: string; id: string }
  | null;


export interface Prefs {
  ambient: number;
  ambientColor: string;
  autoRotate: boolean;
  bloom: boolean;
  sizeMode: SizeMode;
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
const DEFAULT_PREFS: Prefs = {
  ambient: 1,
  ambientColor: '#ffffff',
  autoRotate: false,
  bloom: true,
  sizeMode: 'keep',
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
  return { enabled: e.enabled ?? {}, pending: e.pending ?? {} };
}

function normalize(doc: SkinDocument): SkinDocument {
  doc.materials ??= [];
  doc.attachments ??= [];
  doc.parts ??= [];
  doc.sounds ??= [];
  doc.target ??= {};
  // The skin id is made once with the project and kept, so applying again overwrites the same pack in game.
  doc.id ||= newSkinId(doc);
  doc.editor = { ...editorOf(doc) } as unknown as SkinDocument['editor'];
  return doc;
}

/** Layout entry of a part root (`path` null) or of the child at `path`. */
export function partTransformOf(doc: SkinDocument, part: string, path?: string | null): PartTransform | undefined {
  return doc.parts?.find((t) => t?.part === part && (t.path ?? null) === (path ?? null)) ?? undefined;
}

export function materialOf(doc: SkinDocument, material: string): MaterialOverride | undefined {
  return doc.materials?.find((m) => m?.material === material) ?? undefined;
}

function ensureMaterial(doc: SkinDocument, material: string, part?: string): MaterialOverride {
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

const uid = () => Math.random().toString(36).slice(2, 10);
const newSkinId = (doc: SkinDocument) => `${doc.target?.weapon ?? 'skin'}-${uid()}`;

interface State {
  ready: boolean;
  backendDown: boolean;
  dataDir: string;
  game: GameInfo | null;
  profiles: Profile[];
  catalog: CatalogItem[];
  weaponId: string | null;
  preview: WeaponPreview | null;
  previewLoading: boolean;
  mode: Mode;
  selection: Selection;
  doc: SkinDocument;
  projectPath: string | null;
  undo: Step[];
  redo: Step[];
  lastEditKey: string | null;
  lastEditAt: number;
  prefs: Prefs;
  logs: string[];
  progress: { text: string; done: number; total: number } | null;
  /** Background preparation of every weapon preview (first run after a game update). */
  cache: { done: number; total: number } | null;
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
  log(line: string): void;
  toast(kind: NotifyKind, text: string, action?: NotifyAction): void;
  fail(e: unknown, fallback?: string): void;
  setPrefs(p: Partial<Prefs>): void;
  setMode(m: Mode): void;
  select(s: Selection): void;
  refreshGame(): Promise<void>;
  chooseGameDir(): Promise<void>;
  refreshProfiles(): Promise<void>;
  selectWeapon(id: string): Promise<void>;
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
  addAttachment(kind: 'screen' | 'mesh', mesh?: { sfmesh: string; glb: string }): void;
  updateAttachment(id: string, fn: (a: Attachment) => void, coalesceKey?: string): void;
  removeAttachment(id: string): void;
  /** Converts the picked audio file to Ogg next to the project and adds it as a sound (one undo step). */
  addSound(file: string): Promise<void>;
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
}

let initialised = false;

type Set = (p: Partial<State>) => void;
function subscribe(f: Window['forge'], set: Set, get: () => State) {
  f.onLog((line) => get().log(line));
  f.onEvent((event, data) => {
    if (event === 'progress') {
      const p = data as { done: number; total: number; text: string };
      set({ progress: p.done >= p.total ? null : p });
    }
    if (event === 'cache-progress') {
      const p = data as { done: number; total: number };
      set({ cache: p.done >= p.total ? null : { done: p.done, total: p.total } });
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

export const useStore = create<State>((set, get) => ({
  ready: false,
  backendDown: false,
  dataDir: '',
  game: null,
  profiles: [],
  catalog: [],
  weaponId: null,
  preview: null,
  previewLoading: false,
  mode: 'skin',
  selection: null,
  doc: normalize({ name: '', materials: [], attachments: [] }),
  projectPath: null,
  undo: [],
  redo: [],
  lastEditKey: null,
  lastEditAt: 0,
  prefs: loadPrefs(),
  logs: [],
  progress: null,
  cache: null,
  autosaveError: null,
  unmatched: [],
  busy: null,
  snapToSurface: false,
  subParts: {},

  async init() {
    const f = window.forge;
    if (!initialised) {
      initialised = true;
      subscribe(f, set, get);
    }
    set({ dataDir: await f.dataDir() });
    await get().refreshGame();
    try {
      const catalog = await call('catalog.get', {});
      set({ catalog: catalog.items });
    } catch (e) {
      get().log(`catalog.get: ${detail(e)}`);
    }
    await get().refreshProfiles();
    const last = localStorage.getItem('sf.weapon');
    set({ ready: true });
    if (last && !get().weaponId && get().catalog.some((c) => c.id === last)) await get().selectWeapon(last);
    void fillThumbnails();
  },
  log(line) {
    const stamp = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    set((s) => ({ logs: [...s.logs.slice(-499), `[${stamp}] ${line}`] }));
  },

  toast(kind, text, action) {
    notify(kind, text, action);
  },
  fail(e, fallback) {
    get().log(t('store.k2', { v0: detail(e) }));
    get().toast('error', friendly(e, fallback));
  },

  setPrefs(p) {
    const prefs = { ...get().prefs, ...p };
    localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    set({ prefs });
    setLang(prefs.lang ?? systemLang());
  },
  setMode(mode) {
    set({ mode, selection: null });
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
    const [dir] = await window.forge.openDialog('folder');
    if (!dir) return;
    try {
      const game = await call('game.setDir', { dir });
      set({ game });
      if (!game.found) get().toast('error', t('store.k4'));
      else {
        get().toast('ok', t('store.k5'));
        set({ catalog: (await call('catalog.get', {})).items });
        await get().refreshProfiles();
      }
    } catch (e) {
      get().fail(e);
    }
  },
  async refreshProfiles() {
    try {
      const profiles = await call('profiles.list', {});
      set({ profiles });
      // The last one used while it still has BepInEx; else the test profile; else the most recently changed one with
      // BepInEx; else none (the top bar then says what to do).
      const last = profiles.find((p) => p.path === get().prefs.profilePath && p.hasBepInEx);
      const ready = profiles.filter((p) => p.hasBepInEx).sort((a, b) => (b.modified ?? '').localeCompare(a.modified ?? ''));
      const pick = last ?? profiles.find((p) => p.name === 'SkinTest') ?? ready[0];
      get().setPrefs({ profilePath: pick?.path ?? null });
    } catch (e) {
      get().log(`profiles.list: ${detail(e)}`);
    }
  },

  async selectWeapon(id) {
    const s = get();
    if (s.weaponId === id) return;
    if (s.weaponId) await persistAutosave();
    localStorage.setItem('sf.weapon', id);
    set({ weaponId: id, preview: null, previewLoading: true, selection: null, undo: [], redo: [], unmatched: [] });
    const item = s.catalog.find((c) => c.id === id);
    // Each weapon keeps its own autosaved project.
    const auto = autosavePath(id);
    let doc: SkinDocument | null = null;
    try {
      doc = await call('project.load', { path: auto });
    } catch {
      try {
        doc = await call('project.new', { weaponId: id });
      } catch (e) {
        get().log(`project.new: ${detail(e)}`);
      }
    }
    doc ??= { name: t('store.k6', { v0: item ? localName(item) : id }), materials: [], attachments: [], target: { weapon: id } };
    doc.target = { ...doc.target, weapon: id };
    const hadId = !!doc.id;
    doc = normalize(doc);
    if (!doc.name) doc.name = t('store.k6', { v0: item ? localName(item) : id });
    const ed = editorOf(doc);
    for (const p of item?.parts ?? []) if (ed.enabled[p.id] === undefined) ed.enabled[p.id] = p.defaultEnabled;
    doc.editor = ed as unknown as SkinDocument['editor'];
    set({ doc, projectPath: null });
    if (!hadId) get().autosave();
    try {
      const preview = await call('preview.weapon', { id });
      if (get().weaponId === id) set({ preview, previewLoading: false, progress: null });
    } catch (e) {
      if (get().weaponId === id) set({ previewLoading: false, progress: null });
      get().fail(e, t('store.k7'));
    }
  },

  edit(fn, coalesceKey) {
    const s = get();
    const [doc, redo, undo] = produceWithPatches(s.doc, (d) => void fn(d as SkinDocument));
    if (!redo.length) return;
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
    });
    get().autosave();
  },
  undoEdit() {
    const s = get();
    const step = s.undo.at(-1);
    if (!step) return;
    set({ doc: applyPatches(s.doc, step.undo), undo: s.undo.slice(0, -1), redo: [...s.redo, step], lastEditKey: null });
    get().autosave();
  },
  redoEdit() {
    const s = get();
    const step = s.redo.at(-1);
    if (!step) return;
    set({ doc: applyPatches(s.doc, step.redo), redo: s.redo.slice(0, -1), undo: [...s.undo, step], lastEditKey: null });
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
      const weapon = doc.target?.weapon;
      if (weapon && weapon !== get().weaponId) {
        await persistAutosave();
        set({ weaponId: null });
        await get().selectWeapon(weapon);
      }
      set({ doc, projectPath: path.endsWith('.sfproj') ? path : null, undo: [], redo: [] });
      get().autosave();
      get().toast('ok', t('store.k9', { v0: doc.name }));
    } catch (e) {
      get().fail(e, t('store.k10'));
    }
  },
  async saveProject(saveAs) {
    let path = get().projectPath;
    if (!path || saveAs) {
      path = await window.forge.saveDialog(`${get().doc.name || 'skin'}.sfproj`, [{ name: t('store.k11'), extensions: ['sfproj'] }]);
      if (!path) return;
    }
    // Saving as a new skin is the one way to get a new skin id (a second pack in game instead of overwriting).
    if (saveAs) get().edit((d) => void (d.id = newSkinId(d)));
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
      const r = single
        ? await call('match.folder', { weaponId, folder: paths[0] })
        : await call('match.files', { weaponId, files: paths });
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

  addAttachment(kind, mesh) {
    const item = get().catalog.find((c) => c.id === get().weaponId);
    const sel = get().selection;
    const anchor = sel?.kind === 'part' ? sel.id : (item?.parts[0]?.id ?? '');
    const transform = sel?.kind === 'part' ? (sel.sub ?? null) : null;
    const id = uid();
    get().edit((d) => {
      const n = (d.attachments?.length ?? 0) + 1;
      d.attachments!.push({
        id,
        name: kind === 'screen' ? t('store.k21', { v0: n }) : t('store.k22', { v0: n }),
        visible: true,
        part: anchor,
        transform,
        position: [0, 0.03, 0],
        rotation: [0, 0, 0],
        scale: [1, 1, 1],
        faceCamera: false,
        mesh: mesh?.sfmesh ?? null,
        screen:
          kind === 'screen'
            ? {
                source: 'ammoClip',
                digits: 2,
                padZero: true,
                size: [0.03, 0.015],
                surfaceGap: 0.0005,
                panelColor: [0, 0, 0, 0.85],
                borderColor: [0.73, 0.98, 1, 1],
                borderWidth: 0.0008,
                textColor: [0.73, 0.98, 1, 1],
                textScale: 0.8,
                glow: 1.5,
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
    });
    set({ selection: null });
  },
  async addSound(file) {
    const s = get();
    const id = uid();
    set({ busy: t('store.k23') });
    try {
      const ogg = await transcodeToOgg(await (await fetch(fileUrl(file))).arrayBuffer());
      const out = `${dirName(s.projectPath ?? autosavePath(s.weaponId!))}\\sounds\\${id}.ogg`;
      await window.forge.writeFile(out, ogg);
      get().edit((d) => {
        d.sounds = [...(d.sounds ?? []), { id, name: baseName(file).replace(/\.[^.]+$/, ''), trigger: 'kill', file: out, volume: 1, lowAmmoFraction: 0.2 }];
      });
    } catch (e) {
      if (e instanceof AudioError) get().toast('error', e.message);
      else get().fail(e, t('store.k24'));
    } finally {
      set({ busy: null });
    }
  },
  updateSound(id, fn, coalesceKey) {
    get().edit((d) => {
      const x = d.sounds?.find((v) => v?.id === id);
      if (x) fn(x);
    }, coalesceKey && `sound-${id}-${coalesceKey}`);
  },
  removeSound(id) {
    get().edit((d) => void (d.sounds = (d.sounds ?? []).filter((v) => v?.id !== id)));
  },
  duplicateAttachment(id) {
    const copy = uid();
    get().edit((d) => {
      const a = d.attachments!.find((x) => x?.id === id);
      if (!a) return;
      d.attachments!.push({ ...structuredClone(current(a)), id: copy, name: t('store.k25', { v0: a.name ?? '' }) });
      const previews = (d.editor as Record<string, Record<string, string> | undefined>).meshPreviews;
      if (previews?.[id]) previews[copy] = previews[id];
    });
    set({ selection: { kind: 'attachment', id: copy } });
  },

  addLayer(material, part, layer) {
    const id = uid();
    get().edit((d) => {
      const m = ensureMaterial(d, material, part);
      m.layers!.push({
        id,
        name: layer.name ?? t('store.k26'),
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
}));

setLang(useStore.getState().prefs.lang ?? systemLang());

/** An entry that keeps every game value says nothing; leave it out. */
function dropEmptyParts(d: SkinDocument) {
  d.parts = d.parts!.filter((t) => t && (t.position != null || t.rotation != null || t.scale != null || t.visible != null));
}

export function autosavePath(weaponId: string): string {
  return `${useStore.getState().dataDir}\\autosave\\${weaponId}.sfproj`;
}

export async function persistAutosave() {
  const { weaponId, doc } = useStore.getState();
  if (!weaponId) return;
  try {
    await call('project.save', { path: autosavePath(weaponId), project: doc });
    useStore.setState({ autosaveError: null });
  } catch (e) {
    useStore.getState().log(t('store.k27', { v0: detail(e) }));
    useStore.setState({ autosaveError: friendly(e, t('store.k28')) });
  }
}

export function currentItem(): CatalogItem | undefined {
  const s = useStore.getState();
  return s.catalog.find((c) => c.id === s.weaponId);
}

export function useItem(): CatalogItem | undefined {
  return useStore((s) => s.catalog.find((c) => c.id === s.weaponId));
}
