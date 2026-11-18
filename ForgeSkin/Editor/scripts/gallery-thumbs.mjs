// Makes the weapon gallery's images (src/renderer/thumbs/<item id>.webp, drawn at 512 x 256 and trimmed to the item) of every catalog item from the
// installed game, with the editor's own thumbnail renderer (viewport/thumbnails.ts itemImage): it starts the development
// editor with --ai-control and a temporary game cache (the full preview of every item is needed once, then deleted). The
// images are build output, not kept in Git (media derived from the game stay local; build-editor-release.ps1 runs this
// before packaging and they ship inside the app). An item without one is drawn when it is first opened.
//   node scripts/gallery-thumbs.mjs               every item
//   node scripts/gallery-thumbs.mjs --port 9480   use a development editor already started with --ai-control=9480
//   node scripts/gallery-thumbs.mjs --check       exits 1 when there are no images
import { cpSync, existsSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { aiPort, editorDir, openEditor } from './dev-editor.mjs';

const outDir = join(editorDir, 'src/renderer/thumbs');
if (process.argv.includes('--check')) {
  const none = !existsSync(outDir) || !readdirSync(outDir).some((f) => f.endsWith('.webp'));
  if (none) console.error('gallery images missing (run node scripts/gallery-thumbs.mjs)');
  process.exit(none ? 1 : 0);
}

const { port, attach } = aiPort(9482);
const cache = mkdtempSync(join(tmpdir(), 'forgeskin-thumbs-'));
// Written into src/ only after the editor is closed: a new file there makes the dev server reload the page.
const made = mkdtempSync(join(tmpdir(), 'forgeskin-thumbs-out-'));
const { page, close } = await openEditor(port, { attach, env: { FORGESKIN_GAME_CACHE: cache } });
try {
  const ids = await page.run(async () => {
    const { useStore } = await import('/src/renderer/store.ts');
    for (let i = 0; i < 300 && !useStore.getState().catalog.length; i++) await new Promise((r) => setTimeout(r, 1000));
    return useStore.getState().catalog.map((c) => c.id);
  });
  if (!ids.length) throw new Error('the editor read no catalog (is GTFO installed?)');
  let bytes = 0;
  const failed = [];
  for (const id of ids) {
    const url = await page
      .run(async (_, id) => {
        const { call } = await import('/src/renderer/protocol.ts');
        const { itemImage } = await import('/src/renderer/viewport/thumbnails.ts');
        const preview = await call('preview.weapon', { id });
        return preview.damaged || preview.modified ? null : itemImage(id, preview);
      }, id)
      .catch((e) => (console.error(`  ${id}: ${e.message}`), null));
    if (!url) {
      failed.push(id);
      continue;
    }
    const buf = Buffer.from(url.split(',')[1], 'base64');
    writeFileSync(join(made, `${id}.webp`), buf);
    bytes += buf.length;
  }
  close();
  rmSync(outDir, { recursive: true, force: true });
  cpSync(made, outDir, { recursive: true });
  console.log(`${ids.length - failed.length} gallery images, ${Math.round(bytes / 1024)} KB${failed.length ? `; none for ${failed.join(', ')}` : ''}`);
} finally {
  close();
  // The backend may hold the cache a moment after it exits.
  for (const dir of [cache, made]) rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
}
