import { app, autoUpdater, BrowserWindow, dialog, ipcMain, net, protocol, shell, type OpenDialogOptions } from 'electron';
import { appendFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, statSync, watch, writeFileSync, type FSWatcher } from 'node:fs';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, normalize, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import squirrelStartup from 'electron-squirrel-startup';
import { updateElectronApp, UpdateSourceType } from 'update-electron-app';
import { Backend } from './backend';
import { dataDir, edition, moveData, moveInstallData } from './dataDir';

// The installer runs the app once per install, update and uninstall to make or remove its shortcuts; it quits then. The
// portable copy has no installer.
const squirrelRun = edition === 'installed' && squirrelStartup;
if (squirrelRun) app.quit();

// Moves the pre-rename Electron profile (GTFO Skin Forge -> ForgeSkin: preferences, panel layout, last weapon) to the
// new app name before Chromium opens it; once moved there is nothing left to move. Electron has already created the new
// folder by now, so the old one is copied in and then removed.
function movePreRenameUserData(): void {
  const old = join(app.getPath('appData'), 'GTFO Skin Forge');
  const now = app.getPath('userData');
  if (!existsSync(old) || existsSync(join(now, 'Local Storage'))) return;
  cpSync(old, now, { recursive: true });
  rmSync(old, { recursive: true, force: true });
}
movePreRenameUserData();

// `--ai-control[=<port>]` (default 9333) opens the Chromium DevTools protocol on 127.0.0.1 so an AI agent can drive the
// window, and the renderer then offers window.forgeAI (renderer/aiControl.ts); without it no debugging port is open. The
// environment variable FORGESKIN_AI_CONTROL (a port, or 1 for the default) does the same, for launches that cannot pass
// arguments (the Start-menu shortcut). It has to be appended before the app is ready.
// File dialogs stay the system's; an agent answers them beforehand (see pickPaths).
const aiControl =
  process.argv.find((a) => a === '--ai-control' || a.startsWith('--ai-control=')) ??
  (process.env.FORGESKIN_AI_CONTROL ? `--ai-control=${process.env.FORGESKIN_AI_CONTROL === '1' ? 9333 : process.env.FORGESKIN_AI_CONTROL}` : undefined);
let aiControlOn = false;
if (aiControl) {
  const port = aiControl === '--ai-control' ? 9333 : Number(aiControl.slice('--ai-control='.length));
  if (Number.isInteger(port) && port >= 1024 && port <= 65535) {
    app.commandLine.appendSwitch('remote-debugging-address', '127.0.0.1');
    app.commandLine.appendSwitch('remote-debugging-port', String(port));
    // A window behind others counts as hidden and Chromium then stops animation frames, so menus never open: keep it live.
    app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion');
    app.commandLine.appendSwitch('disable-renderer-backgrounding');
    aiControlOn = true;
  } else appLog('ai-control', `ignored ${aiControl}: the port must be an integer from 1024 to 65535`);
}

// Chromium's HTTP disk cache only ever holds what comes over http (a development run's modules): the app's own files are
// file:// and sf:// and never cached. Kept small.
app.commandLine.appendSwitch('disk-cache-size', String(32 << 20));

/**
 * V8's code cache keeps the compiled scripts of every app version (and development build): deleted when the version
 * changes or it passes 128 MB, before Chromium opens it (the session starts when the app is ready).
 */
function trimCodeCache(): void {
  const mark = join(app.getPath('userData'), 'code-cache-version');
  const dir = join(app.getPath('userData'), 'Code Cache');
  const files = existsSync(dir) ? readdirSync(dir, { recursive: true, withFileTypes: true }).filter((e) => e.isFile()) : [];
  const size = files.reduce((sum, e) => sum + statSync(join(e.parentPath, e.name)).size, 0);
  if (existsSync(mark) && readFileSync(mark, 'utf8') === app.getVersion() && size <= 128 << 20) return;
  rmSync(dir, { recursive: true, force: true });
  writeFileSync(mark, app.getVersion());
}
try {
  trimCodeCache();
} catch (e) {
  appLog('code cache', e);
}

// Local files (preview GLB, textures, videos) are served to the renderer as sf://local/<abs path>, never kept in the HTTP
// cache (the backend's own cache already holds them).
protocol.registerSchemesAsPrivileged([{ scheme: 'sf', privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true, corsEnabled: true } }]);

