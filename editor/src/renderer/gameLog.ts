// The skin plugin's own log (forgeskin.log in the test profile, cleared at each game launch): reading what the game did
// after 应用到游戏, in plain words.
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
    const warns = lines.filter((l) => l.level === 'warn');
    if (warns.length) {
      s.toast('error', `${t('gameLog.warned')} ${warns.map((w) => plainPluginLine(w.message)).join(' · ')}`);
      return;
    }
    if (lines.some((l) => l.message.includes(`id ${skinId},`))) {
      if (!quiet) s.toast('ok', t('gameLog.refreshed', { v0: label }));
      return;
    }
  }
  if (!quiet) s.toast('info', t('gameLog.noReply'));
}
