// Unity-Transform style block for the selected part, sub-part or attachment: Position (mm) / Rotation (°) / Scale rows of
// X/Y/Z fields. Shows the resulting local values; the caller stores only the delta. Axis letters scrub (Shift fine,
// Ctrl snaps); typed values commit on Enter or blur; each commit or scrub gesture is one undo step.
import { ActionIcon, Button, Group, NumberInput, Text, Tooltip } from '@mantine/core';
import { IconEye, IconEyeOff, IconLink, IconLinkOff, IconRestore } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import type { UnityPose, Vec3 } from '../space';
import { newGesture } from '../viewport/ModalTransform';

type Row = 'position' | 'rotation' | 'scale';
const ROWS: { row: Row; label: string; factor: number; step: number; snap: number; digits: number }[] = [
  { row: 'position', label: '位置 mm', factor: 1000, step: 0.5, snap: 1, digits: 2 },
  { row: 'rotation', label: '旋转 °', factor: 1, step: 0.5, snap: 5, digits: 2 },
  { row: 'scale', label: '缩放 ×', factor: 1, step: 0.005, snap: 0.1, digits: 3 },
];
const AXES = [
  { l: 'X', c: 'var(--x)' },
  { l: 'Y', c: 'var(--y)' },
  { l: 'Z', c: 'var(--z)' },
];

function AxisField({
  value,
  digits,
  step,
  snap,
  axis,
  onCommit,
}: {
  value: number;
  digits: number;
  step: number;
  snap: number;
  axis: number;
  onCommit: (v: number, key?: string) => void;
}) {
  const shown = +value.toFixed(digits);
  const [text, setText] = useState<string | number>(shown);
  const editing = useRef(false);
  useEffect(() => {
    if (!editing.current) setText(shown);
  }, [shown]);
  const commit = () => {
    const v = typeof text === 'number' ? text : parseFloat(text);
    if (Number.isFinite(v) && v !== shown) onCommit(v);
  };
  // Drag the axis letter left/right to scrub; Shift = 1/10 speed, Ctrl = snap.
  const scrub = (e: React.PointerEvent<HTMLSpanElement>) => {
    e.preventDefault();
    const el = e.currentTarget;
    el.setPointerCapture(e.pointerId);
    const x0 = e.clientX;
    const v0 = value;
    const key = newGesture();
    const move = (ev: PointerEvent) => {
      let v = v0 + (ev.clientX - x0) * step * (ev.shiftKey ? 0.1 : 1);
      if (ev.ctrlKey) v = Math.round(v / snap) * snap;
      onCommit(+v.toFixed(digits), key);
    };
    const up = () => {
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
    };
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
  };
  return (
    <NumberInput
      size="xs"
      hideControls
      value={text}
      decimalScale={digits}
      onChange={setText}
      onFocus={() => (editing.current = true)}
      onBlur={() => {
        editing.current = false;
        commit();
      }}
      onKeyDown={(e) => {
        if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
        if (e.key === 'Escape') {
          setText(shown);
          editing.current = false;
          (e.target as HTMLInputElement).blur();
        }
      }}
      leftSectionWidth={20}
      leftSection={
        <Tooltip label="左右拖动调整（Shift 精细，Ctrl 吸附）" openDelay={700}>
          <Text component="span" size="xs" fw={700} ff="monospace" onPointerDown={scrub} style={{ color: AXES[axis].c, cursor: 'ew-resize', padding: '0 4px' }}>
            {AXES[axis].l}
          </Text>
        </Tooltip>
      }
      styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', paddingLeft: 22 } }}
    />
  );
}

export function TransformBlock({
  title,
  pose,
  onChange,
  onReset,
  visible,
  onVisible,
}: {
  title: string;
  /** Resulting local pose (Unity space). */
  pose: UnityPose;
  /** New resulting pose; `key` merges a scrub gesture into one undo step. */
  onChange: (pose: UnityPose, key?: string) => void;
  /** Reset one row, or everything when `row` is undefined. */
  onReset: (row?: Row) => void;
  visible: boolean;
  onVisible: (v: boolean) => void;
}) {
  const [uniform, setUniform] = useState(true);
  const set = (row: Row, axis: number, display: number, factor: number, key?: string) => {
    const v = display / factor;
    const next: UnityPose = { position: [...pose.position] as Vec3, rotation: [...pose.rotation] as Vec3, scale: [...pose.scale] as Vec3 };
    if (row === 'scale' && uniform) {
      const k = v / (pose.scale[axis] || 1e-6);
      next.scale = pose.scale.map((c) => c * k) as Vec3;
    } else next[row][axis] = v;
    onChange(next, key);
  };
  return (
    <div className="section">
      <h3>
        <span className="grow">{title}</span>
        <Tooltip label={visible ? '隐藏（游戏里也隐藏）' : '显示'}>
          <ActionIcon variant="subtle" color={visible ? 'gray' : 'flare'} size="sm" onClick={() => onVisible(!visible)}>
            {visible ? <IconEye size={14} /> : <IconEyeOff size={14} />}
          </ActionIcon>
        </Tooltip>
        <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconRestore size={12} />} onClick={() => onReset()}>
          重置全部
        </Button>
      </h3>
      {ROWS.map(({ row, label, factor, step, snap, digits }) => (
        <div className="trow" key={row}>
          <Text size="xs" c="dimmed">
            {label}
          </Text>
          <div className="vec" style={{ gridTemplateColumns: 'repeat(3, 1fr)' }}>
            {[0, 1, 2].map((i) => (
              <AxisField key={i} axis={i} value={pose[row][i] * factor} digits={digits} step={step} snap={snap} onCommit={(v, key) => set(row, i, v, factor, key)} />
            ))}
          </div>
          <Group gap={0} wrap="nowrap">
            {row === 'scale' ? (
              <Tooltip label={uniform ? '等比缩放（点击解锁）' : '分轴缩放（点击锁定等比）'}>
                <ActionIcon variant="subtle" color={uniform ? 'flare' : 'gray'} size="sm" onClick={() => setUniform(!uniform)}>
                  {uniform ? <IconLink size={13} /> : <IconLinkOff size={13} />}
                </ActionIcon>
              </Tooltip>
            ) : (
              <span style={{ width: 22 }} />
            )}
            <Tooltip label="恢复原版">
              <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => onReset(row)}>
                <IconRestore size={13} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </div>
      ))}
      <Group justify="space-between" mt={8}>
        <Text size="xs" c="dimmed">
          拖动 3D 里的控制柄，或选中后按 G 移动 / R 旋转 / S 缩放
        </Text>
      </Group>
    </div>
  );
}
