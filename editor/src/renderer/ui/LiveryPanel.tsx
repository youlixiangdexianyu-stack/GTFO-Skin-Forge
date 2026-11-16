// Livery panels: the part's finish and paint layers (part inspector), the selected paint layer (right panel) and the
// brush bar in the 3D view. Layers are listed top first: the top of the list is painted on top.
import {
  ActionIcon,
  Alert,
  Button,
  Checkbox,
  Group,
  NumberInput,
  Popover,
  RangeSlider,
  SegmentedControl,
  Select,
  SimpleGrid,
  Slider as MSlider,
  Stack,
  Switch,
  Text,
  Textarea,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import {
  IconArrowDown,
  IconArrowsMove,
  IconArrowUp,
  IconBorderCorners,
  IconBrush,
  IconBrushOff,
  IconBucketDroplet,
  IconCircle,
  IconCircleHalf2,
  IconColorFilter,
  IconCopy,
  IconDice5,
  IconEye,
  IconEyeOff,
  IconFlipVertical,
  IconLayoutGrid,
  IconLine,
  IconMask,
  IconPalette,
  IconPhoto,
  IconPlayerPlay,
  IconPlus,
  IconScribble,
  IconShare3,
  IconSticker,
  IconTexture,
  IconTrash,
  IconTypography,
  IconX,
  type Icon,
} from '@tabler/icons-react';
import { useEffect, useMemo, useState, type ComponentType } from 'react';
import { loadFont } from '../fonts';
import { baseTexturePath, loadedTextCanvas, loadImage, sourceFrames, type SourceFrames } from '../frames';
import {
  addDecal,
  addLivery,
  addTile,
  BLENDS,
  copyLiveryTo,
  defaultGradient,
  duplicateLivery,
  FINISH_IDS,
  FINISHES,
  hasLivery,
  isGifDecal,
  isProjected,
  layersOf,
  liveryName,
  moveLivery,
  PATTERN_PRESETS,
  removeLivery,
  scaledTo,
  setFinish,
  TEXTURE_SIZES,
  textureMemory,
  textureSize,
  updateLivery,
} from '../livery';
import { drawTrim, TRIM_PRESETS, TRIM_STYLES, type Trim } from '../trim';
import { gradientStops, materialBounds } from '../viewport/liveryGpu';
import { PatternPicker } from './PatternPicker';
import { FontPicker } from './FontPicker';
import { pickDecalImage, pickTileImage } from '../stickers';
import { Playback } from './DynamicInspector';
import { baseName, fileUrl, type BlendMode, type CatalogPart, type LiveryKind, type LiveryLayer } from '../protocol';
import { ensureMaterial, materialOf, ownerOf, useItem, useStore } from '../store';
import { useBrush, useLiverySelection, type BrushShape } from '../viewport/LiveryTool';
import { Color, Field, Num, Slider, Vec } from './fields';
import { Empty, FEATURE, Info, InspHead, KindGrid, Section } from './Features';
import { partLabel, t } from '../i18n';

export const LIVERY_KINDS: { id: LiveryKind; label: () => string; icon: Icon }[] = [
  { id: 'fill', label: () => t('livery.fill'), icon: IconBucketDroplet },
  { id: 'decal', label: () => t('livery.decal'), icon: IconSticker },
  { id: 'brush', label: () => t('livery.brush'), icon: IconBrush },
  { id: 'pattern', label: () => t('livery.pattern'), icon: IconTexture },
  { id: 'wear', label: () => t('livery.wear'), icon: IconBrushOff },
  { id: 'trim', label: () => t('livery.trim'), icon: IconBorderCorners },
  { id: 'text', label: () => t('livery.text'), icon: IconTypography },
  { id: 'gradient', label: () => t('livery.gradient'), icon: IconColorFilter },
  { id: 'tile', label: () => t('livery.tile'), icon: IconLayoutGrid },
];
export const kindOf = (k: LiveryKind | null | undefined) => LIVERY_KINDS.find((x) => x.id === k) ?? LIVERY_KINDS[0];

/** Adds a paint layer of `kind` to the part's material (a decal asks for its image first). */
export function addLiveryLayer(material: string, part: string, kind: LiveryKind) {
  if (kind === 'decal') return void addDecal(material, part);
  if (kind === 'tile') return void addTile(material, part);
  addLivery(material, part, kind);
}

const blendData = () => BLENDS.map((b: BlendMode) => ({ value: b, label: t(`livery.b.${b}`) }));

const megabytes = (bytes: number) => `${bytes < 10 * 2 ** 20 ? (bytes / 2 ** 20).toFixed(1) : Math.round(bytes / 2 ** 20)} MB`;

/** 贴图清晰度: the size the material's own textures have in game (and in the preview), with what they cost. Shown once the
 *  material has textures or paint of its own (otherwise the game keeps its texture). */
export function TextureSizeField({ material, part }: { material: string; part: string }) {
  const doc = useStore((s) => s.doc);
  const preview = useStore((s) => s.preview);
  const m = materialOf(doc, material);
  const basePath = baseTexturePath(doc, preview, material, 'albedo');
  const [base, setBase] = useState<[number, number] | null>(null);
  useEffect(() => {
    let live = true;
    setBase(null);
    if (basePath)
      void loadImage(basePath).then(
        (img) => live && setBase([img.naturalWidth, img.naturalHeight]),
        () => undefined,
      );
    return () => void (live = false);
  }, [basePath]);
  if (!hasLivery(m) && !Object.values(m?.textures ?? {}).some(Boolean)) return null;
  const auto = textureSize(m && { ...m, textureSize: null }, base) ?? (base ? Math.max(...base) : null);
  const longest = m?.textureSize ?? auto;
  const set = (v: string | null) => useStore.getState().edit((d) => void (ensureMaterial(d, material, part).textureSize = v === 'auto' || !v ? null : Number(v)));
  return (
    <Field label={t('skinInspector.texSize')}>
      <Group gap={6} wrap="nowrap">
        <Select
          style={{ flex: 1 }}
          aria-label={t('skinInspector.texSize')}
          value={m?.textureSize ? String(m.textureSize) : 'auto'}
          allowDeselect={false}
          data={[{ value: 'auto', label: t('skinInspector.texSizeAuto', { v0: auto ?? '…' }) }, ...TEXTURE_SIZES.map((n) => ({ value: String(n), label: String(n) }))]}
          onChange={set}
        />
        <Info label={t('skinInspector.texSizeInfo')} />
      </Group>
      {longest && (
        <Text size="xs" c="dimmed">
          {t('skinInspector.texSizeMemory', { v0: megabytes(textureMemory(m, scaledTo(base ?? [1, 1], longest))) })}
        </Text>
      )}
    </Field>
  );
}

function FinishPicker({ material, part }: { material: string; part: string }) {
  const finish = useStore((s) => materialOf(s.doc, material)?.finish ?? null);
  const glow = useStore((s) => materialOf(s.doc, material)?.params?.emissiveIntensity ?? 1);
  return (
    <>
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
    <UnstyledButton
      className={`item livery-item ${layer.visible === false ? 'off' : ''}`}
      aria-selected={selected}
      onClick={() => useStore.getState().select({ kind: 'livery', material, id: layer.id! })}
    >
      <ActionIcon
        variant="subtle"
        size="sm"
        color={layer.visible === false ? 'dark.3' : 'pink'}
        aria-label={t('livery.visible')}
        onClick={stop(() => updateLivery(material, layer.id!, (l) => void (l.visible = l.visible === false)))}
      >
        {layer.visible === false ? <IconEyeOff size={14} /> : <IconEye size={14} />}
      </ActionIcon>
      <K.icon size={16} color={FEATURE.livery.color} />
      <div style={{ minWidth: 0 }}>
        <Text size="sm" truncate>
          {liveryName(layer)}
        </Text>
        <Text size="xs" c="dimmed" truncate>
          {K.label()} · {t(`livery.b.${layer.blend ?? 'normal'}`)}
        </Text>
      </div>
      <Group gap={0} wrap="nowrap" className="row-actions">
        <Tooltip label={t('livery.duplicate')}>
          <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('livery.duplicate')} onClick={stop(() => duplicateLivery(material, layer.id!))}>
            <IconCopy size={13} />
          </ActionIcon>
        </Tooltip>
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
export function PartLivery({ part }: { part: Pick<CatalogPart, 'id' | 'materials'> }) {
  const doc = useStore((s) => s.doc);
  const kinds = LIVERY_KINDS.map((k) => ({ id: k.id, label: k.label(), icon: k.icon }));
  return part.materials.map((mat) => {
    const layers = layersOf(materialOf(doc, mat));
    const of = (title: string) => (part.materials.length > 1 ? `${title} · ${mat}` : title);
    return (
      <div key={mat}>
        <Section feature="livery" title={of(t('livery.title'))} info={t('livery.info')}>
          <KindGrid kinds={kinds} color={FEATURE.livery.color} onPick={(k) => addLiveryLayer(mat, part.id, k)} />
          <div className="list" style={{ marginTop: 10 }}>
            {[...layers].reverse().map((l, i) => (
              <LayerRow key={l.id} material={mat} layer={l} index={layers.length - 1 - i} count={layers.length} />
            ))}
            {!layers.length && <Empty>{t('livery.empty')}</Empty>}
          </div>
        </Section>
        <Section id="livery-finish" title={of(t('livery.finish'))} icon={IconCircleHalf2} color="var(--paint)" info={t('livery.finishInfo')}>
          <FinishPicker material={mat} part={part.id} />
          <TextureSizeField material={mat} part={part.id} />
        </Section>
      </div>
    );
  });
}

function MaskSection({ material, layer }: { material: string; layer: LiveryLayer }) {
  const mask = useBrush((b) => b.mask);
  const strokes = (layer.mask ?? []).length;
  const up = (fn: (l: LiveryLayer) => void, key?: string) => updateLivery(material, layer.id!, fn, key);
  const fade = layer.maskGradient;
  const stops = gradientStops(fade);
  return (
    <Section id="livery-mask" title={t('livery.mask')} icon={IconMask} color="var(--violet)" info={t('livery.maskInfo')}>
      <div className="switch-row">
        <Switch label={t('livery.paintMask')} checked={mask} onChange={(e) => useBrush.setState({ mask: e.currentTarget.checked })} />
        <span className="grow" />
        {strokes > 0 && (
          <Text size="xs" c="dimmed">
            {t('livery.maskCount', { v0: strokes })}
          </Text>
        )}
        <Button size="compact-xs" variant="subtle" color="gray" disabled={!strokes} onClick={() => updateLivery(material, layer.id!, (l) => void (l.mask = []))}>
          {t('livery.clearMask')}
        </Button>
      </div>
      <div className="switch-row">
        <Switch
          label={t('livery.fade')}
          checked={!!fade}
          onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.maskGradient = on ? defaultGradient(material, true) : null));
          }}
        />
        <Info label={t('livery.fadeInfo')} />
      </div>
      {fade && (
        <>
          <div style={{ height: 8 }} />
          <GradientPlacement material={material} g={fade} set={(g, key) => up((l) => void (l.maskGradient = g), key)} />
          <Field label={t('livery.fadeRange')}>
            <MRange
              value={[stops[0].at, stops[stops.length - 1].at]}
              onChange={([a, b]) =>
                up(
                  (l) =>
                    void (l.maskGradient = {
                      ...fade,
                      stops: [
                        { at: a, color: [1, 1, 1, stops[0].color[3]] },
                        { at: b, color: [1, 1, 1, stops[stops.length - 1].color[3]] },
                      ],
                    }),
                  'fade',
                )
              }
            />
          </Field>
          <Button
            mt={8}
            size="compact-xs"
            variant="light"
            color="gray"
            leftSection={<IconFlipVertical size={13} />}
            onClick={() => up((l) => void (l.maskGradient = { ...fade, stops: stops.map((x) => ({ at: x.at, color: [1, 1, 1, 1 - x.color[3]] })) }))}
          >
            {t('livery.fadeInvert')}
          </Button>
        </>
      )}
    </Section>
  );
}

