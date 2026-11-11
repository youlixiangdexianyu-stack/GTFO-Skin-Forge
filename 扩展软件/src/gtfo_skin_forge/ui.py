from __future__ import annotations

import copy
import os
import shutil
import sys
import tempfile
from datetime import datetime
from pathlib import Path

from PIL import Image
from PySide6.QtCore import QByteArray, QSize, Qt, QTimer, Signal
from PySide6.QtGui import (
    QBrush,
    QCloseEvent,
    QColor,
    QImage,
    QKeySequence,
    QPainter,
    QPalette,
    QPixmap,
)
from PySide6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QComboBox,
    QDoubleSpinBox,
    QDialog,
    QFileDialog,
    QFrame,
    QGraphicsPixmapItem,
    QGraphicsScene,
    QGraphicsView,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QListWidget,
    QMainWindow,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QStyle,
    QStyledItemDelegate,
    QStyleOptionViewItem,
    QSplitter,
    QStackedWidget,
    QTabBar,
    QTabWidget,
    QTableWidget,
    QTableWidgetItem,
    QTreeWidget,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)

from .assets import (
    BuildContext,
    affected_by_asset,
    build_assets,
    inspect_material,
    safe_output_name,
)
from .catalog import (
    ATTACHMENT_CATEGORY_ORDER,
    item_display_name,
    load_catalog,
    official_weapon_meta,
    weapon_display_label,
    weapon_sections,
)
from .config import (
    AppSettings,
    app_data_dir,
    ensure_app_dirs,
    load_settings,
    save_settings,
)
from .dly_workspace import DlyWorkspace
from .dynamic_workspace import DynamicVisualWorkspace
from .dynamic_visuals import load_dynamic_project
from .game_locator import best_game_install, is_gtfo_running, validate_game_root
from .installer import (
    InstallManager,
    migrate_backup_records,
    test_directory_writable,
)
from .matching import analyze_texture_folder, manual_assign, refresh_missing
from .obj_export import (
    ObjExportError,
    ObjExportMode,
    ObjExportRequest,
    export_obj,
)
from .models import (
    CHANNELS,
    CHANNEL_LABELS_ZH,
    CatalogItem,
    EmissiveSettings,
    GameInstall,
    MatchAnalysis,
    SkinProject,
    SlotMatch,
)
from .project_store import autosave_project, load_project, save_project
from .workers import TaskWorker


CHECKMARK_PATH = (
    Path(__file__).with_name("data") / "checkmark.svg"
).resolve().as_posix()
SPIN_UP_PATH = (
    Path(__file__).with_name("data") / "spin_up.svg"
).resolve().as_posix()
SPIN_DOWN_PATH = (
    Path(__file__).with_name("data") / "spin_down.svg"
).resolve().as_posix()

APP_STYLE = """
QMainWindow, QWidget {
    background: #111417;
    color: #e3e7e5;
}
QFrame#Header {
    background: #191f22;
    border-bottom: 1px solid #364146;
}
QLabel#Title {
    color: #d8ff67;
    font-size: 22px;
    font-weight: 700;
    letter-spacing: 1px;
}
QLabel#Subtitle {
    color: #7f9096;
}
QLineEdit, QComboBox, QDoubleSpinBox, QPlainTextEdit, QListWidget {
    background: #171c1f;
    border: 1px solid #354146;
    border-radius: 4px;
    padding: 6px;
    selection-background-color: #829f31;
}
QListWidget#DlyPartsList::item {
    background: #151a1d;
    color: #d6dfdc;
    padding: 4px 7px;
}
QListWidget#DlyPartsList::item:alternate {
    background: #191f22;
}
QListWidget#DlyPartsList::item:selected {
    background: #174d56;
    color: #f1ffff;
}
QTableWidget#DlyPartsTable::item {
    padding: 4px 6px;
}
QTableWidget#DlyPartsTable QToolButton {
    margin: 7px 5px;
}
QLineEdit:focus, QComboBox:focus, QDoubleSpinBox:focus {
    border: 1px solid #b5db4b;
}
QCheckBox {
    color: #c4cfcc;
    spacing: 6px;
}
QCheckBox::indicator {
    width: 15px;
    height: 15px;
    border: 1px solid #9caaad;
    border-radius: 2px;
    background: #e2e7e5;
}
QCheckBox::indicator:hover {
    border-color: #bdcbc7;
    background: #eef2f0;
}
QCheckBox::indicator:checked {
    border-color: #c2d9a0;
    background: #a7c45e;
    image: url("__CHECKMARK_PATH__");
}
QCheckBox::indicator:disabled {
    border-color: #465257;
    background: #2a3337;
}
QTreeWidget, QTableWidget {
    background: #151a1d;
    alternate-background-color: #191f22;
    border: 1px solid #303a3f;
    gridline-color: #293136;
    outline: none;
}
QTreeWidget::item {
    padding: 7px 4px;
}
QTreeWidget::item:selected, QTableWidget::item:selected {
    background: #4d6127;
    color: #ffffff;
}
QHeaderView::section {
    background: #20272b;
    color: #aab8bc;
    border: none;
    border-right: 1px solid #303a3f;
    padding: 7px;
    font-weight: 600;
}
QTabWidget::pane {
    border: 1px solid #303a3f;
    background: #111417;
}
QTabBar::tab {
    background: #20272b;
    color: #aab8bc;
    border: 1px solid #303a3f;
    border-bottom: none;
    padding: 5px 12px;
    margin-right: 2px;
}
QTabBar::tab:hover {
    background: #2b353a;
    color: #e3e7e5;
}
QTabBar::tab:selected {
    background: #4d6127;
    color: #ffffff;
    border: 1px solid #303a3f;
    border-bottom: none;
    font-weight: 700;
}
QTabBar#WorkspaceTabs::tab {
    background: #20272b;
    color: #8fa0a5;
    border: 1px solid #3a474c;
    border-radius: 4px;
    padding: 8px 18px;
    margin-right: 4px;
    font-size: 11px;
    font-weight: 700;
}
QTabBar#WorkspaceTabs::tab:hover {
    background: #2d373c;
    color: #e9efec;
}
QTabBar#WorkspaceTabs::tab:selected {
    background: #70e7f3;
    color: #071012;
    border-color: #a4f8ff;
}
QPushButton {
    background: #293136;
    border: 1px solid #46565d;
    border-radius: 4px;
    padding: 8px 14px;
}
QPushButton:hover {
    background: #354149;
    border-color: #748950;
}
QPushButton:pressed {
    background: #20272b;
}
QPushButton:disabled {
    background: #181d20;
    color: #566166;
    border-color: #293136;
}
QPushButton#Primary {
    background: #9dbe3e;
    color: #10130f;
    border: none;
    font-weight: 700;
}
QPushButton#Primary:hover {
    background: #b7dc55;
}
QPushButton#Danger {
    border-color: #8b4545;
    color: #ffb4b4;
}
QPushButton#PathButton {
    background: transparent;
    border: none;
    color: #9cadb2;
    padding: 2px 6px;
    text-align: left;
}
QPushButton#PathButton:hover {
    color: #d8ff67;
    text-decoration: underline;
}
QDoubleSpinBox::up-button,
QDoubleSpinBox::down-button {
    width: 20px;
    background: #20272b;
    border-left: 1px solid #46565d;
}
QDoubleSpinBox::up-button:hover,
QDoubleSpinBox::down-button:hover {
    background: #354149;
}
QDoubleSpinBox::up-arrow {
    image: url("__SPIN_UP__");
    width: 10px;
    height: 7px;
}
QDoubleSpinBox::down-arrow {
    image: url("__SPIN_DOWN__");
    width: 10px;
    height: 7px;
}
QGroupBox {
    border: 1px solid #303a3f;
    border-radius: 5px;
    margin-top: 10px;
    padding-top: 10px;
    font-weight: 600;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 10px;
    padding: 0 5px;
    color: #b7c4c8;
}
QProgressBar {
    border: 1px solid #354146;
    border-radius: 3px;
    background: #171c1f;
    text-align: center;
}
QProgressBar::chunk {
    background: #a6c948;
}
QSplitter::handle {
    background: #222a2e;
    width: 2px;
}
QScrollBar:vertical {
    background: #151a1d;
    width: 12px;
}
QScrollBar::handle:vertical {
    background: #3b474c;
    min-height: 30px;
    border-radius: 4px;
}
"""

APP_STYLE = (
    APP_STYLE.replace("__SPIN_UP__", SPIN_UP_PATH)
    .replace("__SPIN_DOWN__", SPIN_DOWN_PATH)
)
APP_STYLE = APP_STYLE.replace("__CHECKMARK_PATH__", CHECKMARK_PATH)


class PreviewTile(QFrame):
    activated = Signal(object)

    def __init__(self, title: str, parent=None):
        super().__init__(parent)
        self.setFrameShape(QFrame.Shape.StyledPanel)
        self.setStyleSheet("QFrame { border: 1px solid #303a3f; background: #0d1012; }")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(6, 6, 6, 6)
        label = QLabel(title)
        label.setStyleSheet("color: #9fb0b5; font-weight: 600; border: none;")
        label.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.image = QLabel("未分配")
        self.image.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.image.setMinimumSize(110, 90)
        self.image.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Ignored,
        )
        self.image.setStyleSheet("color: #59666a; border: none;")
        self.image.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.path: Path | None = None
        layout.addWidget(label)
        layout.addWidget(self.image, 1)

    def set_file(self, path: Path | None) -> None:
        self.path = path if path and path.is_file() else None
        if path is None or not path.is_file():
            self.image.setPixmap(QPixmap())
            self.image.setText("未分配")
            self.image.setToolTip("")
            return
        pixmap = _read_pixmap(path)
        if pixmap.isNull():
            self.image.setPixmap(QPixmap())
            self.image.setText("无法预览")
        else:
            self.image.setPixmap(
                pixmap.scaled(
                    self.image.size(),
                    Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation,
                )
            )
            self.image.setText("")
        self.image.setToolTip(str(path))

    def mouseReleaseEvent(self, event) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.path is not None:
            self.activated.emit(self.path)
        super().mouseReleaseEvent(event)


class ZoomableImageView(QGraphicsView):
    zoom_changed = Signal(float)

    def __init__(self, pixmap: QPixmap, parent=None):
        super().__init__(parent)
        self.auto_fit = True
        self.scene = QGraphicsScene(self)
        self.item = QGraphicsPixmapItem(pixmap)
        self.scene.addItem(self.item)
        self.setScene(self.scene)
        self.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        self.setDragMode(QGraphicsView.DragMode.ScrollHandDrag)
        self.setTransformationAnchor(QGraphicsView.ViewportAnchor.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.ViewportAnchor.AnchorViewCenter)
        self.setBackgroundBrush(QColor("#0b0e10"))

    def fit_image(self) -> None:
        self.auto_fit = True
        self.fitInView(self.item, Qt.AspectRatioMode.KeepAspectRatio)
        self._emit_zoom()

    def actual_size(self) -> None:
        self.auto_fit = False
        self.resetTransform()
        self.centerOn(self.item)
        self._emit_zoom()

    def zoom_by(self, factor: float) -> None:
        current = self.transform().m11()
        target = current * factor
        if target < 0.03 or target > 40.0:
            return
        self.auto_fit = False
        self.scale(factor, factor)
        self._emit_zoom()

    def _emit_zoom(self) -> None:
        self.zoom_changed.emit(self.transform().m11() * 100.0)

    def wheelEvent(self, event) -> None:
        self.zoom_by(1.18 if event.angleDelta().y() > 0 else 1.0 / 1.18)
        event.accept()

    def resizeEvent(self, event) -> None:
        super().resizeEvent(event)
        if self.auto_fit:
            self.fitInView(self.item, Qt.AspectRatioMode.KeepAspectRatio)
            self._emit_zoom()


