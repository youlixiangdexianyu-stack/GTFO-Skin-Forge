import { ActionIcon, Group, Select, Text, Textarea, Tooltip, UnstyledButton } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { IconArrowsMove, IconEye, IconEyeOff, IconLetterT, IconMovie, IconPhoto, IconPlayerPlay, IconStack2, IconTrash, type Icon } from '@tabler/icons-react';
import type Konva from 'konva';
import { useEffect, useRef, useState } from 'react';
import { Image as KImage, Layer, Stage, Transformer } from 'react-konva';
import { baseTexturePath, isGif, LayerSource, layerName, loadImage, naturalSort, sourceSize } from '../frames';
import { baseName, CHANNELS, triggersFor, type CatalogPart, type DynamicLayer, type LayerKind, type PlayMode, type TextureChannel, type Trigger } from '../protocol';
import { materialOf, ownerOf, useItem, useStore } from '../store';
import { Color, Field, Num, Slider, Vec } from './fields';
import { Empty, FEATURE, InspHead, KindGrid, Section } from './Features';
import { FontPicker } from './FontPicker';
import { CHANNEL_LABEL } from './SkinInspector';
import { t } from '../i18n';

const KINDS: { id: LayerKind; label: string; icon: Icon }[] = [
  {
    id: 'image',
    get label() {
      return t('dynamicInspector.k1');
    },
    icon: IconPhoto,
  },
  {
    id: 'sequence',
    get label() {
      return t('dynamicInspector.k2');
    },
    icon: IconStack2,
  },
  {
    id: 'video',
    get label() {
      return t('dynamicInspector.k3');
    },
    icon: IconMovie,
  },
  {
    id: 'text',
    get label() {
      return t('dynamicInspector.k4');
    },
    icon: IconLetterT,
  },
];
const PLAY_MODES: { value: PlayMode; label: string }[] = [
  {
    value: 'loop',
    get label() {
      return t('dynamicInspector.k5');
    },
  },
  {
    value: 'once',
    get label() {
      return t('dynamicInspector.k6');
    },
  },
  {
    value: 'loopFor',
    get label() {
      return t('dynamicInspector.k7');
    },
  },
];
const IMAGES = [
  {
    get name() {
      return t('dynamicInspector.k1');
    },
    extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'],
  },
];
const VIDEOS = [
  {
    get name() {
      return t('dynamicInspector.k3');
    },
    extensions: ['mp4', 'webm', 'mov', 'm4v'],
  },
];

export async function addLayer(material: string, part: string, kind: LayerKind) {
  const { addLayer } = useStore.getState();
  if (kind === 'text')
    return addLayer(material, part, {
      kind,
      get name() {
        return t('dynamicInspector.k4');
      },
      text: { content: 'GTFO', sizePx: 96, color: [1, 1, 1, 1] },
      uvSize: [0.3, 0.12],
    });
  const files = await window.forge.openDialog('files', kind === 'video' ? VIDEOS : IMAGES);
  if (files.length) layerFromFiles(material, part, kind === 'sequence' ? files : files.slice(0, 1));
}

const isVideo = (p: string) => VIDEOS[0].extensions.some((e) => p.toLowerCase().endsWith(`.${e}`));
const isImage = (p: string) => IMAGES[0].extensions.some((e) => p.toLowerCase().endsWith(`.${e}`));

/** New layer from files: a video, one image, or several images or an animated GIF as a sequence (natural order). False when none fit. */
export function layerFromFiles(material: string, part: string, paths: string[]): boolean {
  const video = paths.find(isVideo);
  const images = naturalSort(paths.filter(isImage));
  const sources = video ? [video] : images;
  if (!sources.length) return false;
  const kind: LayerKind = video ? 'video' : images.length > 1 || images.some(isGif) ? 'sequence' : 'image';
  useStore.getState().addLayer(material, part, { kind, name: baseName(sources[0]).replace(/\.[^.]+$/, ''), sources });
  return true;
}

