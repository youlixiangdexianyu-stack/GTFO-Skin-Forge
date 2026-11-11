import { ActionIcon, Checkbox, Chip, Group, Menu, ScrollArea, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconBox, IconChevronDown, IconChevronRight, IconCopy, IconDeviceDesktop, IconEye, IconEyeOff, IconPencil, IconPlus, IconSearch, IconTrash } from '@tabler/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { importMesh } from '../actions';
import { type Attachment, CHANNELS, type CatalogItem } from '../protocol';
import { editorOf, materialOf, partTransformOf, pendingKey, useItem, useStore } from '../store';
import { THUMB_EVENT, thumbOf } from '../viewport/thumbnails';
import { localCategory, localName, partLabel, t, useLang } from '../i18n';

/** Filter chips by slot, in this order (unknown slots last); a slot without a label here shows its id. */
const SLOT_LABEL: Record<string, () => string> = {
  main: () => t('leftRail.k2'),
  special: () => t('leftRail.k3'),
  melee: () => t('slot.melee'),
  tool: () => t('slot.tool'),
  pack: () => t('slot.pack'),
  consumable: () => t('slot.consumable'),
  carry: () => t('slot.carry'),
  attachment: () => t('leftRail.k4'),
  ammo: () => t('leftRail.k5'),
};

/** Weapon type in the UI language. */
export const categoryOf = (c: CatalogItem) => localCategory(c);

function useThumbs() {
  const [, setV] = useState(0);
  useEffect(() => {
    const f = () => setV((n) => n + 1);
    window.addEventListener(THUMB_EVENT, f);
    return () => window.removeEventListener(THUMB_EVENT, f);
  }, []);
}

function WeaponRow({ item, current }: { item: CatalogItem; current: boolean }) {
  const ref = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (current) ref.current?.scrollIntoView({ block: 'nearest' });
  }, [current]);
  const thumb = thumbOf(item.id);
  return (
    <UnstyledButton ref={ref} className="weapon" aria-current={current} onClick={() => void useStore.getState().selectWeapon(item.id)}>
      <div className="thumb">{thumb ? <img src={thumb} alt="" /> : item.slot === 'ammo' ? 'AMMO' : ''}</div>
      <div style={{ minWidth: 0 }}>
        <div className="t1">
          {localName(item)}
          {item.calibre && <span className="calibre">{item.calibre}</span>}
        </div>
        <div className="t2">
          {categoryOf(item)} · {item.name}
        </div>
      </div>
    </UnstyledButton>
  );
}

function WeaponPicker() {
  const catalog = useStore((s) => s.catalog);
  const weaponId = useStore((s) => s.weaponId);
  const [q, setQ] = useState('');
  const [slot, setSlot] = useState<string>('all');
  const lang = useLang();
  useThumbs();
  const slots = useMemo(() => {
    const order = Object.keys(SLOT_LABEL);
    const rank = (s: string) => (order.includes(s) ? order.indexOf(s) : order.length);
    return ['all', ...[...new Set(catalog.map((c) => c.slot))].sort((x, y) => rank(x) - rank(y))];
  }, [catalog]);
  const groups = useMemo(() => {
    void lang; // type names follow the language
    const needle = q.trim().toLowerCase();
    const out = new Map<string, CatalogItem[]>();
    for (const c of catalog) {
      if (slot !== 'all' && c.slot !== slot) continue;
      const cat = categoryOf(c);
      if (needle && ![c.name, c.nameZh, cat, c.calibre ?? '', c.id].some((s) => s?.toLowerCase().includes(needle))) continue;
      out.set(cat, [...(out.get(cat) ?? []), c]);
    }
    return [...out];
  }, [catalog, q, slot, lang]);

  return (
    <div className="picker">
      <div className="search">
        <TextInput size="sm" leftSection={<IconSearch size={14} />} placeholder={t('leftRail.k6')} value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        <Chip.Group value={slot} onChange={(v) => setSlot(v as string)}>
          <Group gap={4}>
            {slots.map((s) => (
              <Chip key={s} value={s} size="xs" variant="outline">
                {s === 'all' ? t('leftRail.k1') : (SLOT_LABEL[s]?.() ?? s)}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
      </div>
      <ScrollArea className="weapons" type="hover" offsetScrollbars>
        {!catalog.length && (
          <Text c="dimmed" size="sm" p="sm">
            {t('leftRail.k7')}
          </Text>
        )}
        {groups.map(([cat, items]) => (
          <div key={cat}>
            <div className="group-label">{cat}</div>
            {items.map((c) => (
              <WeaponRow key={c.id} item={c} current={c.id === weaponId} />
            ))}
          </div>
        ))}
        {catalog.length > 0 && !groups.length && (
          <Text c="dimmed" size="sm" p="sm">
            {t('leftRail.k8')}{q}{t('leftRail.k9')}
          </Text>
        )}
      </ScrollArea>
    </div>
  );
}

function EyeToggle({ part, sub }: { part: string; sub?: string | null }) {
  const visible = useStore((s) => partTransformOf(s.doc, part, sub)?.visible !== false);
  return (
    <Tooltip label={visible ? t('leftRail.k10') : t('leftRail.k11')}>
      <ActionIcon
        variant="subtle"
        color={visible ? 'gray' : 'flare'}
        size="sm"
        onClick={(e) => (e.stopPropagation(), useStore.getState().setPartTransform(part, sub, (t) => void (t.visible = visible ? false : null)))}
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

function AddAttachment() {
  const target = useStore((s) => {
    const sel = s.selection;
    if (sel?.kind !== 'part') return null;
    const part = s.catalog.find((c) => c.id === s.weaponId)?.parts.find((p) => p.id === sel.id);
    const label = part ? partLabel(part) : sel.id;
    return sel.sub ? `${label} · ${sel.sub.split('/').pop()}` : label;
  });
  const { addAttachment } = useStore.getState();
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <UnstyledButton className="part add">
          <IconPlus size={14} />
          <span className="label">{t('leftRail.k16')}{target ? t('leftRail.k17', { v0: target }) : ''}</span>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconDeviceDesktop size={14} />} onClick={() => addAttachment('screen')}>
          {t('leftRail.k18')}
        </Menu.Item>
        <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void importMesh()}>
          {t('leftRail.k19')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
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
            </div>
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
      <AddAttachment />
    </ScrollArea>
  );
}

export function LeftRail() {
  const item = useItem();
  return (
    <aside className="rail">
      <WeaponPicker />
      <div className="parts-head">
        <span style={{ flex: 1 }}>{t('leftRail.k24')}{item ? ` · ${item.parts.length}` : ''}</span>
        <Text size="xs" c="dimmed">
          {t('leftRail.k25')}
        </Text>
      </div>
      <PartTree />
    </aside>
  );
}
