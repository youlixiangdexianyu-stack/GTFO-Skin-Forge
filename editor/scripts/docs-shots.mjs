// Makes the tutorial's pictures (src/renderer/docs/images/<lang>/<name>.webp and common/<name>.webp) from the editor itself:
// it starts the development editor with --ai-control (its own temporary Chromium profile, so it never touches another
// editor's settings), builds the worked-example skin on the Omneco LRG (helr) through window.forgeAI, opens each panel and
// screenshots it in both languages, then deletes the skin and closes the editor. The pictures are build output, not kept in
// Git (media stay local; build-editor-release.ps1 runs this before packaging). Needs GTFO installed, like the editor.
//   node scripts/docs-shots.mjs                 every picture, both languages
//   node scripts/docs-shots.mjs --only a,b      just these (names as in the Markdown, `shot:<name>`)
//   node scripts/docs-shots.mjs --port 9480     use a development editor already started with --ai-control=9480
//   node scripts/docs-shots.mjs --character m.vrm   also the 人物模型 pictures, on that humanoid model
//   node scripts/docs-shots.mjs --check         exits 1 when a picture the Markdown names is missing
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { aiPort, arg, editorDir, openEditor, sleep } from './dev-editor.mjs';

const docsDir = join(editorDir, 'src/renderer/docs');
const outDir = join(docsDir, 'images');
const LANGS = ['zh', 'en'];

/** Pictures each language's Markdown names. */
function referenced() {
  return Object.fromEntries(LANGS.map((l) => [l, [...readFileSync(join(docsDir, `${l}.md`), 'utf8').matchAll(/\]\(shot:([\w-]+)\)/g)].map((m) => m[1])]));
}
const have = (lang, name) => existsSync(join(outDir, lang, `${name}.webp`)) || existsSync(join(outDir, 'common', `${name}.webp`));

if (process.argv.includes('--check')) {
  const missing = Object.entries(referenced()).flatMap(([l, names]) => names.filter((n) => !have(l, n)).map((n) => `${l}/${n}`));
  if (missing.length) console.error(`tutorial pictures missing (run node scripts/docs-shots.mjs): ${[...new Set(missing)].join(', ')}`);
  process.exit(missing.length ? 1 : 0);
}

// ---- helpers inside the page ----------------------------------------------------------------------------------------

