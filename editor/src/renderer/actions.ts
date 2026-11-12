// Multi-step user actions: apply to game, export pack, export OBJ.
import { bakeGlyphs, bakeLayers } from './frames';
import { bakeLivery, hasLivery } from './livery';
import { decalRenderer } from './viewport/liveryGpu';
import { call, type Profile, type SkinDocument } from './protocol';
import { autosavePath, currentItem, editorOf, useStore, type Selection } from './store';
import { localName, partLabel, t } from './i18n';
import { confirmInGame } from './gameLog';

const st = () => useStore.getState();

/** A skin's video memory in game: up to MEMORY_FINE is light, past MEMORY_HEAVY it can make the game stutter. */
export const MEMORY_FINE = 256 * 2 ** 20;
export const MEMORY_HEAVY = 512 * 2 ** 20;
export const mb = (bytes: number) => Math.round(bytes / 2 ** 20);

/** Document as packed: parts switched off keep vanilla, so their overrides are left out. */
function packDoc(doc = st().doc): SkinDocument {
  const item = currentItem();
  const ed = editorOf(doc);
  const off = new Set(item?.parts.filter((p) => ed.enabled[p.id] === false).flatMap((p) => p.materials) ?? []);
  const out = structuredClone(doc);
  out.materials = (out.materials ?? []).filter((m) => m && !off.has(m.material ?? ''));
  // Hidden layers are not baked, so they stay out of the pack.
  for (const m of out.materials) if (m) m.layers = (m.layers ?? []).filter((l) => l && l.visible !== false);
  // A GTFO/BlendHS material (vertex-coloured, no albedo texture) can only show a picture once the plugin draws it with a
  // textured gear shader: it gets the editor's baked look as its base wherever the skin draws on it.
  const preview = st().preview;
  for (const m of out.materials) {
    if (!m?.material || preview?.params[m.material]?.shader !== 'GTFO/BlendHS') continue;
    if (!m.layers?.length && !Object.keys(m.textures ?? {}).length) continue;
    const baked = preview.textures[m.material] ?? {};
    m.textures ??= {};
    for (const ch of ['albedo', 'metallicSmoothness'] as const) if (!m.textures[ch] && baked[ch]) m.textures[ch] = baked[ch];
  }
  out.target = { ...out.target, weapon: item?.id ?? out.target?.weapon, gearId: item?.gearId ?? null, itemId: item?.itemId ?? null };
  return out;
}

/** Something to fix before a skin can be built, in plain words, and what to select to fix it. */
export interface Problem {
  text: string;
  select?: Selection;
}

/** The current item's part that wears `material`. */
function partOf(material: string | null | undefined): Selection {
  const p = currentItem()?.parts.find((x) => x.materials.includes(material ?? ''));
  return p ? { kind: 'part', id: p.id } : null;
}

/** Everything whose numbers the pack check refuses (SkinPack.Validate `bad-vector`). */
function badVectors(doc: SkinDocument): Problem[] {
  const ok = (v: number[] | null | undefined, n: number, nullable: boolean) => (v == null ? nullable : v.length === n && v.every(Number.isFinite));
  const bad = (what: string, select: Selection) => ({ text: t('actions.badVector', { v0: what }), select });
  const out: Problem[] = [];
  for (const m of doc.materials ?? [])
    if (m && !(ok(m.params?.emissiveColor, 3, true) && ok(m.params?.tint, 4, true))) out.push(bad(t('actions.badVectorMaterial', { v0: m.material ?? '' }), partOf(m.material)));
  const parts = currentItem()?.parts ?? [];
  for (const p of doc.parts ?? []) {
    if (!p || (ok(p.position, 3, true) && ok(p.rotation, 3, true) && ok(p.scale, 3, true))) continue;
    const part = parts.find((x) => x.id === p.part);
    const name = part ? partLabel(part) : (p.part ?? '');
    out.push(bad(t('actions.badVectorPart', { v0: p.path ? `${name} / ${p.path.split('/').pop()}` : name }), { kind: 'part', id: p.part ?? '', sub: p.path }));
  }
  for (const a of doc.attachments ?? [])
    if (a && !(ok(a.position, 3, false) && ok(a.rotation, 3, false) && ok(a.scale, 3, false) && (a.effects ?? []).every((e) => !e || ok(e.color, 3, false))))
      out.push(bad(t('actions.badVectorAttachment', { v0: a.name || a.id || '' }), { kind: 'attachment', id: a.id ?? '' }));
  return out;
}

