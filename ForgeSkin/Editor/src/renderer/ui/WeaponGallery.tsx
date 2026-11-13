// Choosing the item to skin: the 3D view's title opens a gallery of every weapon, tool and item with thumbnails.
import { Chip, Group, Popover, ScrollArea, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useHotkeys } from '@mantine/hooks';
import { IconChevronDown, IconSearch } from '@tabler/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { CatalogItem } from '../protocol';
import { useItem, useStore } from '../store';
import { galleryThumbOf, THUMB_EVENT } from '../viewport/thumbnails';
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

function WeaponCard({ item, current, onPick }: { item: CatalogItem; current: boolean; onPick: () => void }) {
  const ref = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (current) ref.current?.scrollIntoView({ block: 'nearest' });
  }, [current]);
  const thumb = galleryThumbOf(item.id);
  return (
    <UnstyledButton ref={ref} className="weapon" aria-current={current} onClick={() => (void useStore.getState().selectWeapon(item.id), onPick())}>
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

function WeaponPicker({ onPick }: { onPick: () => void }) {
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
            {t('leftRail.k8')}{q}{t('leftRail.k9')}
          </Text>
        )}
      </ScrollArea>
    </div>
  );
}

/** The current item's name; clicking it (or Ctrl+P) opens the gallery. */
export function WeaponTitle() {
  const item = useItem();
  const [open, setOpen] = useState(false);
  useHotkeys([['mod+p', () => setOpen((o) => !o)]]);
  return (
    <Popover opened={open} onChange={setOpen} position="bottom-start" width={720} shadow="xl" trapFocus>
      <Popover.Target>
        <UnstyledButton className="stage-title" aria-label={t('gallery.open')} onClick={() => setOpen((o) => !o)}>
          <div className="name">
            {item ? item.name : t('gallery.pick')}
            <IconChevronDown size={22} className="chev" />
          </div>
          {item && (
            <div className="sub">
              {categoryOf(item)}
              {lang() === 'zh' && item.nameZh ? ` · ${item.nameZh}` : ''}
              {item.calibre ? ` · ${item.calibre}` : ''}
            </div>
          )}
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown p={0} className="gallery">
        <WeaponPicker onPick={() => setOpen(false)} />
      </Popover.Dropdown>
    </Popover>
  );
}
