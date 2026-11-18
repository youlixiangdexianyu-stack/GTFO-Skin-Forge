// The editor's data folder (autosaved skins, caches, packs, logs, settings): the one place every part of the app asks.
import { app } from 'electron';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { cp, readdir, rm, stat } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path';

/** dev: run from a checkout; installed: Squirrel runs it from `<install>/app-<version>/`; portable: anywhere else (the zip). */
export const edition: 'dev' | 'installed' | 'portable' = !app.isPackaged ? 'dev' : /^app-\d/.test(basename(dirname(app.getPath('exe')))) ? 'installed' : 'portable';

const defaultDir = () =>
  edition === 'dev' ? join(app.getAppPath(), 'GTFO_UserData') : edition === 'installed' ? join(app.getPath('documents'), 'ForgeSkin') : join(dirname(app.getPath('exe')), 'GTFO_UserData');

// The folder the user chose and the folders the data was moved out of (Backend formerDirs), never in the data folder
// itself: the installed copy keeps them in its Electron profile, the portable copy beside its exe (a folder inside the exe's
// folder as a relative path), so a portable folder copied elsewhere still finds its data. A development run always uses its
// checkout's folder.
const exeDir = () => dirname(app.getPath('exe'));
const prefFile = () => (edition === 'portable' ? join(exeDir(), 'data-folder.json') : join(app.getPath('userData'), 'data-folder.json'));
type Prefs = { dir?: string; former?: string[] };
function prefs(): Prefs {
  if (edition === 'dev') return {};
  try {
    return JSON.parse(readFileSync(prefFile(), 'utf8')) as Prefs;
  } catch {
    return {};
  }
}
const stored = (dir: string) => {
  const rel = relative(exeDir(), dir);
  return edition === 'portable' && !rel.startsWith('..') && !isAbsolute(rel) ? rel : dir;
};
const read = (dir: string) => resolve(exeDir(), dir);

let current: string | null = null;
export function dataDir(): string {
  const dir = prefs().dir;
  return (current ??= dir ? read(dir) : defaultDir());
}

/** Where the data was before it moved here, for the backend to find files a project names there. */
export const formerDirs = (): string[] => (prefs().former ?? []).map(read);

function choose(dir: string, from?: string) {
  const old = prefs();
  const former = [...new Set([...(old.former ?? []), ...(from ? [stored(from)] : [])])].filter((f) => relative(read(f), dir) !== '');
  mkdirSync(dirname(prefFile()), { recursive: true });
  writeFileSync(prefFile(), JSON.stringify({ dir: stored(dir), former }, null, 2));
  current = dir;
}

/** A folder the backend has used: it writes settings.json and logs/backend.log, the editor autosave/. */
const isData = (dir: string) => ['settings.json', 'autosave', join('logs', 'backend.log')].some((f) => existsSync(join(dir, f)));

/** Every file under `dir` with its size, by path relative to `dir`. */
async function files(dir: string): Promise<Map<string, number>> {
  const out = new Map<string, number>();
  for (const e of await readdir(dir, { recursive: true, withFileTypes: true }))
    if (e.isFile()) {
      const p = join(e.parentPath, e.name);
      out.set(relative(dir, p), (await stat(p)).size);
    }
  return out;
}

export type MoveResult = { ok: true; adopted?: boolean; leftover?: string } | { ok: false; reason: 'same' | 'nested' | 'notEmpty' | 'failed'; detail?: string };

/**
 * Makes `to` the data folder. The data is copied there, every file is checked to have arrived with its size, and only
 * then is the old folder deleted; on any failure the copy is removed and the old folder stays in use, untouched. A
 * folder that already holds ForgeSkin data is taken as it is when `adopt` (the old one is left alone); any other
 * non-empty folder is refused. The caller stops whatever writes into the data folder first.
 */
export async function moveData(to: string, adopt: boolean): Promise<MoveResult> {
  const from = resolve(dataDir());
  to = resolve(to);
  if (!relative(from, to)) return { ok: false, reason: 'same' };
  const inside = (a: string, b: string) => !relative(a, b).startsWith('..') && !isAbsolute(relative(a, b));
  if (inside(from, to) || inside(to, from)) return { ok: false, reason: 'nested' };
  const existed = existsSync(to);
  const before = existed ? await readdir(to).catch(() => null) : [];
  if (!before) return { ok: false, reason: 'failed', detail: `cannot read ${to}` };
  if (before.length) {
    if (!adopt || !isData(to)) return { ok: false, reason: 'notEmpty' };
    choose(to);
    return { ok: true, adopted: true };
  }
  if (!existsSync(from)) {
    choose(to);
    return { ok: true };
  }
  try {
    await cp(from, to, { recursive: true, preserveTimestamps: true }); // `to` is empty or absent
    const [a, b] = await Promise.all([files(from), files(to)]);
    const wrong = [...a].find(([p, size]) => b.get(p) !== size);
    if (wrong || a.size !== b.size) throw new Error(`copy check failed: ${wrong?.[0] ?? `${a.size} files, copied ${b.size}`}`);
    choose(to, from);
  } catch (e) {
    // `to` was empty or absent: what is in it now is this copy.
    if (existed) for (const f of await readdir(to).catch(() => [])) await rm(join(to, f), { recursive: true, force: true }).catch(() => {});
    else await rm(to, { recursive: true, force: true }).catch(() => {});
    return { ok: false, reason: 'failed', detail: e instanceof Error ? e.message : String(e) };
  }
  const left = await rm(from, { recursive: true, force: true, maxRetries: 3 }).then(
    () => undefined,
    () => from,
  );
  return { ok: true, leftover: left };
}

/**
 * An installed copy up to 1.1 kept its data in `<install>/GTFO_UserData`. The first start of a newer one moves it to the
 * data folder the same way; when that fails the old folder stays the data folder. Once moved there is nothing left to move.
 */
export async function moveInstallData(log: (line: string) => void): Promise<void> {
  if (edition !== 'installed') return;
  const old = join(dirname(dirname(app.getPath('exe'))), 'GTFO_UserData');
  if (!existsSync(old) || !relative(resolve(old), resolve(dataDir()))) return;
  const target = dataDir();
  current = old; // moveData moves from the current folder
  const r = await moveData(target, false);
  if (r.ok) log(`moved ${old} to ${target}${r.leftover ? `; could not delete all of ${r.leftover}` : ''}`);
  else {
    choose(old);
    log(`kept ${old}: moving to ${target} failed (${r.reason}${r.detail ? `: ${r.detail}` : ''})`);
  }
}
