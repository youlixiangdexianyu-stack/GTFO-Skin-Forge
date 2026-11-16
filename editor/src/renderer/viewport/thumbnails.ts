// Thumbnails: each catalog item (the gear gallery, the work list) and each skin (its card), drawn one at a time while the app
// is idle by the hidden card window (CardWindow.tsx) with the 3D view's own stage, so a card shows the work as the 3D view does.
// Item images ship with the editor (../thumbs/<id>.webp, made at release by scripts/gallery-thumbs.mjs from the game); an item
// without one (new in a game update, or a checkout that never made them) is drawn in the background and kept in the data folder's
// cache. Skin images are kept in localStorage with a hash of everything their look comes from (preview, document, the files it
// names), so a change draws them again. The weapon gallery shows each item's last edited skin.
import { call, fileUrl, type SkinDocument, type WeaponPreview } from '../protocol';
import { useStore } from '../store';
import type { CardJob, CardPicture } from './CardWindow';

export const THUMB_EVENT = 'sf:thumbs';
const BUNDLED = import.meta.glob<string>('../thumbs/*.webp', { eager: true, query: '?url', import: 'default' });
/** Size an item or skin image is drawn at before its clear border is trimmed (the cards show it scaled to fit). */
const ITEM_SIZE = [512, 256] as const;
/** How cards are drawn (CardWindow.tsx): bumping it draws every kept image again. */
const CARD = 5;
const key = (id: string) => `sf.thumb.v${CARD}.${id}`;
const read = (k: string) => {
  try {
    return localStorage.getItem(k);
  } catch {
    return null;
  }
};
/** A 32-bit FNV-1a hash of `text`, for keys of what a picture was drawn from. */
export const hash = (text: string) => [...text].reduce((h, c) => Math.imul(h ^ c.charCodeAt(0), 16777619), 2166136261 >>> 0) >>> 0;

const idle = async () => {
  while (useStore.getState().previewLoading || useStore.getState().busy) await new Promise((r) => setTimeout(r, 1000));
};

/** A card picture of `doc` on item `id` from the card window, at `size`; null when it could not be drawn. */
const card = (id: string, preview: WeaponPreview, doc: SkinDocument, size: readonly [number, number]) =>
  window.forge.drawCard({
    item: useStore.getState().catalog.find((c) => c.id === id) ?? null,
    preview,
    doc,
    width: size[0],
    height: size[1],
  } satisfies CardJob) as Promise<CardPicture | null>;

/** Size a skin's pack picture is drawn at before its clear border is trimmed: twice the card image, for the game's big details picture. */
const PICTURE_SIZE = [1024, 512] as const;

/** A skin's pack picture (what the game's skin picker shows for it): drawn as its card, on a transparent canvas; null when it could not be drawn. */
export async function skinPicture(id: string, preview: WeaponPreview, doc: SkinDocument): Promise<HTMLCanvasElement | null> {
  const drawn = await card(id, preview, doc, PICTURE_SIZE);
  if (!drawn) return null;
  const image = new Image();
  image.src = drawn.url;
  await image.decode();
  const canvas = document.createElement('canvas');
  canvas.width = image.naturalWidth;
  canvas.height = image.naturalHeight;
  canvas.getContext('2d')!.drawImage(image, 0, 0);
  return canvas;
}

/** An item's image from its preview (no skin) as WebP; scripts/gallery-thumbs.mjs makes the shipped ones with it. */
export const itemImage = async (id: string, preview: WeaponPreview) => (await card(id, preview, { name: '', materials: [], attachments: [] } as SkinDocument, ITEM_SIZE))?.url ?? null;

// Item images drawn here (a checkout without the shipped ones, an item new in a game update) are files in the data folder's
// cache, listed in localStorage with the version they were drawn for: the catalog entry (it changes with the game's model data)
// and the renderer's. The gallery asks for every item when it opens and for the ones scrolled to first; one is drawn at a time
// while the editor is idle, and each shows as soon as it is drawn.
const DRAWN = 'sf.itemThumbs.v1';
const RENDER_VERSION = CARD;
let drawn: Record<string, string> = (() => {
  try {
    return JSON.parse(localStorage.getItem(DRAWN) ?? '{}') as Record<string, string>;
  } catch {
    return {};
  }
})();
const itemFile = (id: string) => `${useStore.getState().dataDir}\\cache\\thumbs\\${id}.webp`;
const versionOf = (id: string) => {
  const item = useStore.getState().catalog.find((c) => c.id === id);
  return item ? `${RENDER_VERSION}.${hash(JSON.stringify(item))}` : null;
};

/** An item's image: the shipped one, else the one drawn for its current version; null while there is none. */
export const thumbOf = (id: string) => {
  const shipped = BUNDLED[`../thumbs/${id}.webp`];
  if (shipped) return shipped;
  const v = drawn[id];
  return v && v === versionOf(id) ? `${fileUrl(itemFile(id))}?v=${v}` : null;
};

async function saveItemImage(id: string, url: string) {
  const v = versionOf(id);
  if (!v) return;
  await window.forge.writeFile(
    itemFile(id),
    Uint8Array.from(atob(url.split(',')[1]), (ch) => ch.charCodeAt(0)),
  );
  drawn = { ...drawn, [id]: v };
  try {
    localStorage.setItem(DRAWN, JSON.stringify(drawn));
  } catch {
    /* without storage the image is drawn again next time */
  }
  window.dispatchEvent(new Event(THUMB_EVENT));
}

