import { ActionIcon, Button, ColorInput, Group, Kbd, Loader, Paper, Popover, SegmentedControl, Slider, Stack, Stepper, Switch, Text, Title, Tooltip } from '@mantine/core';
import { IconArrowsMove, IconFocusCentered, IconFolder, IconMagnet, IconRotate, IconRotate360, IconSun, IconZoomScan } from '@tabler/icons-react';
import { useItem, useStore } from '../store';
import { categoryOf } from './LeftRail';
import { focusSelection, useModal, useViewStore, Viewport } from '../viewport/Viewport';

function FirstRun() {
  const game = useStore((s) => s.game);
  const ready = useStore((s) => s.ready);
  const weaponId = useStore((s) => s.weaponId);
  const choose = useStore((s) => s.chooseGameDir);
  const found = !!game?.found;
  if (ready && found && weaponId) return null;
  return (
    <div className="stage-empty">
      <Paper className="card" bg="transparent">
        {!ready ? (
          <Stack align="center">
            <Title order={2}>正在准备</Title>
            <Text c="dimmed">查找 GTFO 和武器列表…</Text>
            <Loader type="bars" size="sm" />
          </Stack>
        ) : (
          <Stack>
            <Title order={2}>{found ? '选一把枪' : '先找到游戏'}</Title>
            <Text c="dimmed">{found ? '从左边挑一把武器，它会出现在这里。' : '自动查找没有找到 GTFO。选一下游戏所在的文件夹（里面有 GTFO.exe）。'}</Text>
            <Stepper active={found ? 1 : 0} orientation="vertical" size="sm" color="flare">
              <Stepper.Step label="连接游戏" />
              <Stepper.Step label="选一把武器" />
              <Stepper.Step label="把 Substance Painter 导出的贴图文件夹拖进来" />
              <Stepper.Step label="点右上角「应用到游戏」" />
            </Stepper>
            {!found && (
              <Button size="sm" leftSection={<IconFolder size={16} />} onClick={() => void choose()}>
                选择游戏文件夹
              </Button>
            )}
          </Stack>
        )}
      </Paper>
    </div>
  );
}

function ViewBar() {
  const view = useViewStore();
  return (
    <Group className="overlay views" gap={4}>
      <Tooltip label="透视 / 正交（小键盘 5）。前后左右顶底：点右上角方块，或小键盘 1 3 7（加 Ctrl 看反面）">
        <SegmentedControl size="xs" value={view.ortho ? 'o' : 'p'} onChange={(v) => view.set({ ortho: v === 'o' })} data={[{ value: 'p', label: '透视' }, { value: 'o', label: '正交' }]} />
      </Tooltip>
      <Tooltip label="对准选中（F）">
        <ActionIcon variant="subtle" color="gray" onClick={focusSelection} aria-label="对准选中">
          <IconFocusCentered size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

function LightBar() {
  const prefs = useStore((s) => s.prefs);
  const setPrefs = useStore((s) => s.setPrefs);
  return (
    <Group className="overlay light" gap={4}>
      <Popover position="top-start" width={260} withArrow>
        <Popover.Target>
          <Tooltip label="灯光">
            <ActionIcon variant="subtle" color="gray" aria-label="灯光">
              <IconSun size={16} />
            </ActionIcon>
          </Tooltip>
        </Popover.Target>
        <Popover.Dropdown>
          <Stack gap="sm">
            <div>
              <Text size="xs" c="dimmed" mb={4}>
                环境光 {prefs.ambient.toFixed(2)}（调到 0 只看自发光）
              </Text>
              <Slider min={0} max={3} step={0.05} value={prefs.ambient} onChange={(v) => setPrefs({ ambient: v })} />
            </div>
            <ColorInput label="光色" value={prefs.ambientColor} onChange={(v) => setPrefs({ ambientColor: v })} />
            <Switch label="亮的发光像素会泛光" checked={prefs.bloom} onChange={(e) => setPrefs({ bloom: e.currentTarget.checked })} />
          </Stack>
        </Popover.Dropdown>
      </Popover>
      <Tooltip label="自动旋转">
        <ActionIcon variant={prefs.autoRotate ? 'light' : 'subtle'} color={prefs.autoRotate ? 'flare' : 'gray'} onClick={() => setPrefs({ autoRotate: !prefs.autoRotate })} aria-label="自动旋转">
          <IconRotate360 size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

function AttachTools() {
  const view = useViewStore();
  const mode = useStore((s) => s.mode);
  const snap = useStore((s) => s.snapToSurface);
  const selection = useStore((s) => s.selection);
  return (
    <Group className="overlay tools" gap={4}>
      <SegmentedControl
        size="xs"
        value={view.gizmo}
        onChange={(v) => view.set({ gizmo: v as 'translate' })}
        data={[
          { value: 'translate', label: <Group gap={4} wrap="nowrap"><IconArrowsMove size={14} />移动 <Kbd size="xs">G</Kbd></Group> },
          { value: 'rotate', label: <Group gap={4} wrap="nowrap"><IconRotate size={14} />旋转 <Kbd size="xs">R</Kbd></Group> },
          { value: 'scale', label: <Group gap={4} wrap="nowrap"><IconZoomScan size={14} />缩放 <Kbd size="xs">S</Kbd></Group> },
        ]}
      />
      {mode === 'attach' && <Tooltip label="打开后点击枪身，选中的挂件会贴到点击的位置">
        <Button
          variant={snap ? 'light' : 'subtle'}
          color={snap ? 'flare' : 'gray'}
          disabled={selection?.kind !== 'attachment'}
          leftSection={<IconMagnet size={14} />}
          onClick={() => useStore.setState({ snapToSurface: !snap })}
        >
          贴到表面
        </Button>
      </Tooltip>}
    </Group>
  );
}

function ModalHint() {
  const modal = useModal();
  const [tool, axis] = modal.split('|');
  if (tool === 'null') return null;
  const name = { move: '移动', rotate: '旋转', scale: '缩放' }[tool as 'move'];
  return (
    <div className="modal-hint">
      <b style={{ color: 'var(--flare)' }}>{name}</b>
      <span>轴：{axis === 'null' ? '自由' : <b className={`axis-${axis}`}>{axis.toUpperCase()}（部件坐标）</b>}</span>
      <Text span size="xs" c="dimmed">
        <Kbd>X</Kbd> <Kbd>Y</Kbd> <Kbd>Z</Kbd> 锁轴 · 单击/<Kbd>Enter</Kbd> 确认 · 右键/<Kbd>Esc</Kbd> 取消
      </Text>
    </div>
  );
}

export function Stage() {
  const item = useItem();
  const preview = useStore((s) => s.preview);
  const loading = useStore((s) => s.previewLoading);
  return (
    <main className="stage">
      <Viewport />
      {item && (
        <div className="stage-title">
          <div className="name">{item.name}</div>
          <div className="sub">
            {categoryOf(item)}
            {item.nameZh ? ` · ${item.nameZh}` : ''}
            {item.calibre ? ` · ${item.calibre}` : ''}
          </div>
        </div>
      )}
      {loading && (
        <div className="stage-empty">
          <Stack align="center" gap="xs">
            <Loader type="bars" size="sm" />
            <Text c="dimmed" ta="center">
              正在从游戏里读取模型和贴图…
              <br />
              第一次打开一把枪需要几秒钟。
            </Text>
          </Stack>
        </div>
      )}
      <FirstRun />
      {preview && (
        <>
          <ViewBar />
          <LightBar />
          <AttachTools />
          <ModalHint />
        </>
      )}
    </main>
  );
}
