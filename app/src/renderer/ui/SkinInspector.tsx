import { ActionIcon, Badge, Button, Chip, Collapse, Group, Switch, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { Dropzone, type FileWithPath } from '@mantine/dropzone';
import { IconCheck, IconChevronDown, IconChevronRight, IconFolderOpen, IconX } from '@tabler/icons-react';
import { useState } from 'react';
import { baseName, CHANNELS, fileUrl, type CatalogPart, type TextureChannel } from '../protocol';
import { editorOf, materialOf, pendingKey, useItem, useStore } from '../store';
import { Color, Field, Slider } from './fields';
import { SelectionLayout } from './SelectionLayout';

export const CHANNEL_LABEL: Record<TextureChannel, string> = {
  albedo: '基础色',
  normal: '法线',
  metallicSmoothness: '金属/光滑',
  emissive: '自发光',
};

export const IMAGE_EXT = ['png', 'jpg', 'jpeg', 'tga', 'tif', 'tiff', 'bmp', 'webp'];
const IMAGE_ACCEPT = { 'image/*': IMAGE_EXT.map((x) => `.${x}`) };

/** Keep the native File objects (they carry the disk path) instead of react-dropzone's copies. */
export const nativeFiles = async (e: unknown): Promise<FileWithPath[]> => {
  const ev = e as { dataTransfer?: DataTransfer; target?: HTMLInputElement };
  return [...(ev.dataTransfer?.files ?? ev.target?.files ?? [])] as FileWithPath[];
};

function ChannelSlot({ material, part, channel }: { material: string; part: string; channel: TextureChannel }) {
  const doc = useStore((s) => s.doc);
  const preview = useStore((s) => s.preview);
  const { setTexture, confirmPending } = useStore.getState();
  const mine = materialOf(doc, material)?.textures?.[channel] ?? null;
  const vanilla = preview?.textures[material]?.[channel] ?? null;
  const pending = editorOf(doc).pending[pendingKey(material, channel)];
  const shown = mine ?? vanilla;
  const set = (files: File[]) => {
    if (files[0]) setTexture(material, part, channel, window.forge.pathOf(files[0]));
  };
  return (
    <div className={`slot ${pending ? 'pending' : ''}`}>
      <Dropzone onDrop={set} accept={IMAGE_ACCEPT} multiple={false} getFilesFromEvent={nativeFiles} dragEventsBubbling={false} p={0} radius={0} className="slot-drop">
        <Tooltip label={mine ?? '原版贴图。点击或拖入图片替换'} openDelay={600}>
          <div className={`img ${mine ? '' : 'vanilla'}`}>
            {shown ? <img src={fileUrl(shown)} alt="" draggable={false} /> : <Text size="xs" c="dimmed">无</Text>}
          </div>
        </Tooltip>
      </Dropzone>
      <Badge className="tag" size="xs" radius="sm" variant={mine ? 'filled' : 'default'}>
        {mine ? '已替换' : '原版'}
      </Badge>
      {mine && (
        <Tooltip label="恢复原版">
          <ActionIcon className="x" size="sm" variant="filled" color="dark" onClick={() => setTexture(material, part, channel, null)}>
            <IconX size={12} />
          </ActionIcon>
        </Tooltip>
      )}
      <div className="cap">
        <span className="name">
          {CHANNEL_LABEL[channel]}
          {mine && <span className="muted"> · {baseName(mine)}</span>}
        </span>
      </div>
      {pending && (
        <div className="confirm">
          <Tooltip label={pending.reason}>
            <span>不太确定 {Math.round(pending.confidence * 100)}%</span>
          </Tooltip>
          <Button size="compact-xs" color="yellow" variant="light" leftSection={<IconCheck size={12} />} onClick={() => confirmPending(material, channel)}>
            没错
          </Button>
        </div>
      )}
    </div>
  );
}

function useParams(material: string) {
  const doc = useStore((s) => s.doc);
  const preview = useStore((s) => s.preview);
  const m = materialOf(doc, material);
  const own = Object.fromEntries(Object.entries(m?.params ?? {}).filter(([, v]) => v != null));
  return { m, preview, p: { ...(preview?.params[material] ?? {}), ...own } };
}

function EmissiveSection({ material }: { material: string }) {
  const { setParams, copyEmissiveToAll, setTexture } = useStore.getState();
  const { m, p, preview } = useParams(material);
  const hasEmissive = !!(m?.textures?.emissive ?? preview?.textures[material]?.emissive);
  if (!hasEmissive) return <p className="hint">这个部件没有自发光贴图。放入一张自发光贴图后，这里可以调发光颜色和强度。</p>;
  return (
    <>
      <Switch mb="sm" label="发光" checked={p.emissiveEnabled ?? true} onChange={(e) => setParams(material, { emissiveEnabled: e.currentTarget.checked })} />
      <Field label="颜色">
        <Color rgb value={p.emissiveColor ?? [1, 1, 1]} onChange={(c) => setParams(material, { emissiveColor: c }, `emc-${material}`)} />
      </Field>
      <Field label="强度">
        <Slider value={p.emissiveIntensity ?? 1} min={0} max={10} step={0.05} onChange={(v) => setParams(material, { emissiveIntensity: v }, `emi-${material}`)} />
      </Field>
      <Field label="发光遮罩">
        <Group gap={6} wrap="nowrap">
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>
            {m?.textures?.emissive ? baseName(m.textures.emissive) : '使用原版自发光'}
          </Text>
          <Button
            variant="default"
            onClick={async () => {
              const [f] = await window.forge.openDialog('files', [{ name: '图片', extensions: IMAGE_EXT }]);
              if (f) setTexture(material, m?.part ?? undefined, 'emissive', f);
            }}
          >
            选择
          </Button>
          {m?.textures?.emissive && (
            <Button variant="subtle" color="gray" onClick={() => setTexture(material, undefined, 'emissive', null)}>
              清除
            </Button>
          )}
        </Group>
      </Field>
      <Button mt="sm" variant="default" onClick={() => copyEmissiveToAll(material)}>
        应用到全部部件
      </Button>
    </>
  );
}

function MaterialParams({ material }: { material: string }) {
  const { setParams } = useStore.getState();
  const { p } = useParams(material);
  return (
    <>
      <Field label="金属度">
        <Slider value={p.metallic ?? 1} onChange={(v) => setParams(material, { metallic: v }, `met-${material}`)} />
      </Field>
      <Field label="光滑度">
        <Slider value={p.smoothness ?? 1} onChange={(v) => setParams(material, { smoothness: v }, `smo-${material}`)} />
      </Field>
      <Field label="颜色叠加">
        <Color value={p.tint ?? [1, 1, 1, 1]} onChange={(c) => setParams(material, { tint: [c[0], c[1], c[2], 1] }, `tint-${material}`)} />
      </Field>
    </>
  );
}

function PartPanel({ part }: { part: CatalogPart }) {
  const [mi, setMi] = useState(0);
  const [more, setMore] = useState(false);
  const material = part.materials[Math.min(mi, part.materials.length - 1)];
  if (!material) return <p className="hint section">这个部件没有可以替换的贴图。</p>;
  return (
    <>
      {part.materials.length > 1 && (
        <div className="section" style={{ paddingBottom: 10 }}>
          <Chip.Group value={String(mi)} onChange={(v) => setMi(+v)}>
            <Group gap={4}>
              {part.materials.map((m, i) => (
                <Chip key={m} value={String(i)} size="xs" variant="outline">
                  {m}
                </Chip>
              ))}
            </Group>
          </Chip.Group>
        </div>
      )}
      <div className="section">
        <h3>
          <span className="grow">贴图</span>
          <Text span size="xs" c="dimmed">
            点击或拖入图片
          </Text>
        </h3>
        <div className="slots">
          {CHANNELS.map((ch) => (
            <ChannelSlot key={ch} material={material} part={part.id} channel={ch} />
          ))}
        </div>
      </div>
      <div className="section">
        <h3>发光</h3>
        <EmissiveSection material={material} />
      </div>
      <div className="section">
        <UnstyledButton w="100%" onClick={() => setMore(!more)}>
          <h3 style={{ marginBottom: 0 }}>
            <span className="grow">材质参数</span>
            {more ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
          </h3>
        </UnstyledButton>
        <Collapse in={more}>
          <div style={{ paddingTop: 10 }}>
            <MaterialParams material={material} />
          </div>
        </Collapse>
      </div>
    </>
  );
}

/** Nothing selected: the start of the texture flow. */
function Overview() {
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const { matchPaths, confirmAllPending, select } = useStore.getState();
  const pending = Object.entries(editorOf(doc).pending);
  const replaced = (doc.materials ?? []).reduce((n, m) => n + Object.keys(m?.textures ?? {}).length, 0);
  const pick = async () => {
    const [dir] = await window.forge.openDialog('folder');
    if (dir) await matchPaths([dir]);
  };
  return (
    <>
      <div className="insp-head">
        <div className="kind">换皮</div>
        <div className="title">{item ? item.nameZh || item.name : '选一把武器'}</div>
        <div className="meta">{replaced ? `已替换 ${replaced} 张贴图` : '还没有替换贴图'}</div>
      </div>
      <div className="section">
        <UnstyledButton className="drop-card" onClick={pick}>
          <IconFolderOpen size={26} stroke={1.4} />
          <b>拖入贴图文件夹</b>
          把 Substance Painter 导出的整个文件夹拖进窗口，或点这里选择。会自动认出每张图属于哪个部件。
        </UnstyledButton>
      </div>
      {pending.length > 0 && (
        <div className="section">
          <h3>
            <span className="grow" style={{ color: 'var(--warn)' }}>
              需要确认 · {pending.length}
            </span>
            <Button size="compact-xs" variant="light" color="yellow" onClick={confirmAllPending}>
              全部没错
            </Button>
          </h3>
          <div className="list">
            {pending.map(([key, cell]) => {
              const [mat, ch] = key.split('|') as [string, TextureChannel];
              const part = item?.parts.find((p) => p.materials.includes(mat));
              return (
                <UnstyledButton key={key} className="item" onClick={() => part && select({ kind: 'part', id: part.id })} style={{ gridTemplateColumns: '36px 1fr auto' }}>
                  <img src={fileUrl(cell.file)} alt="" style={{ width: 36, height: 36, objectFit: 'cover', borderRadius: 4 }} />
                  <div style={{ minWidth: 0 }}>
                    <div>
                      {part?.label ?? mat} · {CHANNEL_LABEL[ch]}
                    </div>
                    <Text size="xs" c="dimmed" truncate>
                      {baseName(cell.file)} · {Math.round(cell.confidence * 100)}%
                    </Text>
                  </div>
                  <IconChevronRight size={14} />
                </UnstyledButton>
              );
            })}
          </div>
        </div>
      )}
      <div className="section">
        <p className="hint">
          点 3D 里的部件或左边的部件列表，可以单独换某一张贴图、调整发光。改完点右上角 <b>应用到游戏</b>，游戏开着也会自动刷新。
        </p>
      </div>
    </>
  );
}

export function SkinInspector() {
  const selection = useStore((s) => s.selection);
  const item = useItem();
  const part = selection?.kind === 'part' ? item?.parts.find((p) => p.id === selection.id) : undefined;
  if (!part) return <Overview />;
  return (
    <>
      <div className="insp-head">
        <div className="kind">{selection?.kind === 'part' && selection.sub ? `部件 · ${part.label}` : '部件'}</div>
        <div className="title">{(selection?.kind === 'part' && selection.sub) || part.label}</div>
        <div className="meta">
          {item?.nameZh || item?.name} · {part.role}
        </div>
      </div>
      <PartPanel key={part.id} part={part} />
      <SelectionLayout />
    </>
  );
}
