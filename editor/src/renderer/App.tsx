import { ActionIcon, Button, Drawer, Group, Progress, ScrollArea, Text, Tooltip } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { useHotkeys } from '@mantine/hooks';
import { IconArrowBackUp, IconArrowForwardUp, IconFileText, IconFolderDown, IconListNumbers, IconListTree, IconStack2 } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { Group as PanelGroup, Panel, Separator, useDefaultLayout } from 'react-resizable-panels';
import { applyToGame, ensurePlugin, importMesh, openProject } from './actions';
import { useGameWarnings } from './gameLog';
import { useProfile, useProfileSync } from './profile';
import { baseName, call } from './protocol';
import { forgetImage } from './frames';
import { forgetTexture } from './viewport/textures';
import { logText, useStore, watchHeld } from './store';
import { AttachInspector } from './ui/AttachInspector';
import { InspectorTabs, RailHost } from './ui/InspectorTabs';
import { CurrentWork, Library } from './ui/Library';
import { LayerInspector, layerFromFiles } from './ui/DynamicInspector';
import { CodeFxInspector } from './ui/CodeFxInspector';
import { CheckDialog } from './ui/CheckDialog';
import { Empty } from './ui/Features';
import { StickerDialog } from './ui/StickerDialog';
import { Welcome } from './ui/Welcome';
import { HelpDocs } from './ui/HelpDocs';
import { ExportModDialog } from './ui/ExportModDialog';
import { SkinsReport } from './ui/SkinsReport';
import { LeftRail } from './ui/LeftRail';
import { LiveryInspector } from './ui/LiveryPanel';
import { droppedFolders, IMAGE_EXT, nativeFiles, SkinInspector, SoundInspector } from './ui/SkinInspector';
import { Stage } from './ui/Stage';
import { TopBar } from './ui/TopBar';
import { focusSelection, setView, useViewStore } from './viewport/Viewport';
import { liveryViewReady } from './viewport/liveryGpu';
import { CharacterInspector, CharacterSteps } from './character/CharacterPanels';
import { CharacterStage } from './character/CharacterStage';
import { importModel, isCharacterModel } from './character/model';
import { useCharacter } from './character/state';
import { partLabel, t, useLang } from './i18n';

