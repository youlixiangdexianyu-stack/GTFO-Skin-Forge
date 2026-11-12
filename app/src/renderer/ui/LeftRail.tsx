import { ActionIcon, Badge, Checkbox, ScrollArea, TextInput, Tooltip } from '@mantine/core';
import { IconBox, IconChevronDown, IconChevronRight, IconCopy, IconDeviceDesktop, IconEye, IconEyeOff, IconMovie, IconPencil, IconPlus, IconTrash, IconVolume } from '@tabler/icons-react';
import { useState } from 'react';
import { pickSound } from './SkinInspector';
import { baseName, type Attachment, CHANNELS, type DynamicLayer, type SkinSound } from '../protocol';
import { editorOf, materialOf, partTransformOf, pendingKey, useItem, useStore } from '../store';
import { PartAddMenu } from './AddMenu';
import { FEATURE, Info } from './Features';
import { hiddenMeshes, partTarget, useSceneVersion } from '../viewport/refs';
import { partLabel, t } from '../i18n';

function EyeToggle({ part, sub }: { part: string; sub?: string | null }) {
  const visible = useStore((s) => partTransformOf(s.doc, part, sub)?.visible !== false);
  useSceneVersion();
  // Hiding a node hides its own part's meshes only (not a part mounted on it); a mount point alone has nothing to hide.
  const obj = partTarget(part, sub);
  const empty = visible && !!obj && !hiddenMeshes(part, obj).length;
  return (
    <Tooltip label={empty ? t('leftRail.nothingToHide') : visible ? t('leftRail.k10') : t('leftRail.k11')}>
      <ActionIcon
        variant="subtle"
        color={visible ? 'gray' : 'flare'}
        size="sm"
        data-disabled={empty || undefined}
        onClick={(e) => (e.stopPropagation(), empty || useStore.getState().setPartTransform(part, sub, (t) => void (t.visible = visible ? false : null)))}
      >
        {visible ? <IconEye size={14} /> : <IconEyeOff size={14} />}
      </ActionIcon>
    </Tooltip>
  );
}

function AttachmentRow({ a, depth }: { a: Attachment; depth: number }) {
  const selected = useStore((s) => s.selection?.kind === 'attachment' && s.selection.id === a.id);
  const [renaming, setRenaming] = useState(false);
  const { select, updateAttachment, duplicateAttachment, removeAttachment } = useStore.getState();
  const visible = a.visible !== false;
  const stop = (fn: () => void) => (e: React.MouseEvent) => (e.stopPropagation(), fn());
  return (
    <div className="part attach" style={{ paddingLeft: 30 + depth * 14 }} aria-selected={selected} onClick={() => select({ kind: 'attachment', id: a.id! })}>
      <Tooltip label={visible ? t('leftRail.k10') : t('leftRail.k11')}>
        <ActionIcon variant="subtle" color={visible ? 'gray' : 'flare'} size="sm" onClick={stop(() => updateAttachment(a.id!, (x) => void (x.visible = !visible)))}>
          {visible ? <IconEye size={14} /> : <IconEyeOff size={14} />}
        </ActionIcon>
      </Tooltip>
      {a.screen ? <IconDeviceDesktop size={13} className="kind-icon" /> : <IconBox size={13} className="kind-icon" />}
      {renaming ? (
        <TextInput
          size="xs"
          autoFocus
          defaultValue={a.name ?? ''}
          style={{ flex: 1 }}
          onClick={(e) => e.stopPropagation()}
          onBlur={(e) => {
            const name = e.currentTarget.value.trim();
            if (name && name !== a.name) updateAttachment(a.id!, (x) => void (x.name = name));
            setRenaming(false);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') e.currentTarget.blur();
            if (e.key === 'Escape') setRenaming(false);
          }}
        />
      ) : (
        <span className="label" title={t('leftRail.k12')} onDoubleClick={stop(() => setRenaming(true))}>
          {a.name}
        </span>
      )}
      <span className="row-actions">
        <Tooltip label={t('leftRail.k13')}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={stop(() => setRenaming(true))}>
            <IconPencil size={13} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('leftRail.k14')}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={stop(() => duplicateAttachment(a.id!))}>
            <IconCopy size={13} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('leftRail.k15')}>
          <ActionIcon variant="subtle" color="red" size="sm" onClick={stop(() => removeAttachment(a.id!))}>
            <IconTrash size={13} />
          </ActionIcon>
        </Tooltip>
      </span>
    </div>
  );
}

