// Skin sounds: any audio the browser can decode (WAV, MP3, FLAC, OGG, M4A …) becomes Ogg Vorbis, the one format the
// pack carries. Ported from GTFO-Forge's audio-transcode: an Ogg Vorbis file with at most two channels is kept as is;
// anything else is decoded to PCM and encoded with the bundled WebAssembly Vorbis encoder (no network), first two
// channels only. Loudness and length are never changed; longer than MAX_SECONDS is refused.
import { createOggEncoder } from 'wasm-media-encoders';
import { t } from './i18n';
import { fileUrl, type SkinSound } from './protocol';

export const MAX_SECONDS = 10;

/** Plays a skin sound locally, as the game does when its event happens. */
export function playSound(x: SkinSound) {
  const a = new Audio(fileUrl(x.file ?? ''));
  a.volume = x.volume ?? 1;
  void a.play();
}
const DECODE_RATE = 48000;
const BLOCK = 65536;

/** Plain-language refusal; `code` matches the backend's error codes where one exists. */
export class AudioError extends Error {
  constructor(
    readonly code: 'bad-audio' | 'audio-too-long',
    message: string,
  ) {
    super(message);
  }
}

const isOggVorbis = (b: Uint8Array) =>
  b.length > 36 && String.fromCharCode(...b.subarray(0, 4)) === 'OggS' && String.fromCharCode(...b.subarray(29, 35)) === 'vorbis';

export async function transcodeToOgg(source: ArrayBuffer): Promise<Uint8Array> {
  const original = new Uint8Array(source);
  let decoded: AudioBuffer;
  try {
    decoded = await new OfflineAudioContext(2, 1, DECODE_RATE).decodeAudioData(source.slice(0));
  } catch {
    throw new AudioError('bad-audio', t('audio.k1'));
  }
  if (!decoded.length) throw new AudioError('bad-audio', t('audio.k2'));
  if (decoded.duration > MAX_SECONDS)
    throw new AudioError('audio-too-long', t('audio.k3', { v0: MAX_SECONDS, v1: Math.round(decoded.duration) }));
  if (isOggVorbis(original) && decoded.numberOfChannels <= 2) return original;
  const channels = decoded.numberOfChannels >= 2 ? 2 : 1;
  const encoder = await createOggEncoder();
  encoder.configure({ channels, sampleRate: decoded.sampleRate, vbrQuality: 5 });
  const planes = Array.from({ length: channels }, (_, i) => decoded.getChannelData(i));
  const parts: Uint8Array[] = [];
  for (let start = 0; start < decoded.length; start += BLOCK) {
    const part = encoder.encode(planes.map((p) => p.subarray(start, Math.min(p.length, start + BLOCK))));
    if (part.length) parts.push(part.slice());
  }
  parts.push(encoder.finalize().slice());
  const bytes = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    bytes.set(p, at);
    at += p.length;
  }
  return bytes;
}
