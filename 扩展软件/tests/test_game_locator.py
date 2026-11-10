from __future__ import annotations

from types import SimpleNamespace

from gtfo_skin_forge import game_locator


def test_running_check_handles_non_utf8_tasklist_output(monkeypatch):
    monkeypatch.setattr(
        game_locator.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(stdout=b"\xd0\xd1 GTFO.exe"),
    )
    assert game_locator.is_gtfo_running() is True

