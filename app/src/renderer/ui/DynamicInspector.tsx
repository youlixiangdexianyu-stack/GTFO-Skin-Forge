import { ActionIcon, Button, Group, Select, SimpleGrid, Switch, Text, Textarea, TextInput, UnstyledButton } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { IconEye, IconEyeOff, IconLetterT, IconMovie, IconPhoto, IconStack2, IconTrash } from '@tabler/icons-react';
import type Konva from 'konva';
import { useEffect, useRef, useState, type ComponentType } from 'react';
import { Image as KImage, Layer, Stage, Transformer } from 'react-konva';
import { baseTexturePath, LayerSource, loadImage, naturalSort, sourceSize } from '../frames';
import { baseName, CHANNELS, triggersFor, type DynamicLayer, type LayerKind, type PlayMode, type TextureChannel, type Trigger } from '../protocol';
import { materialOf, useItem, useStore } from '../store';
import { Color, Field, Num, Slider, Vec } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { CHANNEL_LABEL } from './SkinInspector';
import { partLabel, t } from '../i18n';

const KINDS: { id: LayerKind; label: string; icon: ComponentType<{ size?: number }> }[] = [
  { id: 'image', get label() { return t('dynamicInspector.k1'); }, icon: IconPhoto },
  { id: 'sequence', get label() { return t('dynamicInspector.k2'); }, icon: IconStack2 },
  { id: 'video', get label() { return t('dynamicInspector.k3'); }, icon: IconMovie },
  { id: 'text', get label() { return t('dynamicInspector.k4'); }, icon: IconLetterT },
];
const PLAY_MODES: { value: PlayMode; label: string }[] = [
  { value: 'loop', get label() { return t('dynamicInspector.k5'); } },
  { value: 'once', get label() { return t('dynamicInspector.k6'); } },
  { value: 'loopFor', get label() { return t('dynamicInspector.k7'); } },
];
const IMAGES = [{ get name() { return t('dynamicInspector.k1'); }, extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'] }];
const VIDEOS = [{ get name() { return t('dynamicInspector.k3'); }, extensions: ['mp4', 'webm', 'mov', 'm4v'] }];

async function addLayer(material: string, part: string, kind: LayerKind) {
  const { addLayer } = useStore.getState();
  if (kind === 'text') return addLayer(material, part, { kind, get name() { return t('dynamicInspector.k4'); }, text: { content: 'GTFO', sizePx: 96, color: [1, 1, 1, 1] }, uvSize: [0.3, 0.12] });
  const files = await window.forge.openDialog('files', kind === 'video' ? VIDEOS : IMAGES);
  if (files.length) layerFromFiles(material, part, kind === 'sequence' ? files : files.slice(0, 1));
}

const isVideo = (p: string) => VIDEOS[0].extensions.some((e) => p.toLowerCase().endsWith(`.${e}`));
const isImage = (p: string) => IMAGES[0].extensions.some((e) => p.toLowerCase().endsWith(`.${e}`));

/** New layer from files: a video, one image, or several images as a sequence (natural order). False when none fit. */
export function layerFromFiles(material: string, part: string, paths: string[]): boolean {
  const video = paths.find(isVideo);
  const images = naturalSort(paths.filter(isImage));
  const sources = video ? [video] : images;
  if (!sources.length) return false;
  const kind: LayerKind = video ? 'video' : images.length > 1 ? 'sequence' : 'image';
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
  const commit = (key: string) => {
    const n = node.current!;
    const nw = n.width() * n.scaleX();
    const nh = n.height() * n.scaleY();
    n.scale({ x: 1, y: 1 });
    updateLayer(material, layer.id!, (l) => {
      l.uvCenter = [n.x() / S, 1 - n.y() / S];
      l.uvSize = [nw / S, nh / S];
      l.rotation = -n.rotation();
    }, `${layer.id}-${key}`);
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
              onDragMove={() => commit('uvc')}
              onTransformEnd={() => commit('uvt')}
            />
            <Transformer ref={tr} rotateEnabled keepRatio={false} borderStroke="#ff7a2f" anchorStroke="#ff7a2f" anchorFill="#1a1d21" anchorSize={8} rotateAnchorOffset={18} />
          </Layer>
        </Stage>
      )}
    </div>
  );
}

