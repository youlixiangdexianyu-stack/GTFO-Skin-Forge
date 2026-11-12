import { ActionIcon, Badge, Button, Checkbox, Group, Menu, Modal, Popover, SegmentedControl, Stack, Switch, Table, Text, Tooltip } from '@mantine/core';
import {
  IconBolt,
  IconBox,
  IconCheck,
  IconChecklist,
  IconChevronDown,
  IconCopy,
  IconFileImport,
  IconFileText,
  IconFileZip,
  IconFolder,
  IconFolderPlus,
  IconListCheck,
  IconMinus,
  IconPackage,
  IconPackageExport,
  IconPlug,
  IconPlugOff,
  IconHelp,
  IconRefresh,
  IconRestore,
  IconSend,
  IconSettings,
  IconSquare,
  IconStack2,
  IconTrash,
  IconWorldUpload,
  IconX,
} from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { applyToGame, exportObj, exportPack, exportThunderstore, profileProblem } from '../actions';
import { detail, friendly } from '../errors';
import { call, type BackendError, type InstalledPack, type SizeMode, type VerifyResult } from '../protocol';
import { useStore } from '../store';
import logo from '../../../assets/icon.png';
import { localName, t, useLang, type Lang } from '../i18n';
import { Info } from './Features';
import { exportGameFiles, ReplaceModal, RestoreModal } from './GameFiles';
import { GameLog } from './GameLog';


const SIZES: { value: SizeMode; label: string }[] = [
  { value: 'keep', get label() { return t('topBar.k4'); } },
  { value: 'original', get label() { return t('topBar.k5'); } },
  { value: '1k', label: '1K' },
  { value: '2k', label: '2K' },
  { value: '4k', label: '4K' },
];

