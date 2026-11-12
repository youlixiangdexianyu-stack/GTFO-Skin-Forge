import { ActionIcon, Button, Checkbox, Group, SegmentedControl, Select, Switch, TextInput } from '@mantine/core';
import { useState } from 'react';
import { cssFont, DEFAULT_FONT, FONT_GROUPS, isFontFile, loadFont, systemFonts } from '../fonts';
import { IconPlus, IconTrash, IconX } from '@tabler/icons-react';
import { baseName, triggersFor, type Attachment, type CatalogItem, type Screen, type ScreenSource, type Trigger, type TriggerEffect } from '../protocol';
import { isBounded, isPercent, screenDigits } from '../frames';
import { useItem, useStore } from '../store';
import { Color, Field, Num, Slider, Vec } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { Info } from './Features';
import { partLabel, t } from '../i18n';

const SOURCES: { value: ScreenSource; label: string }[] = [
  { value: 'ammoClip', get label() { return t('attachInspector.k1'); } },
  { value: 'ammoReserve', get label() { return t('attachInspector.k2'); } },
  { value: 'ammoTotal', get label() { return t('attachInspector.k3'); } },
  { value: 'kills', get label() { return t('attachInspector.k4'); } },
  { value: 'damageDealt', get label() { return t('attachInspector.damageDealt'); } },
  { value: 'damageDealtAll', get label() { return t('attachInspector.damageDealtAll'); } },
  { value: 'damageTaken', get label() { return t('attachInspector.damageTaken'); } },
  { value: 'accuracy', get label() { return t('attachInspector.accuracy'); } },
  { value: 'health', get label() { return t('attachInspector.health'); } },
  { value: 'infection', get label() { return t('attachInspector.infection'); } },
  { value: 'static', get label() { return t('attachInspector.k5'); } },
];

/** What 自动 picks for this weapon, or that the weapon's ammo is unknown here. */
function autoDigits(s: Screen, item: CatalogItem | undefined): string {
  const { digits, max } = screenDigits(s, item);
  if (s.source === 'kills') return t('attachInspector.autoKills');
  if (isPercent(s.source)) return t('attachInspector.autoPercent');
  if (!isBounded(s.source)) return t('attachInspector.autoDamage');
  return max != null ? t('attachInspector.autoDigits', { v0: max, v1: digits }) : t('attachInspector.autoUnknown', { v0: digits });
}

const PICK_FILE = '~file';
const DIGITS = '0123456789';