/** A two-handle 0..1 range shown in percent. */
function MRange({ value, onChange }: { value: [number, number]; onChange: (v: [number, number]) => void }) {
  return <RangeSlider size="sm" min={0} max={1} step={0.01} minRange={0.01} value={value} label={(v) => `${Math.round(v * 100)}%`} onChange={onChange} />;
}

type G = NonNullable<LiveryLayer['gradient']>;

/** Where a gradient (or fade) runs: linear or radial, a direction from the part's own size, and its two ends in mm. */
function GradientPlacement({ material, g, set }: { material: string; g: G; set: (g: G, key?: string) => void }) {
  // Directions from the material's box (Unity root space: x right, y up, z forward).
  const along = (axis: number, flip = false, radial = false) => {
    const b = materialBounds(material);
    if (!b) return;
    const mid = [0, 1, 2].map((i) => (b.min[i] + b.max[i]) / 2);
    const at = (x: number) => mid.map((v, i) => (i === axis ? x : v));
    const [lo, hi] = flip ? [b.max[axis], b.min[axis]] : [b.min[axis], b.max[axis]];
    set(radial ? { ...g, shape: 'radial', start: mid, end: at(b.max[axis]) } : { ...g, shape: 'linear', start: at(lo), end: at(hi) });
  };
  return (
    <>
      <Field label={t('livery.gradShape')}>
        <SegmentedControl
          size="xs"
          fullWidth
          value={g.shape ?? 'linear'}
          data={[
            { value: 'linear', label: t('livery.gradLinear') },
            { value: 'radial', label: t('livery.gradRadial') },
          ]}
          onChange={(v) => set({ ...g, shape: v as G['shape'] })}
        />
      </Field>
      <Field label={t('livery.gradDirection')}>
        <SimpleGrid cols={2} spacing={4}>
          <Button size="compact-xs" variant="light" color="cyan" onClick={() => along(2)}>
            {t('livery.dirLength')}
          </Button>
          <Button size="compact-xs" variant="light" color="cyan" onClick={() => along(1, true)}>
            {t('livery.dirDown')}
          </Button>
          <Button size="compact-xs" variant="light" color="cyan" onClick={() => along(0)}>
            {t('livery.dirAcross')}
          </Button>
          <Button size="compact-xs" variant="light" color="grape" onClick={() => along(2, false, true)}>
            {t('livery.dirCentre')}
          </Button>
        </SimpleGrid>
      </Field>
      <Field label={t('livery.gradStart')}>
        <Vec value={(g.start ?? [0, 0, 0]).map((x) => x ?? 0)} scale={1000} step={5} decimalScale={1} onChange={(v) => set({ ...g, start: v }, 'gs')} />
      </Field>
      <Field label={t('livery.gradEnd')}>
        <Vec value={(g.end ?? [0, 0, 0]).map((x) => x ?? 0)} scale={1000} step={5} decimalScale={1} onChange={(v) => set({ ...g, end: v }, 'ge')} />
      </Field>
    </>
  );
}