function LayerPanel({ material, layer }: { material: string; layer: DynamicLayer }) {
  const triggers = triggersFor(useItem());
  const { updateLayer, removeLayer, select } = useStore.getState();
  const up = (fn: (l: DynamicLayer) => void, key?: string) => updateLayer(material, layer.id!, fn, key && `${layer.id}-${key}`);
  const kind = KINDS.find((k) => k.id === layer.kind);
  return (
    <>
      <div className="insp-head">
        <UnstyledButton className="kind" onClick={() => select({ kind: 'part', id: materialOf(useStore.getState().doc, material)?.part ?? '' })}>
          {t('dynamicInspector.k8')} {kind?.label}
        </UnstyledButton>
        <TextInput mt={4} size="sm" value={layer.name ?? ''} onChange={(e) => {
          const n = e.currentTarget.value;
          up((l) => void (l.name = n), 'name');
        }} />
      </div>
      <div className="section">
        <UvCanvas material={material} layer={layer} />
        <p className="hint" style={{ marginTop: 6 }}>
          {t('dynamicInspector.k9')}
        </p>
      </div>
      <div className="section">
        <h3>{t('dynamicInspector.k10')}</h3>
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
          <Num value={layer.rotation ?? 0} step={1} decimalScale={1} onChange={(v) => up((l) => void (l.rotation = v), 'rot')} />
        </Field>
        <Field label={t('dynamicInspector.k15')}>
          <Slider value={layer.opacity ?? 1} onChange={(v) => up((l) => void (l.opacity = v), 'op')} />
        </Field>
        <Field label={t('dynamicInspector.k16')}>
          <Vec value={layer.crop ?? [0, 0, 1, 1]} labels={['X', 'Y', 'W', 'H']} step={0.01} min={0} max={1} hideControls onChange={(v) => up((l) => void (l.crop = v), 'crop')} />
        </Field>
      </div>
      <div className="section">
        <h3>{t('dynamicInspector.k17')}</h3>
        <Field label={t('dynamicInspector.k18')}>
          <Select value={layer.trigger ?? 'always'} onChange={(v) => v && up((l) => void (l.trigger = v as Trigger))} data={triggers} allowDeselect={false} />
        </Field>
        <Field label={t('dynamicInspector.k19')}>
          <Group gap="xs" wrap="nowrap">
            <Select style={{ flex: 1 }} value={layer.play ?? 'loop'} onChange={(v) => v && up((l) => void (l.play = v as PlayMode))} data={PLAY_MODES} allowDeselect={false} />
            {layer.play === 'loopFor' && <Num w={86} value={layer.playSeconds ?? 3} step={0.5} decimalScale={1} min={0.1} rightSection={t('dynamicInspector.k20')} onChange={(v) => up((l) => void (l.playSeconds = v), 'ps')} />}
          </Group>
        </Field>
        {layer.trigger === 'lowAmmo' && (
          <Field label={t('dynamicInspector.k21')}>
            <Slider value={layer.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} onChange={(v) => up((l) => void (l.lowAmmoFraction = v), 'laf')} />
          </Field>
        )}
        {layer.kind !== 'image' && layer.kind !== 'text' && (
          <Field label={t('dynamicInspector.k22')}>
            <Num value={layer.fps ?? 12} step={1} decimalScale={0} min={1} max={60} onChange={(v) => up((l) => void (l.fps = v), 'fps')} />
          </Field>
        )}
        <p className="hint" style={{ marginTop: 8 }}>
          {layer.trigger && layer.trigger !== 'always'
            ? t('dynamicInspector.k23')
            : layer.kind === 'video'
              ? t('dynamicInspector.k24')
              : layer.kind === 'sequence'
                ? t('dynamicInspector.k25', { v0: layer.sources?.length ?? 0 })
                : t('dynamicInspector.k26')}
        </p>
      </div>
      {layer.kind === 'text' && (
        <div className="section">
          <h3>{t('dynamicInspector.k4')}</h3>
          <Textarea autosize minRows={2} value={layer.text?.content ?? ''} onChange={(e) => {
            const t = e.currentTarget.value;
            up((l) => void (l.text = { ...l.text, content: t }), 'tx');
          }} />
          <div style={{ height: 8 }} />
          <Field label={t('dynamicInspector.k27')}>
            <Num value={layer.text?.sizePx ?? 64} step={2} decimalScale={0} min={8} onChange={(v) => up((l) => void (l.text = { ...l.text, sizePx: v }), 'tsz')} />
          </Field>
          <Field label={t('dynamicInspector.k28')}>
            <Color alpha value={layer.text?.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.text = { ...l.text, color: c }), 'tcol')} />
          </Field>
          <Field label={t('dynamicInspector.k29')}>
            <TextInput value={layer.text?.font ?? ''} placeholder={t('dynamicInspector.k30')} onChange={(e) => {
              const f = e.currentTarget.value;
              up((l) => void (l.text = { ...l.text, font: f || null }), 'tf');
            }} />
          </Field>
        </div>
      )}
      <div className="section">
        <Group justify="space-between">
          <Switch label={t('dynamicInspector.k31')} checked={layer.visible !== false} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.visible = on));
          }} />
          <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={() => removeLayer(material, layer.id!)}>
            {t('dynamicInspector.k32')}
          </Button>
        </Group>
      </div>
    </>
  );
}

