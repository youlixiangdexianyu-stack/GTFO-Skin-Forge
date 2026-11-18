import { ActionIcon, Button, Checkbox, Group, SegmentedControl, Select, Switch, TextInput, Tooltip } from '@mantine/core';
import {
  IconAnchor,
  IconArrowsMove,
  IconBox,
  IconDeviceDesktop,
  IconPalette,
  IconPaperclip,
  IconPlus,
  IconRotate,
  IconRun,
  IconSparkles,
  IconSphere,
  IconTrash,
  IconWind,
  IconX,
} from '@tabler/icons-react';
import { CHARM_DEFAULTS, maxSegments, type CharmSettings } from '../viewport/charm';
import { InspectorTabs } from './InspectorTabs';
import { newHologram } from '../hologram';
import { FontPicker } from './FontPicker';
import { attachmentMaterial, triggersFor, type Attachment, type CatalogItem, type Screen, type ScreenSource, type Trigger, type TriggerEffect } from '../protocol';
import { isBounded, isPercent, screenDigits } from '../frames';
import { attachmentName, materialOf, newCharm, useItem, useStore } from '../store';
import { layersOf } from '../livery';
import { PartLivery } from './LiveryPanel';
import { PartLayers } from './DynamicInspector';
import { removeEffect } from '../playerColors';
import { previewEvents, type CharmMotion } from '../viewport/refs';
import { Color, Field, Num, Slider, Vec } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { useSoundsTab } from './SkinInspector';
import { Empty, FEATURE, Info, InspHead, KindGrid, Section } from './Features';
import { ShapeSection } from './ShapePanel';
import { partLabel, t } from '../i18n';
import { pieceLabel } from '../pieces';

const SOURCES: { value: ScreenSource; label: string }[] = [
  {
    value: 'ammoClip',
    get label() {
      return t('attachInspector.k1');
    },
  },
  {
    value: 'ammoReserve',
    get label() {
      return t('attachInspector.k2');
    },
  },
  {
    value: 'ammoTotal',
    get label() {
      return t('attachInspector.k3');
    },
  },
  {
    value: 'kills',
    get label() {
      return t('attachInspector.k4');
    },
  },
  {
    value: 'damageDealt',
    get label() {
      return t('attachInspector.damageDealt');
    },
  },
  {
    value: 'damageDealtAll',
    get label() {
      return t('attachInspector.damageDealtAll');
    },
  },
  {
    value: 'damageTaken',
    get label() {
      return t('attachInspector.damageTaken');
    },
  },
  {
    value: 'accuracy',
    get label() {
      return t('attachInspector.accuracy');
    },
  },
  {
    value: 'health',
    get label() {
      return t('attachInspector.health');
    },
  },
  {
    value: 'infection',
    get label() {
      return t('attachInspector.infection');
    },
  },
  {
    value: 'static',
    get label() {
      return t('attachInspector.k5');
    },
  },
];

/** What 自动 picks for this weapon, or that the weapon's ammo is unknown here. */
function autoDigits(s: Screen, item: CatalogItem | undefined): string {
  const { digits, max } = screenDigits(s, item);
  if (s.source === 'kills') return t('attachInspector.autoKills');
  if (isPercent(s.source)) return t('attachInspector.autoPercent');
  if (!isBounded(s.source)) return t('attachInspector.autoDamage');
  return max != null ? t('attachInspector.autoDigits', { v0: max, v1: digits }) : t('attachInspector.autoUnknown', { v0: digits });
}

type BorderStyle = NonNullable<Screen['borderStyle']>;
const BORDER_STYLES: BorderStyle[] = ['solid', 'corners', 'double', 'notched', 'ticks'];

