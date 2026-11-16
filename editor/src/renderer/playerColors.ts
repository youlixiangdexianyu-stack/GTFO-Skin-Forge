// Player colours (SkinDocument.playerColors): colours the author lets players change in game. Each drives one or more
// colour slots (a material's tint or glow, an attachment's colour, a screen's colours, a flash). Every slot holds its
// player colour's default, so the 3D view shows what the skin wears until a player picks; changing any linked colour
// changes them all (store.edit runs syncPlayerColors after every edit). Colour schemes (SkinDocument.colorSchemes) give player
// colours other values; the one the author picks (editor.scheme) is what the slots show and what colour edits change, while
// the defaults (the 原色 scheme) stay: a pack's builder writes the defaults into the slots again.
import type { ColorSlot, PlayerColor, SkinDocument } from './protocol';
import { ensureMaterial, uid, useStore } from './store';
import { t } from './i18n';

export type SlotKind = NonNullable<ColorSlot['kind']>;

export const sameSlot = (a: ColorSlot, b: ColorSlot) => a.kind === b.kind && a.target === b.target && (a.kind !== 'effectColor' || (a.effect ?? 0) === (b.effect ?? 0));

const sameRgb = (a: number[], b: number[]) => [0, 1, 2].every((i) => Math.abs((a[i] ?? 1) - (b[i] ?? 1)) < 1e-6);

/** The colour array a slot names in `d`; null when the document has no such colour (a removed attachment or effect). */
export function slotColor(d: SkinDocument, s: ColorSlot): number[] | null {
  const m = d.materials?.find((x) => x?.material === s.target);
  const a = d.attachments?.find((x) => x?.id === s.target);
  switch (s.kind) {
    case 'tint':
      return m?.params?.tint ?? null;
    case 'emissive':
      return m?.params?.emissiveColor ?? null;
    case 'attachmentColor':
      return (a?.mesh && a.material?.color) || null;
    case 'screenText':
      return a?.screen?.textColor ?? null;
    case 'screenPanel':
      return a?.screen?.panelColor ?? null;
    case 'screenBorder':
      return a?.screen?.borderColor ?? null;
    case 'screenLowAmmo':
      return a?.screen?.lowAmmoColor ?? null;
    case 'effectColor': {
      const e = a?.effects?.[s.effect ?? 0];
      return e && e.action !== 'show' ? (e.color ?? null) : null;
    }
  }
  return null;
}

/** The player colour driving `s`, if any. */
export function colorOfSlot(d: SkinDocument, s: ColorSlot): PlayerColor | undefined {
  return d.playerColors?.find((c) => c?.slots?.some((x) => x && sameSlot(x, s))) ?? undefined;
}

type Scheme = NonNullable<NonNullable<SkinDocument['colorSchemes']>[number]>;

/** The colour scheme the editor shows, if one other than the defaults is picked. */
export function activeScheme(d: SkinDocument): Scheme | undefined {
  const id = (d.editor as { scheme?: string } | null | undefined)?.scheme;
  return id ? (d.colorSchemes?.find((x) => x?.id === id) ?? undefined) : undefined;
}

/** What player colour `c` shows now: its value in the picked scheme, else its default. */
export const shownColor = (d: SkinDocument, c: PlayerColor) => activeScheme(d)?.colors?.[c.id ?? ''] ?? c.default ?? [1, 1, 1];

function fill(d: SkinDocument, c: PlayerColor, rgb: number[]) {
  for (const s of c.slots ?? []) {
    const v = s && slotColor(d, s);
    if (v && !sameRgb(v, rgb)) for (let i = 0; i < 3; i++) v[i] = rgb[i];
  }
}

/** Sets player colour `c` to `rgb` (in the picked scheme, else its default) and every slot it drives. */
function paint(d: SkinDocument, c: PlayerColor, rgb: number[]) {
  const scheme = activeScheme(d);
  if (scheme) scheme.colors = { ...scheme.colors, [c.id ?? '']: rgb.slice(0, 3) };
  else c.default = rgb.slice(0, 3);
  fill(d, c, rgb);
}

/**
 * After an edit: slots whose colour is gone are dropped (and player colours left with none); a linked colour the edit
 * changed becomes its player colour's default and is copied to the other slots. Writes nothing when nothing moved.
 */
export function syncPlayerColors(d: SkinDocument) {
  if (!d.playerColors?.length) return;
  for (const c of d.playerColors) {
    if (!c) continue;
    const live = (c.slots ?? []).filter((s) => s && slotColor(d, s));
    if (live.length !== (c.slots ?? []).length) c.slots = live;
    const moved = live.map((s) => slotColor(d, s!)!).find((v) => !sameRgb(v, shownColor(d, c)));
    if (moved) paint(d, c, moved);
  }
  if (d.playerColors.some((c) => !c?.slots?.length)) d.playerColors = d.playerColors.filter((c) => c?.slots?.length);
  if (!d.playerColors.length) d.playerColors = null;
  // A scheme keeps colours only for player colours the skin still has.
  const ids = new Set((d.playerColors ?? []).map((c) => c?.id));
  for (const x of d.colorSchemes ?? []) if (x?.colors && Object.keys(x.colors).some((k) => !ids.has(k))) x.colors = Object.fromEntries(Object.entries(x.colors).filter(([k]) => ids.has(k)));
}

