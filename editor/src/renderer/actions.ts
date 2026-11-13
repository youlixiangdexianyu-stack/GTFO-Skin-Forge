// Multi-step user actions: apply to game, export pack, export OBJ.
import { bakeGlyphs, bakeLayers, baseTexturePath, loadImage, writePng } from './frames';
import { bakeLivery, hasLivery } from './livery';
import { drawUvWireframe, liveryViewReady, meshesOf, uvRenderer } from './viewport/liveryGpu';
import { call, type CatalogItem, type Profile, type SkinDocument, type SkinsImport, type SkinsNote, type WeaponPreview } from './protocol';
import { autosavePath, autosaveRoot, currentItem, editorOf, hangAsCharm, useStore, type Selection } from './store';
import { lang, localName, partLabel, t } from './i18n';
import { confirmInGame } from './gameLog';
import { detail, friendly } from './errors';
import { shownDoc, variantDocs } from './variants';

const st = () => useStore.getState();

/** A skin's video memory in game: up to MEMORY_FINE is light, past MEMORY_HEAVY it can make the game stutter. */
export const MEMORY_FINE = 256 * 2 ** 20;
export const MEMORY_HEAVY = 512 * 2 ** 20;
export const mb = (bytes: number) => Math.round(bytes / 2 ** 20);

/** Document as packed (of `item`, whose preview is `preview`): parts switched off keep vanilla, so their overrides are left out. */
function packDoc(doc = shownDoc(st().doc), item = currentItem(), preview = st().preview): SkinDocument {
  const ed = editorOf(doc);
  const off = new Set(item?.parts.filter((p) => ed.enabled[p.id] === false).flatMap((p) => p.materials) ?? []);
  const out = structuredClone(doc);
  out.variants = null; // each variant is a build of its own (variants.ts)
  out.materials = (out.materials ?? []).filter((m) => m && !off.has(m.material ?? ''));
  // Hidden layers are not baked, so they stay out of the pack.
  for (const m of out.materials) if (m) m.layers = (m.layers ?? []).filter((l) => l && l.visible !== false);
  // Settings the game material has no property for (the preview reads them from the game files) stay out of the pack,
  // as the inspector hides them: the game could not show them.
  for (const m of out.materials) {
    const game = m?.material ? preview?.params[m.material] : null;
    if (!game || !m?.params) continue;
    if (game.metallic == null) m.params.metallic = null;
    if (game.smoothness == null) m.params.smoothness = null;
    if (game.tint == null) m.params.tint = null;
  }
  // A GTFO/BlendHS material (vertex-coloured, no albedo texture) can only show a picture once the plugin draws it with a
  // textured gear shader: it gets the editor's baked look as its base wherever the skin draws on it.
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

/** Things the user must fix before building `doc` of `item` (by default the edited skin), in plain words. */
export function problems(doc = st().doc, item = currentItem()): Problem[] {
  const { prefs } = st();
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

/** What in `doc` names `file`: its name as the author sees it and the selection that shows it (null when nothing selectable does). */
function referrer(doc: SkinDocument, file: string): { name: string; select: Selection } {
  const names = (x: unknown) => JSON.stringify(x).includes(JSON.stringify(file));
  for (const m of doc.materials ?? []) {
    if (!m) continue;
    const l = m.layers?.find(names);
    if (l) return { name: l.name ?? '', select: { kind: 'layer', material: m.material ?? '', id: l.id ?? '' } };
    const d = m.livery?.find(names);
    if (d) return { name: d.name ?? '', select: { kind: 'livery', material: m.material ?? '', id: d.id ?? '' } };
    if (names(m)) return { name: m.material ?? '', select: partOf(m.material) };
  }
  const a = doc.attachments?.find(names);
  if (a) return { name: a.name || (a.id ?? ''), select: { kind: 'attachment', id: a.id ?? '' } };
  const p = doc.parts?.find(names);
  if (p) return { name: p.part ?? '', select: { kind: 'part', id: p.part ?? '', sub: p.path } };
  const snd = doc.sounds?.find(names);
  return snd ? { name: snd.name ?? '', select: { kind: 'sound', id: snd.id ?? '' } } : { name: '', select: null };
}

/** A file `doc` names that is gone, as one error naming what uses it and the file (the build cannot draw without it). */
async function missingSource(doc: SkinDocument): Promise<void> {
  const [file] = (await call('project.missing', { project: doc })).files;
  if (!file) return;
  const what = referrer(doc, file).name;
  throw Object.assign(new Error(t('actions.missingSource', { v0: what, v1: file.split(/[\\/]/).pop() })), { code: 'missing-source' });
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
      const gone = files.map((f) => ({ text: t('check.missing', { v0: f }), select: referrer(doc, f).select }));
      return { id: k.id, name: doc.name || k.name, problems: [...problems(doc), ...gone] };
    }),
  );
}

