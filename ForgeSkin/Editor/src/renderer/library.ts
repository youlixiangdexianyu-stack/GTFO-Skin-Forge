// The author's own library in the data folder (moves with it): imported stickers and tile pictures, copied to
// <data dir>/stickers/user and <data dir>/tiles, and saved palettes, listed in <data dir>/library.json. A picture taken out
// of the library stays on disk, so skins that use it keep working.
import { create } from 'zustand';
import { fileUrl, type SkinDocument } from './protocol';
import { forgetImage } from './frames';
import { layersOf } from './livery';
import { uid, useStore } from './store';
import { colorsOf, editColors, setColors } from './variants';
import { t } from './i18n';

export type LibraryKind = 'stickers' | 'tiles';
export interface LibraryImage {
  id: string;
  name: string;
  file: string;
}
export interface Palette {
  id: string;
  name: string;
  /** 0..1 RGBA, as colour fields store them. */
  colors: number[][];
}
interface Library {
  stickers: LibraryImage[];
  tiles: LibraryImage[];
  palettes: Palette[];
  /** The palette whose colours every colour field offers as swatches. */
  active: string | null;
}

export const useLibrary = create<Library & { loaded: boolean }>(() => ({ stickers: [], tiles: [], palettes: [], active: null, loaded: false }));

const dataDir = () => useStore.getState().dataDir;
const libraryFile = () => `${dataDir()}\\library.json`;
export const IMAGE_TYPES = ['png', 'jpg', 'jpeg', 'webp', 'bmp', 'gif'];

/** Reads library.json once (an absent or unreadable file is an empty library). */
export async function loadLibrary() {
  if (useLibrary.getState().loaded || !dataDir()) return;
  let lib: Partial<Library> = {};
  try {
    if (await window.forge.exists(libraryFile())) lib = await (await fetch(`${fileUrl(libraryFile())}?v=${Date.now()}`)).json();
  } catch {
    // An unreadable library starts empty; the next change writes a new one.
  }
  useLibrary.setState({ stickers: lib.stickers ?? [], tiles: lib.tiles ?? [], palettes: lib.palettes ?? [], active: lib.active ?? null, loaded: true });
}

async function save() {
  const { stickers, tiles, palettes, active } = useLibrary.getState();
  await window.forge.writeFile(libraryFile(), new TextEncoder().encode(JSON.stringify({ stickers, tiles, palettes, active }, null, 2)));
}

function change(fn: (l: Library) => Partial<Library>) {
  useLibrary.setState(fn(useLibrary.getState()));
  save().catch((e) => useStore.getState().fail(e));
}

/** Asks for pictures and copies them into the library; returns the new entries. */
export async function importImages(kind: LibraryKind): Promise<LibraryImage[]> {
  await loadLibrary();
  const files = await window.forge.openDialog('files', [{ name: t('library.images'), extensions: IMAGE_TYPES }]);
  const added: LibraryImage[] = [];
  for (const src of files) {
    const id = uid();
    const ext = src.slice(src.lastIndexOf('.') + 1).toLowerCase();
    const file = `${dataDir()}\\${kind === 'stickers' ? 'stickers\\user' : 'tiles'}\\${id}.${ext}`;
    const r = await fetch(fileUrl(src));
    if (!r.ok) {
      useStore.getState().toast('error', t('library.unreadable', { v0: src }));
      continue;
    }
    await window.forge.writeFile(file, new Uint8Array(await r.arrayBuffer()));
    forgetImage(file);
    added.push({ id, file, name: src.slice(src.lastIndexOf('\\') + 1).replace(/\.[^.]+$/, '') });
  }
  if (added.length) change((l) => ({ [kind]: [...l[kind], ...added] }));
  return added;
}

export const removeImage = (kind: LibraryKind, id: string) => change((l) => ({ [kind]: l[kind].filter((x) => x.id !== id) }));

// Palettes. A skin's colours are its livery layers' (fill, brush, decal tint, pattern, gradient and trim colours), as
// colour variants see them: applying a palette is one more colour edit, into the selected variant when one is shown.

const key = (c: number[]) =>
  c
    .slice(0, 3)
    .map((x) => Math.round((x ?? 0) * 255))
    .join(',');

/** The skin's distinct livery colours, in the order the layers use them (bottom layer first). */
export function skinColors(doc: SkinDocument): number[][] {
  const seen = new Map<string, number[]>();
  for (const m of doc.materials ?? []) for (const l of layersOf(m ?? undefined)) if (l.kind !== 'wear') for (const c of colorsOf(l)) if (!seen.has(key(c))) seen.set(key(c), c);
  return [...seen.values()];
}

/** Saves the skin's colours (at most 8) as a new palette and makes it the active one. */
export function savePalette(name: string, colors: number[][]) {
  const p: Palette = { id: uid(), name, colors: colors.slice(0, 8).map((c) => [c[0], c[1], c[2], 1]) };
  change((l) => ({ palettes: [...l.palettes, p], active: p.id }));
}

export const removePalette = (id: string) => change((l) => ({ palettes: l.palettes.filter((p) => p.id !== id), active: l.active === id ? null : l.active }));
export const setActivePalette = (id: string | null) => change(() => ({ active: id }));

/** Recolours the skin with `p`: its n-th distinct colour becomes the palette's n-th (round again past the end), alpha kept. */
export function applyPalette(p: Palette) {
  if (!p.colors.length) return;
  const s = useStore.getState();
  const order = skinColors(s.doc).map(key);
  s.edit((d) => {
    for (const m of d.materials ?? [])
      for (const l of layersOf(m ?? undefined)) {
        if (l.kind === 'wear') continue;
        editColors(d, l, (x) =>
          setColors(
            x,
            colorsOf(x).map((c) => {
              const i = order.indexOf(key(c));
              const to = i < 0 ? c : p.colors[i % p.colors.length];
              return [to[0], to[1], to[2], c[3] ?? 1];
            }),
          ),
        );
      }
  });
}

/** The active palette's colours as hex swatches for colour fields. */
export function useSwatches(): string[] | undefined {
  const colors = useLibrary((l) => l.palettes.find((p) => p.id === l.active)?.colors);
  return colors?.map(
    (c) =>
      '#' +
      c
        .slice(0, 3)
        .map((x) =>
          Math.round(Math.min(1, Math.max(0, x)) * 255)
            .toString(16)
            .padStart(2, '0'),
        )
        .join(''),
  );
}
