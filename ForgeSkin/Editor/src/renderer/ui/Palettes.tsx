// 配色方案 / Palettes (library.ts): save the skin's livery colours as a palette, recolour the skin with a saved one, and pick
// the palette every colour field offers as swatches. Palettes are the author's, kept in the data folder for every skin.
import { ActionIcon, Group, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconColorSwatch, IconDeviceFloppy, IconTrash, IconWand } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { applyPalette, loadLibrary, removePalette, savePalette, setActivePalette, skinColors, useLibrary } from '../library';
import { useStore } from '../store';
import { Empty, Section } from './Features';
import { t } from '../i18n';

const css = (c: number[]) =>
  `rgb(${c
    .slice(0, 3)
    .map((x) => Math.round(Math.min(1, Math.max(0, x ?? 0)) * 255))
    .join(',')})`;

export function Palettes() {
  const doc = useStore((s) => s.doc);
  const { palettes, active } = useLibrary();
  const [name, setName] = useState('');
  useEffect(() => void loadLibrary(), []);
  const own = skinColors(doc);
  return (
    <Section id="palettes" title={t('palette.title')} icon={IconColorSwatch} color="var(--paint)" info={t('palette.info')}>
      <Group gap={6} wrap="nowrap" mb={8}>
        <TextInput style={{ flex: 1 }} size="xs" placeholder={t('palette.namePlaceholder')} aria-label={t('palette.name')} value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <Tooltip label={own.length ? `${t('palette.save')}: ${t('palette.saveInfo', { v0: Math.min(8, own.length) })}` : t('palette.nothingToSave')} multiline w={240}>
          <ActionIcon
            size="input-xs"
            variant="light"
            color="pink"
            aria-label={t('palette.save')}
            disabled={!own.length}
            onClick={() => {
              savePalette(name.trim() || t('palette.defaultName', { v0: palettes.length + 1 }), own);
              setName('');
            }}
          >
            <IconDeviceFloppy size={15} />
          </ActionIcon>
        </Tooltip>
      </Group>
      {palettes.map((p) => (
        <div key={p.id} className="palette-row" aria-selected={p.id === active}>
          <UnstyledButton style={{ display: 'contents' }} aria-label={t('palette.useSwatches', { v0: p.name })} onClick={() => setActivePalette(p.id === active ? null : p.id)}>
            <Text size="xs" w={72} truncate title={p.name}>
              {p.name}
            </Text>
            <span className="chips">
              {p.colors.map((c, i) => (
                <i key={i} style={{ background: css(c) }} />
              ))}
            </span>
          </UnstyledButton>
          <Tooltip label={t('palette.apply')}>
            <ActionIcon variant="light" color="grape" size="sm" aria-label={t('palette.apply')} disabled={!own.length} onClick={() => applyPalette(p)}>
              <IconWand size={14} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('palette.remove')}>
            <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('palette.remove')} onClick={() => removePalette(p.id)}>
              <IconTrash size={13} />
            </ActionIcon>
          </Tooltip>
        </div>
      ))}
      {!palettes.length && <Empty>{t('palette.empty')}</Empty>}
    </Section>
  );
}
