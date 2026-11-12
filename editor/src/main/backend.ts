// Spawns ForgeSkin.Backend and speaks PLAN §3.3: one JSON object per line on stdio.
import { execFileSync, spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { basename, dirname, join } from 'node:path';
import { app } from 'electron';

export interface BackendError {
  code: string;
  message: string;
}

type Pending = { resolve: (v: unknown) => void; reject: (e: BackendError) => void };

export class Backend {
  private child: ChildProcessWithoutNullStreams | null = null;
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private stdoutBuf = '';
  private stderrBuf = '';

  constructor(
    private readonly onEvent: (event: string, data: unknown) => void,
    private readonly onLog: (line: string) => void,
    private readonly onExit: (code: number | null) => void,
  ) {}

  /**
   * Data dir: `GTFO_UserData` next to the app exe when packaged, `editor/GTFO_UserData` in dev. An installed copy runs
   * from `<install>/app-<version>/`, which every update replaces: its data lives in `<install>/GTFO_UserData`.
   */
  static dataDir(): string {
    if (!app.isPackaged) return join(app.getAppPath(), 'GTFO_UserData');
    const dir = dirname(app.getPath('exe'));
    return join(/^app-\d/.test(basename(dir)) ? dirname(dir) : dir, 'GTFO_UserData');
  }

  private command(): { file: string; args: string[]; cwd: string } {
    const dataArgs = ['--data-dir', Backend.dataDir()];
    if (app.isPackaged) {
      const file = join(process.resourcesPath, 'publish', 'ForgeSkin.Backend.exe');
      return { file, args: dataArgs, cwd: dirname(file) };
    }
    // In a checkout the backend is built from the current source first (incremental, seconds when up to date), so the
    // editor never talks to a backend older than itself (e.g. a glyph strip cut into the wrong number of cells).
    const project = join(app.getAppPath(), '..', 'src', 'ForgeSkin.Backend');
    try {
      execFileSync('dotnet', ['build', project, '-c', 'Debug', '-nologo', '-v', 'q'], { windowsHide: true, stdio: 'pipe' });
    } catch (e) {
      this.onLog(`backend build failed: ${(e as { stdout?: Buffer }).stdout?.toString() ?? e}`);
    }
    const file = join(project, 'bin', 'Debug', 'net10.0', 'win-x64', 'ForgeSkin.Backend.exe');
    return { file, args: dataArgs, cwd: dirname(file) };
  }

  start(): void {
    const { file, args, cwd } = this.command();
    this.onLog(`starting backend: ${file} ${args.join(' ')}`);
    const child = spawn(file, args, { windowsHide: true, cwd });
    this.child = child;
    // A half line from the previous process is not the start of this one's.
    this.stdoutBuf = this.stderrBuf = '';
    // Once per process, whether it exits or never starts: every call still waiting fails, so the UI shows the backend
    // down instead of waiting forever.
    let ended = false;
    const end = (code: number | null) => {
      if (ended) return;
      ended = true;
      if (this.child === child) this.child = null;
      for (const p of this.pending.values()) p.reject({ code: 'backend-exited', message: 'backend exited' });
      this.pending.clear();
      this.onExit(code);
    };
    child.stdout.setEncoding('utf8').on('data', (d: string) => {
      this.stdoutBuf += d;
      for (let i; (i = this.stdoutBuf.indexOf('\n')) >= 0; ) {
        const line = this.stdoutBuf.slice(0, i).trim();
        this.stdoutBuf = this.stdoutBuf.slice(i + 1);
        if (line) this.handleLine(line);
      }
    });
    child.stderr.setEncoding('utf8').on('data', (d: string) => {
      this.stderrBuf += d;
      for (let i; (i = this.stderrBuf.indexOf('\n')) >= 0; ) {
        this.onLog(this.stderrBuf.slice(0, i).trimEnd());
        this.stderrBuf = this.stderrBuf.slice(i + 1);
      }
    });
    child.on('error', (e) => {
      this.onLog(`backend failed: ${e.message}`);
      // A process that never started sends no 'exit'.
      if (child.pid === undefined) end(null);
    });
    // Writing to a process that just died fails here; its 'exit' rejects the call.
    child.stdin.on('error', (e) => this.onLog(`backend stdin: ${e.message}`));
    child.on('exit', end);
  }

  private handleLine(line: string): void {
    let msg: { id?: number; result?: unknown; error?: BackendError; event?: string; data?: unknown };
    try {
      msg = JSON.parse(line);
    } catch {
      this.onLog(`backend stdout (not JSON): ${line}`);
      return;
    }
    if (msg.event) {
      this.onEvent(msg.event, msg.data);
      return;
    }
    const p = msg.id != null ? this.pending.get(msg.id) : undefined;
    if (!p) return;
    this.pending.delete(msg.id!);
    if (msg.error) p.reject(msg.error);
    else p.resolve(msg.result);
  }

  call(method: string, params: unknown): Promise<unknown> {
    if (!this.child) return Promise.reject({ code: 'backend-exited', message: 'backend not running' });
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.child!.stdin.write(JSON.stringify({ id, method, params: params ?? {} }) + '\n');
    });
  }

  stop(): void {
    this.child?.kill();
  }
}
