import { ActionIcon, Badge, Button, Checkbox, Group, Menu, Modal, SegmentedControl, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
import {
  IconBolt,
  IconBox,
  IconChevronDown,
  IconDeviceFloppy,
  IconEraser,
  IconFolder,
  IconFolderOpen,
  IconPackage,
  IconPackageExport,
  IconRefresh,
  IconStack2,
  IconTrash,
} from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { applyToGame, exportObj, exportPack } from '../actions';
import { call, type InstalledPack, type SizeMode } from '../protocol';
import { useStore, type Mode } from '../store';

const MODES: { value: Mode; label: string }[] = [
  { value: 'skin', label: '换皮' },
  { value: 'attach', label: '挂件与屏幕' },
  { value: 'dynamic', label: '动态贴图' },
];

const SIZES: { value: SizeMode; label: string }[] = [
  { value: 'keep', label: '保持' },
  { value: 'original', label: '原版' },
  { value: '1k', label: '1K' },
  { value: '2k', label: '2K' },
  { value: '4k', label: '4K' },
];

function GamePill() {
  const game = useStore((s) => s.game);
  const { refreshGame, chooseGameDir } = useStore.getState();
  const state = !game ? '正在查找游戏…' : !game.found ? '未找到游戏' : game.running ? '游戏运行中' : '已连接游戏';
  const dot = !game ? '' : !game.found ? 'bad' : game.running ? 'live' : 'ok';
  return (
    <Menu position="bottom-end" width={300}>
      <Menu.Target>
        <Button variant="default" radius="xl" leftSection={<span className={`dot ${dot}`} />}>
          {state}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        {game?.gameDir && <Menu.Label style={{ userSelect: 'text' }}>{game.gameDir}</Menu.Label>}
        <Menu.Item leftSection={<IconRefresh size={14} />} onClick={() => void refreshGame()}>
          重新检测
        </Menu.Item>
        <Menu.Item leftSection={<IconFolder size={14} />} onClick={() => void chooseGameDir()}>
          选择游戏文件夹…
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

function ProfileSelect() {
  const profiles = useStore((s) => s.profiles);
  const path = useStore((s) => s.prefs.profilePath);
  const setPrefs = useStore((s) => s.setPrefs);
  if (!profiles.length) return null;
  return (
    <Tooltip label="皮肤装到哪个游戏配置">
      <Select
        w={200}
        value={path}
        onChange={(v) => v && setPrefs({ profilePath: v, profileChosen: true })}
        data={profiles.map((p) => ({ value: p.path, label: p.hasBepInEx ? p.name : `${p.name}（未装 BepInEx）` }))}
      />
    </Tooltip>
  );
}

function InstalledModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const profile = useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));
  const catalog = useStore((s) => s.catalog);
  const { toast, fail, refreshProfiles } = useStore.getState();
  const [packs, setPacks] = useState<InstalledPack[] | null>(null);
  const load = async () => {
    if (!profile) return;
    try {
      setPacks(await call('pack.list', { profile: profile.path }));
    } catch (e) {
      fail(e);
      setPacks([]);
    }
  };
  useEffect(() => {
    if (opened) void load();
  }, [opened, profile?.path]); // eslint-disable-line react-hooks/exhaustive-deps
  const act = async (fn: () => Promise<unknown>, ok: string) => {
    try {
      await fn();
      toast('ok', ok);
      await refreshProfiles();
      await load();
    } catch (e) {
      fail(e);
    }
  };
  return (
    <Modal opened={opened} onClose={onClose} title={`已安装的皮肤 · ${profile?.name ?? '未选择配置'}`} size="lg">
      {profile && (
        <Stack>
          <Group justify="space-between">
            <Text c="dimmed" size="sm">
              皮肤插件：{profile.pluginVersion ? `已安装 ${profile.pluginVersion}` : '未安装'}
            </Text>
            {profile.pluginVersion ? (
              <Button variant="default" color="red" onClick={() => act(() => call('plugin.uninstall', { profile: profile.path }), '插件已卸载，游戏里的武器恢复原样')}>
                卸载插件
              </Button>
            ) : (
              <Button variant="default" onClick={() => act(() => call('plugin.install', { profile: profile.path }), '插件已安装')}>
                安装插件
              </Button>
            )}
          </Group>
          {packs === null && <Text c="dimmed">正在读取…</Text>}
          {packs?.length === 0 && <Text c="dimmed">这个配置里还没有皮肤。做好后点「应用到游戏」就会出现在这里。</Text>}
          {!!packs?.length && (
            <Table verticalSpacing="xs">
              <Table.Tbody>
                {packs.map((p) => {
                  const w = catalog.find((c) => c.id === p.weapon);
                  return (
                    <Table.Tr key={p.id}>
                      <Table.Td w={30}>
                        <Tooltip label="启用">
                          <Checkbox
                            checked={p.active}
                            onChange={(e) => act(() => call('pack.activate', { profile: profile.path, skinId: p.id, active: e.currentTarget.checked }), e.currentTarget.checked ? '已启用' : '已停用')}
                          />
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{p.name}</Text>
                        <Text size="xs" c="dimmed">
                          {w ? w.nameZh || w.name : p.weapon || '通用配件皮肤'}
                        </Text>
                      </Table.Td>
                      <Table.Td w={40}>
                        <ActionIcon variant="subtle" color="red" onClick={() => act(() => call('pack.remove', { profile: profile.path, skinId: p.id }), '已删除这个皮肤')} aria-label="删除">
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          )}
        </Stack>
      )}
    </Modal>
  );
}

function ApplyButton() {
  const busy = useStore((s) => s.busy);
  const prefs = useStore((s) => s.prefs);
  const setPrefs = useStore((s) => s.setPrefs);
  const { saveProject, loadProject, newProject, dataDir } = useStore.getState();
  const [installed, setInstalled] = useState(false);
  return (
    <>
      <Group gap={0} className="primary">
        <Tooltip label="安装插件（如需要）、打包并装进游戏；游戏开着也会自动刷新 · Ctrl+Enter">
          <Button size="sm" loading={!!busy} leftSection={<IconBolt size={16} />} onClick={() => void applyToGame()} style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0 }}>
            应用到游戏
          </Button>
        </Tooltip>
        <Menu position="bottom-end" width={290} closeOnItemClick>
          <Menu.Target>
            <Button size="sm" px={8} aria-label="更多" style={{ borderTopLeftRadius: 0, borderBottomLeftRadius: 0, borderLeft: '1px solid rgba(0,0,0,.2)' }}>
              <IconChevronDown size={16} />
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconStack2 size={14} />} onClick={() => setInstalled(true)}>
              已安装的皮肤…
            </Menu.Item>
            <Menu.Divider />
            <Menu.Item leftSection={<IconPackageExport size={14} />} onClick={() => void exportPack()}>
              导出皮肤包…
            </Menu.Item>
            <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void exportObj('separate')}>
              导出 OBJ 模型（分部件）…
            </Menu.Item>
            <Menu.Item leftSection={<IconPackage size={14} />} onClick={() => void exportObj('merged')}>
              导出 OBJ 模型（合并）…
            </Menu.Item>
            <Menu.Divider />
            <Menu.Label>输出贴图尺寸（始终保持比例）</Menu.Label>
            <div style={{ padding: '0 8px 8px' }}>
              <SegmentedControl fullWidth value={prefs.sizeMode} onChange={(v) => setPrefs({ sizeMode: v as SizeMode })} data={SIZES} />
            </div>
            <div style={{ padding: '2px 12px 10px' }}>
              <Checkbox size="xs" label="缺失的部件和通道保持原样" checked={prefs.allowMissing} onChange={(e) => setPrefs({ allowMissing: e.currentTarget.checked })} />
            </div>
            <Menu.Divider />
            <Menu.Item
              leftSection={<IconFolderOpen size={14} />}
              rightSection={<Text size="xs" c="dimmed">Ctrl O</Text>}
              onClick={async () => (await window.forge.openDialog('project')).forEach((p) => void loadProject(p))}
            >
              打开工程或旧版预设…
            </Menu.Item>
            <Menu.Item leftSection={<IconDeviceFloppy size={14} />} rightSection={<Text size="xs" c="dimmed">Ctrl S</Text>} onClick={() => void saveProject()}>
              保存工程
            </Menu.Item>
            <Menu.Item leftSection={<IconDeviceFloppy size={14} />} onClick={() => void saveProject(true)}>
              另存为…
            </Menu.Item>
            <Menu.Item leftSection={<IconEraser size={14} />} onClick={() => void newProject()}>
              清空当前改动
            </Menu.Item>
            <Menu.Divider />
            <Menu.Item leftSection={<IconFolder size={14} />} onClick={() => void window.forge.openPath(dataDir)}>
              打开数据文件夹
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>
      <InstalledModal opened={installed} onClose={() => setInstalled(false)} />
    </>
  );
}

export function TopBar() {
  const mode = useStore((s) => s.mode);
  const setMode = useStore((s) => s.setMode);
  const game = useStore((s) => s.game);
  return (
    <header className="topbar">
      <div className="brand">
        <b>SKIN FORGE</b>
        <span>GTFO 枪皮工坊</span>
      </div>
      <SegmentedControl value={mode} onChange={(v) => setMode(v as Mode)} data={MODES} size="sm" />
      <div className="spacer" />
      {game?.running && (
        <Badge color="flare" variant="light">
          改动会热更新到游戏里
        </Badge>
      )}
      <GamePill />
      <ProfileSelect />
      <ApplyButton />
    </header>
  );
}
