import { ActionIcon, Button, Drawer, Group, Progress, ScrollArea, Text, Tooltip } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { useHotkeys } from '@mantine/hooks';
import { IconArrowBackUp, IconArrowForwardUp, IconFileText, IconFolderDown } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { Group as PanelGroup, Panel, Separator, useDefaultLayout } from 'react-resizable-panels';
import { applyToGame, ensurePlugin, openProject } from './actions';
import { useGameWarnings } from './gameLog';
import { call } from './protocol';
import { useStore } from './store';
import { AttachInspector } from './ui/AttachInspector';
import { RailHost } from './ui/InspectorTabs';
import { LayerInspector, layerFromFiles } from './ui/DynamicInspector';
import { CheckDialog } from './ui/CheckDialog';
import { Welcome } from './ui/Welcome';
import { ExportModDialog } from './ui/ExportModDialog';
import { LeftRail } from './ui/LeftRail';
import { LiveryInspector } from './ui/LiveryPanel';
import { nativeFiles, SkinInspector, SoundInspector } from './ui/SkinInspector';
import { Stage } from './ui/Stage';
import { TopBar } from './ui/TopBar';
import { focusSelection, setView, useViewStore } from './viewport/Viewport';
import { partLabel, t, useLang } from './i18n';

function StatusBar({ onLog }: { onLog: () => void }) {
  const busy = useStore((s) => s.busy);
  // Backend progress only matters while the user waits on something (thumbnails load quietly in the background).
  const progress = useStore((s) => (s.busy || s.previewLoading ? s.progress : null));
  const cache = useStore((s) => s.cache);
  const update = useStore((s) => s.update);
  const updateReady = useStore((s) => s.updateReady);
  const undo = useStore((s) => s.undo.length);
  const redo = useStore((s) => s.redo.length);
  const { undoEdit, redoEdit } = useStore.getState();
  const projectPath = useStore((s) => s.projectPath);
  const down = useStore((s) => s.backendDown);
  const saveError = useStore((s) => s.autosaveError);
  // Only plain status here; backend log lines stay in the log panel.
  const text = down ? t('app.k1') : (progress?.text ?? busy ?? t('app.k2'));
  const developer = useStore((s) => s.prefs.developer);
  return (
    <footer className="status">
      {developer && (
        <Tooltip label={t('app.k3')}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={onLog} aria-label={t('app.k3')}>
            <IconFileText size={14} />
          </ActionIcon>
        </Tooltip>
      )}
      <span className="grow">{text}</span>
      {progress && <Progress w={120} size="xs" value={(progress.done / Math.max(1, progress.total)) * 100} />}
      {update && (
        <Group gap={6} wrap="nowrap">
          <span>{update.total ? t('app.updating', { v0: Math.floor((update.received / update.total) * 100) }) : t('app.updatingUnknown')}</span>
          <Progress w={120} size="xs" value={update.total ? (update.received / update.total) * 100 : 100} animated={!update.total} striped={!update.total} />
        </Group>
      )}
      {updateReady && (
        <Tooltip label={t('app.updateReady')}>
          <Button size="compact-xs" color="flare" onClick={() => void window.forge.restartToUpdate()}>
            {t('app.restartUpdate')}
          </Button>
        </Tooltip>
      )}
      {cache && (
        <Tooltip label={t('app.k4')}>
          <Group gap={6} wrap="nowrap">
            <span>
              {t('app.k5')} {cache.done}/{cache.total}
            </span>
            <Progress w={90} size="xs" color="gray" value={(cache.done / Math.max(1, cache.total)) * 100} />
          </Group>
        </Tooltip>
      )}
      {saveError ? (
        <Tooltip label={saveError}>
          <span style={{ color: 'var(--mantine-color-red-4)' }}>{t('app.k6')}</span>
        </Tooltip>
      ) : (
        <span>{projectPath ? projectPath.split(/[\\/]/).pop() : t('app.k7')}</span>
      )}
      <Tooltip label={t('app.undo')}>
        <ActionIcon variant="subtle" size="sm" disabled={!undo} onClick={undoEdit} aria-label={t('app.undo')}>
          <IconArrowBackUp size={15} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t('app.redo')}>
        <ActionIcon variant="subtle" size="sm" disabled={!redo} onClick={redoEdit} aria-label={t('app.redo')}>
          <IconArrowForwardUp size={15} />
        </ActionIcon>
      </Tooltip>
    </footer>
  );
}

