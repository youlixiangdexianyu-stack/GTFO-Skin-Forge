import { app, autoUpdater, BrowserWindow, dialog, ipcMain, net, protocol, shell, type OpenDialogOptions } from 'electron';
import { appendFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { mkdir, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, normalize, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import squirrelStartup from 'electron-squirrel-startup';
import { makeUserNotifier, updateElectronApp, UpdateSourceType } from 'update-electron-app';
import { Backend } from './backend';

// The installer runs the app once per install, update and uninstall to make or remove its shortcuts; it quits then.
if (squirrelStartup) app.quit();

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

// Dev only: SF_CDP=<port> exposes DevTools protocol so the running app can be inspected and screenshotted.
if (!app.isPackaged && process.env.SF_CDP) app.commandLine.appendSwitch('remote-debugging-port', process.env.SF_CDP);

// Local files (preview GLB, textures, videos) are served to the renderer as sf://local/<abs path>.
protocol.registerSchemesAsPrivileged([
  { scheme: 'sf', privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true, corsEnabled: true } },
]);

let win: BrowserWindow | null = null;
let quitting = false;
let startedAt = 0;
let crashes = 0;
// The window can be gone while the app shuts down (the backend exits after it).
const send = (channel: string, ...args: unknown[]) => {
  if (win && !win.isDestroyed()) win.webContents.send(channel, ...args);
};

/** Crash and error evidence: one line per event in GTFO_UserData/logs/app.log, written synchronously so it survives the crash. */
function appLog(kind: string, detail: unknown) {
  const text = detail instanceof Error ? `${detail.message}
${detail.stack}` : typeof detail === 'string' ? detail : JSON.stringify(detail);
  try {
    const dir = join(Backend.dataDir(), 'logs');
    mkdirSync(dir, { recursive: true });
    appendFileSync(join(dir, 'app.log'), `${new Date().toISOString()} [${kind}] ${text}
`);
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
    if (quitting) return;
    // Restart, but give up after repeated crashes right after start (shown to the user as "down").
    crashes = Date.now() - startedAt < 10_000 ? crashes + 1 : 0;
    if (crashes >= 3) return send('backend:status', 'down');
    send('backend:status', 'restarting');
    setTimeout(startBackend, 1000);
  },
);

function startBackend() {
  startedAt = Date.now();
  backend.start();
  send('backend:status', 'ready');
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
    },
  });
  win.once('ready-to-show', () => win?.show());
  win.on('maximize', () => send('win:maximized', true));
  win.on('unmaximize', () => send('win:maximized', false));
  // A dead renderer is reloaded; the store then reopens the last weapon from its autosave.
  win.webContents.on('render-process-gone', (_e, d) => {
    appLog('render-process-gone', d);
    if (d.reason !== 'clean-exit' && !quitting) setTimeout(() => win?.webContents.reload(), 500);
  });
  win.webContents.on('unresponsive', () => appLog('renderer unresponsive', ''));
  if (MAIN_WINDOW_VITE_DEV_SERVER_URL) win.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL);
  else win.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`));
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) shell.openExternal(url);
    return { action: 'deny' };
  });
}

// The renderer may only write files inside the data dir (autosaved skins and their frames live there), and next to a skin
// project saved elsewhere (a folder holding a .forgeskinproj) its generated frames (`.frames/`) and converted sounds
// (`sounds/*.ogg`).
function writable(path: string): boolean {
  if (!isAbsolute(path)) return false;
  const p = normalize(path);
  if (p.startsWith(resolve(Backend.dataDir()) + sep)) return true;
  const dirs = p.split(sep);
  const frames = dirs.indexOf('.frames');
  const project =
    frames > 0 && /\.(png|json)$/i.test(p) ? dirs.slice(0, frames)
    : dirs.at(-2) === 'sounds' && p.endsWith('.ogg') ? dirs.slice(0, -2)
    : null;
  try {
    return !!project && readdirSync(project.join(sep)).some((f) => f.endsWith('.forgeskinproj'));
  } catch {
    return false;
  }
}

// sf:// serves what the editor shows (textures, models, videos, sounds, fonts) wherever the user keeps them, and no
// other kind of file.
const SERVED = /\.(png|jpe?g|tga|tiff?|bmp|webp|gif|exr|mp4|webm|mov|m4v|wav|mp3|ogg|flac|m4a|aac|opus|ttf|otf|woff2?|glb|gltf|bin)$/i;

function registerIpc() {
  // IPC turns a rejected plain object into "[object Object]", so backend errors travel as a value and preload rethrows them.
  ipcMain.handle('backend:call', (_e, method: string, params: unknown) =>
    backend.call(method, params).then(
      (result) => ({ result }),
      (error: unknown) => ({ error }),
    ),
  );
  ipcMain.handle('app:dataDir', () => Backend.dataDir());
  ipcMain.handle('dialog:open', async (_e, kind: 'folder' | 'files' | 'project', filters?: OpenDialogOptions['filters'], purpose?: string) => {
    const props: OpenDialogOptions['properties'] =
      kind === 'folder' ? ['openDirectory'] : kind === 'files' ? ['openFile', 'multiSelections'] : ['openFile'];
    const key = dialogKey(purpose ?? kind, filters);
    const r = await dialog.showOpenDialog(win!, { properties: props, filters, defaultPath: lastDir(key) });
    if (r.canceled || !r.filePaths.length) return [];
    remember(key, kind === 'folder' ? r.filePaths[0] : dirname(r.filePaths[0]));
    return r.filePaths;
  });
  ipcMain.handle('dialog:save', async (_e, defaultPath: string, filters: OpenDialogOptions['filters']) => {
    const key = dialogKey('save', filters);
    // A bare file name opens in the folder this kind of file was last saved to.
    const last = basename(defaultPath) === defaultPath ? lastDir(key) : undefined;
    const r = await dialog.showSaveDialog(win!, { defaultPath: last ? join(last, defaultPath) : defaultPath, filters });
    if (r.canceled || !r.filePath) return null;
    remember(key, dirname(r.filePath));
    return r.filePath;
  });
  ipcMain.handle('files:write', async (_e, path: string, data: Uint8Array) => {
    if (!writable(path)) throw new Error(`refused write outside frames/data dir: ${path}`);
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, data);
  });
  ipcMain.on('app:log', (_e, kind: string, detail: string) => appLog(kind, detail));
  ipcMain.handle('files:exists', (_e, path: string) => existsSync(path));
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
      new Promise<'dev' | 'latest' | 'downloading' | 'error'>((done) => {
        if (!app.isPackaged) return done('dev');
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
}

const updateLog = (message: string) => appLog('update', message);

// Every file dialog opens where the last one of its kind was confirmed (a purpose, else the kind and file types), kept
// across runs in <dataDir>/dialogs.json.
const dialogsFile = () => join(Backend.dataDir(), 'dialogs.json');
let dialogDirs: Record<string, string> | null = null;
const dialogKey = (purpose: string, filters?: OpenDialogOptions['filters']) =>
  [purpose, ...(filters ?? []).flatMap((f) => f.extensions)].join(':');
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

app.whenReady().then(() => {
  protocol.handle('sf', (req) => {
    const path = decodeURIComponent(new URL(req.url).pathname.slice(1));
    if (!isAbsolute(path) || !SERVED.test(path)) return new Response(null, { status: 403 });
    return net.fetch(pathToFileURL(path).toString(), { headers: req.headers });
  });
  registerIpc();
  startBackend();
  createWindow();
  // An installed copy checks this repository's GitHub releases (update.electronjs.org) at start and hourly, downloads a
  // newer one in the background and offers to restart into it. The bundled plugin updates with it.
  // The download takes a while; the window says it has started so a quiet start is not taken for no update.
  autoUpdater.on('update-available', () => send('update:available'));
  if (app.isPackaged)
    updateElectronApp({
      updateSource: { type: UpdateSourceType.ElectronPublicUpdateService, repo: 'youlixiangdexianyu-stack/GTFO-Skin-Forge' },
      logger: { log: updateLog, info: updateLog, warn: updateLog, error: updateLog },
      onNotifyUser: makeUserNotifier({
        title: 'ForgeSkin 有新版本',
        detail: '新版本已经下载好了，重启 ForgeSkin 就会用上新版本（插件也一起更新）。',
        restartButtonText: '现在重启',
        laterButtonText: '稍后',
      }),
    });
});

app.on('before-quit', () => {
  quitting = true;
  backend.stop();
});
app.on('window-all-closed', () => app.quit());
