"""Build the ForgeSkin Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeSkin/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

The zip holds ForgeSkin.Plugin.dll (the pack reader is compiled into it), its lang/ texts beside it and the metadata;
ForgeCore comes as a dependency. Then check the whole release set with Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common


METADATA = ("manifest.json", "icon.png", "README.md", "DEVELOPERS.md", "CHANGELOG.md", "LICENSE", "NOTICE")


def extra(folder: Path, built: Path) -> dict[str, Path]:
    return package_common.lang_files(folder)


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeSkin", ("ForgeSkin.Plugin.dll",), extra, METADATA, "ForgeSkin"))
