// Livery panels: the part's finish and paint layers (part inspector), the selected paint layer (right panel) and the
// brush bar in the 3D view. Layers are listed top first: the top of the list is painted on top.
import { ActionIcon, Alert, Button, Group, SegmentedControl, Select, SimpleGrid, Slider as MSlider, Switch, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconArrowDown, IconArrowUp, IconBrush, IconBucketDroplet, IconCircle, IconEye, IconEyeOff, IconLine, IconMask, IconPhoto, IconScribble, IconSticker, IconTrash } from '@tabler/icons-react';
import type { ComponentType } from 'react';
import { addDecal, addLivery, BLENDS, FINISH_IDS, FINISHES, layersOf, moveLivery, removeLivery, setFinish, updateLivery } from '../livery';
import { baseName, fileUrl, type BlendMode, type CatalogPart, type LiveryKind, type LiveryLayer } from '../protocol';
import { materialOf, useStore } from '../store';
import { useBrush, useLiverySelection, type BrushShape } from '../viewport/LiveryTool';
import { Color, Field, Num, Slider, Vec } from './fields';
import { FeatureHead, Info } from './Features';
import { t } from '../i18n';

export const LIVERY_KINDS: { id: LiveryKind; label: () => string; icon: ComponentType<{ size?: number; color?: string }> }[] = [
  { id: 'fill', label: () => t('livery.fill'), icon: IconBucketDroplet },
  { id: 'decal', label: () => t('livery.decal'), icon: IconSticker },
  { id: 'brush', label: () => t('livery.brush'), icon: IconBrush },
];
export const kindOf = (k: LiveryKind | null | undefined) => LIVERY_KINDS.find((x) => x.id === k) ?? LIVERY_KINDS[0];

/** Adds a paint layer of `kind` to the part's material (a decal asks for its image first). */
export function addLiveryLayer(material: string, part: string, kind: LiveryKind) {
  if (kind === 'decal') return void addDecal(material, part);
  addLivery(material, part, kind);
}

const blendData = () => BLENDS.map((b: BlendMode) => ({ value: b, label: t(`livery.b.${b}`) }));

function FinishPicker({ material, part }: { material: string; part: string }) {
  const finish = useStore((s) => materialOf(s.doc, material)?.finish ?? null);
  const glow = useStore((s) => materialOf(s.doc, material)?.params?.emissiveIntensity ?? 1);
  return (
    <>
      <Group gap={6} mb={4}>
        <Text size="sm" c="dimmed">
          {t('livery.finish')}
        </Text>
        <Info label={t('livery.finishInfo')} />
      </Group>
      <div className="finishes">
        <UnstyledButton className="finish" aria-selected={!finish} onClick={() => setFinish(material, part, null)}>
          <i style={{ background: 'repeating-linear-gradient(45deg,#2a2d31 0 4px,#3a3e44 4px 8px)' }} />
          <span>{t('livery.finishNone')}</span>
        </UnstyledButton>
        {FINISH_IDS.map((f) => (
          <UnstyledButton key={f} className="finish" aria-selected={finish === f} onClick={() => setFinish(material, part, f)}>
            <i style={{ background: `radial-gradient(circle at 35% 30%, #fff ${FINISHES[f].smooth * 22}%, ${FINISHES[f].swatch} ${18 + FINISHES[f].smooth * 30}%, #15171a 120%)` }} />
            <span>{t(`livery.f.${f}`)}</span>
          </UnstyledButton>
        ))}
      </div>
      {finish === 'glow' && (
        <Field label={t('livery.glow')}>
          <Slider value={glow} min={0} max={10} step={0.05} onChange={(v) => useStore.getState().setParams(material, { emissiveIntensity: v }, `glow-${material}`)} />
        </Field>
      )}
    </>
  );
}

function LayerRow({ material, layer, index, count }: { material: string; layer: LiveryLayer; index: number; count: number }) {
  const selected = useStore((s) => s.selection?.kind === 'livery' && s.selection.id === layer.id);
  const K = kindOf(layer.kind);
  const stop = (fn: () => void) => (e: React.MouseEvent) => (e.stopPropagation(), fn());
  return (
    <UnstyledButton className="item livery-item" aria-selected={selected} onClick={() => useStore.getState().select({ kind: 'livery', material, id: layer.id! })}>
      <ActionIcon variant="subtle" size="sm" color={layer.visible === false ? 'dark.3' : 'pink'} onClick={stop(() => updateLivery(material, layer.id!, (l) => void (l.visible = l.visible === false)))}>
        {layer.visible === false ? <IconEyeOff size={14} /> : <IconEye size={14} />}
      </ActionIcon>
      <div style={{ minWidth: 0 }}>
        <Text size="sm" truncate>
          {layer.name}
        </Text>
        <Text size="xs" c="dimmed">
          {K.label()} · {t(`livery.b.${layer.blend ?? 'normal'}`)}
        </Text>
      </div>
      <Group gap={0} wrap="nowrap">
        <Tooltip label={t('livery.up')}>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === count - 1} onClick={stop(() => moveLivery(material, layer.id!, 1))}>
            <IconArrowUp size={13} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('livery.down')}>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === 0} onClick={stop(() => moveLivery(material, layer.id!, -1))}>
            <IconArrowDown size={13} />
          </ActionIcon>
        </Tooltip>
      </Group>
    </UnstyledButton>
  );
}

