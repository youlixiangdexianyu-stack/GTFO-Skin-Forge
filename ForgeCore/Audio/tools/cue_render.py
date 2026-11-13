"""Prepare one recorded source segment as a clean clip for a ForgeAudio audio folder.

This is the source step: silence and weak lead-in removed, a short declick fade at both ends, linearly loudness-normalised
(or, for a loop, joined with an equal-power crossfade), mono 48 kHz.  Everything that makes a *cue* out of clips (layers,
offsets, caps, distance versions, buses) belongs to ForgeAudio's cues.forge and is mixed by AudioKit at load; to hear a
finished cue exactly as the game plays it, use ``convert_audio.py render`` (ForgeAudio/tools).  The Ogg Vorbis encode
(``convert_audio.py convert``) is the only step after this one.

Rules (the fields of a sources.forge cue, all optional):

``loop``        true for a loop: audible bounds, then an equal-power 0.25 s crossfade join.
``leadIn``      true keeps the quiet build-up before the first transient (an incoming shell's whistle); by default the clip
                starts 10 ms before the first clear transient.
``exact``       true keeps the section exactly as cut (no trimming, no declick fades, no loudness change, up to 60 s): for
                sections chosen by ear, where the cue's layers set fades and gain.  A source's ``gainDb`` is then the only
                level change (plus the lift below); a loop is still joined with the crossfade.
``targetLufs``  (a source's field, exact sections) raises a quiet section toward that loudness, by at most 12 dB, holding its peaks at
                -1 dBFS with a limiter; never lowers.  Callers lift one-shots to ``ONE_SHOT_LUFS`` unless the source says otherwise.

Command line, for tools in other languages::

    python cue_render.py preview --source <file> [--start <s>] [--seconds <s>] [--rules <json>] [--lufs <dB>] --out <wav>

writes the mono 48 kHz 16-bit WAV of the prepared clip and prints one JSON line:
``{"seconds": ..., "gainDb": ..., "leadInSeconds": ...}``.  In Python, ``render_source`` takes the same values and returns
``(samples, Info)``.  ``--source`` is any audio file ffmpeg can decode.
"""
from __future__ import annotations

import argparse
import array
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import wave
from typing import NamedTuple


RATE = 48_000
MAX_SECONDS = 12.0
ONSET_LEAD_SECONDS = 0.010
ONSET_WINDOW_SECONDS = 0.002
ONSET_LEVEL = 0.1
FADE_IN_SECONDS = 0.003
FADE_OUT_RATIO, FADE_OUT_MIN_SECONDS, FADE_OUT_MAX_SECONDS = 0.12, 0.04, 0.15
LOOP_CROSSFADE_SECONDS = 0.25
DEFAULT_LUFS = -14.0
EXACT_MAX_SECONDS = 60.0
EXACT_PEAK = 0.99
# A quiet exact one-shot is lifted toward ONE_SHOT_LUFS by at most LIFT_MAX_DB; a look-ahead limiter keeps peaks under LIFT_CEILING (-1 dBFS).
ONE_SHOT_LUFS, LAYERED_LUFS, LIFT_MAX_DB, LIFT_CEILING = -14.0, -17.0, 12.0, 0.89


class RenderError(ValueError):
    pass


class Info(NamedTuple):
    seconds: float
    gain_db: float
    lead_in_seconds: float


def run(command: list[str], operation: str) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8",
                                errors="replace", timeout=180, check=False)
    except FileNotFoundError as error:
        raise RenderError(command[0] + " was not found on PATH") from error
    except subprocess.TimeoutExpired as error:
        raise RenderError(operation + " exceeded 180 seconds") from error
    if result.returncode:
        detail = (result.stderr or result.stdout).strip()
        raise RenderError(operation + " failed" + (": " + detail if detail else ""))
    return result