function PartTree() {
  const item = useItem();
  const doc = useStore((s) => s.doc);
  const selection = useStore((s) => s.selection);
  const subParts = useStore((s) => s.subParts);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const { select, setPartEnabled } = useStore.getState();
  if (!item) return <div />;
  const ed = editorOf(doc);
  const attachments = (doc.attachments ?? []).filter((a): a is Attachment => !!a);
  const attachedTo = (part: string, sub: string | null) => attachments.filter((a) => a.part === part && (a.transform ?? null) === sub);
  const selectedAttachment = selection?.kind === 'attachment' ? attachments.find((a) => a.id === selection.id) : undefined;
  const moved = (part: string, sub?: string | null) => {
    const t = partTransformOf(doc, part, sub);
    return !!t && (t.position != null || t.rotation != null || t.scale != null);
  };
  return (
    <ScrollArea className="parts" type="hover">
      {item.parts.map((p) => {
        const enabled = ed.enabled[p.id] ?? p.defaultEnabled;
        const subs = subParts[p.id] ?? [];
        // A sub-transform that only carries attachments still gets a row, so every attachment is listed.
        const extra: string[] = [];
        for (const a of attachments) if (a.part === p.id && a.transform && !subs.some((x) => x.path === a.transform) && !extra.includes(a.transform)) extra.push(a.transform);
        const rows = [...subs, ...extra.map((path) => ({ path, name: path.split('/').pop()!, depth: 0 }))];
        const expanded =
          !!open[p.id] ||
          (selection?.kind === 'part' && selection.id === p.id && !!selection.sub) ||
          (selectedAttachment?.part === p.id && !!selectedAttachment.transform);
        return (
          <div key={p.id}>
            <div
              className={`part ${enabled ? '' : 'off'}`}
              aria-selected={selection?.kind === 'part' && selection.id === p.id && !selection.sub}
              onClick={() => select({ kind: 'part', id: p.id })}
            >
              <ActionIcon variant="subtle" color="gray" size="sm" disabled={!rows.length} onClick={(e) => (e.stopPropagation(), setOpen({ ...open, [p.id]: !expanded }))}>
                {rows.length ? expanded ? <IconChevronDown size={13} /> : <IconChevronRight size={13} /> : null}
              </ActionIcon>
              <EyeToggle part={p.id} />
              <Tooltip label={enabled ? t('leftRail.k20') : t('leftRail.k21')}>
                <Checkbox size="xs" checked={enabled} onClick={(e) => e.stopPropagation()} onChange={(e) => setPartEnabled(p.id, e.currentTarget.checked)} />
              </Tooltip>
              <span className="label" title={p.materials.join(', ')}>
                {partLabel(p)}
                {moved(p.id) && <span className="moved" title={t('leftRail.k22')} />}
              </span>
              <span className="swatches" title={t('leftRail.k23')}>
                {CHANNELS.map((ch) => {
                  const has = p.materials.some((m) => materialOf(doc, m)?.textures?.[ch]);
                  const pend = p.materials.some((m) => ed.pending[pendingKey(m, ch)]);
                  return <i key={ch} className={`sw ${pend ? 'pending' : has ? 'on' : ''}`} />;
                })}
              </span>
              <PartAddMenu part={p}>
                <ActionIcon className="row-add" variant="subtle" size="sm" aria-label={t('addMenu.title')} onClick={(e) => e.stopPropagation()}>
                  <IconPlus size={14} />
                </ActionIcon>
              </PartAddMenu>
            </div>
            {p.materials.flatMap((m) =>
              ((materialOf(doc, m)?.layers ?? []).filter(Boolean) as DynamicLayer[]).map((l) => (
                <div
                  key={l.id}
                  className="part layer"
                  style={{ paddingLeft: 30 }}
                  aria-selected={selection?.kind === 'layer' && selection.id === l.id}
                  onClick={() => select({ kind: 'layer', material: m, id: l.id! })}
                >
                  <IconMovie size={13} color={FEATURE.layer.color} />
                  <span className="label">{l.name}</span>
                </div>
              )),
            )}
            {attachedTo(p.id, null).map((a) => (
              <AttachmentRow key={a.id} a={a} depth={0} />
            ))}
            {expanded &&
              rows.map((sp, i) => (
                <div key={`${sp.path}-${i}`}>
                  <div
                    className="part sub"
                    style={{ paddingLeft: 30 + sp.depth * 14 }}
                    aria-selected={selection?.kind === 'part' && selection.id === p.id && selection.sub === sp.path}
                    onClick={() => select({ kind: 'part', id: p.id, sub: sp.path })}
                  >
                    <EyeToggle part={p.id} sub={sp.path} />
                    <span className="label" title={sp.path}>
                      {sp.name}
                      {moved(p.id, sp.path) && <span className="moved" title={t('leftRail.k22')} />}
                    </span>
                  </div>
                  {attachedTo(p.id, sp.path).map((a) => (
                    <AttachmentRow key={a.id} a={a} depth={sp.depth + 1} />
                  ))}
                </div>
              ))}
          </div>
        );
      })}
      <SoundRows />
    </ScrollArea>
  );
}

/** The skin's sounds, at the end of the tree: they belong to the whole skin, not to a part. */
function SoundRows() {
  const sounds = useStore((s) => s.doc.sounds ?? []).filter((x): x is SkinSound => !!x);
  const selected = useStore((s) => (s.selection?.kind === 'sound' ? s.selection.id : null));
  const { select } = useStore.getState();
  return (
    <>
      <div className="tree-group">
        <IconVolume size={13} color={FEATURE.sound.color} />
        <span>{t('skinInspector.k31')}</span>
        <ActionIcon variant="subtle" size="sm" color="green" ml="auto" aria-label={t('skinInspector.k32')} onClick={() => void pickSound()}>
          <IconPlus size={14} />
        </ActionIcon>
      </div>
      {sounds.map((x) => (
        <div key={x.id} className="part layer" style={{ paddingLeft: 14 }} aria-selected={selected === x.id} onClick={() => select({ kind: 'sound', id: x.id! })}>
          <IconVolume size={13} color={FEATURE.sound.color} />
          <span className="label">{x.name || baseName(x.file ?? '')}</span>
        </div>
      ))}
    </>
  );
}

export function LeftRail() {
  const item = useItem();
  const pending = useStore((s) => Object.keys(editorOf(s.doc).pending).length);
  const { select } = useStore.getState();
  return (
    <aside className="rail">
      <div className="parts-head">
        <span>{t('leftRail.k24')}{item ? ` · ${item.parts.length}` : ''}</span>
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
      <PartTree />
    </aside>
  );
}
