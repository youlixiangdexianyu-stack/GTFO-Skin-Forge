// Built-in shapes: the menu entries that add one, and the inspector section with its parameters.
import { Menu, Switch } from '@mantine/core';
import { IconBox, IconCircle, IconCone, IconCube, IconCylinder, IconGridDots, IconLine, IconPaperclip, IconRectangle, IconSphere, IconVectorSpline } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { importMesh } from '../actions';
import { addShape, setShape, SHAPES, shapeOf, type ShapeKind } from '../shapes';
import { useStore } from '../store';
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
};

type ItemsProps = { color?: string; before?: () => void; charm?: boolean };

/** One menu item per shape; `before` runs first (e.g. selecting the part it goes on); `charm` hangs it as a charm. */
export function ShapeItems({ color, before, charm }: ItemsProps) {
  return (Object.keys(SHAPES) as ShapeKind[]).map((kind) => {
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

/** 形状 › and 挂饰 ›: a shape as a hologram, or a shape or an imported model hung on a rope from a point on the gun. */
export function ShapeMenuItems(props: ItemsProps) {
  return (
    <>
      <SubMenu icon={<IconSphere size={14} color={props.color} />} label={t('shapes.title')}>
        <ShapeItems {...props} />
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
  return (
    <div className="section">
      <h3>{t(`shapes.${shape.kind}`)}</h3>
      {spec.params.map((p) => (
        <Field key={p.key} label={t(`shapes.${p.key}`)}>
          <Num
            value={shape.params[p.key] ?? p.value}
            scale={p.unit === 'mm' ? 1000 : 1}
            step={p.unit === 'mm' ? 0.1 : 1}
            decimalScale={p.unit === 'mm' ? 1 : 0}
            min={p.unit === 'mm' ? p.min * 1000 : p.min}
            max={p.max}
            rightSection={p.unit === 'mm' ? 'mm' : p.unit === 'deg' ? '°' : undefined}
            onChange={(v) => set(p.key, p.unit === 'mm' ? v : Math.round(v))}
          />
        </Field>
      ))}
      {spec.wire && (
        <Switch mt="xs" label={t('shapes.wire')} checked={shape.wire} onChange={(e) => setShape(id, { ...shape, wire: e.currentTarget.checked }, 'wire')} />
      )}
    </div>
  );
}
