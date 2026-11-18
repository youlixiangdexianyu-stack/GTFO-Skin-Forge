"""Detection test for verify_release's zip checks: a zip PNG with a local path in a text chunk must be reported, a clean one and a stripped
one must not; a zip laid out like the published 1.0.0 packages (lang/ and fonts/ at the root, which r2modman flattened so every text showed
its key) must be reported, as must flattened names colliding and a language table missing or out of step, while the zip_entry layout passes.
check_reproducible must pass a fixture built twice under the shared build settings and catch it built without determinism or path map."""
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_reproducible import differences  # noqa: E402
from package_common import PNG_SIGNATURE, manager_install, strip_png_text, zip_entry  # noqa: E402
from verify_release import install_problems, language_problems, plugin_files, png_metadata_problems  # noqa: E402


def chunk(kind: bytes, body: bytes) -> bytes:
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))


IMAGE = chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"\0\0\0\0")) + chunk(b"IEND", b"")
CLEAN = PNG_SIGNATURE + IMAGE
leaks = {
    "tEXt": PNG_SIGNATURE + chunk(b"tEXt", b"Source\0C:\\Users\\example\\frames-preview\\a.png") + IMAGE,
    "zTXt": PNG_SIGNATURE + chunk(b"zTXt", b"Source\0\0" + zlib.compress(b"/home/nainf/frames-preview")) + IMAGE,
}
failures = []
for kind, data in leaks.items():
    if not png_metadata_problems("t", {"icons/a.png": data}):
        failures.append(kind + " leak not detected")
    if strip_png_text(data) != CLEAN or png_metadata_problems("t", {"icons/a.png": strip_png_text(data)}):
        failures.append(kind + " not stripped")
if png_metadata_problems("t", {"icon.png": CLEAN, "manifest.json": b"C:\\Users\\x"}):
    failures.append("clean PNG or non-PNG flagged")

TABLE = b'{"list.title": "Replays"}'
PLUGIN = {"ForgeReplay.dll": b"MZ", "LICENSE": b"x", "lang/en.forge": TABLE, "lang/zh.forge": TABLE, "Carrier/audio/cues.forge": b"{}",
          "Frames/audio/cues.forge": b"{}"}
good = {zip_entry("ForgeReplay", path): data for path, data in {**PLUGIN, "manifest.json": b"{}", "README.md": b"r"}.items()}
if install_problems("ForgeReplay", good) or language_problems("ForgeReplay", plugin_files("ForgeReplay", good)):
    failures.append("zip_entry layout flagged")
if manager_install("NAinfini-ForgeReplay", "plugins/ForgeReplay/lang/en.forge") != "plugins/NAinfini-ForgeReplay/ForgeReplay/lang/en.forge":
    failures.append("plugins/<Name>/ not kept whole by the simulated manager install")
published = {"manifest.json": b"{}", "ForgeReplay.dll": b"MZ", "lang/en.forge": TABLE, "lang/zh.forge": TABLE}
if not any("lang/en.forge" in problem and "plugins/NAinfini-ForgeReplay/en.forge" in problem for problem in install_problems("ForgeReplay", published)):
    failures.append("1.0.0 layout (lang/ at the zip root, flattened by the manager) not detected")
if not any("both install as" in problem for problem in install_problems("ForgeReplay", {"a/cues.forge": b"", "b/cues.forge": b""})):
    failures.append("flattened name collision not detected")
if not language_problems("ForgeReplay", {"lang/en.forge": TABLE}) or not language_problems("ForgeReplay", {"lang/en.forge": TABLE, "lang/zh.forge": b"{}"}):
    failures.append("missing or out-of-step language table not detected")

# Reproducible build (check_reproducible): a fixture assembly compiled under the shared Mods build settings into two output
# directories must compare equal; with determinism off, or with the path map replaced, the comparison must catch it.
FIXTURE = """<Project>
  <Import Project="{mods}/Directory.Build.props" />
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
  <Import Project="{mods}/Directory.Build.targets" />
</Project>"""
with tempfile.TemporaryDirectory() as temp:
    source = Path(temp) / "src"
    source.mkdir()
    (source / "Fixture.csproj").write_text(FIXTURE.format(mods=Path(__file__).resolve().parents[1].as_posix()), encoding="utf-8")
    (source / "Fixture.cs").write_text("namespace Fixture { public static class F { public static int One() => 1; } }\n", encoding="utf-8")

    def fixture_differs(name: str, *properties: str) -> bool:
        roots = [Path(temp) / name / side for side in ("a", "b")]
        for root in roots:
            run = subprocess.run(["dotnet", "build", str(source / "Fixture.csproj"), "-c", "Release", "--artifacts-path", str(root), "-v:q", "-nologo", *properties],
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            if run.returncode != 0:
                sys.exit("fixture build failed:\n" + run.stdout)
        return bool(differences(roots, ["bin/Fixture/release/Fixture.dll"]))

    if fixture_differs("shared"):
        failures.append("shared build settings: two output directories built different bytes")
    if not fixture_differs("nondeterministic", "-p:Deterministic=false"):
        failures.append("non-deterministic build not detected")
    if not fixture_differs("unmapped", "-p:PathMap="):
        failures.append("build with the output directory left in the assembly not detected")
print("\n".join(failures) or "ok")
sys.exit(1 if failures else 0)
