// Player colours in the editor: the person button next to a colour picker makes that colour player-adjustable (alone or
// together with another), and the skin overview lists them with their name, colour, suggested colours and what each changes.
import { ActionIcon, Button, ColorSwatch, Group, Menu, Text, TextInput, Tooltip } from '@mantine/core';
import { IconPlus, IconTrash, IconUser, IconX } from '@tabler/icons-react';
import type { ColorSlot, PlayerColor, SkinDocument } from '../protocol';
import { colorOfSlot, linkSlot, removePlayerColor, setPlayerColor, unlinkSlot, updatePlayerColor } from '../playerColors';
import { useStore } from '../store';
import { Color, Field } from './fields';
import { Info } from './Features';
import { t } from '../i18n';

const css = (c: number[] | null | undefined) => `rgb(${(c ?? [1, 1, 1]).slice(0, 3).map((v) => Math.round(Math.min(1, Math.max(0, v ?? 1)) * 255)).join(',')})`;

/** Next to a colour picker: whether players may change the colour at `slot` (showing `value` now) and with which other colours. */
export function PlayerToggle({ slot, value }: { slot: ColorSlot; value: number[] }) {
  const linked = useStore((s) => colorOfSlot(s.doc, slot));
  const others = useStore((s) => s.doc.playerColors)?.filter((c): c is PlayerColor => !!c && c.id !== linked?.id) ?? [];
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <Tooltip label={linked ? t('playerColors.linked', { v0: linked.name }) : t('playerColors.toggle')}>
          <ActionIcon variant={linked ? 'filled' : 'default'} color="grape" size="input-xs" aria-label={t('playerColors.toggle')}>
            <IconUser size={14} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t('playerColors.title')}</Menu.Label>
        {!linked && (
          <Menu.Item leftSection={<IconPlus size={14} />} onClick={() => linkSlot(slot, value, null)}>
            {t('playerColors.asNew')}
          </Menu.Item>
        )}
        {others.map((c) => (
          <Menu.Item key={c.id} leftSection={<ColorSwatch size={14} color={css(c.default)} />} onClick={() => linkSlot(slot, value, c.id!)}>
            {t('playerColors.join', { v0: c.name })}
          </Menu.Item>
        ))}
        {linked && (
          <Menu.Item color="red" leftSection={<IconX size={14} />} onClick={() => unlinkSlot(slot)}>
            {t('playerColors.unlink')}
          </Menu.Item>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}

/** "Receiver tint", "Ammo 1 · digits". */
function slotLabel(d: SkinDocument, s: ColorSlot): string {
  const a = d.attachments?.find((x) => x?.id === s.target);
  const what = t(`playerColors.slot.${s.kind ?? 'tint'}`, { v0: (s.effect ?? 0) + 1 });
  return `${a?.name || s.target} · ${what}`;
}

function Entry({ c }: { c: PlayerColor }) {
  const doc = useStore((s) => s.doc);
  const id = c.id!;
  const color = c.default ?? [1, 1, 1];
  const palette = (c.palette ?? []).filter((p): p is number[] => !!p);
  return (
    <div className="effect">
      <Group gap="xs" wrap="nowrap">
        <TextInput style={{ flex: 1 }} size="xs" aria-label={t('playerColors.name')} value={c.name ?? ''} onChange={(e) => {
          const name = e.currentTarget.value;
          updatePlayerColor(id, (x) => void (x.name = name), 'name');
        }} />
        <Tooltip label={t('playerColors.remove')}>
          <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('playerColors.remove')} onClick={() => removePlayerColor(id)}>
            <IconTrash size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>
      <Field label={t('playerColors.color')}>
        <Color value={color} onChange={(v) => setPlayerColor(id, v)} />
      </Field>
      <Field label={t('playerColors.palette')}>
        <Group gap={4}>
          {palette.map((p, i) => (
            <Group key={i} gap={0} wrap="nowrap">
              <ColorSwatch component="button" size={20} color={css(p)} onClick={() => setPlayerColor(id, p)} style={{ cursor: 'pointer' }} />
              <ActionIcon variant="subtle" color="gray" size="xs" aria-label={t('playerColors.removeSwatch')} onClick={() => updatePlayerColor(id, (x) => void x.palette!.splice(i, 1))}>
                <IconX size={10} />
              </ActionIcon>
            </Group>
          ))}
          <Tooltip label={t('playerColors.addSwatch')}>
            <ActionIcon variant="default" size="sm" aria-label={t('playerColors.addSwatch')} onClick={() => updatePlayerColor(id, (x) => void (x.palette = [...(x.palette ?? []), color.slice(0, 3)]))}>
              <IconPlus size={12} />
            </ActionIcon>
          </Tooltip>
          <Info label={t('playerColors.paletteInfo')} />
        </Group>
      </Field>
      <Field label={t('playerColors.drives')}>
        <Group gap={4}>
          {(c.slots ?? []).map((s, i) => s && (
            <Button key={i} size="compact-xs" variant="light" color="grape" rightSection={<IconX size={10} />} onClick={() => unlinkSlot(s)}>
              {slotLabel(doc, s)}
            </Button>
          ))}
        </Group>
      </Field>
    </div>
  );
}

/** The skin overview's list of player colours. */
export function PlayerColors() {
  const list = (useStore((s) => s.doc.playerColors) ?? []).filter((c): c is PlayerColor => !!c);
  return (
    <div className="section">
      <Group gap={6} mb={6}>
        <h3 style={{ margin: 0 }}>{t('playerColors.title')}</h3>
        <Info label={t('playerColors.info')} />
      </Group>
      {!list.length && (
        <Text size="xs" c="dimmed">
          {t('playerColors.empty')}
        </Text>
      )}
      {list.map((c) => (
        <Entry key={c.id} c={c} />
      ))}
    </div>
  );
}
