// First start: the spotlight guide (tour/) that walks the player through a first skin, opened by what the editor does in
// four lines, and closed with an example skin on a gun that shows a livery, an ammo screen and a hologram shape, each one
// click away in the part tree. Settings and the tutorial restart it.
import { Group, Stack, Text, ThemeIcon } from '@mantine/core';
import { IconCrosshair, IconPalette, IconPlayerPlay, IconSparkles } from '@tabler/icons-react';
import { addLivery } from '../livery';
import { addShape } from '../shapes';
import { useStore } from '../store';
import { Tour } from '../tour/Tour';
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

/** The guide's first card: what making a skin takes, in four lines. */
function Overview() {
  return (
    <Stack gap="sm">
      <Text size="sm">{t('tour.introInfo')}</Text>
      {STEPS.map((x) => (
        <Group key={x.key} gap="sm" wrap="nowrap" align="center">
          <ThemeIcon size={28} radius="md" variant="light" color={x.color}>
            <x.icon size={16} />
          </ThemeIcon>
          <Text size="sm" fw={600}>
            {t(`welcome.${x.key}`)}
          </Text>
        </Group>
      ))}
    </Stack>
  );
}

/** Starts by itself on the first start (once the game is found) and when Settings or the tutorial restarts it. */
export function Welcome() {
  const opened = useStore((s) => s.ready && !!s.game?.found && !s.prefs.welcomed);
  return opened ? <Tour intro={<Overview />} example={() => void exampleSkin()} /> : null;
}
