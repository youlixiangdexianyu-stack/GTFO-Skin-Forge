import { Button, Checkbox, Group, Image, Modal, SegmentedControl, Select, Stack, Table, Text, Textarea, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';
import { exportMod } from '../actions';
import { call, fileUrl, type ProjectEntry } from '../protocol';
import { autosaveRoot, useStore } from '../store';
import { thumbOf } from '../viewport/thumbnails';
import { localName, t } from '../i18n';

const NAME = /^[A-Za-z0-9_]+$/;
const VERSION = /^\d+\.\d+\.\d+$/;

/** A Thunderstore mod name from a skin name: ASCII letters, digits and _. */
const modName = (name: string, fallback: string) =>
  name
    .normalize('NFKD')
    .replace(/[^A-Za-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '') || fallback;

/** The current item's thumbnail as a PNG in the data dir, for the default icon; null when there is none yet. */
async function thumbnailPng(weaponId: string): Promise<string | null> {
  const url = thumbOf(weaponId);
  if (!url) return null;
  const path = `${useStore.getState().dataDir}\\export\\${weaponId}-icon.png`;
  await window.forge.writeFile(path, new Uint8Array(await (await fetch(url)).arrayBuffer()));
  return path;
}

type Format = 'forgeskin' | 'skins' | 'pack';

/** Name, version, description, icon and skins of a Thunderstore skin mod (opened by `exportThunderstore`). Skins of every
 * item can go in: as ForgeSkin packs, as one Skins group per skin (an item may get several, picked in game), or as one
 * Skins gun pack (one skin per item). */
export function ExportModDialog() {
  const open = useStore((s) => s.exportModOpen);
  const close = () => useStore.setState({ exportModOpen: false });
  const [name, setName] = useState('');
  const [version, setVersion] = useState('1.0.0');
  const [description, setDescription] = useState('');
  const [icon, setIcon] = useState<string | null>(null);
  /** Chosen skin ids (ForgeSkin packs and one Skins group per skin). */
  const [skins, setSkins] = useState<string[]>([]);
  /** Item id -> its skin in the gun pack ('' = not in it). */
  const [pack, setPack] = useState<Record<string, string>>({});
  const [format, setFormat] = useState<Format>('forgeskin');
  const [busy, setBusy] = useState(false);
  const [all, setAll] = useState<Record<string, ProjectEntry[]>>({});
  const catalog = useStore((s) => s.catalog);
  const doc = useStore((s) => s.doc);
  useEffect(() => {
    if (!open) return;
    const s = useStore.getState();
    setName(modName(s.doc.name ?? '', `${s.weaponId}_skin`));
    setSkins([s.doc.id!]);
    setPack({ [s.weaponId!]: s.doc.id! });
    setIcon(null);
    void thumbnailPng(s.weaponId!).then(setIcon, () => setIcon(null));
    void call('project.listAll', { root: autosaveRoot() }).then(setAll, () => setAll({ [s.weaponId!]: s.skins }));
  }, [open]);
  // Items with skins in catalog order, the open one first; the edited skin under its current name.
  const items = catalog
    .filter((c) => all[c.id]?.length)
    .sort((a, b) => Number(b.id === doc.target?.weapon) - Number(a.id === doc.target?.weapon))
    .map((c) => ({ item: c, list: all[c.id].map((k) => (k.id === doc.id ? { ...k, name: doc.name ?? k.name } : k)) }));
  const chosen =
    format === 'pack'
      ? items.flatMap(({ item, list }) => list.filter((k) => k.id === pack[item.id]).map((k) => ({ weapon: item.id, id: k.id, path: k.path })))
      : items.flatMap(({ item, list }) => list.filter((k) => skins.includes(k.id)).map((k) => ({ weapon: item.id, id: k.id, path: k.path })));
  const pickIcon = async () => {
    const [p] = await window.forge.openDialog('files', [{ name: t('exportMod.images'), extensions: ['png', 'jpg', 'jpeg', 'webp', 'bmp'] }]);
    if (p) setIcon(p);
  };
  const go = async () => {
    setBusy(true);
    const done = await exportMod({ name, version, description, icon: icon!, skins: chosen, format: format === 'forgeskin' ? 'forgeskin' : 'skins', gunPack: format === 'pack' });
    setBusy(false);
    if (done) close();
  };
  const ready = NAME.test(name) && VERSION.test(version) && !!icon && chosen.length > 0;
  return (
    <Modal opened={open} onClose={close} title={t('exportMod.title')} centered size="lg">
      <Stack gap="sm">
        <div>
          <Text size="sm" fw={500} mb={6}>
            {t('exportMod.format')}
          </Text>
          <SegmentedControl
            value={format}
            onChange={(v) => setFormat(v as Format)}
            data={[
              { value: 'forgeskin', label: 'ForgeSkin' },
              { value: 'skins', label: t('exportMod.skinsEach') },
              { value: 'pack', label: t('exportMod.skinsPack') },
            ]}
          />
          <Text size="xs" c="dimmed" mt={4}>
            {t(format === 'pack' ? 'exportMod.formatPack' : format === 'skins' ? 'exportMod.formatSkins' : 'exportMod.formatForge')}
          </Text>
        </div>
        <TextInput label={t('exportMod.name')} description={t('exportMod.nameHint')} value={name} onChange={(e) => setName(e.currentTarget.value)} error={name && !NAME.test(name)} />
        <TextInput
          label={t('exportMod.version')}
          description={t('exportMod.versionHint')}
          value={version}
          onChange={(e) => setVersion(e.currentTarget.value)}
          error={!VERSION.test(version)}
          w={160}
        />
        <Textarea label={t('exportMod.description')} value={description} onChange={(e) => setDescription(e.currentTarget.value)} maxLength={250} autosize minRows={2} />
        <div>
          <Text size="sm" fw={500}>
            {t('exportMod.icon')}
          </Text>
          <Text size="xs" c="dimmed" mb={6}>
            {t('exportMod.iconHint')}
          </Text>
          <Group align="center">
            {icon ? <Image src={fileUrl(icon)} w={96} h={96} radius="sm" fit="contain" bg="dark.8" /> : <Text size="sm" c="dimmed">{t('exportMod.noIcon')}</Text>}
            <Button variant="default" size="xs" onClick={() => void pickIcon()}>
              {t('exportMod.pickIcon')}
            </Button>
          </Group>
        </div>
        <div>
          <Text size="sm" fw={500}>
            {t(format === 'pack' ? 'exportMod.packSkins' : 'exportMod.skins')}
          </Text>
          <Text size="xs" c="dimmed" mb={6}>
            {t(format === 'pack' ? 'exportMod.packHint' : 'exportMod.skinsHint')}
          </Text>
          {format === 'pack' ? (
            <Table withRowBorders={false} verticalSpacing={4}>
              <Table.Tbody>
                {items.map(({ item, list }) => (
                  <Table.Tr key={item.id}>
                    <Table.Td w="40%">
                      <Text size="sm">{localName(item)}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Select
                        size="xs"
                        aria-label={localName(item)}
                        value={pack[item.id] ?? ''}
                        onChange={(v) => setPack((p) => ({ ...p, [item.id]: v ?? '' }))}
                        allowDeselect={false}
                        data={[{ value: '', label: t('exportMod.packNone') }, ...list.map((k) => ({ value: k.id, label: k.name }))]}
                      />
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          ) : (
            <Checkbox.Group value={skins} onChange={setSkins}>
              <Stack gap={8}>
                {items.map(({ item, list }) => (
                  <div key={item.id}>
                    <Text size="xs" c="dimmed" mb={4}>
                      {localName(item)}
                    </Text>
                    <Stack gap={4} pl="sm">
                      {list.map((k) => (
                        <Checkbox key={k.id} value={k.id} label={k.name} />
                      ))}
                    </Stack>
                  </div>
                ))}
              </Stack>
            </Checkbox.Group>
          )}
        </div>
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={close}>
            {t('skins.cancel')}
          </Button>
          <Button onClick={() => void go()} disabled={!ready} loading={busy}>
            {t('exportMod.export')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
