import { ActionIcon, Badge, Button, Chip, Group, Menu, Select, SimpleGrid, Switch, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { Dropzone, type FileWithPath } from '@mantine/dropzone';
import { IconArrowsMove, IconBox, IconLayoutDashboard, IconCheck, IconChevronRight, IconDeviceDesktop, IconFolderOpen, IconPlayerPlay, IconPlus, IconTrash, IconX } from '@tabler/icons-react';
import { importMesh, MEMORY_FINE, MEMORY_HEAVY, mb } from '../actions';
import { ShapeItems, ShapeMenuItems } from './ShapePanel';
import { FEATURE, FeatureHead, FeatureTile, Info } from './Features';
import { InspectorTabs, type InspectorTab } from './InspectorTabs';
import { PartLayers } from './DynamicInspector';
import { PartLivery } from './LiveryPanel';
import { PartModel } from './PartModel';
import { useState, type ReactElement } from 'react';
import { MAX_SECONDS, playSound } from '../audio';
import { baseName, CHANNELS, fileUrl, triggersFor, type Attachment, type CatalogPart, type SkinSound, type TextureChannel, type Trigger } from '../protocol';
import { editorOf, materialOf, pendingKey, useItem, useStore } from '../store';
import { Color, Field, Slider } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { Variants } from './Variants';
import { PlayerColors } from './PlayerColors';
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
        <Color rgb slot={{ kind: 'emissive', target: material }} value={p.emissiveColor ?? [1, 1, 1]} onChange={(c) => setParams(material, { emissiveColor: c }, `emc-${material}`)} />
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
        <Color slot={{ kind: 'tint', target: material }} value={p.tint ?? [1, 1, 1, 1]} onChange={(c) => setParams(material, { tint: [c[0], c[1], c[2], 1] }, `tint-${material}`)} />
      </Field>
    </>
  );
}

function PartPanel({ part }: { part: CatalogPart }) {
  const [mi, setMi] = useState(0);
  const material = part.materials[Math.min(mi, part.materials.length - 1)];
  const doc = useStore((s) => s.doc);
  const sounds = useSoundsTab();
  if (!material) return <p className="hint section">{t('skinInspector.k25')}</p>;
  // Counts on the side-panel icons: what this part already has of each kind.
  const mats = part.materials.map((m) => materialOf(doc, m));
  const textures = mats.reduce((n, m) => n + Object.values(m?.textures ?? {}).filter(Boolean).length, 0);
  const liveries = mats.reduce((n, m) => n + (m?.livery ?? []).filter(Boolean).length, 0);
  const layers = mats.reduce((n, m) => n + (m?.layers ?? []).filter(Boolean).length, 0);
  const attachments = (doc.attachments ?? []).filter((a) => a?.part === part.id).length;
  const textureTab = (
    <>
      <div className="section">
        <FeatureHead feature="texture" title={t('skinInspector.k26')}>
          <Tooltip label={t('skinInspector.k27')}>
            <Button size="compact-xs" variant="subtle" leftSection={<IconFolderOpen size={12} />} onClick={pickTextureFolder}>
              {t('skinInspector.k28')}
            </Button>
          </Tooltip>
        </FeatureHead>
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
        <h3>{t('skinInspector.k29')}</h3>
        <MaterialParams material={material} />
      </div>
    </>
  );
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
      <InspectorTabs
        id="part"
        tabs={[
          { id: 'texture', label: t('tabs.texture'), icon: FEATURE.texture.icon, color: FEATURE.texture.color, count: textures, content: textureTab },
          { id: 'livery', label: t('tabs.livery'), icon: FEATURE.livery.icon, color: FEATURE.livery.color, count: liveries, content: <PartLivery part={part} /> },
          { id: 'layers', label: t('tabs.layers'), icon: FEATURE.layer.icon, color: FEATURE.layer.color, count: layers, content: <PartLayers part={part} /> },
          { id: 'attach', label: t('tabs.attach'), icon: FEATURE.attachment.icon, color: FEATURE.attachment.color, count: attachments, content: (
            <>
              <PartAttachments part={part.id} />
              <PartModel part={part.id} />
            </>
          ) },
          { id: 'place', label: t('tabs.place'), icon: IconArrowsMove, content: <SelectionLayout /> },
          sounds,
        ]}
      />
    </>
  );
}

