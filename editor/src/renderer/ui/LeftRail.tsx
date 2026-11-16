import { ActionIcon, Badge, Checkbox, CloseButton, ScrollArea, TextInput, Tooltip } from '@mantine/core';
import {
  IconArrowBackUp,
  IconBox,
  IconChevronRight,
  IconCopy,
  IconCube,
  IconDeviceDesktop,
  IconEye,
  IconEyeOff,
  IconListTree,
  IconMovie,
  IconPaperclip,
  IconPencil,
  IconPlus,
  IconSearch,
  IconSphere,
  IconTrash,
  type Icon as TablerIcon,
} from '@tabler/icons-react';
import { useState } from 'react';
import { attachedOf, attachmentMaterial, type Attachment, CHANNELS, type CodeEffect, type DynamicLayer } from '../protocol';
import { attachmentName, editorOf, materialOf, partTransformOf, pendingKey, useItem, useStore, sameSelection, type Selection } from '../store';
import { AttachmentAddMenu, PartAddMenu } from './AddMenu';
import { kindOf } from './LiveryPanel';
import { layersOf, liveryName, updateLivery } from '../livery';
import { layerName } from '../frames';
import { updateCodeEffect } from '../codeFx';
import { FEATURE, Info } from './Features';
import { hiddenMeshes, partTarget, useSceneVersion } from '../viewport/refs';
import { focusSelection } from '../viewport/Viewport';
import { partLabel, t } from '../i18n';
import { pieceChanged, pieceLabel } from '../pieces';

/** A row's hover: the pointer over it shows the thing in the 3D view, and the pointer over the thing in the 3D view marks
 * the row (store `hover`). */
function useHover(target: NonNullable<Selection>) {
  const on = useStore((s) => sameSelection(s.hover, target));
  const { setHover } = useStore.getState();
  return { 'data-hover': on || undefined, onMouseEnter: () => setHover(target), onMouseLeave: () => setHover(null) };
}

/** A row's eye: shown on hover, and always while the thing is hidden. */
function Eye({ visible, onToggle, label, disabled }: { visible: boolean; onToggle: () => void; label?: string; disabled?: boolean }) {
  const text = label ?? (visible ? t('leftRail.k10') : t('leftRail.k11'));
  return (
    <Tooltip label={text}>
      <ActionIcon
        variant="subtle"
        color={visible ? 'gray' : 'flare'}
        size="sm"
        aria-label={text}
        data-keep={!visible || undefined}
        data-disabled={disabled || undefined}
        onClick={(e) => (e.stopPropagation(), disabled || onToggle())}
      >
        {visible ? <IconEye size={14} /> : <IconEyeOff size={14} />}
      </ActionIcon>
    </Tooltip>
  );
}

function EyeToggle({ part, sub }: { part: string; sub?: string | null }) {
  const visible = useStore((s) => partTransformOf(s.doc, part, sub)?.visible !== false);
  useSceneVersion();
  // Hiding a node hides its own part's meshes only (not a part mounted on it); a mount point alone has nothing to hide.
  const obj = partTarget(part, sub);
  const empty = visible && !!obj && !hiddenMeshes(part, obj).length;
  return (
    <Eye
      visible={visible}
      disabled={empty}
      label={empty ? t('leftRail.nothingToHide') : undefined}
      onToggle={() => useStore.getState().setPartTransform(part, sub, (t) => void (t.visible = visible ? false : null))}
    />
  );
}