function LogDrawer({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const logs = useStore((s) => s.logs);
  const dataDir = useStore((s) => s.dataDir);
  return (
    <Drawer opened={opened} onClose={onClose} position="bottom" size={300} title={t('app.k3')} withOverlay={false}>
      <Group gap="xs" mb="xs">
        <Button variant="default" onClick={() => void window.forge.openPath(`${dataDir}\\logs`)}>
          {t('app.k11')}
        </Button>
        <Button variant="default" onClick={() => void navigator.clipboard.writeText(logs.join('\n'))}>
          {t('app.k12')}
        </Button>
      </Group>
      <ScrollArea h={190}>
        <pre className="log">{logs.join('\n') || t('app.k13')}</pre>
      </ScrollArea>
    </Drawer>
  );
}

/** Dropping a folder or files anywhere matches textures; a project file opens. */
function onGlobalDrop(files: File[]) {
  const paths = files.map((f) => window.forge.pathOf(f)).filter(Boolean);
  if (!paths.length) return;
  const s = useStore.getState();
  const project = paths.find((p) => /\.(forgeskinproj|gtfoskin|gtfdyn)$/i.test(p));
  if (project) return void s.loadProject(project);
  if (paths.some((p) => /\.(glb|gltf)$/i.test(p))) return s.toast('info', t('app.k14'));
  // A video or gif dropped while a part is selected becomes a dynamic layer on it (still images stay textures).
  const part = s.selection?.kind === 'part' ? s.catalog.find((c) => c.id === s.weaponId)?.parts.find((p) => p.id === s.selection!.id) : undefined;
  const moving = paths.filter((p) => /\.(mp4|webm|mov|m4v|gif)$/i.test(p));
  if (part?.materials[0] && moving.length && layerFromFiles(part.materials[0], part.id, moving)) return s.toast('ok', t('app.droppedLayer', { v0: partLabel(part) }));
  void s.matchPaths(paths);
}

function useShortcuts() {
  const view = useViewStore();
  const s = useStore.getState;
  const phys = { preventDefault: true, usePhysicalKeys: true };
  useHotkeys([
    ['mod+z', () => s().undoEdit()],
    ['mod+y', () => s().redoEdit()],
    ['mod+shift+z', () => s().redoEdit()],
    ['mod+s', () => void s().saveProject()],
    ['mod+shift+s', () => void s().saveProject(true)],
    ['mod+o', () => void openProject()],
    ['mod+Enter', () => void applyToGame()],
    ['f', focusSelection],
    ['numpad1', () => setView('front'), phys],
    ['ctrl+numpad1', () => setView('back'), phys],
    ['numpad3', () => setView('right'), phys],
    ['ctrl+numpad3', () => setView('left'), phys],
    ['numpad7', () => setView('top'), phys],
    ['ctrl+numpad7', () => setView('bottom'), phys],
    ['numpad5', () => view.set({ ortho: !view.ortho }), phys],
    ['numpad0', focusSelection, phys],
    [
      'Delete',
      () => {
        const sel = s().selection;
        if (sel?.kind === 'attachment') s().removeAttachment(sel.id);
      },
    ],
    ['Escape', () => void (s().snapToSurface && useStore.setState({ snapToSurface: false }))],
  ]);
}

/** Keeps the 游戏运行中 badge live: asks every 2 s while the window has focus, and right away when it gets focus. */
function useGameRunning() {
  useGameWarnings();
  useEffect(() => {
    const check = async () => {
      const s = useStore.getState();
      if (!document.hasFocus() || !s.game?.found || s.backendDown) return;
      const game = await call('game.locate', {}).catch(() => null);
      if (game && game.running !== useStore.getState().game?.running) useStore.setState({ game });
    };
    const id = setInterval(() => void check(), 2000);
    window.addEventListener('focus', check);
    return () => {
      clearInterval(id);
      window.removeEventListener('focus', check);
    };
  }, []);
}

/** Keeps the chosen profile's plugin current: on start, on a profile change or refresh, and once the game closes. */
function useAutoPlugin() {
  const profile = useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));
  const running = useStore((s) => !!s.game?.running);
  const due = !!profile?.hasBepInEx && profile.pluginCurrent === false;
  useEffect(() => {
    if (due) void ensurePlugin(true);
  }, [due, profile?.path, running]);
}

