// Built-in shapes: the menu entries that add one, and the inspector section with its parameters.
import { Menu, Switch } from '@mantine/core';
import { IconCircle, IconCone, IconCube, IconCylinder, IconGridDots, IconLine, IconRectangle, IconSphere, IconVectorSpline } from '@tabler/icons-react';
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

type ItemsProps = { color?: string; before?: () => void };

/** One menu item per shape; `before` runs first (e.g. selecting the part it goes on). */
export function ShapeItems({ color, before }: ItemsProps) {
  return (Object.keys(SHAPES) as ShapeKind[]).map((kind) => {
    const Icon = ICONS[kind];
    return (
      <Menu.Item key={kind} leftSection={<Icon size={14} color={color} />} onClick={() => (before?.(), void addShape(kind))}>
        {t(`shapes.${kind}`)}
      </Menu.Item>
    );
  });
}

/** 形状 ›: the shapes as a submenu, so the menu it sits in stays short. */
export function ShapeMenuItems(props: ItemsProps) {
  return (
    <Menu.Sub>
      <Menu.Sub.Target>
        <Menu.Sub.Item leftSection={<IconSphere size={14} color={props.color} />}>{t('shapes.title')}</Menu.Sub.Item>
      </Menu.Sub.Target>
      <Menu.Sub.Dropdown>
        <ShapeItems {...props} />
      </Menu.Sub.Dropdown>
    </Menu.Sub>
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