const AUDIO_EXT = ['wav', 'mp3', 'ogg', 'flac', 'm4a', 'aac', 'opus', 'webm'];

async function pickSound(trigger?: Trigger) {
  const [f] = await window.forge.openDialog('files', [{ get name() { return t('skinInspector.k30'); }, extensions: AUDIO_EXT }]);
  if (f) await useStore.getState().addSound(f, trigger);
}

/** 弹药屏 / 导入模型, mounted on the selected part (else the first part; the inspector can move it). */
function AddAttachmentMenu({ children }: { children: ReactElement }) {
  const { addAttachment } = useStore.getState();
  return (
    <Menu position="bottom-start">
      <Menu.Target>{children}</Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconDeviceDesktop size={14} />} onClick={() => addAttachment('screen')}>
          {t('leftRail.k18')}
        </Menu.Item>
        <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void importMesh()}>
          {t('leftRail.k19')}
        </Menu.Item>
        <ShapeMenuItems />
      </Menu.Dropdown>
    </Menu>
  );
}

/** Screens and models mounted on one part, with adding right there. */
function PartAttachments({ part }: { part: string }) {
  const all = useStore((s) => s.doc.attachments);
  const list = (all ?? []).filter((a): a is Attachment => !!a && a.part === part);
  const { select } = useStore.getState();
  return (
    <div className="section">
      <FeatureHead feature="attachment" title={t('skinInspector.partAttachments')} info={t('skinInspector.attachInfo')}>
        <AddAttachmentMenu>
          <Button size="compact-xs" variant="light" color="cyan" leftSection={<IconPlus size={12} />}>
            {t('leftRail.k16')}
          </Button>
        </AddAttachmentMenu>
      </FeatureHead>
      {!list.length && <p className="hint">{t('skinInspector.noAttachments')}</p>}
      <div className="list">
        {list.map((a) => {
          const Icon = a.screen ? IconDeviceDesktop : IconBox;
          return (
            <UnstyledButton key={a.id} className="item" onClick={() => select({ kind: 'attachment', id: a.id! })}>
              <Icon size={15} color={FEATURE.attachment.color} />
              <Text size="sm" truncate>
                {a.name}
              </Text>
              <IconChevronRight size={14} />
            </UnstyledButton>
          );
        })}
      </div>
    </div>
  );
}

/** 音效: the skin's sounds are skin-wide, so every inspector's side panel offers them. */
export function useSoundsTab(): InspectorTab {
  const count = useStore((s) => (s.doc.sounds ?? []).filter(Boolean).length);
  return { id: 'sounds', label: t('tabs.sounds'), icon: FEATURE.sound.icon, color: FEATURE.sound.color, count, content: <Sounds /> };
}

/** The skin's sounds as a list; clicking one opens it. */
function Sounds() {
  const sounds = useStore((s) => s.doc.sounds ?? []).filter((x): x is SkinSound => !!x);
  const triggers = triggersFor(useItem());
  const { select } = useStore.getState();
  return (
    <div className="section">
      <FeatureHead feature="sound" title={t('skinInspector.k31')} info={`${t('skinInspector.k33')} ${MAX_SECONDS} ${t('skinInspector.k34')}`}>
        <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlus size={12} />} onClick={() => void pickSound()}>
          {t('skinInspector.k32')}
        </Button>
      </FeatureHead>
      {!sounds.length && <p className="hint">{t('skinInspector.noSounds')}</p>}
      <div className="list">
        {sounds.map((x) => (
          <UnstyledButton key={x.id} className="item" onClick={() => select({ kind: 'sound', id: x.id! })}>
            <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.k35')} onClick={(e) => (e.stopPropagation(), playSound(x))}>
              <IconPlayerPlay size={14} />
            </ActionIcon>
            <div style={{ minWidth: 0 }}>
              <Text size="sm" truncate>
                {x.name || baseName(x.file ?? '')}
              </Text>
              <Text size="xs" c="dimmed">
                {triggers.find((g) => g.value === (x.trigger ?? 'kill'))?.label}
              </Text>
            </div>
            <IconChevronRight size={14} />
          </UnstyledButton>
        ))}
      </div>
    </div>
  );
}

