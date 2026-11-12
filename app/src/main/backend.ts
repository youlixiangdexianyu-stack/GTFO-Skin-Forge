// Spawns ForgeSkin.Backend and speaks PLAN §3.3: one JSON object per line on stdio.
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
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

  /** Data dir: `GTFO_UserData` next to the app exe when packaged, `app/GTFO_UserData` in dev. */
  static dataDir(): string {
    return app.isPackaged
      ? join(dirname(app.getPath('exe')), 'GTFO_UserData')
      : join(app.getAppPath(), 'GTFO_UserData');
  }

  private command(): { file: string; args: string[]; cwd: string } {
    const dataArgs = ['--data-dir', Backend.dataDir()];
    if (app.isPackaged) {
      const file = join(process.resourcesPath, 'publish', 'ForgeSkin.Backend.exe');
      return { file, args: dataArgs, cwd: dirname(file) };
    }
    const repo = join(app.getAppPath(), '..');
    const dllOf = (exe: string) => (existsSync(exe.replace(/\.exe$/, '.dll')) ? exe.replace(/\.exe$/, '.dll') : exe);
    const built = ['Debug', 'Release']
      .flatMap((c) => ['', 'win-x64/'].map((rid) => join(repo, `src/ForgeSkin.Backend/bin/${c}/net10.0/${rid}ForgeSkin.Backend.exe`)))
      .filter(existsSync)
      // The newest build wins: a Debug copy can go stale while a running backend locks it.
      .sort((a, b) => statSync(dllOf(b)).mtimeMs - statSync(dllOf(a)).mtimeMs)[0];
    if (built) return { file: built, args: dataArgs, cwd: dirname(built) };
    const project = join(repo, 'src/ForgeSkin.Backend');
    return { file: 'dotnet', args: ['run', '--project', project, '--', ...dataArgs], cwd: project };
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