async function helpers() {
  const { t } = await import('/src/renderer/i18n.ts');
  const { useStore } = await import('/src/renderer/store.ts');
  const D = {
    t,
    store: () => useStore.getState(),
    ai: (c, a) => window.forgeAI.command(c, a ?? {}).then((r) => (r.ok ? r : Promise.reject(new Error(`${c}: ${r.code} ${r.message}`)))),
    sleep: (ms) => new Promise((r) => setTimeout(r, ms)),
    settle: async (ms = 400) => {
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      await D.sleep(ms);
    },
    visible: (el) => !!el && el.getClientRects().length > 0,
    /** The visible element named `name` (aria-label or its own text), among `sel` under `root`. */
    find(name, sel = 'button,[role=button],[role=menuitem],[role=tab],[role=option],a,label', root = document) {
      return [...root.querySelectorAll(sel)].find((el) => D.visible(el) && (el.getAttribute('aria-label') === name || el.textContent.trim() === name)) ?? null;
    },
    click: async (el, ms) => {
      if (!el) throw new Error('nothing to click');
      el.click();
      await D.settle(ms);
    },
    tab: (key) => D.click(document.querySelector(`.rail-tab[aria-label="${CSS.escape(t(`tabs.${key}`))}"]`)),
    /** The inspector section titled `title` (a label of the locale). */
    section: (title) => [...document.querySelectorAll('section.section')].find((s) => D.visible(s) && s.querySelector('h3')?.textContent.trim().startsWith(title)) ?? null,
    left: () => document.querySelectorAll('.inspector-pane')[0],
    right: () => [...document.querySelectorAll('.inspector-pane')].pop(),
    stage: () => document.querySelector('main.stage'),
    menu: () => [...document.querySelectorAll('.mantine-Menu-dropdown')].filter(D.visible).pop() ?? null,
    popover: () => [...document.querySelectorAll('.mantine-Popover-dropdown')].filter(D.visible).pop() ?? null,
    /** Clicks the button named `name` until its menu or popover shows (a first click may only close another one). */
    dropdown: async (name) => {
      for (let i = 0; i < 3 && !(D.menu() ?? D.popover()); i++) await D.click(D.find(name), 500);
      return D.menu() ?? D.popover();
    },
    modal: () => [...document.querySelectorAll('.mantine-Modal-content')].filter(D.visible).pop() ?? null,
    rect(el, pad = 0) {
      const r = el.getBoundingClientRect();
      return { x: r.x - pad, y: r.y - pad, w: r.width + 2 * pad, h: r.height + 2 * pad };
    },
    union(rects, pad = 8) {
      const x = Math.min(...rects.map((r) => r.x)),
        y = Math.min(...rects.map((r) => r.y));
      return { x: x - pad, y: y - pad, w: Math.max(...rects.map((r) => r.x + r.w)) - x + 2 * pad, h: Math.max(...rects.map((r) => r.y + r.h)) - y + 2 * pad };
    },
    /** A box from the top of `el` down `h` pixels (a long panel cut where the picture has said enough). */
    top: (el, h) => ({ ...D.rect(el), h: Math.min(D.rect(el).h, h) }),
    /** Numbered callouts: an outline round each element and its number at the top left. */
    mark(items) {
      D.unmark();
      const layer = Object.assign(document.createElement('div'), { id: 'docs-marks' });
      layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:100000';
      for (const { el, n, side } of items) {
        if (!el) continue;
        const r = el.getBoundingClientRect();
        const box = document.createElement('div');
        box.style.cssText = `position:fixed;left:${r.x - 3}px;top:${r.y - 3}px;width:${r.width + 6}px;height:${r.height + 6}px;border:2px solid #ffb02e;border-radius:6px;box-shadow:0 0 0 1px rgba(0,0,0,.6)`;
        const dot = document.createElement('div');
        const left = Math.max(3, Math.min(innerWidth - 25, side === 'right' ? r.right - 9 : r.x - 13));
        dot.style.cssText = `position:fixed;left:${left}px;top:${Math.max(3, r.y - 13)}px;width:22px;height:22px;border-radius:50%;background:#ffb02e;color:#1a1209;font:700 13px/22px Segoe UI,sans-serif;text-align:center;box-shadow:0 0 0 2px #1a1209`;
        dot.textContent = String(n);
        layer.append(box, dot);
      }
      document.body.append(layer);
    },
    unmark: () => document.getElementById('docs-marks')?.remove(),
    escape: async () => {
      document.activeElement?.blur?.();
      for (let i = 0; i < 2; i++) document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      document.querySelectorAll('.mantine-Modal-close').forEach((b) => D.visible(b) && b.click());
      await D.settle(300);
    },
    /** Shows a step of the 人物模型 side. */
    character: async (step) => {
      const { useCharacter } = await import('/src/renderer/character/state.ts');
      useCharacter.setState({ mode: 'character', step, clip: null, playing: false });
      await D.settle(900);
    },
    doc: async () => (await window.forgeAI.state()).doc,
    layer: async (material, is) => (await D.doc()).materials.find((m) => m.material === material).livery.find(is),
    attachment: async (name) => (await D.doc()).attachments.find((a) => a.name === name),
    /** The 3D view as WebP, `w` x `h`. */
    render: async (w, h) => {
      const r = await D.ai('render', { width: w, height: h });
      return D.webp([r.dataUrl], w, h);
    },
    /** Pictures (data URLs) side by side, as one WebP. */
    async webp(urls, w, h, gap = 8) {
      const imgs = await Promise.all(
        urls.map(
          (u) =>
            new Promise((ok) =>
              Object.assign(new Image(), {
                onload() {
                  ok(this);
                },
                src: u,
              }),
            ),
        ),
      );
      const c = Object.assign(document.createElement('canvas'), { width: urls.length * w + (urls.length - 1) * gap, height: h });
      const ctx = c.getContext('2d');
      ctx.fillStyle = '#141619';
      ctx.fillRect(0, 0, c.width, c.height);
      imgs.forEach((img, i) => ctx.drawImage(img, i * (w + gap), 0, w, h));
      return c.toDataURL('image/webp', 0.86);
    },
  };
  window.__docs = D;
  return true;
}

// ---- the worked example: Ember Line on the Omneco LRG ----------------------------------------------------------------

const R = 'Receiver_Machinegun_2',
  F = 'Front_Precision_2',
  S = 'Stock_Precision_4';