function AttachmentRow({ a, depth }: { a: Attachment; depth: number }) {
  const selected = useStore((s) => s.selection?.kind === 'attachment' && s.selection.id === a.id);
  const name = useStore((s) => attachmentName(s.doc, a));
  const [renaming, setRenaming] = useState(false);
  const { select, updateAttachment, duplicateAttachment, removeAttachment } = useStore.getState();
  const visible = a.visible !== false;
  const stop = (fn: () => void) => (e: React.MouseEvent) => (e.stopPropagation(), fn());
  const Icon = a.screen ? IconDeviceDesktop : a.charm ? IconPaperclip : a.hologram ? IconSphere : IconBox;
  const hover = useHover({ kind: 'attachment', id: a.id! });
  const material = attachmentMaterial(a.id!);
  const paint = useStore((s) => materialOf(s.doc, material));
  return (
    <>
      <div
        {...hover}
        className={`part child attach ${visible ? '' : 'off'}`}
        style={{ ['--depth' as string]: depth, ['--accent' as string]: a.hologram ? FEATURE.hologram.color : FEATURE.attachment.color }}
        role="option"
        aria-selected={selected}
        onClick={() => {
          select({ kind: 'attachment', id: a.id! });
          focusSelection();
        }}
      >
        <Icon size={14} className="kind-icon" />
        {renaming ? (
          <TextInput
            size="xs"
            autoFocus
            defaultValue={name}
            style={{ flex: 1 }}
            onClick={(e) => e.stopPropagation()}
            onBlur={(e) => {
              const typed = e.currentTarget.value.trim();
              if (typed && typed !== name) updateAttachment(a.id!, (x) => void (x.name = typed));
              setRenaming(false);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') e.currentTarget.blur();
              if (e.key === 'Escape') setRenaming(false);
            }}
          />
        ) : (
          <span className="label" title={t('leftRail.k12')} onDoubleClick={stop(() => setRenaming(true))}>
            {name}
          </span>
        )}
        <span className="row-actions">
          <Tooltip label={t('leftRail.k13')}>
            <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('leftRail.k13')} onClick={stop(() => setRenaming(true))}>
              <IconPencil size={13} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('leftRail.k14')}>
            <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('leftRail.k14')} onClick={stop(() => duplicateAttachment(a.id!))}>
              <IconCopy size={13} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('leftRail.k15')}>
            <ActionIcon variant="subtle" color="red" size="sm" aria-label={t('leftRail.k15')} onClick={stop(() => removeAttachment(a.id!))}>
              <IconTrash size={13} />
            </ActionIcon>
          </Tooltip>
          <Eye visible={visible} onToggle={() => updateAttachment(a.id!, (x) => void (x.visible = !visible))} />
          {a.mesh && (
            <AttachmentAddMenu id={a.id!}>
              <ActionIcon className="row-add" variant="subtle" size="sm" aria-label={t('addMenu.title')} onClick={(e) => e.stopPropagation()}>
                <IconPlus size={14} />
              </ActionIcon>
            </AttachmentAddMenu>
          )}
        </span>
      </div>
      {/* The paint and animated layers on its model, as under a part. */}
      {[...layersOf(paint)].reverse().map((l) => (
        <ChildRow
          key={l.id}
          icon={kindOf(l.kind).icon}
          color={FEATURE.livery.color}
          name={liveryName(l)}
          visible={l.visible !== false}
          onToggle={() => updateLivery(material, l.id!, (x) => void (x.visible = x.visible === false))}
          target={{ kind: 'livery', material, id: l.id! }}
        />
      ))}
      {((paint?.layers ?? []).filter(Boolean) as DynamicLayer[]).map((l) => (
        <ChildRow
          key={l.id}
          icon={IconMovie}
          color={FEATURE.layer.color}
          name={layerName(l)}
          visible={l.visible !== false}
          onToggle={() => useStore.getState().updateLayer(material, l.id!, (x) => void (x.visible = x.visible === false))}
          target={{ kind: 'layer', material, id: l.id! }}
        />
      ))}
    </>
  );
}

