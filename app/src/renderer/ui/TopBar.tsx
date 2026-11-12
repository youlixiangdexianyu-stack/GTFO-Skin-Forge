import { ActionIcon, Badge, Button, Checkbox, Group, Menu, Modal, Popover, SegmentedControl, Stack, Switch, Table, Text, Tooltip } from '@mantine/core';
import {
  IconBolt,
  IconBox,
  IconWorldUpload,
  IconCheck,
  IconChecklist,
  IconChevronDown,
  IconCopy,
  IconDeviceFloppy,
  IconEraser,
  IconFolder,
  IconFolderOpen,
  IconFileImport,
  IconFileZip,
  IconFolderPlus,
  IconInfoCircle,
  IconMinus,
  IconPackage,
  IconPackageExport,
  IconRefresh,
  IconRestore,
  IconSettings,
  IconSquare,
  IconStack2,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { applyToGame, exportObj, exportPack, exportThunderstore, openProject, profileProblem } from '../actions';
import { detail, friendly } from '../errors';
import { call, type BackendError, type InstalledPack, type SizeMode, type VerifyResult } from '../protocol';
import { useStore, type Mode } from '../store';
import logo from '../../../assets/icon.png';
import { localName, t, useLang, type Lang } from '../i18n';
import { Info } from './Features';
import { exportGameFiles, ReplaceModal, RestoreModal } from './GameFiles';

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

/** Where 应用到游戏 goes: the game profile plus the game's state, as one chip right before the apply button. */
function TargetMenu() {
  const game = useStore((s) => s.game);
  const profiles = useStore((s) => s.profiles);
  const path = useStore((s) => s.prefs.profilePath);
  const pending = useStore((s) => !!s.pluginPending && s.pluginPending === s.prefs.profilePath);
  const { refreshGame, chooseGameDir, setPrefs } = useStore.getState();
  const current = profiles.find((p) => p.path === path);
  const problem = profileProblem(current);
  const state = !game ? t('topBar.k6') : !game.found ? t('topBar.k7') : game.running ? t('topBar.k8') : t('topBar.k9');
  const gameDot = !game ? '' : !game.found ? 'bad' : game.running ? 'live' : 'ok';
  const dot = pending ? 'warn' : problem ? 'bad' : gameDot;
  const color = pending ? 'yellow' : !game ? 'gray' : !game.found || problem ? 'red' : game.running ? 'flare' : 'teal';
  const tip = [problem ?? t('topBar.k12'), state, pending ? t('topBar.pluginPending') : null].filter(Boolean).join(' · ');
  const [open, setOpen] = useState(false);
  return (
    <Menu position="bottom-end" width={280} opened={open} onChange={setOpen}>
      <Menu.Target>
        <Tooltip label={tip} multiline w={280} disabled={open}>
          <Button
            className="target"
            size="sm"
            variant="light"
            color={color}
            leftSection={<span className={`dot ${dot}`} />}
            rightSection={<IconChevronDown size={14} />}
          >
            <span className="target-name">{current?.name ?? t('topBar.k13')}</span>
          </Button>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t('topBar.k12')}</Menu.Label>
        {profiles.map((p) => (
          <Menu.Item
            key={p.path}
            leftSection={p.path === path ? <IconCheck size={14} /> : <span style={{ display: 'inline-block', width: 14 }} />}
            c={p.hasBepInEx ? undefined : 'dimmed'}
            onClick={() => setPrefs({ profilePath: p.path })}
            rightSection={
              p.custom && (
                <ActionIcon
                  component="span"
                  size="xs"
                  variant="subtle"
                  color="gray"
                  aria-label={t('topBar.k16')}
                  onClick={(e) => (e.stopPropagation(), void removeProfileFolder(p.path))}
                >
                  <IconX size={12} />
                </ActionIcon>
              )
            }
          >
            {p.hasBepInEx ? p.name : t('topBar.k14', { v0: p.name })}
          </Menu.Item>
        ))}
        <Menu.Item leftSection={<IconFolderPlus size={14} />} onClick={() => void addProfileFolder()}>
          {t('topBar.k15')}
        </Menu.Item>
        {pending && (
          <Text size="xs" c="yellow" px="sm" py={6}>
            {t('topBar.pluginPending')}
          </Text>
        )}
        <Menu.Divider />
        <Menu.Label>
          <Group gap={6} wrap="nowrap">
            <span className={`dot ${gameDot}`} />
            {state}
          </Group>
        </Menu.Label>
        {game?.gameDir && (
          <Text size="xs" c="dimmed" px="sm" pb={6} style={{ userSelect: 'text', wordBreak: 'break-all' }}>
            {game.gameDir}
          </Text>
        )}
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

/** Settings that change rarely: language, the plugin's development mode for the chosen profile, the data folder. */
function SettingsMenu() {
  const lang = useLang();
  const profile = useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));
  const { setPrefs, dataDir } = useStore.getState();
  const setDevelopment = async (on: boolean) => {
    const s = useStore.getState();
    if (!profile) return;
    try {
      const r = await call('plugin.setDevelopment', { profile: profile.path, on });
      await s.refreshProfiles();
      if (r.restartNeeded) s.toast('info', t('topBar.devRestart'));
    } catch (e) {
      s.fail(e);
    }
  };
  return (
    <Popover position="bottom-end" width={300} shadow="md">
      <Popover.Target>
        <Tooltip label={t('topBar.settings')}>
          <ActionIcon size={34} variant="subtle" color="gray" aria-label={t('topBar.settings')}>
            <IconSettings size={19} />
          </ActionIcon>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="md">
          <Stack gap={6}>
            <Text size="xs" c="dimmed">
              {t('topBar.language')}
            </Text>
            <SegmentedControl
              fullWidth
              size="xs"
              value={lang}
              onChange={(v) => setPrefs({ lang: v as Lang })}
              data={[
                { value: 'zh', label: t('lang.zh') },
                { value: 'en', label: t('lang.en') },
              ]}
            />
          </Stack>
          {profile?.hasBepInEx && (
            <Group gap={6} wrap="nowrap">
              <Switch
                size="sm"
                label={t('topBar.devLabel')}
                checked={!!profile.development}
                onChange={(e) => void setDevelopment(e.currentTarget.checked)}
                style={{ flex: 1 }}
              />
              <Tooltip label={t('topBar.devInfo')} multiline w={280}>
                <IconInfoCircle size={16} color="var(--scan)" style={{ flexShrink: 0 }} />
              </Tooltip>
            </Group>
          )}
          <Button variant="default" size="xs" leftSection={<IconFolder size={14} />} onClick={() => void window.forge.openPath(dataDir)}>
            {t('topBar.k58')}
          </Button>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}

/** Windows-style minimize / maximize-restore / close for the frameless window. */
function WindowControls() {
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    void window.forge.isMaximized().then(setMaximized);
    return window.forge.onMaximized(setMaximized);
  }, []);
  const size = maximized ? t('topBar.restore') : t('topBar.maximize');
  return (
    <div className="winctl">
      <button type="button" aria-label={t('topBar.minimize')} title={t('topBar.minimize')} onClick={() => window.forge.minimize()}>
        <IconMinus size={16} stroke={1.25} />
      </button>
      <button type="button" aria-label={size} title={size} onClick={() => window.forge.toggleMaximize()}>
        {maximized ? <IconCopy size={14} stroke={1.25} style={{ transform: 'scaleX(-1)' }} /> : <IconSquare size={13} stroke={1.25} />}
      </button>
      <button type="button" className="close" aria-label={t('topBar.close')} title={t('topBar.close')} onClick={() => window.forge.close()}>
        <IconX size={16} stroke={1.25} />
      </button>
    </div>
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
  const { saveProject, newProject } = useStore.getState();
  const problem = useStore((s) => profileProblem(s.profiles.find((p) => p.path === s.prefs.profilePath)));
  const [installed, setInstalled] = useState(false);
  const [verify, setVerify] = useState(false);
  const [replace, setReplace] = useState(false);
  const [restore, setRestore] = useState(false);
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
            <Menu.Item leftSection={<IconWorldUpload size={14} />} onClick={() => void exportThunderstore()}>
              {t('actions.exportMod')}
            </Menu.Item>
            <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void exportObj('separate')}>
              {t('topBar.k50')}
            </Menu.Item>
            <Menu.Item leftSection={<IconPackage size={14} />} onClick={() => void exportObj('merged')}>
              {t('topBar.k51')}
            </Menu.Item>
            <Menu.Divider />
            <Menu.Label>
              <Group gap={6} wrap="nowrap">
                {t('gameFiles.group')}
                <Info label={t('gameFiles.groupInfo')} />
              </Group>
            </Menu.Label>
            <Menu.Item leftSection={<IconFileImport size={14} />} onClick={() => setReplace(true)}>
              {t('gameFiles.replace')}
            </Menu.Item>
            <Menu.Item leftSection={<IconRestore size={14} />} onClick={() => setRestore(true)}>
              {t('gameFiles.restore')}
            </Menu.Item>
            <Menu.Item leftSection={<IconFileZip size={14} />} onClick={() => void exportGameFiles()}>
              {t('gameFiles.export')}
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
          </Menu.Dropdown>
        </Menu>
      </Group>
      <InstalledModal opened={installed} onClose={() => setInstalled(false)} />
      <VerifyModal opened={verify} onClose={() => setVerify(false)} />
      <ReplaceModal opened={replace} onClose={() => setReplace(false)} />
      <RestoreModal opened={restore} onClose={() => setRestore(false)} />
    </>
  );
}

export function TopBar() {
  const mode = useStore((s) => s.mode);
  const setMode = useStore((s) => s.setMode);
  return (
    <header className="topbar">
      <div className="brand">
        <img className="logo" src={logo} alt="" />
        <b>SKIN FORGE</b>
      </div>
      <SegmentedControl className="modes" value={mode} onChange={(v) => setMode(v as Mode)} data={MODES} size="sm" />
      <div className="spacer" />
      <SettingsMenu />
      <TargetMenu />
      <ApplyButton />
      <WindowControls />
    </header>
  );
}