const MATS = { R, F, S, M: 'Mag_50_20rnds_A', SIGHT: 'Sight_13' };
const CAM = {
  side: { yaw: -90, pitch: 5, distance: 1.5, target: [0, 0, -0.15] },
  hero: { yaw: -130, pitch: 12, distance: 1.3, target: [0, 0.02, -0.2] },
  sight: { yaw: -155, pitch: 8, distance: 0.45, target: [0, 0.11, -0.13] },
  lines: { yaw: -100, pitch: 6, distance: 0.75, target: [0, -0.01, -0.3] },
};

/** Steps of the example; each leaves the skin as the tutorial's step says. */
const EXAMPLE = {
  base: async (D, m) => {
    await D.ai('selectWeapon', { id: 'helr' });
    await D.ai('newSkin');
    await window.forgeAI.patch([{ op: 'set', path: 'name', value: 'Ember Line' }]);
    const fill = async (material, part, color, opacity, finish) => {
      const r = await D.ai('addLivery', { material, part, kind: 'fill' });
      await window.forgeAI.patch([{ op: 'merge', path: `materials/material=${material}/livery/id=${r.id}`, value: { color, opacity, name: D.t('livery.fill') } }]);
      await D.ai('setFinish', { material, part, finish });
    };
    const W = [0.58, 0.59, 0.6, 1],
      G = [0.36, 0.38, 0.4, 1],
      K = [0.05, 0.055, 0.06, 1];
    await fill(m.R, 'receiver', W, 0.85, 'paintedSteel');
    await fill(m.F, 'front', W, 0.85, 'paintedSteel');
    await fill(m.S, 'stock', G, 0.92, 'satin');
    await fill(m.M, 'mag', K, 0.92, 'rubber');
    await fill(m.SIGHT, 'sight', K, 0.92, 'satin');
    await D.ai('select', { clear: true });
  },
  lines: async (D, m) => {
    const O = [1, 0.42, 0.08, 1];
    const add = async (material, part, kind, value, place) => {
      const r = await D.ai('addLivery', { material, part, kind });
      await window.forgeAI.patch([{ op: 'merge', path: `materials/material=${material}/livery/id=${r.id}`, value }]);
      await D.ai('placeDecal', { material, id: r.id, normal: [-1, 0, 0], ...place });
    };
    await add(
      m.F,
      'front',
      'trim',
      { name: D.t('livery.trimPreset.panelGrid'), color: O, glow: true, symmetric: true, trim: { style: 'panels', width: 0.0006, corner: 0.003, inset: 0, dash: 0, gap: 0.002, count: 4 } },
      { point: [-0.03, -0.02, -0.29], size: [0.22, 0.05], depth: 0.04 },
    );
    await add(
      m.F,
      'front',
      'trim',
      { name: D.t('livery.trimPreset.hazard'), color: O, glow: false, symmetric: true, trim: { style: 'hazard', width: 0.0006, corner: 0, inset: 0, dash: 0, gap: 0.001, count: 8 } },
      { point: [-0.03, 0, -0.55], size: [0.07, 0.012], depth: 0.02 },
    );
    await add(
      m.R,
      'receiver',
      'text',
      { name: 'EMBER-07', color: [0.08, 0.08, 0.09, 1], symmetric: true, text: { content: 'EMBER-07', font: 'Orbitron', sizePx: 64, color: [1, 1, 1, 1], tracking: 0.12 } },
      { point: [-0.013, 0.03, 0.04], size: [0.05, 0.008], depth: 0.01 },
    );
    const g = await D.ai('addLivery', { material: m.S, part: 'stock', kind: 'gradient' });
    const gl = (await D.doc()).materials.find((x) => x.material === m.S).livery.find((x) => x.id === g.id);
    await window.forgeAI.patch([
      {
        op: 'merge',
        path: `materials/material=${m.S}/livery/id=${g.id}`,
        value: {
          name: D.t('livery.gradient'),
          opacity: 0.9,
          gradient: {
            ...gl.gradient,
            stops: [
              { at: 0.15, color: [0.36, 0.38, 0.4, 1] },
              { at: 0.85, color: [0.05, 0.055, 0.06, 1] },
            ],
          },
        },
      },
    ]);
    await D.ai('select', { clear: true });
  },
  screen: async (D) => {
    await D.ai('select', { kind: 'part', id: 'receiver' });
    const { id } = await D.ai('addAttachment', { kind: 'screen' });
    await window.forgeAI.patch([
      { op: 'merge', path: `attachments/id=${id}`, value: { position: [-0.02, 0.07, 0.06], rotation: [0, 90, 0] } },
      {
        op: 'merge',
        path: `attachments/id=${id}/screen`,
        value: { size: [0.04, 0.018], textColor: [1, 0.5, 0.15, 1], borderColor: [1, 0.42, 0.08, 1], borderStyle: 'corners', panelColor: [0.02, 0.02, 0.025, 0.9], digits: 2 },
      },
    ]);
    await D.ai('select', { clear: true });
  },
  holo: async (D) => {
    const shapes = await import('/src/renderer/shapes.ts');
    await D.ai('select', { kind: 'part', id: 'sight' });
    await shapes.addShape('reticle');
    await D.ai('select', { kind: 'part', id: 'front' });
    await shapes.addShape('hexPanel', true);
    const list = (await D.doc()).attachments;
    const [ret, hex] = [list.at(-2), list.at(-1)];
    const s = shapes.shapeOf((await D.doc()).editor, ret.id);
    shapes.setShape(ret.id, { ...s, params: { ...s.params, radius: 0.016, ticks: 36 } }, 'radius');
    await D.sleep(900);
    await window.forgeAI.patch([
      { op: 'merge', path: `attachments/id=${ret.id}`, value: { position: [0.0014, 0.06, 0.085] } },
      { op: 'set', path: `attachments/id=${ret.id}/hologram`, value: { flicker: 0.15, opacity: 0.9, color2: [1, 0.9, 0.75], scanlines: 0.5, edgeGlow: 0.6 } },
      { op: 'set', path: `attachments/id=${hex.id}/hologram`, value: { flicker: 0.3, opacity: 1, color2: null, scanlines: 0, edgeGlow: 0 } },
      { op: 'merge', path: `attachments/id=${hex.id}/charm`, value: { style: 'chain', length: 0.035, color: [0.2, 0.21, 0.22] } },
    ]);
    await D.ai('select', { clear: true });
  },
};