const cssA = (c: number[]) =>
  `rgba(${c
    .slice(0, 3)
    .map((x) => Math.round((x ?? 0) * 255))
    .join(',')},${c[3] ?? 1})`;

/** Gradient layer: where it runs, and 2 to 4 colour stops on a ramp. */
function GradientSection({ material, layer, up }: { material: string; layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  const g = layer.gradient ?? defaultGradient(material, false);
  const stops = gradientStops(g);
  const set = (next: { at: number; color: number[] }[], key?: string) => up((l) => void (l.gradient = { ...g, stops: next }), key);
  return (
    <Section id="livery-kind" title={t('livery.gradient')} icon={IconColorFilter} color="var(--paint)">
      <div className="gradient-ramp">
        <i style={{ background: `linear-gradient(90deg, ${stops.map((x) => `${cssA(x.color)} ${x.at * 100}%`).join(', ')})` }} />
      </div>
      <Stack gap={6} mb={8}>
        {stops.map((x, i) => (
          <Group key={i} gap={10} wrap="nowrap" align="center">
            <Color
              swatch
              alpha
              value={x.color}
              onChange={(c) =>
                set(
                  stops.map((y, j) => (j === i ? { ...y, color: c } : y)),
                  `stopc${i}`,
                )
              }
            />
            <MSlider
              style={{ flex: 1 }}
              size="sm"
              min={0}
              max={1}
              step={0.01}
              value={x.at}
              label={(v) => `${Math.round(v * 100)}%`}
              onChange={(v) =>
                set(
                  stops.map((y, j) => (j === i ? { ...y, at: v } : y)),
                  `stopa${i}`,
                )
              }
            />
            <Text size="xs" ff="monospace" w={34} ta="right" c="dimmed">
              {Math.round(x.at * 100)}%
            </Text>
            <ActionIcon variant="subtle" color="gray" size="sm" disabled={stops.length <= 2} aria-label={t('livery.removeStop')} onClick={() => set(stops.filter((_, j) => j !== i))}>
              <IconX size={13} />
            </ActionIcon>
          </Group>
        ))}
      </Stack>
      <Button
        size="compact-xs"
        variant="light"
        color="pink"
        leftSection={<IconPlus size={13} />}
        disabled={stops.length >= 4}
        onClick={() => {
          // A new stop halfway along the widest gap, in the colour already there.
          let i = 0;
          for (let j = 1; j < stops.length - 1; j++) if (stops[j + 1].at - stops[j].at > stops[i + 1].at - stops[i].at) i = j;
          const a = stops[i],
            b = stops[i + 1];
          set([...stops.slice(0, i + 1), { at: (a.at + b.at) / 2, color: a.color.map((v, k) => (v + b.color[k]) / 2) }, ...stops.slice(i + 1)]);
        }}
      >
        {t('livery.addStop')}
      </Button>
      <div style={{ height: 12 }} />
      <GradientPlacement material={material} g={g} set={(next, key) => up((l) => void (l.gradient = next), key)} />
    </Section>
  );
}

/** A trim style's picture for its button, drawn by the same code as the layer. */
function trimPicture(trim: Trim, size: number[]): string {
  const c = document.createElement('canvas');
  const k = 96 / Math.max(size[0], size[1]);
  [c.width, c.height] = [Math.max(8, Math.round(size[0] * k)), Math.max(8, Math.round(size[1] * k))];
  const px = (m: number | null | undefined) => (m ?? 0) * k;
  drawTrim(c.getContext('2d')!, c.width, c.height, {
    style: trim.style ?? 'cutFrame',
    width: Math.max(1.2, px(trim.width)),
    corner: px(trim.corner),
    inset: px(trim.inset),
    dash: px(trim.dash),
    gap: px(trim.gap),
    count: trim.count ?? 3,
  });
  return c.toDataURL();
}

/** Trim layer: presets, the line style and its lengths (mm). */
function TrimSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  const tr: Trim = { ...TRIM_PRESETS[0].trim, ...layer.trim };
  const pictures = useMemo(() => TRIM_PRESETS.map((p) => trimPicture(p.trim, p.size)), []);
  const set = (fn: (x: Trim) => void, key?: string) =>
    up((l) => {
      const next = { ...TRIM_PRESETS[0].trim, ...l.trim };
      fn(next);
      l.trim = next;
    }, key);
  const mm = (k: 'width' | 'corner' | 'inset' | 'dash' | 'gap', label: string, max: number) => (
    <Field label={label}>
      <Slider value={(tr[k] ?? 0) * 1000} min={0} max={max} step={0.1} unit=" mm" onChange={(v) => set((x) => void (x[k] = v / 1000), k)} />
    </Field>
  );
  return (
    <Section id="livery-kind" title={t('livery.trim')} icon={IconBorderCorners} color="var(--paint)" info={t('livery.trimInfo')}>
      <div className="trim-presets">
        {TRIM_PRESETS.map((p, i) => (
          <Tooltip key={p.id} label={t(`livery.trimPreset.${p.id}`)}>
            <UnstyledButton
              className="trim-preset"
              aria-label={t(`livery.trimPreset.${p.id}`)}
              aria-selected={JSON.stringify(p.trim) === JSON.stringify(tr)}
              onClick={() => up((l) => void ((l.trim = { ...p.trim }), (l.size = [...p.size])))}
            >
              <img src={pictures[i]} alt="" draggable={false} />
            </UnstyledButton>
          </Tooltip>
        ))}
      </div>
      <Field label={t('livery.trimStyle')}>
        <Select
          value={tr.style ?? 'cutFrame'}
          allowDeselect={false}
          data={TRIM_STYLES.map((v) => ({ value: v, label: t(`livery.trimStyle.${v}`) }))}
          onChange={(v) => v && set((x) => void (x.style = v as Trim['style']))}
        />
      </Field>
      {mm('width', t('livery.trimWidth'), 5)}
      {['cutFrame', 'notched', 'double', 'brackets', 'panels'].includes(tr.style ?? '') && mm('corner', tr.style === 'brackets' ? t('livery.trimArm') : t('livery.trimCorner'), 20)}
      {mm('inset', t('livery.trimInset'), 10)}
      {tr.style !== 'hazard' && tr.style !== 'panels' && mm('dash', t('livery.trimDash'), 10)}
      {((tr.dash ?? 0) > 0 || tr.style === 'panels') && mm('gap', tr.style === 'panels' ? t('livery.trimPanelGap') : t('livery.trimGap'), 10)}
      {['ticks', 'chevron', 'hazard', 'seam', 'panels'].includes(tr.style ?? '') && (
        <Field label={t('livery.trimCount')}>
          <Slider value={tr.count ?? 3} min={1} max={tr.style === 'ticks' ? 48 : 16} step={1} onChange={(v) => set((x) => void (x.count = Math.round(v)), 'count')} />
        </Field>
      )}
    </Section>
  );
}