/** Things the user must fix before building `doc` (by default the edited skin), in plain words. */
export function problems(doc = st().doc): Problem[] {
  const { prefs } = st();
  const item = currentItem();
  if (!item) return [{ text: t('actions.k2') }];
  const out: Problem[] = [];
  const pending = Object.keys(editorOf(doc).pending);
  if (pending.length) out.push({ text: t('actions.k1', { v0: pending.length }), select: partOf(pending[0].slice(0, pending[0].lastIndexOf('|'))) });
  out.push(...badVectors(doc));
  const hasContent = (doc.materials ?? []).some((m) => m && (Object.keys(m.textures ?? {}).length || m.layers?.length || hasLivery(m) || Object.values(m.params ?? {}).some((v) => v != null)));
  // Part layout, attachments and sounds are changes too, not only textures.
  if (!hasContent && !(doc.attachments ?? []).length && !(doc.parts ?? []).length && !(doc.sounds ?? []).length) out.push({ text: t('actions.k3') });
  // The "every channel of every part" check only applies to a skin that replaces textures.
  if (!prefs.allowMissing && hasContent) {
    const ed = editorOf(doc);
    for (const p of item.parts) {
      if (ed.enabled[p.id] === false) continue;
      const short = p.materials.some((mat) => Object.keys(doc.materials?.find((m) => m?.material === mat)?.textures ?? {}).length < 4);
      if (short) out.push({ text: t('actions.k4', { v0: partLabel(p) }), select: { kind: 'part', id: p.id } });
    }
  }
  return out;
}

/** The first thing to fix in `doc`, or null. */
function preflight(doc = st().doc): string | null {
  return problems(doc)[0]?.text ?? null;
}

/** The toast button that opens the full check (导出前体检). */
const openCheck = () => ({ label: t('check.open'), run: () => useStore.setState({ checkOpen: true }) });

/** What in `doc` names `file`, to select it; null when nothing selectable does. */
function referrer(doc: SkinDocument, file: string): Selection {
  const names = (x: unknown) => JSON.stringify(x).includes(JSON.stringify(file));
  for (const m of doc.materials ?? []) {
    if (!m) continue;
    const l = m.layers?.find(names);
    if (l) return { kind: 'layer', material: m.material ?? '', id: l.id ?? '' };
    const d = m.livery?.find(names);
    if (d) return { kind: 'livery', material: m.material ?? '', id: d.id ?? '' };
    if (names(m)) return partOf(m.material);
  }
  const a = doc.attachments?.find(names);
  if (a) return { kind: 'attachment', id: a.id ?? '' };
  const p = doc.parts?.find(names);
  if (p) return { kind: 'part', id: p.part ?? '', sub: p.path };
  const snd = doc.sounds?.find(names);
  return snd ? { kind: 'sound', id: snd.id ?? '' } : null;
}

export interface SkinCheck {
  id: string;
  name: string;
  problems: Problem[];
}

/** 导出前体检: every skin of the current item through the build checks, plus the files each one names that are gone. */
export async function checkSkins(): Promise<SkinCheck[]> {
  const s = st();
  return Promise.all(
    s.skins.map(async (k) => {
      const doc = k.id === s.doc.id ? s.doc : await call('project.load', { path: k.path }).catch(() => null);
      if (!doc) return { id: k.id, name: k.name, problems: [{ text: t('check.unreadable') }] };
      const { files } = await call('project.missing', { project: doc });
      const gone = files.map((f) => ({ text: t('check.missing', { v0: f }), select: referrer(doc, f) }));
      return { id: k.id, name: doc.name || k.name, problems: [...problems(doc), ...gone] };
    }),
  );
}

