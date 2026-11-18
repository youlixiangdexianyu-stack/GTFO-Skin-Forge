// The selected code effect (SkinDocument.codeEffects): where it draws, which program with which numbers and colours, its size,
// turn, spin and strength, how it blends, when it plays, and which of these the player may change in game (the skin picker's
// ADJUST page). Ranges, steps and names come from ForgeCore's program catalogue, the one the game checks packs against.
import { ActionIcon, Button, Group, SegmentedControl, Select, Tooltip } from '@mantine/core';
import { IconAdjustmentsHorizontal, IconBolt, IconClock, IconEye, IconEyeOff, IconMapPin, IconTrash, IconUser } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { removeCodeEffect, updateCodeEffect } from '../codeFx';
import { resolveFx, type FxLabel } from '../codeFxValues';
import { triggersFor, type CodeEffect, type Trigger } from '../protocol';
import { attachmentName, useItem, useStore } from '../store';
import { FX_CATALOG } from '../viewport/CodeEffects';
import { simulate } from '../viewport/refs';
import { Color, Field, Num, Slider } from './fields';
import { FEATURE, InspHead, Section } from './Features';
import { CHANNEL_LABEL } from './SkinInspector';
import { partLabel, t, useLang } from '../i18n';

type TweakList = 'params' | 'colors' | 'transform';

/** A control with the person button that lets players change it in game (lit while they may). */
function Tweakable({ label, on, onToggle, children }: { label: string; on: boolean; onToggle: (on: boolean) => void; children: ReactNode }) {
  return (
    <Field label={label}>
      <Group gap={8} wrap="nowrap">
        <div style={{ flex: 1, minWidth: 0 }}>{children}</div>
        <Tooltip label={`${t('codeFx.player')}: ${t('codeFx.playerTip')}`} multiline w={260}>
          <ActionIcon
            variant={on ? 'filled' : 'default'}
            color="grape"
            size="input-xs"
            role="checkbox"
            aria-checked={on}
            aria-label={t('codeFx.playerFor', { v0: label })}
            onClick={() => onToggle(!on)}
          >
            <IconUser size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>
    </Field>
  );
}

