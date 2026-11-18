// 直接改游戏文件: write the current skin's textures and material settings into the game's own asset files (with backups),
// restore them, or export the patched files as a zip for players without this app.
import { Alert, Button, Group, List, Loader, Modal, Stack, Text } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { buildPack, exclusive } from '../actions';
import { call, type AssetStatus } from '../protocol';
import { useStore } from '../store';
import { PROP } from '../gameLog';
import { t } from '../i18n';

/** A backend warning about something only the plugin can show, in plain words; null for other warnings. */
function pluginOnly(w: string): string | null {
  let m: RegExpMatchArray | null;
  if ((m = w.match(/^attachment (.+): .+ need the plugin; not written to game files$/))) return t('gameFiles.w.attachment', { v0: m[1] });
  if ((m = w.match(/^.+: dynamic layer (.+) needs the plugin; not written to game files$/))) return t('gameFiles.w.layer', { v0: m[1] });
  if ((m = w.match(/^sound (.+) needs the plugin; not written to game files$/))) return t('gameFiles.w.sound', { v0: m[1] });
  if ((m = w.match(/^part layout \((\d+) .+\) needs the plugin; not written to game files$/))) return t('gameFiles.w.layout', { v0: m[1] });
  return null;
}

/** The backend's warnings, in plain words. */
export function plainWarning(w: string): string {
  let m: RegExpMatchArray | null;
  const only = pluginOnly(w);
  if (only) return only;
  if ((m = w.match(/^(.+): the game shares this material, so the change also shows on (.+)$/))) return t('gameFiles.w.shared', { v0: m[1], v1: m[2] });
  if ((m = w.match(/^(.+): no renderer of .+ uses this material; skipped$/))) return t('gameFiles.w.unused', { v0: m[1] });
  if ((m = w.match(/^(.+): lives in the engine's built-in resources/))) return t('gameFiles.w.builtin', { v0: m[1] });
  if ((m = w.match(/^(.+): the material has no (.+); not written$/))) return t('gameFiles.w.noProp', { v0: m[1], v1: PROP[m[2]]?.() ?? m[2] });
  if ((m = w.match(/^(.+): the game's texture format \d+ has no encoder/))) return t('gameFiles.w.format', { v0: m[1] });
  if ((m = w.match(/^(.+): \d+x\d+ resized to (\d+x\d+)/))) return t('gameFiles.w.resized', { v0: m[1], v1: m[2] });
  if ((m = w.match(/^(.+): the game stores this texture without alpha/))) return t('gameFiles.w.alpha', { v0: m[1] });
  return w;
}

const packPath = () => {
  const s = useStore.getState();
  return `${s.dataDir}\\packs\\${s.weaponId}.forgeskin`;
};

/** Builds the pack, previews what the game files would get (an export into the data folder; the game is untouched), then writes on confirm. */
export function ReplaceModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const name = useStore((s) => s.doc.name || s.weaponId || '');
  const [check, setCheck] = useState<{ pack: string; warnings: string[]; running: boolean } | null>(null);
  const [writing, setWriting] = useState(false);
  useEffect(() => {
    if (!opened) return;
    setCheck(null);
    const s = useStore.getState();
    void exclusive(async () => {
      try {
        const pack = await buildPack(packPath());
        if (!pack) return (onClose(), true);
        const r = await call('assets.export', { pack, out: `${s.dataDir}\\packs\\${s.weaponId}.check.zip` });
        const status = await call('assets.status', {});
        setCheck({ pack, warnings: r.warnings, running: status.running });
      } catch (e) {
        s.fail(e, t('gameFiles.applyFailed'));
        onClose();
      }
      return true;
    }).then((ran) => void (ran || onClose()));
  }, [opened]); // eslint-disable-line react-hooks/exhaustive-deps
  const write = async () => {
    if (!check) return;
    const s = useStore.getState();
    setWriting(true);
    try {
      const r = await call('assets.apply', { pack: check.pack });
      for (const w of r.warnings) s.log(plainWarning(w));
      s.toast('ok', t('gameFiles.applied'));
      onClose();
    } catch (e) {
      s.fail(e, t('gameFiles.applyFailed'));
    } finally {
      setWriting(false);
    }
  };
  const skipped = (check?.warnings ?? []).map(pluginOnly).filter((x): x is string => !!x);
  const notes = (check?.warnings ?? []).filter((w) => !pluginOnly(w)).map(plainWarning);
  return (
    <Modal opened={opened} onClose={onClose} title={t('gameFiles.replaceTitle')} size="lg">
      <Stack gap="md">
        <Text size="sm">{t('gameFiles.replaceWhat', { v0: name })}</Text>
        {!!skipped.length && (
          <div>
            <Text size="sm" fw={600}>
              {t('gameFiles.notWritten')}
            </Text>
            <List size="sm">
              {skipped.map((x) => (
                <List.Item key={x}>{x}</List.Item>
              ))}
            </List>
          </div>
        )}
        {!check && (
          <Group gap="xs">
            <Loader size="xs" />
            <Text size="sm" c="dimmed">
              {t('gameFiles.checking')}
            </Text>
          </Group>
        )}
        {!!notes.length && (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={t('gameFiles.notes')}>
            <List size="sm">
              {notes.map((w) => (
                <List.Item key={w}>{w}</List.Item>
              ))}
            </List>
          </Alert>
        )}
        {check?.running && (
          <Alert color="red" variant="light">
            {t('gameFiles.closeGame')}
          </Alert>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('gameFiles.cancel')}
          </Button>
          <Button disabled={!check || check.running} loading={writing} onClick={() => void write()}>
            {t('gameFiles.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Skins currently written into the game files, each restorable on its own. */
export function RestoreModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const [status, setStatus] = useState<AssetStatus | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const load = async () => {
    try {
      setStatus(await call('assets.status', {}));
    } catch (e) {
      useStore.getState().fail(e, t('gameFiles.restoreFailed'));
      onClose();
    }
  };
  useEffect(() => {
    if (!opened) return;
    setStatus(null);
    void load();
  }, [opened]); // eslint-disable-line react-hooks/exhaustive-deps
  const restore = async (skinId?: string) => {
    const s = useStore.getState();
    setBusy(skinId ?? '*');
    try {
      const r = await call('assets.restore', skinId ? { skinId } : { discardStale: true });
      s.toast('ok', t('gameFiles.restored', { v0: r.restored.length }));
      await load();
    } catch (e) {
      s.fail(e, t('gameFiles.restoreFailed'));
    } finally {
      setBusy(null);
    }
  };
  const empty = status && !status.skins.length && !status.stale.length;
  return (
    <Modal opened={opened} onClose={onClose} title={t('gameFiles.restoreTitle')} size="lg">
      <Stack gap="sm">
        {!status && <Loader size="sm" />}
        {empty && <Text c="dimmed">{t('gameFiles.none')}</Text>}
        {status?.running && !empty && (
          <Alert color="red" variant="light">
            {t('gameFiles.closeGame')}
          </Alert>
        )}
        {status?.skins.map((k) => (
          <div key={k.id} className="item" style={{ gridTemplateColumns: '1fr auto' }}>
            <div style={{ minWidth: 0 }}>
              <Text size="sm">{k.name}</Text>
              <Text size="xs" c="dimmed">
                {t('gameFiles.appliedAt', { v0: new Date(k.appliedAt).toLocaleString(), v1: k.materials.length })}
              </Text>
            </div>
            <Button size="xs" variant="default" disabled={status.running || !!busy} loading={busy === k.id} onClick={() => void restore(k.id)}>
              {t('gameFiles.restoreOne')}
            </Button>
          </div>
        ))}
        {!!status?.stale.length && (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />}>
            {t('gameFiles.stale')}
          </Alert>
        )}
        {status && !empty && (
          <Group justify="flex-end">
            <Button color="red" variant="light" disabled={status.running || !!busy} loading={busy === '*'} onClick={() => void restore()}>
              {status.skins.length ? t('gameFiles.restoreAll') : t('gameFiles.discardStale')}
            </Button>
          </Group>
        )}
      </Stack>
    </Modal>
  );
}

/** Saves a zip of the patched game files for players without this app; the game folder is not touched. */
export async function exportGameFiles(): Promise<void> {
  const s = useStore.getState();
  const out = await window.forge.saveDialog(`${s.doc.name || s.weaponId}.zip`, [{ name: t('gameFiles.zip'), extensions: ['zip'] }]);
  if (!out) return;
  await exclusive(async () => {
    try {
      const pack = await buildPack(packPath());
      if (!pack) return;
      const r = await call('assets.export', { pack, out });
      for (const w of r.warnings) s.log(plainWarning(w));
      s.toast('ok', t('gameFiles.exported'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(r.zip) });
    } catch (e) {
      s.fail(e, t('gameFiles.exportFailed'));
    }
  });
}
