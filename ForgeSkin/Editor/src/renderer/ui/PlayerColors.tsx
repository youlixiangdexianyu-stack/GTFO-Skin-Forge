// Player colours in the editor: the person button next to a colour picker makes that colour player-adjustable (alone or
// together with another), and the whole gun's look lists them with their name, colour and what each changes. A colour the game
// cannot change (a gun material's tint) gets a grey button and says why.
import { ActionIcon, Button, ColorSwatch, Group, Menu, TextInput, Tooltip } from '@mantine/core';
import { IconPlus, IconTrash, IconUser, IconUsers, IconX } from '@tabler/icons-react';
import type { ColorSlot, PlayerColor, SkinDocument } from '../protocol';
import { activeScheme, colorOfSlot, linkSlot, removePlayerColor, setPlayerColor, shownColor, slotWorksInGame, unlinkSlot, updatePlayerColor } from '../playerColors';
import { attachmentName, useStore } from '../store';
import { Color, Field } from './fields';
import { Empty, Info, Section } from './Features';
import { t } from '../i18n';

const css = (c: number[] | null | undefined) =>
  `rgb(${(c ?? [1, 1, 1])
    .slice(0, 3)
    .map((v) => Math.round(Math.min(1, Math.max(0, v ?? 1)) * 255))
    .join(',')})`;

/** Next to a colour picker: whether players may change the colour at `slot` (showing `value` now) and with which other colours. */
export function PlayerToggle({ slot, value }: { slot: ColorSlot; value: number[] }) {
  const linked = useStore((s) => colorOfSlot(s.doc, slot));
  const others = useStore((s) => s.doc.playerColors)?.filter((c): c is PlayerColor => !!c && c.id !== linked?.id) ?? [];
  const works = useStore((s) => slotWorksInGame(s.doc, slot));
  if (!works && !linked)
    return (
      <Tooltip label={t('playerColors.noTint')} multiline w={240}>
        <span>
          <ActionIcon variant="default" color="grape" size="input-xs" disabled aria-label={t('playerColors.toggle')}>
            <IconUser size={14} />
          </ActionIcon>
        </span>
      </Tooltip>
    );
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
  // An attachment by the name the editor shows for it (its own, else 「弹药屏 1」 and the like), never its id.
  return `${a ? attachmentName(d, a) : s.target} · ${what}`;
}

function Entry({ c }: { c: PlayerColor }) {
  const doc = useStore((s) => s.doc);
  const id = c.id!;
  const color = shownColor(doc, c);
  const scheme = activeScheme(doc);
  const dead = (c.slots ?? []).some((s) => s && !slotWorksInGame(doc, s));
  return (
    <div className="effect">
      <Group gap="xs" wrap="nowrap" mb={8}>
        <TextInput
          style={{ flex: 1 }}
          size="xs"
          aria-label={t('playerColors.name')}
          value={c.name ?? ''}
          onChange={(e) => {
            const name = e.currentTarget.value;
            updatePlayerColor(id, (x) => void (x.name = name), 'name');
          }}
        />
        <Tooltip label={t('playerColors.remove')}>
          <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('playerColors.remove')} onClick={() => removePlayerColor(id)}>
            <IconTrash size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>
      <Field label={scheme ? t('playerColors.colorIn', { v0: scheme.name }) : t('playerColors.color')}>
        <Color value={color} onChange={(v) => setPlayerColor(id, v)} />
      </Field>
      <Field label={t('playerColors.drives')}>
        <Group gap={4}>
          {(c.slots ?? []).map(
            (s, i) =>
              s && (
                <Button key={i} size="compact-xs" variant="light" color={slotWorksInGame(doc, s) ? 'grape' : 'gray'} rightSection={<IconX size={10} />} onClick={() => unlinkSlot(s)}>
                  {slotLabel(doc, s)}
                </Button>
              ),
          )}
        </Group>
        {dead && <Info label={t('playerColors.noTint')} />}
      </Field>
    </div>
  );
}

/** The skin overview's list of player colours. */
export function PlayerColors() {
  const list = (useStore((s) => s.doc.playerColors) ?? []).filter((c): c is PlayerColor => !!c);
  return (
    <Section id="player-colours" title={t('playerColors.title')} icon={IconUsers} color="var(--mantine-color-grape-4)" info={t('playerColors.info')}>
      {!list.length && <Empty>{t('playerColors.empty')}</Empty>}
      {list.map((c) => (
        <Entry key={c.id} c={c} />
      ))}
    </Section>
  );
}