/** A hologram's look: flicker, how much light it gives, a second colour up the model, scanlines and a lit outline. */
function HologramFields({ a, up }: { a: Attachment; up: (fn: (a: Attachment) => void, key?: string) => void }) {
  const h = a.hologram!;
  const set = (fn: (h: NonNullable<Attachment['hologram']>) => void, key?: string) => up((x) => void (x.hologram && fn(x.hologram)), key);
  const fx = !!a.mesh;
  return (
    <>
      <Field label={t('hologram.flicker')}>
        <Slider value={h.flicker ?? 0.3} min={0} max={1} onChange={(v) => set((x) => void (x.flicker = v), 'flicker')} />
      </Field>
      <Field label={t('hologram.opacity')}>
        <Slider value={h.opacity ?? 1} min={0} max={1} onChange={(v) => set((x) => void (x.opacity = v), 'opacity')} />
      </Field>
      {fx && (
        <>
          <div className="switch-row" style={{ marginTop: 8 }}>
            <Switch
              color="cyan"
              label={t('hologram.ramp')}
              checked={!!h.color2}
              onChange={(e) => {
                const on = e.currentTarget.checked;
                set((x) => void (x.color2 = on ? [1, 0.35, 0.85] : null));
              }}
            />
            <Info label={t('hologram.rampInfo')} />
          </div>
          {h.color2 && (
            <Field label={t('hologram.topColor')}>
              <Color value={h.color2.map((x) => x ?? 1)} onChange={(c) => set((x) => void (x.color2 = c.slice(0, 3)), 'color2')} />
            </Field>
          )}
          <Field label={t('hologram.scanlines')}>
            <Slider value={h.scanlines ?? 0} min={0} max={1} onChange={(v) => set((x) => void (x.scanlines = v), 'scan')} />
          </Field>
          <Field label={t('hologram.edgeGlow')}>
            <Slider value={h.edgeGlow ?? 0} min={0} max={1} onChange={(v) => set((x) => void (x.edgeGlow = v), 'edge')} />
          </Field>
        </>
      )}
    </>
  );
}

/** Attachment effects: flash the emission or show the attachment for a few seconds when a game event happens. */
function Effects({ a }: { a: Attachment }) {
  const triggers = triggersFor(useItem()).filter((x) => x.value !== 'always');
  const { updateAttachment } = useStore.getState();
  const effects = (a.effects ?? []).filter((e): e is TriggerEffect => !!e);
  const up = (i: number, fn: (e: TriggerEffect) => void, key?: string) => updateAttachment(a.id!, (x) => void fn(x.effects![i]!), key && `${a.id}-fx${i}-${key}`);
  return (
    <Section
      feature="fx"
      title={t('attachInspector.k11')}
      info={t('attachInspector.k13')}
      actions={
        <Button
          size="compact-xs"
          variant="light"
          color="violet"
          leftSection={<IconPlus size={12} />}
          onClick={() => updateAttachment(a.id!, (x) => void (x.effects = [...(x.effects ?? []), { trigger: 'kill', action: 'flash', color: [1, 0.3, 0.2], intensity: 4, seconds: 0.4 }]))}
        >
          {t('attachInspector.k12')}
        </Button>
      }
    >
      {!effects.length && <Empty>{t('attachInspector.k13')}</Empty>}
      {effects.map((e, i) => (
        <div key={i} className="effect">
          <Group gap={6} wrap="nowrap">
            <Select style={{ flex: 1 }} value={e.trigger ?? 'kill'} data={triggers} allowDeselect={false} onChange={(v) => v && up(i, (x) => void (x.trigger = v as Trigger))} />
            <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('attachInspector.k17')} onClick={() => useStore.getState().edit((d) => removeEffect(d, a.id!, i))}>
              <IconX size={13} />
            </ActionIcon>
          </Group>
          <Group gap={6} mt={6} wrap="nowrap">
            <SegmentedControl
              style={{ flex: 1 }}
              size="xs"
              value={e.action ?? 'flash'}
              data={[
                {
                  value: 'flash',
                  get label() {
                    return t('attachInspector.k14');
                  },
                },
                {
                  value: 'show',
                  get label() {
                    return t('attachInspector.k15');
                  },
                },
              ]}
              onChange={(v) => up(i, (x) => void (x.action = v as 'flash' | 'show'))}
            />
            <Num
              w={76}
              hideControls
              value={e.seconds ?? 0.3}
              step={0.1}
              decimalScale={2}
              min={0.05}
              suffix={` ${t('attachInspector.k16')}`}
              onChange={(v) => up(i, (x) => void (x.seconds = v), 's')}
            />
          </Group>
          {e.action !== 'show' && (
            <Group gap={6} mt={6} wrap="nowrap">
              <div style={{ flex: 1, minWidth: 0 }}>
                <Color
                  slot={{ kind: 'effectColor', target: a.id, effect: i }}
                  value={[...(e.color ?? [1, 1, 1]).slice(0, 3), 1]}
                  onChange={(c) => up(i, (x) => void (x.color = [c[0], c[1], c[2]]), 'c')}
                />
              </div>
              <Tooltip label={t('attachInspector.k18')}>
                <Num
                  w={76}
                  hideControls
                  value={e.intensity ?? 1}
                  step={0.5}
                  decimalScale={1}
                  min={0}
                  leftSection={<IconSparkles size={13} />}
                  aria-label={t('attachInspector.k18')}
                  onChange={(v) => up(i, (x) => void (x.intensity = v), 'k')}
                />
              </Tooltip>
            </Group>
          )}
        </div>
      ))}
    </Section>
  );
}

