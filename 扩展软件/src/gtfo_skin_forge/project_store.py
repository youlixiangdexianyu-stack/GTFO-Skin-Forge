from __future__ import annotations

import json
import os
from pathlib import Path

from .config import ensure_app_dirs
from .models import SkinProject


def save_project(project: SkinProject, path: Path) -> None:
    path = path.with_suffix(".gtfoskin") if path.suffix.casefold() != ".gtfoskin" else path
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(project.to_dict(), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    os.replace(temporary, path)


def load_project(path: Path) -> SkinProject:
    value = json.loads(path.read_text(encoding="utf-8"))
    return SkinProject.from_dict(value)


def autosave_path() -> Path:
    return ensure_app_dirs()["projects"] / "autosave.gtfoskin"


def autosave_project(project: SkinProject) -> Path:
    target = autosave_path()
    save_project(project, target)
    return target