let win: BrowserWindow | null = null;
let quitting = false;
// The backend is stopped on purpose while the data folder moves.
let moving = false;
let startedAt = 0;
let crashes = 0;
// The window can be gone while the app shuts down (the backend exits after it).
const send = (channel: string, ...args: unknown[]) => {
  if (win && !win.isDestroyed()) win.webContents.send(channel, ...args);
};

/** Crash and error evidence: one line per event in GTFO_UserData/logs/app.log, written synchronously so it survives the crash. */
function appLog(kind: string, detail: unknown) {
  const text =
    detail instanceof Error
      ? `${detail.message}
${detail.stack}`
      : typeof detail === 'string'
        ? detail
        : JSON.stringify(detail);
  try {
    const dir = join(dataDir(), 'logs');
    mkdirSync(dir, { recursive: true });
    // Past 2 MB the log moves to app.1.log (the older ones to .2 and .3, the oldest dropped), as the backend's backend.log.
    const file = join(dir, 'app.log');
    if ((statSync(file, { throwIfNoEntry: false })?.size ?? 0) > 2 << 20)
      for (let i = 3; i >= 1; i--) {
        const from = i === 1 ? file : join(dir, `app.${i - 1}.log`);
        if (existsSync(from)) renameSync(from, join(dir, `app.${i}.log`));
      }
    appendFileSync(
      file,
      `${new Date().toISOString()} [${kind}] ${text}
`,
    );
  } catch {
    // nowhere left to report it
  }
}

process.on('uncaughtException', (e) => appLog('main uncaughtException', e));
process.on('unhandledRejection', (e) => appLog('main unhandledRejection', e));
app.on('child-process-gone', (_e, d) => appLog('child-process-gone', d));

const backend = new Backend(
  (event, data) => send('backend:event', event, data),
  (line) => send('backend:log', line),
  (code) => {
    send('backend:log', `backend exited (${code})`);
    if (quitting || moving) return;
    // Restart, but give up after repeated crashes right after start (shown to the user as "down").
    crashes = Date.now() - startedAt < 10_000 ? crashes + 1 : 0;
    if (crashes >= 3) return send('backend:status', 'down');
    send('backend:status', 'restarting');
    setTimeout(startBackend, 1000);
  },
);

function startBackend() {
  startedAt = Date.now();
  // Ready only once the process exists; an exe that is missing never reports ready.
  backend.start(() => send('backend:status', 'ready'));
}

function createWindow() {
  win = new BrowserWindow({
    width: 1600,
    height: 960,
    minWidth: 1180,
    minHeight: 720,
    backgroundColor: '#15171a',
    title: 'ForgeSkin',
    // The packaged exe carries the icon (packagerConfig.icon); from source the window takes it from assets/.
    icon: app.isPackaged ? undefined : join(app.getAppPath(), 'assets', 'icon.ico'),
    // Frameless; the top bar draws its own minimize / maximize / close (see `win:*` below).
    titleBarStyle: 'hidden',
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      // A minimized or covered window keeps running its frames and timers for an agent that drives it.
      backgroundThrottling: !aiControlOn,
    },
  });
  win.once('ready-to-show', () => win?.show());
  // The hidden card window goes with the editor's (it would keep the app running).
  win.on(
    'closed',
    () =>
      void cards?.then(
        (w) => w.destroy(),
        () => undefined,
      ),
  );
  win.on('maximize', () => send('win:maximized', true));
  win.on('unmaximize', () => send('win:maximized', false));
  // A dead renderer is reloaded; the store then reopens the last weapon from its autosave.
  win.webContents.on('render-process-gone', (_e, d) => {
    appLog('render-process-gone', d);
    if (d.reason !== 'clean-exit' && !quitting) setTimeout(() => win?.webContents.reload(), 500);
  });
  win.webContents.on('unresponsive', () => appLog('renderer unresponsive', ''));
  // A first load can fail (a cold dev server, a crashed network service) and would leave the window blank: retry a few times.
  let loadTries = 0;
  let loaded = false;
  const load = () =>
    (MAIN_WINDOW_VITE_DEV_SERVER_URL ? win?.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL) : win?.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`)))?.catch(() => {});
  const retry = (why: unknown) => {
    if (quitting) return;
    appLog('did-fail-load', { why, try: loadTries + 1 });
    if (++loadTries > 5) return appLog('did-fail-load', 'gave up after 5 retries');
    setTimeout(load, 1000);
  };
  win.webContents.on('did-finish-load', () => (loaded = true));
  win.webContents.on('did-fail-load', (_e, code, _desc, url, isMainFrame) => {
    // -3 (aborted) is skipped only while a newer navigation goes on; one aborted by a crashed network service leaves nothing loading.
    if (isMainFrame && (code !== -3 || !win?.webContents.isLoading())) retry({ code, url });
  });
  // A network service that crashes before the first page is up can end that load without any did-fail-load.
  app.on('child-process-gone', (_e, d) => {
    if (!loaded && d.serviceName === 'network.mojom.NetworkService') retry('network service crashed');
  });
  load();
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) shell.openExternal(url);
    return { action: 'deny' };
  });
}

// Card pictures (the work list's and the gallery's) are drawn in a hidden second window of the same renderer (renderer/viewport/
// CardWindow.tsx, the page's `#card`), with the 3D view's own stage, so drawing them never holds up the editor window. It opens
// with the first picture asked for, draws one at a time and closes after a minute without work.
let cards: Promise<BrowserWindow> | null = null;
const cardJobs = new Map<number, (result: unknown) => void>();
let cardSeq = 0;
let cardIdle: NodeJS.Timeout | undefined;

