from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path

from .models import GameInstall


def _steam_registry_paths() -> list[Path]:
    paths: list[Path] = []
    if os.name != "nt":
        return paths
    try:
        import winreg

        for hive, key_name in (
            (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam"),
        ):
            try:
                with winreg.OpenKey(hive, key_name) as key:
                    for value_name in ("SteamPath", "InstallPath"):
                        try:
                            value, _ = winreg.QueryValueEx(key, value_name)
                        except OSError:
                            continue
                        if value:
                            paths.append(Path(str(value)))
            except OSError:
                continue
    except ImportError:
        pass
    return paths


def _common_steam_paths() -> list[Path]:
    values = [
        Path(r"D:\Steam"),
        Path(r"C:\Steam"),
        Path(r"E:\Steam"),
        Path(r"D:\SteamLibrary"),
        Path(r"E:\SteamLibrary"),
        Path(r"F:\SteamLibrary"),
        Path(r"C:\Program Files (x86)\Steam"),
        Path(r"C:\Program Files\Steam"),
    ]
    local = os.environ.get("LOCALAPPDATA")
    if local:
        values.append(Path(local) / "Steam")
    return values


def _library_roots(steam_root: Path) -> list[Path]:
    roots = [steam_root]
    candidates = [
        steam_root / "steamapps" / "libraryfolders.vdf",
        steam_root / "config" / "libraryfolders.vdf",
    ]
    for vdf in candidates:
        if not vdf.is_file():
            continue
        try:
            text = vdf.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for match in re.finditer(r'"path"\s+"([^"]+)"', text, flags=re.IGNORECASE):
            raw = match.group(1).replace("\\\\", "\\")
            roots.append(Path(raw))
    unique: list[Path] = []
    seen: set[str] = set()
    for root in roots:
        key = os.path.normcase(os.path.abspath(root))
        if key not in seen:
            seen.add(key)
            unique.append(root)
    return unique


def validate_game_root(root: Path | str) -> GameInstall:
    path = Path(root)
    if path.name.casefold() == "gtfo_data":
        path = path.parent
    data_dir = path / "GTFO_Data"
    issues: list[str] = []
    if not path.is_dir():
        issues.append("GTFO 安装目录不存在")
    if not (path / "GTFO.exe").is_file():
        issues.append("未找到 GTFO.exe")
    if not (path / "UnityPlayer.dll").is_file():
        issues.append("未找到 UnityPlayer.dll")
    if not data_dir.is_dir():
        issues.append("未找到 GTFO_Data")
    assets = list(data_dir.glob("sharedassets*.assets")) if data_dir.is_dir() else []
    if data_dir.is_dir() and not assets:
        issues.append("GTFO_Data 中没有 sharedassets*.assets")
    return GameInstall(
        root=path,
        data_dir=data_dir,
        source="manual",
        valid=not issues,
        issues=issues,
    )


def discover_game_installs() -> list[GameInstall]:
    found: list[GameInstall] = []
    seen: set[str] = set()
    steam_roots = _steam_registry_paths() + _common_steam_paths()
    for steam_root in steam_roots:
        for library in _library_roots(steam_root):
            candidate = library / "steamapps" / "common" / "GTFO"
            key = os.path.normcase(os.path.abspath(candidate))
            if key in seen or not candidate.is_dir():
                continue
            seen.add(key)
            install = validate_game_root(candidate)
            install.source = "steam"
            if install.valid:
                found.append(install)
    return found


def best_game_install(preferred: str = "") -> GameInstall | None:
    if preferred:
        candidate = validate_game_root(preferred)
        if candidate.valid:
            return candidate
    installs = discover_game_installs()
    return installs[0] if installs else None


def is_gtfo_running() -> bool:
    if os.name != "nt":
        return False
    try:
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq GTFO.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            timeout=5,
            startupinfo=startupinfo,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return b"gtfo.exe" in result.stdout.lower()
    except (OSError, subprocess.SubprocessError):
        return False
