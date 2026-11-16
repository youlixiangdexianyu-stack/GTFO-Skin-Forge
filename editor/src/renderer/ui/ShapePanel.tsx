// Built-in shapes: the menu entries that add one, and the inspector section with its parameters.
import { Menu, Switch } from '@mantine/core';
import {
  IconBox,
  IconCircle,
  IconCircleDashed,
  IconCone,
  IconCube,
  IconCylinder,
  IconGridDots,
  IconHexagons,
  IconLine,
  IconPaperclip,
  IconPhoto,
  IconRectangle,
  IconRuler2,
  IconSphere,
  IconStack3,
  IconVectorSpline,
} from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { importMesh } from '../actions';
import { addImageHologram, addShape, MENU_SHAPES, setShape, SHAPES, shapeOf, type ShapeKind } from '../shapes';
import { addHoloSet, HOLO_SETS, type HoloSet } from '../holoSets';
import { useStore } from '../store';
import { Section } from './Features';
import { Field, Num } from './fields';
import { t } from '../i18n';

const ICONS: Record<ShapeKind, typeof IconCube> = {
  line: IconLine,
  sphere: IconSphere,
  ring: IconCircle,
  arc: IconVectorSpline,
  box: IconCube,
  grid: IconGridDots,
  cylinder: IconCylinder,
  cone: IconCone,
  panel: IconRectangle,
  hexPanel: IconHexagons,
  reticle: IconCircleDashed,
  tickArc: IconRuler2,
  image: IconPhoto,
};

const SET_ICONS: Record<HoloSet, typeof IconCube> = { sight: IconCircleDashed, barrel: IconRuler2, strip: IconRectangle };

type ItemsProps = { color?: string; before?: () => void; charm?: boolean };

/** One menu item per shape; `before` runs first (e.g. selecting the part it goes on); `charm` hangs it as a charm. */
export function ShapeItems({ color, before, charm }: ItemsProps) {
  return MENU_SHAPES.map((kind) => {
    const Icon = ICONS[kind];
    return (
      <Menu.Item key={kind} leftSection={<Icon size={14} color={color} />} onClick={() => (before?.(), void addShape(kind, charm))}>
        {t(`shapes.${kind}`)}
      </Menu.Item>
    );
  });
}

/** A submenu entry, so the menu it sits in stays short enough to fit the window. */
export function SubMenu({ icon, label, children }: { icon: ReactNode; label: string; children: ReactNode }) {
  return (
    <Menu.Sub>
      <Menu.Sub.Target>
        <Menu.Sub.Item leftSection={icon}>{label}</Menu.Sub.Item>
      </Menu.Sub.Target>
      <Menu.Sub.Dropdown>{children}</Menu.Sub.Dropdown>
    </Menu.Sub>
  );
}

/**
 * 图片全息屏, 形状 ›, 成套全息 › and 挂饰 ›: a picture as a hologram screen, a shape as a hologram, a matching set of them
 * placed on the gear (holoSets.ts), or a shape or an imported model hung on a rope from a point on the gun.
 */
export function ShapeMenuItems(props: ItemsProps) {
  return (
    <>
      <Menu.Item leftSection={<IconPhoto size={14} color={props.color} />} onClick={() => (props.before?.(), void addImageHologram())}>
        {t('shapes.imageHologram')}
      </Menu.Item>
      <SubMenu icon={<IconSphere size={14} color={props.color} />} label={t('shapes.title')}>
        <ShapeItems {...props} />
      </SubMenu>
      <SubMenu icon={<IconStack3 size={14} color={props.color} />} label={t('holoSets.title')}>
        <Menu.Label>{t('holoSets.menuHint')}</Menu.Label>
        {HOLO_SETS.map((set) => {
          const Icon = SET_ICONS[set];
          return (
            <Menu.Item key={set} leftSection={<Icon size={14} color={props.color} />} onClick={() => void addHoloSet(set)}>
              {t(`holoSets.${set}`)}
            </Menu.Item>
          );
        })}
      </SubMenu>
      <SubMenu icon={<IconPaperclip size={14} color={props.color} />} label={t('charm.title')}>
        <Menu.Label>{t('charm.menuHint')}</Menu.Label>
        <Menu.Item leftSection={<IconBox size={14} color={props.color} />} onClick={() => (props.before?.(), void importMesh(true))}>
          {t('leftRail.k19')}
        </Menu.Item>
        <ShapeItems {...props} charm />
      </SubMenu>
    </>
  );
}

/** The selected shape attachment's parameters; nothing for an imported model. */
export function ShapeSection({ id }: { id: string }) {
  const shape = useStore((s) => shapeOf(s.doc.editor, id));
  if (!shape) return null;
  const spec = SHAPES[shape.kind];
  const set = (key: string, v: number) => setShape(id, { ...shape, params: { ...shape.params, [key]: v } }, key);
  const Icon = ICONS[shape.kind];
  return (
    <Section id="shape" title={t(`shapes.${shape.kind}`)} icon={Icon} color="var(--holo)">
      {spec.params.map((p) => (
        <Field key={p.key} label={t(`shapes.${p.key}`)}>
          <Num
            value={shape.params[p.key] ?? p.value}
            scale={p.unit === 'mm' ? 1000 : 1}
            step={p.unit === 'mm' ? 0.1 : 1}
            decimalScale={p.unit === 'mm' ? 1 : 0}
            min={p.unit === 'mm' ? p.min * 1000 : p.min}
            max={p.max}
            suffix={p.unit === 'mm' ? ' mm' : p.unit === 'deg' ? '°' : undefined}
            onChange={(v) => set(p.key, p.unit === 'mm' ? v : Math.round(v))}
          />
        </Field>
      ))}
      {spec.wire && (
        <div className="switch-row" style={{ marginTop: 8 }}>
          <Switch label={t('shapes.wire')} checked={shape.wire} onChange={(e) => setShape(id, { ...shape, wire: e.currentTarget.checked }, 'wire')} />
        </div>
      )}
    </Section>
  );
}
