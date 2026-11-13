"""Detection test for verify_release's PNG check: a zip PNG with a local path in a text chunk must be reported, a clean one and a stripped one must not."""
import struct
import sys
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from package_common import PNG_SIGNATURE, strip_png_text  # noqa: E402
from verify_release import png_metadata_problems  # noqa: E402


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
print("\n".join(failures) or "ok")
sys.exit(1 if failures else 0)