function PartLayers({ partId }: { partId: string }) {
  const item = useItem();
  const doc = useStore((s) => s.doc);
  const { select, updateLayer } = useStore.getState();
  const part = item?.parts.find((p) => p.id === partId);
  if (!part) return null;
  return (
    <>
      <div className="insp-head">
        <div className="kind">{t('dynamicInspector.k33')}</div>
        <div className="title">{partLabel(part)}</div>
        <div className="meta">{t('dynamicInspector.k34')}</div>
      </div>
      {part.materials.map((mat) => {
        const layers = (materialOf(doc, mat)?.layers ?? []).filter(Boolean) as DynamicLayer[];
        return (
          <div className="section" key={mat}>
            {part.materials.length > 1 && <h3>{mat}</h3>}
            <SimpleGrid cols={4} spacing={4} mb="sm">
              {KINDS.map((k) => (
                <Button key={k.id} variant="default" px={4} leftSection={<k.icon size={13} />} onClick={() => void addLayer(mat, part.id, k.id)}>
                  {k.label}
                </Button>
              ))}
            </SimpleGrid>
            <div className="list">
              {layers.map((l) => {
                const K = KINDS.find((k) => k.id === l.kind);
                return (
                  <UnstyledButton key={l.id} className="item" onClick={() => select({ kind: 'layer', material: mat, id: l.id! })}>
                    <ActionIcon
                      variant="subtle"
                      size="sm"
                      color={l.visible === false ? 'dark.3' : 'gray'}
                      onClick={(e) => (e.stopPropagation(), updateLayer(mat, l.id!, (x) => void (x.visible = x.visible === false)))}
                    >
                      {l.visible === false ? <IconEyeOff size={14} /> : <IconEye size={14} />}
                    </ActionIcon>
                    <div style={{ minWidth: 0 }}>
                      <div>{l.name}</div>
                      <Text size="xs" c="dimmed">
                        {K?.label} · {CHANNEL_LABEL[l.channel ?? 'albedo']}
                      </Text>
                    </div>
                    {K && <K.icon size={14} />}
                  </UnstyledButton>
                );
              })}
              {!layers.length && <p className="hint">{t('dynamicInspector.k35')}</p>}
            </div>
          </div>
        );
      })}
    </>
  );
}

export function DynamicInspector() {
  const selection = useStore((s) => s.selection);
  const layer = useStore((s) => (selection?.kind === 'layer' ? materialOf(s.doc, selection.material)?.layers?.find((l) => l?.id === selection.id) : undefined));
  if (selection?.kind === 'layer' && layer) return <LayerPanel material={selection.material} layer={layer} />;
  if (selection?.kind === 'part')
    return (
      <>
        <PartLayers partId={selection.id} />
        <SelectionLayout />
      </>
    );
  return (
    <>
      <div className="insp-head">
        <div className="kind">{t('dynamicInspector.k33')}</div>
        <div className="title">{t('dynamicInspector.k36')}</div>
        <div className="meta">{t('dynamicInspector.k37')}</div>
      </div>
      <div className="section">
        <p className="hint">
          {t('dynamicInspector.k38')} <b>{t('dynamicInspector.k39')}</b>{t('dynamicInspector.k40')}
        </p>
      </div>
    </>
  );
}