def read_pcm(path: Path) -> array.array:
    with wave.open(str(path), "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) != (1, 2, RATE):
            raise RenderError(str(path) + " is not mono 16-bit 48 kHz PCM")
        samples = array.array("h", audio.readframes(audio.getnframes()))
    if sys.byteorder != "little":
        samples.byteswap()
    return samples


def write_pcm(path: Path, samples: array.array) -> None:
    output = array.array("h", samples)
    if sys.byteorder != "little":
        output.byteswap()
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(RATE)
        audio.writeframes(output.tobytes())


def decode_section(source: Path, destination: Path, start: float = 0.0, seconds: float | None = None) -> None:
    """Decode ``seconds`` of ``source`` from ``start`` to mono 48 kHz 16-bit PCM."""
    if start < 0 or (seconds is not None and not 0 < seconds <= MAX_SECONDS):
        raise RenderError("section start must be non-negative and length must be in (0, 12]")
    command = ["ffmpeg", "-y", "-v", "error", "-xerror", "-nostdin"]
    if start:
        command.extend(["-ss", str(start)])
    command.extend(["-i", str(source)])
    if seconds is not None:
        command.extend(["-t", str(seconds)])
    command.extend(["-map", "0:a:0", "-vn", "-ac", "1", "-ar", str(RATE), "-c:a", "pcm_s16le", str(destination)])
    run(command, "source section decode")


def _window_energies(samples: array.array, window: int) -> list[float]:
    return [sum(value * value for value in samples[start : start + window]) / len(samples[start : start + window])
            for start in range(0, len(samples), window)]


def audible_bounds(samples: array.array) -> tuple[int, int]:
    """The first and last audible samples (-50 dB below the loudest 10 ms), 20 ms of padding each side."""
    window = max(1, round(RATE * 0.01))
    energies = _window_energies(samples, window)
    peak = max(energies, default=0.0)
    if peak <= 0:
        raise RenderError("source section is silent")
    active = [index for index, energy in enumerate(energies) if energy >= max(1.0, peak * 0.00001)]
    if not active:
        raise RenderError("source section has no audible samples")
    padding = round(RATE * 0.02)
    return max(0, active[0] * window - padding), min(len(samples), (active[-1] + 1) * window + padding)


def onset_start(samples: array.array) -> int:
    """The sample 10 ms before the first clear transient: the first 2 ms whose level reaches a tenth of the loudest."""
    window = max(1, round(RATE * ONSET_WINDOW_SECONDS))
    energies = _window_energies(samples, window)
    peak = max(energies, default=0.0)
    if peak <= 0:
        raise RenderError("source section is silent")
    first = next(index for index, energy in enumerate(energies) if energy >= peak * ONSET_LEVEL ** 2)
    return max(0, first * window - round(RATE * ONSET_LEAD_SECONDS))


def fade_out_seconds(length_seconds: float) -> float:
    """The declick fade-out: about 12% of the clip, never shorter than 40 ms or longer than 150 ms."""
    return min(FADE_OUT_MAX_SECONDS, max(FADE_OUT_MIN_SECONDS, length_seconds * FADE_OUT_RATIO))


def edge_fades(samples: array.array) -> array.array:
    result = array.array("h", samples)
    fade_in = min(round(RATE * FADE_IN_SECONDS), len(result) // 4)
    fade_out = min(round(RATE * fade_out_seconds(len(result) / RATE)), len(result) // 4)
    for index in range(fade_in):
        result[index] = round(result[index] * index / max(1, fade_in - 1))
    for index in range(fade_out):
        position = len(result) - fade_out + index
        result[position] = round(result[position] * (fade_out - 1 - index) / max(1, fade_out - 1))
    return result


def make_loop(samples: array.array, crossfade_seconds: float = LOOP_CROSSFADE_SECONDS) -> array.array:
    fade = min(round(RATE * crossfade_seconds), len(samples) // 5)
    if fade < 2 or len(samples) <= fade * 2:
        raise RenderError("loop section is too short for a crossfade")
    result = array.array("h", samples[fade:-fade])
    for index in range(fade):
        phase = (index + 1) / (fade + 1) * math.pi / 2
        mixed = round(samples[-fade + index] * math.cos(phase) + samples[index] * math.sin(phase))
        result.append(max(-32768, min(32767, mixed)))
    return result


def validate_rules(rules: dict[str, object]) -> None:
    unknown = sorted(set(rules) - {"loop", "leadIn", "exact"})
    if unknown:
        raise RenderError("unknown source rule " + ", ".join(unknown))
    if rules.get("exact") and rules.get("leadIn"):
        raise RenderError("exact keeps the section as cut; leadIn does not apply")


def decode_exact(source: Path, start: float, seconds: float) -> array.array:
    """``seconds`` of ``source`` from ``start`` as mono 48 kHz float samples without losing level: a multichannel source is
    averaged (ffmpeg's default downmix adds the channels, which clips a dual-mono recording)."""
    if start < 0 or not 0 < seconds <= EXACT_MAX_SECONDS:
        raise RenderError(f"exact section start must be non-negative and length must be in (0, {EXACT_MAX_SECONDS:g}]")
    probe = run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels", "-of", "default=noprint_wrappers=1:nokey=1", str(source)],
                "source channel probe")
    channels = max(1, int(probe.stdout.strip() or 1))
    mix = ["-af", "pan=mono|c0=" + "+".join(f"{1 / channels:.6f}*c{index}" for index in range(channels))] if channels > 1 else []
    command = ["ffmpeg", "-v", "error", "-xerror", "-nostdin", "-ss", str(start), "-i", str(source), "-t", str(seconds),
               "-map", "0:a:0", "-vn", *mix, "-ac", "1", "-ar", str(RATE), "-f", "f32le", "-"]
    try:
        result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, timeout=180, check=False)
    except FileNotFoundError as error:
        raise RenderError("ffmpeg was not found on PATH") from error
    if result.returncode:
        raise RenderError("exact section decode failed: " + result.stderr.decode("utf-8", "replace").strip())
    data = array.array("f")
    data.frombytes(result.stdout[: len(result.stdout) // 4 * 4])
    if sys.byteorder != "little":
        data.byteswap()
    return data


def render_exact(source: Path, start: float, seconds: float, rules: dict[str, object], gain_db: float,
                 lift_lufs: float | None = None) -> tuple[array.array, Info]:
    """The section exactly as cut: no trimming or declick fades.  ``gain_db`` is the recorded level change (aligns a variant to
    its reference); a section that would pass 0.99 full scale is lowered to that.  ``lift_lufs`` then raises a quiet section toward
    that loudness (see ``lift``).  A loop is joined with the equal-power crossfade."""
    validate_rules(rules)
    data = decode_exact(source, start, seconds)
    if not data:
        raise RenderError("exact section is empty")
    scale = 10 ** (gain_db / 20)
    peak = max(max(data), -min(data)) * scale
    if peak > EXACT_PEAK:
        scale *= EXACT_PEAK / peak
    samples = array.array("h", (max(-32768, min(32767, round(value * scale * 32768.0))) for value in data))
    if rules.get("loop"):
        samples = make_loop(samples)
    lifted = 0.0
    if lift_lufs is not None:
        samples, lifted = lift(samples, lift_lufs)
    return samples, Info(len(samples) / RATE, 20 * math.log10(scale) + lifted, 0.0)


def render(samples: array.array, rules: dict[str, object]) -> tuple[array.array, float]:
    """The prepared clip of a decoded section: ``(samples, lead_in_seconds)``; not yet loudness-normalised."""
    validate_rules(rules)
    start, end = audible_bounds(samples)
    if rules.get("loop"):
        return make_loop(array.array("h", samples[start:end])), 0.0
    body = array.array("h", samples[start:end])
    begin = 0 if rules.get("leadIn") else onset_start(body)
    body = array.array("h", body[begin:])
    if not body or len(body) / RATE > MAX_SECONDS:
        raise RenderError("prepared clip must be greater than zero and at most 12 seconds")
    return edge_fades(body), begin / RATE


def measured(path: Path, target: float) -> tuple[float, float]:
    """The gated integrated loudness (LUFS) and true peak (dBTP) ffmpeg reports; -inf for a clip too short to gate."""
    result = run(["ffmpeg", "-hide_banner", "-nostats", "-nostdin", "-i", str(path), "-af",
                  f"loudnorm=I={target}:TP=-1.5:LRA=11:print_format=json", "-f", "null", os.devnull], "loudness measurement")
    start, end = result.stderr.rfind("{"), result.stderr.rfind("}")
    if start < 0 or end < start:
        raise RenderError("ffmpeg did not report loudness measurements")
    try:
        report = json.loads(result.stderr[start : end + 1])
        return float(report["input_i"]), float(report["input_tp"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise RenderError("ffmpeg returned invalid loudness measurements") from error


def lift(samples: array.array, target: float) -> tuple[array.array, float]:
    """``(samples, gain_db)``: the clip raised toward ``target`` LUFS by at most ``LIFT_MAX_DB``, its peaks held at ``LIFT_CEILING``
    by a look-ahead limiter instead of lowering the whole clip.  A clip already at the target, or too short to measure, is returned
    unchanged: a lift never lowers."""
    with tempfile.TemporaryDirectory(prefix="forge-cue-lift-") as directory:
        source, lifted = Path(directory) / "in.wav", Path(directory) / "out.wav"
        write_pcm(source, samples)
        integrated, _ = measured(source, target)
        if not math.isfinite(integrated):
            return samples, 0.0
        extra = min(max(target - integrated, 0.0), LIFT_MAX_DB)
        if extra < 0.1:
            return samples, 0.0
        run(["ffmpeg", "-hide_banner", "-nostats", "-nostdin", "-y", "-i", str(source), "-af",
             f"volume={extra:.3f}dB,alimiter=limit={LIFT_CEILING}:attack=2:release=80:level=disabled", "-ar", str(RATE), "-ac", "1",
             "-c:a", "pcm_s16le", str(lifted)], "loudness lift")
        return read_pcm(lifted), extra


def lift_target(rules: dict[str, object], source: dict[str, object], layered: bool = False) -> float | None:
    """The loudness an exact section is lifted toward: the source's ``targetLufs`` (``false`` for none), else ``ONE_SHOT_LUFS`` for a
    single-layer one-shot, ``LAYERED_LUFS`` for a layer of a layered cue (AudioKit sums the layers and hard-clips the sum, so each
    layer is lifted less) and nothing for a loop; non-exact sections are normalised instead."""
    if not rules.get("exact"):
        return None
    if "targetLufs" in source:
        value = source["targetLufs"]
        return None if value is False or value is None else float(value)
    if rules.get("loop"):
        return None
    return LAYERED_LUFS if layered else ONE_SHOT_LUFS


def loudness(path: Path, target: float) -> tuple[float, float]:
    integrated, true_peak = measured(path, target)
    if not math.isfinite(integrated) or not math.isfinite(true_peak):
        # EBU R128 has no gated integration block for very short effects: use the full-clip RMS and peak instead.
        samples = read_pcm(path)
        rms = math.sqrt(sum((value / 32768.0) ** 2 for value in samples) / len(samples))
        peak = max(abs(value) for value in samples) / 32768.0
        if rms <= 0 or peak <= 0:
            raise RenderError("source section has no measurable loudness")
        integrated, true_peak = 20 * math.log10(rms), 20 * math.log10(peak)
    return integrated, true_peak


def normalise(samples: array.array, integrated: float, true_peak: float, target: float) -> tuple[array.array, float]:
    gain_db = max(min(target - integrated, -1.5 - true_peak, 18.0), -30.0)
    scale = 10 ** (gain_db / 20)
    return array.array("h", (max(-32768, min(32767, round(value * scale))) for value in samples)), gain_db


def render_source(source: Path, start: float, seconds: float | None, rules: dict[str, object],
                  target_lufs: float = DEFAULT_LUFS, gain_db: float = 0.0, lift_lufs: float | None = None) -> tuple[array.array, Info]:
    """The prepared clip of ``seconds`` of ``source`` from ``start``.  With the ``exact`` rule ``seconds`` is required, the
    section is kept as cut and only ``gain_db`` and the optional ``lift_lufs`` change its level (``target_lufs`` is not used)."""
    if rules.get("exact"):
        if seconds is None:
            raise RenderError("an exact section needs its length")
        return render_exact(source, start, seconds, rules, gain_db, lift_lufs)
    with tempfile.TemporaryDirectory(prefix="forge-cue-render-") as directory:
        wav = Path(directory) / "section.wav"
        decode_section(source, wav, start, seconds)
        samples, lead_in = render(read_pcm(wav), rules)
        write_pcm(wav, samples)
        integrated, true_peak = loudness(wav, target_lufs)
    samples, gain_db = normalise(samples, integrated, true_peak, target_lufs)
    return samples, Info(len(samples) / RATE, gain_db, lead_in)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    preview = commands.add_parser("preview", help="write the prepared clip of a source segment")
    preview.add_argument("--source", type=Path, required=True)
    preview.add_argument("--start", type=float, default=0.0)
    preview.add_argument("--seconds", type=float)
    preview.add_argument("--rules", default="{}", help='source rules as JSON, e.g. {"leadIn": true}')
    preview.add_argument("--lufs", type=float, default=DEFAULT_LUFS)
    preview.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        samples, info = render_source(args.source, args.start, args.seconds, json.loads(args.rules), args.lufs)
        write_pcm(args.out, samples)
        print(json.dumps({"seconds": round(info.seconds, 6), "gainDb": round(info.gain_db, 3),
                          "leadInSeconds": round(info.lead_in_seconds, 6)}))
        return 0
    except (OSError, RenderError, json.JSONDecodeError) as error:
        print("cue_render: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
