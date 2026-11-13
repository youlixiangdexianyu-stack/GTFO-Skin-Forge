"""Convert author audio to Ogg Vorbis for ForgeAudio, and check an audio folder.

Examples:
  python convert_audio.py convert my-sounds/ MyMod/audio/      every WAV/MP3/FLAC/OGG -> audio/<name>.ogg
  python convert_audio.py convert shot.wav MyMod/audio/shot.ogg --evidence shot.audio.json
  python convert_audio.py validate MyMod/audio                 what AudioKit.Load will see, fully decoded
  python convert_audio.py render MyMod/audio boom --out boom.wav --distance 80
                                                               the mixed cue exactly as the game plays it at 80 m

Conversion requires ffmpeg and ffprobe on PATH; validation requires the .NET SDK. Sources
are read only, outputs are created atomically, and existing files are never overwritten
(a folder conversion keeps outputs that already exist and converts the rest).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Any, Sequence


TOOL_VERSION = "1.0.0"
PACKAGE = Path(__file__).resolve().parent.parent
REPO = PACKAGE.parents[2]
VALIDATOR = PACKAGE / "tools" / "Validate" / "ForgeAudio.Validate.csproj"
VALIDATOR_ARTIFACTS = REPO / "artifacts" / "dotnet-audiokit-author"
SUPPORTED_SUFFIXES = {".wav", ".mp3", ".ogg", ".flac"}
SUPPORTED_CODECS = {"mp3", "vorbis", "flac"}
MIN_SAMPLE_RATE = 8000
MAX_SAMPLE_RATE = 192000
MAX_CHANNELS = 2
MAX_SECONDS = 3600.0
MAX_COMPRESSED_BYTES = 64 * 1024 * 1024
MAX_DECODED_BYTES = 512 * 1024 * 1024


class ToolError(ValueError):
    """An author-facing input or tool failure."""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run(command: Sequence[str], timeout: float) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            list(command),
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except FileNotFoundError as error:
        raise ToolError(command[0] + " was not found on PATH") from error
    except subprocess.TimeoutExpired as error:
        raise ToolError(command[0] + " exceeded the " + str(timeout) + " second timeout") from error


def checked(command: Sequence[str], timeout: float, operation: str) -> subprocess.CompletedProcess[str]:
    result = run(command, timeout)
    if result.returncode:
        detail = (result.stderr or result.stdout).strip()
        raise ToolError(operation + " failed" + (": " + detail if detail else ""))
    return result


def version_line(program: str, timeout: float) -> str:
    output = checked([program, "-version"], timeout, program + " version check").stdout
    return output.splitlines()[0].strip() if output.splitlines() else "unknown"


def probe(path: Path, timeout: float) -> dict[str, Any]:
    result = checked(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration:stream=codec_type,codec_name,sample_rate,channels,duration",
            "-of",
            "json",
            str(path),
        ],
        timeout,
        "ffprobe",
    )
    try:
        document = json.loads(result.stdout)
        audio = [stream for stream in document.get("streams", []) if stream.get("codec_type") == "audio"]
        if len(audio) != 1:
            raise ToolError("input must contain exactly one audio stream")
        stream = audio[0]
        duration_value = stream.get("duration") or document.get("format", {}).get("duration")
        duration = float(duration_value)
        rate = int(stream["sample_rate"])
        channels = int(stream["channels"])
        codec = str(stream["codec_name"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise ToolError("ffprobe returned incomplete audio metadata") from error
    if not math.isfinite(duration) or duration <= 0:
        raise ToolError("audio duration must be finite and greater than zero")
    if rate <= 0 or channels <= 0:
        raise ToolError("sample rate and channel count must be greater than zero")
    return {"codec": codec, "durationSeconds": duration, "sampleRate": rate, "channels": channels}


def validate_source(
    path: Path,
    metadata: dict[str, Any],
    max_seconds: float,
    max_channels: int,
    max_sample_rate: int,
    max_decoded_bytes: int,
) -> int:
    suffix = path.suffix.lower()
    if suffix not in SUPPORTED_SUFFIXES:
        raise ToolError("input extension must be .wav, .mp3, .flac or .ogg")
    codec = metadata["codec"]
    if suffix == ".wav" and not (codec.startswith("pcm_") or codec.startswith("adpcm_")):
        raise ToolError(".wav input must contain PCM or ADPCM audio, found " + codec)
    if suffix == ".mp3" and codec != "mp3":
        raise ToolError(".mp3 input must contain MP3 audio, found " + codec)
    if suffix == ".ogg" and codec != "vorbis":
        raise ToolError(".ogg input must contain Vorbis audio, found " + codec)
    if suffix == ".flac" and codec != "flac":
        raise ToolError(".flac input must contain FLAC audio, found " + codec)
    if codec not in SUPPORTED_CODECS and not codec.startswith("pcm_") and not codec.startswith("adpcm_"):
        raise ToolError("unsupported input codec " + codec)
    if metadata["durationSeconds"] > max_seconds:
        raise ToolError("audio duration exceeds --max-seconds")
    if metadata["channels"] > max_channels:
        raise ToolError("audio channel count exceeds --max-channels")
    if metadata["sampleRate"] < MIN_SAMPLE_RATE:
        raise ToolError("audio sample rate is below the runtime minimum of 8000 Hz")
    if metadata["sampleRate"] > max_sample_rate:
        raise ToolError("audio sample rate exceeds --max-sample-rate")
    decoded_bytes = math.ceil(metadata["durationSeconds"] * metadata["sampleRate"] * metadata["channels"] * 4)
    if decoded_bytes > max_decoded_bytes:
        raise ToolError("estimated decoded float PCM size exceeds --max-decoded-bytes")
    return decoded_bytes


def ensure_new_file(path: Path, label: str) -> None:
    if path.exists():
        raise ToolError(label + " already exists: " + str(path))
    path.parent.mkdir(parents=True, exist_ok=True)


def validate_limits(args: argparse.Namespace) -> None:
    if not math.isfinite(args.quality) or not -1 <= args.quality <= 10:
        raise ToolError("quality must be finite and between -1 and 10")
    if not math.isfinite(args.timeout) or not math.isfinite(args.max_seconds):
        raise ToolError("timeout and maximum duration must be finite")
    values = (
        args.timeout,
        args.max_seconds,
        args.max_channels,
        args.max_sample_rate,
        args.max_output_bytes,
        args.max_decoded_bytes,
    )
    if any(value <= 0 for value in values):
        raise ToolError("timeout and limits must be greater than zero")
    if args.max_seconds > MAX_SECONDS:
        raise ToolError("--max-seconds cannot exceed the runtime limit of 3600")
    if args.max_channels > MAX_CHANNELS:
        raise ToolError("--max-channels cannot exceed the runtime limit of 2")
    if not MIN_SAMPLE_RATE <= args.max_sample_rate <= MAX_SAMPLE_RATE:
        raise ToolError("--max-sample-rate must be between 8000 and the runtime limit of 192000")
    if args.max_output_bytes > MAX_COMPRESSED_BYTES:
        raise ToolError("--max-output-bytes cannot exceed the runtime limit of 67108864")
    if args.max_decoded_bytes > MAX_DECODED_BYTES:
        raise ToolError("--max-decoded-bytes cannot exceed the runtime limit of 536870912")


def publish_without_replacing(files: Sequence[tuple[Path, Path, str]]) -> None:
    """Hard-link completed temporaries into place and roll back only links made here."""
    published: list[tuple[Path, Path, str]] = []
    try:
        for temporary, target, label in files:
            try:
                os.link(temporary, target)
            except FileExistsError as error:
                raise ToolError(label + " was created concurrently and was not replaced: " + str(target)) from error
            except OSError as error:
                raise ToolError("could not publish " + label + " without replacing files: " + str(error)) from error
            published.append((temporary, target, label))
    except ToolError as error:
        rollback_failures = []
        for temporary, target, label in reversed(published):
            try:
                if target.exists() and os.path.samefile(temporary, target):
                    target.unlink()
            except OSError as rollback_error:
                rollback_failures.append(label + " at " + str(target) + ": " + str(rollback_error))
        if rollback_failures:
            raise ToolError(str(error) + "; rollback failed for own " + "; ".join(rollback_failures)) from error
        raise


def convert(args: argparse.Namespace) -> dict[str, Any]:
    validate_limits(args)
    if args.input.is_dir():
        return convert_folder(args)
    return convert_file(args)


def convert_folder(args: argparse.Namespace) -> dict[str, Any]:
    """Every supported file under the input folder becomes <output>/<same relative name>.ogg, so the file name is the cue."""
    source_root = args.input.resolve()
    output_root = args.output.resolve()
    if args.evidence:
        raise ToolError("--evidence applies to single-file conversion")
    if output_root == source_root or source_root in output_root.parents:
        raise ToolError("the output folder must not be inside the source folder")
    sources = sorted(path for path in source_root.rglob("*") if path.is_file() and path.suffix.lower() in SUPPORTED_SUFFIXES)
    if not sources:
        raise ToolError("no .wav, .mp3, .flac or .ogg files in " + str(source_root))
    converted, kept, failed = [], [], []
    for source in sources:
        relative = source.relative_to(source_root).with_suffix(".ogg")
        target = output_root / relative
        if target.exists():
            kept.append(relative.as_posix())
            continue
        try:
            convert_file(argparse.Namespace(**{**vars(args), "input": source, "output": target, "evidence": None}))
            converted.append(relative.as_posix())
        except ToolError as error:
            failed.append({"file": source.relative_to(source_root).as_posix(), "error": str(error)})
    report = {"output": str(output_root), "converted": converted, "kept": kept, "failed": failed}
    if failed:
        raise ToolError(json.dumps(report, ensure_ascii=False))
    return report


def convert_file(args: argparse.Namespace) -> dict[str, Any]:
    source = args.input.resolve()
    output = args.output.resolve()
    evidence = args.evidence.resolve() if args.evidence else None
    if not source.is_file():
        raise ToolError("input does not exist: " + str(source))
    if source == output:
        raise ToolError("input and output must be different files")
    if output.suffix.lower() != ".ogg":
        raise ToolError("output extension must be .ogg")
    if evidence and evidence in (source, output):
        raise ToolError("evidence path must differ from input and output")
    ensure_new_file(output, "output")
    if evidence:
        ensure_new_file(evidence, "evidence")

    before = probe(source, args.timeout)
    decoded_bytes = validate_source(
        source,
        before,
        args.max_seconds,
        args.max_channels,
        args.max_sample_rate,
        args.max_decoded_bytes,
    )
    checked(
        [
            "ffmpeg",
            "-v",
            "error",
            "-xerror",
            "-nostdin",
            "-i",
            str(source),
            "-map",
            "0:a:0",
            "-f",
            "null",
            "-",
        ],
        args.timeout,
        "full input decode",
    )

    file_handle, temporary_name = tempfile.mkstemp(prefix=output.name + ".", suffix=".tmp.ogg", dir=output.parent)
    os.close(file_handle)
    temporary = Path(temporary_name)
    evidence_temporary: Path | None = None
    try:
        checked(
            [
                "ffmpeg",
                "-y",
                "-v",
                "error",
                "-xerror",
                "-nostdin",
                "-i",
                str(source),
                "-map",
                "0:a:0",
                "-vn",
                "-map_metadata",
                "-1",
                "-c:a",
                "libvorbis",
                "-q:a",
                str(args.quality),
                str(temporary),
            ],
            args.timeout,
            "Vorbis conversion",
        )
        after = probe(temporary, args.timeout)
        output_decoded_bytes = validate_source(
            temporary,
            after,
            args.max_seconds,
            args.max_channels,
            args.max_sample_rate,
            args.max_decoded_bytes,
        )
        output_bytes = temporary.stat().st_size
        if output_bytes > args.max_output_bytes:
            raise ToolError("converted Ogg exceeds --max-output-bytes")
        if after["channels"] != before["channels"] or after["sampleRate"] != before["sampleRate"]:
            raise ToolError("conversion changed the channel count or sample rate")
        if abs(after["durationSeconds"] - before["durationSeconds"]) > 0.05:
            raise ToolError("conversion changed the duration by more than 0.05 seconds")
        checked(
            [
                "ffmpeg",
                "-v",
                "error",
                "-xerror",
                "-nostdin",
                "-i",
                str(temporary),
                "-map",
                "0:a:0",
                "-f",
                "null",
                "-",
            ],
            args.timeout,
            "full output decode",
        )
        record = {
            "schemaVersion": "1.0.0",
            "toolVersion": TOOL_VERSION,
            "input": {
                "name": source.name,
                "sha256": sha256(source),
                **before,
                "estimatedDecodedFloatPcmBytes": decoded_bytes,
            },
            "output": {
                "name": output.name,
                "sha256": sha256(temporary),
                **after,
                "bytes": output_bytes,
                "estimatedDecodedFloatPcmBytes": output_decoded_bytes,
            },
            "settings": {"codec": "vorbis", "quality": args.quality, "normalization": False, "truncated": False},
            "tools": {"ffmpeg": version_line("ffmpeg", args.timeout), "ffprobe": version_line("ffprobe", args.timeout)},
        }
        if evidence:
            descriptor = json.dumps(record, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
            file_handle, evidence_name = tempfile.mkstemp(
                prefix=evidence.name + ".", suffix=".tmp", dir=evidence.parent
            )
            evidence_temporary = Path(evidence_name)
            with os.fdopen(file_handle, "w", encoding="utf-8", newline="\n") as target:
                target.write(descriptor)
        publish = [(temporary, output, "output")]
        if evidence and evidence_temporary:
            publish.append((evidence_temporary, evidence, "evidence"))
        publish_without_replacing(publish)
        return record
    finally:
        temporary.unlink(missing_ok=True)
        if evidence_temporary:
            evidence_temporary.unlink(missing_ok=True)


def validate(args: argparse.Namespace) -> int:
    folder = args.folder.resolve()
    if not folder.is_dir():
        raise ToolError("audio folder does not exist: " + str(folder))
    return run_validator([str(folder)])


def render(args: argparse.Namespace) -> int:
    """The mixed preview of one cue at a listener distance, made by the same code the game mixes with."""
    folder = args.folder.resolve()
    if not folder.is_dir():
        raise ToolError("audio folder does not exist: " + str(folder))
    command = ["render", str(folder), args.cue, "--out", str(args.out.resolve()), "--distance", str(args.distance), "--variant", str(args.variant)]
    return run_validator(command)


def run_validator(arguments: Sequence[str]) -> int:
    if not VALIDATOR.is_file():
        raise ToolError("validator project does not exist: " + str(VALIDATOR))
    command = [
        "dotnet",
        "run",
        "--project",
        str(VALIDATOR),
        "-c",
        "Release",
        "--disable-build-servers",
        "--artifacts-path",
        str(VALIDATOR_ARTIFACTS),
        "--",
        *arguments,
    ]
    try:
        return subprocess.run(command, check=False).returncode
    except FileNotFoundError as error:
        raise ToolError("dotnet was not found on PATH") from error


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(description=__doc__)
    commands = root.add_subparsers(dest="command", required=True)
    conversion = commands.add_parser("convert", help="convert a folder (or one file) of WAV, MP3, FLAC or OGG to Ogg Vorbis")
    conversion.add_argument("input", type=Path, help="source folder or file")
    conversion.add_argument("output", type=Path, help="audio folder, or the .ogg file for a single input")
    conversion.add_argument("--evidence", type=Path, help="optional JSON conversion receipt")
    conversion.add_argument("--quality", type=float, default=6.0, help="libvorbis VBR quality from -1 through 10")
    conversion.add_argument("--timeout", type=float, default=120.0)
    conversion.add_argument("--max-seconds", type=float, default=MAX_SECONDS)
    conversion.add_argument("--max-channels", type=int, default=MAX_CHANNELS)
    conversion.add_argument("--max-sample-rate", type=int, default=MAX_SAMPLE_RATE)
    conversion.add_argument("--max-output-bytes", type=int, default=MAX_COMPRESSED_BYTES)
    conversion.add_argument("--max-decoded-bytes", type=int, default=MAX_DECODED_BYTES)
    conversion.set_defaults(action=convert)
    validation = commands.add_parser("validate", help="check an audio folder exactly as AudioKit.Load reads it, decoding every clip")
    validation.add_argument("folder", type=Path)
    validation.set_defaults(action=validate)
    rendering = commands.add_parser("render", help="write the mixed preview of one cue as the game plays it at a listener distance")
    rendering.add_argument("folder", type=Path)
    rendering.add_argument("cue")
    rendering.add_argument("--out", type=Path, required=True, help="the 16-bit WAV to write")
    rendering.add_argument("--distance", type=float, default=0.0, help="listener distance in metres (selects the near or far layers and the distance loss)")
    rendering.add_argument("--variant", type=int, default=0)
    rendering.set_defaults(action=render)
    return root


def main(argv: Sequence[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "convert":
            print(json.dumps(args.action(args), sort_keys=True, ensure_ascii=False))
            return 0
        return args.action(args)
    except (OSError, ToolError) as error:
        print("convert_audio: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
