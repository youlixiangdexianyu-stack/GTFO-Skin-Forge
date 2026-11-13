"""Shared Thunderstore packaging for the Forge library and mod packages; never publishes.

A package's tools/package_release.py supplies its folder, the dlls it ships and any extra files; this module checks the
manifest, takes every dll from the Release build output (never from a copy), writes a deterministic zip and then proves the
zip against the build with verify_release.py's checks. Use it for new packages; verify_release.py checks all of them.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
import tempfile
from urllib.parse import urlsplit
import zipfile

REPO = Path(__file__).resolve().parents[2]
TEAM = "NAinfini"
NAME = r"[A-Za-z0-9_]{1,128}"
VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
LIBRARY_METADATA = ("manifest.json", "icon.png", "README.md", "DEVELOPERS.md", "CHANGELOG.md", "LICENSE", "NOTICE", "web-manifest.forge")


def check_manifest(folder: Path, name: str) -> dict[str, object]:
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if not isinstance(manifest, dict):
        raise ValueError("manifest.json must be an object")
    for key in ("name", "version_number", "website_url", "description"):
        if not isinstance(manifest.get(key), str):
            raise ValueError(key + " must be a string")
    if manifest["name"] != name or not re.fullmatch(NAME, name):
        raise ValueError("manifest name must be " + name + ", found " + str(manifest["name"]))
    if not re.fullmatch(VERSION, manifest["version_number"]):
        raise ValueError("version_number must be major.minor.patch")
    if len(manifest["description"]) > 250:
        raise ValueError("description exceeds 250 characters")
    url = urlsplit(manifest["website_url"])
    if manifest["website_url"] and (url.scheme not in ("http", "https") or not url.netloc):
        raise ValueError("website_url must be empty or an HTTP(S) URL")
    dependencies = manifest.get("dependencies")
    if not isinstance(dependencies, list) or any(
        not isinstance(item, str) or not re.fullmatch(NAME + "-" + NAME + "-" + VERSION, item) for item in dependencies
    ):
        raise ValueError("dependencies must be a list of Team-Name-major.minor.patch strings")
    with (folder / "icon.png").open("rb") as icon:
        head = icon.read(33)
    if len(head) != 33 or head[:16] != b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" or struct.unpack(">II", head[16:24]) != (256, 256):
        raise ValueError("icon.png must be a 256 x 256 PNG")
    for doc in ("README.md", "CHANGELOG.md"):
        if not (folder / doc).read_text(encoding="utf-8").strip():
            raise ValueError(doc + " must not be empty")
    return manifest


PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_TEXT_CHUNKS = (b"tEXt", b"zTXt", b"iTXt")


def png_chunks(data: bytes):
    """(type, start, end, body) of every chunk of a PNG; end is the offset after the CRC."""
    position = len(PNG_SIGNATURE)
    while position + 12 <= len(data):
        length = struct.unpack(">I", data[position:position + 4])[0]
        end = position + 12 + length
        yield data[position + 4:position + 8], position, end, data[position + 8:position + 8 + length]
        position = end


def strip_png_text(data: bytes) -> bytes:
    """A PNG without its tEXt/zTXt/iTXt chunks (tool paths and dates the build left in); any other bytes pass through."""
    if not data.startswith(PNG_SIGNATURE):
        return data
    return PNG_SIGNATURE + b"".join(data[start:end] for kind, start, end, _ in png_chunks(data) if kind not in PNG_TEXT_CHUNKS)


def zip_entry_bytes(entry: str, data: bytes) -> bytes:
    """The bytes a zip entry ships: every PNG leaves the build without text metadata."""
    return strip_png_text(data) if entry.lower().endswith(".png") else data


def write_zip(path: Path, files: dict[str, bytes]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for entry, data in sorted(files.items()):
                info = zipfile.ZipInfo(entry, date_time=(2026, 1, 1, 0, 0, 0))
                info.create_system = 3
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                archive.writestr(info, zip_entry_bytes(entry, data), compresslevel=9)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def lang_files(folder: Path, target: str = "lang", source: str = "lang") -> dict[str, Path]:
    """<folder>/<source>/*.forge as zip entries <target>/*.forge: en.forge and zh.forge are required and every file is an object of key -> text."""
    found = sorted((folder / source).glob("*.forge"))
    if not {"en.forge", "zh.forge"} <= {path.name for path in found}:
        raise ValueError(str(folder / source) + " must hold en.forge and zh.forge")
    for path in found:
        texts = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(texts, dict) or not all(isinstance(text, str) for text in texts.values()):
            raise ValueError(str(path) + " must be a JSON object of key -> text")
    return {target + "/" + path.name: path for path in found}


def png_size(path: Path | str) -> tuple[int, int]:
    """Width and height of a PNG from its header."""
    with open(path, "rb") as image:
        head = image.read(24)
    if head[:8] != PNG_SIGNATURE:
        raise ValueError(str(path) + " is not a PNG")
    return struct.unpack(">II", head[16:24])


def sha256_file(path: Path | str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def audio_files(folder: Path) -> dict[str, Path]:
    """The ForgeAudio cue file and exactly the Ogg clips it names, from a Release build's audio folder."""
    settings = folder / "cues.forge"
    if not settings.is_file():
        raise ValueError("missing " + str(settings) + " (run the Release build)")
    cues = json.loads(settings.read_text(encoding="utf-8")).get("cues")
    if not isinstance(cues, dict) or not cues:
        raise ValueError("invalid audio cues.forge " + str(settings))
    files = {"audio/cues.forge": settings}
    for cue, entry in cues.items():
        for layer in entry.get("layers", [{"clips": [cue + ".ogg"]}]):
            for clip in layer["clips"]:
                if not re.fullmatch(r"[a-z0-9_.-]+\.ogg", clip) or not (folder / clip).is_file():
                    raise ValueError("missing audio file " + str(folder / clip) + " for cue " + cue)
                files["audio/" + clip] = folder / clip
    stray = sorted({path.name for path in folder.glob("*.ogg")} - {entry[6:] for entry in files})
    if stray:
        raise ValueError("Ogg files not named by cues.forge would load as extra cues: " + ", ".join(stray))
    return files


def shared_mods(repo: Path = REPO) -> Path | None:
    """The main checkout's Mods/ folder (FORGE_MODS_ROOT, by default the checkout holding the git common directory), or None when
    repo is not a checkout root (a test fixture never falls back)."""
    if not (Path(repo) / ".git").exists():
        return None
    root = os.environ.get("FORGE_MODS_ROOT")
    if not root:
        import subprocess
        common = subprocess.run(["git", "-C", str(repo), "rev-parse", "--path-format=absolute", "--git-common-dir"], capture_output=True, text=True)
        root = str(Path(common.stdout.strip()).parent / "Mods") if common.returncode == 0 and common.stdout.strip() else ""
    return Path(root) if root else None


def media_path(relative: str, repo: Path = REPO) -> Path:
    """Where a build reads one file under Mods/ (relative to Mods/, e.g. ForgeOrdnance/Assets/Carrier/Models/forge-ordnance.bundle): this
    checkout's file when it exists, else the main checkout's. The same per-file rule as build-release-set.ps1 and Tools/local-inputs.ts,
    so a worktree's tracked tables (fx-slots.forge) are never read from a stale main checkout. A file found in neither place is returned
    at this checkout's path."""
    own = Path(repo) / "Mods" / relative
    shared = shared_mods(repo)
    if own.exists() or shared is None:
        return own
    return shared / relative if (shared / relative).exists() else own


def media_file(path: Path | str, repo: Path = REPO) -> Path:
    """media_path for a path already inside this checkout's Mods/ (audio folders, icons): any other path is returned as it is."""
    path = Path(path)
    try:
        return media_path(path.resolve().relative_to((Path(repo) / "Mods").resolve()).as_posix(), repo)
    except ValueError:
        return path


def media_names(folder: Path | str, repo: Path = REPO) -> set[str]:
    """File names in a folder inside this checkout's Mods/, together with the same folder of the main checkout (the set media_file reads from)."""
    folder = Path(folder)
    names = {path.name for path in folder.iterdir()} if folder.is_dir() else set()
    shared = media_file(folder, repo)
    return names | ({path.name for path in shared.iterdir()} if shared.is_dir() else set())


def media_audio_files(folder: Path | str, records: tuple[str, ...] = (), repo: Path = REPO) -> dict[str, Path]:
    """The AudioKit audio folder of a package (cues.forge, the record files, exactly the Ogg clips cues.forge names), each file through media_file."""
    folder = Path(folder)
    settings = media_file(folder / "cues.forge", repo)
    if not settings.is_file():
        raise ValueError("missing " + str(settings) + " (run the package's audio build)")
    cues = json.loads(settings.read_text(encoding="utf-8")).get("cues")
    if not isinstance(cues, dict) or not cues:
        raise ValueError("invalid AudioKit cues.forge " + str(settings))
    files = {"audio/cues.forge": settings}
    for name in records:
        files["audio/" + name] = media_file(folder / name, repo)
    for cue, entry in cues.items():
        for layer in entry.get("layers", [{"clips": [cue + ".ogg"]}]):
            for clip in layer["clips"]:
                path = media_file(folder / clip, repo)
                if not re.fullmatch(r"[a-z0-9_.-]+\.ogg", clip) or not path.is_file():
                    raise ValueError("missing audio file " + str(path) + " for cue " + cue)
                files["audio/" + clip] = path
    for entry, path in files.items():
        if not path.is_file():
            raise ValueError("missing " + str(path) + " for " + entry)
    stray = sorted({name for name in media_names(folder, repo) if name.endswith(".ogg")} - {entry[6:] for entry in files})
    if stray:
        raise ValueError("Ogg files not named by cues.forge would load as extra cues: " + ", ".join(stray))
    return files

def vfx_files(package: str, short: str, repo: Path = REPO, folder: str = "Assets/Vfx") -> dict[str, Path]:
    """The effect library's bundle, slot table and licence note (Mods/<package>/<folder>, each file through media_path); a package
    built without them ships its own effects."""
    names = ("forge-" + short + "-fx.bundle", "fx-slots.forge", "fx-licences.forge")
    files = {name: media_path(package + "/" + folder + "/" + name, repo) for name in names}
    if files["fx-slots.forge"].is_file() and not files[names[0]].is_file():
        raise ValueError(str(files[names[0]]) + ": fx-slots.forge is in Git but the bundle is not; effect media stays out of Git and has to be put there before packaging")
    return {name: path for name, path in files.items() if path.is_file()}


def release_version(mods: Path, name: str) -> str | None:
    """The version Release/release.json (the one table, beside the package folders in `mods`) gives package <name>; None where there is no table (a test fixture)."""
    table = mods / "Release" / "release.json"
    if not table.is_file():
        return None
    release = json.loads(table.read_text(encoding="utf-8"))
    return next((row["version"] for key in ("packages", "libraries", "standalone", "payloads") for row in release.get(key, []) if row["packageName"] == TEAM + "-" + name), None)


def package_files(folder: Path, name: str, files: dict[str, Path | str], output: Path) -> Path:
    """Zip an entry -> file table as package <name>: the manifest and the release.json version are checked, every file must exist, the zip is
    deterministic (write_zip) and replaces an earlier one only once it is whole. Never publishes."""
    manifest = check_manifest(folder, name)
    table = release_version(folder.parent, name)
    if table is not None and table != manifest["version_number"]:
        raise ValueError("release.json version " + table + " differs from manifest version " + str(manifest["version_number"]))
    data = {}
    for entry, source in files.items():
        if not Path(source).is_file():
            raise ValueError("missing " + str(source) + " for " + entry)
        data[entry] = Path(source).read_bytes()
    path = output / (TEAM + "-" + name + "-" + str(manifest["version_number"]) + ".zip")
    write_zip(path, data)
    print(path.name + ": " + str(path.stat().st_size) + " bytes, sha256 " + hashlib.sha256(path.read_bytes()).hexdigest() + ", " + str(len(data)) + " files")
    return path


def package_library(
    folder: Path, name: str, dlls: tuple[str, ...], artifacts: Path, output: Path,
    metadata: tuple[str, ...] = LIBRARY_METADATA, extra: dict[str, Path] | None = None, project: str | None = None,
) -> Path:
    """Zip a package: metadata beside the package, every dll read from artifacts/bin/<project>/release (by default the dll's own name),
    plus the extra entry -> file table."""
    files: dict[str, Path | str] = {entry: folder / entry for entry in metadata}
    for dll in dlls:
        built = artifacts / "bin" / (project or dll[:-4]) / "release" / dll
        if not built.is_file():
            raise ValueError("missing build output " + str(built) + "; run the Release build first")
        with built.open("rb") as head:
            if head.read(2) != b"MZ":
                raise ValueError(dll + " is not a PE assembly")
        files[dll] = built
    for entry, source in (extra or {}).items():
        if entry.endswith(".bundle") and source.is_file():
            with source.open("rb") as head:
                if not head.read(7) == b"UnityFS":
                    raise ValueError(entry + " is not a Unity bundle")
        files[entry] = source
    return package_files(folder, name, files, output)


def main(
    name: str, dlls: tuple[str, ...], extra: Callable[[Path, Path], dict[str, Path]] | None = None, metadata: tuple[str, ...] = LIBRARY_METADATA,
    project: str | None = None,
) -> int:
    """extra(folder, built) returns the entry -> file table beyond the dlls and metadata; built is the first dll's Release output folder.
    project names the project folder under artifacts/bin when the assembly is named differently from its project."""
    parser = argparse.ArgumentParser(description="Build the " + name + " Thunderstore zip from the Release build output.")
    parser.add_argument("--artifacts-path", type=Path, required=True, help="the dotnet --artifacts-path of the Release build")
    parser.add_argument("--output", type=Path, required=True, help="folder that receives the zip")
    args = parser.parse_args()
    artifacts = args.artifacts_path if args.artifacts_path.is_absolute() else Path.cwd() / args.artifacts_path
    output = args.output if args.output.is_absolute() else Path.cwd() / args.output
    try:
        folder = REPO / "Mods" / name
        built = artifacts / "bin" / (project or dlls[0][:-4]) / "release"
        package_library(folder, name, dlls, artifacts, output, metadata, extra(folder, built) if extra else None, project)
        return 0
    except (OSError, ValueError) as error:
        print("package_release: " + str(error), file=sys.stderr)
        return 2