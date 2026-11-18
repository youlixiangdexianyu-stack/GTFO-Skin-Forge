// Panel lines and borders drawn in code (SkinDocument TrimStyle): the one drawing behind a livery trim layer and a screen's
// styled border (frames.borderCanvas), white on transparent, so a tint colours it and it stays sharp at any size.
import type { LiveryLayer } from './protocol';

export type Trim = NonNullable<LiveryLayer['trim']>;
export type TrimStyle = NonNullable<Trim['style']>;
export const TRIM_STYLES: TrimStyle[] = ['frame', 'cutFrame', 'double', 'notched', 'ticks', 'brackets', 'chevron', 'hazard', 'seam', 'panels'];

/** A trim in pixels of the canvas it is drawn on (the document keeps metres). */
export interface TrimPx {
  style: TrimStyle;
  width: number;
  corner: number;
  inset: number;
  dash: number;
  gap: number;
  count: number;
}

/** A closed outline through `pts`. */
function poly(ctx: CanvasRenderingContext2D, pts: number[][]) {
  ctx.moveTo(pts[0][0], pts[0][1]);
  for (const [x, y] of pts.slice(1)) ctx.lineTo(x, y);
  ctx.closePath();
}

/** The rectangle (x, y, w, h) with the corners in `cut` (top-left, top-right, bottom-right, bottom-left) cut at 45° by `k`. */
function cutRect(x: number, y: number, w: number, h: number, k: number, cut: boolean[]): number[][] {
  k = Math.max(0, Math.min(k, w / 2, h / 2));
  const [a, b, c, d] = cut.map((on) => (on ? k : 0));
  return [
    [x + a, y],
    [x + w - b, y],
    [x + w, y + b],
    [x + w, y + h - c],
    [x + w - c, y + h],
    [x + d, y + h],
    [x, y + h - d],
    [x, y + a],
  ];
}

