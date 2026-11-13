"""Build the ForgeCore Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeCore/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

The fonts come from UI/tools/fonts/build_fonts.ps1, the shared effect bundle from Tools/Vfx. Then check the whole release set with
Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common

FONTS = ("ForgeUI-fonts.bundle", "OFL-Rajdhani.forge", "OFL-GlowSans.forge")


def extra(folder: Path, built: Path) -> dict[str, Path]:
    """The UI's language files and fonts, the audio decoder with its licence, and the shared heavy-blast effect bundle with its slot table
    (the Forge effect bundle and the shader bundles are embedded in the DLL). folder is <repository>/Mods/ForgeCore."""
    vendor = folder / "Audio" / "vendor" / "NVorbis"
    return {
        **package_common.lang_files(folder),
        **{"fonts/" + name: package_common.media_file(folder / "Assets" / "fonts" / name) for name in FONTS},
        "NVorbis.dll": built / "NVorbis.dll", "NVorbis.LICENSE": vendor / "LICENSE", "NVorbis.NOTICE.md": vendor / "NOTICE.md",
        **package_common.vfx_files(folder.name, "visual", folder.parents[1]),
    }


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeCore", ("ForgeCore.dll",), extra))
