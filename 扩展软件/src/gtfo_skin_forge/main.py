from __future__ import annotations

import multiprocessing
import os
import sys

os.environ["QT3D_RENDERER"] = "opengl"

from PySide6.QtCore import Qt
from PySide6.QtGui import QFont
from PySide6.QtWidgets import QApplication


def main() -> int:
    multiprocessing.freeze_support()
    if "--build-project" in sys.argv:
        from .build_cli import build_project_cli

        return build_project_cli(sys.argv)
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    app = QApplication(sys.argv)
    app.setApplicationName("GTFO 枪皮工坊")
    app.setOrganizationName("GTFOSkinForge")
    app.setStyle("Fusion")
    app.setFont(QFont("Microsoft YaHei UI", 10))
    from .ui import MainWindow

    window = MainWindow()
    window.show()
    if "--smoke-test" in sys.argv:
        app.processEvents()
        window.close()
        return 0
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