/** The right panel shows whatever is selected; with nothing selected, the skin as a whole. */
function Inspector() {
  const kind = useStore((s) => s.selection?.kind);
  if (kind === 'attachment') return <AttachInspector />;
  if (kind === 'layer') return <LayerInspector />;
  if (kind === 'livery') return <LiveryInspector />;
  if (kind === 'sound') return <SoundInspector />;
  return <SkinInspector />;
}

/** 自动应用: while the game runs, re-apply two seconds after the last change. Asked during a build, it runs once that build
 * is done (applyToGame queues it); other busy steps end in an edit, which starts the wait again. */
function useAutoApply() {
  const doc = useStore((s) => s.doc);
  const on = useStore((s) => s.prefs.autoApply && !!s.game?.running);
  const opened = useRef(doc.id);
  useEffect(() => {
    // Opening another skin or item is not an edit.
    if (opened.current !== doc.id) return void (opened.current = doc.id);
    if (!on) return;
    const id = setTimeout(() => {
      if (!useStore.getState().previewLoading) void applyToGame(true);
    }, 2000);
    return () => clearTimeout(id);
  }, [doc, on]);
}

export function App() {
  useLang(); // the whole tree re-renders in the new language
  const [log, setLog] = useState(false);
  const layout = useDefaultLayout({ id: 'sf.layout', storage: localStorage });
  const [rail, setRail] = useState<HTMLElement | null>(null);
  const developer = useStore((s) => s.prefs.developer);
  useShortcuts();
  useEffect(() => void useStore.getState().init(), []);
  useGameRunning();
  useAutoPlugin();
  useAutoApply();
  return (
    <div className="shell">
      <TopBar />
      <PanelGroup className="panes" orientation="horizontal" {...layout}>
        <Panel id="left" defaultSize={300} minSize={240} maxSize={460}>
          <LeftRail />
          <ExportModDialog />
          <CheckDialog />
          <Welcome />
        </Panel>
        <Separator className="sep-v" />
        <Panel id="stage" minSize={400}>
          <Stage />
        </Panel>
        <Separator className="sep-v" />
        <Panel id="right" defaultSize={360} minSize={300} maxSize={560}>
          <div className="inspector-pane">
            <aside className="inspector">
              <RailHost.Provider value={rail}>
                <Inspector />
              </RailHost.Provider>
            </aside>
            <nav className="insp-rail" ref={setRail} />
          </div>
        </Panel>
      </PanelGroup>
      <StatusBar onLog={() => setLog(!log)} />
      <LogDrawer opened={log && developer} onClose={() => setLog(false)} />
      <Dropzone.FullScreen onDrop={onGlobalDrop} getFilesFromEvent={nativeFiles} activateOnClick={false} zIndex={300}>
        <div className="drop-veil">
          <IconFolderDown size={44} stroke={1.3} color="var(--flare)" />
          <b>{t('app.k15')}</b>
          <Text c="dimmed">{t('app.k16')}</Text>
        </div>
      </Dropzone.FullScreen>
    </div>
  );
}
