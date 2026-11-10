from __future__ import annotations

import json
import os
import shutil
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

from .assets import safe_output_name, sha256
from .config import ensure_app_dirs
from .game_locator import is_gtfo_running
from .models import BuildResult, GameInstall, InstallRecord


class InstallManager:
    def __init__(
        self,
        backup_root: Path | None = None,
        *,
        allow_system_drive: bool = True,
    ):
        self.backup_root = (backup_root or ensure_app_dirs()["backups"]).expanduser().resolve()
        self.backup_root.mkdir(parents=True, exist_ok=True)

    def install(
        self,
        game: GameInstall,
        result: BuildResult,
        *,
        persistent_backup: bool = True,
        keep_backup: bool = True,
    ) -> InstallRecord:
        if is_gtfo_running():
            raise RuntimeError("检测到 GTFO 正在运行，请先退出游戏")
        for output in result.files:
            if not output.output_path.is_file():
                raise FileNotFoundError(output.output_path)
            if output.output_path.name != f"sharedassets{output.asset}.assets":
                raise ValueError(f"构建文件名称不规范: {output.output_path.name}")
        self._ensure_targets_replaceable(game, result)

        stamp = datetime.now(timezone.utc)
        record_id = f"{stamp.strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:8]}"
        local_stamp = datetime.now().astimezone()
        item_name = safe_output_name(result.item_name or result.item_id)
        backup_prefix = (
            f"{local_stamp.strftime('%Y-%m-%d-%H%M%S')}-备份-{item_name}"
        )
        backup_dir = self.backup_root / backup_prefix
        suffix = 2
        while backup_dir.exists():
            backup_dir = self.backup_root / f"{backup_prefix}-{suffix}"
            suffix += 1
        backup_dir.mkdir(parents=True, exist_ok=False)
        files: list[dict[str, str]] = []
        installed: list[Path] = []
        try:
            for output in result.files:
                target = game.data_dir / output.output_path.name
                backup = backup_dir / target.name
                if target.is_file():
                    shutil.copy2(target, backup)
                    backup_sha = sha256(backup)
                    existed = True
                else:
                    backup_sha = ""
                    existed = False
                files.append(
                    {
                        "file": target.name,
                        "target": str(target),
                        "backup": str(backup) if existed else "",
                        "backup_sha256": backup_sha,
                        "installed_sha256": output.sha256,
                        "existed": "1" if existed else "0",
                    }
                )
                temporary = target.with_name(target.name + ".gtfoskinforge.tmp")
                shutil.copy2(output.output_path, temporary)
                self._replace_with_retry(temporary, target)
                if sha256(target) != output.sha256:
                    raise OSError(f"安装后校验失败: {target.name}")
                installed.append(target)
            manifest_path = backup_dir / "install-record.json"
            record = InstallRecord(
                id=record_id,
                created_at=stamp.isoformat(),
                game_root=str(game.root),
                backup_dir=str(backup_dir),
                files=files,
                manifest_path=str(manifest_path),
                retained=keep_backup,
            )
            manifest_path.write_text(
                json.dumps(record.to_dict(), ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            backup_manifest = {
                "schema_version": 1,
                "record_id": record_id,
                "created_at": stamp.isoformat(),
                "source_build_manifest": str(result.manifest_path),
                "persistent_backup": persistent_backup,
                "files": files,
            }
            (backup_dir / "backup-manifest.json").write_text(
                json.dumps(backup_manifest, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            if not keep_backup:
                shutil.rmtree(backup_dir, ignore_errors=True)
            return record
        except Exception:
            for item in reversed(files):
                target = Path(item["target"])
                backup = Path(item["backup"]) if item["backup"] else None
                try:
                    if backup and backup.is_file():
                        temporary = target.with_name(target.name + ".gtfoskinforge.restore.tmp")
                        shutil.copy2(backup, temporary)
                        os.replace(temporary, target)
                    elif item["existed"] == "0" and target.is_file():
                        target.unlink()
                except OSError:
                    pass
            raise

    def restore(self, record: InstallRecord) -> None:
        if is_gtfo_running():
            raise RuntimeError("检测到 GTFO 正在运行，请先退出游戏")
        for item in reversed(record.files):
            target = Path(item["target"])
            backup_raw = item.get("backup", "")
            backup = Path(backup_raw) if backup_raw else None
            if backup and backup.is_file():
                if item.get("backup_sha256") and sha256(backup) != item["backup_sha256"]:
                    raise ValueError(f"备份校验失败: {backup}")
                temporary = target.with_name(target.name + ".gtfoskinforge.restore.tmp")
                shutil.copy2(backup, temporary)
                self._replace_with_retry(temporary, target)
            elif item.get("existed") == "0" and target.is_file():
                target.unlink()

    def _ensure_targets_replaceable(self, game: GameInstall, result: BuildResult) -> None:
        blocked: list[str] = []
        for output in result.files:
            target = game.data_dir / output.output_path.name
            if not target.exists():
                continue
            if not target.is_file():
                raise RuntimeError(f"目标不是普通文件，无法替换: {target}")
            try:
                with target.open("r+b"):
                    pass
            except PermissionError:
                blocked.append(target.name)
            except OSError as exc:
                blocked.append(f"{target.name} ({exc})")
        if blocked:
            names = "、".join(blocked)
            raise RuntimeError(
                "以下目标 .assets 正被占用或没有写入权限，请关闭 GTFO、其他"
                f"枪皮工坊实例以及可能锁定游戏的压缩/模组工具后重试：{names}"
            )

    @staticmethod
    def _replace_with_retry(source: Path, target: Path) -> None:
        last_error: OSError | None = None
        for attempt in range(4):
            try:
                os.replace(source, target)
                return
            except PermissionError as exc:
                last_error = exc
                if attempt < 3:
                    time.sleep(0.2)
        raise RuntimeError(
            f"无法替换 {target.name}。文件可能被其他程序占用，"
            "请关闭 GTFO、Steam 库扫描工具、杀毒软件扫描窗口或旧版枪皮工坊后重试。"
        ) from last_error

    def list_records(self) -> list[InstallRecord]:
        records: list[InstallRecord] = []
        for manifest in self.backup_root.glob("*/install-record.json"):
            try:
                value = json.loads(manifest.read_text(encoding="utf-8"))
                records.append(InstallRecord.from_dict(value))
            except (OSError, ValueError, TypeError):
                continue
        records.sort(key=lambda value: value.created_at, reverse=True)
        return records

    def delete_old_records(self, keep: int = 20) -> None:
        for record in self.list_records()[keep:]:
            directory = Path(record.backup_dir)
            if directory.parent == self.backup_root and directory.is_dir():
                shutil.rmtree(directory, ignore_errors=True)


def test_directory_writable(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    probe = path / ".gtfoskinforge-write-test"
    probe.write_text("ok", encoding="utf-8")
    probe.unlink(missing_ok=True)


def migrate_backup_records(source_root: Path, destination_root: Path) -> int:
    source_root = source_root.resolve()
    destination_root = destination_root.resolve()
    if source_root == destination_root or not source_root.is_dir():
        return 0
    destination_root.mkdir(parents=True, exist_ok=True)
    moved = 0
    for source in source_root.iterdir():
        if not source.is_dir() or not (source / "install-record.json").is_file():
            continue
        destination = destination_root / source.name
        if destination.exists():
            continue
        shutil.move(str(source), str(destination))
        record_path = destination / "install-record.json"
        record_value = json.loads(record_path.read_text(encoding="utf-8"))
        record_value["backup_dir"] = str(destination)
        record_value["manifest_path"] = str(record_path)
        for item in record_value.get("files", []):
            backup = Path(item.get("backup", "")) if item.get("backup") else None
            if backup:
                item["backup"] = str(destination / backup.name)
        _write_json_atomic(record_path, record_value)
        backup_manifest = destination / "backup-manifest.json"
        if backup_manifest.is_file():
            manifest_value = json.loads(backup_manifest.read_text(encoding="utf-8"))
            for item in manifest_value.get("files", []):
                backup = Path(item.get("backup", "")) if item.get("backup") else None
                if backup:
                    item["backup"] = str(destination / backup.name)
            _write_json_atomic(backup_manifest, manifest_value)
        moved += 1
    return moved


def _write_json_atomic(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)