/** Pictures of the 3D view alone (no words in them), shared by both languages. */
const RENDERS = {
  'example-base': async (D, { CAM }) => (await D.ai('camera', CAM.side), D.render(1200, 560)),
  'example-lines': async (D, { CAM }) => (await D.ai('camera', CAM.side), D.render(1200, 560)),
  'panel-lines-before-after': async (D, { CAM, m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'panels');
    const path = `materials/material=${m.F}/livery/id=${l.id}`;
    await D.ai('camera', CAM.lines);
    await window.forgeAI.patch([{ op: 'set', path: `${path}/visible`, value: false }]);
    const a = (await D.ai('render', { width: 640, height: 400 })).dataUrl;
    await window.forgeAI.patch([{ op: 'set', path: `${path}/visible`, value: true }]);
    await D.settle(600);
    const b = (await D.ai('render', { width: 640, height: 400 })).dataUrl;
    return D.webp([a, b], 640, 400);
  },
  'hologram-before-after': async (D, { CAM }) => {
    const ret = (await D.doc()).attachments.find((a) => a.hologram && !a.charm);
    const path = `attachments/id=${ret.id}/hologram`;
    const full = ret.hologram;
    await D.ai('camera', CAM.sight);
    await window.forgeAI.patch([{ op: 'set', path, value: { ...full, color2: null, scanlines: 0, edgeGlow: 0 } }]);
    await D.settle(600);
    const a = (await D.ai('render', { width: 520, height: 400 })).dataUrl;
    await window.forgeAI.patch([{ op: 'set', path, value: full }]);
    await D.settle(600);
    const b = (await D.ai('render', { width: 520, height: 400 })).dataUrl;
    return D.webp([a, b], 520, 400);
  },
  'example-final': async (D, { CAM }) => (await D.ai('camera', CAM.hero), D.render(1200, 640)),
  'example-sight': async (D, { CAM }) => (await D.ai('camera', CAM.sight), D.render(900, 520)),
};

// ---- screenshots of the editor, per language ------------------------------------------------------------------------
// Each returns the box to keep (CSS pixels); the page is 1280 x 800.