async function addProfileFolder() {
  const [folder] = await window.forge.openDialog('folder', undefined, 'profile');
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
  const tip = [t('topBar.testProfile'), problem ?? current?.name, state, pending ? t('topBar.pluginPending') : null].filter(Boolean).join(' · ');
  const plugin = async (install: boolean) => {
    const s = useStore.getState();
    if (!current) return;
    try {
      await call(install ? 'plugin.install' : 'plugin.uninstall', { profile: current.path });
      await s.refreshProfiles();
      s.toast('ok', install ? t('topBar.k23') : t('topBar.k21'));
    } catch (e) {
      s.fail(e);
    }
  };
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
        <Menu.Label>
          <Group gap={6} wrap="nowrap">
            {t('topBar.testProfile')}
            <Info label={t('topBar.testProfileInfo')} />
          </Group>
        </Menu.Label>
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
        {current?.hasBepInEx && (
          <>
            <Menu.Divider />
            <Menu.Label>
              {t('topBar.k18')}
              {current.pluginVersion ? t('topBar.k19', { v0: current.pluginVersion }) : t('topBar.k20')}
              {current.pluginVersion && current.pluginCurrent === false && ` · ${t('topBar.pluginWrong')}`}
            </Menu.Label>
            {current.pluginVersion ? (
              <Menu.Item leftSection={<IconPlugOff size={14} />} color="red" onClick={() => void plugin(false)}>
                {t('topBar.k22')}
              </Menu.Item>
            ) : (
              <Menu.Item leftSection={<IconPlug size={14} />} onClick={() => void plugin(true)}>
                {t('topBar.k24')}
              </Menu.Item>
            )}
          </>
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

/** Settings that change rarely: language and the data folder. (Live reload needs no switch: 应用到游戏 turns it on
 * in the test profile.) */
function SettingsMenu() {
  const lang = useLang();
  const developer = useStore((s) => s.prefs.developer);
  const { setPrefs, dataDir, toast } = useStore.getState();
  const [version, setVersion] = useState('');
  const [checking, setChecking] = useState(false);
  useEffect(() => void window.forge.version().then(setVersion), []);
  const check = async () => {
    setChecking(true);
    const r = await window.forge.checkUpdate().finally(() => setChecking(false));
    // A found update announces itself (the same message at start and on a manual check).
    if (r !== 'downloading') toast(r === 'error' ? 'error' : 'ok', t(`topBar.update.${r}`));
  };
  return (
    <Popover position="bottom-end" width={300} shadow="md">
      <Popover.Target>
        <Tooltip label={t('topBar.settings')}>
          <ActionIcon size={30} variant="subtle" color="gray" aria-label={t('topBar.settings')}>
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
          <Switch size="sm" labelPosition="left" styles={{ body: { justifyContent: 'space-between', gap: 12 }, labelWrapper: { flex: 1 } }} label={<Group gap={4} wrap="nowrap">{t('topBar.developer')}<Info label={t('topBar.developerInfo')} /></Group>} checked={developer} onChange={(e) => setPrefs({ developer: e.currentTarget.checked })} />
          <Button variant="default" size="xs" leftSection={<IconFolder size={14} />} onClick={() => void window.forge.openPath(dataDir)}>
            {t('topBar.k58')}
          </Button>
          <Button variant="default" size="xs" leftSection={<IconHelp size={14} />} onClick={() => setPrefs({ welcomed: false })}>
            {t('welcome.reopen')}
          </Button>
          <Button variant="default" size="xs" leftSection={<IconRefresh size={14} />} loading={checking} onClick={() => void check()}>
            {t('topBar.checkUpdate', { v0: version })}
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

/** The author's test loop: apply into the test profile, automatically if wanted, and see what the game did. */
function ApplyButton() {
  const busy = useStore((s) => s.busy);
  const prefs = useStore((s) => s.prefs);
  const setPrefs = useStore((s) => s.setPrefs);
  const problem = useStore((s) => profileProblem(s.profiles.find((p) => p.path === s.prefs.profilePath)));
  const [installed, setInstalled] = useState(false);
  const [verify, setVerify] = useState(false);
  const [log, setLog] = useState(false);
  return (
    <>
      <Group gap={0} className="primary">
        <Tooltip label={problem ?? t('topBar.k44')} multiline w={problem ? 300 : undefined}>
          <Button
            size="sm"
            loading={!!busy}
            disabled={!!problem}
            leftSection={prefs.autoApply ? <IconRefresh size={16} /> : <IconBolt size={16} />}
            onClick={() => void applyToGame()}
            style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0 }}
          >
            {t('topBar.k45')}
          </Button>
        </Tooltip>
        <Menu position="bottom-end" width={300} closeOnItemClick>
          <Menu.Target>
            <Button size="sm" px={8} aria-label={t('topBar.k46')} style={{ borderTopLeftRadius: 0, borderBottomLeftRadius: 0, borderLeft: '1px solid rgba(0,0,0,.2)' }}>
              <IconChevronDown size={16} />
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Group gap={6} wrap="nowrap" px="sm" py={8}>
              <Switch size="sm" label={t('topBar.autoApply')} checked={prefs.autoApply} onChange={(e) => setPrefs({ autoApply: e.currentTarget.checked })} style={{ flex: 1 }} />
              <Info label={t('topBar.autoApplyInfo')} />
            </Group>
            <Menu.Divider />
            <Menu.Item leftSection={<IconStack2 size={14} />} onClick={() => setInstalled(true)}>
              {t('topBar.k47')}
            </Menu.Item>
            <Menu.Item leftSection={<IconChecklist size={14} />} onClick={() => setVerify(true)}>
              {t('topBar.k48')}
            </Menu.Item>
            {prefs.developer && (
              <Menu.Item leftSection={<IconFileText size={14} />} onClick={() => setLog(true)}>
                {t('gameLog.title')}
              </Menu.Item>
            )}
            <Menu.Divider />
            <Menu.Label>{t('topBar.k52')}</Menu.Label>
            <div style={{ padding: '0 8px 8px' }}>
              <SegmentedControl fullWidth value={prefs.sizeMode} onChange={(v) => setPrefs({ sizeMode: v as SizeMode })} data={SIZES} />
            </div>
            <div style={{ padding: '2px 12px 10px' }}>
              <Checkbox size="xs" label={t('topBar.k53')} checked={prefs.allowMissing} onChange={(e) => setPrefs({ allowMissing: e.currentTarget.checked })} />
            </div>
          </Menu.Dropdown>
        </Menu>
      </Group>
      <InstalledModal opened={installed} onClose={() => setInstalled(false)} />
      <VerifyModal opened={verify} onClose={() => setVerify(false)} />
      <GameLog opened={log} onClose={() => setLog(false)} />
    </>
  );
}

/** Getting the skin to other players: packs, a Thunderstore mod, game files, models. */
function PublishMenu() {
  const [replace, setReplace] = useState(false);
  const [restore, setRestore] = useState(false);
  return (
    <>
      <Menu position="bottom-end" width={300}>
        <Menu.Target>
          <Button size="sm" variant="light" color="cyan" leftSection={<IconSend size={15} />} rightSection={<IconChevronDown size={14} />}>
            {t('topBar.publish')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item leftSection={<IconListCheck size={14} />} onClick={() => useStore.setState({ checkOpen: true })}>
            {t('check.open')}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item leftSection={<IconWorldUpload size={14} />} onClick={() => void exportThunderstore()}>
            {t('actions.exportMod')}
          </Menu.Item>
          <Menu.Item leftSection={<IconPackageExport size={14} />} onClick={() => void exportPack()}>
            {t('topBar.k49')}
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
          <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void exportObj('separate')}>
            {t('topBar.k50')}
          </Menu.Item>
          <Menu.Item leftSection={<IconPackage size={14} />} onClick={() => void exportObj('merged')}>
            {t('topBar.k51')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <ReplaceModal opened={replace} onClose={() => setReplace(false)} />
      <RestoreModal opened={restore} onClose={() => setRestore(false)} />
    </>
  );
}

export function TopBar() {
  return (
    <header className="topbar">
      <div className="brand">
        <img className="logo" src={logo} alt="" />
        <b>FORGESKIN</b>
      </div>
      <div className="spacer" />
      <SettingsMenu />
      <PublishMenu />
      <span className="bar-sep" />
      <TargetMenu />
      <ApplyButton />
      <WindowControls />
    </header>
  );
}
