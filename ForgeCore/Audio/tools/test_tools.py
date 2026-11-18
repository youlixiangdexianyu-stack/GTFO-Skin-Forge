from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest
from unittest import mock
import wave


TOOLS = Path(__file__).resolve().parent


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


convert_audio = load("convert_audio")
sys.path.insert(0, str(TOOLS.parents[2] / "Build"))
import package_common
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")


def write_wav(path: Path) -> None:
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(8000)
        audio.writeframes(b"\x00\x00\x00\x10\x00\xf0" * 400)


def write_table(folder: Path, version: str) -> None:
    """The one version table (Release/release.json) beside the package folder: the packager compares the manifest with it."""
    (folder.parent / "Release").mkdir(exist_ok=True)
    (folder.parent / "Release" / "release.json").write_text(
        json.dumps({"libraries": [{"packageName": "NAinfini-ForgeCore", "version": version}]}), encoding="utf-8"
    )


def write_package(folder: Path, version: str = "0.0.1") -> None:
    """A package folder (<root>/Mods/ForgeCore) with its manifest, icon, documents and version table."""
    folder.mkdir(parents=True)
    (folder / "manifest.json").write_text(
        json.dumps(
            {
                "name": "ForgeCore",
                "version_number": version,
                "website_url": "https://github.com/NAinfini/GTFO-Forge",
                "description": "The Forge library for GTFO mods.",
                "dependencies": ["BepInEx-BepInExPack_GTFO-3.2.4"],
            }
        ),
        encoding="utf-8",
    )
    for name in METADATA[2:]:
        (folder / name).write_text("test " + name + "\n", encoding="utf-8")
    write_table(folder, version)
    (folder / "icon.png").write_bytes(
        b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" + struct.pack(">II", 256, 256) + b"\x08\x06\x00\x00\x00\x00\x00\x00\x00"
    )


