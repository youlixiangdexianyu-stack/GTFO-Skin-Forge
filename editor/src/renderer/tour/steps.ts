// The first-start guide as data: each step names the control to light up, its texts and when it is done. Tour.tsx only
// reads this list. A target is found by CSS plus, optionally, its accessible name (aria-label or its own text) in the UI
// language; the first visible target of a step wins, so a step lists the control it wants last and what opens it after.
import { profileProblem } from '../actions';
import { writePng } from '../frames';
import type zh from '../locales/zh.json';
import { currentItem, useStore } from '../store';

export type Key = keyof typeof zh;
export type EditorState = ReturnType<typeof useStore.getState>;

export interface Target {
  css: string;
  name?: Key;
}

/** What the editor looked like when a step began; `done` compares against it. */
export interface Start {
  profilePath: string | null;
  weaponId: string | null;
  skinIds: string[];
  textures: string;
  projectPath: string | null;
}

export interface TourStep {
  /** Texts are `tour.<id>` and `tour.<id>Info`. */
  id: 'intro' | 'profile' | 'weapon' | 'newSkin' | 'part' | 'texture' | 'preview' | 'save' | 'apply' | 'finish';
  targets: Target[];
  /** Next is allowed (else only once `done`); a step with neither always allows it. */
  ready?: (s: EditorState) => boolean;
  /** The player did it: the guide moves on by itself. */
  done?: (s: EditorState, start: Start) => boolean;
  /** A warning to show, in plain words. */
  warn?: (s: EditorState) => string | null;
  /** Offers the sample texture. */
  sample?: boolean;
}

const profile = (s: EditorState) => s.profiles.find((p) => p.path === s.prefs.profilePath);
const textures = (s: EditorState) => JSON.stringify((s.doc.materials ?? []).map((m) => m?.textures ?? null));
const loaded = (s: EditorState) => !!s.weaponId && !!s.preview && !s.previewLoading;
const fresh = (s: EditorState, start: Start) => loaded(s) && s.doc.target?.weapon === s.weaponId && !start.skinIds.includes(s.doc.id ?? '');
const installed = (s: EditorState) => s.installed.some((p) => p.id === s.doc.id && p.active);
const profileWarn = (s: EditorState) => profileProblem(profile(s));

export const snapshot = (s: EditorState): Start => ({
  profilePath: s.prefs.profilePath,
  weaponId: s.weaponId,
  skinIds: [...s.skins.map((k) => k.id), s.doc.id ?? ''],
  textures: textures(s),
  projectPath: s.projectPath,
});

export const STEPS: TourStep[] = [
  { id: 'intro', targets: [] },
  {
    id: 'profile',
    targets: [{ css: '.mantine-Menu-dropdown' }, { css: '.topbar .target' }],
    ready: (s) => !profileWarn(s),
    done: (s, start) => !profileWarn(s) && s.prefs.profilePath !== start.profilePath,
    warn: profileWarn,
  },
  {
    id: 'weapon',
    targets: [{ css: '.gallery' }, { css: 'button', name: 'gallery.open' }],
    ready: loaded,
    done: (s, start) => loaded(s) && s.weaponId !== start.weaponId,
  },
  {
    id: 'newSkin',
    targets: [
      { css: '[role=menuitem]', name: 'skins.new' },
      { css: 'button', name: 'skins.add' },
      { css: '.rail-tab', name: 'skins.title' },
    ],
    done: fresh,
  },
  {
    id: 'part',
    targets: [{ css: '.rail' }, { css: '.rail-tab', name: 'leftRail.parts' }],
    done: (s) => s.selection?.kind === 'part',
  },
  {
    id: 'texture',
    targets: [{ css: '[role=group]', name: 'skinInspector.k1' }, { css: '.rail-tab', name: 'tabs.texture' }, { css: '.rail' }],
    done: (s, start) => textures(s) !== start.textures,
    sample: true,
  },
  { id: 'preview', targets: [{ css: '.stage' }] },
  {
    id: 'save',
    targets: [
      { css: '[role=menuitem]', name: 'skins.saveFile' },
      { css: '.skin-card[aria-current="true"] button', name: 'skins.more' },
      { css: '.rail-tab', name: 'skins.title' },
    ],
    // Autosaved is enough to go on; saving a file of one's own (a system dialog) finishes the step by itself.
    ready: (s) => !s.autosaveError && s.skins.some((k) => k.id === s.doc.id),
    done: (s, start) => !!s.projectPath && s.projectPath !== start.projectPath,
  },
  {
    id: 'apply',
    targets: [{ css: 'button', name: 'topBar.k45' }],
    ready: (s) => installed(s) || !!profileWarn(s),
    done: installed,
    warn: profileWarn,
  },
  { id: 'finish', targets: [] },
];

/** A loud striped albedo that reads well on any gun, drawn here so a new player needs no image of their own. */
function sampleCanvas(): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = c.height = 1024;
  const g = c.getContext('2d')!;
  g.fillStyle = '#20303a';
  g.fillRect(0, 0, 1024, 1024);
  g.save();
  g.translate(512, 512);
  g.rotate(-Math.PI / 4);
  g.fillStyle = '#ff7a2f';
  for (let x = -1024; x < 1024; x += 160) g.fillRect(x, -1024, 64, 2048);
  g.fillStyle = '#63d2e4';
  for (let x = -944; x < 1024; x += 160) g.fillRect(x, -1024, 14, 2048);
  g.restore();
  g.strokeStyle = 'rgba(233, 228, 216, 0.18)';
  g.lineWidth = 2;
  for (let i = 0; i <= 1024; i += 64) {
    g.beginPath();
    g.moveTo(i, 0);
    g.lineTo(i, 1024);
    g.moveTo(0, i);
    g.lineTo(1024, i);
    g.stroke();
  }
  return c;
}

/** Puts the sample texture on the selected part's albedo (else the receiver's, else the first part with a material). */
export async function putSampleTexture(): Promise<void> {
  const s = useStore.getState();
  const item = currentItem();
  const sel = s.selection;
  const picked = sel?.kind === 'part' ? item?.parts.find((p) => p.id === sel.id && p.materials.length) : undefined;
  const part = picked ?? item?.parts.find((p) => p.role === 'receiver' && p.materials.length) ?? item?.parts.find((p) => p.materials.length);
  if (!part) return;
  const path = `${s.dataDir}\\tutorial\\sample-albedo.png`;
  if (!(await window.forge.exists(path))) await writePng(path, sampleCanvas());
  s.select({ kind: 'part', id: part.id });
  s.setTexture(part.materials[0], part.id, 'albedo', path);
}