export function CodeFxInspector() {
  const id = useStore((s) => (s.selection?.kind === 'fx' ? s.selection.id : null));
  const e = useStore((s) => s.doc.codeEffects?.find((x) => x?.id === id) ?? undefined);
  const doc = useStore((s) => s.doc);
  const attachments = doc.attachments;
  const item = useItem();
  const lang = useLang();
  if (!id || !e) return null;
  const name = (l: FxLabel) => (lang === 'zh' ? l.zh : l.en);
  const program = FX_CATALOG.programs.find((p) => p.name === e.program) ?? FX_CATALOG.programs[0];
  const values = resolveFx(FX_CATALOG, { ...e, program: program.name })!;
  const up = (fn: (x: CodeEffect) => void, key?: string) => updateCodeEffect(id, fn, key);
  const tweaked = (list: TweakList, n: string) => !!e.playerTweaks?.[list]?.includes(n);
  const tweak = (list: TweakList, n: string, on: boolean) =>
    up((x) => {
      const tw = (x.playerTweaks ??= { params: [], colors: [], transform: [] });
      const next = (tw[list] ?? []).filter((v) => v !== n);
      if (on) next.push(n);
      tw[list] = next;
      if (!tw.params?.length && !tw.colors?.length && !tw.transform?.length) x.playerTweaks = null;
    });
  // A material several parts share (a revolver's shells) is one target, named after the first part that uses it.
  const materials = new Map<string, string>();
  for (const p of item?.parts ?? []) for (const m of p.materials) if (!materials.has(m)) materials.set(m, `${partLabel(p)} · ${m}`);
  const targets = [
    ...[...materials].map(([m, label]) => ({ value: `m:${m}`, label })),
    ...(attachments ?? []).filter((a) => !!a?.mesh).map((a) => ({ value: `a:${a!.id}`, label: t('codeFx.onAttachment', { v0: attachmentName(doc, a!) }) })),
  ];
  const target = e.material ? `m:${e.material}` : `a:${e.attachment}`;
  // The game draws an effect on an attached model only while the model is shown.
  const hidden = !e.material ? attachments?.find((a) => a?.id === e.attachment && a?.visible === false) : undefined;
  const event = e.trigger && e.trigger !== 'always';
  const transform = [e.scale ?? 1, e.rotation ?? 0, e.spin ?? 0, e.intensity ?? 1];
  const setTransform = (i: number, v: number) => up((x) => void (i === 0 ? (x.scale = v) : i === 1 ? (x.rotation = v) : i === 2 ? (x.spin = v) : (x.intensity = v)), `t${i}`);

  const hiddenFx = e.visible === false;
  return (
    <>
      <InspHead
        kind={t('codeFx.title')}
        icon={FEATURE.fx.icon}
        color={FEATURE.fx.color}
        name={{ value: e.name ?? '', label: t('codeFx.name'), onChange: (n) => up((y) => void (y.name = n), 'name') }}
        actions={
          <>
            <Tooltip label={t('dynamicInspector.k31')}>
              <ActionIcon
                variant="subtle"
                color={hiddenFx ? 'dark.2' : 'violet'}
                aria-label={t('dynamicInspector.k31')}
                aria-pressed={!hiddenFx}
                onClick={() => up((y) => void (y.visible = hiddenFx))}
              >
                {hiddenFx ? <IconEyeOff size={16} /> : <IconEye size={16} />}
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('codeFx.remove')}>
              <ActionIcon variant="subtle" color="red" aria-label={t('codeFx.remove')} onClick={() => removeCodeEffect(id)}>
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </>
        }
      />
      <Section id="fx-where" title={t('codeFx.where')} icon={IconMapPin} color="var(--scan)" info={t('codeFx.whereInfo')}>
        <Field label={t('codeFx.target')}>
          <Select
            searchable
            data={targets}
            value={target}
            allowDeselect={false}
            onChange={(v) =>
              v &&
              up((x) => {
                if (v.startsWith('m:')) {
                  x.material = v.slice(2);
                  x.attachment = null;
                  x.channel ??= 'albedo';
                } else {
                  x.attachment = v.slice(2);
                  x.material = null;
                }
              })
            }
          />
        </Field>
        {hidden && <p className="hint">{t('codeFx.attachmentHidden', { v0: attachmentName(doc, hidden) })}</p>}
        {e.material && (
          <Field label={t('codeFx.channel')}>
            <SegmentedControl
              fullWidth
              size="xs"
              value={e.channel === 'emissive' ? 'emissive' : 'albedo'}
              onChange={(v) => up((x) => void (x.channel = v as 'albedo' | 'emissive'))}
              data={[
                { value: 'albedo', label: CHANNEL_LABEL.albedo },
                { value: 'emissive', label: CHANNEL_LABEL.emissive },
              ]}
            />
          </Field>
        )}
      </Section>
      <Section id="fx-program" feature="fx" title={t('codeFx.program')} info={t('codeFx.programInfo')}>
        <Field label={t('codeFx.program')}>
          <Select
            data={FX_CATALOG.programs.map((p) => ({ value: p.name, label: name(p.label) }))}
            value={program.name}
            allowDeselect={false}
            onChange={(v) =>
              v &&
              v !== program.name &&
              up((x) => {
                // Numbers and colours are named per program: a new program starts at its own defaults.
                x.program = v;
                x.params = {};
                x.colors = {};
                if (x.playerTweaks) x.playerTweaks = x.playerTweaks.transform?.length ? { params: [], colors: [], transform: x.playerTweaks.transform } : null;
              })
            }
          />
        </Field>
        {program.params.map((p, i) => (
          <Tweakable key={p.name} label={name(p.label)} on={tweaked('params', p.name)} onToggle={(on) => tweak('params', p.name, on)}>
            <Slider
              value={values.params[i]}
              min={p.min}
              max={p.max}
              step={p.step || 0.01}
              label={(v) => v.toFixed(p.step >= 1 ? 0 : 2)}
              onChange={(v) => up((x) => void ((x.params ??= {})[p.name] = v), `p-${p.name}`)}
            />
          </Tweakable>
        ))}
        {program.colours.map((c, i) => (
          <Tweakable key={c.name} label={name(c.label)} on={tweaked('colors', c.name)} onToggle={(on) => tweak('colors', c.name, on)}>
            <Color alpha value={values.colors[i]} onChange={(v) => up((x) => void ((x.colors ??= {})[c.name] = v), `c-${c.name}`)} />
          </Tweakable>
        ))}
      </Section>
      <Section id="fx-transform" title={t('codeFx.transform')} icon={IconAdjustmentsHorizontal} color={FEATURE.fx.color}>
        {FX_CATALOG.transform.map((p, i) => (
          <Tweakable key={p.name} label={name(p.label)} on={tweaked('transform', p.name)} onToggle={(on) => tweak('transform', p.name, on)}>
            <Slider value={transform[i]} min={p.min} max={p.max} step={p.step || 0.01} label={(v) => v.toFixed(p.step >= 1 ? 0 : 2)} onChange={(v) => setTransform(i, v)} />
          </Tweakable>
        ))}
        <Field label={t('codeFx.blend')}>
          <SegmentedControl
            fullWidth
            size="xs"
            value={e.blend ?? 'alpha'}
            onChange={(v) => up((x) => void (x.blend = v as 'alpha' | 'additive'))}
            data={[
              { value: 'alpha', label: t('codeFx.blendAlpha') },
              { value: 'additive', label: t('codeFx.blendAdd') },
            ]}
          />
        </Field>
      </Section>
      <Section id="fx-when" title={t('codeFx.when')} icon={IconClock} color="var(--warn)">
        <Field label={t('dynamicInspector.k18')}>
          <Select value={e.trigger ?? 'always'} data={triggersFor(item)} allowDeselect={false} onChange={(v) => v && up((x) => void (x.trigger = v as Trigger))} />
        </Field>
        {event && (
          <>
            <Field label={t('codeFx.seconds')}>
              <Num value={e.seconds ?? 1} min={0.1} max={60} step={0.1} decimalScale={1} suffix=" s" onChange={(v) => up((x) => void (x.seconds = v), 'sec')} />
            </Field>
            {e.trigger === 'lowAmmo' && (
              <Field label={t('dynamicInspector.k21')}>
                <Slider value={e.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} onChange={(v) => up((x) => void (x.lowAmmoFraction = v), 'laf')} />
              </Field>
            )}
            <Button mt={10} size="compact-sm" variant="light" leftSection={<IconBolt size={14} />} onClick={() => simulate(e.trigger as Trigger)}>
              {t('codeFx.tryEvent')}
            </Button>
          </>
        )}
      </Section>
    </>
  );
}