/** Text layer: the words, font, letter height and spacing; the box's width follows the text. */
function TextSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  const text = layer.text ?? { content: '' };
  const height = layer.size?.[1] ?? 0.008;
  // Keeps the box as wide as the letters at their height once the font has them.
  const fit = async (next: NonNullable<LiveryLayer['text']>, h: number, key: string) => {
    up((l) => void ((l.text = next), (l.size = [l.size?.[0] ?? h * 4, h])), key);
    await loadFont(next.font, next.content || ' ').catch(() => undefined);
    const c = await loadedTextCanvas({ ...next, sizePx: 160 });
    up((l) => void (l.size = [(h * c.width) / c.height, h]), key);
  };
  return (
    <Section id="livery-kind" title={t('livery.text')} icon={IconTypography} color="var(--paint)">
      <Textarea
        autosize
        minRows={1}
        maxRows={4}
        aria-label={t('livery.textContent')}
        value={text.content ?? ''}
        onChange={(e) => void fit({ ...text, content: e.currentTarget.value }, height, 'text')}
      />
      <div style={{ height: 8 }} />
      <Field label={t('livery.textFont')}>
        <FontPicker value={text.font ?? null} onChange={(f) => void fit({ ...text, font: f }, height, 'font')} />
      </Field>
      <Field label={t('livery.textHeight')}>
        <Slider value={height * 1000} min={1} max={60} step={0.5} unit=" mm" onChange={(v) => void fit(text, v / 1000, 'theight')} />
      </Field>
      <Field label={t('text.tracking')}>
        <Slider value={text.tracking ?? 0} min={-0.1} max={0.6} step={0.01} onChange={(v) => void fit({ ...text, tracking: v }, height, 'tracking')} />
      </Field>
    </Section>
  );
}