/** A paint layer, animated layer or code effect under its part: its kind's icon, its name and its eye. */
function ChildRow({
  icon: Icon,
  color,
  name,
  visible,
  onToggle,
  target,
}: {
  icon: TablerIcon;
  color: string;
  name?: string | null;
  visible: boolean;
  onToggle: () => void;
  target: NonNullable<Selection>;
}) {
  const selected = useStore((s) => sameSelection(s.selection, target));
  const hover = useHover(target);
  return (
    <div
      {...hover}
      className={`part child ${visible ? '' : 'off'}`}
      style={{ ['--accent' as string]: color }}
      role="option"
      aria-selected={selected}
      onClick={() => useStore.getState().select(target)}
    >
      <Icon size={14} className="kind-icon" />
      <span className="label">{name}</span>
      <span className="row-actions">
        <Eye visible={visible} onToggle={onToggle} />
      </span>
    </div>
  );
}

/** A part's or sub-part's row (the 3D view hovers parts without their sub-part, so it marks the part's own row). */
function PartRow({ target, ...props }: { target: { kind: 'part'; id: string; sub: string | null } } & React.HTMLAttributes<HTMLDivElement>) {
  return <div {...useHover(target)} {...props} />;
}

/** Puts an original piece back as the game builds it: shown, in its own place, with its own model. */
const resetPiece = (part: string, path: string) => {
  const s = useStore.getState();
  if (partTransformOf(s.doc, part, path)?.mesh) s.setPartMesh(part, path, null);
  s.setPartTransform(part, path, (x) => void Object.assign(x, { position: null, rotation: null, scale: null, visible: null }));
};

/** Which parts are open, kept per work; a part not yet toggled is open when it holds something the author added. */
function useOpenParts(docId: string) {
  const key = `sf.partTree.${docId}`;
  const read = (): Record<string, boolean> => {
    try {
      return JSON.parse(localStorage.getItem(key) ?? '{}') as Record<string, boolean>;
    } catch {
      return {};
    }
  };
  const [state, setState] = useState(() => ({ key, open: read() }));
  const open = state.key === key ? state.open : read();
  if (state.key !== key) setState({ key, open });
  const toggle = (id: string, value: boolean) => {
    const next = { ...open, [id]: value };
    setState({ key, open: next });
    try {
      localStorage.setItem(key, JSON.stringify(next));
    } catch {
      /* the open state is a convenience */
    }
  };
  return [open, toggle] as const;
}

