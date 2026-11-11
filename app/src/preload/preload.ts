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
  openDialog: (kind: 'folder' | 'files' | 'project', filters?: FileFilter[]): Promise<string[]> =>
    ipcRenderer.invoke('dialog:open', kind, filters),
  saveDialog: (defaultPath: string, filters: FileFilter[]): Promise<string | null> =>
    ipcRenderer.invoke('dialog:save', defaultPath, filters),
  writeFile: (path: string, data: Uint8Array): Promise<void> => ipcRenderer.invoke('files:write', path, data),
  openPath: (path: string): Promise<string> => ipcRenderer.invoke('shell:open', path),
  showInFolder: (path: string): Promise<void> => ipcRenderer.invoke('shell:show', path),
  /** Absolute path of a dropped File (Electron removed File.path). */
  pathOf: (file: File): string => webUtils.getPathForFile(file),
};

export type ForgeApi = typeof api;
contextBridge.exposeInMainWorld('forge', api);