/** A decal's or tile's picture: its thumbnail and name, and choosing another. */
function ImageField({ layer, pick, up }: { layer: LiveryLayer; pick: () => Promise<string | null | undefined>; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  return (
    <div className="image-field">
      {layer.image ? <img src={fileUrl(layer.image)} alt="" className="decal-thumb" draggable={false} /> : <IconPhoto size={20} />}
      <Text size="sm" truncate>
        {layer.image ? baseName(layer.image) : ''}
      </Text>
      <Button
        variant="light"
        color="pink"
        size="compact-sm"
        leftSection={<IconPhoto size={14} />}
        onClick={async () => {
          const f = await pick();
          if (f) up((l) => void (l.image = f));
        }}
      >
        {t('livery.pickImage')}
      </Button>
    </div>
  );
}

/** Tile layer: its picture, repeat size and turn. */
function TileSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  return (
    <Section id="livery-kind" title={t('livery.tile')} icon={IconLayoutGrid} color="var(--paint)" info={t('livery.tileInfo')}>
      <ImageField layer={layer} pick={pickTileImage} up={up} />
      <Field label={t('livery.tileScale')}>
        <Slider value={(layer.scale ?? 0.03) * 1000} min={2} max={300} step={1} unit=" mm" onChange={(v) => up((l) => void (l.scale = v / 1000), 'scale')} />
      </Field>
      <Field label={t('livery.patternRotation')}>
        <Vec
          value={(layer.patternRotation ?? [0, 0, 0]) as [number, number, number]}
          labels={['X', 'Y', 'Z']}
          step={5}
          decimalScale={0}
          onChange={(v) => up((l) => void (l.patternRotation = v), 'rot')}
        />
      </Field>
    </Section>
  );
}

/** Where a decal, trim or text sits on its part, by number (dragging in the 3D view sets the same values), and its mirror copy. */
function PlacementSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  const [w, h] = layer.size ?? [0.04, 0.04];
  return (
    <Section id="livery-place" title={t('livery.placement')} icon={IconArrowsMove} color="var(--scan)" info={`${t('livery.placementInfo')} ${t('livery.placeHint')}`}>
      {!layer.position && (
        <Alert color="pink" variant="light" p="xs" mb="sm">
          {t('livery.notPlaced')}
        </Alert>
      )}
      {layer.position && (
        <>
          <Field label={t('livery.position')}>
            <Vec value={layer.position.map((x) => x ?? 0)} scale={1000} step={0.5} decimalScale={1} onChange={(v) => up((l) => void (l.position = v), 'pos')} />
          </Field>
          <Field label={t('livery.rotation')}>
            <Vec value={(layer.rotation ?? [0, 0, 0]).map((x) => x ?? 0)} step={1} decimalScale={1} onChange={(v) => up((l) => void (l.rotation = v), 'rot3')} />
          </Field>
        </>
      )}
      {layer.kind !== 'text' && (
        <Field label={t('livery.size')}>
          <Vec value={[w ?? 0.04, h ?? 0.04]} labels={['W', 'H']} scale={1000} step={1} decimalScale={1} min={1} onChange={(v) => up((l) => void (l.size = v), 'size')} />
        </Field>
      )}
      <Field label={t('livery.angle')}>
        <Slider value={layer.angle ?? 0} min={-180} max={180} step={1} unit="°" onChange={(v) => up((l) => void (l.angle = v), 'angle')} />
      </Field>
      <Field label={t('livery.depth')}>
        <Group gap={6} wrap="nowrap">
          <Num style={{ flex: 1 }} value={layer.depth ?? 0.01} scale={1000} step={1} decimalScale={1} min={0.5} onChange={(v) => up((l) => void (l.depth = v), 'depth')} />
          <Info label={t('livery.depthInfo')} />
        </Group>
      </Field>
      <div className="switch-row" style={{ marginTop: 10 }}>
        {layer.kind === 'decal' && (
          <Switch
            label={t('livery.mirror')}
            checked={!!layer.mirror}
            onChange={(e) => {
              const on = e.currentTarget.checked;
              up((l) => void (l.mirror = on));
            }}
          />
        )}
        <Tooltip label={t('livery.symmetricInfo')} multiline w={260}>
          <Switch
            color="cyan"
            label={t('livery.symmetric')}
            checked={!!layer.symmetric}
            onChange={(e) => {
              const on = e.currentTarget.checked;
              up((l) => void (l.symmetric = on));
            }}
          />
        </Tooltip>
      </div>
    </Section>
  );
}

