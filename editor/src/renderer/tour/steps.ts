// The first-start guide as data: each step names the control to light up, its texts and when it is done. Tour.tsx only
// reads this list. A target is found by CSS plus, optionally, its accessible name (aria-label or its own text) in the UI
// language; the first visible target of a step wins, so a step lists the control it wants last and what opens it after.
import { profileProblem } from '../actions';
import { matchReady, useCharacter } from '../character/state';
import { writePng } from '../frames';
import type zh from '../locales/zh.json';
import { currentItem, useStore } from '../store';

export type Key = keyof typeof zh;
/** A step that has its two texts. */
type StepId = { [K in Key]: K extends `tour.${infer I}Info` ? (`tour.${I}` extends Key ? I : never) : never }[Key];
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
  id: StepId;
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

/** The left panel's works tab: what opens the profile row, the new button and the work cards when another tab shows. */
const WORKS: Target = { css: '.rail-tab', name: 'works.tab' };

/** The left panel's other tab: a gun's parts, a character's steps. */
const PARTS: Target = { css: '[data-tour=tab-parts]' };
/** The card that 应用 sits on: the open work's. */
const CURRENT: Target = { css: '.work[aria-current="true"]' };
const character = () => useCharacter.getState();
const characterInUse = (s: EditorState) => s.installed.some((p) => p.id === character().skinId && p.inUse);

const OTHERS = {
  character: [
    { id: 'charIntro', targets: [] },
    { id: 'charMode', targets: [{ css: '[data-tour=mode]' }, WORKS], done: () => character().mode === 'character' },
    { id: 'charNew', targets: [{ css: '[data-tour=new]' }, WORKS], done: () => !!character().model },
    { id: 'charBones', targets: [{ css: '.insp-head' }, { css: '.rail' }], ready: () => matchReady(character().check) },
    { id: 'charSprings', targets: [{ css: '.rail' }, PARTS] },
    { id: 'charApply', targets: [{ css: '[data-tour=apply]' }, CURRENT, WORKS], ready: (s) => characterInUse(s) || !!profileWarn(s), done: characterInUse, warn: profileWarn },
    { id: 'charFinish', targets: [] },
  ],
  profiles: [
    { id: 'profIntro', targets: [] },
    { id: 'profPick', targets: [{ css: '.mantine-Menu-dropdown' }, { css: '[data-tour=profile]' }, WORKS], warn: profileWarn },
    { id: 'profStates', targets: [{ css: '.lib-list' }, WORKS] },
    { id: 'profMore', targets: [CURRENT, { css: '.lib-list' }, WORKS] },
    { id: 'profOthers', targets: [{ css: '[data-tour=others]' }, { css: '[data-tour=new-more]' }, WORKS] },
    { id: 'profFinish', targets: [] },
  ],
  hotReload: [
    { id: 'hotIntro', targets: [] },
    { id: 'hotStatus', targets: [{ css: '[data-tour=hot-reload]' }] },
    { id: 'hotApply', targets: [{ css: '[data-tour=apply]' }, CURRENT, WORKS] },
    { id: 'hotAuto', targets: [{ css: '.mantine-Menu-dropdown' }, { css: '[data-tour=settings]' }] },
    { id: 'hotFinish', targets: [] },
  ],
  schemes: [
    { id: 'schemeIntro', targets: [] },
    { id: 'schemeWhole', targets: [{ css: '[data-tour=whole]' }, PARTS], done: (s) => loaded(s) && !s.selection },
    { id: 'schemePlayer', targets: [{ css: '[data-section=player-colours]' }, { css: '[data-tour=tab-look]' }] },
    { id: 'schemeCards', targets: [{ css: '[data-section=color-schemes]' }, { css: '[data-tour=tab-look]' }] },
    { id: 'schemeFinish', targets: [] },
  ],
} satisfies Record<string, TourStep[]>;

const BASICS: TourStep[] = [
  { id: 'intro', targets: [] },
  {
    id: 'profile',
    targets: [{ css: '.mantine-Menu-dropdown' }, { css: '[data-tour=profile]' }, WORKS],
    ready: (s) => !profileWarn(s),
    done: (s, start) => !profileWarn(s) && s.prefs.profilePath !== start.profilePath,
    warn: profileWarn,
  },
  {
    id: 'weapon',
    targets: [{ css: '.gallery' }, { css: '[data-tour=new]' }, WORKS],
    ready: loaded,
    done: (s, start) => loaded(s) && s.weaponId !== start.weaponId,
  },
  {
    id: 'newSkin',
    targets: [{ css: '.gallery' }, { css: '[data-tour=new]' }, WORKS],
    done: fresh,
  },
  {
    id: 'part',
    // A click on the gun in the 3D view picks a part whichever list the left column shows.
    targets: [{ css: '.stage' }],
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
    targets: [{ css: '[role=menuitem]', name: 'skins.saveFile' }, { css: '.work[aria-current="true"] button', name: 'skins.more' }, WORKS],
    // Autosaved is enough to go on; saving a file of one's own (a system dialog) finishes the step by itself.
    ready: (s) => !s.autosaveError && s.skins.some((k) => k.id === s.doc.id),
    done: (s, start) => !!s.projectPath && s.projectPath !== start.projectPath,
  },
  {
    id: 'apply',
    // 应用 on the open work's card; the works tab first when another tab shows.
    targets: [{ css: '[data-tour=apply]' }, WORKS],
    ready: (s) => installed(s) || !!profileWarn(s),
    done: installed,
    warn: profileWarn,
  },
  { id: 'finish', targets: [] },
];

/** The tutorials, each opened on its own from the help menu (`tour.chapter.<id>`); `basics` also from the empty window. */
export const CHAPTERS = { basics: BASICS, ...OTHERS };
export type Chapter = keyof typeof CHAPTERS;
export const isChapter = (x: string | null): x is Chapter => !!x && x in CHAPTERS;

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