/** Builds a skin: by default the edited one, else `skin` loaded from its autosave, of the current item unless it names
 * another (with that item's preview; a livery is drawn on the model in the 3D view, so its item must be the open one). */
async function build(out: string, skin?: { doc: SkinDocument; path: string; item?: CatalogItem; preview?: WeaponPreview | null }): Promise<string> {
  const s = st();
  const projectPath = skin?.path ?? s.projectPath ?? autosavePath(s.weaponId!);
  const preview = skin?.preview ?? s.preview;
  const doc = packDoc(skin?.doc, skin?.item ?? currentItem(), preview);
  useStore.setState({ busy: t('actions.k5') });
  await missingSource(doc);
  // A livery is drawn on the model in the 3D view: right after a weapon is opened its model may still be loading.
  if (doc.materials?.some((m) => m && hasLivery(m))) await liveryViewReady();
  // The livery first: it becomes the base the dynamic layers play over. Its animated decals' frames are written already,
  // so their layers join the material only after its own layers are baked.
  const animated = await bakeLivery(doc, preview, projectPath, (m) => uvRenderer(m.material ?? '', m.part), (t) => useStore.setState({ busy: t }));
  await bakeLayers(doc, preview, projectPath, (t) => useStore.setState({ busy: t }));
  for (const { m, layers } of animated) m.layers = [...layers, ...(m.layers ?? [])];
  await bakeGlyphs(doc, projectPath);
  useStore.setState({ busy: t('actions.k6') });
  const r = await call('pack.build', { project: doc, projectPath, out, sizeMode: s.prefs.sizeMode });
  for (const w of r.warnings ?? []) {
    const m = w.match(/^(.+): (\d+) frames of (\d+x\d+) scaled to (\d+x\d+) to fit/);
    s.log(m ? t('actions.framesScaled', { v0: m[1], v1: m[2], v2: m[3], v3: m[4] }) : t('actions.k7', { v0: w }));
  }
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
    } else failedAt(e, t('actions.stepPlugin'));
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
  // The step under way, named in the toast when it fails.
  let step = t('actions.stepPlugin');
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
    step = t('actions.stepBuild');
    const pack = await build(`${s.dataDir}\\packs\\${s.weaponId}.forgeskin`);
    step = t('actions.stepInstall');
    useStore.setState({ busy: t('actions.k11') });
    const since = new Date().toISOString();
    // The author's apply goes before a choice made in the game's picker for this gear (PLAN §4.8).
    await call('pack.install', { pack, profile: profile.path, activate: true, pick: true });
    void s.refreshInstalled();
    const now = st().profiles.find((p) => p.path === profile.path) ?? profile;
    const live = (await call('game.locate', {}).catch(() => null))?.running && now.development && !devRestart;
    // In a running game the plugin's log says whether the skin really loaded.
    const item = currentItem();
    const shown = shownDoc(s.doc);
    if (live) void confirmInGame(profile.path, since, shown.id ?? '', `${shown.name} → ${item ? localName(item) : ''}`, quiet);
    else if (!quiet) s.toast('ok', await whenSeen(now, devRestart));
    s.log(t('actions.k14', { v0: pack, v1: profile.path }));
  } catch (e) {
    failedAt(e, step);
  }
}

/** One toast for a failed 应用到游戏: the step it stopped at and why (a backend code in plain words, else the error's own
 * text, such as the file an image could not be read from); the full error goes to the log. */