/** Builds a skin of the current item: by default the edited one, else `skin` loaded from its autosave. */
async function build(out: string, skin?: { doc: SkinDocument; path: string }): Promise<string> {
  const s = st();
  const projectPath = skin?.path ?? s.projectPath ?? autosavePath(s.weaponId!);
  const doc = packDoc(skin?.doc);
  useStore.setState({ busy: t('actions.k5') });
  // The livery first: it becomes the base the dynamic layers play over. Its animated decals' frames are written already,
  // so their layers join the material only after its own layers are baked.
  const animated = await bakeLivery(doc, s.preview, projectPath, (m) => decalRenderer(m.material ?? '', m.part), (t) => useStore.setState({ busy: t }));
  await bakeLayers(doc, s.preview, projectPath, (t) => useStore.setState({ busy: t }));
  for (const { m, layers } of animated) m.layers = [...layers, ...(m.layers ?? [])];
  await bakeGlyphs(doc, projectPath);
  useStore.setState({ busy: t('actions.k6') });
  const r = await call('pack.build', { project: doc, projectPath, out, sizeMode: s.prefs.sizeMode });
  for (const w of r.warnings ?? []) s.log(t('actions.k7', { v0: w }));
  // Warned when the skin first goes over the budget, not on every automatic apply after.
  const prev = useStore.getState().skinMemory;
  const id = doc.id ?? '';
  useStore.setState({ skinMemory: { id, bytes: r.memory } });
  if (r.memory > MEMORY_HEAVY && (prev?.id !== id || prev.bytes <= MEMORY_HEAVY)) s.toast('error', t('actions.memoryHeavy', { v0: mb(r.memory) }));
  return r.pack;
}

/** Builds the current skin's pack at `out` after the usual checks; null (with a toast) when something must be fixed first.
 * Runs inside the caller's exclusive(), which clears `busy` when its own step is done. */
export async function buildPack(out: string): Promise<string | null> {
  const problem = preflight();
  if (problem) {
    st().toast('error', problem, openCheck());
    return null;
  }
  return build(out);
}

let building = false;
let applyAgain = false;

/** Runs one build or export at a time; undefined (with a note unless `quiet`) when another is still going. The one
 * running owns `busy` and clears it when done; an automatic apply asked for meanwhile runs once after it. */
export async function exclusive<T>(fn: () => Promise<T>, quiet = false): Promise<T | undefined> {
  if (building) {
    if (!quiet) st().toast('info', t('actions.building'));
    return undefined;
  }
  building = true;
  try {
    return await fn();
  } finally {
    building = false;
    useStore.setState({ busy: null });
    if (applyAgain) {
      applyAgain = false;
      void applyToGame(true);
    }
  }
}

/** What stops 应用到游戏 for the chosen profile, in plain words; null when it can go ahead. */
export function profileProblem(profile: Profile | undefined): string | null {
  if (!profile) return t('actions.k8');
  if (!profile.hasBepInEx) return t('actions.k9', { v0: profile.name });
  return null;
}

/**
 * Installs or updates the skin plugin in the chosen profile when it is missing or not this app's build, like a mod
 * manager would. While the running game holds an old plugin the update waits (`pluginPending`) and is retried once the
 * game is closed. Returns false when the plugin is still not the current one.
 */
export async function ensurePlugin(quiet = false): Promise<boolean> {
  // Read the profile fresh: the plugin files may have changed on disk since the list was last loaded.
  await st().refreshProfiles();
  const s = st();
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  if (!profile?.hasBepInEx || profile.pluginCurrent !== false) return true;
  // Say which problem this is before fixing it; not an error, the install follows by itself.
  const why = t(profile.pluginVersion ? 'actions.pluginWrong' : 'actions.pluginMissing');
  try {
    if (!quiet) s.toast('info', why);
    await call('plugin.install', { profile: profile.path });
    useStore.setState({ pluginPending: null });
    await s.refreshProfiles();
    s.toast('ok', t(profile.pluginVersion ? 'actions.pluginUpdated' : 'actions.pluginInstalled', { v0: profile.name }));
    return true;
  } catch (e) {
    if ((e as { code?: string }).code === 'game-running') {
      useStore.setState({ pluginPending: profile.path });
      if (!quiet) s.toast('info', `${why} ${t('actions.pluginUpdate')}`);
    } else s.fail(e, t('actions.k15'));
    return false;
  }
}

/** Builds the skin and installs it into the test profile. `quiet` (automatic applies) skips silently when something
 * must be fixed first and reports only the game's warnings. */