const SHOTS = {
  'editor-map': async (D) => {
    await D.ai('select', { kind: 'part', id: 'front' });
    await D.ai('camera', { yaw: -120, pitch: 14, distance: 1.25, target: [0, 0, -0.15] });
    await D.tab('livery');
    const top = document.querySelector('.topbar, header') ?? document.body;
    D.mark([
      { el: D.left(), n: 1 },
      { el: D.find(D.t('gallery.open')) ?? D.stage().querySelector('h1,h2,button'), n: 2 },
      { el: D.find(D.t('stage.simulate', { v0: D.t('trigger.kill') }))?.parentElement ?? D.find(D.t('trigger.kill'))?.parentElement, n: 3 },
      { el: D.right(), n: 4 },
      { el: [...document.querySelectorAll('.rail-tab')].pop()?.parentElement, n: 5, side: 'right' },
      { el: D.find(D.t('topBar.publish')), n: 6 },
      { el: D.find(D.t('topBar.k45')), n: 7 },
    ]);
    void top;
    return { x: 0, y: 0, w: 1280, h: 800, max: 1200 };
  },
  gallery: async (D) => {
    await D.click(D.find(D.t('gallery.open')), 700);
    if (![...document.querySelectorAll('.gallery')].some(D.visible)) await D.click(D.find(D.t('gallery.open')), 700);
    const g = [...document.querySelectorAll('.gallery')].find(D.visible);
    return { ...D.union([D.rect(D.find(D.t('gallery.open'))), D.top(g, 520)]), max: 1000 };
  },
  'skins-panel': async (D) => {
    await D.click(D.find(D.t('works.tab')), 500);
    return D.top(D.left(), 430);
  },
  'textures-tab': async (D) => {
    await D.click(D.find(D.t('works.tabParts')), 300);
    await D.ai('select', { kind: 'part', id: 'receiver' });
    await D.tab('texture');
    return D.top(D.right(), 560);
  },
  'livery-tab': async (D) => {
    await D.ai('select', { kind: 'part', id: 'front' });
    await D.tab('livery');
    const grid = document.querySelector('.kind-grid');
    const list = grid?.parentElement?.querySelector('.list');
    D.mark([
      { el: grid, n: 1 },
      { el: list, n: 2 },
      { el: D.section(D.t('livery.finish'))?.querySelector('.finishes'), n: 3 },
    ]);
    return D.top(D.right(), 760);
  },
  'add-menu': async (D) => {
    await D.ai('select', { kind: 'part', id: 'front' });
    const row = D.find(D.t('name.front'), '[role=option]', D.left()) ?? [...D.left().querySelectorAll('[role=option]')].find((r) => r.getAttribute('aria-selected') === 'true');
    const plus = [...row.querySelectorAll('button')].pop();
    await D.click(plus, 400);
    const menu = D.menu();
    return D.union([D.rect(row), D.rect(menu)]);
  },
  'panel-lines': async (D, { m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'panels');
    await D.ai('select', { kind: 'livery', material: m.F, id: l.id });
    await D.ai('camera', { yaw: -100, pitch: 6, distance: 0.75, target: [0, -0.01, -0.3] });
    await D.settle(500);
    const sec = D.section(D.t('livery.trim'));
    D.mark([
      { el: sec.querySelector('.trim-presets'), n: 1 },
      { el: D.find(D.t('livery.trimStyle'), '[role=group]', sec), n: 2 },
    ]);
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(sec)], 4);
  },
  'layer-look': async (D, { m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'panels');
    await D.ai('select', { kind: 'livery', material: m.F, id: l.id });
    const sec = D.section(D.t('livery.look'));
    sec.scrollIntoView({ block: 'start' });
    await D.settle();
    return D.rect(sec, 4);
  },
  placement: async (D, { m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'panels');
    await D.ai('select', { kind: 'livery', material: m.F, id: l.id });
    const sec = D.section(D.t('livery.placement'));
    sec.scrollIntoView({ block: 'start' });
    await D.settle();
    return D.rect(sec, 4);
  },
  'copy-to-parts': async (D, { m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'panels');
    await D.ai('select', { kind: 'livery', material: m.F, id: l.id });
    document.querySelector('.inspector-pane')?.scrollTo?.(0, 0);
    await D.click(D.find(D.t('livery.copyTo')), 500);
    const pop = [...document.querySelectorAll('.mantine-Popover-dropdown')].filter(D.visible).pop();
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(pop)]);
  },
  'text-layer': async (D, { m }) => {
    const l = await D.layer(m.R, (l) => l.kind === 'text');
    await D.ai('select', { kind: 'livery', material: m.R, id: l.id });
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('livery.text')))], 4);
  },
  'gradient-layer': async (D, { m }) => {
    const l = await D.layer(m.S, (l) => l.kind === 'gradient');
    await D.ai('select', { kind: 'livery', material: m.S, id: l.id });
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('livery.gradient')))], 4);
  },
  'mask-fade': async (D, { m }) => {
    const l = await D.layer(m.F, (l) => l.trim?.style === 'hazard');
    await D.ai('select', { kind: 'livery', material: m.F, id: l.id });
    const sec = D.section(D.t('livery.mask'));
    await D.click(D.find(D.t('livery.fade'), 'label', sec), 500);
    sec.scrollIntoView({ block: 'end' });
    await D.settle();
    const r = D.rect(sec, 4);
    window.__undo = true;
    return r;
  },
  'pattern-layer': async (D, { m }) => {
    const { id } = await D.ai('addLivery', { material: m.M, part: 'mag', kind: 'pattern' });
    await D.settle(600);
    const r = D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('livery.pattern')))], 4);
    window.__cleanup = { kind: 'livery', material: m.M, id };
    return r;
  },
  'wear-layer': async (D, { m }) => {
    const { id } = await D.ai('addLivery', { material: m.F, part: 'front', kind: 'wear' });
    await D.settle(600);
    const r = D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('livery.wear')))], 4);
    window.__cleanup = { kind: 'livery', material: m.F, id };
    return r;
  },
  'sticker-dialog': async (D) => {
    const { pickDecalImage } = await import('/src/renderer/stickers.ts');
    void pickDecalImage();
    await D.settle(700);
    return { ...D.top(D.modal(), 520), max: 900 };
  },
  'code-effect': async (D, { m }) => {
    const { addCodeEffect } = await import('/src/renderer/codeFx.ts');
    await D.ai('select', { kind: 'part', id: 'front' });
    addCodeEffect({ material: m.F, part: 'front' });
    await D.settle(900);
    const id = D.store().selection?.id;
    window.__cleanup = { kind: 'fx', id };
    return D.top(D.right(), 780);
  },
  'attach-tab': async (D) => {
    await D.ai('select', { kind: 'part', id: 'receiver' });
    await D.tab('attach');
    return D.top(D.right(), 520);
  },
  'hologram-look': async (D) => {
    const a = (await D.doc()).attachments.find((x) => x.hologram && !x.charm);
    await D.ai('select', { kind: 'attachment', id: a.id });
    await D.ai('camera', { yaw: -140, pitch: 10, distance: 0.42, target: [0, 0.07, -0.02] });
    await D.tab('look');
    const shape = D.section(D.t('shapes.reticle'));
    const holo = D.section(D.t('hologram.title'));
    D.mark([
      { el: shape, n: 1 },
      { el: holo?.querySelector('.section-head'), n: 2 },
    ]);
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(shape), D.rect(holo)], 4);
  },
  'charm-place': async (D) => {
    const a = (await D.doc()).attachments.find((x) => x.charm);
    await D.ai('select', { kind: 'attachment', id: a.id });
    await D.tab('place');
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('charm.title')))], 4);
  },
  'screen-tab': async (D) => {
    const a = (await D.doc()).attachments.find((x) => x.screen);
    await D.ai('select', { kind: 'attachment', id: a.id });
    await D.tab('screen');
    return D.union([D.rect(document.querySelector('.insp-head')), D.rect(D.section(D.t('attachInspector.k19')))], 4);
  },
  'screen-look': async (D) => {
    const a = (await D.doc()).attachments.find((x) => x.screen);
    await D.ai('select', { kind: 'attachment', id: a.id });
    await D.tab('look');
    const sec = D.section(D.t('attachInspector.k34'));
    sec.scrollIntoView({ block: 'start' });
    await D.settle();
    return D.rect(sec, 4);
  },
  overview: async (D) => {
    await D.ai('select', { clear: true });
    const sections = [D.section(D.t('variants.title')), D.section(D.t('palette.title')), D.section(D.t('playerColors.title'))].filter(Boolean);
    sections[0].scrollIntoView({ block: 'start' });
    await D.settle();
    return D.union(
      sections.map((s) => D.rect(s)),
      4,
    );
  },
  'publish-menu': async (D) => {
    return D.union([D.rect(D.find(D.t('topBar.publish'))), D.rect(await D.dropdown(D.t('topBar.publish')))]);
  },
  'apply-menu': async (D) => {
    return D.union([D.rect(D.find(D.t('topBar.k45'))), D.rect(await D.dropdown(D.t('topBar.k46')))]);
  },
  'settings-menu': async (D) => {
    const box = await D.dropdown(D.t('topBar.settings'));
    // The data folder line shows this computer's path; the picture shows the installed app's default instead.
    const walk = document.createTreeWalker(box, NodeFilter.SHOW_TEXT);
    for (let n = walk.nextNode(); n; n = walk.nextNode()) if (/^[A-Z]:\\/.test(n.textContent.trim())) n.textContent = '%USERPROFILE%\\Documents\\ForgeSkin';
    return D.union([D.rect(D.find(D.t('topBar.settings'))), D.rect(box)]);
  },
  'tutorial-page': async (D, { title }) => {
    await D.click(D.find(D.t('help.title')), 600);
    await D.click(D.find(title, '.help-nav a, .help-nav button'), 700);
    return { ...D.rect(D.modal()), max: 1100 };
  },
};