function failedAt(e: unknown, step: string) {
  const s = st();
  s.log(t('store.k2', { v0: detail(e) }));
  const own = (e as { message?: string } | undefined)?.message ?? String(e);
  s.toast('error', t('actions.applyFailed', { v0: step, v1: friendly(e, own) }));
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

const isSkinGroup = (path: string) => /manifest\.skingroup\.json$/i.test(path);

/** Imports a Skins library skin group (its manifest.skingroup.json) as skins of ours, one per gear it is for. */
export async function importSkinGroup(): Promise<void> {
  const [p] = await window.forge.openDialog('files', [{ name: 'Skins (manifest.skingroup.json)', extensions: ['json'] }], 'skingroup');
  if (!p) return;
  if (!isSkinGroup(p)) return st().toast('error', t('skins.notGroup'));
  await importSkins(p);
}

/**
 * Imports the skin group at `manifest` (onto `weapon` when the group names no gear) and opens its skin of the open item,
 * else its first; what was left out or changed opens in ui/SkinsReport. A group that names no gear asks for one there.
 */
export async function importSkins(manifest: string, weapon?: string): Promise<void> {
  const s = st();
  useStore.setState({ busy: t('skinsImport.busy') });
  let r: SkinsImport;
  try {
    r = await call('skins.import', { manifest, root: autosaveRoot(), weapon, lang: lang() });
  } catch (e) {
    return s.fail(e, t('skinsImport.failed'));
  } finally {
    useStore.setState({ busy: null });
  }
  if (r.needsTarget) return void useStore.setState({ skinsTarget: { manifest, notes: r.notes } });
  const report = () => r.notes.length && useStore.setState({ skinsReport: { title: t('skinsImport.title'), notes: r.notes, weapons: r.skins.map((k) => k.weapon) } });
  if (!r.skins.length) {
    s.toast('error', t('skinsImport.nothing'));
    return void report();
  }
  const first = r.skins.find((k) => k.weapon === s.weaponId) ?? r.skins[0];
  localStorage.setItem(`sf.skin.${first.weapon}`, first.id);
  if (first.weapon === st().weaponId) {
    await st().refreshSkins();
    await st().switchSkin(first.id);
  } else await st().selectWeapon(first.weapon);
  const names = r.skins.map((k) => st().catalog.find((c) => c.id === k.weapon)).map((c) => (c ? localName(c) : '')).join('、');
  s.toast('ok', t('skinsImport.done', { v0: r.skins.length, v1: names }));
  report();
}

export async function openProject(): Promise<void> {
  const [p] = await window.forge.openDialog('project', [{ name: t('store.k11'), extensions: ['forgeskinproj', 'gtfoskin', 'gtfdyn', 'json'] }]);
  if (p) await (isSkinGroup(p) ? importSkins(p) : st().loadProject(p));
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

/**
 * Per material of the open item, its UV wireframe at the base albedo's size, alone (transparent) and over the albedo, as
 * `<material>_uv.png` and `<material>_uv_albedo.png` in a chosen folder: a guide for painting textures in an image editor.
 */
export async function exportUvTemplates(): Promise<void> {
  const s = st();
  const item = currentItem();
  if (!item || !s.preview) return s.toast('info', t('actions.k2'));
  const [outDir] = await window.forge.openDialog('folder', undefined, 'uv');
  if (!outDir) return;
  const materials = [...new Set(item.parts.flatMap((p) => p.materials))].filter((m) => meshesOf(m).length);
  await exclusive(async () => {
    try {
      for (const [i, material] of materials.entries()) {
        useStore.setState({ busy: t('actions.uvExporting', { v0: material, v1: i + 1, v2: materials.length }) });
        const path = baseTexturePath(s.doc, s.preview, material, 'albedo');
        const base = path ? await loadImage(path).catch(() => null) : null;
        const [w, h] = base ? [base.naturalWidth, base.naturalHeight] : [2048, 2048];
        const c = Object.assign(document.createElement('canvas'), { width: w, height: h });
        const ctx = c.getContext('2d')!;
        ctx.lineWidth = Math.max(1, Math.round(Math.max(w, h) / 1024));
        ctx.lineJoin = 'round';
        ctx.strokeStyle = '#00e5ff';
        drawUvWireframe(material, ctx, w, h);
        const safe = material.replace(/[^\w.-]/g, '_');
        await writePng(`${outDir}\\${safe}_uv.png`, c);
        if (base) {
          const over = Object.assign(document.createElement('canvas'), { width: w, height: h });
          const o = over.getContext('2d')!;
          o.drawImage(base, 0, 0, w, h);
          o.drawImage(c, 0, 0);
          await writePng(`${outDir}\\${safe}_uv_albedo.png`, over);
        }
      }
      s.toast('ok', t('actions.uvDone', { v0: materials.length }), { label: t('actions.k22'), run: () => void window.forge.openPath(outDir) });
    } catch (e) {
      s.fail(e, t('actions.uvFailed'));
    } finally {
      useStore.setState({ busy: null });
    }
  });
}

/** Imports a model as an attachment on the selected part; `charm` hangs it as a charm. */
export async function importMesh(charm = false): Promise<void> {
  const s = st();
  const [glb] = await window.forge.openDialog('files', [{ name: t('actions.k24'), extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  useStore.setState({ busy: t('actions.k25') });
  try {
    const r = await call('mesh.import', { glb });
    s.addAttachment('mesh', { forgeskinmesh: r.forgeskinmesh, glb: r.glb }, charm ? hangAsCharm : undefined);
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
  /** The skins that go in, any item's (each its autosave); a gun pack takes one per item. */
  skins: { weapon: string; id: string; path: string }[];
  /** forgeskin: packs applied by ForgeSkin; skins: skin groups applied by the Skins library (still textures only). */
  format: 'forgeskin' | 'skins';
  /** Skins format: one skin group for every item (a gun pack) instead of one group per skin. */
  gunPack: boolean;
}

/** Opens item `weapon` in the editor (its livery bakes on its model) and waits until the 3D view shows `materials`. */
async function openForBake(weapon: string, materials: string[]): Promise<WeaponPreview | null> {
  await st().selectWeapon(weapon);
  for (let waited = 0; st().weaponId === weapon && !materials.some((m) => meshesOf(m).length); waited += 200) {
    if (waited > 30000) throw new Error(t('exportMod.modelTimeout'));
    await new Promise((r) => setTimeout(r, 200));
  }
  return st().preview;
}

/** Builds the chosen skins and packs them as a Thunderstore skin mod zip. Returns false when it stopped. */
export async function exportMod(info: ModInfo): Promise<boolean> {
  const s = st();
  const home = s.weaponId;
  // Every skin that goes in passes the same checks as the edited one; the first that does not is named.
  let skins: { item: CatalogItem; doc: SkinDocument; path: string }[];
  try {
    skins = await Promise.all(
      info.skins.map(async (k) => ({
        item: s.catalog.find((c) => c.id === k.weapon)!,
        doc: k.id === s.doc.id ? s.doc : await call('project.load', { path: k.path }),
        path: k.id === s.doc.id ? (s.projectPath ?? k.path) : k.path,
      })),
    );
  } catch (e) {
    s.fail(e, t('exportMod.failed'));
    return false;
  }
  for (const { doc, item } of skins) {
    const problem = problems(doc, item)[0]?.text;
    if (problem) return (s.toast('error', t('exportMod.skinProblem', { v0: doc.name ?? '', v1: problem }), openCheck()), false);
  }
  const out = await window.forge.saveDialog(`${info.name}-${info.version}.zip`, [{ name: 'Thunderstore', extensions: ['zip'] }]);
  if (!out) return false;
  const gunPack = info.format === 'skins' && info.gunPack;
  const done = await exclusive(async () => {
    try {
      const packs: string[] = [];
      const notes: SkinsNote[] = [];
      // The open item's skins first (built as they are edited); another item's livery bakes on its own model, so that
      // item opens in the editor for it.
      for (const k of [...skins].sort((a, b) => Number(a.item.id !== home) - Number(b.item.id !== home))) {
        // One skin per colour variant (variants.ts); a gun pack holds one skin per item.
        let docs = variantDocs(k.doc);
        if (gunPack && docs.length > 1) {
          notes.push({ kind: 'variants', skin: k.doc.name ?? '', what: null, n: docs.length - 1 });
          docs = docs.slice(0, 1);
        }
        const livery = docs.flatMap((d) => (d.materials ?? []).filter((m) => m && hasLivery(m)).map((m) => m!.material ?? ''));
        const preview =
          k.item.id === st().weaponId ? st().preview : livery.length ? await openForBake(k.item.id, livery) : await call('preview.weapon', { id: k.item.id });
        for (const doc of docs) packs.push(await build(`${s.dataDir}\\packs\\${doc.id}.forgeskin`, { doc, path: k.path, item: k.item, preview }));
      }
      useStore.setState({ busy: t('actions.k6') });
      const r = await call('pack.exportMod', { packs, out, name: info.name, version: info.version, description: info.description, icon: info.icon, format: info.format, gunPack });
      const all = [...notes, ...r.dropped];
      for (const d of all) s.log(`${t('exportMod.dropped')}: ${noteText(d)}`);
      s.toast('ok', all.length ? t('exportMod.doneDropped', { v0: all.length }) : t('exportMod.done'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(r.mod) });
      if (all.length) useStore.setState({ skinsReport: { title: t('exportMod.reportTitle'), notes: all, folder: r.mod } });
      return true;
    } catch (e) {
      s.fail(e, t('exportMod.failed'));
      return false;
    } finally {
      if (home && st().weaponId !== home) await st().selectWeapon(home);
    }
  });
  return done ?? false;
}

/** One Skins import or export note in plain words. */
export const noteText = (n: SkinsNote) => t(`skinsNote.${n.kind}`, { skin: n.skin, what: n.what ?? '', n: n.n });

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