/** A part's livery in the part inspector: per material its finish, the add buttons and the layer stack. */
export function PartLivery({ part }: { part: CatalogPart }) {
  const doc = useStore((s) => s.doc);
  return (
    <div className="section">
      <FeatureHead feature="livery" title={t('livery.title')} info={t('livery.info')} />
      {part.materials.map((mat) => {
        const layers = layersOf(materialOf(doc, mat));
        return (
          <div key={mat} style={{ marginBottom: 8 }}>
            {part.materials.length > 1 && (
              <Text size="xs" c="dimmed" mb={4}>
                {mat}
              </Text>
            )}
            <FinishPicker material={mat} part={part.id} />
            <SimpleGrid cols={3} spacing={4} my={8}>
              {LIVERY_KINDS.map((k) => (
                <Button key={k.id} size="xs" variant="light" color="pink" px={4} leftSection={<k.icon size={13} />} onClick={() => addLiveryLayer(mat, part.id, k.id)}>
                  {k.label()}
                </Button>
              ))}
            </SimpleGrid>
            <div className="list">
              {[...layers].reverse().map((l, i) => (
                <LayerRow key={l.id} material={mat} layer={l} index={layers.length - 1 - i} count={layers.length} />
              ))}
              {!layers.length && <p className="hint">{t('livery.empty')}</p>}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function MaskSection({ material, layer }: { material: string; layer: LiveryLayer }) {
  const mask = useBrush((b) => b.mask);
  const strokes = (layer.mask ?? []).length;
  return (
    <div className="section">
      <h3 className="feature-head">
        <IconMask size={14} />
        <span>{t('livery.mask')}</span>
        <Info label={t('livery.maskInfo')} />
      </h3>
      <Group justify="space-between">
        <Switch label={t('livery.paintMask')} checked={mask} onChange={(e) => useBrush.setState({ mask: e.currentTarget.checked })} />
        <Group gap={6}>
          {strokes > 0 && (
            <Text size="xs" c="dimmed">
              {t('livery.maskCount', { v0: strokes })}
            </Text>
          )}
          <Button size="compact-xs" variant="subtle" color="gray" disabled={!strokes} onClick={() => updateLivery(material, layer.id!, (l) => void (l.mask = []))}>
            {t('livery.clearMask')}
          </Button>
        </Group>
      </Group>
    </div>
  );
}

/** The selected paint layer. */
export function LiveryInspector() {
  const sel = useLiverySelection();
  if (!sel) return null;
  const { material, part, layer } = sel;
  const up = (fn: (l: LiveryLayer) => void, key?: string) => updateLivery(material, layer.id!, fn, key);
  const K = kindOf(layer.kind);
  const [w, h] = layer.size ?? [0.04, 0.04];
  return (
    <>
      <div className="insp-head">
        <UnstyledButton className="kind" onClick={() => useStore.getState().select({ kind: 'part', id: part ?? '' })}>
          {t('livery.layer', { v0: K.label() })}
        </UnstyledButton>
        <TextInput mt={4} size="sm" value={layer.name ?? ''} onChange={(e) => {
          const n = e.currentTarget.value;
          up((l) => void (l.name = n), 'name');
        }} />
      </div>
      {layer.kind === 'decal' && (
        <div className="section">
          <h3>{t('livery.decal')}</h3>
          {!layer.position && (
            <Alert color="pink" variant="light" p="xs" mb="sm">
              {t('livery.notPlaced')}
            </Alert>
          )}
          <Field label={t('livery.image')}>
            <Group gap={6} wrap="nowrap">
              {layer.image && <img src={fileUrl(layer.image)} alt="" className="decal-thumb" draggable={false} />}
              <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>
                {layer.image ? baseName(layer.image) : ''}
              </Text>
              <Button variant="default" size="xs" leftSection={<IconPhoto size={13} />} onClick={async () => {
                const [f] = await window.forge.openDialog('files', [{ name: t('livery.decal'), extensions: ['png', 'jpg', 'jpeg', 'webp', 'bmp', 'gif'] }]);
                if (f) up((l) => void (l.image = f));
              }}>
                {t('livery.pickImage')}
              </Button>
            </Group>
          </Field>
          <Field label={t('livery.size')}>
            <Vec value={[w, h]} labels={['W', 'H']} scale={1000} step={1} decimalScale={1} min={1} onChange={(v) => up((l) => void (l.size = v), 'size')} />
          </Field>
          <Field label={t('livery.angle')}>
            <Slider value={layer.angle ?? 0} min={-180} max={180} step={1} label={(v) => `${v.toFixed(0)}°`} onChange={(v) => up((l) => void (l.angle = v), 'angle')} />
          </Field>
          <Field label={t('livery.depth')}>
            <Group gap={6} wrap="nowrap">
              <Num style={{ flex: 1 }} value={layer.depth ?? 0.01} scale={1000} step={1} decimalScale={1} min={0.5} onChange={(v) => up((l) => void (l.depth = v), 'depth')} />
              <Info label={t('livery.depthInfo')} />
            </Group>
          </Field>
          <Switch mt={4} label={t('livery.mirror')} checked={!!layer.mirror} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.mirror = on));
          }} />
          <p className="hint" style={{ marginTop: 8 }}>
            {t('livery.placeHint')}
          </p>
        </div>
      )}
      {layer.kind === 'brush' && (
        <div className="section">
          <h3>{t('livery.brush')}</h3>
          <p className="hint">{t('livery.brushHint')}</p>
          <Button mt={6} size="compact-xs" variant="subtle" color="gray" disabled={!(layer.strokes ?? []).length} onClick={() => up((l) => void (l.strokes = []))}>
            {t('livery.clearStrokes')}
          </Button>
        </div>
      )}
      <div className="section">
        <h3>{t('livery.look')}</h3>
        <Field label={layer.kind === 'decal' ? t('livery.tint') : t('livery.color')}>
          <Color value={layer.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.color = [c[0], c[1], c[2], 1]), 'color')} />
        </Field>
        <Field label={t('livery.opacity')}>
          <Slider value={layer.opacity ?? 1} onChange={(v) => up((l) => void (l.opacity = v), 'op')} />
        </Field>
        <Field label={t('livery.blend')}>
          <Group gap={6} wrap="nowrap">
            <Select style={{ flex: 1 }} value={layer.blend ?? 'normal'} data={blendData()} allowDeselect={false} onChange={(v) => v && up((l) => void (l.blend = v as BlendMode))} />
            <Info label={t('livery.blendInfo')} />
          </Group>
        </Field>
      </div>
      <MaskSection material={material} layer={layer} />
      <div className="section">
        <Group justify="space-between">
          <Switch label={t('livery.visible')} checked={layer.visible !== false} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.visible = on));
          }} />
          <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={() => removeLivery(material, layer.id!)}>
            {t('livery.delete')}
          </Button>
        </Group>
      </div>
    </>
  );
}