class ImageViewerDialog(QDialog):
    def __init__(self, path: Path, parent=None):
        super().__init__(parent)
        self.setWindowTitle(path.name)
        self.resize(1000, 760)
        pixmap = _read_pixmap(path)
        layout = QVBoxLayout(self)
        info = QLabel(
            f"{path.name}   {pixmap.width()}×{pixmap.height()}   "
            "滚轮缩放 · 按住左键拖动"
        )
        info.setStyleSheet("color: #9cadb2;")
        layout.addWidget(info)
        self.image_view = ZoomableImageView(pixmap, self)
        layout.addWidget(self.image_view, 1)
        controls = QHBoxLayout()
        fit_button = QPushButton("适应窗口")
        fit_button.clicked.connect(self.image_view.fit_image)
        actual_button = QPushButton("100%")
        actual_button.clicked.connect(self.image_view.actual_size)
        self.zoom_label = QLabel("100%")
        self.zoom_label.setMinimumWidth(70)
        close_button = QPushButton("关闭")
        close_button.clicked.connect(self.accept)
        controls.addWidget(fit_button)
        controls.addWidget(actual_button)
        controls.addWidget(self.zoom_label)
        controls.addStretch(1)
        controls.addWidget(close_button)
        layout.addLayout(controls)
        self.image_view.zoom_changed.connect(
            lambda percent: self.zoom_label.setText(f"{percent:.0f}%")
        )
        QTimer.singleShot(0, self.image_view.fit_image)


class CompactTableWidget(QTableWidget):
    def sizeHint(self) -> QSize:
        return QSize(620, 360)

    def minimumSizeHint(self) -> QSize:
        return QSize(260, 180)

    def sizeHintForColumn(self, column: int) -> int:
        return min(super().sizeHintForColumn(column), 240)


class CompactTreeWidget(QTreeWidget):
    def sizeHint(self) -> QSize:
        return QSize(250, 600)

    def minimumSizeHint(self) -> QSize:
        return QSize(150, 180)

    def sizeHintForColumn(self, column: int) -> int:
        return min(super().sizeHintForColumn(column), 230)


class ChannelHighlightDelegate(QStyledItemDelegate):
    def __init__(self, table: QTableWidget):
        super().__init__(table)
        self.table = table

    def paint(self, painter, option, index) -> None:
        paint_option = QStyleOptionViewItem(option)
        self.initStyleOption(paint_option, index)
        is_current_channel = (
            index.row() == self.table.currentRow()
            and index.column() == self.table.currentColumn()
            and 3 <= index.column() <= 6
        )
        if is_current_channel:
            paint_option.state &= ~(
                QStyle.StateFlag.State_Selected
                | QStyle.StateFlag.State_HasFocus
            )
            paint_option.backgroundBrush = QBrush(QColor("#d8ff67"))
            paint_option.palette.setColor(
                QPalette.ColorRole.Text,
                QColor("#10130f"),
            )
        super().paint(painter, paint_option, index)
        if is_current_channel:
            highlight = QColor("#d8ff67")
            highlight.setAlpha(80)
            painter.fillRect(option.rect.adjusted(1, 1, -1, -1), highlight)


