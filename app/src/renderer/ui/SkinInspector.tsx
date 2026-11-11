import { ActionIcon, Badge, Button, Chip, Collapse, Group, Select, Switch, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { Dropzone, type FileWithPath } from '@mantine/dropzone';
import { IconCheck, IconChevronDown, IconChevronRight, IconFolderOpen, IconPlayerPlay, IconPlus, IconTrash, IconX } from '@tabler/icons-react';
import { useState } from 'react';
import { MAX_SECONDS } from '../audio';
import { baseName, CHANNELS, fileUrl, triggersFor, type CatalogPart, type SkinSound, type TextureChannel, type Trigger } from '../protocol';
import { editorOf, materialOf, pendingKey, useItem, useStore } from '../store';
import { Color, Field, Slider } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { localName, partLabel, t } from '../i18n';

export const CHANNEL_LABEL: Record<TextureChannel, string> = {
  get albedo() { return t('skinInspector.k1'); },
  get normal() { return t('skinInspector.k2'); },
  get metallicSmoothness() { return t('skinInspector.k3'); },
  get emissive() { return t('skinInspector.k4'); },
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
        <Tooltip label={mine ?? t('skinInspector.k5')} openDelay={600}>
          <div className={`img ${mine ? '' : 'vanilla'}`}>
            {shown ? <img src={fileUrl(shown)} alt="" draggable={false} /> : <Text size="xs" c="dimmed">{t('skinInspector.k6')}</Text>}
          </div>
        </Tooltip>
      </Dropzone>
      <Badge className="tag" size="xs" radius="sm" variant={mine ? 'filled' : 'default'}>
        {mine ? t('skinInspector.k7') : t('skinInspector.k8')}
      </Badge>
      {mine && (
        <Tooltip label={t('skinInspector.k9')}>
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
            <span>{t('skinInspector.k10')} {Math.round(pending.confidence * 100)}%</span>
          </Tooltip>
          <Button size="compact-xs" color="yellow" variant="light" leftSection={<IconCheck size={12} />} onClick={() => confirmPending(material, channel)}>
            {t('skinInspector.k11')}
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
  if (!hasEmissive) return <p className="hint">{t('skinInspector.k12')}</p>;
  return (
    <>
      <Switch mb="sm" label={t('skinInspector.k13')} checked={p.emissiveEnabled ?? true} onChange={(e) => setParams(material, { emissiveEnabled: e.currentTarget.checked })} />
      <Field label={t('skinInspector.k14')}>
        <Color rgb value={p.emissiveColor ?? [1, 1, 1]} onChange={(c) => setParams(material, { emissiveColor: c }, `emc-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k15')}>
        <Slider value={p.emissiveIntensity ?? 1} min={0} max={10} step={0.05} onChange={(v) => setParams(material, { emissiveIntensity: v }, `emi-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k16')}>
        <Group gap={6} wrap="nowrap">
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>
            {m?.textures?.emissive ? baseName(m.textures.emissive) : t('skinInspector.k17')}
          </Text>
          <Button
            variant="default"
            onClick={async () => {
              const [f] = await window.forge.openDialog('files', [{ get name() { return t('skinInspector.k18'); }, extensions: IMAGE_EXT }]);
              if (f) setTexture(material, m?.part ?? undefined, 'emissive', f);
            }}
          >
            {t('skinInspector.k19')}
          </Button>
          {m?.textures?.emissive && (
            <Button variant="subtle" color="gray" onClick={() => setTexture(material, undefined, 'emissive', null)}>
              {t('skinInspector.k20')}
            </Button>
          )}
        </Group>
      </Field>
      <Button mt="sm" variant="default" onClick={() => copyEmissiveToAll(material)}>
        {t('skinInspector.k21')}
      </Button>
    </>
  );
}

function MaterialParams({ material }: { material: string }) {
  const { setParams } = useStore.getState();
  const { p } = useParams(material);
  return (
    <>
      <Field label={t('skinInspector.k22')}>
        <Slider value={p.metallic ?? 1} onChange={(v) => setParams(material, { metallic: v }, `met-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k23')}>
        <Slider value={p.smoothness ?? 1} onChange={(v) => setParams(material, { smoothness: v }, `smo-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k24')}>
        <Color value={p.tint ?? [1, 1, 1, 1]} onChange={(c) => setParams(material, { tint: [c[0], c[1], c[2], 1] }, `tint-${material}`)} />
      </Field>
    </>
  );
}

function PartPanel({ part }: { part: CatalogPart }) {
  const [mi, setMi] = useState(0);
  const [more, setMore] = useState(false);
  const material = part.materials[Math.min(mi, part.materials.length - 1)];
  if (!material) return <p className="hint section">{t('skinInspector.k25')}</p>;
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
          <span className="grow">{t('skinInspector.k26')}</span>
          <Tooltip label={t('skinInspector.k27')}>
            <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconFolderOpen size={12} />} onClick={pickTextureFolder}>
              {t('skinInspector.k28')}
            </Button>
          </Tooltip>
        </h3>
        <div className="slots">
          {CHANNELS.map((ch) => (
            <ChannelSlot key={ch} material={material} part={part.id} channel={ch} />
          ))}
        </div>
      </div>
      <div className="section">
        <h3>{t('skinInspector.k13')}</h3>
        <EmissiveSection material={material} />
      </div>
      <div className="section">
        <UnstyledButton w="100%" onClick={() => setMore(!more)}>
          <h3 style={{ marginBottom: 0 }}>
            <span className="grow">{t('skinInspector.k29')}</span>
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

const AUDIO_EXT = ['wav', 'mp3', 'ogg', 'flac', 'm4a', 'aac', 'opus', 'webm'];

/** Sounds played in game (locally) when an event happens. */
function Sounds() {
  const triggers = triggersFor(useItem()).filter((x) => x.value !== 'always');
  const sounds = useStore((s) => s.doc.sounds ?? []).filter((x): x is SkinSound => !!x);
  const { addSound, updateSound, removeSound } = useStore.getState();
  const pick = async () => {
    const [f] = await window.forge.openDialog('files', [{ get name() { return t('skinInspector.k30'); }, extensions: AUDIO_EXT }]);
    if (f) await addSound(f);
  };
  const play = (x: SkinSound) => {
    const a = new Audio(fileUrl(x.file ?? ''));
    a.volume = x.volume ?? 1;
    void a.play();
  };
  return (
    <div className="section">
      <h3>
        <span className="grow">{t('skinInspector.k31')}</span>
        <Button size="compact-xs" variant="subtle" leftSection={<IconPlus size={12} />} onClick={pick}>
          {t('skinInspector.k32')}
        </Button>
      </h3>
      {!sounds.length && <p className="hint">{t('skinInspector.k33')} {MAX_SECONDS} {t('skinInspector.k34')}</p>}
      {sounds.map((x) => (
        <div key={x.id} className="effect">
          <Group gap={6} wrap="nowrap">
            <Select size="xs" w={96} value={x.trigger ?? 'kill'} data={triggers} allowDeselect={false} onChange={(v) => v && updateSound(x.id!, (s) => void (s.trigger = v as Trigger))} />
            <Text size="xs" truncate style={{ flex: 1 }}>
              {x.name || baseName(x.file ?? '')}
            </Text>
            <Tooltip label={t('skinInspector.k35')}>
              <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => play(x)}>
                <IconPlayerPlay size={14} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('skinInspector.k36')}>
              <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => removeSound(x.id!)}>
                <IconTrash size={14} />
              </ActionIcon>
            </Tooltip>
          </Group>
          <Field label={t('skinInspector.k37')}>
            <Slider
              value={Math.round((x.volume ?? 1) * 100)}
              min={0}
              max={100}
              step={1}
              label={(v) => `${v}%`}
              onChange={(v) => updateSound(x.id!, (s) => void (s.volume = Math.min(100, Math.max(0, v)) / 100), 'vol')}
            />
          </Field>
          {x.trigger === 'lowAmmo' && (
            <Field label={t('skinInspector.k38')}>
              <Slider value={x.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} step={0.05} onChange={(v) => updateSound(x.id!, (s) => void (s.lowAmmoFraction = v), 'low')} />
            </Field>
          )}
        </div>
      ))}
    </div>
  );
}

/** Choosing a whole-gun texture folder: matches every file and applies all of them as one undo step. */
export async function pickTextureFolder() {
  const [dir] = await window.forge.openDialog('folder');
  if (dir) await useStore.getState().matchPaths([dir]);
}

/** Nothing selected: the start of the texture flow. */
function Overview() {
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const { confirmAllPending, select } = useStore.getState();
  const pending = Object.entries(editorOf(doc).pending);
  const replaced = (doc.materials ?? []).reduce((n, m) => n + Object.keys(m?.textures ?? {}).length, 0);
  const unmatched = useStore((s) => s.unmatched);
  return (
    <>
      <div className="insp-head">
        <div className="kind">{t('skinInspector.k39')}</div>
        <div className="title">{item ? localName(item) : t('skinInspector.k40')}</div>
        <div className="meta">{replaced ? t('skinInspector.k41', { v0: replaced }) : t('skinInspector.k42')}</div>
      </div>
      <div className="section">
        <UnstyledButton className="drop-card" onClick={pickTextureFolder}>
          <IconFolderOpen size={26} stroke={1.4} />
          <b>{t('skinInspector.k43')}</b>
          {t('skinInspector.k44')}
        </UnstyledButton>
      </div>
      {unmatched.length > 0 && (
        <div className="section">
          <h3>
            <span className="grow">{t('skinInspector.k45')} {unmatched.length}</span>
          </h3>
          <p className="hint">{t('skinInspector.k46')}</p>
          <div className="list">
            {unmatched.map((f) => (
              <Text key={f} size="xs" c="dimmed" truncate title={f}>
                {baseName(f)}
              </Text>
            ))}
          </div>
        </div>
      )}
      {pending.length > 0 && (
        <div className="section">
          <h3>
            <span className="grow" style={{ color: 'var(--warn)' }}>
              {t('skinInspector.k47')} {pending.length}
            </span>
            <Button size="compact-xs" variant="light" color="yellow" onClick={confirmAllPending}>
              {t('skinInspector.k48')}
            </Button>
          </h3>
          <div className="list">
            {pending.map(([key, cell]) => {
              const [mat, ch] = key.split('|') as [string, TextureChannel];
              const part = item?.parts.find((p) => p.materials.includes(mat));
              return (
                <UnstyledButton key={key} className="item" onClick={() => part && select({ kind: 'part', id: part.id })} style={{ gridTemplateColumns: '36px 1fr auto' }}>
                  <img src={fileUrl(cell.file ?? '')} alt="" style={{ width: 36, height: 36, objectFit: 'cover', borderRadius: 4 }} />
                  <div style={{ minWidth: 0 }}>
                    <div>
                      {part ? partLabel(part) : mat} · {CHANNEL_LABEL[ch]}
                    </div>
                    <Text size="xs" c="dimmed" truncate>
                      {baseName(cell.file ?? '')} · {Math.round(cell.confidence * 100)}%
                    </Text>
                  </div>
                  <IconChevronRight size={14} />
                </UnstyledButton>
              );
            })}
          </div>
        </div>
      )}
      <Sounds />
      <div className="section">
        <p className="hint">
          {t('skinInspector.k49')} <b>{t('skinInspector.k50')}</b>{t('skinInspector.k51')}
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
        <div className="kind">{selection?.kind === 'part' && selection.sub ? t('skinInspector.k52', { v0: partLabel(part) }) : t('skinInspector.k53')}</div>
        <div className="title">{(selection?.kind === 'part' && selection.sub?.split('/').pop()) || partLabel(part)}</div>
        <div className="meta">
          {item ? localName(item) : ''} · {part.role}
        </div>
      </div>
      <PartPanel key={part.id} part={part} />
      <SelectionLayout />
    </>
  );
}
