// Code effects in the document (SkinDocument.codeEffects): adding, editing and removing them, and keeping each one's target valid
// after every edit (store.edit runs syncCodeEffects): an effect on a material needs that material's override in the skin (the plugin
// paints into the skin's copy of it), an effect on an attachment needs the attachment and its model, or it goes.
import type { CodeEffect, SkinDocument } from './protocol';
import { ensureMaterial, uid, useStore } from './store';
import { t } from './i18n';

/** At most this many code effects in one skin (Core CodeEffects.MaxPerSkin). */
export const MAX_CODE_EFFECTS = 8;

export function syncCodeEffects(d: SkinDocument) {
  if (!d.codeEffects?.length) return;
  const keep = d.codeEffects.filter((e) => !!e && (e.material ? true : !!d.attachments?.find((a) => a?.id === e.attachment)?.mesh));
  if (keep.length !== d.codeEffects.length) d.codeEffects = keep.length ? keep : null;
  for (const e of keep) if (e?.material && !d.materials?.some((m) => m?.material === e.material)) ensureMaterial(d, e.material);
}

/** A new effect (the flow-lines program at its defaults) on a material of `part` or on an attachment's model; it opens. */
export function addCodeEffect(target: { material: string; part: string } | { attachment: string }) {
  const { doc, edit, select, toast } = useStore.getState();
  if ((doc.codeEffects?.length ?? 0) >= MAX_CODE_EFFECTS) return toast('error', t('codeFx.tooMany', { v0: MAX_CODE_EFFECTS }));
  const id = uid();
  edit((d) => {
    const e: CodeEffect = { id, name: t('codeFx.newName', { v0: (d.codeEffects?.length ?? 0) + 1 }), program: 'flowLines', params: {}, colors: {} };
    if ('material' in target) {
      ensureMaterial(d, target.material, target.part);
      Object.assign(e, { material: target.material, channel: 'albedo' });
    } else e.attachment = target.attachment;
    (d.codeEffects ??= []).push(e);
  });
  select({ kind: 'fx', id });
}

export function updateCodeEffect(id: string, fn: (e: CodeEffect) => void, coalesceKey?: string) {
  useStore.getState().edit((d) => {
    const e = d.codeEffects?.find((x) => x?.id === id);
    if (e) fn(e);
  }, coalesceKey && `fx-${id}-${coalesceKey}`);
}

export function removeCodeEffect(id: string) {
  const { edit, select } = useStore.getState();
  edit((d) => void (d.codeEffects = d.codeEffects?.filter((x) => x?.id !== id) ?? null));
  select(null);
}