/** 2D UV canvas (Konva): the target texture with the playing layer; drag to move, Transformer to resize and rotate. */
function UvCanvas({ material, layer }: { material: string; layer: DynamicLayer }) {
  const { ref, width } = useElementSize();
  const S = Math.max(64, Math.floor(width));
  const doc = useStore((s) => s.doc);
  const preview = useStore((s) => s.preview);
  const basePath = baseTexturePath(doc, preview, material, layer.channel ?? 'albedo');
  const [base, setBase] = useState<HTMLImageElement | null>(null);
  const node = useRef<Konva.Image>(null);
  const tr = useRef<Konva.Transformer>(null);
  const [frame] = useState(() => document.createElement('canvas'));

  useEffect(() => {
    setBase(null);
    if (basePath) void loadImage(basePath).then(setBase, () => setBase(null));
  }, [basePath]);

  // Play the layer source into an offscreen canvas that Konva draws.
  useEffect(() => {
    const src = new LayerSource(layer);
    const ctx = frame.getContext('2d')!;
    const t0 = performance.now();
    let raf = 0;
    const tick = () => {
      const f = src.at(((performance.now() - t0) / 1000) % src.duration);
      if (f) {
        const [sw, sh] = sourceSize(f);
        const [cx, cy, cw, ch] = layer.crop ?? [0, 0, 1, 1];
        frame.width = Math.max(1, Math.min(512, Math.round(cw * sw)));
        frame.height = Math.max(1, Math.min(512, Math.round(ch * sh)));
        ctx.drawImage(f, cx * sw, cy * sh, cw * sw, ch * sh, 0, 0, frame.width, frame.height);
        node.current?.getLayer()?.batchDraw();
      }
      raf = requestAnimationFrame(tick);
    };
    tick();
    return () => {
      cancelAnimationFrame(raf);
      src.dispose();
    };
  }, [layer.sources, layer.kind, layer.text, layer.fps, layer.crop]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (node.current && tr.current) tr.current.nodes([node.current]);
  }, [S]);

  const [u, v] = layer.uvCenter ?? [0.5, 0.5];
  const [su, sv] = layer.uvSize ?? [0.25, 0.25];
  const w = su * S;
  const h = sv * S;
  const { updateLayer } = useStore.getState();
  // Every move of a drag or resize goes to the document (the 3D view follows live) under one gesture key: one undo step.
  const gesture = useRef('');
  const begin = () => void (gesture.current = `gesture:${layer.id}-${performance.now()}`);
  const commit = () => {
    const n = node.current!;
    const nw = n.width() * n.scaleX();
    const nh = n.height() * n.scaleY();
    n.scale({ x: 1, y: 1 });
    updateLayer(
      material,
      layer.id!,
      (l) => {
        l.uvCenter = [n.x() / S, 1 - n.y() / S];
        l.uvSize = [Math.max(nw, 1) / S, Math.max(nh, 1) / S];
        l.rotation = -n.rotation();
      },
      gesture.current,
    );
  };

  return (
    <div ref={ref} className="uv">
      {width > 0 && (
        <Stage width={S} height={S}>
          <Layer>
            {base && <KImage image={base} width={S} height={S} listening={false} />}
            <KImage
              ref={node}
              image={frame}
              x={u * S}
              y={(1 - v) * S}
              width={w}
              height={h}
              offsetX={w / 2}
              offsetY={h / 2}
              rotation={-(layer.rotation ?? 0)}
              opacity={layer.opacity ?? 1}
              draggable
              onDragStart={begin}
              onDragMove={commit}
              onTransformStart={begin}
              onTransform={commit}
            />
            <Transformer
              ref={tr}
              rotateEnabled
              keepRatio={false}
              boundBoxFunc={(old, box) => (Math.abs(box.width) < 2 || Math.abs(box.height) < 2 ? old : box)}
              borderStroke="#ff7a2f"
              anchorStroke="#ff7a2f"
              anchorFill="#1a1d21"
              anchorSize={8}
              rotateAnchorOffset={18}
            />
          </Layer>
        </Stage>
      )}
    </div>
  );
}

type Timing = { trigger?: Trigger; play?: PlayMode; playSeconds?: number; lowAmmoFraction?: number; fps?: number | null };

/** When something animated plays in game (a dynamic layer, an animated decal): trigger, play mode and, given `fps`, its rate. */
export function Playback({ x, fps, up }: { x: Timing; fps: number | null; up: (fn: (x: Timing) => void, key?: string) => void }) {
  const triggers = triggersFor(useItem());
  return (
    <>
      <Field label={t('dynamicInspector.k18')}>
        <Select value={x.trigger ?? 'always'} onChange={(v) => v && up((l) => void (l.trigger = v as Trigger))} data={triggers} allowDeselect={false} />
      </Field>
      <Field label={t('dynamicInspector.k19')}>
        <Group gap="xs" wrap="nowrap">
          <Select style={{ flex: 1 }} value={x.play ?? 'loop'} onChange={(v) => v && up((l) => void ((l.play = v as PlayMode), (l.playSeconds ||= 3)))} data={PLAY_MODES} allowDeselect={false} />
          {x.play === 'loopFor' && (
            <Num w={86} value={x.playSeconds || 3} step={0.5} decimalScale={1} min={0.1} rightSection={t('dynamicInspector.k20')} onChange={(v) => up((l) => void (l.playSeconds = v), 'ps')} />
          )}
        </Group>
      </Field>
      {x.trigger === 'lowAmmo' && (
        <Field label={t('dynamicInspector.k21')}>
          <Slider value={x.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} onChange={(v) => up((l) => void (l.lowAmmoFraction = v), 'laf')} />
        </Field>
      )}
      {fps !== null && (
        <Field label={t('dynamicInspector.k22')}>
          <Num value={fps} step={1} decimalScale={1} min={1} max={60} onChange={(v) => up((l) => void (l.fps = v), 'fps')} />
        </Field>
      )}
    </>
  );
}