export async function applyToGame(quiet = false): Promise<void> {
  if (building && quiet) return void (applyAgain = true);
  await exclusive(() => apply(quiet), quiet);
}

async function apply(quiet: boolean): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return quiet ? undefined : s.toast('error', problem, openCheck());
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  const blocked = profileProblem(profile);
  if (blocked || !profile) return quiet ? undefined : s.toast('error', blocked ?? '');
  try {
    useStore.setState({ busy: t('actions.k10') });
    if (!(await ensurePlugin())) return;
    // The author's own profile refreshes live: turn the plugin's hot reload on (players who only install skin mods keep
    // its default, off). A running game read the old setting at launch and picks it up next time.
    let devRestart = false;
    if (!profile.development) {
      devRestart = (await call('plugin.setDevelopment', { profile: profile.path, on: true })).restartNeeded;
      await s.refreshProfiles();
    }
    const pack = await build(`${s.dataDir}\\packs\\${s.weaponId}.forgeskin`);
    useStore.setState({ busy: t('actions.k11') });
    const since = new Date().toISOString();
    await call('pack.install', { pack, profile: profile.path, activate: true });
    void s.refreshInstalled();
    const now = st().profiles.find((p) => p.path === profile.path) ?? profile;
    const live = (await call('game.locate', {}).catch(() => null))?.running && now.development && !devRestart;
    // In a running game the plugin's log says whether the skin really loaded.
    const item = currentItem();
    if (live) void confirmInGame(profile.path, since, s.doc.id ?? '', `${s.doc.name} → ${item ? localName(item) : ''}`, quiet);
    else if (!quiet) s.toast('ok', await whenSeen(now, devRestart));
    s.log(t('actions.k14', { v0: pack, v1: profile.path }));
  } catch (e) {
    s.fail(e, t('actions.k15'));
  }
}

/** When the game shows a skin change in `profile`, in plain words. */
async function whenSeen(profile: Profile, devRestart = false): Promise<string> {
  // Whether the game runs now, not when the window last looked.
  const running = (await call('game.locate', {}).catch(() => null))?.running ?? false;
  useStore.setState((x) => ({ game: x.game && { ...x.game, running } }));
  // Without the development switch the plugin reads skins only at game start.
  if (!running) return t('actions.k13', { v0: profile.name });
  if (devRestart) return t('actions.devNextLaunch');
  return profile.development ? t('actions.k12') : t('actions.nextLaunch');
}

/** Makes an installed skin the one the game uses for its item (the item's other skins stay installed). */
export async function activateSkin(id: string, name: string): Promise<void> {
  const s = st();
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  if (!profile) return;
  try {
    await call('pack.activate', { profile: profile.path, skinId: id, active: true });
    await s.refreshInstalled();
    s.toast('ok', `${t('skins.activated', { v0: name })} · ${await whenSeen(profile)}`);
  } catch (e) {
    s.fail(e);
  }
}

/** Opens a skin project (or an old preset) chosen in the file dialog. */
export async function openProject(): Promise<void> {
  const [p] = await window.forge.openDialog('project', [{ name: t('store.k11'), extensions: ['forgeskinproj', 'gtfoskin', 'gtfdyn', 'json'] }]);
  if (p) await st().loadProject(p);
}

export async function exportPack(): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return s.toast('error', problem, openCheck());
  const out = await window.forge.saveDialog(`${s.doc.name || s.weaponId}.forgeskin`, [{ name: t('actions.k16'), extensions: ['forgeskin'] }]);
  if (!out) return;
  await exclusive(async () => {
    try {
      const pack = await build(out);
      s.toast('ok', t('actions.k17'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(pack) });
    } catch (e) {
      s.fail(e, t('actions.k19'));
    }
  });
}

export async function exportObj(mode: 'separate' | 'merged'): Promise<void> {
  const s = st();
  const item = currentItem();
  if (!item) return s.toast('info', t('actions.k2'));
  const [outDir] = await window.forge.openDialog('folder', undefined, 'obj');
  if (!outDir) return;
  const ed = editorOf(s.doc);
  const parts = item.parts.filter((p) => ed.enabled[p.id] !== false).map((p) => p.id);
  useStore.setState({ busy: t('actions.k20') });
  try {
    const r = await call('export.obj', { weaponId: item.id, parts, mode, outDir });
    s.toast('ok', t('actions.k21'), { label: t('actions.k22'), run: () => void window.forge.openPath(r.dir) });
  } catch (e) {
    s.fail(e, t('actions.k23'));
  } finally {
    useStore.setState({ busy: null });
  }
}

