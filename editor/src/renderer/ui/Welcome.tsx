// First start: what the editor does in four steps, and an example skin on a gun that shows a livery, an ammo screen and
// a hologram shape, each one click away in the part tree. Settings reopens it (新手引导).
import { Button, Group, Modal, Stack, Text, ThemeIcon } from '@mantine/core';
import { IconCrosshair, IconPalette, IconPlayerPlay, IconSparkles } from '@tabler/icons-react';
import { addLivery } from '../livery';
import { addShape } from '../shapes';
import { useStore } from '../store';
import { partLabel, t } from '../i18n';

const STEPS = [
  { icon: IconCrosshair, color: 'orange', key: 'pick' },
  { icon: IconPalette, color: 'pink', key: 'look' },
  { icon: IconSparkles, color: 'teal', key: 'attach' },
  { icon: IconPlayerPlay, color: 'green', key: 'apply' },
] as const;

/** A new skin on the open gun (else the first main weapon): a coloured part (the receiver where there is one), an ammo screen and a hologram ring. */
async function exampleSkin() {
  const s = useStore.getState();
  if (!s.weaponId) {
    const first = s.catalog.find((c) => c.slot === 'main');
    if (!first) return;
    await s.selectWeapon(first.id);
  }
  await useStore.getState().newSkin(false);
  const { catalog, weaponId, edit, select } = useStore.getState();
  const item = catalog.find((c) => c.id === weaponId);
  const part = item?.parts.find((p) => p.role === 'receiver' && p.materials.length) ?? item?.parts.find((p) => p.materials.length);
  if (!part) return;
  edit((d) => void (d.name = t('welcome.exampleName')));
  addLivery(part.materials[0], part.id, 'fill');
  select({ kind: 'part', id: part.id });
  useStore.getState().addAttachment('screen');
  select({ kind: 'part', id: part.id });
  await addShape('ring');
  select(null);
  useStore.getState().toast('ok', t('welcome.exampleDone', { v0: partLabel(part) }));
}

export function Welcome() {
  const opened = useStore((s) => s.ready && !!s.game?.found && !s.prefs.welcomed);
  const close = () => useStore.getState().setPrefs({ welcomed: true });
  return (
    <Modal opened={opened} onClose={close} title={t('welcome.title')} size="lg" centered>
      <Stack gap="md">
        {STEPS.map((x, i) => (
          <Group key={x.key} gap="md" wrap="nowrap" align="flex-start">
            <ThemeIcon size={36} radius="md" variant="light" color={x.color}>
              <x.icon size={20} />
            </ThemeIcon>
            <div>
              <Text fw={650}>
                {i + 1}. {t(`welcome.${x.key}`)}
              </Text>
              <Text size="sm" c="dimmed">
                {t(`welcome.${x.key}Info`)}
              </Text>
            </div>
          </Group>
        ))}
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={close}>
            {t('welcome.start')}
          </Button>
          <Button color="flare" onClick={() => (close(), void exampleSkin())}>
            {t('welcome.example')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