// ---- the 人物模型 side, on the model given with --character ----------------------------------------------------------

const CHARACTER_SHOTS = {
  'character-map': async (D) => {
    await D.character('bones');
    D.mark([
      { el: document.querySelector('.rail'), n: 1 },
      { el: document.querySelector('.overlay.motions'), n: 2 },
      { el: D.right(), n: 3 },
    ]);
    return { x: 0, y: 0, w: 1280, h: 800, max: 1200 };
  },
  'character-import': async (D) => (await D.character('model'), D.top(D.right(), 420)),
  'character-bones': async (D) => (await D.character('bones'), D.top(D.right(), 760)),
  'character-motion': async (D) => {
    await D.character('bones');
    await D.click(D.find(D.t('character.motion.walk'), 'button', document.querySelector('.overlay.motions')), 1200);
    return D.rect(document.querySelector('.overlay.motions'), 8);
  },
  'character-springs': async (D) => (await D.character('springs'), D.top(D.right(), 760)),
  // The steps are model, bones, arms, springs, materials and check (character/state.ts STEPS); export is the top bar's
  // 导出 ▸ 导出模组包 dialog on the character side (character/CharacterPanels.tsx CharacterExportModal).
  'character-export': async (D) => {
    await D.character('check');
    await D.click(D.find(D.t('actions.exportMod'), '[role=menuitem]', await D.dropdown(D.t('topBar.export'))), 900);
    return { ...D.rect(D.modal(), 8), max: 1000 };
  },
};