class ConvertChecks(unittest.TestCase):
    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg and ffprobe are required")
    def test_convert_wav_to_vorbis_with_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output, evidence = root / "source.wav", root / "result.ogg", root / "result.json"
            write_wav(source)
            with contextlib.redirect_stdout(io.StringIO()):
                code = convert_audio.main(
                    ["convert", str(source), str(output), "--evidence", str(evidence), "--timeout", "20"]
                )
            self.assertEqual(code, 0)
            self.assertEqual(output.read_bytes()[:4], b"OggS")
            receipt = json.loads(evidence.read_text(encoding="utf-8"))
            self.assertEqual(receipt["input"]["codec"], "pcm_s16le")
            self.assertEqual(receipt["output"]["codec"], "vorbis")
            self.assertEqual(receipt["output"]["sha256"], hashlib.sha256(output.read_bytes()).hexdigest())
            self.assertFalse(receipt["settings"]["normalization"])
            self.assertFalse(receipt["settings"]["truncated"])

    def test_existing_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output = root / "source.wav", root / "result.ogg"
            write_wav(source)
            output.write_bytes(b"keep")
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(convert_audio.main(["convert", str(source), str(output)]), 2)
            self.assertEqual(output.read_bytes(), b"keep")

    def test_extension_must_match_detected_codec(self):
        metadata = {"codec": "mp3", "durationSeconds": 1.0, "sampleRate": 48000, "channels": 1}
        with self.assertRaisesRegex(convert_audio.ToolError, "PCM or ADPCM"):
            convert_audio.validate_source(Path("false.wav"), metadata, 60, 2, 48000, 1_000_000)

    def test_each_resource_limit_is_enforced(self):
        base = {"codec": "vorbis", "durationSeconds": 1.0, "sampleRate": 48000, "channels": 1}
        cases = (
            ({**base, "durationSeconds": 61}, (60, 2, 48000, 10_000_000), "max-seconds"),
            ({**base, "channels": 3}, (60, 2, 48000, 10_000_000), "max-channels"),
            ({**base, "sampleRate": 7999}, (60, 2, 48000, 10_000_000), "runtime minimum"),
            ({**base, "sampleRate": 96000}, (60, 2, 48000, 10_000_000), "max-sample-rate"),
            (base, (60, 2, 48000, 100), "max-decoded-bytes"),
        )
        for metadata, limits, error in cases:
            with self.subTest(error=error), self.assertRaisesRegex(convert_audio.ToolError, error):
                convert_audio.validate_source(Path("source.ogg"), metadata, *limits)

    def test_author_limits_cannot_relax_runtime_limits(self):
        cases = (
            ("--max-seconds", "3601", "3600"),
            ("--max-channels", "3", "limit of 2"),
            ("--max-sample-rate", "192001", "192000"),
            ("--max-output-bytes", str(64 * 1024 * 1024 + 1), "67108864"),
            ("--max-decoded-bytes", str(512 * 1024 * 1024 + 1), "536870912"),
        )
        for option, value, error in cases:
            with self.subTest(option=option):
                args = convert_audio.parser().parse_args(["convert", "in.wav", "out.ogg", option, value])
                with self.assertRaisesRegex(convert_audio.ToolError, error):
                    convert_audio.validate_limits(args)




    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe") and shutil.which("dotnet"), "ffmpeg, ffprobe and dotnet are required")
    def test_folder_conversion_is_loadable_by_the_runtime(self):
        """The author path end to end: a folder of WAVs becomes audio/<name>.ogg that the runtime decoder accepts."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sources, audio = root / "sounds", root / "audio"
            (sources / "ui").mkdir(parents=True)
            write_wav(sources / "shot.wav")
            write_wav(sources / "ui" / "click.wav")
            (sources / "notes.txt").write_text("ignored", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(convert_audio.main(["convert", str(sources), str(audio), "--timeout", "20"]), 0)
            self.assertEqual(sorted(p.relative_to(audio).as_posix() for p in audio.rglob("*.ogg")), ["shot.ogg", "ui/click.ogg"])
            first = (audio / "shot.ogg").read_bytes()
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(convert_audio.main(["convert", str(sources), str(audio), "--timeout", "20"]), 0)
            report = json.loads(output.getvalue())
            self.assertEqual((report["converted"], sorted(report["kept"])), ([], ["shot.ogg", "ui/click.ogg"]))
            self.assertEqual((audio / "shot.ogg").read_bytes(), first)
            # The author names each cue's bus and each bus's global category.
            (audio / "cues.forge").write_text(json.dumps({
                "buses": {"gun": {"category": "weapon"}, "menu": {"category": "ui"}},
                "cues": {"shot": {"bus": "gun"}, "ui/click": {"bus": "menu"}},
            }), encoding="utf-8")
            completed = convert_audio.subprocess.run(
                ["dotnet", "run", "--project", str(convert_audio.VALIDATOR), "-c", "Release", "--disable-build-servers",
                 "--artifacts-path", str(convert_audio.VALIDATOR_ARTIFACTS), "--", str(audio)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr + completed.stdout)
            result = json.loads(completed.stdout.strip().splitlines()[-1])
            self.assertEqual((result["status"], result["cues"]), ("valid", ["shot", "ui/click"]))



class PackageChecks(unittest.TestCase):
    """The shared packager's gate (Mods/Build/package_common.py): the manifest version must be the one release.json gives."""

    def test_release_table_must_match_manifest_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "Mods" / "ForgeCore"
            write_package(folder)
            write_table(folder, "0.0.2")
            built = root / "artifacts" / "bin" / "ForgeCore" / "release"
            built.mkdir(parents=True)
            (built / "ForgeCore.dll").write_bytes(b"MZtest assembly")
            with self.assertRaisesRegex(ValueError, "differs from manifest"):
                package_common.package_library(folder, "ForgeCore", ("ForgeCore.dll",), root / "artifacts", root / "out", METADATA)

if __name__ == "__main__":
    unittest.main(verbosity=2)
