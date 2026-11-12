// Fragment programs of the UV-space renderer (liveryGpu) for the livery kinds that are made from the model itself:
// procedural patterns and wear. Both read the fragment's position and normal in the item's root space (metres), so they
// run across UV seams, and the per-vertex curvature (positive on outer edges, negative in cavities).
import type { Pattern } from "../protocol";

/** Pattern ids in the order the program's `pattern` uniform numbers them (SkinDocument Pattern). */
export const PATTERNS: Pattern[] = [
  "digital",
  "woodland",
  "tiger",
  "splinter",
  "flecktarn",
  "multiTerrain",
  "hydro",
  "anodised",
  "marble",
  "damascus",
  "honeycomb",
  "hexGrid",
  "contour",
  "circuit",
  "zebra",
  "leopard",
  "scales",
  "stripes",
  "plaid",
  "dots",
  "rust",
  "flame",
  "starfield",
  "crackle",
];

/** Hashes and noises shared by both programs; `p` in pattern units. */
const NOISE = /* glsl */ `
  float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
  vec3 hash33(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
  }
  float hash12(vec2 p) { vec3 q = fract(vec3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return fract((q.x + q.y) * q.z); }
  float vnoise(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x), mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x), mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y), f.z);
  }
  float fbm(vec3 p) {
    float v = 0.0, a = 0.5;
    for (int i = 0; i < 5; i++) { v += a * vnoise(p); p = p * 2.03 + 17.1; a *= 0.5; }
    return v / 0.97;
  }
  // Cellular noise: distance to the nearest and second nearest feature point, and the nearest cell's id.
  vec3 worley(vec3 p, out vec3 cell) {
    vec3 i = floor(p), f = fract(p);
    float d1 = 9.0, d2 = 9.0;
    cell = i;
    for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
      vec3 o = vec3(x, y, z);
      float d = length(o + hash33(i + o) - f);
      if (d < d1) { d2 = d1; d1 = d; cell = i + o; } else if (d < d2) d2 = d;
    }
    return vec3(d1, d2, 0.0);
  }
`;

const VARYINGS = /* glsl */ `
  varying vec3 vPos;
  varying vec3 vNormal;
  varying float vCurve;
`;

/** Vertex program of both: draws at the UVs, passes root-space position and normal and the curvature. */
export const UV_VERTEX = /* glsl */ `
  uniform mat4 toRoot;
  attribute float curvature;
  ${VARYINGS}
  void main() {
    vPos = (toRoot * modelMatrix * vec4(position, 1.0)).xyz;
    vNormal = normalize(mat3(toRoot) * mat3(modelMatrix) * normal);
    vCurve = curvature;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
  }
`;

