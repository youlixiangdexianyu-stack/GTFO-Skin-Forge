import { app, BrowserWindow, dialog, ipcMain, net, protocol, shell, type OpenDialogOptions } from 'electron';
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
const send = (channel: string, ...args: unknown[]) => win?.webContents.send(channel, ...args);

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
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#15171a', symbolColor: '#b9b4a8', height: 44 },
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  win.once('ready-to-show', () => win?.show());
  if (MAIN_WINDOW_VITE_DEV_SERVER_URL) win.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL);
  else win.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`));
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) shell.openExternal(url);
    return { action: 'deny' };
  });
}

// The renderer may only write generated frame PNGs (project `.frames/`) and files inside the data dir.
function writable(path: string): boolean {
  if (!isAbsolute(path)) return false;
  const p = normalize(path);
  return p.split(sep).includes('.frames') || p.startsWith(resolve(Backend.dataDir()) + sep);
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
      filters:
        kind === 'project'
          ? [{ name: '皮肤工程', extensions: ['sfproj', 'gtfoskin', 'gtfdyn', 'json'] }]
          : filters,
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
  ipcMain.handle('shell:open', (_e, path: string) => shell.openPath(path));
  ipcMain.handle('shell:show', (_e, path: string) => shell.showItemInFolder(path));
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
