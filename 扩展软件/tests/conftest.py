from __future__ import annotations

import os
from pathlib import Path

import pytest


@pytest.fixture()
def forge_data(tmp_path, monkeypatch):
    monkeypatch.setenv("GTFO_SKIN_FORGE_DATA", str(tmp_path / "app-data"))
    return tmp_path / "app-data"


@pytest.fixture(scope="session")
def reference_root() -> Path | None:
    workspace = Path(__file__).resolve().parents[2]
    for candidate in workspace.glob("gtfo-forge*"):
        if candidate.is_dir() and next(candidate.glob("**/hle*"), None) is not None:
            return candidate
    return None
