// Plain-language messages for backend error codes. Each says what happened and what to do next.
import type { BackendError } from './protocol';
import { t } from './i18n';

const MESSAGES: Record<string, string> = {
  get 'game-not-found'() { return t('errors.k1'); },
  get 'game-running'() { return t('errors.k2'); },
  get 'missing-file'() { return t('errors.k4'); },
  get 'bad-path'() { return t('errors.k5'); },
  get 'bad-mesh'() { return t('errors.k6'); },
  get 'bad-screen'() { return t('errors.k7'); },
  get 'no-glyphs'() { return t('errors.k8'); },
  get 'bad-atlas'() { return t('errors.k9'); },
  get 'no-atlas'() { return t('errors.k9'); },
  get 'empty-attachment'() { return t('errors.k10'); },
  get 'no-anchor'() { return t('errors.k11'); },
  get 'no-material'() { return t('errors.k12'); },
  get 'backend-exited'() { return t('errors.k13'); },
  get 'bad-request'() { return t('errors.k14'); },
  get 'bad-audio'() { return t('errors.k15'); },
  get 'bad-trigger'() { return t('errors.k16'); },
  get 'bad-image'() { return t('errors.k17'); },
  get 'bad-vector'() { return t('errors.k18'); },
  get 'no-gear'() { return t('errors.k19'); },
  get 'no-id'() { return t('errors.k20'); },
  get 'no-manifest'() { return t('errors.k21'); },
  get 'preview-cache'() { return t('errors.k22'); },
  get 'not-a-profile'() { return t('errors.k23'); },
  get 'ammo-display'() { return t('errors.k24'); },
  get 'no-plugin'() { return t('errors.noPlugin'); },
  get 'nothing-to-apply'() { return t('errors.nothingToApply'); },
  get 'game-changed'() { return t('errors.gameChanged'); },
  get 'stale-patch'() { return t('errors.stalePatch'); },
  get 'bad-backup'() { return t('errors.badBackup'); },
};

export function friendly(e: unknown, fallback = t('errors.k25')): string {
  const err = e as Partial<BackendError> | undefined;
  if (err?.code && MESSAGES[err.code]) return MESSAGES[err.code];
  if (err?.code === 'not-found') return t('errors.k3', { v0: err.message ?? '' });
  return fallback;
}

/** Message of an error the player may see: a mapped backend code, else the caller's fallback. Never codes or paths. */
export function detail(e: unknown): string {
  const err = e as Partial<BackendError & Error> | undefined;
  return err?.code ? `${err.code}: ${err.message}` : String(err?.message ?? e);
}
