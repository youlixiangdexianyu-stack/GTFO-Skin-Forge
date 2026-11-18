import { contextBridge, ipcRenderer, webUtils, type IpcRendererEvent } from 'electron';

type FileFilter = { name: string; extensions: string[] };

function subscribe<A extends unknown[]>(channel: string, cb: (...args: A) => void): () => void {
  const fn = (_e: IpcRendererEvent, ...args: unknown[]) => cb(...(args as A));
  ipcRenderer.on(channel, fn);
  return () => ipcRenderer.off(channel, fn);
}

const api = {
  call: async (method: string, params?: unknown): Promise<unknown> => {
    const r = (await ipcRenderer.invoke('backend:call', method, params)) as { result?: unknown; error?: unknown };
    if ('error' in r) throw r.error;
    return r.result;
  },
  onEvent: (cb: (event: string, data: unknown) => void) => subscribe('backend:event', cb),
  onLog: (cb: (line: string) => void) => subscribe('backend:log', cb),
  onStatus: (cb: (status: 'ready' | 'restarting' | 'down') => void) => subscribe('backend:status', cb),
  dataDir: (): Promise<string> => ipcRenderer.invoke('app:dataDir'),
  /** dev: run from a checkout; installed: the Setup.exe install; portable: the unzipped copy. */
  edition: (): Promise<'dev' | 'installed' | 'portable'> => ipcRenderer.invoke('app:edition'),
  /** Development editor only: builds and stages the plugin from the checkout before an install (main.ts stageDevPlugin). */
  stagePlugin: (): Promise<{ ok: true } | { ok: false; reason: 'notDev' | 'noForgeCore' | 'failed'; detail?: string }> => ipcRenderer.invoke('plugin:stage'),
  /** Whether the editor was started with --ai-control (the renderer then installs window.forgeAI). */
  aiControl: (): Promise<boolean> => ipcRenderer.invoke('app:aiControl'),
  /** Moves the data folder (main/dataDir.ts moveData); the window reloads after a move that worked. */
  moveDataDir: (to: string): Promise<{ ok: true; adopted?: boolean; leftover?: string } | { ok: false; reason: 'dev' | 'same' | 'nested' | 'notEmpty' | 'failed'; detail?: string }> =>
    ipcRenderer.invoke('data:move', to),
  /** Opens where the last dialog of the same `purpose` (else kind and file types) was confirmed. */
  /** The system's dialog: 'file' one file, 'files' one or more. */
  openDialog: (kind: 'folder' | 'files' | 'file', filters?: FileFilter[], purpose?: string): Promise<string[]> => ipcRenderer.invoke('dialog:open', kind, filters, purpose),
  saveDialog: (defaultPath: string, filters: FileFilter[]): Promise<string | null> => ipcRenderer.invoke('dialog:save', defaultPath, filters),
  /** With --ai-control: the answer of the next file dialog, which then does not open (forgeAI pickNext). */
  presetPaths: (paths: string[]): Promise<void> => ipcRenderer.invoke('dialog:preset', paths),
  writeFile: (path: string, data: Uint8Array): Promise<void> => ipcRenderer.invoke('files:write', path, data),
  exists: (path: string): Promise<boolean> => ipcRenderer.invoke('files:exists', path),
  /** A text file where writeFile may write, null when it is missing. */
  readText: (path: string): Promise<string | null> => ipcRenderer.invoke('files:readText', path),
  /** [size, write time in ms] of a file, null when it is not one. */
  stat: (path: string): Promise<[number, number] | null> => ipcRenderer.invoke('files:stat', path),
  openPath: (path: string): Promise<string> => ipcRenderer.invoke('shell:open', path),
  showInFolder: (path: string): Promise<void> => ipcRenderer.invoke('shell:show', path),
  /** Appends an error line to GTFO_UserData/logs/app.log. */
  appLog: (kind: string, detail: string): void => ipcRenderer.send('app:log', kind, detail),
  /** Absolute path of a dropped File (Electron removed File.path). */
  pathOf: (file: File): string => webUtils.getPathForFile(file),
  minimize: (): void => ipcRenderer.send('win:minimize'),
  toggleMaximize: (): void => ipcRenderer.send('win:toggleMaximize'),
  close: (): void => ipcRenderer.send('win:close'),
  isMaximized: (): Promise<boolean> => ipcRenderer.invoke('win:isMaximized'),
  /** Which version the update feed holds; downloads nothing. `available`: an installed copy can download `version`
   *  (downloadUpdate); `portable`: a newer `version` exists, fetched by hand. */
  checkUpdate: (): Promise<{ state: 'dev' | 'latest' | 'error' } | { state: 'available' | 'portable'; version: string }> => ipcRenderer.invoke('update:check'),
  /** Downloads the update the check found (onUpdateProgress, then onUpdateReady). */
  downloadUpdate: (): Promise<void> => ipcRenderer.invoke('update:download'),
  version: (): Promise<string> => ipcRenderer.invoke('app:version'),
  onMaximized: (cb: (maximized: boolean) => void) => subscribe('win:maximized', cb),
  /** A found update downloading: bytes so far and in all (0 when unknown); received -1 means it failed, = total done. */
  onUpdateProgress: (cb: (received: number, total: number) => void) => subscribe('update:progress', cb),
  onUpdateReady: (cb: () => void) => subscribe('update:ready', cb),
  /** Watches these files (replacing the last list); onFilesChanged reports the ones written since. */
  watchFiles: (paths: string[]): Promise<void> => ipcRenderer.invoke('files:watch', paths),
  onFilesChanged: (cb: (paths: string[]) => void) => subscribe('files:changed', cb),
  /** Follows a game profile's skins folder and the plugin's choices (null: none); onProfileChanged says when they changed. */
  watchProfile: (profile: string | null): Promise<void> => ipcRenderer.invoke('profile:watch', profile),
  onProfileChanged: (cb: () => void) => subscribe('profile:changed', cb),
  restartToUpdate: (): Promise<void> => ipcRenderer.invoke('update:restart'),
  /** Draws a card picture in the hidden card window (renderer/viewport/CardWindow.tsx); null when it could not be drawn. */
  drawCard: (job: unknown): Promise<unknown> => ipcRenderer.invoke('card:draw', job),
  /** The card window: takes the jobs (after saying it is ready) and hands back each picture under its job's id. */
  onCardJob: (cb: (id: number, job: unknown) => void) => {
    const off = subscribe('card:job', cb);
    ipcRenderer.send('card:ready');
    return off;
  },
  cardDone: (id: number, result: unknown): void => ipcRenderer.send('card:done', id, result),
};

export type ForgeApi = typeof api;
contextBridge.exposeInMainWorld('forge', api);