function StatusBar({ onLog }: { onLog: () => void }) {
  const busy = useStore((s) => s.busy);
  // Backend progress only matters while the user waits on something (thumbnails load quietly in the background).
  const progress = useStore((s) => (s.busy || s.previewLoading ? s.progress : null));
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
  const profile = useProfile();
  const running = useStore((s) => !!s.game?.running);
  return (
    <footer className="status">
      <Tooltip label={t('app.k3')}>
        <ActionIcon variant="subtle" color="gray" size="sm" onClick={onLog} aria-label={t('app.k3')} data-tour="log">
          <IconFileText size={14} />
        </ActionIcon>
      </Tooltip>
      {profile?.pluginVersion && (
        <span className={`hot ${running ? 'live' : ''}`} data-tour="hot-reload">
          {running ? t('app.hotLive', { v0: profile.name }) : t('app.hotIdle', { v0: profile.name })}
        </span>
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
  const logs = useStore((s) => s.logs).map(logText);
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

const IMAGE = new RegExp(`\\.(${IMAGE_EXT.join('|')})$`, 'i');

/** Dropping anywhere: a project file opens, a model goes to the tab it belongs to, images and folders are matched to parts, and
 *  any other file is refused by name. */
function onGlobalDrop(files: File[]) {
  const dropped = files.map((f) => ({ path: window.forge.pathOf(f), folder: droppedFolders.has(f) })).filter((d) => d.path);
  if (!dropped.length) return;
  const paths = dropped.map((d) => d.path);
  const s = useStore.getState();
  const project = paths.find((p) => /\.(forgeskinproj|gtfoskin|gtfdyn)$/i.test(p));
  if (project) return void s.loadProject(project);
  const model = paths.find((p) => /\.(glb|gltf|vrm)$/i.test(p));
  if (model) return void dropModel(model);
  // A video or gif dropped while a part is selected becomes a dynamic layer on it (still images stay textures).
  const part = s.selection?.kind === 'part' ? s.catalog.find((c) => c.id === s.weaponId)?.parts.find((p) => p.id === s.selection!.id) : undefined;
  const moving = paths.filter((p) => /\.(mp4|webm|mov|m4v|gif)$/i.test(p));
  if (part?.materials[0] && moving.length && layerFromFiles(part.materials[0], part.id, moving)) return s.toast('ok', t('app.droppedLayer', { v0: partLabel(part) }));
  const textures = dropped.filter((d) => d.folder || IMAGE.test(d.path)).map((d) => d.path);
  if (!textures.length) return s.toast('error', t('errors.unsupportedFile'));
  void s.matchPaths(textures);
}

/** A dropped model opens in the tab it belongs to: a character (a .vrm, or a .glb/.gltf with VRM data or a whole humanoid skeleton) as
 *  the character to import, anything else as an attachment on the weapon. A file that cannot be read stays with the open tab. */
async function dropModel(path: string) {
  const character = /\.vrm$/i.test(path) || (await isCharacterModel(path));
  const mode = character === null ? useCharacter.getState().mode : character ? 'character' : 'weapon';
  useCharacter.setState({ mode });
  if (mode === 'character') return importModel(path);
  if (!useStore.getState().weaponId) return useStore.getState().toast('info', t('app.modelNeedsWeapon'));
  // Coming from the character tab the weapon view mounts again: the new attachment is selected only once the view is up.
  await liveryViewReady().catch(() => undefined);
  await importMesh(false, path);
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

/** The right panel shows whatever is selected; with nothing selected, the skin as a whole; with no work open, only how to get one. */
function Inspector() {
  const kind = useStore((s) => s.selection?.kind);
  const open = useStore((s) => !!s.weaponId);
  if (!open) return <Empty>{t('app.nothingOpen')}</Empty>;
  if (kind === 'attachment') return <AttachInspector />;
  if (kind === 'layer') return <LayerInspector />;
  if (kind === 'livery') return <LiveryInspector />;
  if (kind === 'sound') return <SoundInspector />;
  if (kind === 'fx') return <CodeFxInspector />;
  return <SkinInspector />;
}

/** 自动应用 (a setting): while the game runs, the work in use in the chosen profile is applied again two seconds after the last
 * change; a work not in use waits for its 应用. Asked during a build, it runs once that build is done (applyToGame queues it);
 * other busy steps end in an edit, which starts the wait again. */
function useAutoApply() {
  const doc = useStore((s) => s.doc);
  const fileEpoch = useStore((s) => s.fileEpoch);
  const on = useStore((s) => s.prefs.autoApply && !!s.game?.running && s.installed.some((p) => p.id === s.doc.id && p.inUse));
  const opened = useRef(doc.id);
  const seen = useRef({ doc, fileEpoch });
  useEffect(() => {
    // Only an edit (the document or a file it uses) applies: not opening another skin, nor the work coming into use (its 应用).
    const edited = seen.current.doc !== doc || seen.current.fileEpoch !== fileEpoch;
    seen.current = { doc, fileEpoch };
    if (opened.current !== doc.id) return void (opened.current = doc.id);
    if (!on || !edited) return;
    const id = setTimeout(() => {
      if (!useStore.getState().previewLoading) void applyToGame(true);
    }, 2000);
    return () => clearTimeout(id);
  }, [doc, on, fileEpoch]);
}

/** Watches the files the skin uses; one written by another program (Photoshop, Substance Painter) is read again. */
function useWatchedFiles() {
  const doc = useStore((s) => s.doc);
  useEffect(
    () =>
      window.forge.onFilesChanged((paths) => {
        paths.forEach((p) => (forgetTexture(p), forgetImage(p)));
        const s = useStore.getState();
        s.log(t('app.filesChanged', { v0: paths.map(baseName).join(', ') }));
        useStore.setState({ fileEpoch: s.fileEpoch + 1, unreadable: s.unreadable.filter((p) => !paths.includes(p)) });
      }),
    [],
  );
  useEffect(() => {
    const id = setTimeout(() => {
      if (useStore.getState().backendDown || watchHeld) return;
      void call('project.files', { project: doc }).then(
        ({ files }) => window.forge.watchFiles(files),
        () => undefined,
      );
    }, 800);
    return () => clearTimeout(id);
  }, [doc]);
}

/** The left panel: two side tabs on the window's edge, the works (profile, category, list) and the open work's structure (parts, or a
 * character's steps), each the panel's full height; the open work's name stays on top of both. */
function LeftColumn({ character }: { character: boolean }) {
  const [rail, setRail] = useState<HTMLElement | null>(null);
  const count = useStore((s) => (s.weaponId ? (s.works[s.weaponId]?.length ?? 0) : 0));
  const characters = useStore((s) => s.characters.length);
  return (
    <div className="inspector-pane">
      <nav className="insp-rail left" ref={setRail} />
      <div className="left-body">
        <CurrentWork />
        <RailHost.Provider value={rail}>
          <InspectorTabs
            id="left"
            side="left"
            tabs={[
              { id: 'works', label: t('works.tab'), icon: IconStack2, count: character ? characters : count, content: <Library /> },
              character
                ? { id: 'parts', label: t('works.tabSteps'), icon: IconListNumbers, content: <CharacterSteps /> }
                : { id: 'parts', label: t('works.tabParts'), icon: IconListTree, content: <LeftRail /> },
            ]}
          />
        </RailHost.Provider>
      </div>
    </div>
  );
}

export function App() {
  useLang(); // the whole tree re-renders in the new language
  const [log, setLog] = useState(false);
  const layout = useDefaultLayout({ id: 'sf.layout', storage: localStorage });
  const [rail, setRail] = useState<HTMLElement | null>(null);
  const character = useCharacter((s) => s.mode === 'character');
  useShortcuts();
  useEffect(() => void useStore.getState().init(), []);
  useGameRunning();
  useAutoPlugin();
  useProfileSync();
  useAutoApply();
  useWatchedFiles();
  // The whole window takes dropped files; a target inside it (a texture slot) takes its own first. The veil only shows
  // and never catches the drop, so the target under the pointer gets it.
  return (
    <Dropzone className="app-drop" unstyled onDrop={onGlobalDrop} getFilesFromEvent={nativeFiles} activateOnClick={false} activateOnKeyboard={false} enablePointerEvents>
      <div className="shell">
        <TopBar />
        <PanelGroup className="panes" orientation="horizontal" {...layout}>
          <Panel id="left" defaultSize={330} minSize={260} maxSize={520}>
            <LeftColumn character={character} />
            <ExportModDialog />
            <SkinsReport />
            <CheckDialog />
            <StickerDialog />
            <Welcome />
            <HelpDocs />
          </Panel>
          <Separator className="sep-v" />
          <Panel id="stage" minSize={400}>
            {character ? <CharacterStage /> : <Stage />}
          </Panel>
          <Separator className="sep-v" />
          <Panel id="right" defaultSize={360} minSize={320} maxSize={560}>
            <div className="inspector-pane">
              <aside className="inspector">
                {character ? (
                  <CharacterInspector />
                ) : (
                  <RailHost.Provider value={rail}>
                    <Inspector />
                  </RailHost.Provider>
                )}
              </aside>
              <nav className="insp-rail" ref={setRail} />
            </div>
          </Panel>
        </PanelGroup>
        <StatusBar onLog={() => setLog(!log)} />
        <LogDrawer opened={log} onClose={() => setLog(false)} />
      </div>
      <Dropzone.Accept>
        <div className="drop-host">
          <div className="drop-veil">
            <IconFolderDown size={44} stroke={1.3} color="var(--flare)" />
            <b>{t('app.k15')}</b>
            <Text c="dimmed">{t('app.k16')}</Text>
          </div>
        </div>
      </Dropzone.Accept>
    </Dropzone>
  );
}
