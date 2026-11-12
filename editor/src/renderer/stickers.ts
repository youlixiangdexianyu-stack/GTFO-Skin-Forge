// The sticker library: white silhouettes (a decal's tint colours them) drawn on a 100 x 100 box. A picked sticker is
// rasterised once to <data dir>/stickers/<id>.png, which the decal layer then uses like any image file.
import { writePng } from './frames';
import { useStore } from './store';

const star = (n: number, r1: number, r2: number, turn = -90) =>
  Array.from({ length: n * 2 }, (_, i) => {
    const a = ((turn + (i * 180) / n) * Math.PI) / 180;
    const r = i % 2 ? r2 : r1;
    return `${(50 + r * Math.cos(a)).toFixed(1)},${(50 + r * Math.sin(a)).toFixed(1)}`;
  }).join(' ');

export const STICKERS: Record<string, string> = {
  skull: '<path d="M50 8C27 8 14 24 14 44c0 12 6 20 12 25v15h10v-8h8v8h12v-8h8v8h10V69c6-5 12-13 12-25C86 24 73 8 50 8z"/><circle cx="35" cy="45" r="10" fill="#000"/><circle cx="65" cy="45" r="10" fill="#000"/><path d="M50 56l-6 10h12z" fill="#000"/>',
  biohazard: '<circle cx="50" cy="50" r="40" fill="none" stroke="#fff" stroke-width="8"/>' + [0, 120, 240].map((a) => `<circle cx="50" cy="30" r="14" transform="rotate(${a} 50 50)"/>`).join('') + '<circle cx="50" cy="50" r="9" fill="#000"/>',
  radiation: '<circle cx="50" cy="50" r="9"/>' + [0, 120, 240].map((a) => `<path d="M50 50L32 19A36 36 0 0 1 68 19z" transform="rotate(${a} 50 50)"/>`).join(''),
  warning: '<path d="M50 8L94 88H6z"/><rect x="45" y="35" width="10" height="30" fill="#000"/><rect x="45" y="70" width="10" height="10" fill="#000"/>',
  crosshair: '<circle cx="50" cy="50" r="34" fill="none" stroke="#fff" stroke-width="7"/><path d="M46 4h8v30h-8zM46 66h8v30h-8zM4 46h30v8H4zM66 46h30v8H66z"/><circle cx="50" cy="50" r="5"/>',
  target: '<circle cx="50" cy="50" r="44"/><circle cx="50" cy="50" r="32" fill="#000"/><circle cx="50" cy="50" r="22"/><circle cx="50" cy="50" r="10" fill="#000"/>',
  star: `<polygon points="${star(5, 46, 19)}"/>`,
  star4: `<polygon points="${star(4, 46, 12)}"/>`,
  burst: `<polygon points="${star(12, 46, 30)}"/>`,
  bolt: '<path d="M58 4L18 56h26l-8 40 46-56H56z"/>',
  flame: '<path d="M50 4c4 18 30 30 30 58a30 30 0 0 1-60 0c0-14 8-22 12-30 2 10 6 14 10 16-2-16 2-32 8-44z"/>',
  heart: '<path d="M50 88L12 50C0 36 6 12 28 12c10 0 18 6 22 14 4-8 12-14 22-14 22 0 28 24 16 38z"/>',
  crown: '<path d="M8 30l20 18 22-34 22 34 20-18-8 52H16z"/>',
  eye: '<path d="M4 50C20 22 80 22 96 50 80 78 20 78 4 50z"/><circle cx="50" cy="50" r="18" fill="#000"/><circle cx="50" cy="50" r="8"/>',
  wings: '<path d="M50 40C38 22 18 18 2 22c8 6 10 12 10 18 6 2 12 6 16 10-6 0-12 2-16 6 10 4 22 4 30-2 4 8 4 12 8 16 4-4 4-8 8-16 8 6 20 6 30 2-4-4-10-6-16-6 4-4 10-8 16-10 0-6 2-12 10-18-16-4-36 0-48 18z"/>',
  chevrons: '<path d="M10 20l40 24 40-24v16L50 60 10 36z"/><path d="M10 46l40 24 40-24v16L50 86 10 62z"/>',
  arrow: '<path d="M50 4l40 40H66v52H34V44H10z"/>',
  hazard: '<clipPath id="c"><rect x="4" y="30" width="92" height="40"/></clipPath><g clip-path="url(#c)">' + Array.from({ length: 8 }, (_, i) => `<path d="M${i * 24 - 30} 70l24-40h12l-24 40z"/>`).join('') + '</g><rect x="4" y="30" width="92" height="40" fill="none" stroke="#fff" stroke-width="4"/>',
  hexagon: '<polygon points="50,6 88,28 88,72 50,94 12,72 12,28"/><polygon points="50,22 74,36 74,64 50,78 26,64 26,36" fill="#000"/>',
  diamond: '<polygon points="50,4 92,50 50,96 8,50"/>',
  cross: '<path d="M38 6h24v32h32v24H62v32H38V62H6V38h32z"/>',
  tally: '<path d="M14 14h8v72h-8zM32 14h8v72h-8zM50 14h8v72h-8zM68 14h8v72h-8z"/><path d="M4 70L90 24l4 8L8 78z"/>',
  barcode: (() => {
    let x = 8; // bars and gaps alternate
    return [3, 3, 6, 2, 4, 2, 8, 3, 3, 5, 2, 6, 3, 2, 4].map((w, i) => ((x += w + 2), i % 2 ? '' : `<rect x="${x - w - 2}" y="20" width="${w}" height="60"/>`)).join('');
  })(),
  moon: '<path d="M62 6a44 44 0 1 0 32 66A36 36 0 0 1 62 6z"/>',
  sun: '<circle cx="50" cy="50" r="22"/>' + Array.from({ length: 12 }, (_, i) => `<rect x="47" y="2" width="6" height="18" transform="rotate(${i * 30} 50 50)"/>`).join(''),
  paw: '<ellipse cx="50" cy="66" rx="22" ry="18"/><ellipse cx="22" cy="40" rx="9" ry="12"/><ellipse cx="40" cy="22" rx="9" ry="12"/><ellipse cx="60" cy="22" rx="9" ry="12"/><ellipse cx="78" cy="40" rx="9" ry="12"/>',
  ring: '<circle cx="50" cy="50" r="40" fill="none" stroke="#fff" stroke-width="12"/>',
  dots: Array.from({ length: 16 }, (_, i) => `<circle cx="${17 + (i % 4) * 22}" cy="${17 + Math.floor(i / 4) * 22}" r="8"/>`).join(''),
};

export type StickerId = keyof typeof STICKERS;

/** The sticker's SVG as a data URL (for the picker and rasterising). */
export const stickerUrl = (id: string) =>
  `data:image/svg+xml;charset=utf-8,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="512" height="512" fill="#fff">${STICKERS[id]}</svg>`)}`;

/** The sticker as a 512 px PNG in the data folder; its path. */
export async function stickerFile(id: string): Promise<string> {
  const img = new Image();
  img.src = stickerUrl(id);
  await img.decode();
  const c = document.createElement('canvas');
  c.width = c.height = 512;
  c.getContext('2d')!.drawImage(img, 0, 0, 512, 512);
  const path = `${useStore.getState().dataDir}\\stickers\\${id}.png`;
  await writePng(path, c);
  return path;
}

/** Asks for a decal image: a sticker from the library or an image file; null when cancelled. */
export function pickDecalImage(): Promise<string | null> {
  return new Promise((resolve) => useStore.setState({ stickerPick: resolve }));
}