/** Puts a copy of the layer on other parts of the item (each of their materials), in one step. */
function CopyToParts({ material, layer }: { material: string; layer: LiveryLayer }) {
  const item = useItem();
  const [picked, setPicked] = useState<string[]>([]);
  const [open, setOpen] = useState(false);
  const targets = (item?.parts ?? []).flatMap((p) =>
    p.materials.filter((m) => m !== material).map((m) => ({ material: m, part: p.id, label: p.materials.length > 1 ? `${partLabel(p)} · ${m}` : partLabel(p) })),
  );
  const disabled = layer.kind === 'brush' || !targets.length;
  return (
    <Popover opened={open} onChange={setOpen} position="bottom-end" withArrow shadow="md" width={280}>
      <Popover.Target>
        <Tooltip label={layer.kind === 'brush' ? t('livery.copyToBrush') : `${t('livery.copyTo')}: ${t('livery.copyToInfo')}`} multiline w={240}>
          <ActionIcon variant="subtle" color="teal" aria-label={t('livery.copyTo')} disabled={disabled} onClick={() => setOpen((o) => !o)}>
            <IconShare3 size={16} />
          </ActionIcon>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown>
        <Checkbox.Group value={picked} onChange={setPicked}>
          <Stack gap={6} mah={260} style={{ overflowY: 'auto' }}>
            {targets.map((x) => (
              <Checkbox key={x.material} value={x.material} label={x.label} />
            ))}
          </Stack>
        </Checkbox.Group>
        <Group justify="space-between" mt="sm">
          <Button size="compact-xs" variant="subtle" color="gray" onClick={() => setPicked(picked.length === targets.length ? [] : targets.map((x) => x.material))}>
            {t('livery.copyToAll')}
          </Button>
          <Button
            size="compact-sm"
            color="teal"
            disabled={!picked.length}
            onClick={() => {
              copyLiveryTo(
                material,
                layer.id!,
                targets.filter((x) => picked.includes(x.material)),
              );
              setOpen(false);
              setPicked([]);
            }}
          >
            {t('livery.copyToDo', { v0: picked.length })}
          </Button>
        </Group>
      </Popover.Dropdown>
    </Popover>
  );
}

/** A GIF decal plays in game like a dynamic layer: when, how and how fast (its own rate unless set). */
function DecalPlayback({ material, layer }: { material: string; layer: LiveryLayer }) {
  const [gif, setGif] = useState<{ path: string; frames: SourceFrames } | null>(null);
  useEffect(() => {
    let live = true;
    const path = layer.image!;
    sourceFrames(path).then(
      (frames) => live && setGif({ path, frames }),
      () => undefined,
    );
    return () => void (live = false);
  }, [layer.image]);
  const s = gif && gif.path === layer.image ? gif.frames : null;
  if (!s || s.frames.length < 2) return null;
  return (
    <Section id="livery-play" title={t('livery.playback')} icon={IconPlayerPlay} color="var(--violet)" info={t('livery.playbackHint', { v0: s.frames.length })}>
      <Playback x={layer} fps={layer.fps || +s.fps.toFixed(1)} up={(fn, key) => updateLivery(material, layer.id!, fn, key)} />
    </Section>
  );
}

const css = (c: number[] | null | undefined) =>
  `rgb(${(c ?? [0, 0, 0])
    .slice(0, 3)
    .map((x) => Math.round((x ?? 0) * 255))
    .join(',')})`;

