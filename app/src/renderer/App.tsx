import { ActionIcon, Button, Drawer, Group, Progress, ScrollArea, Text, Tooltip } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { useHotkeys } from '@mantine/hooks';
import { IconFileText, IconFolderDown } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { Group as PanelGroup, Panel, Separator, useDefaultLayout } from 'react-resizable-panels';
import { applyToGame } from './actions';
import { useStore } from './store';
import { AttachInspector } from './ui/AttachInspector';
import { DynamicInspector } from './ui/DynamicInspector';
import { LeftRail } from './ui/LeftRail';
import { nativeFiles, SkinInspector } from './ui/SkinInspector';
import { Stage } from './ui/Stage';
import { TopBar } from './ui/TopBar';
import { focusSelection, setView, useViewStore } from './viewport/Viewport';

function StatusBar({ onLog }: { onLog: () => void }) {
  const busy = useStore((s) => s.busy);
  // Backend progress only matters while the user waits on something (thumbnails load quietly in the background).
  const progress = useStore((s) => (s.busy || s.previewLoading ? s.progress : null));
  const lastLog = useStore((s) => s.logs.at(-1));
  const undo = useStore((s) => s.undo.length);
  const projectPath = useStore((s) => s.projectPath);
  const down = useStore((s) => s.backendDown);
  const text = down ? '后台服务重启中…' : (progress?.text ?? busy ?? lastLog?.replace(/^\[[^\]]+\] /, '') ?? '就绪');
  return (
    <footer className="status">
      <Tooltip label="日志">
        <ActionIcon variant="subtle" color="gray" size="sm" onClick={onLog} aria-label="日志">
          <IconFileText size={14} />
        </ActionIcon>
      </Tooltip>
      <span className="grow">{text}</span>
      {progress && <Progress w={120} size="xs" value={(progress.done / Math.max(1, progress.total)) * 100} />}
      <span>{projectPath ? projectPath.split(/[\\/]/).pop() : '已自动保存'}</span>
      <Tooltip label="Ctrl+Z 撤销 / Ctrl+Y 重做">
        <span>可撤销 {undo} 步</span>
      </Tooltip>
    </footer>
  );
}

function LogDrawer({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const logs = useStore((s) => s.logs);
  const dataDir = useStore((s) => s.dataDir);
  return (
    <Drawer opened={opened} onClose={onClose} position="bottom" size={300} title="日志" withOverlay={false}>
      <Group gap="xs" mb="xs">
        <Button variant="default" onClick={() => void window.forge.openPath(`${dataDir}\\logs`)}>
          打开日志文件夹
        </Button>
        <Button variant="default" onClick={() => void navigator.clipboard.writeText(logs.join('\n'))}>
          复制
        </Button>
      </Group>
      <ScrollArea h={190}>
        <pre className="log">{logs.join('\n') || '暂无日志'}</pre>
      </ScrollArea>
    </Drawer>
  );
}

/** Dropping a folder or files anywhere matches textures; a project file opens. */
function onGlobalDrop(files: File[]) {
  const paths = files.map((f) => window.forge.pathOf(f)).filter(Boolean);
  if (!paths.length) return;
  const s = useStore.getState();
  const project = paths.find((p) => /\.(sfproj|gtfoskin|gtfdyn)$/i.test(p));
  if (project) return void s.loadProject(project);
  if (paths.some((p) => /\.(glb|gltf)$/i.test(p))) return s.toast('info', '要导入模型，请切到「挂件与屏幕」，点「导入模型」。');
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
    ['mod+o', () => void window.forge.openDialog('project').then((p) => void (p[0] && s().loadProject(p[0])))],
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

export function App() {
  const mode = useStore((s) => s.mode);
  // A selected attachment opens its own inspector in every mode.
  const attachment = useStore((s) => s.selection?.kind === 'attachment');
  const [log, setLog] = useState(false);
  const layout = useDefaultLayout({ id: 'sf.layout', storage: localStorage });
  useShortcuts();
  useEffect(() => void useStore.getState().init(), []);
  return (
    <div className="shell">
      <TopBar />
      <PanelGroup className="panes" orientation="horizontal" {...layout}>
        <Panel id="left" defaultSize={300} minSize={240} maxSize={460}>
          <LeftRail />
        </Panel>
        <Separator className="sep-v" />
        <Panel id="stage" minSize={400}>
          <Stage />
        </Panel>
        <Separator className="sep-v" />
        <Panel id="right" defaultSize={360} minSize={300} maxSize={560}>
          <aside className="inspector">{mode === 'attach' || attachment ? <AttachInspector /> : mode === 'skin' ? <SkinInspector /> : <DynamicInspector />}</aside>
        </Panel>
      </PanelGroup>
      <StatusBar onLog={() => setLog(!log)} />
      <LogDrawer opened={log} onClose={() => setLog(false)} />
      <Dropzone.FullScreen onDrop={onGlobalDrop} getFilesFromEvent={nativeFiles} activateOnClick={false} zIndex={300}>
        <div className="drop-veil">
          <IconFolderDown size={44} stroke={1.3} color="var(--flare)" />
          <b>松手即可</b>
          <Text c="dimmed">贴图文件夹或图片会自动对应到部件；工程文件会直接打开</Text>
        </div>
      </Dropzone.FullScreen>
    </div>
  );
}
