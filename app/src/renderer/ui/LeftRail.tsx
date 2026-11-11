import { ActionIcon, Checkbox, Chip, Group, Menu, ScrollArea, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconBox, IconChevronDown, IconChevronRight, IconCopy, IconDeviceDesktop, IconEye, IconEyeOff, IconPencil, IconPlus, IconSearch, IconTrash } from '@tabler/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { importMesh } from '../actions';
import { type Attachment, CHANNELS, type CatalogItem, type Slot } from '../protocol';
import { editorOf, materialOf, partTransformOf, pendingKey, useItem, useStore } from '../store';
import { THUMB_EVENT, thumbOf } from '../viewport/thumbnails';

const SLOTS: { id: Slot | 'all'; label: string }[] = [
  { id: 'all', label: '全部' },
  { id: 'main', label: '主武器' },
  { id: 'special', label: '特殊武器' },
  { id: 'attachment', label: '配件' },
  { id: 'ammo', label: '弹药' },
];

/** Official Chinese weapon type; the raw category only until the catalog provides it. */
export const categoryOf = (c: CatalogItem) => c.categoryZh || c.category;

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
          {item.nameZh || item.name}
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
  useThumbs();
  const groups = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const out = new Map<string, CatalogItem[]>();
    for (const c of catalog) {
      if (slot !== 'all' && c.slot !== slot) continue;
      const cat = categoryOf(c);
      if (needle && ![c.name, c.nameZh, cat, c.calibre ?? '', c.id].some((s) => s?.toLowerCase().includes(needle))) continue;
      out.set(cat, [...(out.get(cat) ?? []), c]);
    }
    return [...out];
  }, [catalog, q, slot]);

  return (
    <div className="picker">
      <div className="search">
        <TextInput size="sm" leftSection={<IconSearch size={14} />} placeholder="搜索武器、配件或口径" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        <Chip.Group value={slot} onChange={(v) => setSlot(v as string)}>
          <Group gap={4}>
            {SLOTS.map((s) => (
              <Chip key={s.id} value={s.id} size="xs" variant="outline">
                {s.label}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
      </div>
      <ScrollArea className="weapons" type="hover" offsetScrollbars>
        {!catalog.length && (
          <Text c="dimmed" size="sm" p="sm">
            连接游戏后，这里会列出所有武器和配件。
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
            没有找到「{q}」。
          </Text>
        )}
      </ScrollArea>
    </div>
  );
}

function EyeToggle({ part, sub }: { part: string; sub?: string | null }) {
  const visible = useStore((s) => partTransformOf(s.doc, part, sub)?.visible !== false);
  return (
    <Tooltip label={visible ? '隐藏（游戏里也隐藏）' : '显示'}>
      <ActionIcon
        variant="subtle"
        color={visible ? 'gray' : 'flare'}
        size="sm"
        onClick={(e) => (e.stopPropagation(), useStore.getState().setPartTransform(part, sub, (t) => void (t.visible = !visible)))}
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
      <Tooltip label={visible ? '隐藏（游戏里也隐藏）' : '显示'}>
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
        <span className="label" title="双击改名" onDoubleClick={stop(() => setRenaming(true))}>
          {a.name}
        </span>
      )}
      <span className="row-actions">
        <Tooltip label="改名">
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={stop(() => setRenaming(true))}>
            <IconPencil size={13} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="复制一份">
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={stop(() => duplicateAttachment(a.id!))}>
            <IconCopy size={13} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="删除">
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
    const label = s.catalog.find((c) => c.id === s.weaponId)?.parts.find((p) => p.id === sel.id)?.label ?? sel.id;
    return sel.sub ? `${label} · ${sel.sub}` : label;
  });
  const { addAttachment } = useStore.getState();
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <UnstyledButton className="part add">
          <IconPlus size={14} />
          <span className="label">添加挂件{target ? `到「${target}」` : ''}</span>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconDeviceDesktop size={14} />} onClick={() => addAttachment('screen')}>
          弹药屏
        </Menu.Item>
        <Menu.Item leftSection={<IconBox size={14} />} onClick={() => void importMesh()}>
          导入模型（.glb）
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
    return !!t && [...(t.position ?? []), ...(t.rotation ?? [])].some((v) => Math.abs(v) > 1e-6) || (t?.scale ?? []).some((v) => Math.abs(v - 1) > 1e-6);
  };
  return (
    <ScrollArea className="parts" type="hover">
      {item.parts.map((p) => {
        const enabled = ed.enabled[p.id] ?? p.defaultEnabled;
        const subs = subParts[p.id] ?? [];
        // A sub-transform that only carries attachments still gets a row, so every attachment is listed.
        const extra: string[] = [];
        for (const a of attachments) if (a.part === p.id && a.transform && !subs.some((x) => x.name === a.transform) && !extra.includes(a.transform)) extra.push(a.transform);
        const rows = [...subs, ...extra.map((name) => ({ name, depth: 0 }))];
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
              <Tooltip label={enabled ? '这个部件会装在枪上' : '这个部件不装'}>
                <Checkbox size="xs" checked={enabled} onClick={(e) => e.stopPropagation()} onChange={(e) => setPartEnabled(p.id, e.currentTarget.checked)} />
              </Tooltip>
              <span className="label" title={p.materials.join(', ')}>
                {p.label}
                {moved(p.id) && <span className="moved" title="位置或大小改过" />}
              </span>
              <span className="swatches" title="基础色 · 法线 · 金属/光滑 · 自发光">
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
                <div key={`${sp.name}-${i}`}>
                  <div
                    className="part sub"
                    style={{ paddingLeft: 30 + sp.depth * 14 }}
                    aria-selected={selection?.kind === 'part' && selection.id === p.id && selection.sub === sp.name}
                    onClick={() => select({ kind: 'part', id: p.id, sub: sp.name })}
                  >
                    <EyeToggle part={p.id} sub={sp.name} />
                    <span className="label" title={sp.name}>
                      {sp.name}
                      {moved(p.id, sp.name) && <span className="moved" title="位置或大小改过" />}
                    </span>
                  </div>
                  {attachedTo(p.id, sp.name).map((a) => (
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
        <span style={{ flex: 1 }}>部件与挂件{item ? ` · ${item.parts.length}` : ''}</span>
        <Text size="xs" c="dimmed">
          眼睛＝隐藏 · 勾选＝装上
        </Text>
      </div>
      <PartTree />
    </aside>
  );
}