/** Draws `s` in white over a W x H canvas; lines are `width` thick and lie inside the canvas edge plus `inset`. */
export function drawTrim(ctx: CanvasRenderingContext2D, W: number, H: number, s: TrimPx) {
  const lw = Math.max(0.5, s.width);
  const i = Math.max(0, s.inset) + lw / 2; // the centre line of a frame
  const [w, h] = [W - 2 * i, H - 2 * i];
  const n = Math.max(1, Math.round(s.count));
  ctx.save();
  ctx.fillStyle = ctx.strokeStyle = '#fff';
  ctx.lineWidth = lw;
  ctx.lineJoin = 'miter';
  ctx.lineCap = 'butt';
  if (s.dash > 0) ctx.setLineDash([s.dash, Math.max(0.5, s.gap)]);
  const outline = (pts: number[][]) => {
    ctx.beginPath();
    poly(ctx, pts);
    ctx.stroke();
  };
  // The cut keeps its 45° look on the centre line: the outer corner cut by `corner`.
  const k = Math.max(0, s.corner - (lw / 2) * (Math.SQRT2 - 1));
  switch (s.style) {
    case 'frame':
      outline(cutRect(i, i, w, h, 0, [false, false, false, false]));
      break;
    case 'cutFrame':
      outline(cutRect(i, i, w, h, k, [true, false, true, false]));
      break;
    case 'notched':
      outline(cutRect(i, i, w, h, k, [true, true, true, true]));
      break;
    case 'double': {
      outline(cutRect(i, i, w, h, k, [true, true, true, true]));
      ctx.lineWidth = lw * 0.6;
      const j = i + lw * 2.3;
      outline(cutRect(j, j, W - 2 * j, H - 2 * j, Math.max(0, k - (j - i) * (Math.SQRT2 - 1)), [true, true, true, true]));
      break;
    }
    case 'ticks': {
      // A thin frame (half the width) with ruler ticks along the top and bottom, every third one longer.
      ctx.lineWidth = lw * 0.5;
      const e = Math.max(0, s.inset) + lw / 4;
      const [ew, eh] = [W - 2 * e, H - 2 * e];
      outline(cutRect(e, e, ew, eh, 0, [false, false, false, false]));
      ctx.setLineDash([]);
      const ticks = Math.max(2, n);
      ctx.beginPath();
      for (let t = 1; t < ticks; t++) {
        const len = lw * (t % 3 === 0 ? 3 : 1.8);
        const x = e + (ew * t) / ticks;
        ctx.moveTo(x, e);
        ctx.lineTo(x, e + len);
        ctx.moveTo(x, e + eh);
        ctx.lineTo(x, e + eh - len);
      }
      ctx.stroke();
      break;
    }
    case 'brackets': {
      // An L in each corner, its arms `corner` long.
      const L = Math.max(lw, Math.min(s.corner, w / 2, h / 2));
      ctx.lineJoin = 'miter';
      ctx.beginPath();
      for (const [x, y, dx, dy] of [
        [i, i, 1, 1],
        [i + w, i, -1, 1],
        [i + w, i + h, -1, -1],
        [i, i + h, 1, -1],
      ]) {
        ctx.moveTo(x + dx * L, y);
        ctx.lineTo(x, y);
        ctx.lineTo(x, y + dy * L);
      }
      ctx.stroke();
      break;
    }
    case 'chevron': {
      // `count` arrow heads pointing right, side by side.
      const step = w / n;
      ctx.lineJoin = 'miter';
      ctx.beginPath();
      for (let t = 0; t < n; t++) {
        const x0 = i + t * step + step * 0.15;
        const x1 = x0 + Math.min(step * 0.7, h / 2);
        ctx.moveTo(x0, i);
        ctx.lineTo(x1, i + h / 2);
        ctx.lineTo(x0, i + h);
      }
      ctx.stroke();
      break;
    }
    case 'hazard': {
      // Diagonal stripes, `count` across the width, half stripe and half gap, inside a frame.
      const inner = s.inset;
      const [x, y, bw, bh] = [inner, inner, W - 2 * inner, H - 2 * inner];
      const period = bw / n;
      ctx.save();
      ctx.beginPath();
      ctx.rect(x, y, bw, bh);
      ctx.clip();
      ctx.beginPath();
      for (let t = -Math.ceil(bh / period) - 1; t <= n + 1; t++) {
        const x0 = x + t * period;
        poly(ctx, [
          [x0, y + bh],
          [x0 + period / 2, y + bh],
          [x0 + period / 2 + bh, y],
          [x0 + bh, y],
        ]);
      }
      ctx.fill();
      ctx.restore();
      outline(cutRect(i, i, w, h, 0, [false, false, false, false]));
      break;
    }
    case 'seam': {
      // `count` lines across, evenly spaced (one runs along the middle).
      ctx.beginPath();
      for (let t = 0; t < n; t++) {
        const y = i + (h * (t + 0.5)) / n;
        ctx.moveTo(i, y);
        ctx.lineTo(i + w, y);
      }
      ctx.stroke();
      break;
    }
    case 'panels': {
      // `count` panels across and as many rows as keep them about square, `gap` apart, corners cut by `corner`.
      const rows = Math.max(1, Math.round((n * h) / Math.max(1, w)));
      const g = Math.max(lw * 1.5, s.gap);
      const [pw, ph] = [(w - (n - 1) * g) / n, (h - (rows - 1) * g) / rows];
      ctx.setLineDash([]);
      ctx.beginPath();
      for (let r = 0; r < rows; r++) for (let c = 0; c < n; c++) poly(ctx, cutRect(i + c * (pw + g), i + r * (ph + g), pw, ph, k, [true, false, true, false]));
      ctx.stroke();
      break;
    }
  }
  ctx.restore();
}

