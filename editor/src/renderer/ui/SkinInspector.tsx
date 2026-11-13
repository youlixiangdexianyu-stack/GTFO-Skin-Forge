import { ActionIcon, Badge, Button, Chip, Group, Menu, Select, SimpleGrid, Switch, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { Dropzone, type FileWithPath } from '@mantine/dropzone';
import { IconArrowsMove, IconArrowsShuffle, IconBox, IconLayoutDashboard, IconCheck, IconChevronRight, IconDeviceDesktop, IconFolderOpen, IconPlayerPlay, IconPlus, IconTrash, IconX } from '@tabler/icons-react';
import { importMesh, MEMORY_FINE, MEMORY_HEAVY, mb } from '../actions';
import { ShapeItems, ShapeMenuItems } from './ShapePanel';
import { FEATURE, FeatureHead, FeatureTile, Info } from './Features';
import { InspectorTabs, type InspectorTab } from './InspectorTabs';
import { PartLayers } from './DynamicInspector';
import { PartLivery } from './LiveryPanel';
import { PartModel } from './PartModel';
import { useState, type ReactElement } from 'react';
import { MAX_SECONDS, playGroup, playSound, soundChance, soundGroupKey } from '../audio';
import { baseName, CHANNELS, fileUrl, TRIGGERS, triggersFor, type Attachment, type CatalogPart, type EnemyInfo, type SkinSound, type TextureChannel, type Trigger } from '../protocol';
import { editorOf, glowOf, materialOf, pendingKey, useItem, useStore } from '../store';
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
    <div className={`slot ${pending ? 'pending' : ''}`} role="group" aria-label={CHANNEL_LABEL[channel]}>
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
  // Shown as the game shows it (the slider reaches the game's own glow, often far above 1); an edit sets colour and
  // intensity together, so the pack never scales the game's own glow.
  const glow = glowOf(preview?.params[material], m?.params ?? {});
  return (
    <>
      <Switch mb="sm" label={t('skinInspector.k13')} checked={p.emissiveEnabled ?? true} onChange={(e) => setParams(material, { emissiveEnabled: e.currentTarget.checked })} />
      <Field label={t('skinInspector.k14')}>
        <Color rgb slot={{ kind: 'emissive', target: material }} value={glow.color} onChange={(c) => setParams(material, { emissiveColor: c, emissiveIntensity: glow.intensity }, `emc-${material}`)} />
      </Field>
      <Field label={t('skinInspector.k15')}>
        <Slider value={glow.intensity} min={0} max={Math.max(10, Math.ceil(preview?.params[material]?.emissiveIntensity ?? 0))} step={0.05} onChange={(v) => setParams(material, { emissiveColor: glow.color, emissiveIntensity: v }, `emi-${material}`)} />
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
  const { p, preview } = useParams(material);
  // Only what the game material has (the preview reads its values from the game files); the plugin ignores the rest.
  const game = preview?.params[material];
  const has = (v: unknown) => !game || v != null;
  if (game && game.metallic == null && game.smoothness == null && game.tint == null) return <p className="hint">{t('skinInspector.noParams')}</p>;
  return (
    <>
      {has(game?.metallic) && (
        <Field label={t('skinInspector.k22')}>
          <Slider value={p.metallic ?? 1} onChange={(v) => setParams(material, { metallic: v }, `met-${material}`)} />
        </Field>
      )}
      {has(game?.smoothness) && (
        <Field label={t('skinInspector.k23')}>
          <Slider value={p.smoothness ?? 1} onChange={(v) => setParams(material, { smoothness: v }, `smo-${material}`)} />
        </Field>
      )}
      {has(game?.tint) && (
        <Field label={t('skinInspector.k24')}>
          <Color slot={{ kind: 'tint', target: material }} value={p.tint ?? [1, 1, 1, 1]} onChange={(c) => setParams(material, { tint: [c[0], c[1], c[2], 1] }, `tint-${material}`)} />
        </Field>
      )}
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

async function pickSound(trigger?: Trigger, enemyId?: number | null) {
  const [f] = await window.forge.openDialog('files', [{ get name() { return t('skinInspector.k30'); }, extensions: AUDIO_EXT }]);
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
    <div className="section">
      <FeatureHead feature="sound" title={t('skinInspector.k31')} info={`${t('skinInspector.k33')} ${MAX_SECONDS} ${t('skinInspector.k34')} ${t('skinInspector.soundWeightInfo')}`}>
        <AddSoundMenu>
          <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlus size={12} />}>
            {t('skinInspector.k32')}
          </Button>
        </AddSoundMenu>
      </FeatureHead>
      {!groups.length && <p className="hint">{t('skinInspector.noSounds')}</p>}
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
    </div>
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
      <div className="insp-head">
        <UnstyledButton className="kind" onClick={() => select(null)}>
          ← {t('skinInspector.k31')}
        </UnstyledButton>
        <div className="title">{soundName(x)}</div>
        <div className="meta">{baseName(x.file ?? '')}</div>
      </div>
      <div className="section">
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
            label={(v) => `${v}%`}
            onChange={(v) => updateSound(x.id!, (s) => void (s.volume = Math.min(100, Math.max(0, v)) / 100), 'vol')}
          />
        </Field>
        <Field label={t('skinInspector.soundWeight')}>
          <Group gap={6} wrap="nowrap">
            <div style={{ flex: 1 }}>
              <Slider value={x.weight ?? 1} min={0.5} max={10} step={0.5} label={(v) => v.toFixed(1)} onChange={(v) => updateSound(x.id!, (s) => void (s.weight = Math.min(10, Math.max(0.5, v))), 'weight')} />
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
      </div>
      <div className="section">
        <FeatureHead feature="sound" title={t('skinInspector.soundGroupOf', { v0: group?.label ?? '' })} info={t('skinInspector.soundWeightInfo')}>
          {siblings.length > 1 && (
            <Button size="compact-xs" variant="subtle" color="green" leftSection={<IconArrowsShuffle size={12} />} onClick={() => playGroup(siblings)}>
              {t('skinInspector.soundPlayRandom')}
            </Button>
          )}
          <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlus size={12} />} aria-label={t('skinInspector.soundAddTo', { v0: group?.label ?? '' })} onClick={() => void pickSound(trigger, x.enemyId)}>
            {t('skinInspector.soundAdd')}
          </Button>
        </FeatureHead>
        <div className="list">
          {siblings.map((v) => (
            <SoundRow key={v.id} x={v} group={siblings} current={v.id === x.id} />
          ))}
        </div>
      </div>
      <div className="section">
        <Group justify="space-between">
          <Button variant="light" color="green" leftSection={<IconPlayerPlay size={14} />} onClick={() => playSound(x)}>
            {t('skinInspector.k35')}
          </Button>
          <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={remove}>
            {t('skinInspector.k36')}
          </Button>
        </Group>
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