/** Draws the image of an opened item that has none from the preview the 3D view loaded. */
export async function itemThumbnail(id: string, preview: WeaponPreview): Promise<void> {
  // Drawn from game files another mod changed, or a damaged one: left for a later opening.
  if (thumbOf(id) || preview.damaged || preview.modified) return;
  try {
    const url = await itemImage(id, preview);
    if (url) await saveItemImage(id, url);
  } catch {
    /* a missing thumbnail only shows the placeholder */
  }
}

const wanted: string[] = [];
const skipped = new Set<string>();
/** Whether an item without an image still gets one this session (its card shows that it is being drawn). */
export const thumbComing = (id: string) => !skipped.has(id);
let drawing = false;

/** Asks for the images of these items: `first` puts them before the ones already asked for (the gallery's visible cards). */
export function requestItemThumbs(ids: string[], first = false) {
  const missing = ids.filter((id) => !thumbOf(id) && !skipped.has(id));
  if (!missing.length) return;
  for (const id of missing) {
    const at = wanted.indexOf(id);
    if (at >= 0) wanted.splice(at, 1);
  }
  if (first) wanted.unshift(...missing);
  else wanted.push(...missing);
  if (!drawing) void drawWanted();
}

async function drawWanted() {
  drawing = true;
  const started = performance.now();
  let count = 0;
  try {
    for (let id = wanted.shift(); id; id = wanted.shift()) {
      if (thumbOf(id) || skipped.has(id)) continue;
      await idle();
      try {
        const preview = await call('preview.weapon', { id });
        if (preview.damaged || preview.modified) {
          skipped.add(id);
          continue;
        }
        const url = await itemImage(id, preview);
        if (url) {
          await saveItemImage(id, url);
          count++;
        } else skipped.add(id);
      } catch {
        skipped.add(id); // tried again at the next start
      }
    }
  } finally {
    drawing = false;
    if (count) useStore.getState().log(`gallery images: ${count} drawn in ${((performance.now() - started) / 1000).toFixed(1)} s`);
  }
}

// Item images kept in localStorage by earlier versions, and those of earlier formats, are never read again.
try {
  for (const k of Object.keys(localStorage)) if (k.startsWith('sf.thumb.') && !k.startsWith(key('skin.'))) localStorage.removeItem(k);
} catch {
  /* no storage: nothing kept */
}

const skinKey = (skinId: string) => key(`skin.${skinId}`);
export const skinThumbOf = (skinId: string) => read(skinKey(skinId));
/**
 * The skin's own pictures its image was drawn without (the game's in their place), by file name: `gone` no longer exist, `unreadable`
 * are there but are no picture the editor reads (another format, or damaged); null when it has them all.
 */
export const skinThumbMissing = (skinId: string): { gone: string[]; unreadable: string[] } | null => {
  try {
    const m = JSON.parse(read(`${skinKey(skinId)}.missing`) ?? 'null') as { gone?: unknown; unreadable?: unknown } | null;
    return m && Array.isArray(m.gone) && Array.isArray(m.unreadable) ? (m as { gone: string[]; unreadable: string[] }) : null;
  } catch {
    return null;
  }
};

/**
 * What a skin's card is drawn from, as a hash: the item's preview, the document (of the editor's own notes only what the view
 * shows: the colour variant and the models of attachments and replaced parts) and the size and write time of every file it names.
 */
async function lookOf(preview: WeaponPreview, doc: SkinDocument): Promise<string> {
  const editor = (doc.editor ?? {}) as { variant?: string; meshPreviews?: Record<string, string>; partMeshPreviews?: Record<string, string> };
  const models = [...Object.values(editor.meshPreviews ?? {}), ...Object.values(editor.partMeshPreviews ?? {})];
  const { files } = await call('project.files', { project: doc });
  const stats = await Promise.all([...files, ...models].map((f) => window.forge.stat(f)));
  const shown = { variant: editor.variant ?? null, meshPreviews: editor.meshPreviews ?? null, partMeshPreviews: editor.partMeshPreviews ?? null };
  return String(hash(JSON.stringify(preview) + JSON.stringify({ ...doc, editor: shown }) + JSON.stringify(stats)));
}

/** Draws the card of one skin of `weaponId` (its document, or the file at `path`) unless the stored one is still current. */
export async function skinThumbnail(weaponId: string, skinId: string, source: { doc?: SkinDocument; path?: string }): Promise<void> {
  await idle();
  try {
    const doc = source.doc ?? (source.path ? await call('project.load', { path: source.path }) : null);
    if (!doc) return;
    const s = useStore.getState();
    const preview = s.weaponId === weaponId && s.preview ? s.preview : await call('preview.weapon', { id: weaponId });
    const src = await lookOf(preview, doc);
    if (skinThumbOf(skinId) && read(`${skinKey(skinId)}.src`) === src) return;
    const drawn = await card(weaponId, preview, doc, ITEM_SIZE);
    if (!drawn) return;
    localStorage.setItem(skinKey(skinId), drawn.url);
    if (drawn.missing.length) {
      const gone = new Set((await call('project.missing', { project: doc })).files);
      const name = (p: string) => p.split(/[\\/]/).pop()!;
      const missing = { gone: drawn.missing.filter((p) => gone.has(p)).map(name), unreadable: drawn.missing.filter((p) => !gone.has(p)).map(name) };
      localStorage.setItem(`${skinKey(skinId)}.missing`, JSON.stringify(missing));
    } else localStorage.removeItem(`${skinKey(skinId)}.missing`);
    localStorage.setItem(`${skinKey(skinId)}.src`, src);
    window.dispatchEvent(new Event(THUMB_EVENT));
  } catch {
    /* a missing thumbnail only shows the weapon's own */
  }
}
