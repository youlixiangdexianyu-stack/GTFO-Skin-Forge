import { ActionIcon, Badge, Button, Checkbox, Group, Menu, Modal, SegmentedControl, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
import {
  IconBolt,
  IconBox,
  IconChecklist,
  IconChevronDown,
  IconDeviceFloppy,
  IconEraser,
  IconFolder,
  IconFolderOpen,
  IconFolderPlus,
  IconPackage,
  IconPackageExport,
  IconRefresh,
  IconStack2,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { applyToGame, exportObj, exportPack, openProject, profileProblem } from '../actions';
import { detail, friendly } from '../errors';
import { call, type BackendError, type InstalledPack, type SizeMode, type VerifyResult } from '../protocol';
import { useStore, type Mode } from '../store';
import { localName, t, useLang, type Lang } from '../i18n';

const MODES: { value: Mode; label: string }[] = [
  { value: 'skin', get label() { return t('topBar.k1'); } },
  { value: 'attach', get label() { return t('topBar.k2'); } },
  { value: 'dynamic', get label() { return t('topBar.k3'); } },
];

const SIZES: { value: SizeMode; label: string }[] = [
  { value: 'keep', get label() { return t('topBar.k4'); } },
  { value: 'original', get label() { return t('topBar.k5'); } },
  { value: '1k', label: '1K' },
  { value: '2k', label: '2K' },
  { value: '4k', label: '4K' },
];

function GamePill() {
  const game = useStore((s) => s.game);
  const { refreshGame, chooseGameDir } = useStore.getState();
  const state = !game ? t('topBar.k6') : !game.found ? t('topBar.k7') : game.running ? t('topBar.k8') : t('topBar.k9');
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
          {t('topBar.k10')}
        </Menu.Item>
        <Menu.Item leftSection={<IconFolder size={14} />} onClick={() => void chooseGameDir()}>
          {t('topBar.k11')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

const ADD_FOLDER = '__add__';

async function addProfileFolder() {
  const [folder] = await window.forge.openDialog('folder');
  if (!folder) return;
  const s = useStore.getState();
  try {
    const p = await call('profiles.add', { path: folder });
    await s.refreshProfiles();
    s.setPrefs({ profilePath: p.path });
  } catch (e) {
    s.fail(e);
  }
}

async function removeProfileFolder(path: string) {
  const s = useStore.getState();
  try {
    await call('profiles.remove', { path });
    await s.refreshProfiles();
  } catch (e) {
    s.fail(e);
  }
}

function ProfileSelect() {
  const profiles = useStore((s) => s.profiles);
  const path = useStore((s) => s.prefs.profilePath);
  const setPrefs = useStore((s) => s.setPrefs);
  const current = profiles.find((p) => p.path === path);
  const problem = profileProblem(current);
  return (
    <Tooltip label={problem ?? t('topBar.k12')} multiline w={problem ? 300 : undefined}>
      <Select
        w={210}
        value={current ? path : null}
        placeholder={t('topBar.k13')}
        error={!!problem}
        onChange={(v) => {
          if (v === ADD_FOLDER) void addProfileFolder();
          else if (v) setPrefs({ profilePath: v });
        }}
        data={[
          ...profiles.map((p) => ({ value: p.path, label: p.hasBepInEx ? p.name : t('topBar.k14', { v0: p.name }) })),
          { value: ADD_FOLDER, get label() { return t('topBar.k15'); } },
        ]}
        renderOption={({ option }) => {
          const p = profiles.find((x) => x.path === option.value);
          return (
            <Group gap={6} wrap="nowrap" w="100%">
              {option.value === ADD_FOLDER && <IconFolderPlus size={14} />}
              <Text size="sm" style={{ flex: 1 }} c={p && !p.hasBepInEx ? 'dimmed' : undefined}>
                {option.label}
              </Text>
              {p?.custom && (
                <ActionIcon
                  size="xs"
                  variant="subtle"
                  color="gray"
                  aria-label={t('topBar.k16')}
                  onMouseDown={(e) => (e.stopPropagation(), e.preventDefault(), void removeProfileFolder(p.path))}
                >
                  <IconX size={12} />
                </ActionIcon>
              )}
            </Group>
          );
        }}
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
    <Modal opened={opened} onClose={onClose} title={t('topBar.k17', { v0: profile?.name ?? t('topBar.noProfile') })} size="lg">
      {profile && (
        <Stack>
          <Group justify="space-between">
            <Text c="dimmed" size="sm">
              {t('topBar.k18')}{profile.pluginVersion ? t('topBar.k19', { v0: profile.pluginVersion }) : t('topBar.k20')}
            </Text>
            {profile.pluginVersion ? (
              <Button variant="default" color="red" onClick={() => act(() => call('plugin.uninstall', { profile: profile.path }), t('topBar.k21'))}>
                {t('topBar.k22')}
              </Button>
            ) : (
              <Button variant="default" onClick={() => act(() => call('plugin.install', { profile: profile.path }), t('topBar.k23'))}>
                {t('topBar.k24')}
              </Button>
            )}
          </Group>
          {packs === null && <Text c="dimmed">{t('topBar.k25')}</Text>}
          {packs?.length === 0 && <Text c="dimmed">{t('topBar.k26')}</Text>}
          {!!packs?.length && (
            <Table verticalSpacing="xs">
              <Table.Tbody>
                {packs.map((p) => {
                  const w = catalog.find((c) => c.id === p.weapon);
                  return (
                    <Table.Tr key={p.id}>
                      <Table.Td w={30}>
                        <Tooltip label={t('topBar.k27')}>
                          <Checkbox
                            checked={p.active}
                            onChange={(e) => act(() => call('pack.activate', { profile: profile.path, skinId: p.id, active: e.currentTarget.checked }), e.currentTarget.checked ? t('topBar.k28') : t('topBar.k29'))}
                          />
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{p.name}</Text>
                        <Text size="xs" c="dimmed">
                          {w ? localName(w) : p.weapon || t('topBar.k30')}
                        </Text>
                      </Table.Td>
                      <Table.Td w={40}>
                        <ActionIcon variant="subtle" color="red" onClick={() => act(() => call('pack.remove', { profile: profile.path, skinId: p.id }), t('topBar.k31'))} aria-label={t('topBar.k32')}>
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

/** Compares the game's own weapon assembly (read back by the plugin) with this app's preview, per weapon. */
function VerifyModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const profile = useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));
  const catalog = useStore((s) => s.catalog);
  const [rows, setRows] = useState<VerifyResult[] | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  useEffect(() => {
    if (!opened || !profile) return;
    setRows(null);
    setProblem(null);
    call('assembly.verify', { profile: profile.path }).then(setRows, (e: BackendError) => {
      useStore.getState().log(`assembly.verify ${detail(e)}`);
      setProblem(
        e?.code === 'not-found'
          ? t('topBar.k33')
          : friendly(e),
      );
    });
  }, [opened, profile?.path]); // eslint-disable-line react-hooks/exhaustive-deps
  const name = (r: VerifyResult) => {
    const w = catalog.find((c) => c.id === r.weapon);
    return w ? localName(w) : t('topBar.k34', { v0: r.gearId });
  };
  const why = (r: VerifyResult) => {
    if (r.pass) return t('topBar.k35', { v0: r.compared });
    const w = r.worst[0];
    if (!w) return t('topBar.k36');
    if (w.problem) return t('topBar.k37');
    return t('topBar.k38', { v0: w.positionMm.toFixed(1), v1: w.rotationDeg.toFixed(1) });
  };
  return (
    <Modal opened={opened} onClose={onClose} title={t('topBar.k39', { v0: profile?.name ?? t('topBar.noProfile') })} size="lg">
      {problem && <Text size="sm">{problem}</Text>}
      {!problem && rows === null && <Text c="dimmed">{t('topBar.k40')}</Text>}
      {rows?.length === 0 && <Text c="dimmed">{t('topBar.k41')}</Text>}
      {!!rows?.length && (
        <Table verticalSpacing="xs">
          <Table.Tbody>
            {rows.map((r) => (
              <Table.Tr key={r.file}>
                <Table.Td w={88}>
                  <Badge color={r.pass ? 'teal' : 'red'} variant="light" style={{ overflow: 'visible' }} miw={64}>
                    {r.pass ? t('topBar.k42') : t('topBar.k43')}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{name(r)}</Text>
                  <Text size="xs" c="dimmed">
                    {why(r)}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Modal>
  );
}

function ApplyButton() {
  const busy = useStore((s) => s.busy);
  const prefs = useStore((s) => s.prefs);
  const setPrefs = useStore((s) => s.setPrefs);
  const { saveProject, newProject, dataDir } = useStore.getState();
  const problem = useStore((s) => profileProblem(s.profiles.find((p) => p.path === s.prefs.profilePath)));
  const [installed, setInstalled] = useState(false);
  const [verify, setVerify] = useState(false);
  return (
    <>
      <Group gap={0} className="primary">
        <Tooltip label={problem ?? t('topBar.k44')} multiline w={problem ? 300 : undefined}>
          <Button
            size="sm"
            loading={!!busy}
            disabled={!!problem}
            leftSection={<IconBolt size={16} />}
            onClick={() => void applyToGame()}
            style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0 }}
          >
            {t('topBar.k45')}
          </Button>
        </Tooltip>
        <Menu position="bottom-end" width={290} closeOnItemClick>
          <Menu.Target>
            <Button size="sm" px={8} aria-label={t('topBar.k46')} style={{ borderTopLeftRadius: 0, borderBottomLeftRadius: 0, borderLeft: '1px solid rgba(0,0,0,.2)' }}>
              <IconChevronDown size={16} />
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconStack2 size={14} />} onClick={() => setInstalled(true)}>
              {t('topBar.k47')}
            </Menu.Item>
            <Menu.Item leftSection={<IconChecklist size={14} />} onClick={() => setVerify(true)}>
              {t('topBar.k48')}
            </Menu.Item>
            <Menu.Divider />
            <Menu.Item leftSection={<IconPackageExport size={14} />} onClick={() => void exportPack()}>
              {t('topBar.k49')}
            </Menu.Item>
            <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void exportObj('separate')}>
              {t('topBar.k50')}
            </Menu.Item>
            <Menu.Item leftSection={<IconPackage size={14} />} onClick={() => void exportObj('merged')}>
              {t('topBar.k51')}
            </Menu.Item>
            <Menu.Divider />
            <Menu.Label>{t('topBar.k52')}</Menu.Label>
            <div style={{ padding: '0 8px 8px' }}>
              <SegmentedControl fullWidth value={prefs.sizeMode} onChange={(v) => setPrefs({ sizeMode: v as SizeMode })} data={SIZES} />
            </div>
            <div style={{ padding: '2px 12px 10px' }}>
              <Checkbox size="xs" label={t('topBar.k53')} checked={prefs.allowMissing} onChange={(e) => setPrefs({ allowMissing: e.currentTarget.checked })} />
            </div>
            <Menu.Divider />
            <Menu.Item
              leftSection={<IconFolderOpen size={14} />}
              rightSection={<Text size="xs" c="dimmed">Ctrl O</Text>}
              onClick={() => void openProject()}
            >
              {t('topBar.k54')}
            </Menu.Item>
            <Menu.Item leftSection={<IconDeviceFloppy size={14} />} rightSection={<Text size="xs" c="dimmed">Ctrl S</Text>} onClick={() => void saveProject()}>
              {t('topBar.k55')}
            </Menu.Item>
            <Menu.Item leftSection={<IconDeviceFloppy size={14} />} onClick={() => void saveProject(true)}>
              {t('topBar.k56')}
            </Menu.Item>
            <Menu.Item leftSection={<IconEraser size={14} />} onClick={() => void newProject()}>
              {t('topBar.k57')}
            </Menu.Item>
            <Menu.Divider />
            <Menu.Item leftSection={<IconFolder size={14} />} onClick={() => void window.forge.openPath(dataDir)}>
              {t('topBar.k58')}
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>
      <InstalledModal opened={installed} onClose={() => setInstalled(false)} />
      <VerifyModal opened={verify} onClose={() => setVerify(false)} />
    </>
  );
}

/** 中文 / English; remembered in the settings, the first start follows the system. */
function LangSwitch() {
  const lang = useLang();
  const setPrefs = useStore((s) => s.setPrefs);
  return (
    <SegmentedControl
      size="xs"
      aria-label={t('topBar.language')}
      value={lang}
      onChange={(v) => setPrefs({ lang: v as Lang })}
      data={[
        { value: 'zh', label: t('lang.zh') },
        { value: 'en', label: t('lang.en') },
      ]}
    />
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
        <span>{t('topBar.k59')}</span>
      </div>
      <SegmentedControl value={mode} onChange={(v) => setMode(v as Mode)} data={MODES} size="sm" />
      <div className="spacer" />
      {game?.running && (
        <Badge color="flare" variant="light">
          {t('topBar.k60')}
        </Badge>
      )}
      <LangSwitch />
      <GamePill />
      <ProfileSelect />
      <ApplyButton />
    </header>
  );
}
