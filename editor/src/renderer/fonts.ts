// Screen fonts: bundled OFL fonts (from @fontsource, licences under licenses/fonts), installed system fonts, or a font
// file. The document stores a family name, or the absolute path of a .ttf/.otf file.
import '@fontsource/orbitron/700.css';
import '@fontsource/share-tech-mono';
import '@fontsource/rajdhani/700.css';
import '@fontsource/audiowide';
import '@fontsource/vt323';
import '@fontsource/jetbrains-mono/700.css';
import '@fontsource/roboto-mono/700.css';
import '@fontsource/ibm-plex-mono/700.css';
import '@fontsource/press-start-2p';
import '@fontsource/monoton';
import '@fontsource/bungee';
import '@fontsource/silkscreen';
import '@fontsource/pacifico';
import { fileUrl } from './protocol';
import { t } from './i18n';

/** Bundled fonts by group, with the weight each one ships in. */
export const FONT_GROUPS: { group: string; fonts: { family: string; weight: number }[] }[] = [
  {
    get group() { return t('fonts.k1'); },
    fonts: [
      { family: 'Orbitron', weight: 700 },
      { family: 'Share Tech Mono', weight: 400 },
      { family: 'Rajdhani', weight: 700 },
      { family: 'Audiowide', weight: 400 },
      { family: 'VT323', weight: 400 },
    ],
  },
  {
    get group() { return t('fonts.k2'); },
    fonts: [
      { family: 'JetBrains Mono', weight: 700 },
      { family: 'Roboto Mono', weight: 700 },
      { family: 'IBM Plex Mono', weight: 700 },
    ],
  },
  {
    get group() { return t('fonts.k3'); },
    fonts: [
      { family: 'Press Start 2P', weight: 400 },
      { family: 'Monoton', weight: 400 },
      { family: 'Bungee', weight: 400 },
      { family: 'Silkscreen', weight: 400 },
      { family: 'Pacifico', weight: 400 },
    ],
  },
];

export const DEFAULT_FONT = 'JetBrains Mono';
const WEIGHT = new Map(FONT_GROUPS.flatMap((g) => g.fonts.map((f) => [f.family, f.weight] as const)));

export const isFontFile = (font: string) => /\.(ttf|otf|woff2?)$/i.test(font);

/** CSS font shorthand for drawing `font` at `px`; a font file is addressed by the family it was registered under. */
export function cssFont(font: string | null | undefined, px: number): string {
  const f = font || DEFAULT_FONT;
  const family = isFontFile(f) ? fileFamily(f) : f;
  return `${WEIGHT.get(f) ?? 400} ${px}px "${family}", "${DEFAULT_FONT}", monospace`;
}

const fileFamily = (path: string) => `sf-file-${[...path].reduce((h, c) => (h * 31 + c.charCodeAt(0)) | 0, 0) >>> 0}`;
const registered = new Map<string, Promise<void>>();

/** Makes the font ready to draw on a canvas (canvas text does not wait for a font to load). */
export function loadFont(font: string | null | undefined): Promise<void> {
  const f = font || DEFAULT_FONT;
  let p = registered.get(f);
  if (!p) {
    p = isFontFile(f)
      ? new FontFace(fileFamily(f), `url("${fileUrl(f)}")`).load().then((face) => void document.fonts.add(face))
      : document.fonts.load(cssFont(f, 64), '0123456789').then(() => undefined);
    p.catch(() => registered.delete(f));
    registered.set(f, p);
  }
  return p;
}

/** Families of the fonts installed on this computer (empty when the system does not allow listing them). */
export async function systemFonts(): Promise<string[]> {
  const q = (window as { queryLocalFonts?: () => Promise<{ family: string }[]> }).queryLocalFonts;
  if (!q) return [];
  try {
    return [...new Set((await q()).map((f) => f.family))].sort((a, b) => a.localeCompare(b));
  } catch {
    return [];
  }
}
