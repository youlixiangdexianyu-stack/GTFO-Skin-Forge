# -*- mode: python ; coding: utf-8 -*-

from PyInstaller.utils.hooks import collect_all, collect_data_files, collect_dynamic_libs


unity_datas, unity_binaries, unity_hidden = collect_all("UnityPy")
fmod_datas = collect_data_files("fmod_toolkit")
archspec_datas = collect_data_files("archspec", includes=["json/cpu/*"])
cv2_binaries = collect_dynamic_libs("cv2")

a = Analysis(
    ["run_app.py"],
    pathex=["src"],
    binaries=unity_binaries + cv2_binaries,
    datas=unity_datas
    + fmod_datas
    + archspec_datas
    + [
        ("src/gtfo_skin_forge/data/catalog.json", "gtfo_skin_forge/data"),
        ("src/gtfo_skin_forge/data/assembly.json", "gtfo_skin_forge/data"),
        ("src/gtfo_skin_forge/data/preview_meshes", "gtfo_skin_forge/data/preview_meshes"),
        ("src/gtfo_skin_forge/data/dly_plugin", "gtfo_skin_forge/data/dly_plugin"),
        ("src/gtfo_skin_forge/data/dynamic_plugin", "gtfo_skin_forge/data/dynamic_plugin"),
        ("src/gtfo_skin_forge/data/checkmark.svg", "gtfo_skin_forge/data"),
        ("src/gtfo_skin_forge/data/spin_up.svg", "gtfo_skin_forge/data"),
        ("src/gtfo_skin_forge/data/spin_down.svg", "gtfo_skin_forge/data"),
    ],
    hiddenimports=unity_hidden + ["cv2"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "PySide6.QtWebEngineCore",
        "PySide6.QtWebEngineWidgets",
        "PySide6.QtPdf",
        "PySide6.QtPdfWidgets",
    ],
    noarchive=False,
    optimize=1,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="GTFOSkinForge",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="GTFOSkinForge",
)
