// Colour variants of a skin (SkinDocument.variants): each gives chosen livery layers other colours. The selected variant
// (the document's editor state) is what the 3D view shows and what colour edits write; null is the skin's own colours.
// Every variant is built as a skin of its own, so the game's skin picker lists them.
import type { LiveryLayer, SkinDocument } from './protocol';
import { uid, useStore } from './store';
import { t } from './i18n';

export type ColorVariant = NonNullable<NonNullable<SkinDocument['variants']>[number]>;

/** The variant the editor shows, if one is selected. */
export function activeVariant(doc: SkinDocument): ColorVariant | undefined {
  const id = (doc.editor as { variant?: string } | null | undefined)?.variant;
  return id ? doc.variants?.find((v) => v?.id === id) ?? undefined : undefined;
}

/** A layer's colours as a variant stores them: a pattern's list, else its one colour. */
export const colorsOf = (l: LiveryLayer): number[][] => (l.kind === 'pattern' ? (l.colors ?? []).map((c) => [...(c ?? [])]) : [[...(l.color ?? [1, 1, 1, 1])]]);

function setColors(l: LiveryLayer, colors: number[][]) {
  if (l.kind === 'pattern') l.colors = colors;
  else if (colors[0]) l.color = colors[0];
}

/** `l` with the colours `v` gives it (the same object when it gives none). */
export function layerIn(l: LiveryLayer, v: ColorVariant | undefined): LiveryLayer {
  const colors = v?.colors?.[l.id ?? ''];
  if (!colors) return l;
  const out = { ...l };
  setColors(out, colors.map((c) => [...(c ?? [])]));
  return out;
}

/** The document as variant `v` looks (its colours on the livery layers); `doc` itself when there is no variant. */
export function withVariant(doc: SkinDocument, v = activeVariant(doc)): SkinDocument {
  if (!v) return doc;
  return { ...doc, materials: (doc.materials ?? []).map((m) => (m?.livery ? { ...m, livery: m.livery.map((l) => l && layerIn(l, v)) } : m)) };
}

/** Inside an edit of `d`: runs `fn` on layer `l`; with a variant selected, colour changes go to the variant and the layer keeps its own. */
export function editColors(d: SkinDocument, l: LiveryLayer, fn: (l: LiveryLayer) => void) {
  const v = activeVariant(d);
  if (!v) return fn(l);
  const own = colorsOf(l);
  // `fn` edits a copy: `own` is put back afterwards.
  setColors(l, ((v.colors?.[l.id ?? ''] as number[][] | undefined) ?? own).map((c) => [...(c ?? [])]));
  fn(l);
  const now = colorsOf(l);
  setColors(l, own);
  if (JSON.stringify(now) !== JSON.stringify(v.colors?.[l.id ?? ''] ?? own)) (v.colors ??= {})[l.id ?? ''] = now;
}

/** Every build of a skin: its own colours, then each variant (id and name extended). */
export function variantDocs(doc: SkinDocument): SkinDocument[] {
  return [doc, ...(doc.variants ?? []).filter((v): v is ColorVariant => !!v).map((v) => ({ ...withVariant(doc, v), id: `${doc.id}~${v.id}`, name: `${doc.name} · ${v.name}` }))];
}

/** The build of the variant the editor shows. */
export function shownDoc(doc: SkinDocument): SkinDocument {
  const v = activeVariant(doc);
  return v ? variantDocs(doc).find((d) => d.id === `${doc.id}~${v.id}`)! : doc;
}

export function selectVariant(id: string | null) {
  useStore.getState().edit((d) => void (d.editor = { ...(d.editor ?? {}), variant: id }));
}

/** A new variant with the colours shown now, selected. */
export function addVariant() {
  const id = uid();
  useStore.getState().edit((d) => {
    const from = activeVariant(d);
    d.variants ??= [];
    d.variants.push({ id, name: t('variants.newName', { v0: d.variants.length + 1 }), colors: structuredClone(from?.colors ?? {}) });
    d.editor = { ...(d.editor ?? {}), variant: id };
  });
}

export function renameVariant(id: string, name: string) {
  useStore.getState().edit((d) => {
    const v = d.variants?.find((x) => x?.id === id);
    if (v) v.name = name;
  }, `variant-name-${id}`);
}

export function removeVariant(id: string) {
  useStore.getState().edit((d) => {
    d.variants = (d.variants ?? []).filter((v) => v?.id !== id);
    if (activeVariant(d) === undefined) d.editor = { ...(d.editor ?? {}), variant: null };
  });
}