function cardWindow(): Promise<BrowserWindow> {
  cards ??= new Promise<BrowserWindow>((ready, failed) => {
    const w = new BrowserWindow({
      show: false,
      width: 600,
      height: 400,
      webPreferences: { preload: join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, backgroundThrottling: false },
    });
    // Ready once its page listens for jobs (card:ready); a job sent before that would be lost.
    const onReady = (e: Electron.IpcMainEvent) => e.sender === w.webContents && (ipcMain.off('card:ready', onReady), ready(w));
    ipcMain.on('card:ready', onReady);
    const gone = () => {
      ipcMain.off('card:ready', onReady);
      cards = null;
      failed(new Error('card window closed'));
      for (const done of cardJobs.values()) done(null);
      cardJobs.clear();
    };
    w.on('closed', gone);
    w.webContents.on('render-process-gone', (_e, d) => (appLog('card window gone', d), w.destroy()));
    if (MAIN_WINDOW_VITE_DEV_SERVER_URL) void w.loadURL(`${MAIN_WINDOW_VITE_DEV_SERVER_URL}#card`).catch(() => w.destroy());
    else void w.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`), { hash: 'card' }).catch(() => w.destroy());
  });
  return cards;
}

async function drawCard(job: unknown): Promise<unknown> {
  clearTimeout(cardIdle);
  const w = await cardWindow();
  const id = ++cardSeq;
  const result = await new Promise((done) => {
    cardJobs.set(id, done);
    w.webContents.send('card:job', id, job);
  });
  if (!cardJobs.size) cardIdle = setTimeout(() => void cards?.then((x) => x.destroy()), 60_000);
  return result;
}

// The renderer may only write files inside the data dir (autosaved skins and their frames live there), and next to a skin
// project saved elsewhere (a folder holding a .forgeskinproj) its generated frames (`.frames/`) and converted sounds
// (`sounds/*.ogg`).
function writable(path: string): boolean {
  if (!isAbsolute(path)) return false;
  const p = normalize(path);
  if (p.startsWith(resolve(dataDir()) + sep)) return true;
  const dirs = p.split(sep);
  const frames = dirs.indexOf('.frames');
  const project = frames > 0 && /\.(png|json)$/i.test(p) ? dirs.slice(0, frames) : dirs.at(-2) === 'sounds' && p.endsWith('.ogg') ? dirs.slice(0, -2) : null;
  try {
    return !!project && readdirSync(project.join(sep)).some((f) => f.endsWith('.forgeskinproj'));
  } catch {
    return false;
  }
}

// sf:// serves what the editor shows (textures, models, videos, sounds, fonts, and a character work's converted model: its
// descriptor and mesh, drawn when the work came from a pack) wherever the user keeps them, and no other kind of file.
const SERVED = /\.(png|jpe?g|tga|tiff?|bmp|webp|gif|exr|mp4|webm|mov|m4v|wav|mp3|ogg|flac|m4a|aac|opus|ttf|otf|woff2?|glb|gltf|vrm|bin|forgemesh)$|[\\/]character[\\/]model\.json$/i;

function registerIpc() {
  // IPC turns a rejected plain object into "[object Object]", so backend errors travel as a value and preload rethrows them.
  ipcMain.handle('backend:call', (_e, method: string, params: unknown) =>
    backend.call(method, params).then(
      (result) => ({ result }),
      (error: unknown) => ({ error }),
    ),
  );
  ipcMain.handle('app:dataDir', () => dataDir());
  ipcMain.handle('app:edition', () => edition);
  ipcMain.handle('app:aiControl', () => aiControlOn);
  // The window saves first and reloads after a move (its open skin and paths name the old folder).
  ipcMain.handle('data:move', async (_e, to: string) => {
    if (edition === 'dev') return { ok: false, reason: 'dev' };
    moving = true;
    try {
      await backend.stop();
      watchFiles([]); // a watched folder cannot be deleted
      const r = await moveData(to, true).catch((e: Error) => ({ ok: false as const, reason: 'failed' as const, detail: e.message }));
      appLog('data folder', { to, ...r });
      dialogDirs = null;
      return r;
    } finally {
      moving = false;
      startBackend();
    }
  });
  ipcMain.handle('dialog:open', async (_e, kind: 'folder' | 'files' | 'file', filters?: OpenDialogOptions['filters'], purpose?: string) => {
    const key = dialogKey(purpose ?? kind, filters);
    const paths = await pickPaths(kind, filters, lastDir(key));
    if (!paths.length) return [];
    remember(key, kind === 'folder' ? paths[0] : dirname(paths[0]));
    return paths;
  });
  ipcMain.handle('dialog:save', async (_e, defaultPath: string, filters: OpenDialogOptions['filters']) => {
    const key = dialogKey('save', filters);
    // A bare file name opens in the folder this kind of file was last saved to.
    const last = basename(defaultPath) === defaultPath ? lastDir(key) : undefined;
    const [path] = await pickPaths('save', filters, last ? join(last, defaultPath) : defaultPath);
    if (!path) return null;
    remember(key, dirname(path));
    return path;
  });
  ipcMain.handle('dialog:preset', (_e, paths: string[]) => {
    if (!aiControlOn) throw new Error('dialog:preset needs --ai-control');
    if (!paths.length || paths.some((p) => !isAbsolute(p))) throw new Error('dialog:preset takes absolute paths');
    presetPaths.push(paths.map((p) => normalize(p)));
  });
  ipcMain.handle('files:write', async (_e, path: string, data: Uint8Array) => {
    if (!writable(path)) throw new Error(`refused write outside frames/data dir: ${path}`);
    await mkdir(dirname(path), { recursive: true });
    // The same bytes again (a bake whose inputs did not change) leave the file and its write time alone, so the pack
    // build reuses what it made from it.
    if ((await readFile(path).catch(() => null))?.equals(data)) return;
    await writeFile(path, data);
  });
  // A text file the renderer may write (a bake's record), null when it is missing.
  ipcMain.handle('files:readText', async (_e, path: string) => {
    if (!writable(path)) throw new Error(`refused read outside frames/data dir: ${path}`);
    return readFile(path, 'utf8').catch(() => null);
  });
  // [size, write time in ms] of a file, null when it is not one: part of a bake's input key (renderer frames.ts bakeKey).
  ipcMain.handle('files:stat', async (_e, path: string) => {
    const s = await stat(path).catch(() => null);
    return s?.isFile() ? [s.size, s.mtimeMs] : null;
  });
  ipcMain.on('app:log', (_e, kind: string, detail: string) => appLog(kind, detail));
  ipcMain.handle('files:exists', (_e, path: string) => existsSync(path));
  ipcMain.handle('files:watch', (_e, paths: string[]) => watchFiles(paths));
  ipcMain.handle('profile:watch', (_e, profile: string | null) => watchProfile(profile));
  // A folder opens in Explorer; a file is only shown in its folder, never launched.
  ipcMain.handle('shell:open', async (_e, path: string) => {
    if (isAbsolute(path) && (await stat(path).catch(() => null))?.isDirectory()) return shell.openPath(path);
    shell.showItemInFolder(path);
    return '';
  });
  ipcMain.handle('shell:show', (_e, path: string) => shell.showItemInFolder(path));
  ipcMain.on('win:minimize', () => win?.minimize());
  ipcMain.on('win:toggleMaximize', () => (win?.isMaximized() ? win.unmaximize() : win?.maximize()));
  ipcMain.on('win:close', () => win?.close());
  ipcMain.handle('win:isMaximized', () => !!win?.isMaximized());
  // 检查更新: the same check the installed copy runs at start and hourly; a found update downloads and then offers the
  // restart as usual. A development run has no update feed.
  ipcMain.handle(
    'update:check',
    () =>
      new Promise<'dev' | 'portable' | 'latest' | 'downloading' | 'error'>((done) => {
        if (edition !== 'installed') return done(edition);
        const off = () => {
          autoUpdater.off('update-available', found);
          autoUpdater.off('update-not-available', latest);
          autoUpdater.off('error', failed);
        };
        const found = () => (off(), done('downloading'));
        const latest = () => (off(), done('latest'));
        const failed = (e: Error) => (off(), updateLog(`check failed: ${e.message}`), done('error'));
        autoUpdater.on('update-available', found);
        autoUpdater.on('update-not-available', latest);
        autoUpdater.on('error', failed);
        autoUpdater.checkForUpdates();
      }),
  );
  ipcMain.handle('app:version', () => app.getVersion());
  ipcMain.handle('card:draw', (_e, job: unknown) => drawCard(job).catch(() => null));
  ipcMain.on('card:done', (_e, id: number, result: unknown) => {
    cardJobs.get(id)?.(result);
    cardJobs.delete(id);
  });
}

const updateLog = (message: string) => appLog('update', message);
// Updates come from the GTFO-Forge-Mods repository: its release `ForgeSkin-update` always holds the newest installer's
// RELEASES and .nupkg (the repository has other packages' releases, so its "latest" is not ours).
const FEED = 'https://github.com/NAinfini/GTFO-Forge-Mods/releases/download/ForgeSkin-update';

// The download takes minutes and Squirrel reports no progress: the window shows a bar from the package's size in the
// feed's RELEASES and the size of the file Update.exe is writing into <install>/packages/. Unknown sizes show a moving bar.
let watching = false;
async function watchDownload() {
  if (watching) return;
  watching = true;
  let file: string | null = null;
  let total = 0;
  try {
    const feed = `${FEED}/RELEASES`;
    const [, url, size] = (await (await net.fetch(feed)).text())
      .replace(/^\uFEFF/, '')
      .trim()
      .split(/\r?\n/)
      .pop()!
      .split(' ');
    file = join(dirname(process.execPath), '..', 'packages', decodeURIComponent(url.split('/').pop()!));
    total = Number(size) || 0;
  } catch (e) {
    updateLog(`progress: no package size: ${String(e)}`);
  }
  const tick = () => {
    let received = 0;
    try {
      if (file) received = statSync(file).size;
    } catch {
      // not started yet
    }
    send('update:progress', received, total);
  };
  tick();
  const timer = setInterval(tick, 1000);
  const stop = (done: boolean) => () => {
    clearInterval(timer);
    watching = false;
    send('update:progress', done ? total || 1 : -1, total || 1);
  };
  autoUpdater.once('update-downloaded', stop(true));
  autoUpdater.once('error', stop(false));
}

// Every file the editor asks for comes through pickPaths: the system's dialog, with the file types and, for 'files', several
// at once. An empty answer is a cancel. With --ai-control an agent may answer the next dialog beforehand (forgeAI pickNext),
// since a protocol client cannot drive the system's dialog; nothing is shown in the window for it.
type PathKind = 'folder' | 'files' | 'file' | 'save';
type FileFilters = OpenDialogOptions['filters'];
const presetPaths: string[][] = [];

async function pickPaths(kind: PathKind, filters: FileFilters, start: string | undefined): Promise<string[]> {
  const preset = presetPaths.shift();
  if (preset) return preset;
  if (kind === 'save') {
    const r = await dialog.showSaveDialog(win!, { defaultPath: start, filters });
    return r.canceled || !r.filePath ? [] : [r.filePath];
  }
  const properties: OpenDialogOptions['properties'] = kind === 'folder' ? ['openDirectory'] : kind === 'files' ? ['openFile', 'multiSelections'] : ['openFile'];
  const r = await dialog.showOpenDialog(win!, { properties, filters, defaultPath: start });
  return r.canceled ? [] : r.filePaths;
}
// Every file dialog opens where the last one of its kind was confirmed (a purpose, else the kind and file types), kept
// across runs in <dataDir>/dialogs.json.
const dialogsFile = () => join(dataDir(), 'dialogs.json');
let dialogDirs: Record<string, string> | null = null;
const dialogKey = (purpose: string, filters?: OpenDialogOptions['filters']) => [purpose, ...(filters ?? []).flatMap((f) => f.extensions)].join(':');
function lastDir(key: string): string | undefined {
  if (!dialogDirs) {
    try {
      dialogDirs = JSON.parse(readFileSync(dialogsFile(), 'utf8')) as Record<string, string>;
    } catch {
      dialogDirs = {};
    }
  }
  const dir = dialogDirs[key];
  return dir && existsSync(dir) ? dir : undefined;
}
function remember(key: string, dir: string) {
  lastDir(key);
  dialogDirs![key] = dir;
  try {
    writeFileSync(dialogsFile(), JSON.stringify(dialogDirs, null, 2));
  } catch (e) {
    appLog('dialogs', e);
  }
}

// The files the open skin uses, watched through their folders (an editor saving a file often replaces it, which a watch on
// the file itself loses). A changed file is reported once its writes have been quiet for 400 ms.
const watched = new Map<string, { watcher: FSWatcher; names: Set<string> }>();
const changed = new Set<string>();
let changedTimer: NodeJS.Timeout | undefined;
function watchFiles(paths: string[]) {
  const want = new Map<string, Set<string>>();
  for (const p of paths) {
    const dir = dirname(p);
    if (!want.has(dir)) want.set(dir, new Set());
    want.get(dir)!.add(basename(p).toLowerCase());
  }
  for (const [dir, w] of watched)
    if (!want.has(dir)) {
      w.watcher.close();
      watched.delete(dir);
    }
  for (const [dir, names] of want) {
    const w = watched.get(dir);
    if (w) {
      w.names = names;
      continue;
    }
    try {
      const entry = { names, watcher: watch(dir) };
      entry.watcher.on('change', (_type, name) => {
        if (!name || !entry.names.has(String(name).toLowerCase())) return;
        changed.add(join(dir, String(name)));
        clearTimeout(changedTimer);
        changedTimer = setTimeout(() => {
          send('files:changed', [...changed]);
          changed.clear();
        }, 400);
      });
      entry.watcher.on('error', () => (entry.watcher.close(), watched.delete(dir)));
      watched.set(dir, entry);
    } catch (e) {
      appLog('watch', e); // a folder that is gone: nothing to watch
    }
  }
}

// The chosen game profile's skin state: its skins folder (packs, active.json, picks) and the plugin's choices in config/, watched
// so the window's lists follow a pack put in or taken out by hand, the game's picker, or another program. Reported once the
// writes have been quiet for 400 ms.
let profileWatchers: FSWatcher[] = [];
let profileTimer: NodeJS.Timeout | undefined;
function watchProfile(profile: string | null) {
  for (const w of profileWatchers) w.close();
  profileWatchers = [];
  if (!profile) return;
  const folders: [string, (name: string) => boolean][] = [
    [join(profile, 'BepInEx', 'ForgeSkin', 'skins'), () => true],
    [join(profile, 'BepInEx', 'config'), (name) => /^ForgeSkin\.choices\.json$/i.test(name)],
  ];
  for (const [dir, counts] of folders) {
    if (!existsSync(dir)) continue; // made by the first install, which watches again
    try {
      const w = watch(dir, (_type, name) => {
        if (name && !counts(String(name))) return;
        clearTimeout(profileTimer);
        profileTimer = setTimeout(() => send('profile:changed'), 400);
      });
      w.on('error', () => w.close());
      profileWatchers.push(w);
    } catch (e) {
      appLog('watch', e); // a profile without BepInEx: nothing to follow
    }
  }
}

app.whenReady().then(async () => {
  // Before anything writes into the data folder.
  if (!squirrelRun) await moveInstallData((line) => appLog('data folder', line));
  protocol.handle('sf', async (req) => {
    const path = decodeURIComponent(new URL(req.url).pathname.slice(1));
    if (!isAbsolute(path) || !SERVED.test(path)) return new Response(null, { status: 403 });
    const r = await net.fetch(pathToFileURL(path).toString(), { headers: req.headers });
    const headers = new Headers(r.headers);
    headers.set('Cache-Control', 'no-store');
    return new Response(r.body, { status: r.status, statusText: r.statusText, headers });
  });
  registerIpc();
  startBackend();
  createWindow();
  // An installed copy checks the update feed (FEED) at start and hourly, downloads a
  // newer one in the background and offers to restart into it, in the window (update:ready, update:restart) rather than
  // a system dialog. The bundled plugin updates with it.
  ipcMain.handle('update:restart', () => autoUpdater.quitAndInstall());
  autoUpdater.on('update-available', () => void watchDownload());
  if (edition === 'installed')
    updateElectronApp({
      updateSource: { type: UpdateSourceType.StaticStorage, baseUrl: FEED },
      logger: { log: updateLog, info: updateLog, warn: updateLog, error: updateLog },
      onNotifyUser: () => send('update:ready'),
    });
});

app.on('before-quit', () => {
  quitting = true;
  backend.stop();
});
app.on('window-all-closed', () => app.quit());