/** One sound: all its settings inline, so a group is edited without switching between sounds. */
export function SoundInspector() {
  const id = useStore((s) => (s.selection?.kind === 'sound' ? s.selection.id : null));
  const x = useStore((s) => s.doc.sounds?.find((v) => v?.id === id));
  const all = useStore((s) => s.doc.sounds);
  const triggers = triggersFor(useItem()).filter((g) => g.value !== 'always');
  const { updateSound, removeSound, select, edit } = useStore.getState();
  if (!x) return null;
  // One sound of a trigger's group plays per firing: its share of the group's weights, shown with the weight.
  const trigger = x.trigger ?? 'kill';
  const siblings = (all ?? []).filter((v): v is SkinSound => !!v && (v.trigger ?? 'kill') === trigger);
  const groupTotal = siblings.reduce((a, v) => a + Math.max(0.1, v.weight ?? 1), 0);
  const shareOf = (s: SkinSound) => Math.round((Math.max(0.1, s.weight ?? 1) / groupTotal) * 100);
  // The event belongs to the whole group: moving it moves every sound in it (one undo step).
  const setTrigger = (to: Trigger) => edit((d) => {
    for (const s of d.sounds ?? []) if (s && (s.trigger ?? 'kill') === trigger) s.trigger = to;
  });
  const del = (s: SkinSound) => {
    removeSound(s.id!);
    if (s.id !== x.id) return;
    const next = siblings.find((v) => v.id !== x.id);
    select(next ? { kind: 'sound', id: next.id! } : null);
  };
  return (
    <>
      <div className="insp-head">
        <UnstyledButton className="kind" onClick={() => select(null)}>
          ← {t('skinInspector.k31')}
        </UnstyledButton>
        <div className="title">{x.name || baseName(x.file ?? '')}</div>
        <div className="meta">{baseName(x.file ?? '')}</div>
      </div>
      <div className="section">
        <FeatureHead feature="sound" title={t('skinInspector.soundGroup', { v0: triggers.find((g) => g.value === trigger)?.label ?? '' })} info={t('skinInspector.soundGroupInfo')}>
          <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlus size={12} />} onClick={() => void pickSound(trigger as Trigger)}>
            {t('skinInspector.k32')}
          </Button>
        </FeatureHead>
        <Field label={t('skinInspector.soundWhen')}>
          <Select value={trigger} data={triggers} allowDeselect={false} onChange={(v) => v && setTrigger(v as Trigger)} />
        </Field>
        {siblings.map((s) => {
          const weight = Math.max(0.1, s.weight ?? 1);
          return (
            <div key={s.id} className="sound-card" aria-selected={s.id === x.id} onClick={() => select({ kind: 'sound', id: s.id! })}>
              <div className="sound-card-head">
                <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.k35')} onClick={(e) => (e.stopPropagation(), playSound(s))}>
                  <IconPlayerPlay size={14} />
                </ActionIcon>
                <Text size="sm" truncate className="grow">
                  {s.name || baseName(s.file ?? '')}
                </Text>
                <Text size="xs" c="dimmed">
                  {shareOf(s)}%
                </Text>
                <ActionIcon size="sm" variant="subtle" color="red" aria-label={t('skinInspector.k36')} onClick={(e) => (e.stopPropagation(), del(s))}>
                  <IconTrash size={14} />
                </ActionIcon>
              </div>
              <Field label={t('skinInspector.k37')}>
                <Slider
                  value={Math.round((s.volume ?? 1) * 100)}
                  min={0}
                  max={100}
                  step={1}
                  label={(v) => `${v}%`}
                  onChange={(v) => updateSound(s.id!, (o) => void (o.volume = Math.min(100, Math.max(0, v)) / 100), 'vol')}
                />
              </Field>
              <Field label={t('skinInspector.soundWeight')}>
                <Slider
                  value={weight}
                  min={0.1}
                  max={10}
                  step={0.1}
                  label={() => `${weight.toFixed(1)} · ${shareOf(s)}%`}
                  onChange={(v) => updateSound(s.id!, (o) => void (o.weight = v), 'weight')}
                />
              </Field>
              {trigger === 'lowAmmo' && (
                <Field label={t('skinInspector.k38')}>
                  <Slider value={s.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} step={0.05} onChange={(v) => updateSound(s.id!, (o) => void (o.lowAmmoFraction = v), 'low')} />
                </Field>
              )}
            </div>
          );
        })}
      </div>
    </>
  );
}