export const PATTERN_FRAGMENT = /* glsl */ `
  uniform int pattern;
  uniform vec4 colors[4];
  uniform float scale;
  uniform mat3 rot;
  uniform float seed;
  ${VARYINGS}
  ${NOISE}
  vec4 col(int i) { return i == 0 ? colors[0] : i == 1 ? colors[1] : i == 2 ? colors[2] : colors[3]; }
  // Smooth palette through the four colours (t wraps).
  vec4 palette(float t) {
    t = fract(t) * 4.0;
    int i = int(floor(t));
    float f = smoothstep(0.0, 1.0, fract(t));
    return mix(col(i), col(i == 3 ? 0 : i + 1), f);
  }
  // Flat coordinates on the plane the surface faces most (patterns that are drawn on a sheet).
  vec2 sheet(vec3 p, vec3 n) {
    vec3 a = abs(n);
    return a.x > a.y && a.x > a.z ? p.zy : a.y > a.z ? p.xz : p.xy;
  }
  float hexDist(vec2 p) { p = abs(p); return max(dot(p, normalize(vec2(1.0, 1.7320508))), p.x); }
  // Hex cell: local offset in xy, cell id in zw.
  vec4 hexCell(vec2 p) {
    vec2 r = vec2(1.0, 1.7320508), h = r * 0.5;
    vec2 a = mod(p, r) - h, b = mod(p - h, r) - h;
    vec2 g = dot(a, a) < dot(b, b) ? a : b;
    return vec4(g, p - g);
  }
  vec4 shade(vec3 p, vec3 n) {
    vec3 cell;
    if (pattern == 0) { // digital: pixel blocks of a layered noise
      vec3 q = floor(p * 5.0) / 5.0;
      float v = fbm(q * 0.6);
      return col(v < 0.42 ? 0 : v < 0.52 ? 1 : v < 0.6 ? 2 : 3);
    }
    if (pattern == 1) { // woodland: large organic blobs
      vec3 w = p * 0.45 + 0.6 * vec3(fbm(p * 0.3), fbm(p * 0.3 + 5.0), fbm(p * 0.3 + 9.0));
      float v = fbm(w);
      return col(v < 0.43 ? 0 : v < 0.53 ? 1 : v < 0.61 ? 2 : 3);
    }
    if (pattern == 2) { // tiger: broken stripes
      float s = sin((p.x + fbm(p * 0.8) * 1.6) * 3.2);
      if (s > 0.55 && fbm(p * 1.3 + 3.0) > 0.42) return col(1);
      return fbm(p * 0.4) > 0.55 ? col(2) : col(0);
    }
    if (pattern == 3) { // splinter: angular shards and rain marks
      worley(vec3(p.x * 0.7, p.y * 1.4, p.z * 0.7), cell);
      vec4 c = col(int(floor(hash13(cell) * 3.0)));
      if (fract(p.x * 6.0) < 0.07 && fract(p.y * 1.5 + hash12(floor(vec2(p.x * 6.0, 0.0)))) < 0.55) c = col(3);
      return c;
    }
    if (pattern == 4) { // flecktarn: dense dots over a base
      vec4 c = col(0);
      for (int k = 1; k < 4; k++) {
        vec3 d = worley(p * 2.6 + float(k) * 17.0, cell);
        if (d.x < 0.32 && hash13(cell) > 0.35) c = col(k);
      }
      return c;
    }
    if (pattern == 5) { // multi-terrain: soft blobs and thin branches
      vec4 c = col(0);
      if (fbm(p * 0.5) > 0.55) c = col(1);
      if (fbm(p * 1.4 + 7.0) > 0.62) c = col(2);
      if (abs(fbm(p * 1.1 + 13.0) - 0.5) < 0.025) c = col(3);
      return c;
    }
    if (pattern == 6) { // hydro dip: swirled bands
      vec3 w = p * 0.5 + 2.0 * vec3(fbm(p * 0.5 + 1.0), fbm(p * 0.5 + 2.0), fbm(p * 0.5 + 3.0));
      return palette(fbm(w) * 2.5);
    }
    if (pattern == 7) { // anodised: gradient along the item, slightly uneven
      return palette(p.z * 0.15 + fbm(p * 0.6) * 0.12);
    }
    if (pattern == 8) { // marble: veins
      float v = abs(sin(p.x * 1.5 + 7.0 * fbm(p * 0.7)));
      vec4 c = mix(col(0), col(2), fbm(p * 0.3) * 0.5);
      return mix(col(1), c, smoothstep(0.0, 0.12, v));
    }
    if (pattern == 9) { // damascus: folded steel layers
      float v = sin((p.y + 1.4 * fbm(p * 1.2)) * 14.0);
      return mix(col(0), col(1), smoothstep(-0.3, 0.3, v));
    }
    if (pattern == 10) { // honeycomb: filled hexagons
      vec4 h = hexCell(sheet(p, n) * 2.0);
      float e = hexDist(h.xy);
      vec4 c = col(int(floor(hash12(h.zw) * 3.0)));
      return e > 0.45 ? col(3) : c;
    }
    if (pattern == 11) { // hex grid: outlines
      vec4 h = hexCell(sheet(p, n) * 2.0);
      return hexDist(h.xy) > 0.44 ? col(1) : col(0);
    }
    if (pattern == 12) { // contour lines
      float v = fbm(p * 0.35) * 10.0;
      float line = fract(v);
      bool major = mod(floor(v), 5.0) == 0.0;
      return line < (major ? 0.09 : 0.05) ? col(major ? 2 : 1) : col(0);
    }
    if (pattern == 13) { // circuit: traces on a grid with pads
      vec2 q = sheet(p, n) * 3.0;
      vec2 i = floor(q), f = fract(q) - 0.5;
      float r = hash12(i + seed);
      float trace = r < 0.4 ? abs(f.y) : r < 0.8 ? abs(f.x) : min(abs(f.x), abs(f.y));
      if (length(f) < 0.16 && hash12(i + 3.7) > 0.6) return col(2);
      return trace < 0.06 ? col(1) : col(0);
    }
    if (pattern == 14) { // zebra
      return sin((p.x + fbm(p * 0.6) * 2.2) * 4.0) > 0.0 ? col(0) : col(1);
    }
    if (pattern == 15) { // leopard: rosettes
      vec3 d = worley(p * 1.6, cell);
      float r = 0.18 + 0.08 * hash13(cell);
      if (d.x < r * 0.75) return col(1);
      if (d.x < r * 1.35 && fbm(p * 4.0 + cell) > 0.35) return col(2);
      return col(0);
    }
    if (pattern == 16) { // scales: overlapping rows
      vec2 q = sheet(p, n) * vec2(2.0, 3.0);
      float row = floor(q.y);
      q.x += mod(row, 2.0) * 0.5;
      vec2 f = vec2(fract(q.x) - 0.5, fract(q.y));
      float d = length(f);
      if (d > 0.5) return col(2);
      return mix(col(0), col(1), smoothstep(0.1, 0.5, d)) * (d > 0.44 ? 0.6 : 1.0);
    }
    if (pattern == 17) { // stripes through the colours
      return col(int(mod(floor(p.x * 2.0), 4.0)));
    }
    if (pattern == 18) { // plaid: two crossing stripe sets
      vec4 a = col(int(mod(floor(p.x * 2.0), 3.0))), b = col(int(mod(floor(p.y * 2.0), 3.0)));
      vec4 c = mix(a, b, 0.5);
      return fract(p.x * 8.0) < 0.1 || fract(p.y * 8.0) < 0.1 ? mix(c, col(3), 0.6) : c;
    }
    if (pattern == 19) { // polka dots
      vec2 q = sheet(p, n) * 2.0;
      vec2 f = fract(q + vec2(mod(floor(q.y), 2.0) * 0.5, 0.0)) - 0.5;
      return length(f) < 0.28 ? col(1) : col(0);
    }
    if (pattern == 20) { // rust and grime
      float v = fbm(p * 0.8);
      vec4 c = mix(col(0), col(1), smoothstep(0.45, 0.6, v));
      if (fbm(p * 6.0 + 4.0) > 0.68) c = mix(c, col(2), 0.8);
      return mix(c, col(3), smoothstep(0.55, 0.75, fbm(p * 2.5 + 9.0)) * 0.5);
    }
    if (pattern == 21) { // flames licking along the item
      float t = p.z * 0.6 + fbm(p * 1.4 + vec3(0.0, 0.0, -p.z)) * 1.3;
      return t < 0.5 ? col(2) : t < 0.8 ? col(1) : t < 1.05 ? col(3) : col(0);
    }
    if (pattern == 22) { // starfield over nebula
      vec4 c = mix(col(0), col(2), smoothstep(0.5, 0.8, fbm(p * 0.4)) * 0.6);
      vec3 q = p * 6.0, i = floor(q);
      float h = hash13(i);
      float d = length(fract(q) - 0.5 - (hash33(i) - 0.5) * 0.6);
      if (h > 0.93) c = mix(c, col(1), smoothstep(0.12, 0.0, d * (2.0 - h)));
      return c;
    }
    // crackle: cracks between cells
    vec3 d = worley(p * 1.5, cell);
    return d.y - d.x < 0.06 ? col(1) : mix(col(0), col(2), hash13(cell) * 0.35);
  }
  void main() {
    vec3 p = rot * vPos / max(scale, 1e-4) + vec3(seed * 13.37, seed * 7.31, seed * 3.17);
    vec4 c = shade(p, rot * vNormal);
    gl_FragColor = vec4(c.rgb, 1.0);
  }
`;