/** A seed with a button that rolls a new one. */
function Seed({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  return (
    <Group gap={6} wrap="nowrap">
      <NumberInput style={{ flex: 1 }} size="xs" value={value} min={0} max={9999} allowDecimal={false} onChange={(v) => onChange(Number(v) || 0)} />
      <Tooltip label={t('livery.reroll')}>
        <ActionIcon variant="default" onClick={() => onChange(Math.floor(Math.random() * 10000))}>
          <IconDice5 size={15} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

/** Pattern layer: presets, the pattern, its four colours, size, rotation and seed. */
function PatternSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  const colors = layer.colors ?? PATTERN_PRESETS[0].colors;
  return (
    <Section id="livery-kind" title={t('livery.pattern')} icon={IconTexture} color="var(--paint)">
      <div className="presets">
        {PATTERN_PRESETS.map((p) => (
          <Tooltip key={p.id} label={t(`livery.preset.${p.id}`)}>
            <UnstyledButton
              className="preset"
              aria-selected={layer.pattern === p.pattern && JSON.stringify(colors) === JSON.stringify(p.colors)}
              onClick={() => up((l) => void ((l.pattern = p.pattern), (l.colors = p.colors)))}
            >
              {p.colors.map((c, i) => (
                <i key={i} style={{ background: css(c) }} />
              ))}
            </UnstyledButton>
          </Tooltip>
        ))}
      </div>
      <Field label={t('livery.patternKind')}>
        <PatternPicker value={layer.pattern ?? 'woodland'} colors={colors} onChange={(p) => up((l) => void (l.pattern = p))} />
      </Field>
      <Field label={t('livery.colors')}>
        <Group gap={6}>
          {[0, 1, 2, 3].map((i) => (
            <Color
              key={i}
              swatch
              value={colors[i] ?? [0, 0, 0, 1]}
              onChange={(c) => up((l) => void (l.colors = [0, 1, 2, 3].map((j) => (j === i ? [c[0], c[1], c[2], 1] : (colors[j] ?? [0, 0, 0, 1])))), `color${i}`)}
            />
          ))}
        </Group>
      </Field>
      <Field label={t('livery.patternScale')}>
        <Slider value={(layer.scale ?? 0.05) * 1000} min={5} max={300} step={1} unit=" mm" onChange={(v) => up((l) => void (l.scale = v / 1000), 'scale')} />
      </Field>
      <Field label={t('livery.patternRotation')}>
        <Vec
          value={(layer.patternRotation ?? [0, 0, 0]) as [number, number, number]}
          labels={['X', 'Y', 'Z']}
          step={5}
          decimalScale={0}
          onChange={(v) => up((l) => void (l.patternRotation = v), 'rot')}
        />
      </Field>
      <Field label={t('livery.seed')}>
        <Seed value={layer.seed ?? 0} onChange={(v) => up((l) => void (l.seed = v), 'seed')} />
      </Field>
    </Section>
  );
}

/** Wear layer: how much, where (edges, patches, scratches), the bare metal and growth with kills. */
function WearSection({ layer, up }: { layer: LiveryLayer; up: (fn: (l: LiveryLayer) => void, key?: string) => void }) {
  return (
    <Section id="livery-kind" title={t('livery.wear')} icon={IconBrushOff} color="var(--paint)" info={t('livery.wearInfo')}>
      <Field label={t('livery.wearAmount')}>
        <Slider value={layer.amount ?? 0.4} onChange={(v) => up((l) => void (l.amount = v), 'amount')} />
      </Field>
      <Field label={t('livery.wearEdges')}>
        <Slider value={layer.edgeWeight ?? 0.7} onChange={(v) => up((l) => void (l.edgeWeight = v), 'edges')} />
      </Field>
      <Field label={t('livery.wearScratches')}>
        <Slider value={layer.scratches ?? 0.3} onChange={(v) => up((l) => void (l.scratches = v), 'scratches')} />
      </Field>
      <Field label={t('livery.wearScale')}>
        <Slider value={(layer.scale ?? 0.03) * 1000} min={3} max={200} step={1} unit=" mm" onChange={(v) => up((l) => void (l.scale = v / 1000), 'scale')} />
      </Field>
      <Field label={t('livery.metalColor')}>
        <Color value={layer.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.color = [c[0], c[1], c[2], 1]), 'color')} />
      </Field>
      <Field label={t('livery.wearMetallic')}>
        <Slider value={layer.metallic ?? 0.85} onChange={(v) => up((l) => void (l.metallic = v), 'metal')} />
      </Field>
      <Field label={t('livery.wearSmoothness')}>
        <Slider value={layer.smoothness ?? 0.55} onChange={(v) => up((l) => void (l.smoothness = v), 'smooth')} />
      </Field>
      <Field label={t('livery.seed')}>
        <Seed value={layer.seed ?? 0} onChange={(v) => up((l) => void (l.seed = v), 'seed')} />
      </Field>
      <Field label={t('livery.killsToFull')}>
        <Group gap={6} wrap="nowrap">
          <NumberInput
            style={{ flex: 1 }}
            size="xs"
            value={layer.killsToFull ?? 0}
            min={0}
            max={100000}
            step={50}
            allowDecimal={false}
            onChange={(v) => up((l) => void (l.killsToFull = Number(v) || 0), 'kills')}
          />
          <Info label={t('livery.killsToFullInfo')} />
        </Group>
      </Field>
    </Section>
  );
}

