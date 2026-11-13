// The skin plugin's own log (forgeskin.log in the test profile, cleared at each game launch): reading what the game did
// after 应用到游戏, in plain words.
import { useEffect } from 'react';
import { call } from './protocol';
import { useStore } from './store';
import { t } from './i18n';

export interface PluginLine {
  time: string;
  level: 'info' | 'warn';
  message: string;
}

/**
 * Reads forgeskin.log on from where the previous read stopped: each read returns only the lines written since (all of them again,
 * with `reset`, after the game was started again and cleared the log), and only those after `since`. A read while the previous one
 * is still running returns null.
 */
export function pluginLogReader(profile: string, since?: string) {
  let from = 0;
  let head = '';
  let busy = false;
  return async (): Promise<{ lines: PluginLine[]; reset: boolean } | null> => {
    if (busy) return null;
    busy = true;
    try {
      const r = await call('plugin.log', { profile, since, from, head });
      from = r.next;
      head = r.head;
      return r;
    } finally {
      busy = false;
    }
  };
}

/** Material settings the plugin and the game-files writer name in their warnings, as the inspector labels them. */
export const PROP: Record<string, () => string> = {
  metallic: () => t('skinInspector.k22'),
  smoothness: () => t('skinInspector.k23'),
  tint: () => t('skinInspector.k24'),
  'emissive colour': () => t('skinInspector.k13'),
};

/** The plugin's gear label ("Raptus Steigro [1P]", "… [pickup item:12]") as "Raptus Steigro（第一人称）". */
function gear(label: string): string {
  const m = label.match(/^(.+?) \[(1P|3P|placed|menu|pickup)\b[^\]]*\]$/);
  return m ? `${m[1]}（${t(`gameLog.view.${m[2] as '1P' | '3P' | 'placed' | 'menu' | 'pickup'}`)}）` : label;
}

/** A plugin warning in plain words; unknown ones keep the plugin's text. */
export function plainPluginLine(message: string): string {
  let m: RegExpMatchArray | null;
  if ((m = message.match(/^(.+?): bad-audio: sound (.+?): .*; skin not loaded$/))) return t('gameLog.badAudio', { v0: m[1], v1: m[2] });
  if ((m = message.match(/^(.+?): .+?: (.+); skin not loaded$/))) return t('gameLog.notLoaded', { v0: m[1], v1: m[2] });
  if ((m = message.match(/: material (.+) matched no renderer/))) return t('gameLog.noRenderer', { v0: m[1] });
  if ((m = message.match(/: revert (.+): the game's material was destroyed/))) return t('gameLog.destroyed', { v0: m[1] });
  if ((m = message.match(/^(.+): \d+x\d+ is not a multiple of 4, left uncompressed$/))) return t('gameLog.notMultiple', { v0: m[1].split(/[\\/]/).pop() });
  if ((m = message.match(/^(.+): update failed: (.+); left vanilla$/))) return t('gameLog.updateFailed', { v0: gear(m[1]), v1: m[2] });
  if ((m = message.match(/^(.+): active skin (.+) is not installed; left vanilla$/))) return t('gameLog.notInstalled', { v0: gear(m[1]), v1: m[2] });
  if ((m = message.match(/^(.+\]): (.+); left vanilla$/))) return t('gameLog.leftVanilla', { v0: gear(m[1]), v1: m[2] });
  if ((m = message.match(/^(.+\]): code effect \S+ \((.*)\): skipped, (.+)$/))) return t('gameLog.fxSkipped', { v0: gear(m[1]), v1: m[2], v2: m[3] });
  if ((m = message.match(/^two packs share id (.+); one is ignored$/))) return t('gameLog.sameId', { v0: m[1] });
  if ((m = message.match(/^.+?: active\.json now picks (.+), but the in-game picker's choice \((.+)\) goes first; /))) return t('gameLog.pickedFirst', { v0: m[1], v1: m[2] });
  return message;
}

/**
 * After an apply into a running game: waits a few seconds for the plugin to load the skin, then reports it loaded or what
 * it warned about. Only this skin's warnings count: a reload re-applies every installed skin, and another skin's lines are
 * not this apply's. `quiet` reports only warnings (automatic applies).
 */
export async function confirmInGame(profile: string, since: string, skinId: string, label: string, quiet = false): Promise<void> {
  const s = useStore.getState();
  const read = pluginLogReader(profile, since);
  const seen: PluginLine[] = [];
  // The gears that took this skin ("<gear label>: gear:14, skin <id>, …"); their warnings are this skin's.
  const gears = () => new Set(seen.flatMap((l) => l.message.match(new RegExp(`^(.+\\]): \\S+, skin ${escape(skinId)},`))?.[1] ?? []));
  const report = () => {
    const mine = gears();
    const warns = seen
      .filter((l) => l.level === 'warn' && (l.message.includes(skinId) || [...mine].some((g) => l.message.startsWith(`${g}: `))))
      .map((l) => plainPluginLine(l.message));
    if (!warns.length) return false;
    // Warnings are reported by useGameWarnings as they arrive, which reads the game only in 开发者模式; otherwise here.
    if (!s.prefs.developer) {
      for (const w of new Set(warns)) s.log(`${t('gameLog.warn')}: ${w}`);
      s.toast('error', `${t('gameLog.warned')} ${[...new Set(warns)].join(' · ')}`);
    }
    return true;
  };
  for (let i = 0; i < 6; i++) {
    await new Promise((r) => setTimeout(r, 1200));
    // Only the lines new since the previous read: the earlier ones were looked at then.
    seen.push(...((await read().catch(() => null))?.lines ?? []));
    if (seen.some((l) => l.message.includes(`skin ${skinId},`))) {
      if (!report() && !quiet) s.toast('ok', t('gameLog.refreshed', { v0: label }));
      return;
    }
  }
  // Read but worn by no gear in the game now.
  if (report() || quiet) return;
  if (seen.some((l) => l.message.includes(`id ${skinId},`))) s.toast('ok', t('gameLog.refreshed', { v0: label }));
  else s.toast('info', t('gameLog.noReply'));
}

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/**
 * While the game runs, every plugin warning (a part left vanilla, a material destroyed, a skin not loaded) goes into the
 * log panel and pops up as an alert as it happens: after an apply, a hot reload or a re-equip in game. A 开发者模式 tool:
 * off, the game log is not read.
 */
export function useGameWarnings() {
  const profile = useStore((s) => s.prefs.profilePath);
  const running = useStore((s) => !!s.game?.running);
  const developer = useStore((s) => s.prefs.developer);
  useEffect(() => {
    if (!running || !profile || !developer) return;
    const read = pluginLogReader(profile, new Date().toISOString());
    const poll = async () => {
      const lines = (await read().catch(() => null))?.lines ?? [];
      if (!lines.length) return;
      const warns = lines.filter((l) => l.level === 'warn').map((l) => plainPluginLine(l.message));
      if (!warns.length) return;
      const s = useStore.getState();
      for (const w of warns) s.log(`${t('gameLog.warn')}: ${w}`);
      s.toast('error', `${t('gameLog.warned')} ${[...new Set(warns)].join(' · ')}`);
    };
    const id = setInterval(() => void poll(), 3000);
    return () => clearInterval(id);
  }, [running, profile, developer]);
}