/** Names the example's layers and attachments in the language shown. */
async function names(D) {
  const ops = [];
  for (const m of (await D.doc()).materials)
    (m.livery ?? []).forEach((l) => {
      const name =
        l.kind === 'fill'
          ? D.t('livery.fill')
          : l.trim?.style === 'panels'
            ? D.t('livery.trimPreset.panelGrid')
            : l.trim?.style === 'hazard'
              ? D.t('livery.trimPreset.hazard')
              : l.kind === 'gradient'
                ? D.t('livery.gradient')
                : null;
      if (name) ops.push({ op: 'set', path: `materials/material=${m.material}/livery/id=${l.id}/name`, value: name });
    });
  for (const a of (await D.doc()).attachments) {
    const name = a.screen ? D.t('store.k21', { v0: 1 }) : a.charm ? D.t('shapes.hexPanel') : a.hologram ? D.t('shapes.reticle') : null;
    if (name) ops.push({ op: 'set', path: `attachments/id=${a.id}/name`, value: name });
  }
  await window.forgeAI.patch(ops);
}

/** Puts back what a shot changed for itself. */
async function tidy(D) {
  D.unmark();
  if (window.__cleanup) {
    const c = window.__cleanup;
    if (c.kind === 'fx') (await import('/src/renderer/codeFx.ts')).removeCodeEffect(c.id);
    else await D.ai('removeLivery', { material: c.material, id: c.id });
    window.__cleanup = null;
  }
  if (window.__undo) {
    await D.ai('undo');
    window.__undo = false;
  }
  const { useStore } = await import('/src/renderer/store.ts');
  if (useStore.getState().stickerPick) {
    const done = useStore.getState().stickerPick;
    useStore.setState({ stickerPick: null });
    done(null);
  }
  const { useCharacter } = await import('/src/renderer/character/state.ts');
  useCharacter.setState({ mode: 'weapon', clip: null, playing: false });
  if ([...document.querySelectorAll('.gallery')].some(D.visible)) await D.click(document.querySelector('.stage-title'));
  await D.escape();
  // Menus and popovers close on a press outside them.
  if (D.menu() ?? D.popover()) {
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup']) document.body.dispatchEvent(new MouseEvent(type, { bubbles: true }));
    await D.settle(300);
  }
}

// ---- run ------------------------------------------------------------------------------------------------------------

