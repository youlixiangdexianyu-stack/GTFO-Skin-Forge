from __future__ import annotations

import json
import re
from pathlib import Path

from gtfo_skin_forge.assets import sha256
from gtfo_skin_forge.installer import InstallManager, migrate_backup_records
from gtfo_skin_forge.models import BuildFileResult, BuildResult, GameInstall


def test_install_and_restore(tmp_path, monkeypatch):
    monkeypatch.setattr("gtfo_skin_forge.installer.is_gtfo_running", lambda: False)
    game_root = tmp_path / "GTFO"
    data_dir = game_root / "GTFO_Data"
    data_dir.mkdir(parents=True)
    original = data_dir / "sharedassets999.assets"
    original.write_bytes(b"official")
    build_dir = tmp_path / "build"
    build_dir.mkdir()
    generated = build_dir / "sharedassets999.assets"
    generated.write_bytes(b"custom")
    generated_sha = sha256(generated)
    result = BuildResult(
        item_id="test",
        item_name="狙击枪 · Köning PR 11",
        output_dir=build_dir,
        manifest_path=build_dir / "build-manifest.json",
        files=[
            BuildFileResult(
                asset=999,
                output_path=generated,
                source_path=original,
                sha256=generated_sha,
                materials=["Test"],
                texture_path_ids={},
                emissive={},
            )
        ],
        warnings=[],
        affected_items=[],
    )
    manager = InstallManager(tmp_path / "backups", allow_system_drive=True)
    record = manager.install(
        GameInstall(game_root, data_dir, "test"),
        result,
        persistent_backup=True,
    )
    assert original.read_bytes() == b"custom"
    assert re.fullmatch(
        r"\d{4}-\d{2}-\d{2}-\d{6}-备份-狙击枪 · Köning PR 11(?:-\d+)?",
        Path(record.backup_dir).name,
    )
    assert Path(record.manifest_path).is_file()
    manager.restore(record)
    assert original.read_bytes() == b"official"


def test_backup_directory_accepts_system_drive(tmp_path):
    backup_dir = tmp_path / "backups"
    assert backup_dir.drive.casefold() == "c:"
    manager = InstallManager(backup_dir)
    assert manager.backup_root == backup_dir.resolve()


def test_legacy_backup_records_can_be_migrated(tmp_path):
    source = tmp_path / "old-backups" / "record-1"
    source.mkdir(parents=True)
    old_backup = source / "sharedassets999.assets"
    old_backup.write_bytes(b"old")
    record_value = {
        "id": "record-1",
        "created_at": "now",
        "game_root": "D:/GTFO",
        "backup_dir": str(source),
        "files": [
            {
                "file": "sharedassets999.assets",
                "target": "D:/GTFO/GTFO_Data/sharedassets999.assets",
                "backup": str(old_backup),
                "backup_sha256": "",
                "installed_sha256": "",
                "existed": "1",
            }
        ],
        "manifest_path": str(source / "install-record.json"),
    }
    (source / "install-record.json").write_text(
        json.dumps(record_value),
        encoding="utf-8",
    )
    destination = tmp_path / "new-backups"
    assert migrate_backup_records(tmp_path / "old-backups", destination) == 1
    record_path = destination / "record-1" / "install-record.json"
    value = json.loads(record_path.read_text(encoding="utf-8"))
    assert Path(value["backup_dir"]) == destination / "record-1"
    assert Path(value["files"][0]["backup"]).parent == destination / "record-1"


def test_install_can_delete_temporary_backup_after_success(tmp_path, monkeypatch):
    monkeypatch.setattr("gtfo_skin_forge.installer.is_gtfo_running", lambda: False)
    game_root = tmp_path / "GTFO"
    data_dir = game_root / "GTFO_Data"
    data_dir.mkdir(parents=True)
    target = data_dir / "sharedassets999.assets"
    target.write_bytes(b"official")
    build_dir = tmp_path / "build"
    build_dir.mkdir()
    generated = build_dir / "sharedassets999.assets"
    generated.write_bytes(b"custom")
    result = BuildResult(
        item_id="test",
        output_dir=build_dir,
        manifest_path=build_dir / "build-manifest.json",
        files=[
            BuildFileResult(
                asset=999,
                output_path=generated,
                source_path=target,
                sha256=sha256(generated),
                materials=["Test"],
                texture_path_ids={},
                emissive={},
            )
        ],
        warnings=[],
        affected_items=[],
    )
    backup_root = tmp_path / "backups"
    manager = InstallManager(backup_root, allow_system_drive=True)
    record = manager.install(
        GameInstall(game_root, data_dir, "test"),
        result,
        keep_backup=False,
    )
    assert target.read_bytes() == b"custom"
    assert record.retained is False
    assert not any(backup_root.iterdir())


def test_backup_folder_adds_suffix_for_same_day_same_item(tmp_path, monkeypatch):
    monkeypatch.setattr("gtfo_skin_forge.installer.is_gtfo_running", lambda: False)
    game_root = tmp_path / "GTFO"
    data_dir = game_root / "GTFO_Data"
    data_dir.mkdir(parents=True)
    target = data_dir / "sharedassets999.assets"
    target.write_bytes(b"official")
    build_dir = tmp_path / "build"
    build_dir.mkdir()
    generated = build_dir / "sharedassets999.assets"
    generated.write_bytes(b"custom")
    result = BuildResult(
        item_id="test",
        item_name="IronSights 10",
        output_dir=build_dir,
        manifest_path=build_dir / "build-manifest.json",
        files=[
            BuildFileResult(
                asset=999,
                output_path=generated,
                source_path=target,
                sha256=sha256(generated),
                materials=["Test"],
                texture_path_ids={},
                emissive={},
            )
        ],
        warnings=[],
        affected_items=[],
    )
    manager = InstallManager(tmp_path / "backups", allow_system_drive=True)

    first = manager.install(GameInstall(game_root, data_dir, "test"), result)
    second = manager.install(GameInstall(game_root, data_dir, "test"), result)

    assert first.backup_dir != second.backup_dir
    for record in (first, second):
        assert re.fullmatch(
            r"\d{4}-\d{2}-\d{2}-\d{6}-备份-IronSights 10(?:-\d+)?",
            Path(record.backup_dir).name,
        )
