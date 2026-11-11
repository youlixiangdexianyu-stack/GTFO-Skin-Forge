// Multi-step user actions: apply to game, export pack, export OBJ.
import { bakeGlyphs, bakeLayers } from './frames';
import { call, type Profile, type SkinDocument } from './protocol';
import { autosavePath, currentItem, editorOf, useStore } from './store';
import { partLabel, t } from './i18n';

const st = () => useStore.getState();

/** Document as packed: parts switched off keep vanilla, so their overrides are left out. */
function packDoc(): SkinDocument {
  const { doc } = st();
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

async function build(out: string): Promise<string> {
  const s = st();
  const projectPath = s.projectPath ?? autosavePath(s.weaponId!);
  const doc = packDoc();
  useStore.setState({ busy: t('actions.k5') });
  await bakeLayers(doc, s.preview, projectPath, (t) => useStore.setState({ busy: t }));
  await bakeGlyphs(doc, projectPath);
  useStore.setState({ busy: t('actions.k6') });
  const r = await call('pack.build', { project: doc, projectPath, out, sizeMode: s.prefs.sizeMode });
  for (const w of r.warnings ?? []) s.log(t('actions.k7', { v0: w }));
  return r.pack;
}

/** What stops 应用到游戏 for the chosen profile, in plain words; null when it can go ahead. */
export function profileProblem(profile: Profile | undefined): string | null {
  if (!profile) return t('actions.k8');
  if (!profile.hasBepInEx) return t('actions.k9', { v0: profile.name });
  return null;
}

export async function applyToGame(): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return s.toast('error', problem);
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  const blocked = profileProblem(profile);
  if (blocked || !profile) return s.toast('error', blocked ?? '');
  try {
    if (!profile.pluginVersion || profile.pluginCurrent === false) {
      useStore.setState({ busy: t('actions.k10') });
      try {
        await call('plugin.install', { profile: profile.path });
      } catch (e) {
        // The running game holds the old plugin: it can only be replaced with the game closed.
        if ((e as { code?: string }).code === 'game-running' && profile.pluginVersion) return s.toast('error', t('actions.pluginUpdate'));
        throw e;
      }
      await s.refreshProfiles();
    }
    const pack = await build(`${s.dataDir}\\packs\\${s.weaponId}.gtfskin`);
    useStore.setState({ busy: t('actions.k11') });
    await call('pack.install', { pack, profile: profile.path, activate: true });
    // Whether the game runs now, not when the window last looked.
    const running = (await call('game.locate', {}).catch(() => null))?.running ?? false;
    useStore.setState((x) => ({ game: x.game && { ...x.game, running } }));
    s.toast('ok', running ? t('actions.k12') : t('actions.k13', { v0: profile.name }));
    s.log(t('actions.k14', { v0: pack, v1: profile.path }));
  } catch (e) {
    s.fail(e, t('actions.k15'));
  } finally {
    useStore.setState({ busy: null });
  }
}

/** Opens a skin project (or an old preset) chosen in the file dialog. */
export async function openProject(): Promise<void> {
  const [p] = await window.forge.openDialog('project', [{ name: t('store.k11'), extensions: ['sfproj', 'gtfoskin', 'gtfdyn', 'json'] }]);
  if (p) await st().loadProject(p);
}

export async function exportPack(): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return s.toast('error', problem);
  const out = await window.forge.saveDialog(`${s.doc.name || s.weaponId}.gtfskin`, [{ name: t('actions.k16'), extensions: ['gtfskin'] }]);
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
    s.addAttachment('mesh', { sfmesh: r.sfmesh, glb: r.glb });
    s.toast('ok', t('actions.k26'));
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}
