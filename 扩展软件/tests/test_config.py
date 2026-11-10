from __future__ import annotations

import json

from pathlib import Path

from gtfo_skin_forge.config import (
    load_settings,
    migrate_legacy_app_data,
    settings_path,
)


def test_auto_backup_defaults_to_disabled(forge_data):
    settings = load_settings()
    assert settings.auto_backup is False
    assert settings.resolution_mode == "user"
    assert settings.preview_ambient_intensity == 1.0
    assert settings.preview_ambient_color == "#fff4e8"
    assert settings.preview_emissive_enabled is True
    assert settings.preview_auto_rotate is True
    assert settings.window_geometry == ""
    assert settings.splitter_sizes == []


def test_old_settings_migrate_auto_backup_to_disabled(forge_data):
    path = settings_path()
    path.write_text(
        json.dumps(
            {
                "game_root": r"D:\Steam\steamapps\common\GTFO",
                "auto_backup": True,
            }
        ),
        encoding="utf-8",
    )
    settings = load_settings()
    assert settings.auto_backup is False
    assert settings.resolution_mode == "user"


def test_legacy_application_data_migrates_to_portable_directory(
    tmp_path,
    monkeypatch,
):
    legacy_root = tmp_path / "Local" / "GTFOSkinForge"
    (legacy_root / "logs").mkdir(parents=True)
    record_dir = legacy_root / "backups" / "record-1"
    record_dir.mkdir(parents=True)
    (record_dir / "sharedassets999.assets").write_bytes(b"backup")
    (record_dir / "install-record.json").write_text(
        json.dumps(
            {
                "id": "record-1",
                "backup_dir": str(record_dir),
                "files": [
                    {
                        "file": "sharedassets999.assets",
                        "target": "D:/GTFO/GTFO_Data/sharedassets999.assets",
                        "backup": str(record_dir / "sharedassets999.assets"),
                    }
                ],
                "manifest_path": str(record_dir / "install-record.json"),
            }
        ),
        encoding="utf-8",
    )
    (legacy_root / "logs" / "app.log").write_text("old log", encoding="utf-8")
    (legacy_root / "settings.json").write_text('{"game_root":"D:/GTFO"}', encoding="utf-8")
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))
    destination = tmp_path / "Portable" / "GTFO_UserData"
    migrate_legacy_app_data(destination)
    assert (destination / "logs" / "app.log").read_text(encoding="utf-8") == "old log"
    assert (destination / "settings.json").is_file()
    migrated_record = json.loads(
        (destination / "backups" / "record-1" / "install-record.json").read_text(
            encoding="utf-8"
        )
    )
    assert Path(migrated_record["backup_dir"]) == destination / "backups" / "record-1"
    assert Path(migrated_record["files"][0]["backup"]).parent == (
        destination / "backups" / "record-1"
    )
    assert not legacy_root.exists()
