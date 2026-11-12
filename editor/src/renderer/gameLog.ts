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

/** A plugin warning in plain words; unknown ones keep the plugin's text. */
export function plainPluginLine(message: string): string {
  let m: RegExpMatchArray | null;
  if ((m = message.match(/^(.+?): .+?: (.+); skin not loaded$/))) return t('gameLog.notLoaded', { v0: m[1], v1: m[2] });
  if ((m = message.match(/: material (.+) matched no renderer/))) return t('gameLog.noRenderer', { v0: m[1] });
  if ((m = message.match(/: revert (.+): the game's material was destroyed/))) return t('gameLog.destroyed', { v0: m[1] });
  if ((m = message.match(/: material (.+) has no (.+); ignored$/))) return t('gameLog.noProp', { v0: m[1], v1: m[2] });
  if ((m = message.match(/^(.+): \d+x\d+ is not a multiple of 4, left uncompressed$/))) return t('gameLog.notMultiple', { v0: m[1].split(/[\\/]/).pop() });
  if ((m = message.match(/^(.+): update failed: (.+); left vanilla$/))) return t('gameLog.updateFailed', { v0: m[1], v1: m[2] });
  if ((m = message.match(/^two packs share id (.+); one is ignored$/))) return t('gameLog.sameId', { v0: m[1] });
  return message;
}

/**
 * After an apply into a running game: waits a few seconds for the plugin to load the skin, then reports it loaded or what
 * it warned about. `quiet` reports only warnings (automatic applies).
 */
export async function confirmInGame(profile: string, since: string, skinId: string, label: string, quiet = false): Promise<void> {
  const s = useStore.getState();
  for (let i = 0; i < 6; i++) {
    await new Promise((r) => setTimeout(r, 1200));
    const lines = (await call('plugin.log', { profile, since }).catch(() => ({ lines: [] as PluginLine[] }))).lines;
    // Warnings are reported by useGameWarnings as they arrive.
    if (lines.some((l) => l.level === 'warn')) return;
    if (lines.some((l) => l.message.includes(`id ${skinId},`))) {
      if (!quiet) s.toast('ok', t('gameLog.refreshed', { v0: label }));
      return;
    }
  }
  if (!quiet) s.toast('info', t('gameLog.noReply'));
}

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
    let since = new Date().toISOString();
    const poll = async () => {
      const lines = (await call('plugin.log', { profile, since }).catch(() => ({ lines: [] as PluginLine[] }))).lines;
      if (!lines.length) return;
      since = lines[lines.length - 1].time;
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
