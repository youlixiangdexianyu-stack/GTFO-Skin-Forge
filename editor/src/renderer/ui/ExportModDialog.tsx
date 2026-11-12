import { Button, Checkbox, Group, Image, Modal, SegmentedControl, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';
import { exportMod } from '../actions';
import { fileUrl } from '../protocol';
import { useStore } from '../store';
import { thumbOf } from '../viewport/thumbnails';
import { t } from '../i18n';

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

/** Name, version, description, icon and skins of a Thunderstore skin mod (opened by `exportThunderstore`). */
export function ExportModDialog() {
  const open = useStore((s) => s.exportModOpen);
  const close = () => useStore.setState({ exportModOpen: false });
  const [name, setName] = useState('');
  const [version, setVersion] = useState('1.0.0');
  const [description, setDescription] = useState('');
  const [icon, setIcon] = useState<string | null>(null);
  const [skins, setSkins] = useState<string[]>([]);
  const [format, setFormat] = useState<'forgeskin' | 'skins'>('forgeskin');
  const [busy, setBusy] = useState(false);
  const list = useStore((s) => s.skins);
  useEffect(() => {
    if (!open) return;
    const s = useStore.getState();
    setName(modName(s.doc.name ?? '', `${s.weaponId}_skin`));
    setSkins([s.doc.id!]);
    setIcon(null);
    void thumbnailPng(s.weaponId!).then(setIcon, () => setIcon(null));
  }, [open]);
  const pickIcon = async () => {
    const [p] = await window.forge.openDialog('files', [{ name: t('exportMod.images'), extensions: ['png', 'jpg', 'jpeg', 'webp', 'bmp'] }]);
    if (p) setIcon(p);
  };
  const go = async () => {
    setBusy(true);
    const done = await exportMod({ name, version, description, icon: icon!, skins, format });
    setBusy(false);
    if (done) close();
  };
  const ready = NAME.test(name) && VERSION.test(version) && !!icon && skins.length > 0;
  return (
    <Modal opened={open} onClose={close} title={t('exportMod.title')} centered size="lg">
      <Stack gap="sm">
        <div>
          <Text size="sm" fw={500} mb={6}>
            {t('exportMod.format')}
          </Text>
          <SegmentedControl
            value={format}
            onChange={(v) => setFormat(v as typeof format)}
            data={[
              { value: 'forgeskin', label: 'ForgeSkin' },
              { value: 'skins', label: 'Skins' },
            ]}
          />
          <Text size="xs" c="dimmed" mt={4}>
            {t(format === 'skins' ? 'exportMod.formatSkins' : 'exportMod.formatForge')}
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
          <Text size="sm" fw={500} mb={6}>
            {t('exportMod.skins')}
          </Text>
          <Checkbox.Group value={skins} onChange={setSkins}>
            <Stack gap={6}>
              {list.map((k) => (
                <Checkbox key={k.id} value={k.id} label={k.id === useStore.getState().doc.id ? useStore.getState().doc.name : k.name} />
              ))}
            </Stack>
          </Checkbox.Group>
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