const MM = 1000;
const CM = 100;

const MOTIONS: { motion: CharmMotion; icon: typeof IconWind }[] = [
  { motion: 'shake', icon: IconWind },
  { motion: 'run', icon: IconRun },
  { motion: 'turn', icon: IconRotate },
];

/** Charm: a rope or chain from the attachment's origin with the model hanging on its end; the game's rope (the backend's charm.simulate). */
function CharmSection({ a }: { a: Attachment }) {
  const { updateAttachment } = useStore.getState();
  const c = a.charm;
  const set = <K extends keyof CharmSettings>(k: K, v: CharmSettings[K], key = true) =>
    updateAttachment(a.id!, (x) => void (x.charm && (x.charm[k] = v as never)), key ? `${a.id}-charm-${k}` : undefined);
  return (
    <Section
      title={t('charm.title')}
      icon={IconPaperclip}
      color="var(--warn)"
      info={c ? `${t('charm.info')} ${t('charm.pointHint')}` : t('charm.info')}
      actions={
        <Switch
          aria-label={t('charm.title')}
          checked={!!c}
          onChange={(e) => {
            const on = e.currentTarget.checked;
            updateAttachment(a.id!, (x) => void (x.charm = on ? newCharm() : null));
          }}
        />
      }
    >
      {c && (
        <>
          <SegmentedControl
            mb={10}
            fullWidth
            size="xs"
            value={c.style ?? CHARM_DEFAULTS.style}
            data={(['cord', 'chain', 'beads'] as const).map((v) => ({ value: v, label: t(`charm.${v}`) }))}
            onChange={(v) => set('style', v as CharmSettings['style'], false)}
          />
          <Field label={t('charm.length')}>
            <Slider value={(c.length ?? CHARM_DEFAULTS.length) * CM} min={0.5} max={30} step={0.1} unit=" cm" onChange={(v) => set('length', v / CM)} />
          </Field>
          <Field label={t('charm.thickness')}>
            <Slider value={(c.thickness ?? CHARM_DEFAULTS.thickness) * MM} min={0.3} max={10} step={0.1} unit=" mm" onChange={(v) => set('thickness', v / MM)} />
          </Field>
          <Field label={t('charm.color')}>
            <Color value={c.color ?? CHARM_DEFAULTS.color} onChange={(v) => set('color', v.slice(0, 3))} />
          </Field>
          <Field label={t('charm.weight')}>
            <Slider value={c.weight ?? CHARM_DEFAULTS.weight} min={0.1} max={3} step={0.05} onChange={(v) => set('weight', v)} />
          </Field>
          <Field label={t('charm.damping')}>
            <Slider value={c.damping ?? CHARM_DEFAULTS.damping} onChange={(v) => set('damping', v)} />
          </Field>
          <Field label={t('charm.stiffness')}>
            <Slider value={c.stiffness ?? CHARM_DEFAULTS.stiffness} onChange={(v) => set('stiffness', v)} />
          </Field>
          <Field label={t('charm.segments')}>
            <Slider value={c.segments ?? CHARM_DEFAULTS.segments} min={1} max={maxSegments(c.length ?? CHARM_DEFAULTS.length)} step={1} onChange={(v) => set('segments', Math.round(v))} />
          </Field>
          <div className="switch-row" style={{ marginTop: 10 }}>
            <Switch label={t('charm.collide')} checked={c.collide ?? CHARM_DEFAULTS.collide} onChange={(e) => set('collide', e.currentTarget.checked, false)} />
          </div>
          <div style={{ height: 12 }} />
          <KindGrid
            kinds={MOTIONS.map(({ motion, icon }) => ({ id: motion, label: t(`charm.${motion}`), icon, tip: t(`charm.${motion}Info`) }))}
            color="var(--warn)"
            onPick={(motion) => void (previewEvents.charm = { motion, at: performance.now() })}
          />
        </>
      )}
    </Section>
  );
}

