import { app, BrowserWindow, dialog, ipcMain, net, protocol, shell, type OpenDialogOptions } from 'electron';
import { appendFileSync, existsSync, mkdirSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, normalize, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { Backend } from './backend';

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
    title: 'GTFO Skin Forge',
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

// The renderer may only write generated frame PNGs (project `.frames/`), converted sounds (project `sounds/*.ogg`) and
// files inside the data dir.
function writable(path: string): boolean {
  if (!isAbsolute(path)) return false;
  const p = normalize(path);
  const dirs = p.split(sep);
  return dirs.includes('.frames') || (dirs.at(-2) === 'sounds' && p.endsWith('.ogg')) || p.startsWith(resolve(Backend.dataDir()) + sep);
}

function registerIpc() {
  // IPC turns a rejected plain object into "[object Object]", so backend errors travel as a value and preload rethrows them.
  ipcMain.handle('backend:call', (_e, method: string, params: unknown) =>
    backend.call(method, params).then(
      (result) => ({ result }),
      (error: unknown) => ({ error }),
    ),
  );
  ipcMain.handle('app:dataDir', () => Backend.dataDir());
  ipcMain.handle('dialog:open', async (_e, kind: 'folder' | 'files' | 'project', filters?: OpenDialogOptions['filters']) => {
    const props: OpenDialogOptions['properties'] =
      kind === 'folder' ? ['openDirectory'] : kind === 'files' ? ['openFile', 'multiSelections'] : ['openFile'];
    const r = await dialog.showOpenDialog(win!, {
      properties: props,
      filters,
    });
    return r.canceled ? [] : r.filePaths;
  });
  ipcMain.handle('dialog:save', async (_e, defaultPath: string, filters: OpenDialogOptions['filters']) => {
    const r = await dialog.showSaveDialog(win!, { defaultPath, filters });
    return r.canceled ? null : r.filePath;
  });
  ipcMain.handle('files:write', async (_e, path: string, data: Uint8Array) => {
    if (!writable(path)) throw new Error(`refused write outside frames/data dir: ${path}`);
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, data);
  });
  ipcMain.on('app:log', (_e, kind: string, detail: string) => appLog(kind, detail));
  ipcMain.handle('files:exists', (_e, path: string) => existsSync(path));
  ipcMain.handle('shell:open', (_e, path: string) => shell.openPath(path));
  ipcMain.handle('shell:show', (_e, path: string) => shell.showItemInFolder(path));
  ipcMain.on('win:minimize', () => win?.minimize());
  ipcMain.on('win:toggleMaximize', () => (win?.isMaximized() ? win.unmaximize() : win?.maximize()));
  ipcMain.on('win:close', () => win?.close());
  ipcMain.handle('win:isMaximized', () => !!win?.isMaximized());
}

app.whenReady().then(() => {
  protocol.handle('sf', (req) => {
    const path = decodeURIComponent(new URL(req.url).pathname.slice(1));
    return net.fetch(pathToFileURL(path).toString(), { headers: req.headers });
  });
  registerIpc();
  startBackend();
  createWindow();
});

app.on('before-quit', () => {
  quitting = true;
  backend.stop();
});
app.on('window-all-closed', () => app.quit());