/** Presets a trim layer starts from: a style with lengths in metres that suit a gun. */
export type TrimPresetId = 'scifiPlate' | 'octagon' | 'doubleFrame' | 'brackets' | 'ruler' | 'panelGrid' | 'seamLine' | 'stitched' | 'chevrons' | 'hazard' | 'dashedFrame';
export const TRIM_PRESETS: { id: TrimPresetId; trim: Trim; size: [number, number] }[] = [
  { id: 'scifiPlate', trim: { style: 'cutFrame', width: 0.0008, corner: 0.004, inset: 0, dash: 0, gap: 0.001, count: 3 }, size: [0.06, 0.03] },
  { id: 'octagon', trim: { style: 'notched', width: 0.0007, corner: 0.003, inset: 0, dash: 0, gap: 0.001, count: 3 }, size: [0.04, 0.025] },
  { id: 'doubleFrame', trim: { style: 'double', width: 0.0006, corner: 0.002, inset: 0, dash: 0, gap: 0.001, count: 3 }, size: [0.05, 0.03] },
  { id: 'brackets', trim: { style: 'brackets', width: 0.0008, corner: 0.006, inset: 0, dash: 0, gap: 0.001, count: 3 }, size: [0.04, 0.03] },
  { id: 'ruler', trim: { style: 'ticks', width: 0.0008, corner: 0, inset: 0, dash: 0, gap: 0.001, count: 12 }, size: [0.06, 0.012] },
  { id: 'panelGrid', trim: { style: 'panels', width: 0.0005, corner: 0.002, inset: 0, dash: 0, gap: 0.0015, count: 3 }, size: [0.09, 0.03] },
  { id: 'seamLine', trim: { style: 'seam', width: 0.0005, corner: 0, inset: 0, dash: 0, gap: 0.001, count: 1 }, size: [0.12, 0.004] },
  { id: 'stitched', trim: { style: 'seam', width: 0.0006, corner: 0, inset: 0, dash: 0.0025, gap: 0.0015, count: 2 }, size: [0.1, 0.006] },
  { id: 'chevrons', trim: { style: 'chevron', width: 0.0012, corner: 0, inset: 0, dash: 0, gap: 0.001, count: 3 }, size: [0.03, 0.012] },
  { id: 'hazard', trim: { style: 'hazard', width: 0.0006, corner: 0, inset: 0, dash: 0, gap: 0.001, count: 8 }, size: [0.06, 0.012] },
  { id: 'dashedFrame', trim: { style: 'frame', width: 0.0006, corner: 0, inset: 0, dash: 0.002, gap: 0.0012, count: 3 }, size: [0.05, 0.03] },
];

/** Long side of the picture a trim layer is drawn into before it is projected (lines stay sharp on any gear texture). */
const TRIM_PX = 1024;
const trimCache = new Map<string, HTMLCanvasElement>();

/** A trim layer's picture: its box (Size, metres) at TRIM_PX on the long side, white on transparent; the same canvas while it is unchanged. */
export function trimCanvas(l: LiveryLayer): HTMLCanvasElement {
  const [bw, bh] = (l.size ?? [0.04, 0.04]).map((x) => Math.max(1e-4, x ?? 0.04));
  const t: Trim = { ...TRIM_PRESETS[0].trim, ...l.trim };
  const key = JSON.stringify([bw, bh, t]);
  const hit = trimCache.get(key);
  if (hit) return hit;
  const k = TRIM_PX / Math.max(bw, bh);
  const c = document.createElement('canvas');
  c.width = Math.max(8, Math.round(bw * k));
  c.height = Math.max(8, Math.round(bh * k));
  const px = (m: number | null | undefined) => (m ?? 0) * k;
  drawTrim(c.getContext('2d')!, c.width, c.height, {
    style: t.style ?? 'cutFrame',
    width: px(t.width),
    corner: px(t.corner),
    inset: px(t.inset),
    dash: px(t.dash),
    gap: px(t.gap),
    count: t.count ?? 3,
  });
  trimCache.set(key, c);
  if (trimCache.size > 32) trimCache.delete(trimCache.keys().next().value!);
  return c;
}
