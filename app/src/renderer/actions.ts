// Multi-step user actions: apply to game, export pack, export OBJ.
import { bakeGlyphs, bakeLayers } from './frames';
import { call, type SkinDocument } from './protocol';
import { autosavePath, currentItem, editorOf, useStore } from './store';

const st = () => useStore.getState();

/** Document as packed: parts switched off keep vanilla, so their overrides are left out. */
function packDoc(): SkinDocument {
  const { doc } = st();
  const item = currentItem();
  const ed = editorOf(doc);
  const off = new Set(item?.parts.filter((p) => ed.enabled[p.id] === false).flatMap((p) => p.materials) ?? []);
  const out = structuredClone(doc);
  out.materials = (out.materials ?? []).filter((m) => m && !off.has(m.material ?? ''));
  out.target = { ...out.target, weapon: item?.id ?? out.target?.weapon, archetypeId: item?.archetypeId ?? null };
  if (!out.id) out.id = `${item?.id ?? 'skin'}-${Date.now().toString(36)}`;
  return out;
}

/** Things the user must fix before building, in plain words. */
function preflight(): string | null {
  const { doc, prefs } = st();
  const pending = Object.keys(editorOf(doc).pending).length;
  if (pending) return `还有 ${pending} 张贴图没确认（标黄的格子）。点一下确认，或换成正确的贴图。`;
  const item = currentItem();
  if (!item) return '先在左边选一把武器。';
  const hasContent = (doc.materials ?? []).some((m) => m && (Object.keys(m.textures ?? {}).length || m.layers?.length || Object.values(m.params ?? {}).some((v) => v != null)));
  if (!hasContent && !(doc.attachments ?? []).length) return '还没有任何改动。把 Substance Painter 导出的贴图文件夹拖进窗口试试。';
  if (!prefs.allowMissing) {
    const ed = editorOf(doc);
    for (const p of item.parts) {
      if (ed.enabled[p.id] === false) continue;
      for (const mat of p.materials) {
        const t = doc.materials?.find((m) => m?.material === mat)?.textures ?? {};
        if (Object.keys(t).length < 4) return `「${p.label}」还缺贴图。补上，或在「更多」里打开“缺失的部件保持原样”。`;
      }
    }
  }
  return null;
}

async function build(out: string): Promise<string> {
  const s = st();
  const projectPath = s.projectPath ?? autosavePath(s.weaponId!);
  const doc = packDoc();
  useStore.setState({ busy: '正在生成动态贴图和屏幕字体…' });
  await bakeLayers(doc, s.preview, projectPath, (t) => useStore.setState({ busy: t }));
  await bakeGlyphs(doc, projectPath);
  useStore.setState({ busy: '正在打包皮肤…' });
  const r = await call('pack.build', { project: doc, projectPath, out, sizeMode: s.prefs.sizeMode });
  for (const w of r.warnings ?? []) s.log(`提示：${w}`);
  return r.pack;
}

export async function applyToGame(): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return s.toast('error', problem);
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  if (!profile) return s.toast('error', '没找到可以安装的游戏配置。先用 r2modman 装好 BepInEx，或在「应用到游戏」旁边选择一个配置。');
  try {
    if (!profile.pluginVersion) {
      useStore.setState({ busy: '正在安装皮肤插件…' });
      await call('plugin.install', { profile: profile.path });
      await s.refreshProfiles();
    }
    const pack = await build(`${s.dataDir}\\packs\\${s.weaponId}.gtfskin`);
    useStore.setState({ busy: '正在安装到游戏…' });
    await call('pack.install', { pack, profile: profile.path, activate: true });
    const game = s.game?.running ? '游戏里会自动刷新。' : '下次进游戏就能看到。';
    s.toast('ok', `已应用到「${profile.name}」。${game}`);
    s.log(`已安装 ${pack} → ${profile.path}`);
  } catch (e) {
    s.fail(e, '应用到游戏没有完成。打开日志看看哪一步出了问题。');
  } finally {
    useStore.setState({ busy: null });
  }
}

export async function exportPack(): Promise<void> {
  const s = st();
  const problem = preflight();
  if (problem) return s.toast('error', problem);
  const out = await window.forge.saveDialog(`${s.doc.name || s.weaponId}.gtfskin`, [{ name: '皮肤包', extensions: ['gtfskin'] }]);
  if (!out) return;
  try {
    const pack = await build(out);
    s.toast('ok', '皮肤包已导出', { label: '打开位置', run: () => void window.forge.showInFolder(pack) });
  } catch (e) {
    s.fail(e, '导出没有完成。打开日志看看哪一步出了问题。');
  } finally {
    useStore.setState({ busy: null });
  }
}

export async function exportObj(mode: 'separate' | 'merged'): Promise<void> {
  const s = st();
  const item = currentItem();
  if (!item) return s.toast('info', '先在左边选一把武器。');
  const [outDir] = await window.forge.openDialog('folder');
  if (!outDir) return;
  const ed = editorOf(s.doc);
  const parts = item.parts.filter((p) => ed.enabled[p.id] !== false).map((p) => p.id);
  useStore.setState({ busy: '正在导出 OBJ…' });
  try {
    const r = await call('export.obj', { weaponId: item.id, parts, mode, outDir });
    s.toast('ok', 'OBJ 模型已导出，可以拿去 Substance Painter 画贴图', { label: '打开文件夹', run: () => void window.forge.openPath(r.dir) });
  } catch (e) {
    s.fail(e, 'OBJ 导出没有完成。');
  } finally {
    useStore.setState({ busy: null });
  }
}

export async function importMesh(): Promise<void> {
  const s = st();
  const [glb] = await window.forge.openDialog('files', [{ name: '3D 模型', extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  useStore.setState({ busy: '正在导入模型…' });
  try {
    const r = await call('mesh.import', { glb });
    s.addAttachment('mesh', { sfmesh: r.sfmesh, glb: r.glb });
    s.toast('ok', '模型已挂到选中的部件上。按 G 移动，R 旋转，S 缩放。');
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}