function AttachmentPanel({ a }: { a: Attachment }) {
  const item = useItem();
  const subParts = useStore((s) => s.subParts);
  const paint = useStore((s) => materialOf(s.doc, attachmentMaterial(a.id ?? '')));
  const { updateAttachment, removeAttachment } = useStore.getState();
  const up = (fn: (a: Attachment) => void, key?: string) => updateAttachment(a.id!, fn, key && `${a.id}-${key}`);
  const upScreen = (fn: (s: Screen) => void, key?: string) => up((x) => void (x.screen && fn(x.screen)), key);
  const s = a.screen;
  const sounds = useSoundsTab();
  // 位置: charm first (where and how it hangs), where it is mounted and its pose; 外观: shape, hologram, colours; 屏幕: what a screen shows; 特效: events.
  const place = (
    <>
      <CharmSection a={a} />
      <Section title={t('attachInspector.k21')} icon={IconAnchor} color="var(--scan)">
        <Field label={t('attachInspector.k22')}>
          {/* A part, or one of its original pieces (the attachment then follows that piece as it moves). */}
          <Select
            searchable
            value={`${a.part ?? ''}|${a.transform ?? ''}`}
            onChange={(v) => {
              if (!v) return;
              const [part, path] = v.split('|');
              up((x) => void Object.assign(x, { part, transform: path || null }));
            }}
            data={
              item?.parts.map((p) => ({
                group: partLabel(p),
                items: [{ value: `${p.id}|`, label: partLabel(p) }, ...(subParts[p.id] ?? []).map((sp) => ({ value: `${p.id}|${sp.path}`, label: `${partLabel(p)} · ${pieceLabel(sp.path)}` }))],
              })) ?? []
            }
          />
        </Field>
        <div className="switch-row" style={{ marginTop: 10 }}>
          <Switch label={t('attachInspector.k23')} checked={!!a.faceCamera} onChange={(e) => up((x) => void (x.faceCamera = e.currentTarget.checked))} />
        </div>
      </Section>
      <SelectionLayout />
    </>
  );
  const look = (
    <>
      <ShapeSection id={a.id!} />
      <Section
        title={t('hologram.title')}
        feature="hologram"
        info={a.hologram && a.mesh ? `${t('hologram.info')} ${t('hologram.fxHint')}` : t('hologram.info')}
        actions={
          <Switch
            aria-label={t('hologram.title')}
            checked={!!a.hologram}
            onChange={(e) => {
              const on = e.currentTarget.checked;
              up((x) => void (x.hologram = on ? newHologram() : null));
            }}
          />
        }
      >
        {a.hologram && <HologramFields a={a} up={up} />}
      </Section>
      {s && (
        <Section id="screen-colours" title={t('attachInspector.k34')} icon={IconPalette} color="var(--scan)" info={t('attachInspector.k40')}>
          <Field label={t('attachInspector.k24')}>
            <Color slot={{ kind: 'screenText', target: a.id }} value={s.textColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.textColor = [c[0], c[1], c[2], 1]), 'tc')} />
          </Field>
          <Field label={t('attachInspector.k35')}>
            <Color alpha slot={{ kind: 'screenPanel', target: a.id }} value={s.panelColor ?? [0, 0, 0, 0.85]} onChange={(c) => upScreen((x) => void (x.panelColor = c), 'pc')} />
          </Field>
          <Field label={t('attachInspector.k36')}>
            <Color alpha slot={{ kind: 'screenBorder', target: a.id }} value={s.borderColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.borderColor = c), 'bc')} />
          </Field>
          <Field label={t('border.style')}>
            <Select
              value={s.borderStyle ?? 'solid'}
              data={BORDER_STYLES.map((v) => ({ value: v, label: t(`border.${v}`) }))}
              allowDeselect={false}
              onChange={(v) => v && upScreen((x) => void (x.borderStyle = v as BorderStyle))}
            />
          </Field>
          <Field label={t('attachInspector.k37')}>
            <Num value={s.borderWidth ?? 0.0008} scale={MM} step={0.1} min={0} suffix=" mm" onChange={(v) => upScreen((x) => void (x.borderWidth = v), 'bw')} />
          </Field>
          <Field label={t('attachInspector.k38')}>
            <Group gap="sm" wrap="nowrap">
              <Switch
                checked={!!s.lowAmmoColor}
                onChange={(e) => {
                  const on = e.currentTarget.checked;
                  upScreen((x) => void (x.lowAmmoColor = on ? [1, 0.35, 0.25, 1] : null));
                }}
              />
              {s.lowAmmoColor && (
                <div style={{ flex: 1 }}>
                  <Color slot={{ kind: 'screenLowAmmo', target: a.id }} value={s.lowAmmoColor} onChange={(c) => upScreen((x) => void (x.lowAmmoColor = [c[0], c[1], c[2], 1]), 'lc')} />
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
            <div className="switch-row" style={{ marginTop: 8 }}>
              <Switch label={t('attachInspector.lowGradient')} checked={!!s.lowAmmoGradient} onChange={(e) => upScreen((x) => void (x.lowAmmoGradient = e.currentTarget.checked))} />
            </div>
          )}
          {s.lowAmmoColor && s.lowAmmoGradient && (
            <Field label={t('attachInspector.gradientStart')}>
              <Slider
                value={Math.max((s.lowAmmoGradientStart ?? 0.4) * 100, Math.round((s.lowAmmoFraction ?? 0.2) * 100) + 1)}
                min={Math.round((s.lowAmmoFraction ?? 0.2) * 100) + 1}
                max={100}
                step={1}
                unit="%"
                onChange={(v) => upScreen((x) => void (x.lowAmmoGradientStart = v / 100), 'gs')}
              />
            </Field>
          )}
        </Section>
      )}
      {a.material && (
        <Section id="model-look" title={t('attachInspector.k41')} icon={IconBox} color="var(--scan)">
          <Field label={t('attachInspector.k34')}>
            <Color
              slot={a.mesh ? { kind: 'attachmentColor', target: a.id } : undefined}
              value={a.material.color ?? [1, 1, 1, 1]}
              onChange={(c) => up((x) => void (x.material && (x.material.color = [c[0], c[1], c[2], 1])), 'mc')}
            />
          </Field>
          <div className="switch-row" style={{ marginTop: 8 }}>
            <Switch
              label={t('attachInspector.k42')}
              checked={!!a.material.unlit}
              onChange={(e) => {
                const on = e.currentTarget.checked;
                up((x) => void (x.material && (x.material.unlit = on)));
              }}
            />
          </div>
        </Section>
      )}
    </>
  );
  const screen = (
    <>
      {s && (
        <Section title={t('attachInspector.k19')} icon={IconDeviceDesktop} color="var(--scan)">
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
                fullWidth
                value={s.style ?? 'digits'}
                data={[
                  {
                    value: 'digits',
                    get label() {
                      return t('attachInspector.styleDigits');
                    },
                  },
                  {
                    value: 'bar',
                    get label() {
                      return t('attachInspector.styleBar');
                    },
                  },
                ]}
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
              <Group gap="sm" wrap="wrap">
                <Select
                  w={96}
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
            <Vec
              value={s.size ?? [0.03, 0.015]}
              labels={[t('attachInspector.k29'), t('attachInspector.k30')]}
              scale={MM}
              step={0.5}
              decimalScale={1}
              min={0.5}
              onChange={(v) => upScreen((x) => void (x.size = v), 'size')}
            />
          </Field>
          <Field label={t('attachInspector.k31')}>
            <Num value={s.surfaceGap ?? 0.0005} scale={MM} step={0.1} min={0} suffix=" mm" onChange={(v) => upScreen((x) => void (x.surfaceGap = v), 'gap')} />
          </Field>
          <Field label={t('attachInspector.k32')}>
            <Slider value={s.textScale ?? 0.8} min={0.2} max={1} onChange={(v) => upScreen((x) => void (x.textScale = v), 'ts')} />
          </Field>
          <Field label={t('attachInspector.k33')}>
            <Slider value={s.glow ?? 1.5} min={0} max={5} step={0.05} onChange={(v) => upScreen((x) => void (x.glow = v), 'glow')} />
          </Field>
          <div className="switch-row" style={{ marginTop: 8 }}>
            <Switch label={t('attachInspector.glowInGame')} checked={s.glowInGame !== false} onChange={(e) => upScreen((x) => void (x.glowInGame = e.currentTarget.checked))} />
          </div>
          <Field label={t('attachInspector.k10')}>
            <FontPicker value={s.font ?? null} onChange={(f) => upScreen((x) => void (x.font = f))} />
          </Field>
        </Section>
      )}
    </>
  );
  const material = attachmentMaterial(a.id ?? '');
  const model = { id: material, materials: [material] };
  return (
    <>
      <InspHead
        kind={s ? t('attachInspector.k19') : t('attachInspector.k20')}
        icon={s ? IconDeviceDesktop : a.hologram ? IconSphere : a.charm ? IconPaperclip : IconBox}
        color={a.hologram ? FEATURE.hologram.color : FEATURE.attachment.color}
        name={{ value: a.name ?? '', placeholder: attachmentName(useStore.getState().doc, a), onChange: (n) => up((x) => void (x.name = n), 'name') }}
        actions={
          <>
            <Tooltip label={t('attachInspector.k43')}>
              <ActionIcon variant="subtle" color="red" aria-label={t('attachInspector.k43')} onClick={() => removeAttachment(a.id!)}>
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </>
        }
      />
      <InspectorTabs
        id="attachment"
        tabs={[
          { id: 'place', label: t('tabs.place'), icon: IconArrowsMove, content: place },
          { id: 'look', label: t('tabs.look'), icon: IconPalette, color: a.hologram ? 'var(--holo)' : undefined, content: look },
          ...(s ? [{ id: 'screen', label: t('tabs.screen'), icon: IconDeviceDesktop, color: 'var(--scan)', content: screen }] : []),
          // A model takes paint and animated layers as a part does.
          ...(a.mesh
            ? [
                { id: 'livery', label: t('tabs.livery'), icon: FEATURE.livery.icon, color: FEATURE.livery.color, count: layersOf(paint).length, content: <PartLivery part={model} /> },
                {
                  id: 'layers',
                  label: t('tabs.layers'),
                  icon: FEATURE.layer.icon,
                  color: FEATURE.layer.color,
                  count: (paint?.layers ?? []).filter(Boolean).length,
                  content: <PartLayers part={model} />,
                },
              ]
            : []),
          { id: 'effects', label: t('tabs.effects'), icon: IconSparkles, count: (a.effects ?? []).filter(Boolean).length, content: <Effects a={a} /> },
          sounds,
        ]}
      />
    </>
  );
}

export function AttachInspector() {
  const selection = useStore((s) => s.selection);
  const a = useStore((s) => (selection?.kind === 'attachment' ? s.doc.attachments?.find((x) => x?.id === selection.id) : undefined));
  return a ? <AttachmentPanel a={a} /> : null;
}