def _read_pixmap(path: Path) -> QPixmap:
    try:
        with Image.open(path) as image:
            rgba = image.convert("RGBA")
            data = rgba.tobytes("raw", "RGBA")
            qimage = QImage(
                data,
                rgba.width,
                rgba.height,
                rgba.width * 4,
                QImage.Format.Format_RGBA8888,
            ).copy()
        return QPixmap.fromImage(qimage)
    except Exception:
        return QPixmap()


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("GTFO 枪皮工坊 v0.2.1")
        self.resize(1480, 920)
        self.setMinimumSize(1120, 720)
        self.setStyleSheet(APP_STYLE)

        ensure_app_dirs()
        self.settings: AppSettings = load_settings()
        self._layout_save_timer = QTimer(self)
        self._layout_save_timer.setSingleShot(True)
        self._layout_save_timer.setInterval(350)
        self._layout_save_timer.timeout.connect(self._save_layout_settings)
        self.catalog = load_catalog()
        self.install_manager: InstallManager | None = None
        self.current_game: GameInstall | None = None
        self.current_item: CatalogItem | None = None
        self.current_analysis: MatchAnalysis | None = None
        self.current_project: SkinProject | None = None
        self.current_part_id: str | None = None
        self.selected_channel: str | None = None
        self._optional_enabled: set[str] = set()
        self._disabled_parts: set[str] = set()
        self._undo_stack: list[dict[str, object]] = []
        self._undo_restoring = False
        self._loading_table = False
        self.worker: TaskWorker | None = None
        self.three_d_view = None
        self.three_d_dirty = True
        self.dly_workspace: DlyWorkspace | None = None
        self.dynamic_workspace: DynamicVisualWorkspace | None = None
        self.skin_workspace: QWidget | None = None
        self._workspace_switching = False
        self.log_path = ensure_app_dirs()["logs"] / "app.log"
        self._loading_tree = False

        self._build_ui()
        self._restore_window_layout()
        self.main_splitter.splitterMoved.connect(self._schedule_layout_save)
        self._populate_tree()
        self._detect_game(self.settings.game_root)
        self._refresh_install_manager()
        self._restore_last_item()
        self._update_actions()
        QTimer.singleShot(0, self._restore_active_workspace)

    def _restore_window_layout(self) -> None:
        if self.settings.window_geometry:
            geometry = QByteArray.fromBase64(
                self.settings.window_geometry.encode("ascii")
            )
            self.restoreGeometry(geometry)
        sizes = list(self.settings.splitter_sizes)
        if len(sizes) == self.main_splitter.count() and all(
            value > 0 for value in sizes
        ):
            self.main_splitter.setSizes(sizes)

    def _schedule_layout_save(self, *_) -> None:
        if hasattr(self, "_layout_save_timer"):
            self._layout_save_timer.start()

    def _save_layout_settings(self) -> None:
        if not hasattr(self, "main_splitter"):
            return
        self.settings.window_geometry = bytes(
            self.saveGeometry().toBase64()
        ).decode("ascii")
        self.settings.splitter_sizes = self.main_splitter.sizes()
        if self.dly_workspace is not None:
            self.settings.dly_splitter_sizes = (
                self.dly_workspace.save_splitter_sizes()
            )
            self.settings.dly_last_part_id = (
                self.dly_workspace.preset.anchor_part_id
            )
            self.settings.dly_profile_root = (
                str(self.dly_workspace.profile_root)
                if self.dly_workspace.profile_root
                else ""
            )
        if self.dynamic_workspace is not None:
            self.settings.dynamic_splitter_sizes = (
                self.dynamic_workspace.save_splitter_sizes()
            )
            self.settings.dynamic_project_path = str(
                self.dynamic_workspace.project_path
            )
            self.settings.dynamic_profile_root = (
                str(self.dynamic_workspace.profile_root)
                if self.dynamic_workspace.profile_root
                else ""
            )
            self.settings.dynamic_last_part_id = (
                self.dynamic_workspace.project.selected_part_id
            )
            self.settings.dynamic_last_channel = (
                self.dynamic_workspace.project.selected_channel
            )
        index = (
            self.workspace_stack.currentIndex()
            if hasattr(self, "workspace_stack")
            else 0
        )
        self.settings.active_workspace = {
            1: "dly",
            2: "dynamic",
        }.get(index, "skin")
        save_settings(self.settings)

    def _restore_active_workspace(self) -> None:
        self._set_workspace(
            (
                self.settings.active_workspace
                if self.settings.active_workspace in {"skin", "dly", "dynamic"}
                else "skin"
            ),
            persist=False,
        )

    def _workspace_tab_changed(self, index: int) -> None:
        if self._workspace_switching:
            return
        self._set_workspace({0: "skin", 1: "dly", 2: "dynamic"}.get(index, "skin"))

    def _set_workspace(self, workspace: str, *, persist: bool = True) -> None:
        if not hasattr(self, "workspace_stack"):
            return
        self._workspace_switching = True
        try:
            if workspace == "dly":
                dly_workspace = self._ensure_dly_workspace()
                self.workspace_stack.setCurrentIndex(1)
                self.workspace_tabs.setCurrentIndex(1)
                dly_workspace.set_game(self.current_game)
                dly_workspace.activate()
                if hasattr(self, "undo_action"):
                    self.undo_action.setEnabled(False)
            elif workspace == "dynamic":
                dynamic_workspace = self._ensure_dynamic_workspace()
                self.workspace_stack.setCurrentIndex(2)
                self.workspace_tabs.setCurrentIndex(2)
                dynamic_workspace.set_game(self.current_game)
                dynamic_workspace.activate()
                if hasattr(self, "undo_action"):
                    self.undo_action.setEnabled(False)
            else:
                self.workspace_stack.setCurrentIndex(0)
                self.workspace_tabs.setCurrentIndex(0)
                if hasattr(self, "undo_action"):
                    self.undo_action.setEnabled(bool(self._undo_stack))
            if hasattr(self, "skin_only_actions"):
                for action in self.skin_only_actions:
                    action.setEnabled(workspace == "skin")
            self.settings.active_workspace = workspace
            if persist:
                save_settings(self.settings)
        finally:
            self._workspace_switching = False

    def _ensure_dynamic_workspace(self) -> DynamicVisualWorkspace:
        if self.dynamic_workspace is not None:
            return self.dynamic_workspace
        app_root = (
            Path(sys.executable).resolve().parent
            if getattr(sys, "frozen", False)
            else Path(__file__).resolve().parents[2]
        )
        workspace = DynamicVisualWorkspace(
            app_root=app_root,
            game=self.current_game,
        )
        if self.settings.dynamic_project_path:
            project_path = Path(self.settings.dynamic_project_path)
            if project_path.is_file():
                try:
                    workspace.project_path = project_path
                    workspace.project = load_dynamic_project(project_path)
                except (OSError, ValueError):
                    pass
        if self.settings.dynamic_profile_root:
            profile_root = Path(self.settings.dynamic_profile_root)
            if profile_root.is_dir():
                workspace.profile_root = profile_root
                workspace.profile_label.setText(str(profile_root))
        workspace.project.selected_part_id = (
            self.settings.dynamic_last_part_id or "front"
        )
        workspace.project.selected_channel = (
            self.settings.dynamic_last_channel or "Albedo"
        )
        workspace._populate_parts()
        workspace._refresh_layer_table()
        workspace._sync_controls()
        workspace.restore_splitter_sizes(
            list(self.settings.dynamic_splitter_sizes)
        )
        workspace.status_message.connect(self._dynamic_status_changed)
        self.workspace_stack.removeWidget(self.dynamic_workspace_placeholder)
        self.workspace_stack.insertWidget(2, workspace)
        self.dynamic_workspace = workspace
        return workspace

    def _dynamic_status_changed(self, text: str) -> None:
        if self.dynamic_workspace is None:
            return
        self.settings.dynamic_project_path = str(
            self.dynamic_workspace.project_path
        )
        self.settings.dynamic_profile_root = (
            str(self.dynamic_workspace.profile_root)
            if self.dynamic_workspace.profile_root
            else ""
        )

    def _ensure_dly_workspace(self) -> DlyWorkspace:
        if self.dly_workspace is not None:
            return self.dly_workspace
        app_root = (
            Path(sys.executable).resolve().parent
            if getattr(sys, "frozen", False)
            else Path(__file__).resolve().parents[2]
        )
        selected_profile = (
            Path(self.settings.dly_profile_root)
            if self.settings.dly_profile_root
            else None
        )
        workspace = DlyWorkspace(
            app_root=app_root,
            game=self.current_game,
        )
        if selected_profile and selected_profile.is_dir():
            workspace.profile_root = selected_profile
            workspace._refresh_profile_status()
        workspace.preset.select_part(
            self.settings.dly_last_part_id or "receiver",
            reset_offsets=False,
        )
        workspace._sync_controls_from_preset()
        workspace.restore_splitter_sizes(
            list(self.settings.dly_splitter_sizes)
        )
        workspace.status_message.connect(self._dly_status_changed)
        self.workspace_stack.removeWidget(self.dly_workspace_placeholder)
        self.workspace_stack.insertWidget(1, workspace)
        self.dly_workspace = workspace
        return workspace

    def _dly_status_changed(self, text: str) -> None:
        if self.dly_workspace is not None:
            self.settings.dly_last_part_id = (
                self.dly_workspace.preset.anchor_part_id
            )
            self.settings.dly_profile_root = (
                str(self.dly_workspace.profile_root)
                if self.dly_workspace.profile_root
                else ""
            )

    def moveEvent(self, event) -> None:
        super().moveEvent(event)
        self._schedule_layout_save()

    def resizeEvent(self, event) -> None:
        super().resizeEvent(event)
        self._schedule_layout_save()

    def closeEvent(self, event: QCloseEvent) -> None:
        self._save_layout_settings()
        super().closeEvent(event)

    def _clear_undo_history(self) -> None:
        self._undo_stack.clear()
        if hasattr(self, "undo_action"):
            self.undo_action.setEnabled(False)

    def _push_undo_snapshot(self) -> None:
        if self._undo_restoring or not self.current_item:
            return
        snapshot = {
            "project": copy.deepcopy(self.current_project),
            "analysis": copy.deepcopy(self.current_analysis),
            "optional_parts": set(self._optional_enabled),
            "disabled_parts": set(self._disabled_parts),
            "part_id": self.current_part_id,
            "channel": self.selected_channel,
            "project_name": self.project_name.text(),
            "resolution_mode": self.resolution_combo.currentData(),
            "partial": self.partial_check.isChecked(),
            "low_confidence": self.low_confidence_check.isChecked(),
        }
        self._undo_stack.append(snapshot)
        if len(self._undo_stack) > 50:
            del self._undo_stack[0]
        self.undo_action.setEnabled(True)

    def _undo_last_action(self) -> None:
        if not self._undo_stack or not self.current_item:
            return
        snapshot = self._undo_stack.pop()
        self._undo_restoring = True
        try:
            self.current_project = copy.deepcopy(snapshot["project"])
            self.current_analysis = copy.deepcopy(snapshot["analysis"])
            self._optional_enabled = set(snapshot["optional_parts"])
            self._disabled_parts = set(snapshot["disabled_parts"])
            self.current_part_id = snapshot["part_id"]
            self.selected_channel = snapshot["channel"]
            self.project_name.setText(str(snapshot["project_name"]))
            index = self.resolution_combo.findData(snapshot["resolution_mode"])
            if index >= 0:
                self.resolution_combo.setCurrentIndex(index)
            self.partial_check.setChecked(bool(snapshot["partial"]))
            self.low_confidence_check.setChecked(bool(snapshot["low_confidence"]))
            if self.current_analysis:
                self._render_analysis_table(
                    self.current_part_id,
                    self.selected_channel,
                )
                self._render_warnings()
            else:
                self._populate_empty_table()
            self._refresh_part_preview()
            self._load_emissive_controls()
            if self.current_project:
                self._save_autosave()
            self._mark_3d_dirty()
            if self.preview_tabs.currentIndex() == 1:
                self._refresh_3d_preview()
            self._update_actions()
        finally:
            self._undo_restoring = False
            self.undo_action.setEnabled(bool(self._undo_stack))

    def _build_ui(self) -> None:
        root = QWidget()
        self.setCentralWidget(root)
        outer = QVBoxLayout(root)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        header = QFrame()
        header.setObjectName("Header")
        header_layout = QHBoxLayout(header)
        header_layout.setContentsMargins(18, 12, 18, 12)
        title_box = QVBoxLayout()
        title = QLabel("GTFO 枪皮工坊")
        title.setObjectName("Title")
        subtitle = QLabel("WORKSPACE TOOLS // v0.2.1")
        subtitle.setObjectName("Subtitle")
        title_box.addWidget(title)
        title_box.addWidget(subtitle)
        header_layout.addLayout(title_box)
        self.workspace_tabs = QTabBar()
        self.workspace_tabs.setObjectName("WorkspaceTabs")
        self.workspace_tabs.setDrawBase(False)
        self.workspace_tabs.setExpanding(False)
        self.workspace_tabs.addTab("枪皮工坊")
        self.workspace_tabs.addTab("DLY 弹夹显示")
        self.workspace_tabs.addTab("动态视觉")
        self.workspace_tabs.currentChanged.connect(
            self._workspace_tab_changed
        )
        header_layout.addSpacing(30)
        header_layout.addWidget(self.workspace_tabs)
        header_layout.addStretch(1)
        self.game_status = QLabel("正在检测 GTFO…")
        self.game_status.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.game_status.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        header_layout.addWidget(self.game_status, 1)
        detect_button = QPushButton("重新检测")
        detect_button.clicked.connect(lambda: self._detect_game(""))
        browse_game = QPushButton("选择目录")
        browse_game.clicked.connect(self._browse_game)
        header_layout.addWidget(detect_button)
        header_layout.addWidget(browse_game)
        outer.addWidget(header)

        self.workspace_stack = QStackedWidget()
        outer.addWidget(self.workspace_stack, 1)
        self.skin_workspace = QWidget()
        skin_layout = QVBoxLayout(self.skin_workspace)
        skin_layout.setContentsMargins(0, 0, 0, 0)
        skin_layout.setSpacing(0)
        self.workspace_stack.addWidget(self.skin_workspace)
        self.dly_workspace_placeholder = QWidget()
        self.dynamic_workspace_placeholder = QWidget()
        self.workspace_stack.addWidget(self.dly_workspace_placeholder)
        self.workspace_stack.addWidget(self.dynamic_workspace_placeholder)

        self.main_splitter = QSplitter(Qt.Orientation.Horizontal)
        self.main_splitter.setChildrenCollapsible(False)
        self.main_splitter.setStretchFactor(0, 0)
        self.main_splitter.setStretchFactor(1, 1)
        self.main_splitter.setStretchFactor(2, 0)
        skin_layout.addWidget(self.main_splitter, 1)

        self._build_catalog_panel()
        self._build_mapping_panel()
        self._build_preview_panel()
        self.main_splitter.setSizes([340, 650, 420])

        bottom = QFrame()
        bottom.setStyleSheet("QFrame { border-top: 1px solid #303a3f; }")
        bottom_layout = QHBoxLayout(bottom)
        bottom_layout.setContentsMargins(14, 8, 14, 10)
        self.progress = QProgressBar()
        self.progress.setRange(0, 1)
        self.progress.setValue(0)
        self.progress.setTextVisible(False)
        self.progress.setFixedWidth(220)
        bottom_layout.addWidget(self.progress)
        self.backup_path_label = QPushButton()
        self.backup_path_label.setObjectName("PathButton")
        self.backup_path_label.setCursor(Qt.CursorShape.PointingHandCursor)
        self.backup_path_label.setMinimumWidth(250)
        self.backup_path_label.setMaximumWidth(520)
        self.backup_path_label.setSizePolicy(
            QSizePolicy.Policy.Preferred,
            QSizePolicy.Policy.Preferred,
        )
        self.backup_path_label.clicked.connect(
            lambda: self.ensure_backup_directory_interactive(force=True)
        )
        bottom_layout.addWidget(self.backup_path_label)
        self.status_label = QLabel("就绪")
        self.status_label.setStyleSheet("color: #87969b;")
        bottom_layout.addWidget(self.status_label, 1)
        self.rollback_button = QPushButton("还原上次安装")
        self.rollback_button.setObjectName("Danger")
        self.rollback_button.clicked.connect(self._rollback_last)
        self.export_obj_button = QPushButton("导出 OBJ")
        self.export_obj_button.clicked.connect(self._export_obj)
        self.export_button = QPushButton("导出 .assets")
        self.export_button.clicked.connect(self._export)
        self.install_button = QPushButton("安装到游戏")
        self.install_button.setObjectName("Primary")
        self.install_button.clicked.connect(self._install)
        bottom_layout.addWidget(self.rollback_button)
        bottom_layout.addWidget(self.export_obj_button)
        bottom_layout.addWidget(self.export_button)
        bottom_layout.addWidget(self.install_button)
        skin_layout.addWidget(bottom)

        menu = self.menuBar()
        file_menu = menu.addMenu("文件")
        open_action = file_menu.addAction("打开工程")
        open_action.triggered.connect(self._open_project)
        save_action = file_menu.addAction("保存工程")
        save_action.triggered.connect(lambda: self._save_project(False))
        save_as_action = file_menu.addAction("工程另存为")
        save_as_action.triggered.connect(lambda: self._save_project(True))
        self.skin_only_actions = [
            open_action,
            save_action,
            save_as_action,
        ]
        file_menu.addSeparator()
        quit_action = file_menu.addAction("退出")
        quit_action.triggered.connect(self.close)
        tools_menu = menu.addMenu("工具")
        self.persistent_backup_action = tools_menu.addAction("自动保存源 .assets 基准")
        self.persistent_backup_action.setCheckable(True)
        self.persistent_backup_action.setChecked(self.settings.persistent_backup)
        self.persistent_backup_action.toggled.connect(self._toggle_persistent_backup)
        open_data = tools_menu.addAction("打开应用数据目录")
        open_data.triggered.connect(lambda: os.startfile(app_data_dir()))
        clean_baseline = tools_menu.addAction("指定干净 .assets 目录")
        clean_baseline.triggered.connect(self._choose_clean_baseline)
        backup_dir_action = tools_menu.addAction("选择备份目录")
        backup_dir_action.triggered.connect(
            lambda: self.ensure_backup_directory_interactive(force=True)
        )
        last_install = tools_menu.addAction("还原最近一次安装")
        last_install.triggered.connect(self._rollback_last)
        self.undo_action = menu.addAction("撤回")
        self.undo_action.setShortcut(QKeySequence.StandardKey.Undo)
        self.undo_action.setToolTip("撤回上一个映射或参数编辑操作（Ctrl+Z）")
        self.undo_action.setEnabled(False)
        self.undo_action.triggered.connect(self._undo_last_action)

    def _build_catalog_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(220)
        panel.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Expanding)
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(12, 12, 6, 12)
        heading = QLabel("武器目录")
        heading.setStyleSheet("font-size: 15px; font-weight: 700; color: #cbd5d1;")
        layout.addWidget(heading)
        self.search_box = QLineEdit()
        self.search_box.setPlaceholderText("搜索名称、英文别名或内部名称")
        self.search_box.textChanged.connect(self._filter_tree)
        layout.addWidget(self.search_box)
        self.catalog_tree = CompactTreeWidget()
        self.catalog_tree.setHeaderHidden(True)
        self.catalog_tree.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self.catalog_tree.setTextElideMode(Qt.TextElideMode.ElideRight)
        self.catalog_tree.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.catalog_tree.itemSelectionChanged.connect(self._tree_selection_changed)
        layout.addWidget(self.catalog_tree, 1)
        hint = QLabel("双击映射表任意贴图格可手动替换。\n枪械缺件时可继续部分构建。")
        hint.setWordWrap(True)
        hint.setStyleSheet("color: #6f7f84; font-size: 9pt;")
        layout.addWidget(hint)
        self.main_splitter.addWidget(panel)

    def _build_mapping_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(260)
        panel.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Expanding)
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(6, 12, 6, 12)

        title_row = QHBoxLayout()
        title_box = QVBoxLayout()
        self.item_title = QLabel("选择一把武器")
        self.item_title.setStyleSheet("font-size: 20px; font-weight: 700; color: #d8ff67;")
        self.item_description = QLabel("从左侧目录选择后，导入整枪贴图目录。")
        self.item_description.setWordWrap(True)
        self.item_description.setStyleSheet("color: #829197;")
        title_box.addWidget(self.item_title)
        title_box.addWidget(self.item_description)
        title_row.addLayout(title_box, 1)
        layout.addLayout(title_row)

        project_row = QHBoxLayout()
        project_row.addWidget(QLabel("枪皮名称"))
        self.project_name = QLineEdit("未命名枪皮")
        project_row.addWidget(self.project_name, 1)
        project_row.addWidget(QLabel("输出尺寸"))
        self.resolution_combo = QComboBox()
        self.resolution_combo.addItem("保持用户贴图尺寸", "user")
        self.resolution_combo.addItem("跟随原材质", "source")
        self.resolution_combo.addItem("1K", "1k")
        self.resolution_combo.addItem("2K", "2k")
        self.resolution_combo.addItem("4K", "4k")
        project_row.addWidget(self.resolution_combo)
        layout.addLayout(project_row)

        folder_row = QHBoxLayout()
        self.folder_label = QLabel("尚未选择贴图目录")
        self.folder_label.setStyleSheet("color: #91a1a6;")
        self.folder_label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        self.folder_label.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Preferred,
        )
        self.analyze_button = QPushButton("选择整枪贴图目录")
        self.analyze_button.setObjectName("Primary")
        self.analyze_button.clicked.connect(self._choose_texture_folder)
        self.clear_slot_button = QPushButton("清除当前通道")
        self.clear_slot_button.setEnabled(False)
        self.clear_slot_button.clicked.connect(self._clear_current_channel)
        folder_row.addWidget(self.folder_label, 1)
        folder_row.addWidget(self.clear_slot_button)
        folder_row.addWidget(self.analyze_button)
        layout.addLayout(folder_row)

        self.selected_channel_label = QLabel("当前通道：未选择")
        self.selected_channel_label.setStyleSheet("color: #74868b;")
        layout.addWidget(self.selected_channel_label)

        self.table = CompactTableWidget(0, 8)
        self.table.setHorizontalHeaderLabels(
            ["部件", "位置", "Asset", "基础色", "法线", "金属/光滑", "自发光", "识别状态"]
        )
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setWordWrap(False)
        self.table.setTextElideMode(Qt.TextElideMode.ElideMiddle)
        self.table.setItemDelegate(ChannelHighlightDelegate(self.table))
        self.table.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Expanding,
        )
        self.table.doubleClicked.connect(self._table_double_clicked)
        self.table.itemSelectionChanged.connect(self._table_selection_changed)
        self.table.itemChanged.connect(self._part_enabled_changed)
        self.table.currentCellChanged.connect(
            lambda current_row, current_column, previous_row, previous_column:
            self._table_selection_changed()
        )
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        for index in range(3, 7):
            header.setSectionResizeMode(index, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(7, QHeaderView.ResizeMode.ResizeToContents)
        layout.addWidget(self.table, 2)

        self.emissive_group = QFrame()
        self.emissive_group.setObjectName("EmissivePanel")
        self.emissive_group.setStyleSheet(
            "QFrame#EmissivePanel {"
            "background: #111417; border: 1px solid #303a3f;"
            "border-radius: 5px;"
            "}"
        )
        self.emissive_group.setSizePolicy(
            QSizePolicy.Policy.Maximum,
            QSizePolicy.Policy.Fixed,
        )
        self.emissive_group.setMaximumWidth(455)
        emissive_layout = QGridLayout(self.emissive_group)
        emissive_layout.setContentsMargins(8, 5, 8, 5)
        emissive_layout.setSpacing(5)
        self.emissive_layout = emissive_layout
        emissive_title = QLabel("自发光颜色 / 强度")
        emissive_title.setStyleSheet(
            "color: #b7c4c8; font-weight: 600; border: none;"
        )
        emissive_layout.addWidget(emissive_title, 0, 0)
        self.emissive_channel_spins: dict[int, QDoubleSpinBox] = {}
        self.emissive_channel_widgets: dict[int, QWidget] = {}
        for index, channel in enumerate(("R", "G", "B")):
            channel_widget = QWidget()
            channel_layout = QHBoxLayout(channel_widget)
            channel_layout.setContentsMargins(0, 0, 0, 0)
            channel_layout.setSpacing(3)
            label = QLabel(channel)
            spin = QDoubleSpinBox()
            spin.setRange(0.0, 100.0)
            spin.setDecimals(2)
            spin.setSingleStep(0.1)
            spin.setFixedWidth(76)
            spin.valueChanged.connect(
                lambda value, channel_index=index: self._emissive_channel_changed(
                    channel_index, value
                )
            )
            channel_layout.addWidget(label)
            channel_layout.addWidget(spin, 1)
            emissive_layout.addWidget(channel_widget, 0, index + 1)
            emissive_layout.setColumnStretch(index + 1, 1)
            self.emissive_channel_spins[index] = spin
            self.emissive_channel_widgets[index] = channel_widget
        self.emissive_group.setVisible(False)
        emissive_row = QHBoxLayout()
        emissive_row.addStretch(1)
        emissive_row.addWidget(self.emissive_group)
        layout.addLayout(emissive_row)

        review_row = QHBoxLayout()
        self.partial_check = QCheckBox("允许缺失部件/通道保持原样")
        self.low_confidence_check = QCheckBox("我已检查低置信度自动匹配")
        self.auto_backup_check = QCheckBox("安装前自动备份")
        self.auto_backup_check.setChecked(self.settings.auto_backup)
        self.auto_backup_check.setToolTip(
            "勾选后会保留可还原的安装备份；取消后仅在安装过程中临时保留，成功后自动删除"
        )
        self.auto_backup_check.toggled.connect(self._toggle_auto_backup)
        self.partial_check.toggled.connect(self._save_autosave)
        self.low_confidence_check.toggled.connect(self._save_autosave)
        review_row.addWidget(self.partial_check)
        review_row.addWidget(self.low_confidence_check)
        review_row.addWidget(self.auto_backup_check)
        review_row.addStretch(1)
        layout.addLayout(review_row)

        log_label = QLabel("识别与构建日志")
        log_label.setStyleSheet("color: #9caaa6; font-weight: 600;")
        layout.addWidget(log_label)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(2000)
        self.log.setPlaceholderText("操作日志将在这里显示")
        self.log.setLineWrapMode(QPlainTextEdit.LineWrapMode.WidgetWidth)
        self.log.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Preferred,
        )
        self.log.setMaximumHeight(150)
        layout.addWidget(self.log)
        self.main_splitter.addWidget(panel)

    def _build_preview_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(230)
        panel.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Expanding)
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(6, 12, 12, 12)
        heading = QLabel("部件预览")
        heading.setStyleSheet("font-size: 15px; font-weight: 700; color: #cbd5d1;")
        layout.addWidget(heading)
        self.preview_part_label = QLabel("尚未选择部件")
        self.preview_part_label.setStyleSheet("color: #d8ff67; font-weight: 600;")
        layout.addWidget(self.preview_part_label)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        content = QWidget()
        grid = QGridLayout(content)
        grid.setContentsMargins(0, 0, 0, 0)
        self.preview_tiles: dict[str, PreviewTile] = {}
        for index, channel in enumerate(CHANNELS):
            tile = PreviewTile(f"{CHANNEL_LABELS_ZH[channel]} / {channel}")
            tile.activated.connect(self._open_preview_image)
            self.preview_tiles[channel] = tile
            grid.addWidget(tile, index // 2, index % 2)
        scroll.setWidget(content)
        self.preview_tabs = QTabWidget()
        self.preview_tabs.addTab(scroll, "2D 通道")
        self.three_d_page = QWidget()
        self.three_d_page_layout = QVBoxLayout(self.three_d_page)
        self.three_d_page_layout.setContentsMargins(0, 0, 0, 0)
        self.preview_tabs.addTab(self.three_d_page, "3D 视图")
        self.preview_tabs.currentChanged.connect(self._preview_tab_changed)
        layout.addWidget(self.preview_tabs, 1)

        warning_group = QGroupBox("检查项")
        warning_layout = QVBoxLayout(warning_group)
        self.warning_list = QListWidget()
        self.warning_list.setMaximumHeight(140)
        self.warning_list.setWordWrap(True)
        self.warning_list.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self.warning_list.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Expanding,
        )
        warning_layout.addWidget(self.warning_list)
        layout.addWidget(warning_group)
        self.main_splitter.addWidget(panel)

    def _populate_tree(self) -> None:
        self._loading_tree = True
        self.catalog_tree.clear()
        for section, items in weapon_sections(self.catalog.values()).items():
            section_item = self._catalog_container(section)
            self.catalog_tree.addTopLevelItem(section_item)
            for catalog_item in items:
                section_item.addChild(self._catalog_leaf(catalog_item))

        attachment_section = self._catalog_container("枪械配件")
        self.catalog_tree.addTopLevelItem(attachment_section)
        attachment_items = [
            item for item in self.catalog.values() if item.kind == "attachment"
        ]
        categories = list(ATTACHMENT_CATEGORY_ORDER)
        categories.extend(
            sorted(
                {
                    item.category
                    for item in attachment_items
                    if item.category not in ATTACHMENT_CATEGORY_ORDER
                },
                key=str.casefold,
            )
        )
        for category in categories:
            items = sorted(
                (item for item in attachment_items if item.category == category),
                key=lambda item: item.name.casefold(),
            )
            if not items:
                continue
            category_item = self._catalog_container(category)
            attachment_section.addChild(category_item)
            for catalog_item in items:
                category_item.addChild(self._catalog_leaf(catalog_item))
            category_item.setExpanded(False)
        for index in range(self.catalog_tree.topLevelItemCount()):
            self.catalog_tree.topLevelItem(index).setExpanded(True)
        self._loading_tree = False

    @staticmethod
    def _catalog_container(name: str) -> QTreeWidgetItem:
        container = QTreeWidgetItem([name])
        container.setFlags(container.flags() & ~Qt.ItemFlag.ItemIsSelectable)
        container.setExpanded(True)
        return container

    @staticmethod
    def _catalog_leaf(catalog_item: CatalogItem) -> QTreeWidgetItem:
        label = item_display_name(catalog_item)
        child = QTreeWidgetItem([label])
        child.setData(0, Qt.ItemDataRole.UserRole, catalog_item.id)
        tooltip = (
            f"{label}\n"
            f"{len(catalog_item.parts)} 个部件\n"
            + (" / ".join(catalog_item.aliases[:4]))
        )
        child.setToolTip(0, tooltip)
        return child

    def _restore_last_item(self) -> None:
        target = self.settings.last_weapon_id
        tree_item = self._find_tree_item(target) if target else None
        if tree_item is None:
            tree_item = next(self._iter_catalog_nodes(), None)
        if tree_item:
            self.catalog_tree.setCurrentItem(tree_item)

    def _find_tree_item(self, item_id: str) -> QTreeWidgetItem | None:
        for child in self._iter_catalog_nodes():
            if child.data(0, Qt.ItemDataRole.UserRole) == item_id:
                return child
        return None

    def _iter_catalog_nodes(self):
        stack = [
            self.catalog_tree.topLevelItem(index)
            for index in range(self.catalog_tree.topLevelItemCount() - 1, -1, -1)
        ]
        while stack:
            node = stack.pop()
            if node.data(0, Qt.ItemDataRole.UserRole):
                yield node
            stack.extend(
                node.child(index)
                for index in range(node.childCount() - 1, -1, -1)
            )

    def _filter_tree(self, text: str) -> None:
        query = text.strip().casefold()
        for top in range(self.catalog_tree.topLevelItemCount()):
            self._filter_tree_node(
                self.catalog_tree.topLevelItem(top),
                query,
            )

    def _filter_tree_node(self, node: QTreeWidgetItem, query: str) -> int:
        item_id = node.data(0, Qt.ItemDataRole.UserRole)
        if item_id:
            item = self.catalog.get(str(item_id))
            haystack = ""
            if item:
                haystack = " ".join(
                    [
                        item.name,
                        item.category,
                        item.description,
                        *(
                            [weapon_display_label(item)]
                            if item.kind == "weapon"
                            else []
                        ),
                        *item.aliases,
                    ]
                    + [part.material for part in item.parts]
                ).casefold()
            matches = int(not query or query in haystack)
            node.setHidden(not matches)
            return matches

        visible = sum(
            self._filter_tree_node(node.child(index), query)
            for index in range(node.childCount())
        )
        node.setHidden(visible == 0)
        if query and visible:
            node.setExpanded(True)
        return visible

    def _tree_selection_changed(self) -> None:
        if self._loading_tree:
            return
        selected = self.catalog_tree.currentItem()
        if not selected:
            return
        item_id = selected.data(0, Qt.ItemDataRole.UserRole)
        if not item_id:
            return
        item = self.catalog.get(str(item_id))
        if item is None:
            return
        self.current_item = item
        self._clear_undo_history()
        self.current_analysis = None
        self.current_project = None
        self._optional_enabled = {
            part.id
            for part in item.parts
            if part.optional
        }
        self._disabled_parts.clear()
        self.current_part_id = item.parts[0].id if item.parts else None
        self.settings.last_weapon_id = item.id
        save_settings(self.settings)
        display_name = item_display_name(item)
        official_chinese = (
            official_weapon_meta(item)[1]
            if item.kind == "weapon"
            else item.category
        )
        self.item_title.setText(display_name)
        self.item_description.setText(
            item.description
            or f"{official_chinese} · {len(item.parts)} 个可编辑部件 · "
            f"Asset {', '.join(str(value) for value in sorted({p.asset for p in item.parts}))}"
        )
        self.project_name.setText(f"{item_display_name(item)}-枪皮")
        self._set_folder_label(None)
        self._populate_empty_table()
        self._clear_previews()
        self._clear_warnings()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _populate_empty_table(self) -> None:
        self._loading_table = True
        self.table.setRowCount(0)
        if not self.current_item:
            self._loading_table = False
            return
        self.table.setRowCount(len(self.current_item.parts))
        for row, part in enumerate(self.current_item.parts):
            enabled = (
                part.id not in self._disabled_parts
                and (not part.optional or part.id in self._optional_enabled)
            )
            self._set_part_row(
                row,
                part.id,
                part.display_name,
                part.role,
                part.asset,
                optional=part.optional,
                enabled=enabled,
            )
        self._loading_table = False
        if self.table.rowCount():
            self.table.setCurrentCell(0, 0)
            first = self.table.item(0, 0)
            if first:
                self.current_part_id = str(first.data(Qt.ItemDataRole.UserRole) or "")
            self.selected_channel = None
            self.selected_channel_label.setText("当前通道：未选择")
            self.selected_channel_label.setStyleSheet("color: #74868b;")
            self.clear_slot_button.setEnabled(False)
            self.clear_slot_button.setText("清除当前通道")
            self._refresh_part_preview()
            self._load_emissive_controls()

    def _set_part_row(
        self,
        row: int,
        part_id: str,
        name: str,
        role: str,
        asset: int,
        *,
        optional: bool = False,
        enabled: bool = False,
    ) -> None:
        inactive = not enabled
        values = [
            name,
            role,
            str(asset),
            "未启用" if inactive else "待导入",
            "未启用" if inactive else "待导入",
            "未启用" if inactive else "待导入",
            "未启用" if inactive else "待导入",
            "可选未启用" if inactive else "未分析",
        ]
        for column, value in enumerate(values):
            item = QTableWidgetItem(value)
            item.setData(Qt.ItemDataRole.UserRole, part_id)
            if column < 3:
                item.setFlags(item.flags() & ~Qt.ItemFlag.ItemIsEditable)
            if column == 0:
                item.setFlags(item.flags() | Qt.ItemFlag.ItemIsUserCheckable)
                item.setCheckState(
                    Qt.CheckState.Checked if enabled else Qt.CheckState.Unchecked
                )
                item.setToolTip(
                    "取消勾选后，该部件从 3D 预览隐藏，且不参与导出和安装"
                )
            if inactive and column >= 3:
                item.setForeground(QColor("#69767a"))
                item.setToolTip("该部件为可选内容，勾选左侧复选框后启用")
            self.table.setItem(row, column, item)

    def _render_analysis_table(
        self,
        selected_part_id: str | None = None,
        selected_channel: str | None = None,
    ) -> None:
        if not self.current_item or not self.current_analysis:
            self._populate_empty_table()
            return
        analysis = self.current_analysis
        self._loading_table = True
        self.table.setRowCount(len(self.current_item.parts))
        for row, part in enumerate(self.current_item.parts):
            enabled = (
                part.id not in self._disabled_parts
                and (not part.optional or part.id in self._optional_enabled)
            )
            self._set_part_row(
                row,
                part.id,
                part.display_name,
                part.role,
                part.asset,
                optional=part.optional,
                enabled=enabled,
            )
            slots = analysis.slots.get(part.id, {})
            confidences: list[float] = []
            for column, channel in enumerate(CHANNELS, start=3):
                match = slots.get(channel)
                cell = self.table.item(row, column)
                cell.setData(Qt.ItemDataRole.UserRole, channel)
                if not enabled:
                    cell.setText("未启用")
                    cell.setForeground(QColor("#69767a"))
                    cell.setToolTip("勾选左侧部件复选框后启用")
                elif match is None:
                    cell.setText("不支持")
                    cell.setForeground(QColor("#69767a"))
                    cell.setToolTip("当前 shader 不支持该通道")
                elif match.path:
                    cell.setText(match.path.name)
                    cell.setToolTip(str(match.path))
                    confidences.append(match.confidence)
                    if match.needs_confirmation:
                        cell.setForeground(QColor("#ffc75a"))
                    else:
                        cell.setForeground(QColor("#a8d45b"))
                else:
                    cell.setText("缺失")
                    cell.setForeground(QColor("#ff7c75"))
            status = self.table.item(row, 7)
            if not enabled:
                status.setText("部件未启用")
                status.setForeground(QColor("#69767a"))
            elif not slots:
                status.setText("材质不可读")
                status.setForeground(QColor("#ff7c75"))
            elif any(slots.get(channel) and slots[channel].path for channel in CHANNELS):
                minimum = min(confidences) if confidences else 0.0
                if minimum >= 0.82:
                    status.setText(f"自动 {minimum:.0%}")
                    status.setForeground(QColor("#a8d45b"))
                else:
                    status.setText(f"待确认 {minimum:.0%}")
                    status.setForeground(QColor("#ffc75a"))
            else:
                status.setText("缺少贴图")
                status.setForeground(QColor("#ff7c75"))
        self._loading_table = False
        if self.table.rowCount():
            target_row = 0
            if selected_part_id:
                for row in range(self.table.rowCount()):
                    cell = self.table.item(row, 0)
                    if cell and str(cell.data(Qt.ItemDataRole.UserRole) or "") == selected_part_id:
                        target_row = row
                        break
            target_column = 0
            if selected_channel in CHANNELS:
                target_column = CHANNELS.index(selected_channel) + 3
            self.table.setCurrentCell(target_row, target_column)
            self._table_selection_changed()

    def _choose_texture_folder(self) -> None:
        if not self._ensure_game() or not self.current_item:
            return
        initial = self.settings.last_texture_folder or str(Path.home())
        folder = QFileDialog.getExistingDirectory(self, "选择整枪贴图目录", initial)
        if not folder:
            return
        self._clear_undo_history()
        self.settings.last_texture_folder = folder
        save_settings(self.settings)
        self._set_folder_label(folder)
        game = self.current_game
        item = self.current_item
        self._start_worker(
            "正在分析贴图通道和部件…",
            lambda report: analyze_texture_folder(
                game,
                item,
                Path(folder),
                enabled_parts=set(self._optional_enabled),
            ),
            self._analysis_finished,
        )

    def _analysis_finished(self, result: object) -> None:
        analysis = result
        if not isinstance(analysis, MatchAnalysis) or not self.current_item:
            return
        self.current_analysis = analysis
        self._clear_undo_history()
        self.current_project = SkinProject.from_analysis(
            name=self.project_name.text().strip()
            or f"{item_display_name(self.current_item)}-枪皮",
            game_root=self.current_game.root if self.current_game else "",
            item_id=self.current_item.id,
            folder=analysis.folder,
            analysis=analysis,
            resolution_mode=str(self.resolution_combo.currentData()),
        )
        self.current_project.enabled_parts = sorted(self._optional_enabled)
        self.current_project.disabled_parts = sorted(self._disabled_parts)
        for part in self.current_item.parts:
            self.current_project.emissive[part.id] = EmissiveSettings()
        self._render_analysis_table()
        self._render_warnings()
        self._log(
            f"分析完成：识别 {sum(1 for c in analysis.candidates if c.channel_guess)} 张通道图，"
            f"{len(analysis.unknown_files)} 张未归类。"
        )
        self._save_autosave()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _render_warnings(self) -> None:
        self.warning_list.clear()
        if not self.current_analysis or not self.current_item:
            return
        analysis = self.current_analysis
        for part_id in analysis.missing_parts:
            part = next((value for value in self.current_item.parts if value.id == part_id), None)
            if part:
                self.warning_list.addItem(f"缺失部件：{part.display_name}")
        for part_id, channels in analysis.missing_channels.items():
            part = next((value for value in self.current_item.parts if value.id == part_id), None)
            if part and channels:
                labels = "、".join(CHANNEL_LABELS_ZH.get(channel, channel) for channel in channels)
                self.warning_list.addItem(f"{part.display_name}：缺少 {labels}")
        low = sum(
            1
            for slots in analysis.slots.values()
            for match in slots.values()
            if match.needs_confirmation
        )
        if low:
            self.warning_list.addItem(f"低置信度自动匹配：{low} 项")
        for warning in analysis.warnings:
            self.warning_list.addItem(warning)
        if analysis.unknown_files:
            names = "、".join(path.name for path in analysis.unknown_files[:5])
            suffix = "…" if len(analysis.unknown_files) > 5 else ""
            self.warning_list.addItem(f"未使用文件：{names}{suffix}")
        for warning in analysis.warnings:
            self._log(warning)

    def _clear_warnings(self) -> None:
        self.warning_list.clear()

    def _table_selection_changed(self) -> None:
        row = self.table.currentRow()
        if row < 0 or row >= self.table.rowCount():
            return
        cell = self.table.item(row, 0)
        if not cell:
            return
        self.current_part_id = str(cell.data(Qt.ItemDataRole.UserRole) or "")
        column = self.table.currentColumn()
        if 3 <= column <= 6:
            self.selected_channel = CHANNELS[column - 3]
            part = next(
                (
                    candidate
                    for candidate in self.current_item.parts
                    if candidate.id == self.current_part_id
                ),
                None,
            ) if self.current_item else None
            part_name = part.display_name if part else self.current_part_id
            self.selected_channel_label.setText(
                f"当前通道：{part_name} · {CHANNEL_LABELS_ZH[self.selected_channel]}"
                f"（{self.selected_channel}）"
            )
            self.selected_channel_label.setStyleSheet("color: #b7dc55; font-weight: 600;")
            self.clear_slot_button.setEnabled(True)
            self.clear_slot_button.setText(
                f"清除：{CHANNEL_LABELS_ZH[self.selected_channel]}"
            )
            self.clear_slot_button.setStyleSheet(
                "QPushButton { background: #536b27; border: none; "
                "color: #ffffff; font-weight: 600; }"
            )
        else:
            self.selected_channel = None
            self.selected_channel_label.setText("当前通道：未选择")
            self.selected_channel_label.setStyleSheet("color: #74868b;")
            self.clear_slot_button.setEnabled(False)
            self.clear_slot_button.setText("清除当前通道")
            self.clear_slot_button.setStyleSheet("")
        self._refresh_part_preview()
        self._load_emissive_controls()

    def _part_enabled_changed(self, item: QTableWidgetItem) -> None:
        if self._loading_table or not self.current_item:
            return
        if item.column() != 0:
            return
        part_id = str(item.data(Qt.ItemDataRole.UserRole) or "")
        part = next(
            (candidate for candidate in self.current_item.parts if candidate.id == part_id),
            None,
        )
        if part is None:
            return
        self._push_undo_snapshot()
        enabled = item.checkState() == Qt.CheckState.Checked
        if part.optional:
            if enabled:
                self._optional_enabled.add(part.id)
            else:
                self._optional_enabled.discard(part.id)
        elif enabled:
            self._disabled_parts.discard(part.id)
        else:
            self._disabled_parts.add(part.id)
        if not enabled and part.optional:
            if self.current_analysis:
                self.current_analysis.slots.pop(part.id, None)
                refresh_missing(
                    self.current_analysis,
                    self.current_item,
                    self._optional_enabled,
                )

        if self.current_project is None:
            self.current_project = SkinProject(
                name=self.project_name.text().strip()
                or f"{item_display_name(self.current_item)}-枪皮",
                game_root=str(self.current_game.root) if self.current_game else "",
                item_id=self.current_item.id,
                enabled_parts=sorted(self._optional_enabled),
            )
        else:
            self.current_project.enabled_parts = sorted(self._optional_enabled)
        self.current_project.disabled_parts = sorted(self._disabled_parts)

        if (
            enabled
            and self.current_analysis
            and self.current_game
            and self.current_analysis.folder.is_dir()
        ):
            optional_item = CatalogItem(
                id=self.current_item.id,
                name=self.current_item.name,
                category=self.current_item.category,
                aliases=list(self.current_item.aliases),
                parts=[part],
                description=self.current_item.description,
                kind=self.current_item.kind,
            )
            folder = self.current_analysis.folder
            game = self.current_game
            self._start_worker(
                f"正在识别 {part.display_name} 的贴图…",
                lambda report: analyze_texture_folder(
                    game,
                    optional_item,
                    folder,
                    enabled_parts={part.id},
                ),
                lambda result, current_part=part.id: self._optional_analysis_finished(
                    current_part,
                    result,
                ),
            )
        elif self.current_analysis:
            self._render_analysis_table(part.id)
            self._render_warnings()
            self._sync_project_from_analysis()
        else:
            self._populate_empty_table()

        self._save_autosave()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _optional_analysis_finished(self, part_id: str, result: object) -> None:
        if (
            not isinstance(result, MatchAnalysis)
            or not self.current_analysis
            or not self.current_item
        ):
            return
        slots = result.slots.get(part_id, {})
        if slots:
            self.current_analysis.slots[part_id] = slots
        refresh_missing(
            self.current_analysis,
            self.current_item,
            self._optional_enabled,
        )
        self._render_analysis_table(part_id)
        self._render_warnings()
        self._sync_project_from_analysis()
        self._refresh_part_preview()
        self._save_autosave()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _refresh_part_preview(self) -> None:
        self._clear_previews()
        if not self.current_item or not self.current_part_id:
            return
        part = next((value for value in self.current_item.parts if value.id == self.current_part_id), None)
        if not part:
            return
        if part.id in self._disabled_parts or (
            part.optional and part.id not in self._optional_enabled
        ):
            self.preview_part_label.setText(f"{part.display_name} · 部件已取消勾选")
            return
        self.preview_part_label.setText(
            f"{part.display_name} · {part.material} · Asset {part.asset}"
        )
        slots = self.current_analysis.slots.get(part.id, {}) if self.current_analysis else {}
        for channel, tile in self.preview_tiles.items():
            match = slots.get(channel)
            tile.setEnabled(True)
            tile.set_file(match.path if match and match.path else None)

    def _clear_previews(self) -> None:
        self.preview_part_label.setText("尚未选择部件")
        for tile in self.preview_tiles.values():
            tile.setEnabled(True)
            tile.set_file(None)

    def _open_preview_image(self, path: Path) -> None:
        if path.is_file():
            ImageViewerDialog(path, self).exec()

    def _preview_tab_changed(self, index: int) -> None:
        if index == 1:
            self._ensure_3d_view()
            self._refresh_3d_preview()

    def _ensure_3d_view(self) -> None:
        if self.three_d_view is not None or self.three_d_page_layout.count():
            return
        try:
            from .viewer3d import Weapon3DView

            self.three_d_view = Weapon3DView(self.three_d_page)
            self.three_d_view.apply_preview_settings(
                self.settings.preview_ambient_intensity,
                self.settings.preview_ambient_color,
                self.settings.preview_emissive_enabled,
                self.settings.preview_auto_rotate,
            )
            self.three_d_view.ambient_intensity_setting_changed.connect(
                self._preview_ambient_intensity_changed
            )
            self.three_d_view.ambient_color_setting_changed.connect(
                self._preview_ambient_color_changed
            )
            self.three_d_view.emissive_visibility_setting_changed.connect(
                self._preview_emissive_visibility_changed
            )
            self.three_d_view.auto_rotate_setting_changed.connect(
                self._preview_auto_rotate_changed
            )
            self.three_d_page_layout.addWidget(self.three_d_view)
        except Exception as exc:
            error = QLabel(f"3D 预览不可用：{type(exc).__name__}: {exc}")
            error.setWordWrap(True)
            error.setStyleSheet("color: #ff8b83;")
            self.three_d_page_layout.addWidget(error)

    def _preview_ambient_intensity_changed(self, value: float) -> None:
        self.settings.preview_ambient_intensity = float(value)
        save_settings(self.settings)

    def _preview_ambient_color_changed(self, color: str) -> None:
        self.settings.preview_ambient_color = str(color)
        save_settings(self.settings)

    def _preview_emissive_visibility_changed(self, visible: bool) -> None:
        self.settings.preview_emissive_enabled = bool(visible)
        save_settings(self.settings)

    def _preview_auto_rotate_changed(self, enabled: bool) -> None:
        self.settings.preview_auto_rotate = bool(enabled)
        save_settings(self.settings)

    def _mark_3d_dirty(self) -> None:
        self.three_d_dirty = True
        if self.three_d_view is not None:
            self.three_d_view._loaded_key = None

    def _refresh_3d_preview(self) -> None:
        self._ensure_3d_view()
        if self.three_d_view is None or not self.current_game or not self.current_item:
            return
        project = self.current_project or SkinProject(
            name=item_display_name(self.current_item),
            item_id=self.current_item.id,
            enabled_parts=sorted(self._optional_enabled),
        )
        QApplication.setOverrideCursor(Qt.CursorShape.WaitCursor)
        try:
            self.three_d_view.load_weapon(
                self.current_game,
                self.current_item,
                project,
            )
            self.three_d_dirty = False
        finally:
            QApplication.restoreOverrideCursor()

    def _table_double_clicked(self, index) -> None:
        if index.column() < 3 or index.column() > 6 or not self.current_item:
            return
        channel = CHANNELS[index.column() - 3]
        part_id = self.current_part_id
        if not part_id:
            return
        part = next(
            (value for value in self.current_item.parts if value.id == part_id),
            None,
        )
        if part and part.optional and part.id not in self._optional_enabled:
            self._log(f"{part.display_name} 尚未启用，请先勾选该部件。")
            return
        initial = self.current_analysis.folder if self.current_analysis else Path.home()
        path, _ = QFileDialog.getOpenFileName(
            self,
            f"为 {CHANNEL_LABELS_ZH[channel]} 选择贴图",
            str(initial),
            "贴图 (*.png *.tga *.tif *.tiff *.webp *.jpg *.jpeg);;所有文件 (*.*)",
        )
        if path:
            self._assign_path(part_id, channel, Path(path))

    def _assign_path(self, part_id: str, channel: str, path: Path | None) -> None:
        if not self.current_analysis or not self.current_item:
            return
        self._push_undo_snapshot()
        manual_assign(self.current_analysis, self.current_item, part_id, channel, path)
        self._render_analysis_table(part_id, channel)
        self._render_warnings()
        self._sync_project_from_analysis()
        self._refresh_part_preview()
        self._save_autosave()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _clear_current_channel(self) -> None:
        if not self.selected_channel or not self.current_part_id:
            self._log("请先在映射表中选中一个贴图通道单元格。")
            return
        self._assign_path(self.current_part_id, self.selected_channel, None)

    def _sync_project_from_analysis(self) -> None:
        if not self.current_project or not self.current_analysis:
            return
        assignments: dict[str, dict[str, str]] = {}
        parts_by_id = {part.id: part for part in self.current_item.parts}
        for part_id, slots in self.current_analysis.slots.items():
            part = parts_by_id.get(part_id)
            if part and part.optional and part.id not in self._optional_enabled:
                continue
            if part and part.id in self._disabled_parts:
                continue
            files = {
                channel: str(match.path)
                for channel, match in slots.items()
                if match.path is not None
            }
            if files:
                assignments[part_id] = files
        self.current_project.assignments = assignments
        self.current_project.disabled_parts = sorted(self._disabled_parts)
        self.current_project.enabled_parts = sorted(self._optional_enabled)
        self.current_project.name = self.project_name.text().strip() or self.current_project.name
        self.current_project.resolution_mode = str(self.resolution_combo.currentData())
        self.current_project.confirmations = {
            "allow_partial": self.partial_check.isChecked(),
            "review_low_confidence": self.low_confidence_check.isChecked(),
        }

    def _load_emissive_controls(self) -> None:
        if not self.current_project or not self.current_part_id:
            return
        part = next(
            (
                candidate
                for candidate in self.current_item.parts
                if candidate.id == self.current_part_id
            ),
            None,
        ) if self.current_item else None
        if part and part.optional and part.id not in self._optional_enabled:
            self.emissive_group.setVisible(False)
            return
        if part and part.id in self._disabled_parts:
            self.emissive_group.setVisible(False)
            return
        match = (
            self.current_analysis.slots.get(self.current_part_id, {}).get("Emissive")
            if self.current_analysis
            else None
        )
        has_emissive = bool(match and match.path)
        self.emissive_group.setVisible(has_emissive)
        settings = self.current_project.emissive.setdefault(
            self.current_part_id, EmissiveSettings()
        )
        if not has_emissive:
            settings.enabled = False
            return
        settings.enabled = True
        for index, spin in self.emissive_channel_spins.items():
            spin.blockSignals(True)
            spin.setValue(float(settings.color[index]) * settings.intensity)
            spin.blockSignals(False)

    def _emissive_channel_changed(self, channel_index: int, value: float) -> None:
        if not self.current_project or not self.current_part_id:
            return
        self._push_undo_snapshot()
        settings = self.current_project.emissive.setdefault(
            self.current_part_id, EmissiveSettings()
        )
        channels = list(settings.color[:3])
        channels[channel_index] = float(value)
        settings.color = (channels[0], channels[1], channels[2], 1.0)
        settings.intensity = 1.0
        settings.enabled = True
        self._save_autosave()
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()

    def _validate_before_build(self) -> bool:
        if not self._ensure_game() or not self.current_item:
            return False
        self._sync_project_from_analysis()
        if not self.current_project or not self.current_project.assignments:
            QMessageBox.warning(self, "没有贴图", "请先选择贴图目录并完成分析。")
            return False
        if is_gtfo_running():
            QMessageBox.warning(self, "GTFO 正在运行", "请先退出游戏，再生成或安装资产。")
            return False
        if self.current_analysis:
            low = sum(
                1
                for slots in self.current_analysis.slots.values()
                for match in slots.values()
                if match.needs_confirmation
            )
            if low and not self.low_confidence_check.isChecked():
                QMessageBox.warning(
                    self,
                    "仍有低置信度匹配",
                    "请先检查橙色标记项；确认无误后勾选“我已检查低置信度自动匹配”。",
                )
                return False
            missing = self.current_analysis.missing_parts or self.current_analysis.missing_channels
            if missing and not self.partial_check.isChecked():
                QMessageBox.warning(
                    self,
                    "存在缺失内容",
                    "请勾选“允许缺失部件/通道保持原样”，或补齐全部文件。",
                )
                return False
        return True

    def _selected_export_part_ids(self) -> set[str]:
        if not self.current_item:
            return set()
        return {
            part.id
            for part in self.current_item.parts
            if part.id not in self._disabled_parts
            and (not part.optional or part.id in self._optional_enabled)
        }

    def _export_obj(self) -> None:
        if not self.current_item or self.current_game is None:
            QMessageBox.warning(self, "无法导出 OBJ", "请先选择武器并确认 GTFO 目录。")
            return
        selected = self._selected_export_part_ids()
        if not selected:
            QMessageBox.warning(self, "没有勾选部件", "请先勾选需要导出的部件。")
            return
        output_dir = QFileDialog.getExistingDirectory(
            self,
            "选择 OBJ 导出目录",
            self.settings.last_texture_folder or str(Path.home()),
        )
        if not output_dir:
            return

        choice = QMessageBox(self)
        choice.setWindowTitle("选择 OBJ 导出方式")
        choice.setText("请选择部件文件的组织方式：")
        separate_button = choice.addButton(
            "每个部件单独导出",
            QMessageBox.ButtonRole.ActionRole,
        )
        combined_button = choice.addButton(
            "合并为一个 OBJ",
            QMessageBox.ButtonRole.AcceptRole,
        )
        choice.addButton(QMessageBox.StandardButton.Cancel)
        choice.exec()
        clicked = choice.clickedButton()
        if clicked is separate_button:
            mode = ObjExportMode.SEPARATE
        elif clicked is combined_button:
            mode = ObjExportMode.COMBINED
        else:
            return

        project = self.current_project or SkinProject(
            name=self.project_name.text().strip()
            or item_display_name(self.current_item),
            item_id=self.current_item.id,
            enabled_parts=sorted(self._optional_enabled),
            disabled_parts=sorted(self._disabled_parts),
        )
        request = ObjExportRequest(
            game=self.current_game,
            item=self.current_item,
            project=project,
            output_dir=Path(output_dir),
            mode=mode,
            selected_part_ids=selected,
        )
        QApplication.setOverrideCursor(Qt.CursorShape.WaitCursor)
        try:
            result = export_obj(request)
        except ObjExportError as exc:
            QMessageBox.critical(self, "OBJ 导出失败", str(exc))
            self._log(f"OBJ 导出失败：{exc}")
            return
        except Exception as exc:
            QMessageBox.critical(
                self,
                "OBJ 导出失败",
                f"{type(exc).__name__}: {exc}",
            )
            self._log(f"OBJ 导出失败：{type(exc).__name__}: {exc}")
            return
        finally:
            QApplication.restoreOverrideCursor()
        self._set_folder_label(result.mtl_path.parent)
        self._log(
            f"OBJ 导出完成：{result.object_count} 个对象，"
            f"{len(result.output_files)} 个 OBJ 文件，目录：{result.mtl_path.parent}"
        )
        QMessageBox.information(
            self,
            "OBJ 导出完成",
            f"已导出 {result.object_count} 个独立部件/纹理集。\n"
            f"OBJ 文件：{len(result.output_files)} 个\n\n"
            f"输出目录：{result.mtl_path.parent}",
        )

    def _export(self) -> None:
        if not self._validate_before_build():
            return
        directory = QFileDialog.getExistingDirectory(
            self,
            "选择导出目录",
            str(Path.home()),
        )
        if not directory:
            return
        game = self.current_game
        item = self.current_item
        project = self.current_project
        self._sync_project_from_analysis()
        target = Path(directory) / safe_output_name(project.name)
        if any(target.glob("sharedassets*.assets")):
            answer = QMessageBox.question(
                self,
                "目标目录已有资产",
                "输出子目录中已有 sharedassets 文件，是否覆盖？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No,
            )
            if answer != QMessageBox.StandardButton.Yes:
                return

        temp_root = Path(tempfile.mkdtemp(prefix="gtfo-export-", dir=ensure_app_dirs()["temp"]))
        work = temp_root / "build"

        def task(report):
            try:
                report("正在临时目录生成 sharedassets…")
                affected = affected_by_asset(
                    self.catalog.values(),
                    [part.asset for part in item.parts if part.id in project.assignments],
                )
                result = build_assets(
                    BuildContext(
                        game=game,
                        item=item,
                        project=project,
                        output_dir=work,
                        affected_items=affected,
                        persistent_backup=self.settings.persistent_backup,
                        clean_asset_dir=Path(self.settings.clean_asset_dir)
                        if self.settings.clean_asset_dir
                        else None,
                    )
                )
                report("正在复制到导出目录…")
                target.mkdir(parents=True, exist_ok=True)
                for built in result.files:
                    final = target / built.output_path.name
                    shutil.copy2(built.output_path, final)
                    built.output_path = final
                final_manifest = target / result.manifest_path.name
                shutil.copy2(result.manifest_path, final_manifest)
                result.output_dir = target
                result.manifest_path = final_manifest
                return result
            finally:
                shutil.rmtree(temp_root, ignore_errors=True)

        self._start_worker("正在构建资产…", task, self._export_finished)

    def _export_finished(self, result: object) -> None:
        if not hasattr(result, "output_dir"):
            return
        self._log(f"已导出到：{result.output_dir}")
        for warning in result.warnings:
            self._log(f"警告：{warning}")
        message = f"已生成 {len(result.files)} 个 .assets 文件。\n\n{result.output_dir}"
        if result.affected_items:
            message += "\n\n可能共用这些资产的武器：\n" + "、".join(result.affected_items)
        QMessageBox.information(
            self,
            "导出完成",
            message,
        )

    def _install(self) -> None:
        keep_backup = self.auto_backup_check.isChecked()
        if keep_backup and not self.ensure_backup_directory_interactive():
            return
        if not self._validate_before_build():
            return
        answer = QMessageBox.question(
            self,
            "安装到 GTFO",
            "将先备份现有目标 .assets，再覆盖 GTFO_Data 中的同名文件。\n"
            "原始 .resS 不会修改。是否继续？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        game = self.current_game
        item = self.current_item
        project = self.current_project
        persistent = self.settings.persistent_backup
        if self.install_manager is not None:
            manager = self.install_manager
        else:
            transient_root = ensure_app_dirs()["temp"] / "transient-backups"
            manager = InstallManager(transient_root, allow_system_drive=True)
        self._sync_project_from_analysis()
        temp_root = Path(tempfile.mkdtemp(prefix="gtfo-skin-build-", dir=ensure_app_dirs()["temp"]))
        work = temp_root / "build"

        def task(report):
            try:
                report("正在生成资产…")
                affected = affected_by_asset(
                    self.catalog.values(),
                    [part.asset for part in item.parts if part.id in project.assignments],
                )
                result = build_assets(
                    BuildContext(
                        game=game,
                        item=item,
                        project=project,
                        output_dir=work,
                        affected_items=affected,
                        persistent_backup=persistent,
                        clean_asset_dir=Path(self.settings.clean_asset_dir)
                        if self.settings.clean_asset_dir
                        else None,
                    )
                )
                report("正在写入 GTFO_Data…")
                record = manager.install(
                    game,
                    result,
                    persistent_backup=persistent,
                    keep_backup=keep_backup,
                )
                return result, record
            finally:
                shutil.rmtree(temp_root, ignore_errors=True)

        self._start_worker("正在构建并安装…", task, self._install_finished)

    def _install_finished(self, value: object) -> None:
        result, record = value
        if record.retained:
            self._log(f"安装完成，备份目录：{Path(record.backup_dir).name}")
        else:
            self._log("安装完成；自动备份已关闭，本次临时回滚数据已删除。")
        for warning in result.warnings:
            self._log(f"警告：{warning}")
        message = f"已安装 {len(result.files)} 个 .assets 文件。"
        if record.retained:
            message += f"\n备份目录：{Path(record.backup_dir).name}"
        else:
            message += "\n自动备份已关闭，本次安装未保留历史备份。"
        if result.affected_items:
            message += "\n\n可能共用这些资产的其他武器：\n" + "、".join(result.affected_items)
        QMessageBox.information(self, "安装完成", message)
        self._update_actions()

    def _rollback_last(self) -> None:
        if not self.ensure_backup_directory_interactive():
            return
        if self.install_manager is None:
            return
        records = self.install_manager.list_records()
        if not records:
            QMessageBox.information(self, "没有备份", "未找到可还原的安装记录。")
            return
        record = records[0]
        backup_name = Path(record.backup_dir).name
        answer = QMessageBox.question(
            self,
            "还原上次安装",
            f"将还原备份目录 {backup_name} 中的 {len(record.files)} 个文件。是否继续？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        try:
            self.install_manager.restore(record)
        except Exception as exc:
            QMessageBox.critical(self, "还原失败", f"{type(exc).__name__}: {exc}")
            return
        self._log(f"已还原备份目录 {backup_name}")
        QMessageBox.information(self, "还原完成", "目标 .assets 已还原到安装前状态。")

    def _browse_game(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "选择 GTFO 安装目录")
        if path:
            self._detect_game(path)

    def _detect_game(self, preferred: str) -> None:
        install = best_game_install(preferred)
        self.current_game = install
        if self.dly_workspace is not None:
            self.dly_workspace.set_game(install)
            self.dly_workspace._refresh_profile_status()
        if self.dynamic_workspace is not None:
            self.dynamic_workspace.set_game(install)
        if install:
            self.settings.game_root = str(install.root)
            save_settings(self.settings)
            self.game_status.setText(f"GTFO 已连接 · {install.data_dir}")
            self.game_status.setStyleSheet("color: #b7dc55;")
            self._log(f"检测到 GTFO：{install.root}")
        else:
            self.game_status.setText("未检测到 GTFO，请手动选择安装目录")
            self.game_status.setStyleSheet("color: #ff8b83;")
            self._log("未检测到 GTFO，可点击右上角“选择目录”。")
        self._update_actions()

    def _ensure_game(self) -> bool:
        if self.current_game and self.current_game.valid:
            return True
        QMessageBox.warning(self, "未连接 GTFO", "请先选择包含 GTFO.exe 和 GTFO_Data 的目录。")
        return False

    def _choose_clean_baseline(self) -> None:
        folder = QFileDialog.getExistingDirectory(
            self,
            "选择包含干净 sharedassets*.assets 的目录",
        )
        if not folder:
            return
        source = Path(folder)
        if not list(source.glob("sharedassets*.assets")):
            QMessageBox.warning(self, "目录无效", "该目录中没有 sharedassets*.assets。")
            return
        self.settings.clean_asset_dir = str(source)
        save_settings(self.settings)
        self._log(f"已记录干净基准目录：{source}；下次构建优先使用。")
        QMessageBox.information(
            self,
            "干净基准已登记",
            "已记录该目录。程序会优先用这里的同名资产作为构建基准。",
        )

    def ensure_backup_directory_interactive(self, force: bool = False) -> bool:
        current = self._configured_backup_dir()
        if current is not None and not force:
            return True
        initial = current
        if initial is None:
            if self.current_game and self.current_game.root.drive:
                initial = Path(self.current_game.root.drive + "\\")
            else:
                initial = Path.home()
        selected = QFileDialog.getExistingDirectory(
            self,
            "选择枪皮资产备份目录",
            str(initial),
        )
        if not selected:
            self._log("尚未设置备份目录，安装与还原功能暂不可用。")
            return False
        candidate = Path(selected).expanduser().resolve()
        try:
            test_directory_writable(candidate)
        except OSError as exc:
            QMessageBox.critical(self, "备份目录不可写", f"{candidate}\n\n{exc}")
            return False

        legacy_root = ensure_app_dirs()["backups"].resolve()
        migrated = 0
        if legacy_root != candidate and legacy_root.is_dir():
            try:
                migrated = migrate_backup_records(legacy_root, candidate)
            except Exception as exc:
                QMessageBox.critical(
                    self,
                    "旧备份迁移失败",
                    f"未更改备份目录。\n\n{type(exc).__name__}: {exc}",
                )
                return False
        self.settings.backup_dir = str(candidate)
        save_settings(self.settings)
        self._refresh_install_manager()
        self._update_actions()
        self._log(f"备份目录已设置为：{candidate}")
        if migrated:
            self._log(f"已从 C 盘迁移 {migrated} 条旧备份记录。")
            QMessageBox.information(
                self,
                "备份目录已设置",
                f"备份将保存到：\n{candidate}\n\n"
                f"已从 C 盘迁移 {migrated} 条旧安装记录。",
            )
        return True

    def _configured_backup_dir(self) -> Path | None:
        raw = self.settings.backup_dir.strip()
        if not raw:
            return None
        return Path(raw).expanduser().resolve()

    def _refresh_install_manager(self) -> None:
        path = self._configured_backup_dir()
        self.install_manager = None
        if path is None:
            self._update_backup_path_label()
            return
        try:
            self.install_manager = InstallManager(path)
        except (OSError, ValueError) as exc:
            self._log(f"备份目录不可用：{exc}")
        self._update_backup_path_label()

    def _toggle_persistent_backup(self, enabled: bool) -> None:
        self.settings.persistent_backup = enabled
        save_settings(self.settings)
        self._log("持久化源资产备份已开启。" if enabled else "持久化源资产备份已关闭。")

    def _toggle_auto_backup(self, enabled: bool) -> None:
        if enabled and self._configured_backup_dir() is None:
            if not self.ensure_backup_directory_interactive(force=True):
                self.auto_backup_check.blockSignals(True)
                self.auto_backup_check.setChecked(False)
                self.auto_backup_check.blockSignals(False)
                self.settings.auto_backup = False
                save_settings(self.settings)
                self._update_actions()
                return
        self.settings.auto_backup = enabled
        save_settings(self.settings)
        self._log("安装前自动备份已开启。" if enabled else "安装前自动备份已关闭。")
        self._update_actions()

    def _update_backup_path_label(self) -> None:
        path = self._configured_backup_dir()
        if path is None:
            self.backup_path_label.setText("当前备份目录：未设置")
            self.backup_path_label.setToolTip("点击选择备份目录")
        else:
            display = self.backup_path_label.fontMetrics().elidedText(
                str(path),
                Qt.TextElideMode.ElideMiddle,
                360,
            )
            self.backup_path_label.setText(f"当前备份目录：{display}")
            self.backup_path_label.setToolTip(str(path))

    def _open_project(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "打开枪皮工程",
            str(ensure_app_dirs()["projects"]),
            "GTFO 枪皮工程 (*.gtfoskin);;JSON (*.json)",
        )
        if not path:
            return
        try:
            project = load_project(Path(path))
        except Exception as exc:
            QMessageBox.critical(self, "无法打开工程", f"{type(exc).__name__}: {exc}")
            return
        item = self.catalog.get(project.item_id)
        if item is None:
            QMessageBox.critical(self, "目录不兼容", f"工程引用了未知武器：{project.item_id}")
            return
        if project.game_root:
            candidate = validate_game_root(project.game_root)
            if candidate.valid:
                self.current_game = candidate
        tree_item = self._find_tree_item(item.id)
        if tree_item:
            self.catalog_tree.setCurrentItem(tree_item)
        self.current_item = item
        self.current_project = project
        self._clear_undo_history()
        self._optional_enabled = set(project.enabled_parts)
        self._optional_enabled.update(
            part.id
            for part in item.parts
            if part.id.startswith("ammo_")
        )
        self._disabled_parts = set(project.disabled_parts)
        self.project_name.setText(project.name)
        index = self.resolution_combo.findData(project.resolution_mode)
        if index >= 0:
            self.resolution_combo.setCurrentIndex(index)
        self._set_folder_label(project.source_folder or None)
        self.current_analysis = self._analysis_from_project(project, item)
        self._render_analysis_table()
        self._render_warnings()
        self.partial_check.setChecked(project.confirmations.get("allow_partial", False))
        self.low_confidence_check.setChecked(
            project.confirmations.get("review_low_confidence", False)
        )
        self._log(f"已打开工程：{path}")
        self._mark_3d_dirty()
        if self.preview_tabs.currentIndex() == 1:
            self._refresh_3d_preview()
        self._update_actions()

    def _analysis_from_project(
        self,
        project: SkinProject,
        item: CatalogItem,
    ) -> MatchAnalysis:
        analysis = MatchAnalysis(
            item_id=item.id,
            folder=Path(project.source_folder) if project.source_folder else Path(),
        )
        info_by_part = {}
        if self.current_game:
            for part in item.active_parts(self._optional_enabled):
                if part.id in self._disabled_parts:
                    continue
                try:
                    info_by_part[part.id] = inspect_material(self.current_game, part)
                except Exception as exc:
                    analysis.warnings.append(f"{part.display_name}: {exc}")
        for part in item.active_parts(self._optional_enabled):
            if part.id in self._disabled_parts:
                continue
            files = project.assignments.get(part.id, {})
            channels = list(part.channels)
            info = info_by_part.get(part.id)
            if info is not None:
                from .assets import available_channels

                channels = available_channels(info)
            for channel in channels:
                path = Path(files[channel]) if files.get(channel) else None
                analysis.slots.setdefault(part.id, {})[channel] = SlotMatch(
                    part_id=part.id,
                    channel=channel,
                    path=path,
                    part_confidence=1.0 if path else 0.0,
                    channel_confidence=1.0 if path else 0.0,
                    source="manual",
                )
        refresh_missing(analysis, item, self._optional_enabled)
        return analysis

    def _save_project(self, save_as: bool) -> None:
        if not self.current_project:
            QMessageBox.information(self, "没有工程", "请先选择武器并导入贴图。")
            return
        self._sync_project_from_analysis()
        if save_as or not self.current_project.name:
            path, _ = QFileDialog.getSaveFileName(
                self,
                "保存枪皮工程",
                str(ensure_app_dirs()["projects"] / f"{self.current_project.name}.gtfoskin"),
                "GTFO 枪皮工程 (*.gtfoskin)",
            )
        else:
            path = str(ensure_app_dirs()["projects"] / f"{self.current_project.name}.gtfoskin")
        if not path:
            return
        try:
            save_project(self.current_project, Path(path))
            self._log(f"工程已保存：{path}")
        except Exception as exc:
            QMessageBox.critical(self, "保存失败", f"{type(exc).__name__}: {exc}")

    def _save_autosave(self, *_) -> None:
        if not self.current_project:
            return
        self._sync_project_from_analysis()
        try:
            autosave_project(self.current_project)
        except OSError:
            pass

    def _start_worker(self, label: str, task, on_success) -> None:
        if self.worker is not None:
            try:
                running = self.worker.isRunning()
            except RuntimeError:
                running = False
                self.worker = None
            if running:
                QMessageBox.information(self, "请稍候", "当前操作尚未完成。")
                return
            self.worker = None
        self._set_busy(True, label)
        worker = TaskWorker(task, self)
        self.worker = worker
        worker.message.connect(self._log)
        worker.failed.connect(self._worker_failed)
        worker.succeeded.connect(on_success)
        worker.finished.connect(lambda current=worker: self._worker_finished(current))
        worker.finished.connect(worker.deleteLater)
        worker.start()

    def _worker_finished(self, worker: TaskWorker) -> None:
        if self.worker is worker:
            self.worker = None
        self._set_busy(False, "完成")

    def _worker_failed(self, message: str) -> None:
        self._log(f"错误：{message}")
        QMessageBox.critical(self, "操作失败", message)

    def _set_busy(self, busy: bool, label: str) -> None:
        self.progress.setRange(0, 0 if busy else 1)
        self.progress.setValue(0 if busy else 1)
        self.status_label.setText(label)
        for widget in (
            self.catalog_tree,
            self.analyze_button,
            self.export_obj_button,
            self.export_button,
            self.install_button,
            self.rollback_button,
        ):
            widget.setEnabled(not busy)
        if not busy:
            self._update_actions()

    def _update_actions(self) -> None:
        has_item = self.current_item is not None
        has_game = self.current_game is not None and self.current_game.valid
        has_project = self.current_project is not None and bool(self.current_project.assignments)
        has_backup_dir = self.install_manager is not None
        self.analyze_button.setEnabled(has_item and has_game)
        self.export_obj_button.setEnabled(has_item and has_game)
        self.export_button.setEnabled(has_project and has_game)
        self.install_button.setEnabled(
            has_project
            and has_game
            and (not self.auto_backup_check.isChecked() or has_backup_dir)
        )
        self.rollback_button.setEnabled(
            has_backup_dir and bool(self.install_manager.list_records())
        )
        self.table.setEnabled(has_item)

    def _log(self, message: str) -> None:
        stamp = datetime.now().strftime("%H:%M:%S")
        line = f"[{stamp}] {message}"
        self.log.appendPlainText(line)
        try:
            with self.log_path.open("a", encoding="utf-8") as stream:
                stream.write(line + "\n")
        except OSError:
            pass

    def _set_folder_label(self, value: str | Path | None) -> None:
        if not value:
            self.folder_label.setText("贴图目录：未选择")
            self.folder_label.setToolTip("")
            return
        text = str(value)
        name = Path(text).name or text
        self.folder_label.setText(f"贴图目录：{name}")
        self.folder_label.setToolTip(text)