export async function importMesh(): Promise<void> {
  const s = st();
  const [glb] = await window.forge.openDialog('files', [{ name: t('actions.k24'), extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  useStore.setState({ busy: t('actions.k25') });
  try {
    const r = await call('mesh.import', { glb });
    s.addAttachment('mesh', { forgeskinmesh: r.forgeskinmesh, glb: r.glb });
    s.toast('ok', t('actions.k26'));
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}

export interface ModInfo {
  name: string;
  version: string;
  description: string;
  icon: string;
  /** Ids of the current item's skins that go in. */
  skins: string[];
}

/** Builds the chosen skins of the current item and packs them as a Thunderstore skin mod zip. Returns false when it stopped. */
export async function exportMod(info: ModInfo): Promise<boolean> {
  const s = st();
  // Every skin that goes in passes the same checks as the edited one; the first that does not is named.
  let skins: { id: string; skin?: { doc: SkinDocument; path: string } }[];
  try {
    skins = await Promise.all(
      info.skins.map(async (id) => {
        const entry = s.skins.find((k) => k.id === id)!;
        return { id, skin: id === s.doc.id ? undefined : { doc: await call('project.load', { path: entry.path }), path: entry.path } };
      }),
    );
  } catch (e) {
    s.fail(e, t('exportMod.failed'));
    return false;
  }
  for (const { skin } of skins) {
    const doc = skin?.doc ?? s.doc;
    const problem = preflight(doc);
    if (problem) return (s.toast('error', t('exportMod.skinProblem', { v0: doc.name ?? '', v1: problem }), openCheck()), false);
  }
  const out = await window.forge.saveDialog(`${info.name}-${info.version}.zip`, [{ name: 'Thunderstore', extensions: ['zip'] }]);
  if (!out) return false;
  const done = await exclusive(async () => {
    try {
      const packs: string[] = [];
      for (const { id, skin } of skins) packs.push(await build(`${s.dataDir}\\packs\\${id}.forgeskin`, skin));
      useStore.setState({ busy: t('actions.k6') });
      const { mod } = await call('pack.exportMod', { packs, out, name: info.name, version: info.version, description: info.description, icon: info.icon });
      s.toast('ok', t('exportMod.done'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(mod) });
      return true;
    } catch (e) {
      s.fail(e, t('exportMod.failed'));
      return false;
    }
  });
  return done ?? false;
}

/** Opens the Thunderstore skin mod export (ui/ExportModDialog). */
export function exportThunderstore(): void {
  useStore.setState({ exportModOpen: true });
}

/** Folder of the edited skin's snapshots, next to its autosave. Paths inside a snapshot are relative to this folder. */
const snapshotDir = () => autosavePath(st().weaponId!).replace(/\.forgeskinproj$/, '.snapshots');

/** The edited skin's snapshots, newest first; each entry's name is the snapshot's name. */
export const listSnapshots = () => call('project.list', { dir: snapshotDir() });

/** Saves the edited skin as a snapshot named `name` (default: the time). The snapshot keeps its name in the document's name. */
export async function saveSnapshot(name?: string): Promise<void> {
  const now = new Date();
  const project = { ...st().doc, name: name?.trim() || now.toLocaleString() };
  await call('project.save', { path: `${snapshotDir()}\\${now.toISOString().replace(/[:.]/g, '-')}.forgeskinproj`, project });
}

/** Puts a snapshot back into the edited skin as one undoable edit, after snapshotting what it replaces; the skin keeps its
 * id and name. */
export async function restoreSnapshot(path: string): Promise<void> {
  const snap = await call('project.load', { path });
  await saveSnapshot(t('snapshots.before', { v0: new Date().toLocaleString() }));
  useStore.setState({ selection: null });
  st().edit((d) => {
    const keep = { id: d.id, name: d.name };
    for (const k of Object.keys(d)) delete (d as Record<string, unknown>)[k];
    Object.assign(d, snap, keep);
  });
}

export const deleteSnapshot = (path: string) => call('project.delete', { path });
