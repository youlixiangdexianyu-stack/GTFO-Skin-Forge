import { Accordion, Button, Group, List, Modal, Select, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { importSkins, noteText } from '../actions';
import type { SkinsNote } from '../protocol';
import { useStore } from '../store';
import { localName, t } from '../i18n';

/** What a Skins import or export left out or changed: a count, then the list folded away. */
function Notes({ notes }: { notes: SkinsNote[] }) {
  if (!notes.length) return null;
  return (
    <Accordion variant="contained">
      <Accordion.Item value="notes">
        <Accordion.Control>{t('skinsReport.list', { v0: notes.length })}</Accordion.Control>
        <Accordion.Panel>
          <List size="sm" spacing={4}>
            {notes.map((n, i) => (
              <List.Item key={i}>{noteText(n)}</List.Item>
            ))}
          </List>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}

/** The report after a Skins import or export, and the gear question for a skin group that names none. */
export function SkinsReport() {
  const report = useStore((s) => s.skinsReport);
  const target = useStore((s) => s.skinsTarget);
  const catalog = useStore((s) => s.catalog);
  const [weapon, setWeapon] = useState<string | null>(null);
  const closeReport = () => useStore.setState({ skinsReport: null });
  const closeTarget = () => (useStore.setState({ skinsTarget: null }), setWeapon(null));
  // Skins filters guns, melee weapons and tools by their gear id and items by their item id; attachments, ammo and lenses are neither.
  const gear = catalog.filter((c) => c.gearId != null || c.itemId != null).map((c) => ({ value: c.id, label: localName(c) }));
  return (
    <>
      <Modal opened={!!report} onClose={closeReport} title={report?.title} centered size="lg">
        <Stack gap="sm">
          {!!report?.weapons?.length && (
            <div>
              <Text size="sm" mb={6}>
                {t('skinsReport.imported')}
              </Text>
              <Group gap={6}>
                {report.weapons.map((w) => {
                  const item = catalog.find((c) => c.id === w);
                  return (
                    <Button key={w} size="xs" variant="light" onClick={() => (closeReport(), void useStore.getState().selectWeapon(w))}>
                      {item ? localName(item) : w}
                    </Button>
                  );
                })}
              </Group>
            </div>
          )}
          <Text size="sm" c="dimmed">
            {t('skinsReport.summary')}
          </Text>
          <Notes notes={report?.notes ?? []} />
          <Group justify="flex-end">
            {report?.folder && (
              <Button variant="default" onClick={() => void window.forge.showInFolder(report.folder!)}>
                {t('actions.k18')}
              </Button>
            )}
            <Button onClick={closeReport}>{t('skinsReport.ok')}</Button>
          </Group>
        </Stack>
      </Modal>
      <Modal opened={!!target} onClose={closeTarget} title={t('skinsReport.targetTitle')} centered size="lg">
        <Stack gap="sm">
          <Text size="sm">{t('skinsReport.targetBody')}</Text>
          <Select searchable label={t('skinsReport.targetPick')} data={gear} value={weapon} onChange={setWeapon} />
          <Notes notes={target?.notes ?? []} />
          <Group justify="flex-end">
            <Button variant="default" onClick={closeTarget}>
              {t('skins.cancel')}
            </Button>
            <Button disabled={!weapon} onClick={() => (closeTarget(), void importSkins(target!.manifest, weapon!))}>
              {t('skinsReport.targetImport')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