const only = arg('--only')?.split(',');
const wanted = (name) => !only || only.includes(name);
const { port, attach } = aiPort(9481);
const character = arg('--character');
// The motion bar the character pictures show exists only with the preview motions staged (they are not in Git).
if (character && !existsSync(join(editorDir, 'src/renderer/motions/walk.glb'))) throw new Error('preview motions missing: run pnpm ts Tools/Models/stage-skin-motions.ts');
let page;
let close = () => {};
let made = 0;
let bytes = 0;
const save = (lang, name, b64) => {
  const buf = Buffer.from(b64, 'base64');
  mkdirSync(join(outDir, lang), { recursive: true });
  writeFileSync(join(outDir, lang, `${name}.webp`), buf);
  made++;
  bytes += buf.length;
  console.log(`  ${lang}/${name}.webp ${Math.round(buf.length / 1024)} KB`);
};
try {
  ({ page, close } = await openEditor(port, { attach }));
  await page.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1.5, mobile: false });
  await page.run(helpers);
  const lang0 = await page.run((D) => D.store().prefs.lang);
  await page.run((D) => D.store().setPrefs({ welcomed: true, lang: 'en' }));
  for (let i = 0; i < 120 && !(await page.run(async () => (await window.forgeAI.command('listWeapons', { query: 'helr' })).items?.length > 0).catch(() => false)); i++) await sleep(1000);
  const ctx = { m: MATS, CAM };
  const shoot = async (lang, name, fn) => {
    if (!wanted(name)) return;
    try {
      const box = await page.run(fn, ctx);
      if (typeof box === 'string') return save(lang, name, box.split(',')[1]);
      const clip = { x: Math.max(0, box.x), y: Math.max(0, box.y), width: Math.min(1280, box.x + box.w) - Math.max(0, box.x), height: Math.min(800, box.y + box.h) - Math.max(0, box.y) };
      const scale = Math.min(1, (box.max ?? 760) / (clip.width * 1.5));
      const shot = await page.send('Page.captureScreenshot', { format: 'webp', quality: 84, clip: { ...clip, scale } });
      save(lang, name, shot.data);
    } finally {
      await page.run(tidy);
    }
  };
  await page.run(tidy);
  // The example, step by step, with its 3D pictures between the steps.
  // --reuse: the example skin is already open (a previous run with --keep); only the screenshots are taken again.
  const reuse = process.argv.includes('--reuse');
  if (!reuse)
    for (const [step, renders] of [
      ['base', ['example-base']],
      ['lines', ['example-lines', 'panel-lines-before-after']],
      ['screen', []],
      ['holo', ['hologram-before-after', 'example-final', 'example-sight']],
    ]) {
      await page.run(EXAMPLE[step].toString(), MATS);
      await page.run((D) => D.settle(1500));
      for (const r of renders) await shoot('common', r, RENDERS[r]);
    }
  // --character <file.glb|.vrm>: the 人物模型 pictures, on that model (imported as a character skin of its own).
  if (character)
    await page.run(async (D, file) => {
      const { importModel } = await import('/src/renderer/character/model.ts');
      const { useCharacter } = await import('/src/renderer/character/state.ts');
      useCharacter.setState({ mode: 'character' });
      await importModel(file);
      for (let i = 0; i < 120 && (useCharacter.getState().loading || !useCharacter.getState().check); i++) await D.sleep(1000);
    }, character);
  for (const lang of LANGS) {
    // Back on the 武器 side, its 3D view mounts again after the character pictures.
    await page.run(async (D, l) => {
      const { useCharacter } = await import('/src/renderer/character/state.ts');
      useCharacter.setState({ mode: 'weapon' });
      D.store().setPrefs({ lang: l });
      await D.settle(3000);
    }, lang);
    await page.run(names);
    await page.run((D) => D.settle(800));
    ctx.title = readFileSync(join(docsDir, `${lang}.md`), 'utf8').match(/^# (.*?) \{#example\}/m)?.[1];
    for (const [name, fn] of Object.entries(SHOTS)) await shoot(lang, name, fn);
    if (character) for (const [name, fn] of Object.entries(CHARACTER_SHOTS)) await shoot(lang, name, fn);
  }
  if (!process.argv.includes('--keep')) await page.run((D) => D.store().deleteSkin(D.store().doc.id));
  await page.run((D, l) => D.store().setPrefs({ lang: l }), lang0);
  const missing = Object.entries(referenced()).flatMap(([l, names]) => names.filter((n) => !have(l, n)).map((n) => `${l}/${n}`));
  console.log(`${made} pictures, ${Math.round(bytes / 1024)} KB${missing.length ? `; still missing: ${missing.join(', ')}` : ''}`);
} finally {
  close();
}