/** Screen font: bundled fonts by group, installed fonts, or a font file; each option shows the digits in its own font. */
function FontPicker({ value, onChange }: { value: string | null; onChange: (font: string | null) => void }) {
  const [system, setSystem] = useState<string[] | null>(null);
  const current = value ?? DEFAULT_FONT;
  const files = isFontFile(current) ? [{ value: current, label: baseName(current) }] : [];
  const bundled = new Set(FONT_GROUPS.flatMap((g) => g.fonts.map((f) => f.family)));
  const sys = (system ?? []).filter((f) => !bundled.has(f));
  if (!isFontFile(current) && !bundled.has(current) && !sys.includes(current)) sys.unshift(current);
  const data = [
    ...FONT_GROUPS.map((g) => ({ group: g.group, items: g.fonts.map((f) => f.family) })),
    { get group() { return t('attachInspector.k6'); }, items: system ? sys : [...sys, { value: '~loading', get label() { return t('attachInspector.k7'); }, disabled: true }] },
    { get group() { return t('attachInspector.k8'); }, items: [...files, { value: PICK_FILE, get label() { return t('attachInspector.k9'); } }] },
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
          const [f] = await window.forge.openDialog('files', [{ get name() { return t('attachInspector.k10'); }, extensions: ['ttf', 'otf', 'woff', 'woff2'] }]);
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

/** Attachment effects: flash the emission or show the attachment for a few seconds when a game event happens. */
function Effects({ a }: { a: Attachment }) {
  const triggers = triggersFor(useItem()).filter((x) => x.value !== 'always');
  const { updateAttachment } = useStore.getState();
  const effects = (a.effects ?? []).filter((e): e is TriggerEffect => !!e);
  const up = (i: number, fn: (e: TriggerEffect) => void, key?: string) =>
    updateAttachment(a.id!, (x) => void fn(x.effects![i]!), key && `${a.id}-fx${i}-${key}`);
  return (
    <div className="section">
      <h3>
        <span>{t('attachInspector.k11')}</span>
        <Info label={t('attachInspector.k13')} />
        <span className="grow" />
        <Button
          size="compact-xs"
          variant="subtle"
          leftSection={<IconPlus size={12} />}
          onClick={() => updateAttachment(a.id!, (x) => void (x.effects = [...(x.effects ?? []), { trigger: 'kill', action: 'flash', color: [1, 0.3, 0.2], intensity: 4, seconds: 0.4 }]))}
        >
          {t('attachInspector.k12')}
        </Button>
      </h3>
      {effects.map((e, i) => (
        <div key={i} className="effect">
          <Group gap="xs" wrap="nowrap">
            <Select size="xs" w={96} value={e.trigger ?? 'kill'} data={triggers} allowDeselect={false} onChange={(v) => v && up(i, (x) => void (x.trigger = v as Trigger))} />
            <SegmentedControl size="xs" value={e.action ?? 'flash'} data={[{ value: 'flash', get label() { return t('attachInspector.k14'); } }, { value: 'show', get label() { return t('attachInspector.k15'); } }]} onChange={(v) => up(i, (x) => void (x.action = v as 'flash' | 'show'))} />
            <Num w={78} value={e.seconds ?? 0.4} step={0.1} decimalScale={2} min={0.05} rightSection={t('attachInspector.k16')} onChange={(v) => up(i, (x) => void (x.seconds = v), 's')} />
            <ActionIcon variant="subtle" color="gray" size="sm" ml="auto" aria-label={t('attachInspector.k17')} onClick={() => updateAttachment(a.id!, (x) => void x.effects!.splice(i, 1))}>
              <IconX size={13} />
            </ActionIcon>
          </Group>
          {e.action !== 'show' && (
            <Group gap="xs" mt={6} wrap="nowrap">
              <div style={{ flex: 1 }}>
                <Color value={[...(e.color ?? [1, 1, 1]).slice(0, 3), 1]} onChange={(c) => up(i, (x) => void (x.color = [c[0], c[1], c[2]]), 'c')} />
              </div>
              <Num w={92} value={e.intensity ?? 4} step={0.5} decimalScale={1} min={0} leftSection={t('attachInspector.k18')} leftSectionWidth={38} onChange={(v) => up(i, (x) => void (x.intensity = v), 'k')} />
            </Group>
          )}
        </div>
      ))}
    </div>
  );
}

const MM = 1000;

function AttachmentPanel({ a }: { a: Attachment }) {
  const item = useItem();
  const { updateAttachment, removeAttachment } = useStore.getState();
  const up = (fn: (a: Attachment) => void, key?: string) => updateAttachment(a.id!, fn, key && `${a.id}-${key}`);
  const upScreen = (fn: (s: Screen) => void, key?: string) => up((x) => void (x.screen && fn(x.screen)), key);
  const s = a.screen;
  return (
    <>
      <div className="insp-head">
        <div className="kind">{s ? t('attachInspector.k19') : t('attachInspector.k20')}</div>
        <TextInput mt={4} size="sm" fw={650} value={a.name ?? ''} onChange={(e) => up((x) => void (x.name = e.currentTarget.value), 'name')} />
      </div>
      <div className="section">
        <h3>{t('attachInspector.k21')}</h3>
        <Field label={t('attachInspector.k22')}>
          <Select value={a.part ?? ''} onChange={(v) => v && up((x) => void (x.part = v))} data={item?.parts.map((p) => ({ value: p.id, label: partLabel(p) })) ?? []} />
        </Field>
        <Switch mt="sm" label={t('attachInspector.k23')} checked={!!a.faceCamera} onChange={(e) => up((x) => void (x.faceCamera = e.currentTarget.checked))} />
      </div>
      <Effects a={a} />
      <SelectionLayout />
      {s && (
        <div className="section">
          <h3>{t('attachInspector.k19')}</h3>
          <Field label={t('attachInspector.k15')}>
            <Select
              value={s.source ?? 'ammoClip'}
              data={SOURCES}
              onChange={(v) =>
                v &&
                upScreen((x) => {
                  x.source = v as ScreenSource;
                  if (!isBounded(x.source)) x.style = 'digits'; // only a source with a maximum can fill a bar
                })
              }
            />
          </Field>
          {isBounded(s.source) && (
            <Field label={t('attachInspector.style')}>
              <SegmentedControl
                size="xs"
                value={s.style ?? 'digits'}
                data={[{ value: 'digits', get label() { return t('attachInspector.styleDigits'); } }, { value: 'bar', get label() { return t('attachInspector.styleBar'); } }]}
                onChange={(v) => upScreen((x) => void (x.style = v as 'digits' | 'bar'))}
              />
            </Field>
          )}
          {s.style === 'bar' && isBounded(s.source) ? null : s.source === 'static' ? (
            <Field label={t('attachInspector.k24')}>
              <TextInput
                value={s.staticText ?? ''}
                onChange={(e) => {
                  const text = e.currentTarget.value;
                  upScreen((x) => void (x.staticText = text), 'text');
                }}
              />
            </Field>
          ) : (
            <Field label={t('attachInspector.k26')}>
              <Group gap="sm" wrap="nowrap">
                <Select
                  w={110}
                  value={String(s.digits ?? 0)}
                  data={[{ value: '0', label: t('attachInspector.k25') }, ...['1', '2', '3', '4', '5', '6'].map((d) => ({ value: d, label: d }))]}
                  onChange={(v) => v && upScreen((x) => void (x.digits = Number(v)))}
                />
                <Checkbox size="xs" label={t('attachInspector.k27')} checked={s.padZero !== false} onChange={(e) => upScreen((x) => void (x.padZero = e.currentTarget.checked))} />
                {!s.digits && <Info label={autoDigits(s, item)} />}
              </Group>
            </Field>
          )}
          <Field label={t('attachInspector.k28')}>
            <Vec value={s.size ?? [0.03, 0.015]} labels={[t('attachInspector.k29'), t('attachInspector.k30')]} scale={MM} step={0.5} decimalScale={1} min={0.5} onChange={(v) => upScreen((x) => void (x.size = v), 'size')} />
          </Field>
          <Field label={t('attachInspector.k31')}>
            <Num value={s.surfaceGap ?? 0.0005} scale={MM} step={0.1} min={0} onChange={(v) => upScreen((x) => void (x.surfaceGap = v), 'gap')} />
          </Field>
          <Field label={t('attachInspector.k32')}>
            <Slider value={s.textScale ?? 0.8} min={0.2} max={1} onChange={(v) => upScreen((x) => void (x.textScale = v), 'ts')} />
          </Field>
          <Field label={t('attachInspector.k33')}>
            <Slider value={s.glow ?? 1.5} min={0} max={5} step={0.05} onChange={(v) => upScreen((x) => void (x.glow = v), 'glow')} />
          </Field>
          <Switch mt="xs" label={t('attachInspector.glowInGame')} checked={s.glowInGame !== false} onChange={(e) => upScreen((x) => void (x.glowInGame = e.currentTarget.checked))} />
          <Field label={t('attachInspector.k10')}>
            <FontPicker value={s.font ?? null} onChange={(f) => upScreen((x) => void (x.font = f))} />
          </Field>
        </div>
      )}
      {s && (
        <div className="section">
          <h3>{t('attachInspector.k34')}</h3>
          <Field label={t('attachInspector.k24')}>
            <Color value={s.textColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.textColor = [c[0], c[1], c[2], 1]), 'tc')} />
          </Field>
          <Field label={t('attachInspector.k35')}>
            <Color alpha value={s.panelColor ?? [0, 0, 0, 0.85]} onChange={(c) => upScreen((x) => void (x.panelColor = c), 'pc')} />
          </Field>
          <Field label={t('attachInspector.k36')}>
            <Color alpha value={s.borderColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.borderColor = c), 'bc')} />
          </Field>
          <Field label={t('attachInspector.k37')}>
            <Num value={s.borderWidth ?? 0.0008} scale={MM} step={0.1} min={0} onChange={(v) => upScreen((x) => void (x.borderWidth = v), 'bw')} />
          </Field>
          <Field label={t('attachInspector.k38')}>
            <Group gap="sm" wrap="nowrap">
              <Switch checked={!!s.lowAmmoColor} onChange={(e) => {
                const on = e.currentTarget.checked;
                upScreen((x) => void (x.lowAmmoColor = on ? [1, 0.35, 0.25, 1] : null));
              }} />
              {s.lowAmmoColor && (
                <div style={{ flex: 1 }}>
                  <Color value={s.lowAmmoColor} onChange={(c) => upScreen((x) => void (x.lowAmmoColor = [c[0], c[1], c[2], 1]), 'lc')} />
                </div>
              )}
            </Group>
          </Field>
          {s.lowAmmoColor && (
            <Field label={t('attachInspector.k39')}>
              <Slider value={s.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} onChange={(v) => upScreen((x) => void (x.lowAmmoFraction = v), 'lf')} />
            </Field>
          )}
          {s.lowAmmoColor && (
            <Switch mt="xs" label={t('attachInspector.lowGradient')} checked={!!s.lowAmmoGradient} onChange={(e) => upScreen((x) => void (x.lowAmmoGradient = e.currentTarget.checked))} />
          )}
          {s.lowAmmoColor && s.lowAmmoGradient && (
            <Field label={t('attachInspector.gradientStart')}>
              <Slider
                value={Math.max((s.lowAmmoGradientStart ?? 0.4) * 100, Math.round((s.lowAmmoFraction ?? 0.2) * 100) + 1)}
                min={Math.round((s.lowAmmoFraction ?? 0.2) * 100) + 1}
                max={100}
                step={1}
                label={(v) => `${v.toFixed(0)}%`}
                onChange={(v) => upScreen((x) => void (x.lowAmmoGradientStart = v / 100), 'gs')}
              />
            </Field>
          )}
          <p className="hint" style={{ marginTop: 8 }}>
            {t('attachInspector.k40')}
          </p>
        </div>
      )}
      {a.material && (
        <div className="section">
          <h3>{t('attachInspector.k41')}</h3>
          <Field label={t('attachInspector.k34')}>
            <Color value={a.material.color ?? [1, 1, 1, 1]} onChange={(c) => up((x) => void (x.material && (x.material.color = [c[0], c[1], c[2], 1])), 'mc')} />
          </Field>
          <Switch mt="xs" label={t('attachInspector.k42')} checked={!!a.material.unlit} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((x) => void (x.material && (x.material.unlit = on)));
          }} />
        </div>
      )}
      <div className="section">
        <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={() => removeAttachment(a.id!)}>
          {t('attachInspector.k43')}
        </Button>
      </div>
    </>
  );
}

export function AttachInspector() {
  const selection = useStore((s) => s.selection);
  const a = useStore((s) => (selection?.kind === 'attachment' ? s.doc.attachments?.find((x) => x?.id === selection.id) : undefined));
  return a ? <AttachmentPanel a={a} /> : null;
}
