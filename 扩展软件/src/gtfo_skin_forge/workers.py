from __future__ import annotations

from collections.abc import Callable
import traceback
from typing import Any

from PySide6.QtCore import QThread, Signal


class TaskWorker(QThread):
    succeeded = Signal(object)
    failed = Signal(str)
    message = Signal(str)

    def __init__(self, task: Callable[[Callable[[str], None]], Any], parent=None):
        super().__init__(parent)
        self._task = task

    def run(self) -> None:
        try:
            result = self._task(self.message.emit)
        except Exception as exc:
            details = traceback.format_exc()
            self.failed.emit(f"{type(exc).__name__}: {exc}\n\n{details}")
            return
        self.succeeded.emit(result)
