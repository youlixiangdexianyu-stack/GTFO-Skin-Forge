// The font picker of screens, dynamic text layers and livery text: bundled fonts by group, installed fonts, or a font file.
import { Group, Select } from '@mantine/core';
import { useState } from 'react';
import { cssFont, DEFAULT_FONT, FONT_GROUPS, isFontFile, loadFont, systemFonts } from '../fonts';
import { baseName } from '../protocol';
import { t } from '../i18n';

const PICK_FILE = '~file';
const DIGITS = '0123456789';

/** A font for screens and text layers: bundled fonts by group, installed fonts, or a font file; each option shows the digits in its own font. */
export function FontPicker({ value, onChange }: { value: string | null; onChange: (font: string | null) => void }) {
  const [system, setSystem] = useState<string[] | null>(null);
  const current = value ?? DEFAULT_FONT;
  const files = isFontFile(current) ? [{ value: current, label: baseName(current) }] : [];
  const bundled = new Set(FONT_GROUPS.flatMap((g) => g.fonts.map((f) => f.family)));
  const sys = (system ?? []).filter((f) => !bundled.has(f));
  if (!isFontFile(current) && !bundled.has(current) && !sys.includes(current)) sys.unshift(current);
  const data = [
    ...FONT_GROUPS.map((g) => ({ group: g.group, items: g.fonts.map((f) => f.family) })),
    {
      get group() {
        return t('attachInspector.k6');
      },
      items: system
        ? sys
        : [
            ...sys,
            {
              value: '~loading',
              get label() {
                return t('attachInspector.k7');
              },
              disabled: true,
            },
          ],
    },
    {
      get group() {
        return t('attachInspector.k8');
      },
      items: [
        ...files,
        {
          value: PICK_FILE,
          get label() {
            return t('attachInspector.k9');
          },
        },
      ],
    },
  ];
  // Options use their own font once it has loaded; system and file fonts load when shown.
  const style = (font: string) => {
    void loadFont(font).catch(() => undefined);
    return { font: cssFont(font, 15) };
  };
  return (
    <Select
      searchable
      value={current}
      data={data}
      maxDropdownHeight={360}
      comboboxProps={{ width: 340, position: 'bottom-end' }}
      onDropdownOpen={() => void (system ?? systemFonts().then(setSystem))}
      onChange={async (v) => {
        if (v === PICK_FILE) {
          const [f] = await window.forge.openDialog('file', [
            {
              get name() {
                return t('attachInspector.k10');
              },
              extensions: ['ttf', 'otf', 'woff', 'woff2'],
            },
          ]);
          if (f) onChange(f);
          return;
        }
        if (v) onChange(v === DEFAULT_FONT ? null : v);
      }}
      renderOption={({ option }) =>
        option.value.startsWith('~') ? (
          option.label
        ) : (
          <Group gap={10} wrap="nowrap" justify="space-between" w="100%">
            <span>{option.label}</span>
            <span style={style(option.value)}>{DIGITS}</span>
          </Group>
        )
      }
    />
  );
}
