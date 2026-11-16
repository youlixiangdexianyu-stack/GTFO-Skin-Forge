// 配色方案 in the whole gun's look: the player colours' defaults (原色) and every scheme the author added, side by side as
// cards of swatches. A click shows that scheme on the gun (colour edits then change it); ＋ saves the colours shown now as a
// new one; a card's name is edited on the card. The game's colour page offers the same cards to players.
import { ActionIcon, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconPalette, IconPlus, IconX } from '@tabler/icons-react';
import type { PlayerColor } from '../protocol';
import { activeScheme, addScheme, removeScheme, renameScheme, selectScheme } from '../playerColors';
import { useStore } from '../store';
import { Empty, Section } from './Features';
import { t } from '../i18n';

const css = (c: number[] | null | undefined) =>
  `rgb(${(c ?? [1, 1, 1])
    .slice(0, 3)
    .map((v) => Math.round(Math.min(1, Math.max(0, v ?? 1)) * 255))
    .join(',')})`;

export function ColorSchemes() {
  const colors = (useStore((s) => s.doc.playerColors) ?? []).filter((c): c is PlayerColor => !!c);
  const schemes = (useStore((s) => s.doc.colorSchemes) ?? []).filter((x) => !!x);
  const active = useStore((s) => activeScheme(s.doc)?.id ?? null);
  const chips = (value: (c: PlayerColor) => number[] | null | undefined) => (
    <span className="scheme-chips">
      {colors.map((c) => (
        <i key={c.id} style={{ background: css(value(c)) }} title={c.name ?? ''} />
      ))}
    </span>
  );
  return (
    <Section id="color-schemes" title={t('schemes.title')} icon={IconPalette} color="var(--paint)" info={t('schemes.info')}>
      {!colors.length ? (
        <Empty>{t('schemes.needColors')}</Empty>
      ) : (
        <div className="schemes">
          <UnstyledButton className="scheme" aria-selected={!active} onClick={() => selectScheme(null)}>
            {chips((c) => c.default)}
            <span className="name">{t('schemes.original')}</span>
          </UnstyledButton>
          {schemes.map((x) => (
            <div key={x!.id} className="scheme" role="button" aria-selected={active === x!.id} onClick={() => active !== x!.id && selectScheme(x!.id ?? null)}>
              {chips((c) => x!.colors?.[c.id ?? ''] ?? c.default)}
              <TextInput variant="unstyled" size="xs" className="name" aria-label={t('schemes.rename')} value={x!.name ?? ''} onChange={(e) => renameScheme(x!.id ?? '', e.currentTarget.value)} />
              <Tooltip label={t('schemes.remove')}>
                <ActionIcon className="remove" variant="subtle" color="gray" size="xs" aria-label={t('schemes.remove')} onClick={(e) => (e.stopPropagation(), removeScheme(x!.id ?? ''))}>
                  <IconX size={11} />
                </ActionIcon>
              </Tooltip>
            </div>
          ))}
          <Tooltip label={t('schemes.addInfo')} multiline w={220}>
            <UnstyledButton className="scheme add" aria-label={t('schemes.add')} onClick={addScheme}>
              <IconPlus size={16} />
              <span className="name">{t('schemes.add')}</span>
            </UnstyledButton>
          </Tooltip>
        </div>
      )}
    </Section>
  );
}
