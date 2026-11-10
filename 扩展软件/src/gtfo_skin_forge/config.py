from __future__ import annotations

import json
import os
import shutil
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


APP_NAME = "GTFOSkinForge"
DATA_FOLDER_NAME = "GTFO_UserData"


@dataclass(slots=True)
class AppSettings:
    schema_version: int = 7
    game_root: str = ""
    persistent_backup: bool = True
    auto_backup: bool = False
    clean_asset_dir: str = ""
    backup_dir: str = ""
    resolution_mode: str = "user"
    last_weapon_id: str = ""
    last_texture_folder: str = ""
    preview_ambient_intensity: float = 1.0
    preview_ambient_color: str = "#fff4e8"
    preview_emissive_enabled: bool = True
    preview_auto_rotate: bool = True
    window_geometry: str = ""
    splitter_sizes: list[int] = field(default_factory=list)
    active_workspace: str = "skin"
    dly_profile_root: str = ""
    dly_splitter_sizes: list[int] = field(default_factory=list)
    dly_last_part_id: str = "receiver"
    theme: str = "gtfo-dark"


def _portable_data_dir() -> Path:
    override = os.environ.get("GTFO_SKIN_FORGE_DATA")
    if override:
        return Path(override)
    if getattr(sys, "frozen", False):
        base = Path(sys.executable).resolve().parent
    else:
        base = Path(__file__).resolve().parents[2]
    return base / DATA_FOLDER_NAME


def legacy_app_data_dir() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    base = Path(local) if local else Path.home() / "AppData" / "Local"
    return base / APP_NAME


def app_data_dir() -> Path:
    path = _portable_data_dir()
    path.mkdir(parents=True, exist_ok=True)
    return path


def ensure_app_dirs() -> dict[str, Path]:
    root = app_data_dir()
    if not os.environ.get("GTFO_SKIN_FORGE_DATA"):
        migrate_legacy_app_data(root)
    paths = {
        "root": root,
        "baselines": root / "baselines",
        "backups": root / "backups",
        "cache": root / "cache",
        "projects": root / "projects",
        "logs": root / "logs",
        "temp": root / "temp",
    }
    for path in paths.values():
        path.mkdir(parents=True, exist_ok=True)
    return paths


def migrate_legacy_app_data(destination: Path) -> None:
    source = legacy_app_data_dir().resolve()
    destination = destination.resolve()
    if source == destination or not source.is_dir():
        return
    destination.mkdir(parents=True, exist_ok=True)
    for child in list(source.iterdir()):
        target = destination / child.name
        try:
            if child.is_dir():
                _merge_directory(child, target)
            elif not target.exists():
                shutil.move(str(child), str(target))
        except OSError:
            continue
    _repair_migrated_backup_paths(source / "backups", destination / "backups")
    try:
        source.rmdir()
    except OSError:
        pass


def _merge_directory(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    for child in list(source.iterdir()):
        target = destination / child.name
        if child.is_dir():
            _merge_directory(child, target)
        elif target.exists():
            legacy = destination / f"{child.stem}.legacy{child.suffix}"
            if not legacy.exists():
                shutil.move(str(child), str(legacy))
        else:
            shutil.move(str(child), str(target))
    try:
        source.rmdir()
    except OSError:
        pass


def _repair_migrated_backup_paths(old_root: Path, new_root: Path) -> None:
    if not new_root.is_dir():
        return
    for record_dir in new_root.iterdir():
        if not record_dir.is_dir():
            continue
        old_record_dir = old_root / record_dir.name
        for filename in ("install-record.json", "backup-manifest.json"):
            path = record_dir / filename
            if not path.is_file():
                continue
            try:
                value = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            text = json.dumps(value, ensure_ascii=False)
            text = text.replace(
                str(old_record_dir).replace("\\", "\\\\"),
                str(record_dir).replace("\\", "\\\\"),
            )
            try:
                updated = json.loads(text)
            except ValueError:
                continue
            temporary = path.with_suffix(path.suffix + ".tmp")
            temporary.write_text(
                json.dumps(updated, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            os.replace(temporary, path)


def settings_path() -> Path:
    return app_data_dir() / "settings.json"


def load_settings() -> AppSettings:
    path = settings_path()
    if not path.is_file():
        return AppSettings()
    try:
        value: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        version = int(value.get("schema_version", 1))
        if version < 2:
            value["auto_backup"] = False
        if version < 3:
            value["resolution_mode"] = "user"
            value["schema_version"] = 3
        if version < 4:
            value.setdefault("preview_ambient_intensity", 1.0)
            value.setdefault("preview_ambient_color", "#fff4e8")
            value.setdefault("preview_emissive_enabled", True)
            value["schema_version"] = 4
        if version < 5:
            value.setdefault("preview_auto_rotate", True)
            value["schema_version"] = 5
        if version < 6:
            value.setdefault("window_geometry", "")
            value.setdefault("splitter_sizes", [])
            value["schema_version"] = 6
        if version < 7:
            value.setdefault("active_workspace", "skin")
            value.setdefault("dly_profile_root", "")
            value.setdefault("dly_splitter_sizes", [])
            value.setdefault("dly_last_part_id", "receiver")
            value["schema_version"] = 7
        settings = AppSettings(
            **{
                key: value[key]
                for key in AppSettings.__dataclass_fields__
                if key in value
            }
        )
        if version < 7:
            save_settings(settings)
        return settings
    except (OSError, ValueError, TypeError):
        return AppSettings()


def save_settings(settings: AppSettings) -> None:
    path = settings_path()
    temp = path.with_suffix(".json.tmp")
    temp.write_text(json.dumps(asdict(settings), ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def cache_path(name: str) -> Path:
    return ensure_app_dirs()["cache"] / name