/** Wear mask: alpha is how worn the fragment is (edges first, then patches, plus scratches). */
export const WEAR_FRAGMENT = /* glsl */ `
  uniform float amount;
  uniform float edgeWeight;
  uniform float scratches;
  uniform float scale;
  uniform float seed;
  ${VARYINGS}
  ${NOISE}
  void main() {
    vec3 p = vPos / max(scale, 1e-4) + vec3(seed * 13.37, seed * 7.31, seed * 3.17);
    float edge = clamp(vCurve * 3.0, 0.0, 1.0);
    float patches = fbm(p);
    float grain = fbm(p * 6.0);
    float v = mix(patches, edge * (0.75 + 0.5 * grain), edgeWeight);
    // Scratches: thin streaks along a few directions, broken up by noise.
    float s = 0.0;
    for (int k = 0; k < 3; k++) {
      vec3 dir = normalize(hash33(vec3(float(k), seed, 1.0)) - 0.5);
      float line = abs(sin(dot(p, dir) * 40.0 + fbm(p * 2.0 + float(k)) * 6.0));
      s = max(s, smoothstep(0.985, 1.0, line) * step(0.55, fbm(p * 0.7 + float(k) * 11.0)));
    }
    v = max(v, s * scratches * 1.2);
    float t = 1.0 - amount;
    float a = amount <= 0.0 ? 0.0 : smoothstep(t - 0.04, t + 0.04, v);
    gl_FragColor = vec4(1.0, 1.0, 1.0, a);
  }
`;
