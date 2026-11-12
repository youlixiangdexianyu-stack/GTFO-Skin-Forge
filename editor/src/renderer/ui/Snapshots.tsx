// 版本快照: named save points of the edited skin, beyond undo: save, list, restore (after a confirm), delete (after a confirm).
import { ActionIcon, Button, Group, Loader, Modal, Stack, Text, TextInput } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { deleteSnapshot, listSnapshots, restoreSnapshot, saveSnapshot } from '../actions';
import type { ProjectEntry } from '../protocol';
import { useStore } from '../store';
import { t } from '../i18n';

export function SnapshotsModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const skinName = useStore((s) => s.doc.name);
  const [list, setList] = useState<ProjectEntry[] | null>(null);
  const [name, setName] = useState('');
  const [confirm, setConfirm] = useState<{ restore: boolean; k: ProjectEntry } | null>(null);
  const refresh = () => listSnapshots().then(setList, (e) => (useStore.getState().fail(e), setList([])));
  useEffect(() => {
    if (!opened) return;
    setName('');
    setList(null);
    void refresh();
  }, [opened]);
  const run = async (fn: () => Promise<unknown>, done: string) => {
    try {
      await fn();
      useStore.getState().toast('ok', done);
    } catch (e) {
      useStore.getState().fail(e, t('snapshots.failed'));
    }
    await refresh();
  };
  const save = () => void run(() => saveSnapshot(name), t('snapshots.saved')).then(() => setName(''));
  const go = () => {
    const c = confirm!;
    setConfirm(null);
    void (c.restore ? run(() => restoreSnapshot(c.k.path), t('snapshots.restored')) : run(() => deleteSnapshot(c.k.path), t('snapshots.deleted')));
  };
  return (
    <>
      <Modal opened={opened && !confirm} onClose={onClose} title={t('snapshots.title', { v0: skinName ?? '' })} centered size="lg">
        <Text size="xs" c="dimmed" mb="sm">
          {t('snapshots.hint')}
        </Text>
        <Group gap="xs" wrap="nowrap">
          <TextInput
            size="xs"
            style={{ flex: 1 }}
            placeholder={t('snapshots.namePlaceholder')}
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            onKeyDown={(e) => e.key === 'Enter' && save()}
          />
          <Button size="xs" onClick={save}>
            {t('snapshots.save')}
          </Button>
        </Group>
        <Stack gap={6} mt="md">
          {!list ? (
            <Loader size="sm" />
          ) : !list.length ? (
            <Text size="sm" c="dimmed">
              {t('snapshots.none')}
            </Text>
          ) : (
            list.map((k) => (
              <Group key={k.path} gap="xs" wrap="nowrap">
                <Text size="sm" style={{ flex: 1 }} truncate title={k.name}>
                  {k.name}
                </Text>
                <Text size="xs" c="dimmed">
                  {new Date(k.modified).toLocaleString()}
                </Text>
                <Button size="compact-xs" variant="light" onClick={() => setConfirm({ restore: true, k })}>
                  {t('snapshots.restore')}
                </Button>
                <ActionIcon size="sm" variant="subtle" color="red" aria-label={t('snapshots.delete')} onClick={() => setConfirm({ restore: false, k })}>
                  <IconTrash size={14} />
                </ActionIcon>
              </Group>
            ))
          )}
        </Stack>
      </Modal>
      <Modal opened={!!confirm} onClose={() => setConfirm(null)} title={t(confirm?.restore ? 'snapshots.restoreTitle' : 'snapshots.deleteTitle')} centered>
        <Text size="sm">{confirm && t(confirm.restore ? 'snapshots.restoreBody' : 'snapshots.deleteBody', { v0: confirm.k.name })}</Text>
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setConfirm(null)}>
            {t('skins.cancel')}
          </Button>
          <Button color={confirm?.restore ? undefined : 'red'} onClick={go}>
            {t(confirm?.restore ? 'snapshots.restore' : 'snapshots.delete')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