/** Choosing a whole-gun texture folder: matches every file and applies all of them as one undo step. */
export async function pickTextureFolder() {
  const [dir] = await window.forge.openDialog('folder', undefined, 'textures');
  if (dir) await useStore.getState().matchPaths([dir]);
}

/** 显存: what the skin takes in game, from its last build (an apply or an export). */
function SkinMemory() {
  const id = useStore((s) => s.doc.id);
  const memory = useStore((s) => (s.skinMemory?.id === id ? s.skinMemory?.bytes : null));
  if (memory == null) return null;
  const color = memory > MEMORY_HEAVY ? 'var(--mantine-color-red-5)' : memory > MEMORY_FINE ? 'var(--mantine-color-yellow-5)' : undefined;
  return (
    <div className="meta" style={{ color }}>
      {t('skinInspector.memory', { v0: mb(memory) })} <Info label={t('skinInspector.memoryInfo', { v0: mb(MEMORY_FINE), v1: mb(MEMORY_HEAVY) })} />
    </div>
  );
}

/** Nothing selected: the start of the texture flow. */
function Overview() {
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const { confirmAllPending, select } = useStore.getState();
  const pending = Object.entries(editorOf(doc).pending);
  const replaced = (doc.materials ?? []).reduce((n, m) => n + Object.keys(m?.textures ?? {}).length, 0);
  const unmatched = useStore((s) => s.unmatched);
  const attachments = (doc.attachments ?? []).filter(Boolean).length;
  const soundsTab = useSoundsTab();
  const sounds = soundsTab.count ?? 0;
  const layers = (doc.materials ?? []).reduce((n, m) => n + (m?.layers ?? []).filter(Boolean).length, 0);
  const liveries = (doc.materials ?? []).reduce((n, m) => n + (m?.livery ?? []).filter(Boolean).length, 0);
  const holograms = (doc.attachments ?? []).filter((a) => a?.hologram).length;
  // Texture, livery and layers live on a part: the tile asks which part, then opens it.
  const onPart = (tile: ReactElement) => (
    <Menu position="bottom-start">
      <Menu.Target>{tile}</Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t('skinInspector.pickPart')}</Menu.Label>
        {item?.parts.map((p) => (
          <Menu.Item key={p.id} onClick={() => select({ kind: 'part', id: p.id })}>
            {partLabel(p)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
  return (
    <>
      <div className="insp-head">
        <div className="kind">
          {t('skinInspector.k39')} <Info label={`${t('skinInspector.k49')} ${t('skinInspector.k50')}${t('skinInspector.k51')}`} />
        </div>
        <div className="title">{item ? localName(item) : t('skinInspector.k40')}</div>
        <div className="meta">{replaced ? t('skinInspector.k41', { v0: replaced }) : t('skinInspector.k42')}</div>
        <SkinMemory />
      </div>
      <InspectorTabs
        id="skin"
        tabs={[
          { id: 'overview', label: t('tabs.overview'), icon: IconLayoutDashboard, content: (
            <>
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
              {liveries > 0 && <Variants />}
              <PlayerColors />
              <div className="section">
                <h3>{t('skinInspector.addMore')}</h3>
                <SimpleGrid cols={2} spacing={8}>
                  {onPart(<FeatureTile feature="texture" title={t('addMenu.texture')} count={replaced} />)}
                  {onPart(<FeatureTile feature="livery" title={t('livery.title')} count={liveries} />)}
                  <AddAttachmentMenu>
                    <FeatureTile feature="attachment" title={t('skinInspector.attachTile')} count={attachments} />
                  </AddAttachmentMenu>
                  <Menu position="bottom-start">
                    <Menu.Target>
                      <FeatureTile feature="hologram" title={t('shapes.tile')} count={holograms} />
                    </Menu.Target>
                    <Menu.Dropdown>
                      <ShapeItems color={FEATURE.hologram.color} />
                    </Menu.Dropdown>
                  </Menu>
                  {onPart(<FeatureTile feature="layer" title={t('dynamicInspector.k33')} count={layers} />)}
                  <FeatureTile feature="sound" title={t('skinInspector.k31')} count={sounds} onClick={() => void pickSound()} />
                </SimpleGrid>
              </div>
            </>
          ) },
          soundsTab,
        ]}
      />
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
    </>
  );
}
