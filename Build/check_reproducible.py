"""Prove that the release build is reproducible: one commit and one toolchain build every released assembly byte for byte the
same in any output directory, so a content pin (catalog/forge-release/content-pins.json) and the hook evidence hashes change
only when the sources do (Mods/Release/README.md).

    python Mods/Build/check_reproducible.py --build <work dir> --bepinex <profile>/BepInEx
    python Mods/Build/check_reproducible.py --compare <artifacts root> <artifacts root> [...]

--build compiles Mods/Forge.Architecture.sln into two fresh artifacts paths below the work dir and compares them; --compare
only compares existing --artifacts-path builds. Exit 1 names every released assembly whose SHA-256 differs or that one build
lacks. The released assemblies are the ones check-interop-members.py scans (released_projects), at
<root>/bin/<project>/release/<assembly>.dll.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys

REPO = Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("check_interop_members", REPO / "Tools/Forge/check-interop-members.py")
_interop = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_interop)


def released_assemblies() -> list[str]:
    return [f"bin/{p.stem}/release/{_interop.assembly_name(p)}.dll" for p in _interop.released_projects()]


def differences(roots: list[Path], assemblies: list[str]) -> list[str]:
    problems = []
    for relative in assemblies:
        digests = [hashlib.sha256((root / relative).read_bytes()).hexdigest() if (root / relative).is_file() else "missing" for root in roots]
        if len(set(digests)) != 1:
            problems.append(f"{relative}: " + "  ".join(f"{root}={digest[:16]}" for root, digest in zip(roots, digests)))
    return problems


def build(root: Path, bepinex: str) -> None:
    shutil.rmtree(root, ignore_errors=True)
    run = subprocess.run(["dotnet", "build", "Mods/Forge.Architecture.sln", "-c", "Release", "--artifacts-path", str(root),
                          f"-p:GTFOBepInExPath={bepinex}", "-v:q", "-nologo"], cwd=REPO)
    if run.returncode != 0:
        sys.exit(f"build into {root} failed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--build", type=Path)
    group.add_argument("--compare", type=Path, nargs="+")
    parser.add_argument("--bepinex")
    args = parser.parse_args()
    if args.build:
        if not args.bepinex:
            parser.error("--build needs --bepinex")
        roots = [args.build.resolve() / name for name in ("a", "b")]
        for root in roots:
            build(root, args.bepinex)
    else:
        roots = [root.resolve() for root in args.compare]
        if len(roots) < 2:
            parser.error("--compare needs at least two artifacts roots")
    assemblies = released_assemblies()
    problems = differences(roots, assemblies)
    print("\n".join(problems) or f"ok: {len(assemblies)} released assemblies identical in {len(roots)} builds")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