function LayerPanel({ material, layer }: { material: string; layer: DynamicLayer }) {
  const { updateLayer, removeLayer, select } = useStore.getState();
  const up = (fn: (l: DynamicLayer) => void, key?: string) => updateLayer(material, layer.id!, fn, key && `${layer.id}-${key}`);
  const kind = KINDS.find((k) => k.id === layer.kind);
  const hidden = layer.visible === false;
  return (
    <>
      <InspHead
        kind={`${t('dynamicInspector.k8')} ${kind?.label ?? ''}`}
        icon={kind?.icon ?? IconMovie}
        color={FEATURE.layer.color}
        onBack={() => select(ownerOf(useStore.getState().doc, material))}
        name={{ value: layer.name ?? '', placeholder: layerName(layer), onChange: (n) => up((l) => void (l.name = n), 'name') }}
        actions={
          <>
            <Tooltip label={t('dynamicInspector.k31')}>
              <ActionIcon variant="subtle" color={hidden ? 'dark.2' : 'violet'} aria-label={t('dynamicInspector.k31')} aria-pressed={!hidden} onClick={() => up((l) => void (l.visible = hidden))}>
                {hidden ? <IconEyeOff size={16} /> : <IconEye size={16} />}
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('dynamicInspector.k32')}>
              <ActionIcon variant="subtle" color="red" aria-label={t('dynamicInspector.k32')} onClick={() => removeLayer(material, layer.id!)}>
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </>
        }
      />
      <Section id="dyn-place" title={t('dynamicInspector.k10')} icon={IconArrowsMove} color="var(--scan)" info={t('dynamicInspector.k9')}>
        <UvCanvas material={material} layer={layer} />
        <div style={{ height: 12 }} />
        <Field label={t('dynamicInspector.k11')}>
          <Select value={layer.channel ?? 'albedo'} onChange={(c) => c && up((l) => void (l.channel = c as TextureChannel))} data={CHANNELS.map((c) => ({ value: c, label: CHANNEL_LABEL[c] }))} />
        </Field>
        <Field label={t('dynamicInspector.k12')}>
          <Vec value={layer.uvCenter ?? [0.5, 0.5]} labels={['U', 'V']} step={0.01} decimalScale={3} onChange={(v) => up((l) => void (l.uvCenter = v), 'uvc')} />
        </Field>
        <Field label={t('dynamicInspector.k13')}>
          <Vec value={layer.uvSize ?? [0.25, 0.25]} labels={['U', 'V']} step={0.01} decimalScale={3} min={0.001} onChange={(v) => up((l) => void (l.uvSize = v), 'uvs')} />
        </Field>
        <Field label={t('dynamicInspector.k14')}>
          <Num value={layer.rotation ?? 0} step={1} decimalScale={1} suffix="°" onChange={(v) => up((l) => void (l.rotation = v), 'rot')} />
        </Field>
        <Field label={t('dynamicInspector.k15')}>
          <Slider value={layer.opacity ?? 1} onChange={(v) => up((l) => void (l.opacity = v), 'op')} />
        </Field>
        <Field label={t('dynamicInspector.k16')}>
          <Vec value={layer.crop ?? [0, 0, 1, 1]} labels={['X', 'Y', 'W', 'H']} step={0.01} min={0} max={1} onChange={(v) => up((l) => void (l.crop = v), 'crop')} />
        </Field>
      </Section>
      <Section
        id="dyn-play"
        title={t('dynamicInspector.k17')}
        icon={IconPlayerPlay}
        color={FEATURE.layer.color}
        info={
          layer.trigger && layer.trigger !== 'always'
            ? t('dynamicInspector.k23')
            : layer.kind === 'video'
              ? t('dynamicInspector.k24')
              : layer.kind === 'sequence'
                ? t('dynamicInspector.k25', { v0: layer.sources?.length ?? 0 })
                : t('dynamicInspector.k26')
        }
      >
        <Playback x={layer} fps={layer.kind !== 'image' && layer.kind !== 'text' ? (layer.fps ?? 12) : null} up={up} />
      </Section>
      {layer.kind === 'text' && (
        <Section id="dyn-text" title={t('dynamicInspector.k4')} icon={IconLetterT} color={FEATURE.layer.color}>
          <Textarea
            autosize
            minRows={2}
            value={layer.text?.content ?? ''}
            onChange={(e) => {
              const t = e.currentTarget.value;
              up((l) => void (l.text = { ...l.text, content: t }), 'tx');
            }}
          />
          <div style={{ height: 8 }} />
          <Field label={t('dynamicInspector.k27')}>
            <Num value={layer.text?.sizePx ?? 64} step={2} decimalScale={0} min={8} suffix=" px" onChange={(v) => up((l) => void (l.text = { ...l.text, sizePx: v }), 'tsz')} />
          </Field>
          <Field label={t('dynamicInspector.k28')}>
            <Color alpha value={layer.text?.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.text = { ...l.text, color: c }), 'tcol')} />
          </Field>
          <Field label={t('dynamicInspector.k29')}>
            <FontPicker value={layer.text?.font ?? null} onChange={(f) => up((l) => void (l.text = { ...l.text, font: f }), 'tf')} />
          </Field>
          <Field label={t('text.tracking')}>
            <Slider value={layer.text?.tracking ?? 0} min={-0.1} max={0.6} step={0.01} onChange={(v) => up((l) => void (l.text = { ...l.text, tracking: v }), 'ttr')} />
          </Field>
        </Section>
      )}
    </>
  );
}

