import { ActionIcon, Badge, Button, Group, Menu, Select, Switch, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { Dropzone, type FileRejection, type FileWithPath } from '@mantine/dropzone';
import {
  IconAdjustmentsHorizontal,
  IconArrowsMove,
  IconArrowsShuffle,
  IconBox,
  IconLayoutDashboard,
  IconCheck,
  IconChevronRight,
  IconComponents,
  IconCrosshair,
  IconDeviceDesktop,
  IconFolderOpen,
  IconId,
  IconInfoCircle,
  IconPalette,
  IconPlayerPlay,
  IconPlus,
  IconSun,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { importMesh, MEMORY_FINE, MEMORY_HEAVY, mb } from '../actions';
import { ShapeItems, ShapeMenuItems } from './ShapePanel';
import { Empty, FEATURE, FeatureTile, Info, InspHead, Section } from './Features';
import { InspectorTabs, showTab, type InspectorTab } from './InspectorTabs';
import { PartLayers } from './DynamicInspector';
import { PartLivery, TextureSizeField } from './LiveryPanel';
import { PartModel } from './PartModel';
import { useState, type ReactElement } from 'react';
import { MAX_SECONDS, playGroup, playSound, soundChance, soundGroupKey } from '../audio';
import {
  baseName,
  CHANNELS,
  fileUrl,
  RETICLES,
  TRIGGERS,
  triggersFor,
  type Attachment,
  type CatalogPart,
  type EnemyInfo,
  type Reticle,
  type SkinSound,
  type TextureChannel,
  type Trigger,
} from '../protocol';
import { attachmentName, editorOf, glowOf, materialOf, pendingKey, reticleOf, useItem, useStore } from '../store';
import { Color, Field, Slider } from './fields';
import { SelectionLayout } from './SelectionLayout';
import { Variants } from './Variants';
import { PlayerColors } from './PlayerColors';
import { ColorSchemes } from './ColorSchemes';
import { Palettes } from './Palettes';
import { localName, partLabel, t } from '../i18n';
import { pieceLabel } from '../pieces';

export const CHANNEL_LABEL: Record<TextureChannel, string> = {
  get albedo() {
    return t('skinInspector.k1');
  },
  get normal() {
    return t('skinInspector.k2');
  },
  get metallicSmoothness() {
    return t('skinInspector.k3');
  },
  get emissive() {
    return t('skinInspector.k4');
  },
  get reticleA() {
    return t('skinInspector.reticleLayer', { v0: 'A' });
  },
  get reticleB() {
    return t('skinInspector.reticleLayer', { v0: 'B' });
  },
  get reticleC() {
    return t('skinInspector.reticleLayer', { v0: 'C' });
  },
};

export const IMAGE_EXT = ['png', 'jpg', 'jpeg', 'tga', 'tif', 'tiff', 'bmp', 'webp'];
const IMAGE_ACCEPT = { 'image/*': IMAGE_EXT.map((x) => `.${x}`) };

/** The dropped Files that are folders: only the drag item says so (a folder's File looks like an empty file). */
export const droppedFolders = new WeakSet<File>();

/** Keep the native File objects (they carry the disk path) instead of react-dropzone's copies. While a drag is still in
 *  the air only the dragged items (their count and type) are readable: enough for a drop target to light up. */
export const nativeFiles = async (e: unknown): Promise<FileWithPath[]> => {
  const ev = e as { type?: string; dataTransfer?: DataTransfer; target?: HTMLInputElement };
  const items = [...(ev.dataTransfer?.items ?? [])].filter((i) => i.kind === 'file');
  if (ev.dataTransfer && ev.type !== 'drop') return items as unknown as FileWithPath[];
  if (!ev.dataTransfer) return [...(ev.target?.files ?? [])] as FileWithPath[];
  return items.flatMap((i) => {
    const file = i.getAsFile();
    if (file && i.webkitGetAsEntry()?.isDirectory) droppedFolders.add(file);
    return file ? [file as FileWithPath] : [];
  });
};

function ChannelSlot({ material, part, channel }: { material: string; part: string; channel: TextureChannel }) {
  const doc = useStore((s) => s.doc);
  const preview = useStore((s) => s.preview);
  const { setTexture, confirmPending } = useStore.getState();
  const mine = materialOf(doc, material)?.textures?.[channel] ?? null;
  const vanilla = preview?.textures[material]?.[channel] ?? null;
  const pending = editorOf(doc).pending[pendingKey(material, channel)];
  const shown = mine ?? vanilla;
  const broken = useStore((s) => !!mine && s.unreadable.includes(mine));
  const set = (files: File[]) => {
    if (files[0]) setTexture(material, part, channel, window.forge.pathOf(files[0]));
  };
  const reject = (r: FileRejection[]) =>
    useStore
      .getState()
      .toast('error', r.length > 1 ? t('skinInspector.dropOneOnly', { v0: CHANNEL_LABEL[channel] }) : t('skinInspector.dropNotImage', { v0: r[0]?.file.name ?? '', v1: IMAGE_EXT.join(', ') }));
  return (
    <div className={`slot ${pending ? 'pending' : ''}`} role="group" aria-label={CHANNEL_LABEL[channel]}>
      <Dropzone onDrop={set} onReject={reject} accept={IMAGE_ACCEPT} multiple={false} getFilesFromEvent={nativeFiles} dragEventsBubbling={false} p={0} radius={0} className="slot-drop">
        <Tooltip label={mine ?? t('skinInspector.k5')} openDelay={600}>
          <div className={`img ${mine ? '' : 'vanilla'}`}>
            {shown ? (
              <img src={fileUrl(shown)} alt="" draggable={false} />
            ) : (
              <Text size="xs" c="dimmed">
                {t('skinInspector.k6')}
              </Text>
            )}
          </div>
        </Tooltip>
        <Dropzone.Accept>
          <div className="slot-hint">{t('skinInspector.dropInto', { v0: CHANNEL_LABEL[channel] })}</div>
        </Dropzone.Accept>
        <Dropzone.Reject>
          <div className="slot-hint">{t('skinInspector.dropOneImage')}</div>
        </Dropzone.Reject>
      </Dropzone>
      {broken ? (
        <Tooltip label={t('skinInspector.unreadableInfo')} multiline w={240}>
          <Badge className="tag" size="xs" radius="sm" color="red" variant="filled">
            {t('skinInspector.unreadable')}
          </Badge>
        </Tooltip>
      ) : (
        <Badge className="tag" size="xs" radius="sm" variant={mine ? 'filled' : 'default'}>
          {mine ? t('skinInspector.k7') : t('skinInspector.k8')}
        </Badge>
      )}
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
            <span>
              {t('skinInspector.k10')} {Math.round(pending.confidence * 100)}%
            </span>
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
  const head = { id: 'tex-glow', title: t('skinInspector.k13'), icon: IconSun, color: 'var(--warn)' };
  if (!hasEmissive)
    return (
      <Section {...head}>
        <p className="hint">{t('skinInspector.k12')}</p>
      </Section>
    );
  // Shown as the game shows it (the slider reaches the game's own glow, often far above 1); an edit sets colour and
  // intensity together, so the pack never scales the game's own glow.
  const glow = glowOf(preview?.params[material], m?.params ?? {});
  return (
    <Section
      {...head}
      actions={<Switch size="sm" aria-label={t('skinInspector.k13')} checked={p.emissiveEnabled ?? true} onChange={(e) => setParams(material, { emissiveEnabled: e.currentTarget.checked })} />}
    >
      <Field label={t('skinInspector.k14')}>
        <Color rgb slot={{ kind: 'emissive', target: material }} value={glow.color} onChange={(c) => setParams(material, { emissiveColor: c, emissiveIntensity: glow.intensity }, `emc-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k15')}>
        <Slider
          value={glow.intensity}
          min={0}
          max={Math.max(10, Math.ceil(preview?.params[material]?.emissiveIntensity ?? 0))}
          step={0.05}
          onChange={(v) => setParams(material, { emissiveColor: glow.color, emissiveIntensity: v }, `emi-${material}`)}
        />
      </Field>
      <Field label={t('skinInspector.k16')}>
        <Group gap={6} wrap="nowrap">
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>
            {m?.textures?.emissive ? baseName(m.textures.emissive) : t('skinInspector.k17')}
          </Text>
          <Button
            variant="default"
            onClick={async () => {
              const [f] = await window.forge.openDialog('file', [
                {
                  get name() {
                    return t('skinInspector.k18');
                  },
                  extensions: IMAGE_EXT,
                },
              ]);
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
      <Button mt="sm" size="compact-sm" variant="light" color="yellow" onClick={() => copyEmissiveToAll(material)}>
        {t('skinInspector.k21')}
      </Button>
    </Section>
  );
}

function MaterialParams({ material }: { material: string }) {
  const { setParams } = useStore.getState();
  const { p, preview } = useParams(material);
  // Only what the game material has (the preview reads its values from the game files); the plugin ignores the rest.
  const game = preview?.params[material];
  const has = (v: unknown) => !game || v != null;
  const none = game && game.metallic == null && game.smoothness == null && game.tint == null;
  return (
    <Section id="tex-material" title={t('skinInspector.k29')} icon={IconAdjustmentsHorizontal} color="var(--scan)">
      {none && <p className="hint">{t('skinInspector.noParams')}</p>}
      {!none && has(game?.metallic) && (
        <Field label={t('skinInspector.k22')}>
          <Slider value={p.metallic ?? 1} onChange={(v) => setParams(material, { metallic: v }, `met-${material}`)} />
        </Field>
      )}
      {!none && has(game?.smoothness) && (
        <Field label={t('skinInspector.k23')}>
          <Slider value={p.smoothness ?? 1} onChange={(v) => setParams(material, { smoothness: v }, `smo-${material}`)} />
        </Field>
      )}
      {!none && has(game?.tint) && (
        <Field label={t('skinInspector.k24')}>
          <Color slot={{ kind: 'tint', target: material }} value={p.tint ?? [1, 1, 1, 1]} onChange={(c) => setParams(material, { tint: [c[0], c[1], c[2], 1] }, `tint-${material}`)} />
        </Field>
      )}
    </Section>
  );
}

/** A holographic sight's lens: its three reticle layers, each a picture with its colour, brightness, size and depth. */
function ReticleSection({ material, part }: { material: string; part: string }) {
  const { setReticle } = useStore.getState();
  const { m, preview } = useParams(material);
  const game = preview?.params[material]?.reticles;
  return (
    <Section id="tex-reticle" title={t('skinInspector.reticle')} icon={IconCrosshair} color="var(--warn)" info={t('skinInspector.reticleInfo')}>
      {RETICLES.filter((ch) => game?.[ch]).map((ch) => {
        const r = reticleOf(game?.[ch], m?.params?.reticles?.[ch]);
        const set = (v: Partial<Reticle>, key: string) => setReticle(material, part, ch, v, `${key}-${ch}-${material}`);
        return (
          <div key={ch} style={{ display: 'grid', gap: 6, marginBottom: 12 }}>
            <Group justify="space-between" wrap="nowrap">
              <Text size="sm" fw={600}>
                {CHANNEL_LABEL[ch]}
              </Text>
              <Switch size="sm" aria-label={t('skinInspector.reticleShown', { v0: CHANNEL_LABEL[ch] })} checked={r.visible} onChange={(e) => set({ visible: e.currentTarget.checked }, 'ret-on')} />
            </Group>
            <div className="slots">
              <ChannelSlot material={material} part={part} channel={ch} />
            </div>
            <Field label={t('skinInspector.k14')}>
              <Color rgb value={r.color} onChange={(c) => set({ color: c, intensity: r.intensity }, 'ret-c')} />
            </Field>
            <Field label={t('skinInspector.k15')}>
              <Slider value={r.intensity} min={0} max={Math.max(10, Math.ceil(game?.[ch]?.intensity ?? 0))} step={0.05} onChange={(v) => set({ color: r.color, intensity: v }, 'ret-i')} />
            </Field>
            <Field label={t('skinInspector.reticleScale')}>
              <Slider value={r.scale} min={0.1} max={Math.max(40, Math.ceil(game?.[ch]?.scale ?? 0))} step={0.05} onChange={(v) => set({ scale: v }, 'ret-s')} />
            </Field>
            <Field label={t('skinInspector.reticleDepth')}>
              <Slider value={r.offset} min={0} max={1} step={0.01} onChange={(v) => set({ offset: v }, 'ret-o')} />
            </Field>
          </div>
        );
      })}
    </Section>
  );
}

function PartPanel({ part }: { part: CatalogPart }) {
  const [mi, setMi] = useState(0);
  const material = part.materials[Math.min(mi, part.materials.length - 1)];
  const doc = useStore((s) => s.doc);
  const reticle = useStore((s) => !!material && s.preview?.params[material]?.kind === 'reticle');
  if (!material) return <p className="hint section">{t('skinInspector.k25')}</p>;
  // Counts on the side-panel icons: what this part already has of each kind.
  const mats = part.materials.map((m) => materialOf(doc, m));
  const textures = mats.reduce((n, m) => n + Object.values(m?.textures ?? {}).filter(Boolean).length, 0);
  const liveries = mats.reduce((n, m) => n + (m?.livery ?? []).filter(Boolean).length, 0);
  const layers = mats.reduce((n, m) => n + (m?.layers ?? []).filter(Boolean).length, 0);
  const attachments = (doc.attachments ?? []).filter((a) => a?.part === part.id).length;
  const textureTab = (
    <>
      {/* A part with several materials: which one this tab edits. */}
      {part.materials.length > 1 && (
        <div className="section mat-bar">
          <Field label={t('skinInspector.material')}>
            <Select aria-label={t('skinInspector.material')} value={String(mi)} onChange={(v) => v && setMi(+v)} data={part.materials.map((m, i) => ({ value: String(i), label: m }))} />
          </Field>
        </div>
      )}
      <Section
        feature="texture"
        title={t('skinInspector.k26')}
        actions={
          <Tooltip label={t('skinInspector.k27')}>
            <Button size="compact-xs" variant="light" leftSection={<IconFolderOpen size={13} />} onClick={pickTextureFolder}>
              {t('skinInspector.k28')}
            </Button>
          </Tooltip>
        }
      >
        <div className="slots">
          {CHANNELS.map((ch) => (
            <ChannelSlot key={ch} material={material} part={part.id} channel={ch} />
          ))}
        </div>
        <TextureSizeField material={material} part={part.id} />
      </Section>
      {reticle && <ReticleSection material={material} part={part.id} />}
      <EmissiveSection material={material} />
      <MaterialParams material={material} />
    </>
  );
  return (
    <>
      <InspectorTabs
        id="part"
        tabs={[
          { id: 'texture', label: t('tabs.texture'), icon: FEATURE.texture.icon, color: FEATURE.texture.color, count: textures, content: textureTab },
          { id: 'livery', label: t('tabs.livery'), icon: FEATURE.livery.icon, color: FEATURE.livery.color, count: liveries, content: <PartLivery part={part} /> },
          { id: 'layers', label: t('tabs.layers'), icon: FEATURE.layer.icon, color: FEATURE.layer.color, count: layers, content: <PartLayers part={part} /> },
          {
            id: 'attach',
            label: t('tabs.attach'),
            icon: FEATURE.attachment.icon,
            color: FEATURE.attachment.color,
            count: attachments,
            content: (
              <>
                <PartAttachments part={part.id} />
                <PartModel part={part.id} />
              </>
            ),
          },
          { id: 'place', label: t('tabs.place'), icon: IconArrowsMove, content: <SelectionLayout /> },
        ]}
      />
    </>
  );
}

const AUDIO_EXT = ['wav', 'mp3', 'ogg', 'flac', 'm4a', 'aac', 'opus', 'webm'];

async function pickSound(trigger?: Trigger, enemyId?: number | null) {
  const [f] = await window.forge.openDialog('file', [
    {
      get name() {
        return t('skinInspector.k30');
      },
      extensions: AUDIO_EXT,
    },
  ]);
  if (f) await useStore.getState().addSound(f, trigger, enemyId);
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
  const doc = useStore((s) => s.doc);
  const list = (doc.attachments ?? []).filter((a): a is Attachment => !!a && a.part === part);
  const { select } = useStore.getState();
  return (
    <Section
      feature="attachment"
      title={t('skinInspector.partAttachments')}
      info={t('skinInspector.attachInfo')}
      actions={
        <AddAttachmentMenu>
          <Button size="compact-xs" variant="light" color="cyan" leftSection={<IconPlus size={12} />}>
            {t('leftRail.k16')}
          </Button>
        </AddAttachmentMenu>
      }
    >
      {!list.length && <Empty>{t('skinInspector.noAttachments')}</Empty>}
      <div className="list">
        {list.map((a) => {
          const Icon = a.screen ? IconDeviceDesktop : IconBox;
          return (
            <UnstyledButton key={a.id} className="item" onClick={() => select({ kind: 'attachment', id: a.id! })}>
              <Icon size={15} color={FEATURE.attachment.color} />
              <Text size="sm" truncate>
                {attachmentName(doc, a)}
              </Text>
              <IconChevronRight size={14} />
            </UnstyledButton>
          );
        })}
      </div>
    </Section>
  );
}

/** 音效: the skin's sounds are skin-wide, so the whole gun's panel holds them. */
export function useSoundsTab(): InspectorTab {
  const count = useStore((s) => (s.doc.sounds ?? []).filter(Boolean).length);
  return { id: 'sounds', label: t('tabs.sounds'), icon: FEATURE.sound.icon, color: FEATURE.sound.color, count, content: <Sounds /> };
}

interface SoundGroup {
  key: string;
  trigger: Trigger;
  enemyId: number | null;
  label: string;
  sounds: SkinSound[];
}

/** An enemy's exact name: what players call it and its data block, or the id alone for an enemy this game does not have
 *  (another rundown's). */
function enemyLabel(id: number, enemies: EnemyInfo[]) {
  const e = enemies.find((x) => x.id === id);
  if (!e) return t('skinInspector.soundEnemyUnknown', { v0: id });
  const name = localName(e);
  return name === e.block ? name : `${name} · ${e.block}`;
}

/** The skin's sounds by group (one event; for a kill, one enemy), in the order the event list shows them, a kill's
 *  any-enemy group before the enemies' own. */
function useSoundGroups(): SoundGroup[] {
  const all = useStore((s) => s.doc.sounds);
  const enemies = useStore((s) => s.enemies);
  const triggers = triggersFor(useItem());
  const order = (g: SoundGroup) => (triggers.findIndex((x) => x.value === g.trigger) + 1 || 99) * 2 + (g.enemyId != null ? 1 : 0);
  const groups = new Map<string, SoundGroup>();
  for (const x of all ?? []) {
    if (!x) continue;
    const key = soundGroupKey(x);
    const trigger = x.trigger ?? 'kill';
    const enemyId = trigger === 'kill' ? (x.enemyId ?? null) : null;
    const group = groups.get(key) ?? { key, trigger, enemyId, label: soundGroupLabel(trigger, enemyId, enemies, triggers), sounds: [] };
    group.sounds.push(x);
    groups.set(key, group);
  }
  return [...groups.values()].sort((a, b) => order(a) - order(b) || a.label.localeCompare(b.label));
}

function soundGroupLabel(trigger: Trigger, enemyId: number | null, enemies: EnemyInfo[], triggers = TRIGGERS) {
  const event = triggers.find((x) => x.value === trigger)?.label ?? TRIGGERS.find((x) => x.value === trigger)?.label ?? trigger;
  return enemyId != null ? `${event} · ${enemyLabel(enemyId, enemies)}` : event;
}

const soundName = (x: SkinSound) => x.name || baseName(x.file ?? '');
const percent = (x: SkinSound, group: SkinSound[]) => `${Math.round(soundChance(x, group) * 100)}%`;

/** 添加音效: the event comes first, so a new sound joins that event's group; a kill sound's enemy is picked on the sound. */
function AddSoundMenu({ children }: { children: ReactElement }) {
  const triggers = triggersFor(useItem()).filter((x) => x.value !== 'always');
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>{children}</Menu.Target>
      <Menu.Dropdown mah={420} style={{ overflowY: 'auto' }}>
        <Menu.Label>{t('skinInspector.soundWhen')}</Menu.Label>
        {triggers.map((x) => (
          <Menu.Item key={x.value} onClick={() => void pickSound(x.value)}>
            {x.label}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}

/** One sound of a group: play it, its chance, and opening it. The play button sits beside the row, not inside it. */
function SoundRow({ x, group, current }: { x: SkinSound; group: SkinSound[]; current?: boolean }) {
  const { select } = useStore.getState();
  return (
    <div className="sound-row">
      <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.soundPlayOne', { v0: soundName(x) })} onClick={() => playSound(x)}>
        <IconPlayerPlay size={14} />
      </ActionIcon>
      <UnstyledButton className="item" aria-current={current || undefined} aria-label={t('skinInspector.soundOpen', { v0: soundName(x) })} onClick={() => select({ kind: 'sound', id: x.id! })}>
        <Text size="sm" truncate>
          {soundName(x)}
        </Text>
        {group.length > 1 && (
          <Text size="xs" c="dimmed" className="chance">
            {percent(x, group)}
          </Text>
        )}
        <IconChevronRight size={14} />
      </UnstyledButton>
    </div>
  );
}

/** A group's head: its event, how many sounds it picks from, playing one of them as the game would, adding another. */
function SoundGroupHead({ group }: { group: SoundGroup }) {
  return (
    <div className="sound-group-head">
      <Text size="sm" fw={600} truncate>
        {group.label}
      </Text>
      {group.sounds.length > 1 && (
        <Badge size="xs" variant="light" color="green">
          {t('skinInspector.soundGroupCount', { v0: group.sounds.length })}
        </Badge>
      )}
      <span className="grow" />
      {group.sounds.length > 1 && (
        <Tooltip label={t('skinInspector.soundPlayGroup', { v0: group.label })}>
          <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.soundPlayGroup', { v0: group.label })} onClick={() => playGroup(group.sounds)}>
            <IconArrowsShuffle size={14} />
          </ActionIcon>
        </Tooltip>
      )}
      <Tooltip label={t('skinInspector.soundAddTo', { v0: group.label })}>
        <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.soundAddTo', { v0: group.label })} onClick={() => void pickSound(group.trigger, group.enemyId)}>
          <IconPlus size={14} />
        </ActionIcon>
      </Tooltip>
    </div>
  );
}

/** The skin's sounds, one block per group; a row opens its sound. */
function Sounds() {
  const groups = useSoundGroups();
  return (
    <Section
      feature="sound"
      title={t('skinInspector.k31')}
      info={`${t('skinInspector.k33')} ${MAX_SECONDS} ${t('skinInspector.k34')} ${t('skinInspector.soundWeightInfo')}`}
      actions={
        <AddSoundMenu>
          <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlus size={12} />}>
            {t('skinInspector.k32')}
          </Button>
        </AddSoundMenu>
      }
    >
      {!groups.length && <Empty>{t('skinInspector.noSounds')}</Empty>}
      {groups.map((g) => (
        <div key={g.key} className="sound-group" role="group" aria-label={g.label}>
          <SoundGroupHead group={g} />
          <div className="list">
            {g.sounds.map((x) => (
              <SoundRow key={x.id} x={x} group={g.sounds} />
            ))}
          </div>
        </div>
      ))}
    </Section>
  );
}

/** One sound: when it plays (event, and for a kill which enemies), how loud, its weight in its group, and the group's
 *  other sounds right there. */
export function SoundInspector() {
  const id = useStore((s) => (s.selection?.kind === 'sound' ? s.selection.id : null));
  const x = useStore((s) => s.doc.sounds?.find((v) => v?.id === id));
  const groups = useSoundGroups();
  const enemies = useStore((s) => s.enemies);
  const triggers = triggersFor(useItem()).filter((g) => g.value !== 'always');
  const { updateSound, removeSound, setSoundEvent, select, edit } = useStore.getState();
  if (!x) return null;
  // Every enemy of the game by its exact name, searchable; an enemy the game lacks (another rundown's) stays choosable.
  const enemyOptions = [
    { value: 'any', label: t('skinInspector.soundEnemyAny') },
    ...[...enemies, ...(x.enemyId != null && !enemies.some((e) => e.id === x.enemyId) ? [{ id: x.enemyId }] : [])]
      .map((e) => ({ value: String(e.id), label: enemyLabel(e.id, enemies) }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  ];
  const group = groups.find((g) => g.key === soundGroupKey(x));
  const siblings = group?.sounds ?? [x];
  const trigger = x.trigger ?? 'kill';
  const remove = () => {
    removeSound(x.id!);
    const next = siblings.find((v) => v.id !== x.id);
    select(next ? { kind: 'sound', id: next.id! } : null);
  };
  return (
    <>
      <InspHead
        kind={t('skinInspector.k31')}
        icon={FEATURE.sound.icon}
        color={FEATURE.sound.color}
        onBack={() => select(null)}
        title={soundName(x)}
        meta={baseName(x.file ?? '')}
        actions={
          <>
            <Tooltip label={t('skinInspector.k35')}>
              <ActionIcon variant="light" color="green" aria-label={t('skinInspector.k35')} onClick={() => playSound(x)}>
                <IconPlayerPlay size={16} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('skinInspector.k36')}>
              <ActionIcon variant="subtle" color="red" aria-label={t('skinInspector.k36')} onClick={remove}>
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </>
        }
      />
      <Section title={null}>
        <Field label={t('skinInspector.soundWhen')}>
          <Select aria-label={t('skinInspector.soundWhen')} value={trigger} data={triggers} allowDeselect={false} onChange={(v) => v && setSoundEvent(x.id!, v as Trigger, x.enemyId)} />
        </Field>
        {trigger === 'kill' && (
          <Field label={t('skinInspector.soundEnemy')}>
            <Group gap={6} wrap="nowrap">
              <Select
                style={{ flex: 1 }}
                aria-label={t('skinInspector.soundEnemy')}
                value={x.enemyId != null ? String(x.enemyId) : 'any'}
                data={enemyOptions}
                searchable
                nothingFoundMessage={t('skinInspector.soundEnemyNotFound')}
                allowDeselect={false}
                renderOption={({ option }) => (
                  <div>
                    <Text size="sm">{option.label}</Text>
                    {option.value === 'any' && (
                      <Text size="xs" c="dimmed">
                        {t('skinInspector.soundEnemyAnyInfo')}
                      </Text>
                    )}
                  </div>
                )}
                onChange={(v) => v && setSoundEvent(x.id!, 'kill', v === 'any' ? null : Number(v))}
              />
              <Info label={t('skinInspector.soundEnemyInfo')} />
            </Group>
          </Field>
        )}
        <Field label={t('skinInspector.k37')}>
          <Slider
            value={Math.round((x.volume ?? 1) * 100)}
            min={0}
            max={100}
            step={1}
            unit="%"
            onChange={(v) => updateSound(x.id!, (s) => void (s.volume = Math.min(100, Math.max(0, v)) / 100), 'vol')}
          />
        </Field>
        <Field label={t('skinInspector.soundWeight')}>
          <Group gap={6} wrap="nowrap">
            <div style={{ flex: 1 }}>
              <Slider value={x.weight ?? 1} min={0.5} max={10} step={0.5} onChange={(v) => updateSound(x.id!, (s) => void (s.weight = Math.min(10, Math.max(0.5, v))), 'weight')} />
            </div>
            <Info label={t('skinInspector.soundWeightInfo')} />
          </Group>
          <Text size="xs" c="dimmed">
            {t('skinInspector.soundChance', { v0: percent(x, siblings) })}
          </Text>
        </Field>
        {trigger === 'lowAmmo' && (
          <Field label={t('skinInspector.k38')}>
            <Group gap={6} wrap="nowrap">
              <div style={{ flex: 1 }}>
                {/* One threshold per group: the plugin fires the group once, so every low-ammo sound takes the same value. */}
                <Slider
                  value={x.lowAmmoFraction ?? 0.2}
                  min={0.05}
                  max={0.9}
                  step={0.05}
                  onChange={(v) => edit((d) => d.sounds?.forEach((s) => s?.trigger === 'lowAmmo' && (s.lowAmmoFraction = v)), 'sound-low-ammo')}
                />
              </div>
              <Info label={t('skinInspector.soundLowAmmoInfo')} />
            </Group>
          </Field>
        )}
      </Section>
      <Section
        feature="sound"
        title={t('skinInspector.soundGroupOf', { v0: group?.label ?? '' })}
        info={t('skinInspector.soundWeightInfo')}
        actions={
          <>
            {siblings.length > 1 && (
              <Tooltip label={t('skinInspector.soundPlayRandom')}>
                <ActionIcon size="sm" variant="subtle" color="green" aria-label={t('skinInspector.soundPlayRandom')} onClick={() => playGroup(siblings)}>
                  <IconArrowsShuffle size={14} />
                </ActionIcon>
              </Tooltip>
            )}
            <Button
              size="compact-xs"
              variant="light"
              color="green"
              leftSection={<IconPlus size={12} />}
              aria-label={t('skinInspector.soundAddTo', { v0: group?.label ?? '' })}
              onClick={() => void pickSound(trigger, x.enemyId)}
            >
              {t('skinInspector.soundAdd')}
            </Button>
          </>
        }
      >
        <div className="list">
          {siblings.map((v) => (
            <SoundRow key={v.id} x={v} group={siblings} current={v.id === x.id} />
          ))}
        </div>
      </Section>
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
    <span style={{ color, display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      · {t('skinInspector.memory', { v0: mb(memory) })} <Info label={t('skinInspector.memoryInfo', { v0: mb(MEMORY_FINE), v1: mb(MEMORY_HEAVY) })} />
    </span>
  );
}

/** The first part (in the gear's order) that has something of a kind, to open on its tab. */
function firstPart(has: (part: CatalogPart) => boolean): CatalogPart | undefined {
  return useStore
    .getState()
    .catalog.find((c) => c.id === useStore.getState().weaponId)
    ?.parts.find(has);
}

/** 整把枪 · 外观: the texture folder, matches to confirm, colour schemes, player colours and the other colour tools. */
function GunLook({ liveries }: { liveries: number }) {
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const { confirmAllPending, select } = useStore.getState();
  const pending = Object.entries(editorOf(doc).pending);
  const unmatched = useStore((s) => s.unmatched);
  return (
    <>
      <div className="section">
        <Tooltip label={t('skinInspector.k44')} multiline w={280}>
          <UnstyledButton className="drop-card" onClick={pickTextureFolder}>
            <IconFolderOpen size={24} stroke={1.5} />
            <b>{t('skinInspector.k43')}</b>
          </UnstyledButton>
        </Tooltip>
      </div>
      {unmatched.length > 0 && (
        <Section title={`${t('skinInspector.k45')} ${unmatched.length}`} icon={IconX} color="var(--bad)" info={t('skinInspector.k46')}>
          <div className="list">
            {unmatched.map((f) => (
              <Text key={f} size="xs" c="dimmed" truncate title={f}>
                {baseName(f)}
              </Text>
            ))}
          </div>
        </Section>
      )}
      {pending.length > 0 && (
        <Section
          title={`${t('skinInspector.k47')} ${pending.length}`}
          icon={IconCheck}
          color="var(--warn)"
          actions={
            <Button size="compact-xs" variant="light" color="yellow" onClick={confirmAllPending}>
              {t('skinInspector.k48')}
            </Button>
          }
        >
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
        </Section>
      )}
      <ColorSchemes />
      <PlayerColors />
      {liveries > 0 && <Variants />}
      <Palettes />
    </>
  );
}

/** 整把枪 · 信息: the skin's name and author, what it has (a tile opens the first part with it on that tab, or adds one), and
 * what the game keeps for parts and channels the skin leaves out. */
function GunInfo({ counts }: { counts: Record<'texture' | 'livery' | 'attachment' | 'hologram' | 'layer' | 'sound', number> }) {
  const name = useStore((s) => s.doc.name);
  const author = useStore((s) => s.doc.author);
  const allowMissing = useStore((s) => s.prefs.allowMissing);
  const { edit, select, setPrefs } = useStore.getState();
  const item = useItem();
  const doc = useStore((s) => s.doc);
  const openPart = (part: CatalogPart | undefined, tab: string) => part && (select({ kind: 'part', id: part.id }), showTab('part', tab));
  const mats = (part: CatalogPart) => part.materials.map((m) => materialOf(doc, m));
  const hasAttachment = (part: CatalogPart, hologram: boolean) => (doc.attachments ?? []).some((a) => a?.part === part.id && !!a.hologram === hologram);
  // Texture, livery and layers live on a part: with none yet, the tile asks which part, then opens it.
  const onPart = (tile: ReactElement, tab: string, has: (part: CatalogPart) => boolean, count: number) =>
    count > 0 ? (
      <span onClick={() => openPart(firstPart(has), tab)}>{tile}</span>
    ) : (
      <Menu position="bottom-start">
        <Menu.Target>{tile}</Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{t('skinInspector.pickPart')}</Menu.Label>
          {item?.parts.map((p) => (
            <Menu.Item key={p.id} onClick={() => openPart(p, tab)}>
              {partLabel(p)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
    );
  return (
    <>
      <Section title={t('skinInspector.infoTitle')} icon={IconId} color="var(--flare)">
        <Field label={t('skinInspector.skinName')}>
          <TextInput size="xs" aria-label={t('skinInspector.skinName')} value={name ?? ''} onChange={(e) => edit((d) => void (d.name = e.currentTarget.value), 'skin-name')} />
        </Field>
        <Field label={t('skinInspector.author')}>
          <TextInput size="xs" aria-label={t('skinInspector.author')} value={author ?? ''} onChange={(e) => edit((d) => void (d.author = e.currentTarget.value), 'skin-author')} />
        </Field>
      </Section>
      <Section title={t('skinInspector.overview')} icon={IconPlus} color="var(--flare)" info={t('skinInspector.tilesInfo')}>
        <div className="tiles">
          {onPart(
            <FeatureTile feature="texture" title={t('addMenu.texture')} count={counts.texture} />,
            'texture',
            (p) => mats(p).some((m) => Object.values(m?.textures ?? {}).some(Boolean)),
            counts.texture,
          )}
          {onPart(<FeatureTile feature="livery" title={t('livery.title')} count={counts.livery} />, 'livery', (p) => mats(p).some((m) => (m?.livery ?? []).some(Boolean)), counts.livery)}
          {counts.attachment > 0 ? (
            <FeatureTile
              feature="attachment"
              title={t('skinInspector.attachTile')}
              count={counts.attachment}
              onClick={() =>
                openPart(
                  firstPart((p) => hasAttachment(p, false)),
                  'attach',
                )
              }
            />
          ) : (
            <AddAttachmentMenu>
              <FeatureTile feature="attachment" title={t('skinInspector.attachTile')} count={0} />
            </AddAttachmentMenu>
          )}
          {counts.hologram > 0 ? (
            <FeatureTile
              feature="hologram"
              title={t('shapes.tile')}
              count={counts.hologram}
              onClick={() =>
                openPart(
                  firstPart((p) => hasAttachment(p, true)),
                  'attach',
                )
              }
            />
          ) : (
            <Menu position="bottom-start">
              <Menu.Target>
                <FeatureTile feature="hologram" title={t('shapes.tile')} count={0} />
              </Menu.Target>
              <Menu.Dropdown>
                <ShapeItems color={FEATURE.hologram.color} />
              </Menu.Dropdown>
            </Menu>
          )}
          {onPart(<FeatureTile feature="layer" title={t('dynamicInspector.k33')} count={counts.layer} />, 'layers', (p) => mats(p).some((m) => (m?.layers ?? []).some(Boolean)), counts.layer)}
          <FeatureTile feature="sound" title={t('skinInspector.k31')} count={counts.sound} onClick={() => (counts.sound ? showTab('skin', 'sounds') : void pickSound())} />
        </div>
      </Section>
      <Section title={t('skinInspector.missingTitle')} icon={IconAdjustmentsHorizontal} color="var(--dust)">
        <Group gap={6} wrap="nowrap">
          <Switch size="sm" label={t('topBar.k53')} checked={allowMissing} onChange={(e) => setPrefs({ allowMissing: e.currentTarget.checked })} style={{ flex: 1 }} />
          <Info label={t('skinInspector.missingInfo')} />
        </Group>
      </Section>
    </>
  );
}

/** 整把枪: what the whole gun has, on three tabs: its look, its sounds, its information. */
function Overview() {
  const doc = useStore((s) => s.doc);
  const item = useItem();
  const replaced = (doc.materials ?? []).reduce((n, m) => n + Object.keys(m?.textures ?? {}).length, 0);
  const soundsTab = useSoundsTab();
  const counts = {
    texture: replaced,
    livery: (doc.materials ?? []).reduce((n, m) => n + (m?.livery ?? []).filter(Boolean).length, 0),
    attachment: (doc.attachments ?? []).filter((a) => a && !a.hologram).length,
    hologram: (doc.attachments ?? []).filter((a) => a?.hologram).length,
    layer: (doc.materials ?? []).reduce((n, m) => n + (m?.layers ?? []).filter(Boolean).length, 0),
    sound: soundsTab.count ?? 0,
  };
  return (
    <>
      <InspHead
        kind={t('skinInspector.wholeGun')}
        icon={IconLayoutDashboard}
        color="var(--flare)"
        title={item ? localName(item) : t('skinInspector.k40')}
        meta={
          <>
            {replaced ? t('skinInspector.k41', { v0: replaced }) : t('skinInspector.k42')}
            <SkinMemory />
          </>
        }
        actions={<Info label={`${t('skinInspector.k49')} ${t('skinInspector.k50')}${t('skinInspector.k51')}`} />}
      />
      <InspectorTabs
        id="skin"
        tabs={[
          { id: 'look', label: t('tabs.look'), icon: IconPalette, color: 'var(--paint)', content: <GunLook liveries={counts.livery} /> },
          soundsTab,
          { id: 'info', label: t('tabs.info'), icon: IconInfoCircle, content: <GunInfo counts={counts} /> },
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
      <InspHead
        kind={selection?.kind === 'part' && selection.sub ? t('skinInspector.k52', { v0: partLabel(part) }) : t('skinInspector.k53')}
        icon={IconComponents}
        color="var(--flare)"
        title={selection?.kind === 'part' && selection.sub ? pieceLabel(selection.sub) : partLabel(part)}
        meta={`${item ? localName(item) : ''} · ${part.role}`}
      />
      <PartPanel key={part.id} part={part} />
    </>
  );
}
