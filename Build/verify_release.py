"""Prove a set of Forge Thunderstore zips against the Release build and, optionally, against an installed profile.

    python Mods/Build/verify_release.py --packages <zip folder> --artifacts-path <dotnet artifacts> [--installed <BepInEx/plugins>]

Fails (exit 1) when
- a dll in a zip is not byte-identical to the Release build output (artifacts/bin/<project>/release/<dll>);
- a package's main dll version differs from its manifest version, or a NAinfini dependency is missing from the set or
  asks for another version than the set holds;
- a player zip holds a file named .txt, .json, .yml, .yaml or .ini (r2modman lists those as config files; Forge's own data is .forge and .cfg stays allowed; Thunderstore's manifest.json is the one exception);
- a zip lacks LICENSE (the MIT text with a copyright line) or NOTICE (copyright line and a third-party section), or holds a file named like
  an original game asset (the release carries 0 of them);
- a type or member that one dll references in another dll of the set does not exist there (check-assembly-refs.ps1);
- with --installed: any file of a zip is missing or different in plugins/NAinfini-<name>/, or the folder holds a dll no zip owns.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
import zlib

from package_common import PNG_TEXT_CHUNKS, png_chunks, strip_png_text

HERE = Path(__file__).resolve().parent
LICENCE = re.compile(r'(?s)^MIT License\n\nCopyright \(c\) \d{4} \S.*\n\nPermission is hereby granted,.+THE SOFTWARE IS PROVIDED "AS IS"')
# Names of the game's own files and of the interop assemblies generated from them; a Forge zip never carries one.
GAME_ASSET = re.compile(
    r"(?i)(^|/)(gameassembly\.dll|assembly-csharp[^/]*\.dll|unityplayer\.dll|globalgamemanagers[^/]*|data\.unity3d|sharedassets[^/]*|level\d+"
    r"|[^/]*\.assets|[^/]*\.ress|[^/]*\.resource|il2cpp[^/]*\.dll|[^/]*-asm\.dll)$"
)


def licence_problems(name: str, files: dict[str, bytes]) -> list[str]:
    problems = []
    licence = files.get("LICENSE")
    if licence is None or not LICENCE.match(licence.decode("utf-8", errors="replace").replace("\r\n", "\n")):
        problems.append(name + ": LICENSE is missing or is not the MIT text with a copyright line")
    notice = files.get("NOTICE")
    text = notice.decode("utf-8", errors="replace") if notice is not None else ""
    if not re.search(r"Copyright \(c\) \d{4} \S", text) or not re.search(r"(?i)third-party", text):
        problems.append(name + ": NOTICE is missing or lacks a copyright line and a third-party section")
    problems.extend(name + ": " + entry + " is named like an original game asset" for entry in files if GAME_ASSET.search(entry))
    return problems


# r2modman lists every profile file with one of these extensions as a config file; Forge's own data files are `.forge`.
CONFIG_LIST_EXTENSIONS = (".txt", ".json", ".yml", ".yaml", ".ini")


# A drive path or a home directory in a PNG text chunk is the packaging machine leaking into the zip.
LOCAL_PATH = re.compile(rb"(?i)[a-z]:[\\/]|/Users/|/home/")


def png_metadata_problems(name: str, files: dict[str, bytes]) -> list[str]:
    problems = []
    for entry, data in files.items():
        if not entry.lower().endswith(".png") or strip_png_text(data) == data:
            continue
        texts = [body if kind != b"zTXt" else zlib.decompress(body.split(b"\0", 1)[1][1:]) for kind, _s, _e, body in png_chunks(data) if kind in PNG_TEXT_CHUNKS]
        if any(LOCAL_PATH.search(text) for text in texts):
            problems.append(name + ": " + entry + " carries a local path in its PNG text metadata")
    return problems


def audiences() -> dict[str, str]:
    """Package name -> audience from Release/release.json; a package it does not list counts as a player package."""
    identity = json.loads((HERE.parent / "Release" / "release.json").read_text(encoding="utf-8"))
    rows = []
    for key in ("packages", "libraries", "standalone", "payloads"):
        value = identity.get(key) or []
        rows += value if isinstance(value, list) else [value]
    return {row["packageName"].split("-", 1)[1]: row["audience"] for row in rows}


def config_extension_problems(name: str, files: dict[str, bytes]) -> list[str]:
    return [name + ": " + entry + " would be listed by r2modman as a config file; a player package's own files are .forge" for entry in files
            if entry != "manifest.json" and entry.lower().endswith(CONFIG_LIST_EXTENSIONS)]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def require_plain_entry(package: str, entry: str) -> None:
    """A zip entry is a relative path of plain names: it is later written under a temporary folder."""
    if not entry or "\\" in entry or ":" in entry or any(part in ("", ".", "..") for part in entry.split("/")):
        raise ValueError(package + ": unsafe archive entry name " + repr(entry))


def read_packages(folder: Path) -> dict[str, tuple[Path, dict[str, bytes], dict[str, object]]]:
    packages = {}
    for path in sorted(folder.glob("NAinfini-*.zip")):
        with zipfile.ZipFile(path) as archive:
            for info in archive.infolist():
                require_plain_entry(path.name, info.filename.rstrip("/") if info.is_dir() else info.filename)
            files = {info.filename: archive.read(info) for info in archive.infolist() if not info.is_dir()}
        manifest = json.loads(files["manifest.json"].decode("utf-8"))
        expected = "NAinfini-" + manifest["name"] + "-" + manifest["version_number"] + ".zip"
        if path.name != expected:
            raise ValueError(path.name + " should be named " + expected)
        packages[manifest["name"]] = (path, files, manifest)
    if not packages:
        raise ValueError("no NAinfini-*.zip in " + str(folder))
    return packages


def build_outputs(artifacts: Path, dll: str) -> list[Path]:
    return sorted((artifacts / "bin").glob("*/release/" + dll))


def verify(packages_dir: Path, artifacts: Path, installed: Path | None) -> list[str]:
    problems: list[str] = []
    packages = read_packages(packages_dir)
    audience = audiences()

    for name, (path, files, manifest) in packages.items():
        problems.extend(licence_problems(name, files))
        problems.extend(png_metadata_problems(name, files))
        if audience.get(name, "player") == "player":
            problems.extend(config_extension_problems(name, files))
        for entry, data in files.items():
            if not entry.endswith(".dll"):
                continue
            candidates = build_outputs(artifacts, entry)
            if not candidates:
                problems.append(name + ": " + entry + " has no build output under " + str(artifacts / "bin"))
            for built in candidates:
                if built.read_bytes() != data:
                    problems.append(name + ": " + entry + " differs from build output " + str(built))
        for dependency in manifest["dependencies"]:
            team, dep_name, version = dependency.split("-")
            if team != "NAinfini":
                continue
            if dep_name not in packages:
                problems.append(name + ": dependency " + dependency + " is not in the set")
            elif packages[dep_name][2]["version_number"] != version:
                problems.append(name + ": dependency " + dependency + " but the set holds " + str(packages[dep_name][2]["version_number"]))

    temporary = Path(tempfile.mkdtemp(prefix="verify-release-"))
    try:
        dlls = []
        for name, (_path, files, _manifest) in packages.items():
            for entry, data in files.items():
                if entry.endswith(".dll") and Path(entry).name.startswith("Forge"):
                    target = temporary / name / entry
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(data)
                    dlls.append(str(target))
        listing = temporary / "dlls.txt"
        listing.write_text("\n".join(dlls), encoding="utf-8")
        powershell = shutil.which("pwsh")
        if not powershell:
            raise ValueError("pwsh is required for the assembly reference check")
        result = subprocess.run(
            [powershell, "-NoProfile", "-NonInteractive", "-File", str(HERE / "check-assembly-refs.ps1"), "-List", str(listing)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
        )
        if result.returncode:
            problems.extend((result.stderr or result.stdout).strip().splitlines())
        for line in result.stdout.splitlines():
            if line.startswith("ASM "):
                _tag, assembly, version = line.split()
                if assembly in packages:
                    manifest_version = str(packages[assembly][2]["version_number"])
                    if version != manifest_version + ".0":
                        problems.append(assembly + ": assembly version " + version + " differs from manifest " + manifest_version)
            elif line.startswith("assembly refs ok"):
                print(line)
    finally:
        shutil.rmtree(temporary, ignore_errors=True)

    if installed is not None:
        for name, (_path, files, _manifest) in packages.items():
            folder = installed / ("NAinfini-" + name)
            if not folder.is_dir():
                problems.append(name + ": " + str(folder) + " is not installed")
                continue
            for entry, data in files.items():
                target = folder / entry
                if not target.is_file() or sha256(target.read_bytes()) != sha256(data):
                    problems.append(name + ": installed " + entry + " is missing or differs from the zip")
            owned = {entry for entry in files if entry.endswith(".dll")}
            for stray in folder.rglob("*.dll"):
                if stray.relative_to(folder).as_posix() not in owned:
                    problems.append(name + ": installed dll " + stray.relative_to(folder).as_posix() + " belongs to no zip")
        known = {"NAinfini-" + name for name in packages}
        for folder in installed.glob("NAinfini-*"):
            if folder.name not in known:
                problems.append("installed folder " + folder.name + " belongs to no package of the set")
        # One plugin GUID installed twice (GTFO-API at the plugins root and in its own package folder) makes two assemblies race for it and,
        # for GTFO-API, two network signatures: the same dll name must not appear in two places with different bytes.
        seen: dict[str, tuple[Path, str]] = {}
        for dll in installed.rglob("*.dll"):
            digest = sha256(dll.read_bytes())
            earlier = seen.setdefault(dll.name, (dll, digest))
            if earlier[1] != digest:
                problems.append("installed " + dll.name + " exists twice with different bytes: " + str(earlier[0]) + " and " + str(dll))

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--artifacts-path", type=Path, required=True)
    parser.add_argument("--installed", type=Path)
    args = parser.parse_args()
    try:
        problems = verify(args.packages.resolve(), args.artifacts_path.resolve(), args.installed.resolve() if args.installed else None)
    except (OSError, ValueError, KeyError) as error:
        print("verify_release: " + str(error), file=sys.stderr)
        return 2
    for problem in problems:
        print("verify_release: " + problem, file=sys.stderr)
    if problems:
        return 1
    print("verify_release: ok (" + str(len(read_packages(args.packages.resolve()))) + " packages)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


