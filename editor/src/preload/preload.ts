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
  /** Opens where the last dialog of the same `purpose` (else kind and file types) was confirmed. */
  openDialog: (kind: 'folder' | 'files' | 'project', filters?: FileFilter[], purpose?: string): Promise<string[]> =>
    ipcRenderer.invoke('dialog:open', kind, filters, purpose),
  saveDialog: (defaultPath: string, filters: FileFilter[]): Promise<string | null> =>
    ipcRenderer.invoke('dialog:save', defaultPath, filters),
  writeFile: (path: string, data: Uint8Array): Promise<void> => ipcRenderer.invoke('files:write', path, data),
  exists: (path: string): Promise<boolean> => ipcRenderer.invoke('files:exists', path),
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
  checkUpdate: (): Promise<'dev' | 'latest' | 'downloading' | 'error'> => ipcRenderer.invoke('update:check'),
  version: (): Promise<string> => ipcRenderer.invoke('app:version'),
  onMaximized: (cb: (maximized: boolean) => void) => subscribe('win:maximized', cb),
};

export type ForgeApi = typeof api;
contextBridge.exposeInMainWorld('forge', api);