/** The selected paint layer. */
export function LiveryInspector() {
  const sel = useLiverySelection();
  const glow = useStore((s) => (sel ? materialOf(s.doc, sel.material)?.params?.emissiveIntensity : null) ?? 1);
  if (!sel) return null;
  const { material, layer } = sel;
  const up = (fn: (l: LiveryLayer) => void, key?: string) => updateLivery(material, layer.id!, fn, key);
  const K = kindOf(layer.kind);
  const hidden = layer.visible === false;
  return (
    <>
      <InspHead
        kind={t('livery.layer', { v0: K.label() })}
        icon={K.icon}
        color="var(--paint)"
        onBack={() => useStore.getState().select(ownerOf(useStore.getState().doc, material))}
        name={{ value: layer.name ?? '', placeholder: liveryName(layer), onChange: (n) => up((l) => void (l.name = n), 'name') }}
        actions={
          <>
            <Tooltip label={t('livery.visible')}>
              <ActionIcon variant="subtle" color={hidden ? 'dark.2' : 'pink'} aria-label={t('livery.visible')} aria-pressed={!hidden} onClick={() => up((l) => void (l.visible = hidden))}>
                {hidden ? <IconEyeOff size={16} /> : <IconEye size={16} />}
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('livery.duplicate')}>
              <ActionIcon variant="subtle" color="gray" aria-label={t('livery.duplicate')} onClick={() => duplicateLivery(material, layer.id!)}>
                <IconCopy size={16} />
              </ActionIcon>
            </Tooltip>
            <CopyToParts material={material} layer={layer} />
            <Tooltip label={t('livery.delete')}>
              <ActionIcon variant="subtle" color="red" aria-label={t('livery.delete')} onClick={() => removeLivery(material, layer.id!)}>
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </>
        }
      />
      {layer.kind === 'decal' && (
        <Section id="livery-kind" title={t('livery.decal')} icon={IconSticker} color="var(--paint)">
          <ImageField layer={layer} pick={pickDecalImage} up={up} />
        </Section>
      )}
      {layer.kind === 'trim' && <TrimSection layer={layer} up={up} />}
      {layer.kind === 'text' && <TextSection layer={layer} up={up} />}
      {layer.kind === 'gradient' && <GradientSection material={material} layer={layer} up={up} />}
      {layer.kind === 'tile' && <TileSection layer={layer} up={up} />}
      {layer.kind === 'pattern' && <PatternSection layer={layer} up={up} />}
      {layer.kind === 'wear' && <WearSection layer={layer} up={up} />}
      {layer.kind === 'brush' && (
        <Section id="livery-kind" title={t('livery.brush')} icon={IconBrush} color="var(--paint)">
          <p className="hint">{t('livery.brushHint')}</p>
          <Button mt={8} size="compact-xs" variant="light" color="gray" disabled={!(layer.strokes ?? []).length} onClick={() => up((l) => void (l.strokes = []))}>
            {t('livery.clearStrokes')}
          </Button>
        </Section>
      )}
      <Section id="livery-look" title={t('livery.look')} icon={IconPalette} color="var(--paint)">
        {layer.kind !== 'pattern' && layer.kind !== 'wear' && layer.kind !== 'gradient' && (
          <Field label={layer.kind === 'decal' || layer.kind === 'tile' ? t('livery.tint') : t('livery.color')}>
            <Color value={layer.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.color = [c[0], c[1], c[2], 1]), 'color')} />
          </Field>
        )}
        <Field label={t('livery.opacity')}>
          <Slider value={layer.opacity ?? 1} onChange={(v) => up((l) => void (l.opacity = v), 'op')} />
        </Field>
        <Field label={t('livery.blend')}>
          <Group gap={6} wrap="nowrap">
            <Select style={{ flex: 1 }} value={layer.blend ?? 'normal'} data={blendData()} allowDeselect={false} onChange={(v) => v && up((l) => void (l.blend = v as BlendMode))} />
            <Info label={t('livery.blendInfo')} />
          </Group>
        </Field>
        {layer.kind !== 'wear' && (
          <div className="switch-row" style={{ marginTop: 10 }}>
            <Switch
              color="yellow"
              label={t('livery.glowLayer')}
              checked={!!layer.glow}
              onChange={(e) => {
                const on = e.currentTarget.checked;
                up((l) => void (l.glow = on));
              }}
            />
            <Info label={t('livery.glowLayerInfo')} />
          </div>
        )}
        {layer.glow && layer.kind !== 'wear' && (
          <Field label={t('livery.glow')}>
            <Slider value={glow} min={0} max={10} step={0.05} onChange={(v) => useStore.getState().setParams(material, { emissiveIntensity: v }, `glow-${material}`)} />
          </Field>
        )}
      </Section>
      {isProjected(layer) && <PlacementSection layer={layer} up={up} />}
      {isGifDecal(layer) && <DecalPlayback material={material} layer={layer} />}
      <MaskSection material={material} layer={layer} />
    </>
  );
}

const SHAPES: { value: BrushShape; icon: ComponentType<{ size?: number }> }[] = [
  { value: 'free', icon: IconScribble },
  { value: 'line', icon: IconLine },
  { value: 'circle', icon: IconCircle },
  { value: 'fill', icon: IconBucketDroplet },
];

/** What dragging in the 3D view does for the selected paint layer, and the brush settings, over the view. */
export function LiveryBar() {
  const sel = useLiverySelection();
  const brush = useBrush();
  if (!sel) return null;
  const painting = brush.mask || sel.layer.kind === 'brush';
  const what = brush.mask ? t('livery.bar.mask') : sel.layer.kind === 'brush' ? t('livery.bar.brush') : isProjected(sel.layer) ? t('livery.bar.decal') : t('livery.bar.fill');
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
      {painting && (
        <Tooltip label={t('livery.mirrorInfo')}>
          <Switch size="xs" label={t('livery.mirrorPaint')} checked={brush.mirror} onChange={(e) => useBrush.setState({ mirror: e.currentTarget.checked })} />
        </Tooltip>
      )}
      <Switch size="xs" label={t('livery.paintMask')} checked={brush.mask} onChange={(e) => useBrush.setState({ mask: e.currentTarget.checked })} />
    </Group>
  );
}
