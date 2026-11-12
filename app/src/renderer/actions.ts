// Multi-step user actions: apply to game, export pack, export OBJ.
import { bakeGlyphs, bakeLayers } from './frames';
import { call, type Profile, type SkinDocument } from './protocol';
import { autosavePath, currentItem, editorOf, useStore } from './store';
import { localName, partLabel, t } from './i18n';
import { confirmInGame } from './gameLog';

const st = () => useStore.getState();

/** Document as packed: parts switched off keep vanilla, so their overrides are left out. */
function packDoc(doc = st().doc): SkinDocument {
  const item = currentItem();
  const ed = editorOf(doc);
  const off = new Set(item?.parts.filter((p) => ed.enabled[p.id] === false).flatMap((p) => p.materials) ?? []);
  const out = structuredClone(doc);
  out.materials = (out.materials ?? []).filter((m) => m && !off.has(m.material ?? ''));
  out.target = { ...out.target, weapon: item?.id ?? out.target?.weapon, gearId: item?.gearId ?? null, itemId: item?.itemId ?? null };
  return out;
}

/** Things the user must fix before building, in plain words. */
function preflight(): string | null {
  const { doc, prefs } = st();
  const pending = Object.keys(editorOf(doc).pending).length;
  if (pending) return t('actions.k1', { v0: pending });
  const item = currentItem();
  if (!item) return t('actions.k2');
  const hasContent = (doc.materials ?? []).some((m) => m && (Object.keys(m.textures ?? {}).length || m.layers?.length || Object.values(m.params ?? {}).some((v) => v != null)));
  // Part layout, attachments and sounds are changes too, not only textures.
  if (!hasContent && !(doc.attachments ?? []).length && !(doc.parts ?? []).length && !(doc.sounds ?? []).length) return t('actions.k3');
  // The "every channel of every part" check only applies to a skin that replaces textures.
  if (!prefs.allowMissing && hasContent) {
    const ed = editorOf(doc);
    for (const p of item.parts) {
      if (ed.enabled[p.id] === false) continue;
      for (const mat of p.materials) {
        const tex = doc.materials?.find((m) => m?.material === mat)?.textures ?? {};
        if (Object.keys(tex).length < 4) return t('actions.k4', { v0: partLabel(p) });
      }
    }
  }
  return null;
}

/** Builds a skin of the current item: by default the edited one, else `skin` loaded from its autosave. */
async function build(out: string, skin?: { doc: SkinDocument; path: string }): Promise<string> {
  const s = st();
  const projectPath = skin?.path ?? s.projectPath ?? autosavePath(s.weaponId!);
  const doc = packDoc(skin?.doc);
  useStore.setState({ busy: t('actions.k5') });
  await bakeLayers(doc, s.preview, projectPath, (t) => useStore.setState({ busy: t }));
  await bakeGlyphs(doc, projectPath);
  useStore.setState({ busy: t('actions.k6') });
  const r = await call('pack.build', { project: doc, projectPath, out, sizeMode: s.prefs.sizeMode });
  for (const w of r.warnings ?? []) s.log(t('actions.k7', { v0: w }));
  return r.pack;
}

/** Builds the current skin's pack at `out` after the usual checks; null (with a toast) when something must be fixed first.
 * Leaves `busy` set: the caller clears it when its own step is done. */
export async function buildPack(out: string): Promise<string | null> {
  const problem = preflight();
  if (problem) {
    st().toast('error', problem);
    return null;
  }
  return build(out);
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
  const s = st();
  const problem = preflight();
  if (problem) return quiet ? undefined : s.toast('error', problem);
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
  } finally {
    useStore.setState({ busy: null });
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
  if (problem) return s.toast('error', problem);
  const out = await window.forge.saveDialog(`${s.doc.name || s.weaponId}.forgeskin`, [{ name: t('actions.k16'), extensions: ['forgeskin'] }]);
  if (!out) return;
  try {
    const pack = await build(out);
    s.toast('ok', t('actions.k17'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(pack) });
  } catch (e) {
    s.fail(e, t('actions.k19'));
  } finally {
    useStore.setState({ busy: null });
  }
}

export async function exportObj(mode: 'separate' | 'merged'): Promise<void> {
  const s = st();
  const item = currentItem();
  if (!item) return s.toast('info', t('actions.k2'));
  const [outDir] = await window.forge.openDialog('folder');
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
  if (info.skins.includes(s.doc.id!)) {
    const problem = preflight();
    if (problem) return (s.toast('error', problem), false);
  }
  const out = await window.forge.saveDialog(`${info.name}-${info.version}.zip`, [{ name: 'Thunderstore', extensions: ['zip'] }]);
  if (!out) return false;
  try {
    const packs: string[] = [];
    for (const id of info.skins) {
      const entry = s.skins.find((k) => k.id === id)!;
      const skin = id === s.doc.id ? undefined : { doc: await call('project.load', { path: entry.path }), path: entry.path };
      packs.push(await build(`${s.dataDir}\\packs\\${id}.forgeskin`, skin));
    }
    useStore.setState({ busy: t('actions.k6') });
    const { mod } = await call('pack.exportMod', { packs, out, name: info.name, version: info.version, description: info.description, icon: info.icon });
    s.toast('ok', t('exportMod.done'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(mod) });
    return true;
  } catch (e) {
    s.fail(e, t('exportMod.failed'));
    return false;
  } finally {
    useStore.setState({ busy: null });
  }
}

/** Opens the Thunderstore skin mod export (ui/ExportModDialog). */
export function exportThunderstore(): void {
  useStore.setState({ exportModOpen: true });
}