const SHAPES: { value: BrushShape; icon: ComponentType<{ size?: number }> }[] = [
  { value: 'free', icon: IconScribble },
  { value: 'line', icon: IconLine },
  { value: 'circle', icon: IconCircle },
];

/** What dragging in the 3D view does for the selected paint layer, and the brush settings, over the view. */
export function LiveryBar() {
  const sel = useLiverySelection();
  const brush = useBrush();
  if (!sel) return null;
  const painting = brush.mask || sel.layer.kind === 'brush';
  const what = brush.mask ? t('livery.bar.mask') : sel.layer.kind === 'brush' ? t('livery.bar.brush') : sel.layer.kind === 'decal' ? t('livery.bar.decal') : t('livery.bar.fill');
  return (
    <Group className="overlay livery-bar" gap={10} wrap="nowrap">
      <Group gap={6} wrap="nowrap">
        {brush.mask ? <IconMask size={15} color="var(--paint)" /> : <IconBrush size={15} color="var(--paint)" />}
        <Text size="xs">{what}</Text>
      </Group>
      {painting && (
        <>
          <SegmentedControl
            size="xs"
            value={brush.shape}
            onChange={(v) => useBrush.setState({ shape: v as BrushShape })}
            data={SHAPES.map((s) => ({
              value: s.value,
              label: (
                <Tooltip label={t(`livery.shape.${s.value}`)}>
                  <span style={{ display: 'flex' }}>
                    <s.icon size={14} />
                  </span>
                </Tooltip>
              ),
            }))}
          />
          <Group gap={6} wrap="nowrap">
            <Text size="xs" c="dimmed">
              {t('livery.width')}
            </Text>
            <MSlider w={110} size="sm" min={0.5} max={30} step={0.5} value={brush.width * 1000} label={(v) => `${v} mm`} onChange={(v) => useBrush.setState({ width: v / 1000 })} />
          </Group>
        </>
      )}
      <Switch size="xs" label={t('livery.paintMask')} checked={brush.mask} onChange={(e) => useBrush.setState({ mask: e.currentTarget.checked })} />
    </Group>
  );
}