/** A part's dynamic layers, per material, with adding right there. */
export function PartLayers({ part }: { part: Pick<CatalogPart, 'id' | 'materials'> }) {
  const doc = useStore((s) => s.doc);
  const selection = useStore((s) => s.selection);
  const { select, updateLayer } = useStore.getState();
  return part.materials.map((mat) => {
    const layers = (materialOf(doc, mat)?.layers ?? []).filter(Boolean) as DynamicLayer[];
    return (
      <Section
        key={mat}
        feature="layer"
        title={part.materials.length > 1 ? `${t('dynamicInspector.k33')} · ${mat}` : t('dynamicInspector.k33')}
        info={`${t('dynamicInspector.k34')} ${t('dynamicInspector.k38')}`}
      >
        <KindGrid kinds={KINDS} color={FEATURE.layer.color} onPick={(k) => void addLayer(mat, part.id, k)} />
        <div className="list" style={{ marginTop: 10 }}>
          {layers.map((l) => {
            const K = KINDS.find((k) => k.id === l.kind);
            return (
              <UnstyledButton
                key={l.id}
                className={`item livery-item ${l.visible === false ? 'off' : ''}`}
                aria-selected={selection?.kind === 'layer' && selection.id === l.id}
                onClick={() => select({ kind: 'layer', material: mat, id: l.id! })}
              >
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  color={l.visible === false ? 'dark.3' : 'violet'}
                  aria-label={t('dynamicInspector.k31')}
                  onClick={(e) => (e.stopPropagation(), updateLayer(mat, l.id!, (x) => void (x.visible = x.visible === false)))}
                >
                  {l.visible === false ? <IconEyeOff size={14} /> : <IconEye size={14} />}
                </ActionIcon>
                {K ? <K.icon size={16} color={FEATURE.layer.color} /> : <span />}
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" truncate>
                    {layerName(l)}
                  </Text>
                  <Text size="xs" c="dimmed" truncate>
                    {K?.label} · {CHANNEL_LABEL[l.channel ?? 'albedo']}
                  </Text>
                </div>
                <span />
              </UnstyledButton>
            );
          })}
          {!layers.length && <Empty>{t('dynamicInspector.k35')}</Empty>}
        </div>
      </Section>
    );
  });
}

/** The selected dynamic layer. */
export function LayerInspector() {
  const selection = useStore((s) => s.selection);
  const layer = useStore((s) => (selection?.kind === 'layer' ? materialOf(s.doc, selection.material)?.layers?.find((l) => l?.id === selection.id) : undefined));
  if (selection?.kind !== 'layer' || !layer) return null;
  return <LayerPanel material={selection.material} layer={layer} />;
}
