// What a code effect (SkinDocument.codeEffects) draws with: the editor's half of Core/CodeEffect.cs `CodeEffects.Resolve`, the same
// rules (catalogue order, the program's defaults, each value kept in its range, player changes only where allowed), and the program
// dispatch built from the catalogue's order as ForgeCore's shader build does. No imports and no three.js: FormatTests runs it under
// node against the game's resolution, with the catalogue (ForgeCore Visual/Data/fx-programs.json) it reads itself.

export interface FxLabel {
  en: string;
  zh: string;
}
export interface FxParam {
  name: string;
  label: FxLabel;
  unit: string;
  min: number;
  max: number;
  step: number;
  default: number;
}
export interface FxColour {
  name: string;
  label: FxLabel;
  default: string;
}
export interface FxProgram {
  name: string;
  label: FxLabel;
  params: FxParam[];
  colours: FxColour[];
}
export interface FxCatalog {
  transform: FxParam[];
  programs: FxProgram[];
}

/** The fields of a code effect the values come from. */
export interface FxSource {
  id?: string;
  program?: string;
  params?: Record<string, number> | null;
  colors?: Record<string, (number | null)[] | null | undefined> | null;
  scale?: number;
  rotation?: number;
  spin?: number;
  intensity?: number;
  playerTweaks?: { params?: (string | null)[] | null; colors?: (string | null)[] | null; transform?: (string | null)[] | null } | null;
}

export type TweakKind = 'param' | 'color' | 'transform';

/** The program's index, its eight numbers, four sRGB RGBA colours (unused 0) and the transform (scale, rotation, spin, intensity). */
export interface FxValues {
  program: number;
  params: number[];
  colors: number[][];
  transform: number[];
}

/** The key of a player's change in ForgeSkin.colours.json (Core CodeEffects.TweakKey). */
export const tweakKey = (effect: string, kind: TweakKind, name: string) => `fx:${effect}/${kind}/${name}`;

/** "#RRGGBB" or "#RRGGBBAA" as sRGB RGBA 0..1 (ForgeCore ProceduralCatalog.Colour). */
export function hexColour(hex: string): number[] {
  const v = hex.slice(1);
  const n = (i: number) => parseInt(v.slice(i, i + 2), 16) / 255;
  return [n(0), n(2), n(4), v.length >= 8 ? n(6) : 1];
}

const clamp = (p: FxParam, v: number | undefined | null) => (typeof v === 'number' && Number.isFinite(v) ? Math.min(p.max, Math.max(p.min, v)) : p.default);

/** `e`'s values with the player's `tweaks` (tweakKey -> one number or sRGB RGBA) over the document's; null for a program the
 *  catalogue lacks. */
export function resolveFx(catalog: FxCatalog, e: FxSource, tweaks?: Record<string, number[]> | null): FxValues | null {
  const index = catalog.programs.findIndex((p) => p.name === e.program);
  if (index < 0) return null;
  const program = catalog.programs[index];
  const tweak = (kind: TweakKind, name: string, allowed: (string | null)[] | null | undefined, length: number) => {
    const v = allowed?.includes(name) ? tweaks?.[tweakKey(e.id ?? '', kind, name)] : undefined;
    return v?.length === length ? v : undefined;
  };
  const params = new Array<number>(8).fill(0);
  program.params.forEach((p, i) => (params[i] = clamp(p, tweak('param', p.name, e.playerTweaks?.params, 1)?.[0] ?? e.params?.[p.name] ?? p.default)));
  const colors = [0, 1, 2, 3].map(() => [0, 0, 0, 0]);
  program.colours.forEach((c, i) => {
    const fallback = hexColour(c.default);
    const v = tweak('color', c.name, e.playerTweaks?.colors, 4) ?? e.colors?.[c.name] ?? fallback;
    colors[i] = [0, 1, 2, 3].map((k) => (typeof v[k] === 'number' && Number.isFinite(v[k]) ? Math.min(1, Math.max(0, v[k] as number)) : fallback[k]));
  });
  const own = [e.scale ?? 1, e.rotation ?? 0, e.spin ?? 0, e.intensity ?? 1];
  const transform = catalog.transform.map((t, i) => clamp(t, tweak('transform', t.name, e.playerTweaks?.transform, 1)?.[0] ?? own[i]));
  return { program: index, params, colors, transform };
}

/** GLSL `fx_dispatch(int program, FxIn i)` in the catalogue's order, as ForgeCore's ShaderBuild generates it for the game. */
export function fxDispatch(catalog: FxCatalog): string {
  const cases = catalog.programs.map((p, i) => `  if (program == ${i}) return fx_${p.name}(i);`).join('\n');
  return `vec4 fx_dispatch(int program, FxIn i)\n{\n${cases}\n  return vec4(0.0, 0.0, 0.0, 0.0);\n}\n`;
}
