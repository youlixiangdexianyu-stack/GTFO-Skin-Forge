// Inspector rows composed from Mantine inputs. Values are stored as the document stores them (metres, sRGB 0..1
// colour arrays); `scale` converts for display (e.g. metres shown as millimetres).
import { ColorInput, ColorPicker, ColorSwatch, Group, NumberInput, Popover, Slider as MSlider, Text, UnstyledButton, type NumberInputProps } from '@mantine/core';
import type { ReactNode } from 'react';
import type { ColorSlot } from '../protocol';
import { PlayerToggle } from './PlayerColors';
import { useSwatches } from '../library';

/** A labelled control; the label also names the group, so assistive tools and AI control find the control by it. The
 *  label sits in a column beside the control, or above it when the panel is narrow (styles.css). */
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="field" role="group" aria-label={label}>
      <span className="field-label">{label}</span>
      <div className="field-control">{children}</div>
    </div>
  );
}

const AXIS_COLOR = ['var(--x)', 'var(--y)', 'var(--z)', 'var(--dust)'];

interface NumProps extends Omit<NumberInputProps, 'value' | 'onChange'> {
  value: number;
  onChange: (v: number) => void;
  scale?: number;
  axis?: number;
  label?: string;
}

export function Num({ value, onChange, scale = 1, axis, label, decimalScale = 2, ...rest }: NumProps) {
  return (
    <NumberInput
      value={+(value * scale).toFixed(decimalScale)}
      decimalScale={decimalScale}
      onChange={(v) => typeof v === 'number' && onChange(v / scale)}
      leftSection={
        label && (
          <Text size="xs" fw={700} ff="monospace" style={{ color: AXIS_COLOR[axis ?? 3] }}>
            {label}
          </Text>
        )
      }
      leftSectionWidth={label ? 18 : undefined}
      styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', paddingRight: rest.hideControls ? 4 : undefined } }}
      {...rest}
    />
  );
}

export function Vec({ value, onChange, labels = ['X', 'Y', 'Z', 'W'], ...rest }: Omit<NumProps, 'value' | 'onChange'> & { value: number[]; onChange: (v: number[]) => void; labels?: string[] }) {
  return (
    <div className="vec" data-n={value.length} style={{ gridTemplateColumns: `repeat(${value.length}, minmax(0, 1fr))` }}>
      {value.map((v, i) => (
        <Num key={i} axis={i} label={labels[i]} hideControls value={v} onChange={(n) => onChange(value.map((o, j) => (j === i ? n : o)))} {...rest} />
      ))}
    </div>
  );
}

/** A slider with the exact value beside it; `unit` (" mm", "°", "%") follows the number in both. */
export function Slider({
  value,
  onChange,
  min = 0,
  max = 1,
  step = 0.01,
  unit = '',
  label,
}: {
  value: number;
  onChange: (v: number) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  label?: (v: number) => string;
}) {
  const digits = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
  return (
    <div className="slider-row">
      <MSlider size="sm" value={value} min={min} max={max} step={step} onChange={onChange} label={label ?? ((v) => `${v.toFixed(digits)}${unit}`)} />
      <Num hideControls value={value} min={min} max={max} step={step * 10} decimalScale={digits} suffix={unit} onChange={onChange} />
    </div>
  );
}

const toHex = (c: number[], alpha: boolean) =>
  '#' +
  c
    .slice(0, alpha ? 4 : 3)
    .map((v) =>
      Math.round(Math.min(1, Math.max(0, v ?? 1)) * 255)
        .toString(16)
        .padStart(2, '0'),
    )
    .join('');
const fromHex = (h: string) => (h.slice(1).match(/../g) ?? []).map((x) => parseInt(x, 16) / 255);

/** Colour as a 0..1 array. `rgb` adds two-decimal R/G/B boxes with ±0.1 steppers; `alpha` edits the 4th component; `slot`
 * adds the button that lets players change this colour in game; `swatch` shows only the colour, which opens the picker
 * (for rows of several colours). */
export function Color({ value, onChange, rgb, alpha, slot, swatch }: { value: number[]; onChange: (v: number[]) => void; rgb?: boolean; alpha?: boolean; slot?: ColorSlot; swatch?: boolean }) {
  // The active palette (library.ts) is offered under the picker.
  const swatches = useSwatches();
  const set = (hex: string) => {
    const c = fromHex(hex);
    if (c.length < 3) return;
    onChange(alpha ? [c[0], c[1], c[2], c[3] ?? value[3] ?? 1] : value.length > 3 ? [c[0], c[1], c[2], value[3]] : c.slice(0, 3));
  };
  if (swatch)
    return (
      <Popover position="bottom" withArrow shadow="md">
        <Popover.Target>
          <UnstyledButton className="swatch-btn" aria-label={toHex(value, !!alpha)}>
            <ColorSwatch color={toHex(value, !!alpha)} size={22} radius="sm" />
          </UnstyledButton>
        </Popover.Target>
        <Popover.Dropdown p={8}>
          <ColorPicker format={alpha ? 'hexa' : 'hex'} value={toHex(value, !!alpha)} onChange={set} onChangeEnd={set} swatches={swatches} swatchesPerRow={8} />
        </Popover.Dropdown>
      </Popover>
    );
  return (
    <div style={{ display: 'grid', gap: 6 }}>
      {/* Mantine 8's picker calls onChange with the colour before the latest move (its state updater runs after the
          call), so a single click or the end of a drag never arrived; onChangeEnd carries the picked colour. */}
      <Group gap={6} wrap="nowrap">
        <ColorInput style={{ flex: 1 }} format={alpha ? 'hexa' : 'hex'} value={toHex(value, !!alpha)} onChange={set} onChangeEnd={set} withEyeDropper={false} swatches={swatches} swatchesPerRow={8} />
        {slot && <PlayerToggle slot={slot} value={value} />}
      </Group>
      {rgb && <Vec value={value.slice(0, 3)} labels={['R', 'G', 'B']} min={0} step={0.1} onChange={(v) => onChange(value.length > 3 ? [...v, value[3]] : v)} />}
    </div>
  );
}
