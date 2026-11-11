import { applyPatches, current, enablePatches, produceWithPatches, type Patch } from 'immer';
import { create } from 'zustand';
import {
  call,
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
  type TextureChannel,
  type WeaponPreview,
} from './protocol';
import { detail, friendly } from './errors';
import { fillThumbnails } from './viewport/thumbnails';
import { notify, type NotifyAction, type NotifyKind } from './ui/notify';

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
  /** True once the user picked a profile themselves. */
  profileChosen: boolean;
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
  profileChosen: false,
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
  doc.target ??= {};
  doc.editor = { ...editorOf(doc) } as unknown as SkinDocument['editor'];
  return doc;
}

export function partTransformOf(doc: SkinDocument, part: string, sub?: string | null): PartTransform | undefined {
  return doc.parts?.find((t) => t?.part === part && (t.transform ?? null) === (sub ?? null)) ?? undefined;
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
  busy: string | null;
  snapToSurface: boolean;
  /** Sub-transforms of each part from the preview model, in tree order. */
  subParts: Record<string, { name: string; depth: number }[]>;

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
  duplicateAttachment(id: string): void;

  // layers
  addLayer(material: string, part: string, layer: Partial<DynamicLayer>): void;
  updateLayer(material: string, id: string, fn: (l: DynamicLayer) => void, coalesceKey?: string): void;
  removeLayer(material: string, id: string): void;

  // part layout
  setPartTransform(part: string, sub: string | null | undefined, fn: (t: PartTransform) => void, coalesceKey?: string): void;
  resetPartTransform(part: string, sub: string | null | undefined, row?: 'position' | 'rotation' | 'scale'): void;
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
  });
  f.onStatus((s) => {
    set({ backendDown: s !== 'ready' });
    // The backend came back after a crash before anything loaded: load again.
    if (s === 'ready' && !get().catalog.length) setTimeout(() => void get().init(), 500);
    if (s === 'down') get().toast('error', '后台服务启动不了。重新打开软件试试；还不行的话，打开日志把内容发给作者。');
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
    get().log(`错误 ${detail(e)}`);
    get().toast('error', friendly(e, fallback));
  },

  setPrefs(p) {
    const prefs = { ...get().prefs, ...p };
    localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    set({ prefs });
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
      if (game.found) get().log(`检测到 GTFO：${game.gameDir}`);
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
      if (!game.found) get().toast('error', '这个文件夹里没有 GTFO。选择装有 GTFO.exe 的文件夹。');
      else {
        get().toast('ok', '已连接到 GTFO');
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
      // Keep the profile the user picked; otherwise prefer the test profile, then any profile with BepInEx.
      const { profilePath, profileChosen } = get().prefs;
      if (!profileChosen || !profiles.some((p) => p.path === profilePath)) {
        const pick = profiles.find((p) => p.name === 'SkinTest') ?? profiles.find((p) => p.hasBepInEx) ?? profiles[0];
        get().setPrefs({ profilePath: pick?.path ?? null, profileChosen: false });
      }
    } catch (e) {
      get().log(`profiles.list: ${detail(e)}`);
    }
  },

  async selectWeapon(id) {
    const s = get();
    if (s.weaponId === id) return;
    if (s.weaponId) await persistAutosave();
    localStorage.setItem('sf.weapon', id);
    set({ weaponId: id, preview: null, previewLoading: true, selection: null, undo: [], redo: [] });
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
    doc ??= { name: `${item?.nameZh || item?.name || id} 皮肤`, materials: [], attachments: [], target: { weapon: id } };
    doc = normalize(doc);
    doc.target!.weapon = id;
    if (!doc.name) doc.name = `${item?.nameZh || item?.name || id} 皮肤`;
    const ed = editorOf(doc);
    for (const p of item?.parts ?? []) if (ed.enabled[p.id] === undefined) ed.enabled[p.id] = p.defaultEnabled;
    doc.editor = ed as unknown as SkinDocument['editor'];
    set({ doc, projectPath: null });
    try {
      const preview = await call('preview.weapon', { id });
      if (get().weaponId === id) set({ preview, previewLoading: false, progress: null });
    } catch (e) {
      if (get().weaponId === id) set({ previewLoading: false, progress: null });
      get().fail(e, '这把武器的 3D 预览没加载出来。确认游戏已连接后再选一次。');
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
    get().toast('info', '已清空，可以用 Ctrl+Z 撤销');
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
      get().toast('ok', `已打开「${doc.name}」`);
    } catch (e) {
      get().fail(e, '这个文件打不开。确认它是皮肤工程或旧版预设文件。');
    }
  },
  async saveProject(saveAs) {
    let path = get().projectPath;
    if (!path || saveAs) {
      path = await window.forge.saveDialog(`${get().doc.name || 'skin'}.sfproj`, [{ name: '皮肤工程', extensions: ['sfproj'] }]);
      if (!path) return;
    }
    try {
      await call('project.save', { path, project: get().doc });
      set({ projectPath: path });
      get().toast('ok', '工程已保存');
    } catch (e) {
      get().fail(e, '保存失败。换一个文件夹再试。');
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
        const m = ensureMaterial(d, p.material, p.partId);
        for (const [ch, cell] of Object.entries(p.channels) as [TextureChannel, MatchCell][]) {
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
    const parts = new Set(r.parts.map((p) => p.partId)).size;
    const total = item?.parts.length ?? parts;
    let text = `识别出 ${auto + unsure} 张贴图，覆盖 ${parts}/${total} 个部件`;
    if (unsure) text += `，其中 ${unsure} 张需要你确认（已标黄）`;
    if (r.unmatched.length) text += `；${r.unmatched.length} 个文件没认出来`;
    get().toast(unsure ? 'info' : 'ok', text);
    for (const f of r.unmatched) get().log(`未识别：${f}`);
  },
  async matchPaths(paths) {
    const weaponId = get().weaponId;
    if (!weaponId) {
      get().toast('info', '先在左边选一把武器，再拖入贴图。');
      return;
    }
    set({ busy: '正在识别贴图…' });
    try {
      const single = paths.length === 1 && !/\.(png|jpe?g|tga|tiff?|bmp|exr|webp)$/i.test(paths[0]);
      const r = single
        ? await call('match.folder', { weaponId, folder: paths[0] })
        : await call('match.files', { weaponId, files: paths });
      get().applyMatch(r);
    } catch (e) {
      get().fail(e, '贴图没识别成功。确认拖进来的是图片或装有图片的文件夹。');
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
    get().toast('ok', '发光设置已应用到全部部件');
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
        name: kind === 'screen' ? `弹药屏 ${n}` : `模型 ${n}`,
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
  duplicateAttachment(id) {
    const copy = uid();
    get().edit((d) => {
      const a = d.attachments!.find((x) => x?.id === id);
      if (!a) return;
      d.attachments!.push({ ...structuredClone(current(a)), id: copy, name: `${a.name ?? ''} 副本` });
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
        name: layer.name ?? '图层',
        kind: layer.kind ?? 'image',
        channel: 'albedo',
        visible: true,
        sources: layer.sources ?? [],
        text: layer.text ?? null,
        fps: 12,
        loop: true,
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

  setPartTransform(part, sub, fn, coalesceKey) {
    get().edit((d) => {
      let t = partTransformOf(d, part, sub);
      if (!t) {
        t = { part, transform: sub ?? null, position: [0, 0, 0], rotation: [0, 0, 0], scale: [1, 1, 1], visible: true };
        d.parts!.push(t);
      }
      fn(t);
    }, coalesceKey);
  },
  resetPartTransform(part, sub, row) {
    get().edit((d) => {
      const t = partTransformOf(d, part, sub);
      if (!t) return;
      if (row === 'position') t.position = [0, 0, 0];
      else if (row === 'rotation') t.rotation = [0, 0, 0];
      else if (row === 'scale') t.scale = [1, 1, 1];
      else d.parts = d.parts!.filter((x) => x !== t);
    });
  },
}));

export function autosavePath(weaponId: string): string {
  return `${useStore.getState().dataDir}\\autosave\\${weaponId}.sfproj`;
}

async function persistAutosave() {
  const { weaponId, doc } = useStore.getState();
  if (!weaponId) return;
  try {
    await call('project.save', { path: autosavePath(weaponId), project: doc });
  } catch (e) {
    useStore.getState().log(`自动保存失败 ${detail(e)}`);
  }
}

export function currentItem(): CatalogItem | undefined {
  const s = useStore.getState();
  return s.catalog.find((c) => c.id === s.weaponId);
}

export function useItem(): CatalogItem | undefined {
  return useStore((s) => s.catalog.find((c) => c.id === s.weaponId));
}
