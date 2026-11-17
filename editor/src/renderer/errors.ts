// Plain-language messages for backend error codes. Each says what happened and what to do next.
import type { BackendError } from './protocol';
import { t } from './i18n';

const MESSAGES: Record<string, string> = {
  get 'game-not-found'() {
    return t('errors.k1');
  },
  get 'game-running'() {
    return t('errors.k2');
  },
  get 'missing-file'() {
    return t('errors.k4');
  },
  get 'bad-mesh'() {
    return t('errors.k6');
  },
  get 'bad-screen'() {
    return t('errors.k7');
  },
  get 'no-glyphs'() {
    return t('errors.k8');
  },
  get 'bad-atlas'() {
    return t('errors.badAtlas');
  },
  get 'no-atlas'() {
    return t('errors.noAtlas');
  },
  get 'empty-attachment'() {
    return t('errors.k10');
  },
  get 'no-anchor'() {
    return t('errors.k11');
  },
  get 'no-material'() {
    return t('errors.k12');
  },
  get 'backend-exited'() {
    return t('errors.k13');
  },
  get 'bad-request'() {
    return t('errors.k14');
  },
  get 'bad-trigger'() {
    return t('errors.k16');
  },
  get 'bad-image'() {
    return t('errors.k17');
  },
  get 'bad-vector'() {
    return t('errors.k18');
  },
  get 'no-gear'() {
    return t('errors.k19');
  },
  get 'no-id'() {
    return t('errors.k20');
  },
  get 'no-manifest'() {
    return t('errors.k21');
  },
  get 'preview-cache'() {
    return t('errors.k22');
  },
  get 'not-a-profile'() {
    return t('errors.k23');
  },
  get 'ammo-display'() {
    return t('errors.k24');
  },
  get 'nothing-to-apply'() {
    return t('errors.nothingToApply');
  },
  get 'game-changed'() {
    return t('errors.gameChanged');
  },
  get 'stale-patch'() {
    return t('errors.stalePatch');
  },
  get 'bad-backup'() {
    return t('errors.badBackup');
  },
  get 'no-livery-view'() {
    return t('errors.noLiveryView');
  },
  get 'duplicate-material'() {
    return t('errors.duplicateMaterial');
  },
  get 'bad-material-target'() {
    return t('errors.badMaterialTarget');
  },
  get 'file-in-use'() {
    return t('errors.fileInUse');
  },
  get 'settings-unreadable'() {
    return t('errors.settingsUnreadable');
  },
  get 'damaged-game-file'() {
    return t('errors.damagedGameFile');
  },
  get 'unsupported-file'() {
    return t('errors.unsupportedFile');
  },
  get 'work-in-use'() {
    return t('errors.workInUse');
  },
};

export function friendly(e: unknown, fallback = t('errors.k25')): string {
  const err = e as Partial<BackendError> | undefined;
  // A window newer than its backend (the editor was updated while open) asks for something the running backend does not know.
  if (err?.code === 'bad-request' && err.message?.startsWith('Unknown method')) return t('errors.restartEditor');
  // An unreadable picture is named when the message holds its path (the backend's and frames.ts loadImage's do).
  const image = err?.code === 'bad-image' && /[A-Za-z]:\\[^:]*?\.[A-Za-z0-9]{2,4}(?=$|:|\s)/.exec(err.message ?? '')?.[0];
  if (image) return t('errors.badImageFile', { v0: image });
  if (err?.code && MESSAGES[err.code]) return MESSAGES[err.code];
  // The development editor's plugin build (actions.ts installPluginInto) says itself what went wrong.
  if (err?.code === 'dev-plugin') return err.message ?? fallback;
  // A missing file is named by its path alone: .NET says "Could not find file 'C:\…'." (or "a part of the path"), the
  // backend "{path} not found" / "missing" / "does not exist".
  const missing = err?.code === 'not-found' && /'([A-Za-z]:\\[^']*)'|^(?:Folder )?([A-Za-z]:\\.*) (?:not found|missing|does not exist)$/.exec(err.message ?? '');
  if (missing) return t('errors.k3', { v0: missing[1] ?? missing[2] });
  if (err?.code === 'not-found') return t('errors.k3', { v0: err.message ?? '' });
  // The backend's no-plugin message is the plugin files it looked for and did not find.
  if (err?.code === 'no-plugin') return t('errors.noPlugin', { v0: err.message ?? '' });
  // The bake's errors carry the layer's name.
  if (err?.code === 'empty-layer') return t('errors.emptyLayer', { v0: err.message ?? '' });
  if (err?.code === 'no-base-texture') return t('errors.noBaseTexture', { v0: err.message ?? '' });
  // The backend's bad-audio message starts with the sound's name.
  // The backend's bad-path message starts with the path; only its last part, the name the author chose, is shown.
  if (err?.code === 'bad-path') {
    const path = (err.message ?? '').split(': illegal path')[0];
    return t('errors.k5', { v0: path.split(/[\\/]/).pop() ?? '' });
  }
  if (err?.code === 'bad-audio') return t('errors.k15', { v0: (err.message ?? '').split(': ')[0] });
  return fallback;
}

/** Message of an error the player may see: a mapped backend code, else the caller's fallback. Never codes or paths. */
export function detail(e: unknown): string {
  const err = e as Partial<BackendError & Error> | undefined;
  return err?.code ? `${err.code}: ${err.message}` : String(err?.message ?? e);
}
