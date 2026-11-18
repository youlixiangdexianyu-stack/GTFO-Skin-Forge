// The development editor driven from a script (docs-shots.mjs, gallery-thumbs.mjs): started with --ai-control on its own
// temporary Chromium profile (so it never touches another editor's settings), reached over the DevTools protocol.
import { execFileSync, spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const editorDir = join(dirname(fileURLToPath(import.meta.url)), '..');
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const arg = (name) => {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
};
/** The --ai-control port: --port attaches to an editor already started on it; else FORGESKIN_AI_PORT, else `fallback`, for the editor started here. */
export const aiPort = (fallback) => ({ port: Number(arg('--port') ?? process.env.FORGESKIN_AI_PORT ?? fallback), attach: !!arg('--port') });

class Page {
  constructor(url) {
    this.ws = new WebSocket(url);
    this.n = 0;
    this.wait = new Map();
    this.ready = new Promise((r) => this.ws.addEventListener('open', r));
    this.ws.addEventListener('message', (e) => {
      const m = JSON.parse(e.data);
      this.wait.get(m.id)?.(m);
      this.wait.delete(m.id);
    });
  }
  send(method, params = {}) {
    const id = ++this.n;
    return new Promise((resolve, reject) => {
      this.wait.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
  /** Runs `fn(window.__docs, arg)` in the page and returns its result. */
  async run(fn, arg) {
    const r = await this.send('Runtime.evaluate', { expression: `(${fn})(window.__docs, ${JSON.stringify(arg ?? null)})`, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
    return r.result.value;
  }
}

/**
 * The editor's page on `port` once window.forgeAI is up: started here (with `env` added) unless `attach` (an editor already
 * started with --ai-control=<port>). `close()` ends what was started here and deletes its profile.
 */
export async function openEditor(port, { attach = false, env = {} } = {}) {
  let child = null;
  let profile = null;
  let devUrl = 'http://localhost:5190/';
  if (!attach) {
    // An editor already answering on the port (the owner's development editor) would be driven instead of the one started here.
    if (
      await fetch(`http://127.0.0.1:${port}/json/version`).then(
        () => true,
        () => false,
      )
    )
      throw new Error(`port ${port} is already in use; pick another with FORGESKIN_AI_PORT`);
    execFileSync(process.execPath, [join(editorDir, 'scripts/gen-types.mjs')], { cwd: editorDir, stdio: 'inherit' });
    profile = mkdtempSync(join(tmpdir(), 'forgeskin-script-'));
    const start = join(editorDir, 'node_modules/@electron-forge/cli/dist/electron-forge-start.js');
    child = spawn(process.execPath, [start, '--', `--ai-control=${port}`, `--user-data-dir=${profile}`], { cwd: editorDir, windowsHide: true, env: { ...process.env, ...env } });
    const watch = (b) => {
      const m = String(b).match(/http:\/\/localhost:\d+\//);
      if (m) devUrl = m[0];
    };
    child.stdout.on('data', watch);
    child.stderr.on('data', watch);
  }
  const close = () => {
    page?.ws.close();
    if (child?.pid) {
      try {
        execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
      } catch {
        // already gone
      }
    }
    if (profile) rmSync(profile, { recursive: true, force: true, maxRetries: 5 });
  };
  let page = null;
  try {
    for (let i = 0; i < 300 && !page; i++) {
      const list = await fetch(`http://127.0.0.1:${port}/json/list`).then(
        (r) => r.json(),
        () => [],
      );
      const found = list.find((x) => x.type === 'page');
      if (found) {
        page = new Page(found.webSocketDebuggerUrl);
        await page.ready;
      } else await sleep(1000);
    }
    if (!page) throw new Error(`no editor page on port ${port}`);
    // The window's first page load can lose the race with the dev server; load it again then.
    for (let i = 0; i < 120; i++) {
      const ok = await page.run(() => typeof window.forgeAI === 'object' && document.title === 'ForgeSkin').catch(() => false);
      if (ok) break;
      if (i === 30 && !attach) await page.send('Page.navigate', { url: devUrl });
      await sleep(1000);
    }
  } catch (e) {
    close();
    throw e;
  }
  return { page, close };
}
