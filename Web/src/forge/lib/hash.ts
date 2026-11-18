import {base64ToBytes} from '../base64.ts';

export async function sha256Bytes(bytes: ArrayBuffer | Uint8Array): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', bytes as BufferSource);
    return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

const crcTable = Uint32Array.from({length: 256}, (_, value) => {
    for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
    return value >>> 0;
});

export function crc32(bytes: Uint8Array, previous = 0): number {
    let crc = previous ^ 0xffffffff;
    for (let index = 0, length = bytes.length; index < length; index++) crc = (crc >>> 8) ^ crcTable[(crc ^ bytes[index]) & 255];
    return (crc ^ 0xffffffff) >>> 0;
}

/** FNV-1a over the text's UTF-8 bytes: a stable 32-bit choice from an identity. */
export function fnv1a32(text: string): number {
    let hash = 2166136261;
    for (const byte of new TextEncoder().encode(text)) hash = Math.imul(hash ^ byte, 16777619) >>> 0;
    return hash;
}

// Synchronous SHA-256 for callers that cannot await crypto.subtle (the Web Crypto digest is async).
const round = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74,
    0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d,
    0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e,
    0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
    0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
]);
const utf8 = new TextEncoder();
// Calls are synchronous, so one schedule can be reused without retaining every padded input while an export builds its checks.
const schedule = new Uint32Array(64);
const hexWord = (value: number) => value.toString(16).padStart(8, '0');

/** SHA-256 of the UTF-8 bytes of `text`, as lowercase hex. Synchronous, because a check id is read where nothing can await. */
export function sha256Text(text: string): string {
    return sha256Sync(utf8.encode(text));
}

// Digests already computed, by the base64 text itself: a document read again on every save hashes each resource once.
const base64Digests = new Map<string, string>();
/** SHA-256 of the bytes a base64 string encodes (`bytes` when the caller already decoded them), synchronous, remembered by content. */
export function base64Sha256(data: string, bytes?: Uint8Array): string {
    const known = base64Digests.get(data);
    if (known) return known;
    const digest = sha256Sync(bytes ?? base64ToBytes(data));
    if (base64Digests.size >= 256) base64Digests.delete(base64Digests.keys().next().value!);
    base64Digests.set(data, digest);
    return digest;
}

/** SHA-256 of `bytes`, as lowercase hex, synchronous. */
export function sha256Sync(bytes: Uint8Array): string {
    const paddedLength = Math.ceil((bytes.length + 9) / 64) * 64;
    let h0 = 0x6a09e667,
        h1 = 0xbb67ae85,
        h2 = 0x3c6ef372,
        h3 = 0xa54ff53a,
        h4 = 0x510e527f,
        h5 = 0x9b05688c,
        h6 = 0x1f83d9ab,
        h7 = 0x5be0cd19;
    for (let block = 0; block < paddedLength; block += 64) {
        schedule.fill(0, 0, 16);
        const remaining = Math.max(0, Math.min(64, bytes.length - block));
        for (let index = 0; index < remaining; index++) schedule[index >>> 2] |= bytes[block + index]! << (24 - (index & 3) * 8);
        if (remaining < 64 && block + remaining === bytes.length) schedule[remaining >>> 2] |= 0x80 << (24 - (remaining & 3) * 8);
        if (block + 64 === paddedLength) {
            schedule[14] = Math.floor(bytes.length / 0x20000000);
            schedule[15] = (bytes.length << 3) >>> 0;
        }
        for (let index = 16; index < 64; index++) {
            const left = schedule[index - 15]!,
                right = schedule[index - 2]!;
            schedule[index] =
                (schedule[index - 16]! +
                    (((left >>> 7) | (left << 25)) ^ ((left >>> 18) | (left << 14)) ^ (left >>> 3)) +
                    schedule[index - 7]! +
                    (((right >>> 17) | (right << 15)) ^ ((right >>> 19) | (right << 13)) ^ (right >>> 10))) >>>
                0;
        }
        let a = h0,
            b = h1,
            c = h2,
            d = h3,
            e = h4,
            f = h5,
            g = h6,
            h = h7;
        for (let index = 0; index < 64; index++) {
            const first =
                (h +
                    (((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7))) +
                    ((e & f) ^ (~e & g)) +
                    round[index]! +
                    schedule[index]!) >>>
                0;
            const second = ((((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10))) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
            h = g;
            g = f;
            f = e;
            e = (d + first) >>> 0;
            d = c;
            c = b;
            b = a;
            a = (first + second) >>> 0;
        }
        h0 = (h0 + a) >>> 0;
        h1 = (h1 + b) >>> 0;
        h2 = (h2 + c) >>> 0;
        h3 = (h3 + d) >>> 0;
        h4 = (h4 + e) >>> 0;
        h5 = (h5 + f) >>> 0;
        h6 = (h6 + g) >>> 0;
        h7 = (h7 + h) >>> 0;
    }
    return hexWord(h0) + hexWord(h1) + hexWord(h2) + hexWord(h3) + hexWord(h4) + hexWord(h5) + hexWord(h6) + hexWord(h7);
}