/** Shows scheme `id` (null: the defaults): every slot takes that scheme's colour. */
export function selectScheme(id: string | null) {
  useStore.getState().edit((d) => {
    d.editor = { ...(d.editor ?? {}), scheme: id };
    for (const c of d.playerColors ?? []) if (c) fill(d, c, shownColor(d, c));
  });
}

/** A new scheme with the colours shown now, picked. */
export function addScheme() {
  const id = uid();
  useStore.getState().edit((d) => {
    const colors = Object.fromEntries((d.playerColors ?? []).filter((c): c is PlayerColor => !!c).map((c) => [c.id ?? '', shownColor(d, c).slice(0, 3)]));
    d.colorSchemes = [...(d.colorSchemes ?? []), { id, name: t('schemes.newName', { v0: (d.colorSchemes?.length ?? 0) + 1 }), colors }];
    d.editor = { ...(d.editor ?? {}), scheme: id };
  });
}

export function renameScheme(id: string, name: string) {
  useStore.getState().edit((d) => {
    const x = d.colorSchemes?.find((s) => s?.id === id);
    if (x) x.name = name;
  }, `scheme-name-${id}`);
}

/** Removes scheme `id`; when it was the one shown, the defaults show again. */
export function removeScheme(id: string) {
  useStore.getState().edit((d) => {
    const shown = activeScheme(d)?.id === id;
    d.colorSchemes = (d.colorSchemes ?? []).filter((x) => x?.id !== id);
    if (!d.colorSchemes.length) d.colorSchemes = null;
    if (shown) {
      d.editor = { ...(d.editor ?? {}), scheme: null };
      for (const c of d.playerColors ?? []) if (c) fill(d, c, c.default ?? [1, 1, 1]);
    }
  });
}

/** Whether the game can change the colour at `s`: a gun's materials draw with the game's own shader, which has no tint. */
export const slotWorksInGame = (d: SkinDocument, s: ColorSlot) => !(s.kind === 'tint' && !d.character);

/** Writes `shown` (what the picker shows, maybe the game's own value) into the document, so the slot exists. */
function ensureSlot(d: SkinDocument, s: ColorSlot, shown: number[]) {
  const rgb = shown.slice(0, 3);
  const a = d.attachments?.find((x) => x?.id === s.target);
  if (s.kind === 'tint') {
    const p = ensureMaterial(d, s.target!).params!;
    p.tint = [...rgb, p.tint?.[3] ?? 1];
  } else if (s.kind === 'emissive') ensureMaterial(d, s.target!).params!.emissiveColor = rgb;
  else if (s.kind === 'attachmentColor' && a) a.material ??= { textures: {}, color: [...rgb, 1], unlit: false, params: {} };
}

/** Lets players change the colour at `s`: as a new player colour (`to` null), or as part of player colour `to` (it takes that colour). */
export function linkSlot(s: ColorSlot, shown: number[], to: string | null) {
  useStore.getState().edit((d) => {
    ensureSlot(d, s, shown);
    for (const c of d.playerColors ?? []) if (c?.slots) c.slots = c.slots.filter((x) => x && !sameSlot(x, s));
    d.playerColors = (d.playerColors ?? []).filter((c) => c?.slots?.length);
    let c = to ? d.playerColors.find((x) => x?.id === to) : undefined;
    if (!c) {
      c = { id: uid(), name: t('playerColors.newName', { v0: d.playerColors.length + 1 }), default: shown.slice(0, 3), slots: [] };
      d.playerColors.push(c);
    }
    c.slots!.push({ ...s });
    paint(d, c, shownColor(d, c) ?? shown);
  });
}

export function unlinkSlot(s: ColorSlot) {
  useStore.getState().edit((d) => {
    for (const c of d.playerColors ?? []) if (c?.slots) c.slots = c.slots.filter((x) => x && !sameSlot(x, s));
  });
}

/** Sets a player colour's default and every colour it drives. */
export function setPlayerColor(id: string, rgb: number[]) {
  useStore.getState().edit((d) => {
    const c = d.playerColors?.find((x) => x?.id === id);
    if (c) paint(d, c, rgb);
  }, `player-color-${id}`);
}

export function updatePlayerColor(id: string, fn: (c: PlayerColor) => void, key?: string) {
  useStore.getState().edit(
    (d) => {
      const c = d.playerColors?.find((x) => x?.id === id);
      if (c) fn(c);
    },
    key && `player-color-${id}-${key}`,
  );
}

export function removePlayerColor(id: string) {
  useStore.getState().edit((d) => void (d.playerColors = (d.playerColors ?? []).filter((x) => x?.id !== id)));
}

/** Removes effect `i` of attachment `id`; the flash colours after it keep their player colours. */
export function removeEffect(d: SkinDocument, id: string, i: number) {
  d.attachments?.find((x) => x?.id === id)?.effects?.splice(i, 1);
  const mine = (s: ColorSlot | null) => s?.kind === 'effectColor' && s.target === id;
  for (const c of d.playerColors ?? []) {
    if (!c?.slots) continue;
    if (c.slots.some((s) => mine(s) && (s!.effect ?? 0) === i)) c.slots = c.slots.filter((s) => !(mine(s) && (s!.effect ?? 0) === i));
    for (const s of c.slots) if (mine(s) && (s!.effect ?? 0) > i) s!.effect = (s!.effect ?? 0) - 1;
  }
}