function PartTree({ query }: { query: string }) {
  const item = useItem();
  const doc = useStore((s) => s.doc);
  const selection = useStore((s) => s.selection);
  const subParts = useStore((s) => s.subParts);
  const [open, toggle] = useOpenParts(doc.id ?? '');
  const { select, setPartEnabled } = useStore.getState();
  if (!item) return <div />;
  // The search box: a part shows when its name or anything on it matches; a matching part shows all it holds.
  const q = query.trim().toLowerCase();
  const hit = (name: string | null | undefined) => !q || (name ?? '').toLowerCase().includes(q);
  const ed = editorOf(doc);
  const attachments = (doc.attachments ?? []).filter((a): a is Attachment => !!a);
  const selectedAttachment = selection?.kind === 'attachment' ? attachments.find((a) => a.id === selection.id) : undefined;
  const moved = (part: string) => {
    const t = partTransformOf(doc, part, null);
    return !!t && (t.position != null || t.rotation != null || t.scale != null);
  };
  return (
    <ScrollArea className="parts" type="hover" scrollbars="y">
      {item.parts.map((p) => {
        const enabled = ed.enabled[p.id] ?? p.defaultEnabled;
        const subs = subParts[p.id] ?? [];
        const partHit = hit(partLabel(p));
        const show =
          <T,>(label: (x: T) => string | null | undefined) =>
          (x: T) =>
            partHit || hit(label(x));
        const liveries = p.materials.flatMap((m) =>
          [...layersOf(materialOf(doc, m))]
            .reverse()
            .filter(show(liveryName))
            .map((l) => ({ m, l })),
        );
        const dynamic = p.materials.flatMap((m) => ((materialOf(doc, m)?.layers ?? []).filter(Boolean) as DynamicLayer[]).filter(show(layerName)).map((l) => ({ m, l })));
        const mounted = attachments.filter((a) => a.part === p.id).filter(show((a) => attachmentName(doc, a)));
        // Code effects on this part's materials or on models attached to it.
        const fx = (doc.codeEffects ?? [])
          .filter((e): e is CodeEffect => !!e && (e.material ? p.materials.includes(e.material) : attachments.some((a) => a.id === e.attachment && a.part === p.id)))
          .filter(show((e) => e.name));
        if (!partHit && !liveries.length && !dynamic.length && !mounted.length && !fx.length) return null;
        // The original pieces listed are those the author changed (so they can be found and undone) and the one selected
        // in the 3D view; the others are picked there or chosen as where an attachment hangs.
        const s = selection;
        const pieces = subs
          .map((x) => x.path)
          .filter((path) => pieceChanged(doc, p.id, path) || (s?.kind === 'part' && s.id === p.id && s.sub === path))
          .filter((path) => partHit || hit(pieceLabel(path)) || hit(path));
        // Collapsing a part hides everything under it; what it holds stays counted on its row.
        const kids = liveries.length + dynamic.length + fx.length + mounted.length + pieces.length;
        const inside =
          (s?.kind === 'part' && s.id === p.id && !!s.sub) ||
          ((s?.kind === 'livery' || s?.kind === 'layer') && (p.materials.includes(s.material) || mounted.some((a) => a.id === attachedOf(s.material)))) ||
          (s?.kind === 'fx' && fx.some((e) => e.id === s.id)) ||
          selectedAttachment?.part === p.id;
        const expanded = !!q || inside || (open[p.id] ?? kids > 0);
        return (
          <div key={p.id}>
            <PartRow
              className={`part ${enabled ? '' : 'off'}`}
              target={{ kind: 'part', id: p.id, sub: null }}
              role="option"
              aria-selected={selection?.kind === 'part' && selection.id === p.id && !selection.sub}
              onClick={() => select({ kind: 'part', id: p.id })}
            >
              <ActionIcon
                className="twist"
                variant="subtle"
                color="gray"
                size="sm"
                disabled={!kids}
                data-open={expanded || undefined}
                aria-label={expanded ? t('leftRail.collapse') : t('leftRail.expand')}
                onClick={(e) => (e.stopPropagation(), toggle(p.id, !expanded))}
              >
                {kids ? <IconChevronRight size={13} /> : null}
              </ActionIcon>
              <Tooltip label={enabled ? t('leftRail.k20') : t('leftRail.k21')}>
                <Checkbox size="xs" checked={enabled} onClick={(e) => e.stopPropagation()} onChange={(e) => setPartEnabled(p.id, e.currentTarget.checked)} />
              </Tooltip>
              <span className="label" title={p.materials.join(', ')}>
                {partLabel(p)}
                {moved(p.id) && <span className="moved" title={t('leftRail.k22')} />}
              </span>
              {!expanded && kids > 0 && (
                <span className="kids" title={t('leftRail.kids', { v0: kids })}>
                  {kids}
                </span>
              )}
              <span className="swatches" title={t('leftRail.k23')}>
                {CHANNELS.map((ch) => {
                  const has = p.materials.some((m) => materialOf(doc, m)?.textures?.[ch]);
                  const pend = p.materials.some((m) => ed.pending[pendingKey(m, ch)]);
                  return <i key={ch} className={`sw ${pend ? 'pending' : has ? 'on' : ''}`} />;
                })}
              </span>
              <span className="row-actions">
                <EyeToggle part={p.id} />
                <PartAddMenu part={p}>
                  <ActionIcon className="row-add" variant="subtle" size="sm" aria-label={t('addMenu.title')} onClick={(e) => e.stopPropagation()}>
                    <IconPlus size={14} />
                  </ActionIcon>
                </PartAddMenu>
              </span>
            </PartRow>
            {expanded &&
              liveries.map(({ m, l }) => {
                const K = kindOf(l.kind);
                return (
                  <ChildRow
                    key={l.id}
                    icon={K.icon}
                    color={FEATURE.livery.color}
                    name={liveryName(l)}
                    visible={l.visible !== false}
                    onToggle={() => updateLivery(m, l.id!, (x) => void (x.visible = x.visible === false))}
                    target={{ kind: 'livery', material: m, id: l.id! }}
                  />
                );
              })}
            {expanded &&
              dynamic.map(({ m, l }) => (
                <ChildRow
                  key={l.id}
                  icon={IconMovie}
                  color={FEATURE.layer.color}
                  name={layerName(l)}
                  visible={l.visible !== false}
                  onToggle={() => useStore.getState().updateLayer(m, l.id!, (x) => void (x.visible = x.visible === false))}
                  target={{ kind: 'layer', material: m, id: l.id! }}
                />
              ))}
            {expanded &&
              fx.map((e) => (
                <ChildRow
                  key={e.id}
                  icon={FEATURE.fx.icon}
                  color={FEATURE.fx.color}
                  name={e.name}
                  visible={e.visible !== false}
                  onToggle={() => updateCodeEffect(e.id!, (x) => void (x.visible = x.visible === false))}
                  target={{ kind: 'fx', id: e.id! }}
                />
              ))}
            {expanded && mounted.map((a) => <AttachmentRow key={a.id} a={a} depth={0} />)}
            {expanded &&
              pieces.map((path) => (
                <PartRow
                  key={path}
                  className="part child sub"
                  target={{ kind: 'part', id: p.id, sub: path }}
                  role="option"
                  aria-selected={s?.kind === 'part' && s.id === p.id && s.sub === path}
                  onClick={() => select({ kind: 'part', id: p.id, sub: path })}
                >
                  <IconCube size={13} className="kind-icon" />
                  <span className="label" title={path}>
                    {pieceLabel(path)}
                  </span>
                  <span className="row-actions">
                    {pieceChanged(doc, p.id, path) && (
                      <Tooltip label={t('leftRail.pieceReset')}>
                        <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('leftRail.pieceReset')} onClick={(e) => (e.stopPropagation(), resetPiece(p.id, path))}>
                          <IconArrowBackUp size={13} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                    <EyeToggle part={p.id} sub={path} />
                  </span>
                </PartRow>
              ))}
          </div>
        );
      })}
    </ScrollArea>
  );
}

export function LeftRail() {
  const item = useItem();
  const [query, setQuery] = useState('');
  const pending = useStore((s) => Object.keys(editorOf(s.doc).pending).length);
  const whole = useStore((s) => s.selection == null);
  const { select } = useStore.getState();
  return (
    <aside className="rail">
      <div className="parts-head">
        <IconListTree size={15} className="ico" />
        <span className="title" title={t('leftRail.k24')}>
          {t('leftRail.k24')}
        </span>
        {item && <span className="count">{item.parts.length}</span>}
        <Info label={t('leftRail.k25')} />
        <span style={{ flex: 1 }} />
        {pending > 0 && (
          <Tooltip label={t('leftRail.pendingInfo')}>
            <Badge size="sm" color="yellow" variant="light" style={{ cursor: 'pointer' }} onClick={() => select(null)}>
              {t('leftRail.pending', { v0: pending })}
            </Badge>
          </Tooltip>
        )}
      </div>
      <TextInput
        className="tree-search"
        size="xs"
        aria-label={t('leftRail.search')}
        placeholder={t('leftRail.searchShort')}
        leftSection={<IconSearch size={13} />}
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        rightSection={query ? <CloseButton size="xs" onClick={() => setQuery('')} aria-label={t('leftRail.clearSearch')} /> : null}
      />
      <div className="part whole" role="option" aria-selected={whole} data-tour="whole" onClick={() => select(null)}>
        <IconBox size={14} className="ico" />
        <span className="name">{t('leftRail.whole')}</span>
      </div>
      <PartTree query={query} />
    </aside>
  );
}
