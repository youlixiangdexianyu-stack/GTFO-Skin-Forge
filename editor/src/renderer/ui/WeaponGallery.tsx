// Choosing the gear for a new skin: a gallery of every weapon, tool and item with thumbnails.
import { Chip, Group, Loader, Modal, ScrollArea, Text, TextInput, UnstyledButton } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { CatalogItem } from '../protocol';
import { useItem, useStore } from '../store';
import { requestItemThumbs, THUMB_EVENT, thumbComing, thumbOf } from '../viewport/thumbnails';
import { lang, localCategory, localName, t, useLang } from '../i18n';

/** Filter chips by slot, in this order (unknown slots last); a slot without a label here shows its id. */
export const SLOT_LABEL: Record<string, () => string> = {
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

export function useThumbs() {
  const [, setV] = useState(0);
  useEffect(() => {
    const f = () => setV((n) => n + 1);
    window.addEventListener(THUMB_EVENT, f);
    return () => window.removeEventListener(THUMB_EVENT, f);
  }, []);
}

function WeaponCard({ item, current, onPick }: { item: CatalogItem; current: boolean; onPick: (id: string) => void }) {
  const ref = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (current) ref.current?.scrollIntoView({ block: 'nearest' });
  }, [current]);
  const thumb = thumbOf(item.id);
  // A card scrolled into view is drawn before the others.
  useEffect(() => {
    if (thumb || !ref.current) return;
    const seen = new IntersectionObserver((e) => e.some((x) => x.isIntersecting) && requestItemThumbs([item.id], true));
    seen.observe(ref.current);
    return () => seen.disconnect();
  }, [item.id, thumb]);
  return (
    <UnstyledButton ref={ref} className="weapon" aria-current={current} onClick={() => onPick(item.id)}>
      <div className="thumb">
        {thumb ? (
          <img src={thumb} alt="" />
        ) : item.slot === 'ammo' ? (
          'AMMO'
        ) : (
          thumbComing(item.id) && (
            <span className="thumb-drawing">
              <Loader size={12} color="gray" />
              {t('gallery.drawing')}
            </span>
          )
        )}
      </div>
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

function WeaponPicker({ onPick }: { onPick: (id: string) => void }) {
  const catalog = useStore((s) => s.catalog);
  const weaponId = useStore((s) => s.weaponId);
  const [q, setQ] = useState('');
  const [slot, setSlot] = useState<string>('all');
  const lang = useLang();
  useThumbs();
  useEffect(() => requestItemThumbs(catalog.map((c) => c.id)), [catalog]);
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
        <TextInput size="sm" autoFocus leftSection={<IconSearch size={14} />} placeholder={t('leftRail.k6')} value={q} onChange={(e) => setQ(e.currentTarget.value)} />
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
            <div className="weapon-grid">
              {items.map((c) => (
                <WeaponCard key={c.id} item={c} current={c.id === weaponId} onPick={onPick} />
              ))}
            </div>
          </div>
        ))}
        {catalog.length > 0 && !groups.length && (
          <Text c="dimmed" size="sm" p="sm">
            {t('leftRail.k8')}
            {q}
            {t('leftRail.k9')}
          </Text>
        )}
      </ScrollArea>
    </div>
  );
}

/** The gallery of every gear, opened from 「＋ 新建」: picking one makes a new skin for it. */
export function GearGallery({ opened, onClose, onPick }: { opened: boolean; onClose: () => void; onPick: (id: string) => void }) {
  return (
    <Modal opened={opened} onClose={onClose} title={t('works.pickGear')} size={760} padding={0} classNames={{ body: 'gallery', header: 'gallery-head' }}>
      <WeaponPicker onPick={(id) => (onClose(), onPick(id))} />
    </Modal>
  );
}

/** The open item's name over the 3D view. */
export function WeaponTitle() {
  const item = useItem();
  if (!item) return null;
  return (
    <div className="stage-title">
      <div className="name">{item.name}</div>
      <div className="sub">
        {categoryOf(item)}
        {lang() === 'zh' && item.nameZh && item.nameZh !== categoryOf(item) ? ` · ${item.nameZh}` : ''}
        {item.calibre ? ` · ${item.calibre}` : ''}
      </div>
    </div>
  );
}
