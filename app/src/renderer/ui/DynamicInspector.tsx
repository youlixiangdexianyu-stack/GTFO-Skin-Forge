import { ActionIcon, Button, Group, Select, SimpleGrid, Switch, Text, Textarea, TextInput, UnstyledButton } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { IconEye, IconEyeOff, IconLetterT, IconMovie, IconPhoto, IconStack2, IconTrash } from '@tabler/icons-react';
import type Konva from 'konva';
import { useEffect, useRef, useState, type ComponentType } from 'react';
import { Image as KImage, Layer, Stage, Transformer } from 'react-konva';
import { baseTexturePath, LayerSource, loadImage, naturalSort, sourceSize } from '../frames';
import { baseName, CHANNELS, type DynamicLayer, type LayerKind, type TextureChannel } from '../protocol';
import { materialOf, useItem, useStore } from '../store';
import { Color, Field, Num, Slider, Vec } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { CHANNEL_LABEL } from './SkinInspector';

const KINDS: { id: LayerKind; label: string; icon: ComponentType<{ size?: number }> }[] = [
  { id: 'image', label: '图片', icon: IconPhoto },
  { id: 'sequence', label: '序列帧', icon: IconStack2 },
  { id: 'video', label: '视频', icon: IconMovie },
  { id: 'text', label: '文字', icon: IconLetterT },
];
const IMAGES = [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'] }];
const VIDEOS = [{ name: '视频', extensions: ['mp4', 'webm', 'mov', 'm4v'] }];

async function addLayer(material: string, part: string, kind: LayerKind) {
  const { addLayer } = useStore.getState();
  if (kind === 'text') return addLayer(material, part, { kind, name: '文字', text: { content: 'GTFO', sizePx: 96, color: [1, 1, 1, 1] }, uvSize: [0.3, 0.12] });
  const files = await window.forge.openDialog('files', kind === 'video' ? VIDEOS : IMAGES);
  if (!files.length) return;
  const sources = kind === 'sequence' ? naturalSort(files) : files.slice(0, 1);
  addLayer(material, part, { kind, name: baseName(sources[0]).replace(/\.[^.]+$/, ''), sources });
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
      const f = src.at((performance.now() - t0) / 1000);
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
  }, [layer.sources, layer.kind, layer.text, layer.fps, layer.loop, layer.crop]); // eslint-disable-line react-hooks/exhaustive-deps

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
  const { updateLayer, removeLayer, select } = useStore.getState();
  const up = (fn: (l: DynamicLayer) => void, key?: string) => updateLayer(material, layer.id!, fn, key && `${layer.id}-${key}`);
  const kind = KINDS.find((k) => k.id === layer.kind);
  return (
    <>
      <div className="insp-head">
        <UnstyledButton className="kind" onClick={() => select({ kind: 'part', id: materialOf(useStore.getState().doc, material)?.part ?? '' })}>
          ← 图层 · {kind?.label}
        </UnstyledButton>
        <TextInput mt={4} size="sm" value={layer.name ?? ''} onChange={(e) => {
          const n = e.currentTarget.value;
          up((l) => void (l.name = n), 'name');
        }} />
      </div>
      <div className="section">
        <UvCanvas material={material} layer={layer} />
        <p className="hint" style={{ marginTop: 6 }}>
          拖动移动，拖边角缩放，拖上方圆点旋转。3D 视图里会同步播放。
        </p>
      </div>
      <div className="section">
        <h3>放置</h3>
        <Field label="贴到通道">
          <Select value={layer.channel ?? 'albedo'} onChange={(c) => c && up((l) => void (l.channel = c as TextureChannel))} data={CHANNELS.map((c) => ({ value: c, label: CHANNEL_LABEL[c] }))} />
        </Field>
        <Field label="UV 位置">
          <Vec value={layer.uvCenter ?? [0.5, 0.5]} labels={['U', 'V']} step={0.01} decimalScale={3} onChange={(v) => up((l) => void (l.uvCenter = v), 'uvc')} />
        </Field>
        <Field label="大小">
          <Vec value={layer.uvSize ?? [0.25, 0.25]} labels={['U', 'V']} step={0.01} decimalScale={3} min={0.001} onChange={(v) => up((l) => void (l.uvSize = v), 'uvs')} />
        </Field>
        <Field label="旋转 °">
          <Num value={layer.rotation ?? 0} step={1} decimalScale={1} onChange={(v) => up((l) => void (l.rotation = v), 'rot')} />
        </Field>
        <Field label="不透明度">
          <Slider value={layer.opacity ?? 1} onChange={(v) => up((l) => void (l.opacity = v), 'op')} />
        </Field>
        <Field label="裁剪">
          <Vec value={layer.crop ?? [0, 0, 1, 1]} labels={['X', 'Y', 'W', 'H']} step={0.01} min={0} max={1} hideControls onChange={(v) => up((l) => void (l.crop = v), 'crop')} />
        </Field>
      </div>
      {layer.kind !== 'image' && layer.kind !== 'text' && (
        <div className="section">
          <h3>播放</h3>
          <Field label="帧率">
            <Num value={layer.fps ?? 12} step={1} decimalScale={0} min={1} max={60} onChange={(v) => up((l) => void (l.fps = v), 'fps')} />
          </Field>
          <Switch mt="xs" label="循环播放" checked={layer.loop !== false} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.loop = on));
          }} />
          <p className="hint" style={{ marginTop: 8 }}>
            {layer.kind === 'video' ? '视频会按这个帧率拆成图片，最多 240 帧。' : `${layer.sources?.length ?? 0} 帧，按文件名中的数字排序。`}
          </p>
        </div>
      )}
      {layer.kind === 'text' && (
        <div className="section">
          <h3>文字</h3>
          <Textarea autosize minRows={2} value={layer.text?.content ?? ''} onChange={(e) => {
            const t = e.currentTarget.value;
            up((l) => void (l.text = { ...l.text, content: t }), 'tx');
          }} />
          <div style={{ height: 8 }} />
          <Field label="字号 px">
            <Num value={layer.text?.sizePx ?? 64} step={2} decimalScale={0} min={8} onChange={(v) => up((l) => void (l.text = { ...l.text, sizePx: v }), 'tsz')} />
          </Field>
          <Field label="颜色">
            <Color alpha value={layer.text?.color ?? [1, 1, 1, 1]} onChange={(c) => up((l) => void (l.text = { ...l.text, color: c }), 'tcol')} />
          </Field>
          <Field label="字体">
            <TextInput value={layer.text?.font ?? ''} placeholder="系统字体名，如 微软雅黑" onChange={(e) => {
              const f = e.currentTarget.value;
              up((l) => void (l.text = { ...l.text, font: f || null }), 'tf');
            }} />
          </Field>
        </div>
      )}
      <div className="section">
        <Group justify="space-between">
          <Switch label="显示" checked={layer.visible !== false} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((l) => void (l.visible = on));
          }} />
          <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={() => removeLayer(material, layer.id!)}>
            删除图层
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
        <div className="kind">动态贴图</div>
        <div className="title">{part.label}</div>
        <div className="meta">在这个部件的贴图上叠加会动的图片、视频或文字。</div>
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
              {!layers.length && <p className="hint">还没有图层。</p>}
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
        <div className="kind">动态贴图</div>
        <div className="title">先选一个部件</div>
        <div className="meta">在 3D 里点一下要加动画的部件，或在左边列表里选。</div>
      </div>
      <div className="section">
        <p className="hint">
          支持图片、按文件名排序的序列帧、MP4 视频和文字。改好后点 <b>应用到游戏</b>，帧会自动生成并打包。
        </p>
      </div>
    </>
  );
}
