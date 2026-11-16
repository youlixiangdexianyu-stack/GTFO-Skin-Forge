// The chosen game profile as one state: which profile, what is installed in it and which of those the game shows, whether the game
// runs and the plugin is in. The work list, the apply button and the status bar read only this; useProfileSync keeps it current
// (profile change, a change in the profile's skins folder or the plugin's choices, the window getting focus).
import { useEffect } from 'react';
import { logDropped } from './actions';
import { characterPicture, ensureCharacterPicture, notePackPicture, refreshCharacters } from './character/model';
import { call, fileUrl, type InstalledPack, type Methods, type Profile, type ProjectEntry } from './protocol';
import { autosaveRoot, useStore } from './store';
import { t } from './i18n';

/** The profile 应用 goes to. */
export const useProfile = (): Profile | undefined => useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));

/** A work's state in the chosen profile: in use in game, installed, or not in it; `changed`: edited after it was installed. */
export interface WorkStatus {
  pack?: InstalledPack;
  state: 'inUse' | 'installed' | null;
  changed: boolean;
}

/** `edited`: when the work was last changed (ISO), the open one's from the live edit. */
export function statusOf(installed: InstalledPack[], id: string, edited: string | null | undefined): WorkStatus {
  const pack = installed.find((p) => p.id === id && !p.error);
  if (!pack) return { state: null, changed: false };
  return { pack, state: pack.inUse ? 'inUse' : 'installed', changed: !!edited && !!pack.modified && edited > pack.modified };
}

/** When a work was last changed: the open weapon skin's or character skin's from this session's edits, else what its file keeps. */
export function useEdited(entry: ProjectEntry): string | null {
  const live = useStore((s) => (s.doc.id === entry.id && s.editedAt ? new Date(s.editedAt).toISOString() : null));
  return live ?? entry.edited ?? null;
}

/** Keeps the profile state current: on a profile change, when its skins folder or the plugin's choices change, and on focus. */
export function useProfileSync() {
  const path = useStore((s) => s.prefs.profilePath);
  const hasBepInEx = useProfile()?.hasBepInEx;
  useEffect(() => {
    void useStore.getState().refreshInstalled();
    void window.forge.watchProfile(hasBepInEx ? path : null);
  }, [path, hasBepInEx]);
  useEffect(() => {
    const off = window.forge.onProfileChanged(() => void useStore.getState().refreshInstalled());
    const focus = () => void useStore.getState().refreshProfiles();
    window.addEventListener('focus', focus);
    return () => {
      off();
      window.removeEventListener('focus', focus);
    };
  }, []);
}

/** Installs skin packs someone gave the author (.forgeskin, several at once) into the chosen profile, not made the used ones. */
export async function addPacks() {
  const s = useStore.getState();
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  if (!profile) return;
  const packs = await window.forge.openDialog('files', [{ name: t('actions.k16'), extensions: ['forgeskin'] }], 'addPacks');
  if (!packs.length) return;
  let done = 0;
  for (const pack of packs) {
    try {
      logDropped(pack, await call('pack.install', { pack, profile: profile.path, activate: false }));
      done++;
    } catch (e) {
      s.fail(e);
    }
  }
  await s.refreshInstalled();
  if (done) s.toast('ok', t('works.packsAdded', { v0: done, v1: profile.name }));
}

/** Whether an installed pack can become one of the author's works: a gear or character skin that reads. */
export const adoptable = (p: InstalledPack) => !!p.weapon && !p.error;

/** 「搬到我的作品」: installed packs of others made into the author's works, the packs left installed (a name a work of that gear
 * already has gets a number); what the packs no longer hold (Backend Projects.Adopt) is said once in the toast. Nothing opens. */
export async function adoptPacks(packs: InstalledPack[]) {
  const s = useStore.getState();
  const lost = new Set<Methods['pack.adopt'][1]['lost'][number]>();
  const taken = (weapon: string) => (weapon === 'character' ? s.characters : (s.works[weapon] ?? [])).map((k) => k.name);
  const adopted: Record<string, string[]> = {};
  let done = 0;
  for (const p of packs.filter(adoptable)) {
    const weapon = p.weapon!;
    try {
      const r = await call('pack.adopt', { pack: p.file, dir: `${autosaveRoot()}\\${weapon}`, taken: [...taken(weapon), ...(adopted[weapon] ?? [])] });
      (adopted[weapon] ??= []).push(r.name);
      // A character work's card shows the pack's own picture, else one drawn in the background from its model.
      if (r.picture) {
        await window.forge.writeFile(characterPicture(p.id), new Uint8Array(await (await fetch(fileUrl(r.picture))).arrayBuffer()));
        notePackPicture(p.id);
      } else if (weapon === 'character') ensureCharacterPicture({ id: p.id, path: r.path });
      r.lost.forEach((x) => lost.add(x));
      done++;
    } catch (e) {
      s.fail(e);
    }
  }
  await Promise.all([s.refreshWorks(), s.refreshSkins(), s.refreshInstalled(), refreshCharacters()]);
  if (!done) return;
  const missing = [...lost].map((x) => (x === 'editor' ? t('works.lostEditor') : t(`works.lost.${x}` as const)));
  s.toast('ok', [t('works.adopted', { v0: done }), ...missing].join(' '));
}

/** Takes a skin out of the chosen profile: its pack goes to the Recycle Bin. */
export async function removeFromProfile(id: string, name: string) {
  const s = useStore.getState();
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  if (!profile) return;
  try {
    await call('pack.remove', { profile: profile.path, skinId: id });
    await s.refreshInstalled();
    s.toast('ok', t('works.removed', { v0: name, v1: profile.name }));
  } catch (e) {
    s.fail(e);
  }
}
